using System.Reflection;
using System.Runtime.InteropServices;
using CsCode.Core;
using CsCode.Core.Platform;
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
/// One server process = one client connection. Composes the real workspace services (Core, Platform with its own data directory, Lsp), registers
/// the protocol handlers on the connection and runs until the client goes away (EOF), asks to shut down, or the connection breaks. Disposal stops
/// the language servers (killing their process trees), every watch and search, then the container.
/// </summary>
public sealed class ServerHost : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly RpcPeer _peer;
    private readonly ServiceProvider _services;
    private readonly ILogger _log;
    private readonly List<IDisposable> _handlers = new();
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _clientGzip;
    private int _disposed;

    public ServerHost(ServerOptions options, Stream input, Stream output, Action<IServiceCollection>? configure = null)
    {
        _options = options;
        Directory.CreateDirectory(options.DataDir);
        _peer = new RpcPeer(input, output, new RpcPeerOptions
        {
            Name = "Server",
            HandlerThreads = 4,
            Json = RemoteJson.Create(),
            DefaultTimeout = TimeSpan.FromMinutes(5), // server → client requests may wait for the user (message boxes)
        });
        _services = BuildServices(options.DataDir, _peer, configure);
        _log = _services.GetRequiredService<ILoggerFactory>().CreateLogger("CsCode.Server");
        RegisterHandlers();
    }

    public RpcPeer Peer => _peer;
    public IServiceProvider Services => _services;

    /// <summary>The server's composition: the same modules as the GUI's workspace layer, with this process's own paths.</summary>
    internal static ServiceProvider BuildServices(string dataDir, RpcPeer peer, Action<IServiceCollection>? configure)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information));
        services.AddCsCodeCore();
        // CSCODE_HOME = the data dir: settings (config/settings.json, mirrored from the client), state and logs live there, never in the remote user's ~/.config.
        services.AddSingleton(new AppPaths(name => name == AppPaths.HomeEnvironmentVariable ? dataDir : Environment.GetEnvironmentVariable(name)));
        services.AddSingleton<IAppPaths>(sp => sp.GetRequiredService<AppPaths>());
        services.AddCsCodePlatform();
        services.AddCsCodeLsp();
        services.AddSingleton(peer);
        ClientServices.Add(services);
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private void RegisterHandlers()
    {
        var sp = _services;
        _peer.OnRequest<HelloParams, HelloResult>(RemoteMethods.Hello, (p, _) => ValueTask.FromResult(Hello(p)));
        _peer.OnRequest<Empty, Empty>(RemoteMethods.Ping, (_, _) => ValueTask.FromResult(Empty.Value));
        _peer.OnRequest<Empty, Empty>(RemoteMethods.Shutdown, (_, _) =>
        {
            _shutdown.TrySetResult();
            return ValueTask.FromResult(Empty.Value);
        });

        _peer.OnRequest<Empty, ServerStatus>(RemoteMethods.Status, (_, _) => ValueTask.FromResult(Status()));

        var fs = new FileSystemHandlers(sp.GetRequiredService<IFileSystem>(), sp.GetRequiredService<IoLane>(), () => _clientGzip);
        fs.Register(_peer);
        _handlers.Add(_fsHandlers = fs);
        var watch = new WatchHandlers(sp.GetRequiredService<IFileWatcher>());
        watch.Register(_peer);
        _handlers.Add(_watchHandlers = watch);
        var search = new SearchHandlers(sp.GetRequiredService<IFileSystem>(), sp.GetRequiredService<IFileWatcher>(), sp.GetRequiredService<IoLane>(), sp.GetRequiredService<PlatformOptions>());
        search.Register(_peer);
        _handlers.Add(_searchHandlers = search);
        new WorkspaceHandlers(sp.GetRequiredService<WorkspaceService>(), sp.GetRequiredService<ConfigurationService>(), sp.GetRequiredService<IFileSystem>()).Register(_peer);
        var languages = new LanguageHost(sp, _peer);
        languages.Register();
        _handlers.Add(_languages = languages);
    }

    private FileSystemHandlers? _fsHandlers;
    private WatchHandlers? _watchHandlers;
    private SearchHandlers? _searchHandlers;
    private LanguageHost? _languages;

    private ServerStatus Status() => new(
        _languages?.Documents ?? [],
        _watchHandlers?.Count ?? 0,
        _searchHandlers?.ActiveSessions ?? 0,
        _fsHandlers?.ActiveUploads ?? 0,
        (_services.GetService<Core.Languages.ILanguageFeatureRegistry>() as MirroringFeatureRegistry)?.Registrations.Count ?? 0,
        GC.GetTotalMemory(false),
        System.Diagnostics.Process.GetCurrentProcess().Threads.Count);

    private HelloResult Hello(HelloParams p)
    {
        if (p.ProtocolVersion != RemoteProtocol.Version)
            throw new RemoteException(RpcErrorCodes.InvalidRequest, $"Protocol mismatch: client speaks v{p.ProtocolVersion}, this server v{RemoteProtocol.Version}");
        _clientGzip = p.Capabilities.Contains(RemoteProtocol.Capabilities.Gzip);
        _log.LogInformation("Client {Version} connected (mode {Mode})", p.ClientVersion, p.Mode);
        return new HelloResult(
            RemoteProtocol.Version,
            Version,
            OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "Darwin" : OperatingSystem.IsWindows() ? "Windows" : RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Environment.ProcessId,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            _options.DataDir,
            [RemoteProtocol.Capabilities.Gzip, RemoteProtocol.Capabilities.Watch, RemoteProtocol.Capabilities.Search, RemoteProtocol.Capabilities.Languages]);
    }

    public static string Version =>
        typeof(ServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    /// <summary>Serves until the connection ends or the client asks to shut down.</summary>
    public async Task RunAsync()
    {
        _peer.Start();
        await Task.WhenAny(_peer.Completion, _shutdown.Task).ConfigureAwait(false);
        if (_shutdown.Task.IsCompleted) await _peer.FlushAndCloseAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _log.LogInformation("Client disconnected; shutting down");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _peer.Close();
        foreach (var h in _handlers) { try { h.Dispose(); } catch (Exception ex) { _log.LogWarning(ex, "Handler cleanup failed"); } }
        // Language servers are stopped (process trees killed) by the container: bounded so a stuck server cannot keep this process alive.
        await Task.WhenAny(_services.DisposeAsync().AsTask(), Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
    }
}
