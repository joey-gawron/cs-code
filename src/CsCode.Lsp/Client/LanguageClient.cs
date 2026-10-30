using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Features;
using CsCode.Lsp.Manager;
using CsCode.Lsp.Runtime;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Client;

internal enum ClientExitKind { Stopped, Crashed, StartFailed }

internal sealed record ClientExit(ClientExitKind Kind, string? Reason);

/// <summary>Receives lifecycle notifications from a client (implemented by the supervising server slot).</summary>
internal interface IClientObserver
{
    void OnHealthChanged(LanguageClient client, ServerHealth health);
    void OnExited(LanguageClient client, ClientExit exit);
    /// <summary>The server's process tree went over its memory cap (<paramref name="cap"/> bytes): the slot decides whether to restart it (T39).</summary>
    void OnMemoryCapExceeded(LanguageClient client, long cap);
}

/// <summary>Everything shared by the clients of one manager.</summary>
internal sealed class ClientContext
{
    public required LspServices Services { get; init; }
    public required Func<LspSettings> Settings { get; init; }
    public required LanguageWorkQueue Queue { get; init; }
    public required ILspConnectionFactory Connections { get; init; }
    public required ILogger Logger { get; init; }
}

/// <summary>
/// One session with one language server (one connection, one initialize handshake). Owns the JSON-RPC peer, document sync, the
/// feature providers it registers, diagnostics, progress, dynamic registrations and everything else that must die with the session.
/// All non-trivial work (request building, (de)serialisation, mapping, change coalescing) runs on the Language lane; callers on any
/// other thread only enqueue. A client never restarts itself: it reports to its <see cref="IClientObserver"/> and the slot decides.
/// </summary>
internal sealed partial class LanguageClient : IAsyncDisposable
{
    private readonly ClientContext _ctx;
    private readonly IClientObserver _observer;
    private readonly DisposableStore _store = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sendLock = new();
    private readonly CircuitBreaker _breaker;
    private readonly ConcurrentDictionary<Uri, SyncedDocument> _docs = new();
    private readonly string _owner;
    private readonly ServerLog _log;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private JsonRpc? _rpc;
    private ILspConnection? _connection;
    private int _state = (int)LanguageServerState.Stopped;
    private int _tornDown;
    private int _connectionLostHandled;
    private volatile bool _docsMayOpen;
    private long _inFlight;
    private long _lastTimeoutNoticeTicks;
    private readonly object _memoryGate = new();
    private IDisposable? _memoryTracking;
    private long? _memoryCap;
    private bool _memoryCapSet;

    public LanguageClient(ClientContext ctx, LanguageServerDefinition definition, string slotKey, Uri? rootUri, IReadOnlyList<Uri> workspaceFolders, IClientObserver observer)
    {
        _ctx = ctx;
        _observer = observer;
        Definition = definition;
        SlotKey = slotKey;
        RootUri = rootUri;
        WorkspaceFolders = workspaceFolders;
        _owner = "lsp:" + definition.Id;
        var s = ctx.Settings();
        _breaker = new CircuitBreaker(s.BreakerThreshold, s.BreakerProbeInterval);
        _breaker.HealthChanged += OnBreakerHealthChanged;
        _log = new ServerLog(ctx.Services.Output?.GetOrCreate($"{definition.DisplayName} (LSP)"), ctx.Queue);
        _features = new FeatureTable(this);
        Mapper = new LspMapper(PositionEncoder.Utf16, LineSource);
    }

    public LanguageServerDefinition Definition { get; }
    public string SlotKey { get; }
    public Uri? RootUri { get; }
    public IReadOnlyList<Uri> WorkspaceFolders { get; }
    public string OwnerId => _owner;
    public LspMapper Mapper { get; private set; }
    public P.ServerCapabilities? ServerCapabilities { get; private set; }
    public string? ServerName { get; private set; }
    public string? ServerVersion { get; private set; }
    public int? ProcessId => _connection?.ProcessId;
    public string? LastError { get; private set; }
    public ServerHealth Health => _breaker.Health;
    public int InFlightRequests => (int)Interlocked.Read(ref _inFlight);
    public int SyncedDocumentCount => _docs.Count;
    public Task Stopped => _stopped.Task;
    internal LspServices Services => _ctx.Services;
    internal ClientContext Context => _ctx;
    internal ServerLog Log => _log;
    internal CircuitBreaker Breaker => _breaker;
    internal FeatureTable Features => _features;
    internal LspSettings Settings => _ctx.Settings();

    public LanguageServerState State => (LanguageServerState)Volatile.Read(ref _state);
    public bool IsOperational => State is LanguageServerState.Running or LanguageServerState.Unresponsive;

    private void SetState(LanguageServerState s) => Volatile.Write(ref _state, (int)s);

    // Only open documents can be asked for line text (needed for non-UTF-16 encodings); closed files keep the server's columns.
    private string? LineSource(Uri uri, int line) =>
        _ctx.Services.Documents.Get(uri) is { } d && line >= 0 && line < d.LineCount ? d.GetLineText(line) : null;

    private void OnBreakerHealthChanged(ServerHealth h)
    {
        if (h == ServerHealth.Unresponsive) _log.Add($"[client] server unresponsive after {_breaker.ConsecutiveTimeouts} consecutive request timeouts; probing periodically");
        else _log.Add("[client] server responsive again");
        if (IsOperational) SetState(h == ServerHealth.Unresponsive ? LanguageServerState.Unresponsive : LanguageServerState.Running);
        _observer.OnHealthChanged(this, h);
        if (h == ServerHealth.Unresponsive) _ = ProbeLoopAsync();
    }

    /// <summary>
    /// While Unresponsive, one cheap read-only request per probe interval keeps asking whether the server has recovered, so the state clears by
    /// itself (the breaker lets exactly one request through per interval). Without a suitable read-only capability the next user request is the probe.
    /// </summary>
    private async Task ProbeLoopAsync()
    {
        try
        {
            while (_breaker.Health == ServerHealth.Unresponsive && Volatile.Read(ref _tornDown) == 0)
            {
                await Task.Delay(_ctx.Settings().BreakerProbeInterval + TimeSpan.FromMilliseconds(25), _lifetime.Token).ConfigureAwait(false);
                if (_breaker.Health != ServerHealth.Unresponsive) return;
                var sd = _docs.Values.FirstOrDefault(d => d.OpenOnServer);
                if (sd is not null && _features.EntriesOf(ProviderKind.Hover).Count > 0)
                    await SendAsync(P.LspMethods.TextDocumentHover, new P.HoverParams { TextDocument = new P.TextDocumentIdentifier { Uri = sd.UriString }, Position = new P.Position() },
                        LanePriority.Interactive, _lifetime.Token, sd).ConfigureAwait(false);
                else if (sd is not null && _features.EntriesOf(ProviderKind.DocumentSymbol).Count > 0)
                    await SendAsync(P.LspMethods.TextDocumentDocumentSymbol, new P.DocumentSymbolParams { TextDocument = new P.TextDocumentIdentifier { Uri = sd.UriString } },
                        LanePriority.Interactive, _lifetime.Token, sd).ConfigureAwait(false);
                else if (_features.EntriesOf(ProviderKind.WorkspaceSymbol).Count > 0)
                    await SendAsync(P.LspMethods.WorkspaceSymbol, new P.WorkspaceSymbolParams { Query = "" }, LanePriority.Interactive, _lifetime.Token).ConfigureAwait(false);
                else return;
            }
        }
        catch (OperationCanceledException) { /* session ended */ }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException) { }
    }

    /// <summary>Documents waiting in the diagnostics mailbox and how many were dropped because it was full (diagnostics/tests).</summary>
    internal (int Pending, long Dropped) DiagnosticsBacklog => (_diagMailbox.Count, _diagMailbox.Dropped);

    // =================================================================================================================
    // Lifecycle
    // =================================================================================================================

    public async Task StartAsync(CancellationToken ct)
    {
        SetState(LanguageServerState.Starting);
        var settings = _ctx.Settings();
        try
        {
            var cwd = RootFolder ?? Definition.WorkingDirectory;
            var log = _log; // capture the log only: a callback held by the transport must not keep the whole client alive
            var launch = Definition with { Args = LaunchVariables.Expand(Definition.Args, RootFolder) };
            var connection = await _ctx.Connections.ConnectAsync(
                new LspLaunchRequest(launch, _ctx.Services.Paths?.ToolsDir, Definition.WorkingDirectory ?? cwd, settings.Priority, line => log.Add(line), settings.CpuQuotaPercent,
                    settings.SearchDirs, BuiltinLanguageServers.AlternativesFor(Definition)), ct).ConfigureAwait(false);
            _connection = connection;

            var formatter = new SystemTextJsonFormatter();
            ConfigureFormatter(formatter.JsonSerializerOptions);
            var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(connection.Input, new FramingGuardStream(connection.Output, settings.MaxMessageBytes), formatter));
            rpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed = true;
            rpc.AddLocalRpcTarget(new ServerRequestTarget(this), new JsonRpcTargetOptions { DisposeOnDisconnect = false });
            rpc.Disconnected += OnRpcDisconnected;
            _rpc = rpc;
            rpc.StartListening();
            _ = WatchConnectionAsync(connection);

            using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            initCts.CancelAfter(settings.InitializeTimeout);
            var initParams = BuildInitializeParams(settings);
            JsonElement raw;
            try
            {
                // StreamJsonRpc sends $/cancelRequest on cancellation but only completes the local task once the server answers:
                // WaitAsync is what actually bounds the wait for a server that never does.
                var call = rpc.InvokeWithParameterObjectAsync<JsonElement>(P.LspMethods.Initialize, initParams, initCts.Token);
                ObserveAbandoned(call);
                raw = await call.WaitAsync(initCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                throw new TimeoutException($"initialize timed out after {settings.InitializeTimeout.TotalSeconds:0}s");
            }
            var init = P.LspJson.Deserialize<P.InitializeResult>(raw) ?? throw new InvalidOperationException("empty initialize result");
            ServerCapabilities = init.Capabilities ?? new P.ServerCapabilities();
            ServerName = init.ServerInfo?.Name;
            ServerVersion = init.ServerInfo?.Version;
            var encoding = ServerCapabilities.PositionEncoding?.Value;
            Mapper = new LspMapper(settings.AutoPositionEncoding ? PositionEncoder.FromName(encoding) : PositionEncoder.Utf16, LineSource);
            ParseSyncOptions(ServerCapabilities);

            NotifyRaw(P.LspMethods.Initialized, new P.InitializedParams());
            // Atomic Starting -> Running: if the connection was lost meanwhile (teardown already moved the state on) this session is dead.
            if (Interlocked.CompareExchange(ref _state, (int)LanguageServerState.Running, (int)LanguageServerState.Starting) != (int)LanguageServerState.Starting)
                throw new IOException("the server connection was lost during start-up");

            if (_ctx.Services.Memory is { } mem) _store.Add(mem.Register(TokenCache));
            _features.RegisterStatic(ServerCapabilities);
            SubscribeEnvironment();
            if (Definition.Settings is { } defSettings && defSettings.ValueKind == JsonValueKind.Object) PushConfiguration();
            TrackProcessMemory();

            _docsMayOpen = true;
            foreach (var sd in _docs.Values) PostOpen(sd);
            SchedulePullAll();
            _log.Add($"[client] started {ServerName ?? Definition.DisplayName} {ServerVersion} (pid {ProcessId}, encoding {Mapper.Encoder.Name})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var reason = DescribeFailure(ex);
            LastError = reason;
            _log.Add("[client] start failed: " + reason);
            await TearDownAsync(kill: true).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TearDownAsync(kill: true).ConfigureAwait(false);
            throw;
        }
    }

    private string DescribeFailure(Exception ex)
    {
        var exit = _connection?.ExitDescription;
        return exit is null ? ex.Message : $"{ex.Message} ({exit})";
    }

    private static void ConfigureFormatter(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = null;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.ReadCommentHandling = JsonCommentHandling.Skip;
        o.AllowTrailingCommas = true;
        o.MaxDepth = 256;
        o.Converters.Add(new P.OneOfJsonConverterFactory());
    }

    private P.InitializeParams BuildInitializeParams(LspSettings s)
    {
        var rootPath = RootUri is { IsFile: true } r ? r.LocalPath : null;
        return new P.InitializeParams
        {
            ProcessId = Environment.ProcessId,
            ClientInfo = new P.InitializeParamsClientInfo { Name = "CsCode", Version = typeof(LanguageClient).Assembly.GetName().Version?.ToString() ?? "1.0" },
            Locale = CultureInfo.CurrentUICulture.Name,
            RootPath = rootPath,
            RootUri = RootUri is null ? null : LspMapper.ToUriString(RootUri),
            InitializationOptions = LaunchVariables.Expand(Definition.InitializationOptions, RootFolder),
            Capabilities = ClientCapabilitiesFactory.Create(s.AutoPositionEncoding),
            Trace = s.Trace switch { LspTraceMode.Verbose => P.TraceValues.Verbose, LspTraceMode.Messages => P.TraceValues.Messages, _ => P.TraceValues.Off },
            WorkspaceFolders = WorkspaceFolders.Select(f => new P.WorkspaceFolder { Uri = LspMapper.ToUriString(f), Name = FolderName(f) }).ToArray(),
        };
    }

    /// <summary>The directory the server works in: its root, else the first local workspace folder (also <c>${workspaceFolder}</c> in a definition).</summary>
    private string? RootFolder => RootUri is { IsFile: true } r ? r.LocalPath : WorkspaceFolders.FirstOrDefault(f => f.IsFile)?.LocalPath;

    private static string FolderName(Uri f)
    {
        var path = f.IsFile ? f.LocalPath : f.AbsolutePath;
        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private async Task WatchConnectionAsync(ILspConnection connection)
    {
        await connection.Closed.ConfigureAwait(false);
        HandleConnectionLost(connection.ExitDescription ?? "server process exited");
    }

    private void OnRpcDisconnected(object? sender, JsonRpcDisconnectedEventArgs e)
    {
        var inner = e.Exception?.GetBaseException() ?? e.Exception;
        var text = e.Description ?? "connection closed";
        // keep the underlying cause (e.g. "language server announced a 25,000,000-byte message...") visible in the log and LastError
        if (inner is { } ex && !text.Contains(ex.Message, StringComparison.Ordinal)) text += $" ({ex.Message})";
        HandleConnectionLost(text);
    }

    private void HandleConnectionLost(string reason)
    {
        if (State is LanguageServerState.Stopping or LanguageServerState.Stopped) return;
        if (Interlocked.Exchange(ref _connectionLostHandled, 1) != 0) return;
        var exitText = _connection?.ExitDescription;
        var full = exitText is null || reason.Contains(exitText, StringComparison.Ordinal) ? reason : $"{reason} ({exitText})";
        LastError = full;
        _log.Add("[client] connection lost: " + full);
        // Tear down off the reader thread / event callback.
        _ = Task.Run(async () =>
        {
            var wasStarting = State == LanguageServerState.Starting;
            await TearDownAsync(kill: true).ConfigureAwait(false);
            if (!wasStarting) _observer.OnExited(this, new ClientExit(ClientExitKind.Crashed, full));
        });
    }

    /// <summary>Graceful stop: shutdown request, exit notification, bounded wait, then kill of the whole process tree.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (State is LanguageServerState.Stopped) return;
        var previous = State;
        SetState(LanguageServerState.Stopping);
        var graceful = previous is LanguageServerState.Running or LanguageServerState.Unresponsive && _rpc is not null && _connection is not null && !_connection.Closed.IsCompleted;
        if (graceful)
        {
            var timeout = _ctx.Settings().ShutdownTimeout;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                Task<JsonElement?> shutdown;
                lock (_sendLock) shutdown = _rpc!.InvokeWithParameterObjectAsync<JsonElement?>(P.LspMethods.Shutdown, null, cts.Token); // ordered after everything already sent
                ObserveAbandoned(shutdown);
                await shutdown.WaitAsync(cts.Token).ConfigureAwait(false);
                NotifyRaw(P.LspMethods.Exit, null);
                await _connection!.Closed.WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or RemoteRpcException or ConnectionLostException or ObjectDisposedException or InvalidOperationException)
            {
                _log.Add("[client] graceful shutdown did not complete (" + ex.GetType().Name + "); killing");
            }
        }
        await TearDownAsync(kill: true).ConfigureAwait(false);
    }

    private async Task TearDownAsync(bool kill)
    {
        if (Interlocked.Exchange(ref _tornDown, 1) != 0)
        {
            await _stopped.Task.ConfigureAwait(false);
            return;
        }
        try
        {
            SetState(LanguageServerState.Stopping);
            _docsMayOpen = false;
            _lifetime.Cancel();
            lock (_memoryGate)
            {
                _memoryTracking?.Dispose();
                _memoryTracking = null;
            }
            _features.DisposeAll();
            try { _store.Dispose(); } catch (AggregateException) { /* a subscriber's dispose must not abort teardown */ }
            ClearAllDiagnostics();
            EndAllProgress();
            foreach (var sd in _docs.Values)
            {
                lock (sd.Gate) sd.Detached = true;
                lock (sd.PendingGate) { sd.VersionCts?.Cancel(); sd.VersionCts?.Dispose(); sd.VersionCts = null; sd.Pending = null; }
            }
            _docs.Clear();

            var rpc = _rpc;
            _rpc = null;
            if (rpc is not null)
            {
                rpc.Disconnected -= OnRpcDisconnected;
                try { rpc.Dispose(); } catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException) { }
            }
            var connection = _connection;
            if (connection is not null)
            {
                if (kill) await connection.KillAsync().ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            _breaker.HealthChanged -= OnBreakerHealthChanged;
        }
        finally
        {
            SetState(LanguageServerState.Stopped);
            _stopped.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (State != LanguageServerState.Stopped || Volatile.Read(ref _tornDown) == 0) await StopAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    /// <summary>The restart cap the memory monitor applies to this session's process tree (the definition's, unless the slot changed it).</summary>
    internal long? MemoryCap
    {
        get { lock (_memoryGate) return _memoryCapSet ? _memoryCap : Definition.RestartAboveBytes; }
    }

    /// <summary>
    /// Changes the cap: before the session tracks its process it is simply the one used, afterwards the process is re-tracked with it (null: tracked,
    /// never restarted). The slot calls this when it stops enforcing a cap that keeps firing, and when the setting changes (T39).
    /// </summary>
    internal void SetMemoryCap(long? cap)
    {
        lock (_memoryGate)
        {
            _memoryCap = cap;
            _memoryCapSet = true;
            if (_memoryTracking is null || Volatile.Read(ref _tornDown) != 0) return;
            _memoryTracking.Dispose();
            _memoryTracking = Track(cap);
        }
    }

    private void TrackProcessMemory()
    {
        lock (_memoryGate)
        {
            if (Volatile.Read(ref _tornDown) != 0) return;
            _memoryTracking?.Dispose();
            _memoryTracking = Track(_memoryCapSet ? _memoryCap : Definition.RestartAboveBytes);
        }
    }

    private IDisposable? Track(long? cap)
    {
        if (_ctx.Services.Memory is not { } monitor || ProcessId is not { } pid) return null;
        return monitor.TrackChildProcess($"lsp:{Definition.Id}", pid, cap, cap is { } c
            ? () => { _observer.OnMemoryCapExceeded(this, c); return ValueTask.CompletedTask; }
            : null);
    }

    // =================================================================================================================
    // Requests
    // =================================================================================================================

    private const int ContentModified = -32801;
    private const int ServerCancelled = -32802;
    private const int RequestCancelled = -32800;

    /// <summary>
    /// Sends a request and returns the raw JSON result (null for a JSON null / error / timeout / cancelled-by-server).
    /// Runs on the lane (the caller is a lane work item). Throws OperationCanceledException only when <paramref name="ct"/> is cancelled.
    /// When <paramref name="doc"/> is given, pending changes are flushed to the server first, in order, atomically with the send.
    /// </summary>
    internal async ValueTask<JsonElement?> SendAsync(string method, object? @params, LanePriority priority, CancellationToken ct, SyncedDocument? doc = null, int contentModifiedRetries = 0, TimeSpan? timeout = null, bool essential = false)
    {
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var rpc = _rpc;
            if (rpc is null || !IsOperational) return null;
            if (!_breaker.AllowRequest(essential)) return null;

            var limit = timeout ?? (priority == LanePriority.Interactive ? _ctx.Settings().InteractiveTimeout : _ctx.Settings().BackgroundTimeout);
            using var timeoutCts = new CancellationTokenSource(limit);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token, _lifetime.Token);
            Task<JsonElement> call;
            try
            {
                if (doc is not null)
                {
                    lock (doc.Gate)
                    {
                        if (doc.Detached) return null;
                        EnsureOpenLocked(doc);
                        FlushLocked(doc);
                        lock (_sendLock) call = rpc.InvokeWithParameterObjectAsync<JsonElement>(method, @params, linked.Token);
                    }
                }
                else
                {
                    lock (_sendLock) call = rpc.InvokeWithParameterObjectAsync<JsonElement>(method, @params, linked.Token);
                }
                TraceOut(method, @params);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or ConnectionLostException or InvalidOperationException)
            {
                return null;
            }

            Interlocked.Increment(ref _inFlight);
            ObserveAbandoned(call);
            try
            {
                var result = await call.WaitAsync(linked.Token).ConfigureAwait(false);
                _breaker.RecordSuccess();
                TraceIn(method, result);
                return result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : result;
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                if (timeoutCts.IsCancellationRequested)
                {
                    _breaker.RecordTimeout();
                    NoteTimeout(method, limit);
                }
                return null; // timeout, or the session is ending
            }
            catch (RemoteRpcException ex)
            {
                // RemoteInvocationException (an error response) and RemoteMethodNotFoundException are siblings under RemoteRpcException.
                var code = ex is RemoteInvocationException ri ? ri.ErrorCode : ex is RemoteMethodNotFoundException ? -32601 : 0;
                if (code == ContentModified && attempt < contentModifiedRetries)
                {
                    attempt++;
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct).ConfigureAwait(false);
                    continue; // spec: the server's state moved on; retry against the current document state
                }
                if (code is ContentModified or ServerCancelled or RequestCancelled) return null;
                _breaker.RecordSuccess(); // the server answered (with an error): it is alive
                _log.Add($"[client] {method} failed: {ex.Message} (code {code})");
                return null;
            }
            catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException or IOException or InvalidOperationException)
            {
                return null; // the loss itself is reported by HandleConnectionLost
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>
    /// A call we stop waiting for (timeout / cancellation) may still fault later (server error, connection loss at teardown);
    /// observing it keeps those from surfacing as unobserved task exceptions.
    /// </summary>
    private static void ObserveAbandoned(Task call) =>
        _ = call.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private void NoteTimeout(string method, TimeSpan limit)
    {
        _log.Add($"[client] {method} timed out after {limit.TotalMilliseconds:0} ms");
        var now = Environment.TickCount64;
        var last = Interlocked.Exchange(ref _lastTimeoutNoticeTicks, now);
        if (now - last > 30_000)
            _ctx.Services.Window?.SetStatusBarMessage($"{Definition.DisplayName} is slow to respond", TimeSpan.FromSeconds(5));
    }

    /// <summary>Typed request: send, hop back onto the lane, deserialise. Returns default for "no result".</summary>
    internal async ValueTask<TResult?> RequestAsync<TParams, TResult>(WorkContext ctx, P.LspRequest<TParams, TResult> request, TParams @params, CancellationToken ct, SyncedDocument? doc = null, int contentModifiedRetries = 0, TimeSpan? timeout = null)
        where TParams : class
    {
        var raw = await SendAsync(request.Method, @params, ctx.Priority, ct, doc, contentModifiedRetries, timeout).ConfigureAwait(false);
        if (raw is null) return default;
        await ctx.ResumeOnLane();
        return Deserialize<TResult>(raw.Value, request.Method);
    }

    internal TResult? Deserialize<TResult>(JsonElement raw, string method)
    {
        try { return P.LspJson.Deserialize<TResult>(raw); }
        catch (JsonException ex)
        {
            _log.Add($"[client] could not parse the {method} response: {ex.Message}");
            return default;
        }
    }

    /// <summary>Fire-and-forget notification; the call is initiated (ordered) under the send lock, never blocks on I/O.</summary>
    internal void NotifyRaw(string method, object? @params)
    {
        var rpc = _rpc;
        if (rpc is null) return;
        Task t;
        try
        {
            lock (_sendLock) t = rpc.NotifyWithParameterObjectAsync(method, @params);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ConnectionLostException or InvalidOperationException) { return; }
        TraceOut(method, @params);
        if (!t.IsCompleted) _ = t.ContinueWith(static (x, s) => { _ = x.Exception; }, null, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        else _ = t.Exception;
    }

    internal void Notify<TParams>(P.LspNotification<TParams> notification, TParams @params) where TParams : class => NotifyRaw(notification.Method, @params);

    // ---- protocol tracing (off by default; never allocates when off) ----

    private void TraceOut(string method, object? @params)
    {
        var mode = _ctx.Settings().Trace;
        if (mode == LspTraceMode.Off) return;
        _log.Add(mode == LspTraceMode.Verbose ? $"--> {method} {Truncate(@params is null ? "" : P.LspJson.ToElement(@params).GetRawText())}" : $"--> {method}");
    }

    private void TraceIn(string method, JsonElement result)
    {
        var mode = _ctx.Settings().Trace;
        if (mode == LspTraceMode.Off) return;
        _log.Add(mode == LspTraceMode.Verbose ? $"<-- {method} {Truncate(result.GetRawText())}" : $"<-- {method}");
    }

    private static string Truncate(string s) => s.Length <= 2000 ? s : s[..2000] + "…";
}

/// <summary>Server-originated log lines (stderr, window/logMessage, trace): bounded, drained on the Idle lane, never blocks the reader.</summary>
internal sealed class ServerLog(IOutputChannel? channel, LanguageWorkQueue queue)
{
    private readonly BoundedFifo<string> _lines = new(2048);
    private int _scheduled;
    private long _droppedReported;

    public long Dropped => _lines.Dropped;
    public int Pending => _lines.HasItems ? 1 : 0;

    public void Add(string line)
    {
        if (channel is null) return;
        _lines.Enqueue(line.Length > 4000 ? line[..4000] + "…" : line);
        Schedule();
    }

    private void Schedule()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) != 0) return;
        if (!queue.Post(LanePriority.Idle, Drain)) Volatile.Write(ref _scheduled, 0);
    }

    private void Drain()
    {
        try
        {
            var batch = _lines.Drain(256);
            foreach (var l in batch) channel!.AppendLine(l);
            var dropped = _lines.Dropped;
            if (dropped > _droppedReported && batch.Count > 0)
            {
                channel!.AppendLine($"[client] {dropped - _droppedReported} log lines dropped (server is logging faster than they can be shown)");
                _droppedReported = dropped;
            }
        }
        catch (Exception) { /* a faulty channel must not take the lane down */ }
        finally { Volatile.Write(ref _scheduled, 0); } // single consumer: lines keep their order
        if (_lines.HasItems) Schedule();
    }
}
