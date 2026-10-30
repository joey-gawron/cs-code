using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Core.Text;
using CsCode.Remote.Askpass;
using CsCode.Remote.Bridge;
using CsCode.Remote.Install;
using CsCode.Remote.Protocol;
using CsCode.Remote.Ssh;
using CsCode.Remote.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Client;

/// <summary>
/// <see cref="IRemoteConnectionManager"/> for SSH remotes: one connected host at a time. Builds the ssh transport from the <c>remote.ssh.*</c>
/// settings (per-host overrides honoured), routes ssh prompts to the GUI (askpass), bootstraps the server, and wires every remote participant:
/// the <c>ssh:</c> watcher, workspace folders and user settings mirrors, document sync, language bridge, diagnostics mirror, UI forwarding and
/// contributed commands. Also the <see cref="IRemoteEndpoint"/> behind the <c>ssh:</c> file system, watcher and search providers.
/// Status lines and ssh's own stderr go to the "Remote" output channel. All dependencies are resolved lazily (the file system router resolves this
/// class on its first ssh: call), so construction never cycles back.
/// </summary>
public sealed class RemoteConnectionManager : IRemoteConnectionManager, IRemoteEndpoint, IAsyncDisposable, IDisposable
{
    public const string OutputChannel = "Remote";

    private readonly IServiceProvider _sp;
    private readonly RemoteOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Emitter<RemoteConnectionStatus> _statusChanged = new();
    private readonly ILogger _log;
    private Host? _host;
    private bool _forceReinstall;
    private RemoteConnectionStatus _lastStatus = RemoteConnectionStatus.Disconnected;

    /// <summary>One host's client plus the participants and subscriptions that belong to it.</summary>
    private sealed class Host(RemoteClient client, SshUriMapper mapper, DisposableStore owned, RemoteChannels channels, string? serverSession)
    {
        public RemoteClient Client { get; } = client;
        public SshUriMapper Mapper { get; } = mapper;
        public DisposableStore Owned { get; } = owned;
        /// <summary>Terminals and debug adapters on that host (R3), resumed across reconnects (R4).</summary>
        public RemoteChannels Channels { get; } = channels;
        /// <summary>The persistent server's session id (null: persistence is off, the server ends with each connection).</summary>
        public string? ServerSession { get; } = serverSession;
    }

    public RemoteConnectionManager(IServiceProvider sp, RemoteOptions? options = null)
    {
        _sp = sp;
        _options = options ?? new RemoteOptions();
        _log = sp.GetService<ILoggerFactory>()?.CreateLogger("CsCode.Remote") ?? NullLogger.Instance;
        Directories = new DirectoryCache();
        FileSystem = new RemoteFileSystem(this);
        Watcher = new RemoteWatcher(this, _log);
        Search = new RemoteSearch(this);
        TaskRuns = new RemoteTaskRuns(_log);
        SaveParticipant = new RemoteSaveParticipant(() => _host?.Client.Session);
        if (sp.GetService<IMemoryMonitor>() is { } memory) _trimToken = memory.Register(Directories);
        var storage = sp.GetService<IAppPaths>()?.GlobalStorageDir;
        _sessions = new ServerSessionRegistry(storage is null ? null : Path.Combine(storage, "remote", "sessions"));
    }

    private readonly ServerSessionRegistry _sessions;

    private readonly IDisposable? _trimToken;

    public RemoteFileSystem FileSystem { get; }
    public RemoteWatcher Watcher { get; }
    public RemoteSearch Search { get; }
    /// <summary>The remote task runs being waited for (claimed on the server after every reconnect).</summary>
    public RemoteTaskRuns TaskRuns { get; }
    public RemoteSaveParticipant SaveParticipant { get; }
    public DirectoryCache Directories { get; }

    public RemoteConnectionStatus Status => _host?.Client.Status ?? _lastStatus;
    public IEventSource<RemoteConnectionStatus> StatusChanged => _statusChanged;

    /// <summary>The live session (null when not connected).</summary>
    public RemoteSession? Session => _host?.Client.Session;

    /// <summary>The configured host's terminals and debug adapters (null before a host was configured).</summary>
    public RemoteChannels? Channels => _host?.Channels;

    /// <summary>The persistent server's session id for the configured host (null when persistence is off).</summary>
    public string? ServerSessionId => _host?.ServerSession;

    internal ILogger Logger => _log;

    /// <summary>
    /// The live session of the configured host; while a connection is being made or remade (connecting, bootstrapping, reconnecting, or the moment
    /// between a drop and the reconnect) it waits for it, up to <paramref name="wait"/>. Fails with <see cref="RemoteDisconnectedException"/> when no
    /// host is configured, the connection failed or was closed, or the wait ran out. Never blocks the caller (async).
    /// </summary>
    public async Task<RemoteSession> WaitForSessionAsync(TimeSpan wait, CancellationToken ct)
    {
        var h = _host ?? throw new RemoteDisconnectedException("Not connected to a remote host");
        if (h.Client.Session is { IsOpen: true } live) return live;
        if (h.Client.Status.State is RemoteConnectionState.Failed or RemoteConnectionState.Disconnected) h.Client.Require(); // throws with the reason
        var ready = new TaskCompletionSource<RemoteSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sessions = h.Client.SessionChanged.Subscribe(s => { if (s is { IsOpen: true }) ready.TrySetResult(s); });
        using var states = h.Client.StatusChanged.Subscribe(s =>
        {
            if (s.State is RemoteConnectionState.Failed or RemoteConnectionState.Disconnected)
                ready.TrySetException(new RemoteDisconnectedException($"Not connected to {h.Client.Host}: {s.Message}"));
        });
        if (h.Client.Session is { IsOpen: true } raced) return raced;
        try { return await ready.Task.WaitAsync(wait, ct).ConfigureAwait(false); }
        catch (TimeoutException) { throw new RemoteDisconnectedException($"Still not connected to {h.Client.Host} after {wait.TotalSeconds:0} s"); }
    }

    private T? Get<T>() where T : class => _sp.GetService<T>();

    private void Log(string line)
    {
        try { Get<IOutputService>()?.GetOrCreate(OutputChannel).AppendLine(DateTime.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " " + line); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
    }

    // ------------------------------------------------------------------------------------------------------------------- IRemoteConnectionManager

    public async ValueTask ConnectAsync(string host, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var authority = RemoteAuthority.Parse(host);
        await new PoolHop(); // composing a host reads settings and may find bundles on disk: not on the caller's (UI) thread
        Host target;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_host is { } current && current.Mapper.Authority == authority) target = current;
            else
            {
                if (_host is { } old) await CloseHostAsync(old).ConfigureAwait(false);
                target = _host = CreateHost(host, authority);
            }
        }
        finally { _gate.Release(); }
        await target.Client.ConnectAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        await new PoolHop();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_host is { } h) await h.Client.DisconnectAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask ReconnectAsync(CancellationToken ct = default)
    {
        var h = _host ?? throw new InvalidOperationException("No remote host to reconnect to");
        await h.Client.ReconnectAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Uploads the server again (ignoring an existing install) and reconnects.</summary>
    public async ValueTask ReinstallServerAsync(CancellationToken ct = default)
    {
        _forceReinstall = true;
        await ReconnectAsync(ct).ConfigureAwait(false);
    }

    public Uri? ToUri(string remotePath) => _host?.Mapper.FromRemotePath(remotePath);

    /// <summary>The configured host's display name (as typed), or null.</summary>
    public string? HostName => _host?.Client.Host;

    // ------------------------------------------------------------------------------------------------------------------- IRemoteEndpoint

    public (RemoteSession Session, SshUriMapper Mapper) Resolve(Uri uri)
    {
        var h = _host;
        if (h is null || !h.Mapper.Handles(uri)) throw new RemoteDisconnectedException($"Not connected to {uri.Authority}");
        return (h.Client.Require(), h.Mapper);
    }

    public async ValueTask<(RemoteSession Session, SshUriMapper Mapper)> ResolveAsync(Uri uri, CancellationToken ct)
    {
        var h = _host;
        if (h is null || !h.Mapper.Handles(uri)) throw new RemoteDisconnectedException($"Not connected to {uri.Authority}");
        if (h.Client.Session is { IsOpen: true } live) return (live, h.Mapper);
        // Connecting (a restored session reconnects at start-up) or reconnecting after a drop: file operations wait for it instead of failing.
        return (await WaitForSessionAsync(_options.ReconnectWait, ct).ConfigureAwait(false), h.Mapper);
    }

    public SshUriMapper? MapperFor(Uri uri) => _host is { } h && h.Mapper.Handles(uri) ? h.Mapper : null;

    // ------------------------------------------------------------------------------------------------------------------- composition

    private Host CreateHost(string host, RemoteAuthority authority)
    {
        var mapper = new SshUriMapper(authority);
        var config = Get<IConfigurationService>();
        var attempts = RemoteSettings.For(config, host, RemoteSettings.ReconnectAttempts, 8);
        var grace = _options.GracePeriod is { } g ? (int)g.TotalSeconds : RemoteSettings.For(config, host, RemoteSettings.ReconnectGracePeriod, 10800);
        var serverSession = grace > 0 ? _options.ServerSession ?? _sessions.Claim(authority.Root.AbsoluteUri) : null;
        var client = new RemoteClient(host, () => CreateTransport(host, authority, serverSession, grace), mapper, "ssh", _options.Reconnect ?? new ReconnectPolicy(attempts),
            _options.Time, _log, Log, authority.Root) { CallTimeout = _options.CallTimeout };
        var owned = new DisposableStore();
        var channels = new RemoteChannels(_log);
        owned.Add(channels);
        client.AddParticipant(Watcher);
        client.AddParticipant(channels); // early: terminal output and debug streams resume first after a reconnect
        client.AddParticipant(TaskRuns);
        AddParticipants(client, mapper, owned, "remote:" + host, "Remote: ");
        owned.Add(client.StatusChanged.Subscribe(s =>
        {
            _lastStatus = s;
            // Gone for good (retries spent, or closed by the user): remote terminals and debug streams end and say why.
            if (s.State is RemoteConnectionState.Failed or RemoteConnectionState.Disconnected)
                channels.FailAll(s.State == RemoteConnectionState.Failed ? $"The connection to {host} was lost: {s.Message}" : $"The connection to {host} was closed.");
            _statusChanged.Fire(s);
        }));
        IDisposable? tracking = null;
        owned.Add(client.SessionChanged.Subscribe(session =>
        {
            Interlocked.Exchange(ref tracking, null)?.Dispose();
            if (session is null) return;
            session.Channel.StderrLine = line => Log("[ssh] " + line);
            tracking = Get<IMemoryMonitor>()?.TrackChildProcess("ssh " + host, session.Channel.ProcessId);
        }));
        owned.Add(Disposable.Create(() => Interlocked.Exchange(ref tracking, null)?.Dispose()));
        return new Host(client, mapper, owned, channels, serverSession);
    }

    /// <summary>The participants every language-hosting connection shares (ssh remote and the local process host).</summary>
    internal void AddParticipants(RemoteClient client, UriMapper mapper, DisposableStore owned, string owner, string outputPrefix)
    {
        var workspace = new WorkspaceFoldersMirror(Get<IWorkspaceService>(), mapper, _log);
        owned.Add(workspace);
        client.AddParticipant(workspace);
        var settings = new UserSettingsMirror(Get<IConfigurationService>(), Get<IFileSystem>(), _log);
        owned.Add(settings);
        client.AddParticipant(settings);
        AddLanguageParticipants(_sp, client, mapper, owned, owner, outputPrefix, _log);
    }

    internal static void AddLanguageParticipants(IServiceProvider sp, RemoteClient client, UriMapper mapper, DisposableStore owned, string owner, string outputPrefix, ILogger log)
    {
        if (sp.GetService<IDocumentManager>() is { } documents)
        {
            var sync = new DocumentSync(documents, mapper, log);
            owned.Add(sync);
            client.AddParticipant(sync);
        }
        if (sp.GetService<ILanguageFeatureRegistry>() is { } registry)
        {
            var bridge = new LanguageBridge(registry, mapper, owner, log);
            owned.Add(bridge);
            client.AddParticipant(bridge);
        }
        if (sp.GetService<IDiagnosticService>() is { } diagnostics) client.AddParticipant(new DiagnosticsMirror(diagnostics, owner));
        client.AddParticipant(new UiForwarding(sp.GetService<IWindowService>, sp.GetService<INavigationService>, sp.GetService<IOutputService>, outputPrefix));
        client.AddParticipant(new CommandsBridge(sp.GetService<ICommandService>, log));
    }

    private IRemoteTransport CreateTransport(string host, RemoteAuthority authority, string? serverSession, int graceSeconds)
    {
        if (_options.TransportFactory is { } factory) return factory(host, authority);
        var config = Get<IConfigurationService>();
        var ssh = new SshOptions
        {
            SshPath = RemoteSettings.For(config, host, RemoteSettings.SshPath, "ssh"),
            ConfigFile = NullIfEmpty(RemoteSettings.For(config, host, RemoteSettings.ConfigFile, "")),
            ExtraOptions = RemoteSettings.For<string[]>(config, host, RemoteSettings.Options, []),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(RemoteSettings.For(config, host, RemoteSettings.ConnectTimeout, 30), 1, 600)),
            Environment = _options.SshEnvironment,
        };
        var bundles = _options.Bundles ?? ServerBundleSet.Find(NullIfEmpty(RemoteSettings.For(config, host, RemoteSettings.ServerBundles, "")),
            Get<IAppPaths>()?.InstallDir ?? AppContext.BaseDirectory);
        var launch = new ServerLaunchOptions
        {
            InstallRoot = NullIfEmpty(RemoteSettings.For(config, host, RemoteSettings.ServerInstallPath, "")),
            DataDir = NullIfEmpty(RemoteSettings.For(config, host, RemoteSettings.ServerDataPath, "")),
            Marker = _options.ServerMarker,
            Bundles = bundles,
            ForceReinstall = Interlocked.Exchange(ref _forceReinstall, false),
            PersistSession = serverSession,
            GraceSeconds = graceSeconds,
        };
        if (authority.Scheme != RemoteSchemes.Ssh)
        {
            // A container (docker exec) or a WSL distribution (wsl.exe --exec): the same fixed scripts and bootstrap, another carrier.
            IRemoteShell shell = authority.Scheme == RemoteSchemes.Docker
                ? new DockerRemoteShell(config?.Get(RemoteSettings.DockerPath, "docker") ?? "docker", authority, _options.SshEnvironment)
                : new WslRemoteShell(config?.Get(RemoteSettings.WslPath, "wsl.exe") ?? "wsl.exe", authority, _options.SshEnvironment);
            Log($"Connecting to {authority} with {shell.Description}; bundles: {bundles?.Directory ?? "(none found)"}");
            return new ShellServerTransport(shell, launch);
        }
        var askpass = RemoteSettings.For(config, host, RemoteSettings.UseAskpass, true) && !OperatingSystem.IsWindows() ? Get<IWindowService>() : null;
        Log($"Connecting to {host} with {ssh.SshPath}{(ssh.ConfigFile is null ? "" : " -F " + ssh.ConfigFile)}; bundles: {bundles?.Directory ?? "(none found)"}");
        return new SshServerTransport(ssh, authority, launch, askpass, _options.AskpassCommand ?? RemoteOptions.DefaultAskpassCommand(), _log, _options.AskpassDirectory);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static async Task CloseHostAsync(Host h)
    {
        await h.Client.DisposeAsync().ConfigureAwait(false);
        h.Owned.Dispose();
    }

    /// <summary>Synchronous container disposal (the shell disposes this asynchronously first, which makes this a no-op): bounded, never hangs the exit.</summary>
    public void Dispose()
    {
        if (_host is null) { _trimToken?.Dispose(); _statusChanged.Clear(); return; }
        Task.Run(() => DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(10));
    }

    public async ValueTask DisposeAsync()
    {
        _trimToken?.Dispose();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _host, null) is { } h) await CloseHostAsync(h).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        _statusChanged.Clear();
    }
}

/// <summary>The ssh transport with prompts routed to the GUI: an <see cref="AskpassServer"/> lives for the duration of one connect (all its ssh runs).</summary>
public sealed class SshServerTransport(SshOptions ssh, RemoteAuthority destination, ServerLaunchOptions launch, IWindowService? askpassWindow,
    IReadOnlyList<string> askpassCommand, ILogger? log = null, string? askpassDirectory = null) : IRemoteTransport
{
    public string Description => "ssh " + destination.Destination;

    public async Task<RemoteChannel> ConnectAsync(IProgress<string>? progress, CancellationToken ct)
    {
        AskpassServer? askpass = null;
        try
        {
            var options = ssh;
            if (askpassWindow is not null)
            {
                askpass = AskpassServer.Start(askpassWindow, askpassCommand, log, askpassDirectory);
                var env = new Dictionary<string, string?>(ssh.Environment ?? new Dictionary<string, string?>());
                foreach (var (k, v) in askpass.Environment) env[k] = v;
                options = ssh with { Environment = env };
            }
            // `ssh -G`: the effective settings for the destination (the user's ssh_config applied) — logged, and a broken configuration fails here
            // with ssh's own message before anything else runs.
            var resolved = await RemoteShellExtensions.RunAsync(new ProcessLaunch(options.SshPath, SshArgs.ResolveConfig(options, destination)) { Environment = options.Environment },
                null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (resolved.ExitCode != 0)
                throw new RemoteBootstrapException($"ssh rejected the configuration for {destination.Destination}: {resolved.Stderr.Trim()}");
            var effective = SshEffectiveConfig.Parse(resolved.Stdout);
            progress?.Report($"Resolved {destination.Destination}: {effective.User}@{effective.HostName}:{effective.Port}" +
                             (effective.ProxyJump.Count > 0 ? " via " + string.Join(", ", effective.ProxyJump) : ""));
            return await new ShellServerTransport(new SshRemoteShell(options, destination), launch).ConnectAsync(progress, ct).ConfigureAwait(false);
        }
        finally { askpass?.Dispose(); }
    }
}
