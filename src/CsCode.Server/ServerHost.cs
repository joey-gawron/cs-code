using System.Reflection;
using System.Runtime.InteropServices;
using CsCode.Core;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Dap;
using CsCode.Lsp;
using CsCode.Platform;
using CsCode.Platform.Configuration;
using CsCode.Platform.Io;
using CsCode.Platform.Workspace;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CsCode.Server;

/// <summary>
/// The workspace host. Composes the real workspace services (Core, Platform with its own data directory, Lsp, Dap's adapters and task runner, the
/// Terminal module's PTY) and serves the protocol to one client connection at a time (<see cref="ServeAsync"/>): over stdio for one connection
/// (the process exits with it), or — persistent mode, R4 — for every connection a reconnecting client makes to the daemon (<see cref="ServerDaemon"/>),
/// keeping language servers, terminals, debug adapters and task runs across them. Handlers are registered once on a <see cref="RpcPeerSlot"/>;
/// each new connection gets them all. Disposal stops the language servers (killing their process trees), terminals, adapters, every watch and
/// search, then the container.
/// </summary>
public sealed class ServerHost : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly RpcPeerSlot _link;
    private readonly ServiceProvider _services;
    private readonly ILogger _log;
    private readonly ILoggerFactory _loggers;
    private readonly List<IDisposable> _handlers = new();
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly (Stream Input, Stream Output)? _stdio;
    private volatile bool _clientGzip;
    private int _disposed, _connections;

    /// <summary>A host for <see cref="ServeAsync"/> connections (persistent mode).</summary>
    public ServerHost(ServerOptions options, Action<IServiceCollection>? configure = null)
    {
        _options = options;
        Directory.CreateDirectory(options.DataDir);
        _link = new RpcPeerSlot(RemoteJson.Create());
        _services = BuildServices(options.DataDir, _link, configure);
        _loggers = _services.GetRequiredService<ILoggerFactory>();
        _log = _loggers.CreateLogger("CsCode.Server");
        Channels = new ChannelHub(_link, _loggers.CreateLogger("CsCode.Server.Channels"));
        RegisterHandlers();
    }

    /// <summary>A host for one stdio connection (<see cref="RunAsync"/>).</summary>
    public ServerHost(ServerOptions options, Stream input, Stream output, Action<IServiceCollection>? configure = null) : this(options, configure) =>
        _stdio = (input, output);

    /// <summary>Random per process: a client that reconnects and gets the same id resumed this server (its terminals, adapters and tasks).</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public IRpcEndpoint Link => _link;
    public IServiceProvider Services => _services;
    internal ChannelHub Channels { get; }

    /// <summary>Completes when a client asked the server to shut down (a deliberate disconnect).</summary>
    public Task ShutdownRequested => _shutdown.Task;

    /// <summary>Client connections served so far.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>The server's composition: the same modules as the GUI's workspace layer, with this process's own paths.</summary>
    internal static ServiceProvider BuildServices(string dataDir, IRpcEndpoint link, Action<IServiceCollection>? configure)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information));
        services.AddCsCodeCore();
        // CSCODE_HOME = the data dir: settings (config/settings.json, mirrored from the client), state and logs live there, never in the remote user's ~/.config.
        services.AddSingleton(new AppPaths(name => name == AppPaths.HomeEnvironmentVariable ? dataDir : Environment.GetEnvironmentVariable(name)));
        services.AddSingleton<IAppPaths>(sp => sp.GetRequiredService<AppPaths>());
        services.AddCsCodePlatform();
        services.AddCsCodeLsp();
        // R3: the debug adapter factories and the task runner (the debug service itself, and its UI, stay on the client).
        services.AddCsCodeDap();
        services.AddSingleton(link);
        // T49: the task runner shows a task in a terminal of the client (running on a PTY here) while the connected client offers that; else the
        // runner pipes the task's output into its output channel. Transient: asked per task run (null while the client cannot show one).
        services.AddSingleton(sp => new ServerTaskTerminals(link, sp.GetRequiredService<ILoggerFactory>().CreateLogger("CsCode.Server.Tasks")));
        services.AddTransient<ITaskTerminalHost>(sp => sp.GetRequiredService<ServerTaskTerminals>() is { Available: true } terminals ? terminals : null!);
        ClientServices.Add(services);
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private void RegisterHandlers()
    {
        var sp = _services;
        _link.OnRequest<HelloParams, HelloResult>(RemoteMethods.Hello, (p, _) => ValueTask.FromResult(Hello(p)));
        _link.OnRequest<Empty, Empty>(RemoteMethods.Ping, (_, _) => ValueTask.FromResult(Empty.Value));
        _link.OnRequest<Empty, Empty>(RemoteMethods.Shutdown, (_, _) =>
        {
            _shutdown.TrySetResult();
            return ValueTask.FromResult(Empty.Value);
        });

        _link.OnRequest<Empty, ServerStatus>(RemoteMethods.Status, (_, _) => ValueTask.FromResult(Status()));

        var fs = new FileSystemHandlers(sp.GetRequiredService<IFileSystem>(), sp.GetRequiredService<IoLane>(), () => _clientGzip);
        fs.Register(_link);
        _handlers.Add(_fsHandlers = fs);
        var watch = new WatchHandlers(sp.GetRequiredService<IFileWatcher>());
        watch.Register(_link);
        _handlers.Add(_watchHandlers = watch);
        var search = new SearchHandlers(sp.GetRequiredService<IFileSystem>(), sp.GetRequiredService<IFileWatcher>(), sp.GetRequiredService<IoLane>(), sp.GetRequiredService<PlatformOptions>());
        search.Register(_link);
        _handlers.Add(_searchHandlers = search);
        new WorkspaceHandlers(sp.GetRequiredService<WorkspaceService>(), sp.GetRequiredService<ConfigurationService>(), sp.GetRequiredService<IFileSystem>(),
            MachineSettingsPath(_options.DataDir)).Register(_link);
        var languages = new LanguageHost(sp, _link);
        languages.Register();
        _handlers.Add(_languages = languages);

        // Setting scopes (lsp-core-1): the terminal and debugger settings are registered by services this process never creates (TerminalService,
        // DebugService). Register them here so a remote folder's .vscode/.cscode settings.json cannot choose the shell or debugger run on this host.
        if (sp.GetService<IConfigurationService>() is { } config)
            _handlers.Add(config.RegisterProperties([.. Terminal.Sessions.TerminalSettingKeys.Properties, .. Dap.Service.DebugService.SettingProperties()]));

        // R3: channels (terminals, debug adapters) and tasks
        Channels.Register();
        _handlers.Add(Channels);
        _terminals = new TerminalHandlers(sp, Channels, _loggers.CreateLogger("CsCode.Server.Terminals"), sp.GetRequiredService<ServerTaskTerminals>());
        _terminals.Register(_link);
        _debug = new DebugHandlers(sp, Channels, _loggers.CreateLogger("CsCode.Server.Debug"));
        _debug.Register(_link);
        var tasks = new TaskHandlers(sp.GetRequiredService<ITaskService>(), _loggers.CreateLogger("CsCode.Server.Tasks"));
        tasks.Register(_link);
        _handlers.Add(_tasks = tasks);

        // A (re)attached client cleared its mirrors when its previous connection went away: announce the current state again.
        _link.Attached.Subscribe(_ => languages.OnAttached());
    }

    private FileSystemHandlers? _fsHandlers;
    private WatchHandlers? _watchHandlers;
    private SearchHandlers? _searchHandlers;
    private LanguageHost? _languages;
    private TerminalHandlers? _terminals;
    private DebugHandlers? _debug;
    private TaskHandlers? _tasks;

    private ServerStatus Status() => new(
        _languages?.Documents ?? [],
        _watchHandlers?.Count ?? 0,
        _searchHandlers?.ActiveSessions ?? 0,
        _fsHandlers?.ActiveUploads ?? 0,
        (_services.GetService<Core.Languages.ILanguageFeatureRegistry>() as MirroringFeatureRegistry)?.Registrations.Count ?? 0,
        GC.GetTotalMemory(false),
        System.Diagnostics.Process.GetCurrentProcess().Threads.Count)
    {
        Terminals = _terminals?.Count ?? 0,
        DebugAdapters = _debug?.Count ?? 0,
        Channels = Channels.Count,
        RunningTasks = _tasks?.Running ?? 0,
        Connections = Connections,
        InstanceId = InstanceId,
    };

    private HelloResult Hello(HelloParams p)
    {
        if (p.ProtocolVersion != RemoteProtocol.Version)
            throw new RemoteException(RpcErrorCodes.InvalidRequest, $"Protocol mismatch: client speaks v{p.ProtocolVersion}, this server v{RemoteProtocol.Version}");
        _clientGzip = p.Capabilities.Contains(RemoteProtocol.Capabilities.Gzip);
        _services.GetRequiredService<ServerTaskTerminals>().OnHello(p.Capabilities);
        _log.LogInformation("Client {Version} connected (mode {Mode}, connection {N})", p.ClientVersion, p.Mode, Connections);
        List<string> capabilities = [RemoteProtocol.Capabilities.Gzip, RemoteProtocol.Capabilities.Watch, RemoteProtocol.Capabilities.Search, RemoteProtocol.Capabilities.Languages,
            RemoteProtocol.Capabilities.Debug, RemoteProtocol.Capabilities.Tasks];
        if (Terminal.Pty.PtyHosts.IsSupported) capabilities.Add(RemoteProtocol.Capabilities.Terminals);
        if (_stdio is null) capabilities.Add(RemoteProtocol.Capabilities.Persistent);
        return new HelloResult(
            RemoteProtocol.Version,
            Version,
            OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "Darwin" : OperatingSystem.IsWindows() ? "Windows" : RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Environment.ProcessId,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            _options.DataDir,
            capabilities,
            InstanceId);
    }

    /// <summary>
    /// This host's machine settings file, VS Code's remote <c>data/Machine/settings.json</c>: in a remote window the machine-scoped settings (language
    /// server commands, debugger and shell paths, terminal profiles and env, <c>cscode.lsp.searchDirs</c>, …) come from here, not from the client's
    /// user settings, whose paths are the client machine's.
    /// </summary>
    public static string MachineSettingsPath(string dataDir) => Path.Combine(dataDir, "machine", "settings.json");

    public static string Version =>
        typeof(ServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    /// <summary>
    /// Serves one client connection until it ends (EOF, error), the client asks to shut down, or <paramref name="ct"/> (a newer connection took over).
    /// Returns true when the client asked the server to shut down.
    /// </summary>
    public async Task<bool> ServeAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        var peer = new RpcPeer(input, output, new RpcPeerOptions
        {
            Name = "Server",
            HandlerThreads = 4,
            Json = _link.Json,
            DefaultTimeout = TimeSpan.FromMinutes(5), // server → client requests may wait for the user (message boxes)
        });
        Interlocked.Increment(ref _connections);
        _link.Attach(peer);
        peer.Start();
        using (ct.Register(() => peer.Close(new IOException("another client connection took over"))))
            await Task.WhenAny(peer.Completion, _shutdown.Task).ConfigureAwait(false);
        var shutdown = _shutdown.Task.IsCompleted;
        if (shutdown) await peer.FlushAndCloseAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _link.Detach(peer);
        peer.Close();
        Channels.OnDetached();
        _log.LogInformation(shutdown ? "Client asked to shut down" : "Client connection ended");
        return shutdown;
    }

    /// <summary>The stdio mode: serves the one connection the process was started with.</summary>
    public async Task RunAsync()
    {
        if (_stdio is not { } stdio) throw new InvalidOperationException("This host has no stdio connection (persistent mode serves ServeAsync connections).");
        await ServeAsync(stdio.Input, stdio.Output).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _link.Current?.Close();
        foreach (var h in _handlers) { try { h.Dispose(); } catch (Exception ex) { _log.LogWarning(ex, "Handler cleanup failed"); } }
        // Language servers are stopped (process trees killed) by the container: bounded so a stuck server cannot keep this process alive.
        await Task.WhenAny(_services.DisposeAsync().AsTask(), Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
    }
}
