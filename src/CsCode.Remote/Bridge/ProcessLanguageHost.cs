using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using CsCode.Remote.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Bridge;

/// <summary>
/// <c>cscode.languageHost = process</c>: the whole language client (every language server's client, its JSON, its caches and its GC) runs in a local
/// <c>cscode-server</c> child process at below-normal priority, and this class stands in for <see cref="ILanguageServerManager"/> in the GUI. Local
/// documents sync to it and its providers, diagnostics, prompts and commands are bridged back exactly like a remote workspace's (identity URI
/// mapping). In process mode the child is started when this host is first resolved, restarted with backoff if it dies, and stopped (tree killed)
/// on dispose; resolved in inProcess mode, the host stays idle and starts nothing. The child is the self-contained server's own executable in an
/// installed (self-contained) app, else <c>dotnet cscode-server.dll</c> (<see cref="LocalServer.Launch"/>).
/// </summary>
public sealed class ProcessLanguageHost : ILanguageServerManager, IWillSaveParticipant, IFileOperationParticipant, IAsyncDisposable, IDisposable
{
    public const string Owner = "lsp-process";

    private readonly RemoteClient _client;
    private readonly DisposableStore _owned = new();
    private readonly Emitter<IReadOnlyList<LanguageServerInfo>> _serversChanged = new();
    private readonly RemoteSaveParticipant _save;
    private readonly ILogger _log;
    private readonly List<(long Seq, LanguageServerDefinition Definition, long? Token)> _definitions = new();
    private IReadOnlyList<LanguageServerInfo> _servers = [];
    private IReadOnlyList<LanguageServerDefinition> _serverDefinitions = [];
    private IDisposable? _tracking;
    private long _seq;
    private int _disposed;

    public ProcessLanguageHost(IServiceProvider sp, RemoteOptions options)
    {
        _log = sp.GetService<ILoggerFactory>()?.CreateLogger("CsCode.Remote.LanguageHost") ?? NullLogger.Instance;
        var paths = sp.GetService<IAppPaths>();
        var output = sp.GetService<IOutputService>();
        // Resolved per connection attempt, inside the client's connect (off the thread that resolved this service: no disk access there).
        IRemoteTransport Transport()
        {
            var assembly = LocalServer.FindServerAssembly(options.LocalServerAssembly)
                           ?? throw new RemoteBootstrapException("cscode-server.dll was not found next to the program; cscode.languageHost = process needs it") { IsPermanent = true };
            var dataDir = options.LocalServerDataDir ?? Path.Combine(paths?.GlobalStorageDir ?? Path.GetTempPath(), "language-host");
            return new LoopbackTransport(LocalServer.Launch(assembly, dataDir, options.ServerMarker, environment: options.LocalServerEnvironment));
        }
        _client = new RemoteClient("local language host", Transport, UriMapper.Identity, "process", options.Reconnect ?? new ReconnectPolicy(5),
            options.Time, _log, line => output?.GetOrCreate("Language Host").AppendLine(line)) { CallTimeout = options.CallTimeout };
        _save = new RemoteSaveParticipant(() => _client.Session);

        var workspace = new WorkspaceFoldersMirror(sp.GetService<IWorkspaceService>(), UriMapper.Identity, _log);
        _owned.Add(workspace);
        _client.AddParticipant(workspace);
        var settings = new UserSettingsMirror(sp.GetService<IConfigurationService>(), sp.GetService<IFileSystem>(), _log);
        _owned.Add(settings);
        _client.AddParticipant(settings);
        RemoteConnectionManager.AddLanguageParticipants(sp, _client, UriMapper.Identity, _owned, Owner, "", _log);
        _client.AddParticipant(new ServerListParticipant(this));
        _owned.Add(_client.SessionChanged.Subscribe(session =>
        {
            Interlocked.Exchange(ref _tracking, null)?.Dispose();
            if (session is not null) _tracking = sp.GetService<IMemoryMonitor>()?.TrackChildProcess("cscode-server (language host)", session.Channel.ProcessId);
        }));
        // Only the language host of process mode starts its child. Resolved in inProcess mode (a composition that resolves every service), it stays
        // idle: no cscode-server, and nothing to stop on dispose.
        if (sp.GetService<LanguageHostMode>() is not { IsProcess: false })
        {
            _started = true;
            _ = StartAsync();
        }
    }

    private readonly bool _started;

    public RemoteClient Client => _client;

    /// <summary>Whether this host started (and owns) a child process: false when it was resolved in inProcess mode.</summary>
    public bool IsStarted => _started;

    private async Task StartAsync()
    {
        try { await _client.ConnectAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.LogError(ex, "The local language host did not start; language features are unavailable"); }
    }

    /// <summary>Completes once the child is connected and the open documents are synced (tests).</summary>
    public async Task WaitConnectedAsync(CancellationToken ct)
    {
        while (_client.Session is null)
        {
            if (_client.Status.State == Core.Remote.RemoteConnectionState.Failed) throw new IOException(_client.Status.Message);
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------- ILanguageServerManager

    public IReadOnlyList<LanguageServerDefinition> Definitions => Volatile.Read(ref _serverDefinitions);
    public IReadOnlyList<LanguageServerInfo> Servers => Volatile.Read(ref _servers);
    public IEventSource<IReadOnlyList<LanguageServerInfo>> ServersChanged => _serversChanged;

    public IDisposable AddDefinition(LanguageServerDefinition definition)
    {
        long seq;
        lock (_definitions) { seq = ++_seq; _definitions.Add((seq, definition, null)); }
        if (_client.Session is { IsOpen: true } s) _ = SendDefinitionAsync(s, seq, definition);
        return Disposable.Create(() =>
        {
            long? token;
            lock (_definitions)
            {
                var i = _definitions.FindIndex(d => d.Seq == seq);
                if (i < 0) return;
                token = _definitions[i].Token;
                _definitions.RemoveAt(i);
            }
            if (token is { } t && _client.Session is { IsOpen: true } live) live.Peer.Fire(RemoteMethods.LspRemoveDefinition, new LspRemoveDefinitionParams(t));
        });
    }

    private async Task SendDefinitionAsync(RemoteSession session, long seq, LanguageServerDefinition definition)
    {
        try
        {
            var r = await session.Peer.InvokeAsync<LanguageServerDefinition, LspDefinitionResult>(RemoteMethods.LspAddDefinition, definition).ConfigureAwait(false);
            lock (_definitions)
            {
                var i = _definitions.FindIndex(d => d.Seq == seq);
                if (i >= 0) _definitions[i] = _definitions[i] with { Token = r?.Token };
                else if (r is not null) session.Peer.Fire(RemoteMethods.LspRemoveDefinition, new LspRemoveDefinitionParams(r.Token)); // removed meanwhile
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { _log.LogWarning("Could not add language server {Id}: {Error}", definition.Id, ex.Message); }
    }

    public ValueTask StartAsync(string id, CancellationToken ct = default) => ControlAsync("start", id, ct);
    public ValueTask StopAsync(string id, CancellationToken ct = default) => ControlAsync("stop", id, ct);
    public ValueTask RestartAsync(string id, CancellationToken ct = default) => ControlAsync("restart", id, ct);
    public ValueTask StopAllAsync(CancellationToken ct = default) => ControlAsync("stopAll", null, ct);

    private async ValueTask ControlAsync(string action, string? id, CancellationToken ct)
    {
        if (_client.Session is not { IsOpen: true } s) return;
        await s.Peer.InvokeAsync<LspControlParams, Empty>(RemoteMethods.LspControl, new LspControlParams(action, id), ct).ConfigureAwait(false);
    }

    /// <summary>Re-adds this client's definitions on a (new) server and mirrors its server list.</summary>
    private sealed class ServerListParticipant(ProcessLanguageHost host) : IRemoteParticipant
    {
        public void Attach(RpcPeer peer) =>
            peer.OnNotification<LspServersParams>(RemoteMethods.LspServers, p =>
            {
                Volatile.Write(ref host._servers, p.Servers);
                Volatile.Write(ref host._serverDefinitions, p.Definitions);
                host._serversChanged.Fire(p.Servers);
            });

        public async Task OnConnectedAsync(RemoteSession session, CancellationToken ct)
        {
            (long Seq, LanguageServerDefinition Definition, long? Token)[] defs;
            lock (host._definitions) defs = host._definitions.ToArray();
            foreach (var d in defs) await host.SendDefinitionAsync(session, d.Seq, d.Definition).ConfigureAwait(false);
            session.Peer.Fire(RemoteMethods.LspControl, new LspControlParams("refresh"));
        }

        public void OnDisconnected()
        {
            Volatile.Write(ref host._servers, []);
            host._serversChanged.Fire([]);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------- participants

    public ValueTask<IReadOnlyList<TextEdit>?> OnWillSaveAsync(ITextDocument document, DocumentSaveReason reason, CancellationToken ct) => _save.OnWillSaveAsync(document, reason, ct);
    public ValueTask<WorkspaceEdit?> WillRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct) => _save.WillRenameFilesAsync(files, ct);
    public ValueTask DidRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct) => _save.DidRenameFilesAsync(files, ct);
    public ValueTask DidCreateFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct) => _save.DidCreateFilesAsync(files, ct);
    public ValueTask DidDeleteFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct) => _save.DidDeleteFilesAsync(files, ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_started) await _client.DisposeAsync().ConfigureAwait(false);
        DisposeOwned();
    }

    private void DisposeOwned()
    {
        Interlocked.Exchange(ref _tracking, null)?.Dispose();
        _owned.Dispose();
        _serversChanged.Clear();
    }

    /// <summary>
    /// Synchronous disposal (container shutdown without DisposeAsync). A host that never started has no child to stop and returns at once; one
    /// that did waits for the child to be stopped, bounded so it can never hang the exit (the shell disposes it asynchronously first).
    /// </summary>
    public void Dispose()
    {
        if (!_started)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) DisposeOwned();
            return;
        }
        DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
    }
}
