using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCode.Core.Debugging;
using CsCode.Core.Diagnostics;
using CsCode.Core.Lifecycle;
using CsCode.Dap.Client;
using CsCode.Dap.Protocol;
using CsCode.Dap.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Dap.Session;

/// <summary>
/// One debug session = one adapter connection. Implements <see cref="IDebugSession"/> over <see cref="DebugAdapterClient"/>.
/// Threading: ALL mutable state is confined to the shared <see cref="DebugLane"/> (single dedicated thread), so there are no locks
/// in here; every public method is a one-hop enqueue onto the lane (callers never wait on adapter I/O, JSON or mapping
/// synchronously) and the volatile properties may be read from any thread. Events fire on the lane — subscribers must be cheap.
/// Protocol order (spec): initialize → launch/attach → (adapter sends `initialized`) → breakpoints → configurationDone → running.
/// </summary>
public sealed partial class DebugSession : IDebugSession, IDebugSessionHealth, IDebugSessionVariablePaging, IBreakpointSyncHost
{
    private readonly DebugSessionOptions _o;
    private readonly DebugSessionSettings _settings;
    private readonly DebugLane _lane;
    private readonly ILogger _logger;
    private readonly DebugAdapterQuirks _quirks;
    private readonly DapPositions _positions;
    private readonly OutputBatcher _output;
    private readonly DebuggeeLauncher _debuggees;
    private readonly VariableHandleCache _handles = new();
    private readonly BoundedLru<(int? Frame, string Text, int Column), IReadOnlyList<DebugCompletionItem>> _completions = new(2_000, v => v.Count);

    private readonly Emitter<DebugStoppedEventArgs> _stopped = new();
    private readonly Emitter<DebugContinuedEventArgs> _continued = new();
    private readonly Emitter<DebugOutputEventArgs> _outputEvent = new();
    private readonly Emitter<DebugThreadEventArgs> _threadChanged = new();
    private readonly Emitter<DebugSessionState> _stateChanged = new();
    private readonly Emitter<DebugTerminatedEventArgs> _terminated = new();
    private readonly Emitter<DebugBreakpointStatus> _breakpointStatus = new();
    private readonly Emitter<IDebugSession> _invalidated = new();
    private readonly Emitter<bool> _unresponsiveChanged = new();
    private readonly Emitter<DebugProgressEventArgs> _progress = new();
    private readonly Emitter<DebugExceptionInfo?> _exceptionInfo = new();
    private readonly Emitter<DebugLoadedSourceEventArgs> _loadedSource = new();
    private readonly Dictionary<string, DebugSource> _loadedSources = new();

    private int _state = (int)DebugSessionState.Initializing;
    private volatile DebugCapabilities _caps = new();
    private DapCapabilities _dapCaps = new();
    private JsonObject _rawCaps = new();
    private int _focusedThread = -1;
    private int _unresponsive;
    private int _disposeState;
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DebugAdapterClient? _client;
    private DisposableStore? _clientSubs;
    private BreakpointSync? _sync;
    private IDisposable? _memoryTrack;
    private int? _exitCode;
    private bool _terminatedFired, _restarting, _stopping, _draining;
    private long _lastOutputTimestamp;
    private TaskCompletionSource _terminatedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastStopTimestamp;
    private string? _lastStopReason;
    private bool _lastStopAllThreads;
    private long _resumeEpoch, _stopCounter;
    private JsonObject? _launchArgs;
    private SyntheticExceptionFilter? _syntheticStop;
    private DebugExceptionInfo? _syntheticException;
    /// <summary>Lane only: our <c>pause</c> request is outstanding (cleared by the next stop or resume); see <see cref="DebugAdapterQuirks.IsPauseStop"/>.</summary>
    private bool _pauseRequested;
    private readonly System.Text.StringBuilder? _recentOutput;
    private readonly Dictionary<string, DebugModuleInfo> _modules = new();

    public DebugSession(DebugSessionOptions options)
    {
        _o = options;
        _settings = options.Settings;
        _lane = options.Lane;
        _logger = options.Logger ?? NullLogger.Instance;
        _quirks = options.Quirks;
        _recentOutput = _quirks.SyntheticExceptionFilters.Count > 0 || _quirks.BreakpointExceptionFilters.Count > 0 ? new System.Text.StringBuilder() : null;
        _positions = new DapPositions(_settings.LinesStartAt1, _settings.ColumnsStartAt1);
        _output = new OutputBatcher(_lane, this, e => _outputEvent.Fire(e), _settings.OutputWindow);
        _debuggees = new DebuggeeLauncher(_lane, _logger, (category, text) => _output.Add(category, text), options.Terminals);
    }

    // ------------------------------------------------------------------ IDebugSession surface (volatile reads, any thread)

    public string Id => _o.Id;
    public string Name => _o.Configuration.Name;
    public DebugConfiguration Configuration => _o.Configuration;
    public DebugSessionState State => (DebugSessionState)Volatile.Read(ref _state);
    public IDebugSession? Parent => _o.Parent;
    public DebugCapabilities Capabilities => _caps;
    public int? FocusedThreadId => Volatile.Read(ref _focusedThread) is var t and >= 0 ? t : null;

    public IEventSource<DebugStoppedEventArgs> Stopped => _stopped;
    public IEventSource<DebugContinuedEventArgs> Continued => _continued;
    public IEventSource<DebugOutputEventArgs> Output => _outputEvent;
    public IEventSource<DebugThreadEventArgs> ThreadChanged => _threadChanged;
    public IEventSource<DebugSessionState> StateChanged => _stateChanged;
    public IEventSource<DebugTerminatedEventArgs> Terminated => _terminated;
    public IEventSource<DebugBreakpointStatus> BreakpointStatusChanged => _breakpointStatus;
    public IEventSource<IDebugSession> Invalidated => _invalidated;

    // ---- richer than the Core interface
    public bool IsAttach => _o.Configuration.IsAttach;
    /// <summary>Completes when <see cref="DisposeAsync"/> has finished releasing the adapter connection, debuggee processes and handlers.</summary>
    public Task Disposed => _disposed.Task;
    public bool IsUnresponsive => Volatile.Read(ref _unresponsive) != 0;
    public IEventSource<bool> UnresponsiveChanged => _unresponsiveChanged;
    public IEventSource<DebugProgressEventArgs> Progress => _progress;
    public IEventSource<DebugExceptionInfo?> ExceptionInfoChanged => _exceptionInfo;
    /// <summary>Adapter <c>loadedSource</c> events (new/changed/removed); the current set is <see cref="LoadedSources"/> (bounded).</summary>
    public IEventSource<DebugLoadedSourceEventArgs> LoadedSourceChanged => _loadedSource;
    public IReadOnlyList<DebugSource> LoadedSources { get { lock (_loadedSources) return [.. _loadedSources.Values]; } }
    public DebugExceptionInfo? LastException { get; private set; }
    public int? ExitCode => _exitCode;
    public long? DebuggeeProcessId { get; private set; }
    public string? DebuggeeProcessName { get; private set; }
    public int? AdapterProcessId => _client?.Transport.ProcessId;
    /// <summary>
    /// How to reach the adapter server this session is connected to (its private socket / named pipe, or a TCP port; null for a stdio adapter);
    /// its child sessions connect there.
    /// </summary>
    public DebugAdapterDescriptor? AdapterEndpoint => _client?.Transport is { } t ? DapTransportFactory.EndpointOf(t) : null;
    /// <summary>
    /// Environment marker carried by an adapter process this session started (an adapter server, or a stdio adapter started from a
    /// <see cref="DebugAdapterStdioProcess"/> such as GDB) and by everything it spawned (null otherwise).
    /// </summary>
    public string? AdapterProcessMarker => _client?.Transport switch
    {
        ServerProcessTransport server => server.Marker,
        ProcessTransport process => process.Marker,
        _ => null,
    };
    /// <summary>The private directory of the socket of an adapter server this session started (null otherwise); deleted with the session.</summary>
    public string? AdapterSocketDirectory => (_client?.Transport as ServerProcessTransport)?.SocketDirectory;

    /// <summary>
    /// Kills the adapter process this session owns (with its tree: the debuggees an adapter server launched) and the debuggees it spawned for
    /// <c>runInTerminal</c>, synchronously and from any thread. For a container disposed synchronously (DebugService.Dispose), where the
    /// graceful async shutdown may never get to run before the process exits.
    /// </summary>
    internal void KillProcessesNow()
    {
        _debuggees.KillAll();
        if (Volatile.Read(ref _client)?.Transport is IKillableTransport transport) transport.KillNow();
    }
    public IReadOnlyList<int> SpawnedDebuggeeProcessIds => _debuggees.LiveProcessIds;
    public IReadOnlyList<DebugModuleInfo> Modules { get { lock (_modules) return [.. _modules.Values]; } }
    public long DroppedOutputChars => _output.DroppedChars;
    public int TrackedVariableHandles => _handles.TrackedHandles;
    public int BreakpointRequestsSent => _sync?.RequestsSent ?? 0;
    public DapPositions Positions => _positions;
    public DapCapabilities RawCapabilities => _dapCaps;

    // ------------------------------------------------------------------ IBreakpointSyncHost

    IDebugSession IBreakpointSyncHost.Session => this;
    string? IBreakpointSyncHost.AdapterPathOf(Uri uri) => uri.IsFile ? uri.LocalPath : _o.PathMapper?.ToAdapterPath(uri);
    DapPositions IBreakpointSyncHost.Positions => _positions;
    DapCapabilities IBreakpointSyncHost.Capabilities => _dapCaps;
    bool IBreakpointSyncHost.IsLive => _client is { IsClosed: false } && State != DebugSessionState.Terminated;
    DebugAdapterQuirks IBreakpointSyncHost.Quirks => _quirks;
    Task<JsonElement> IBreakpointSyncHost.RequestAsync(string command, object? arguments, CancellationToken ct) => RequestAsync(command, arguments, ct);
    void IBreakpointSyncHost.BreakpointStatusChanged(DebugBreakpointStatus status) => _breakpointStatus.Fire(status);

    // ------------------------------------------------------------------ start

    /// <summary>
    /// Connects and runs the handshake; returns when the adapter has accepted launch/attach and configurationDone (the debuggee is
    /// running, or already stopped at a breakpoint/entry). Throws <see cref="DebugSessionStartException"/> on failure, after which
    /// the session is Terminated and its resources are released.
    /// </summary>
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        try
        {
            await _lane.RunAsync(async () =>
            {
                await ConnectAndHandshakeAsync(ct);
                return 0;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            await FailStartAsync("cancelled");
            throw;
        }
        catch (Exception ex)
        {
            var message = ex is DebugSessionStartException ? ex.Message : $"Could not start debug session '{Name}': {ex.Message}";
            await FailStartAsync(message);
            throw ex is DebugSessionStartException ? ex : new DebugSessionStartException(message, ex);
        }
    }

    private Task FailStartAsync(string reason) =>
        _lane.RunAsync(async () =>
        {
            FinishTermination(reason);
            await CleanupAsync(quick: true);
            return 0;
        });

    private async Task ConnectAndHandshakeAsync(CancellationToken ct)
    {
        var transport = await _o.TransportFactory(ct);
        AttachClient(transport);
        await HandshakeAsync(ct);
    }

    private void AttachClient(IDapTransport transport)
    {
        var client = new DebugAdapterClient(transport, _lane, new DebugAdapterClientOptions
        {
            RequestTimeout = _settings.RequestTimeout,
            MaxFrameBytes = _settings.MaxFrameBytes,
        }, _logger);
        var subs = new DisposableStore();
        subs.Add(client.Stopped.Subscribe(OnStopped));
        subs.Add(client.Continued.Subscribe(OnContinued));
        subs.Add(client.Exited.Subscribe(OnExited));
        subs.Add(client.Terminated.Subscribe(OnTerminatedEvent));
        subs.Add(client.Thread.Subscribe(OnThread));
        subs.Add(client.Output.Subscribe(OnOutput));
        subs.Add(client.Breakpoint.Subscribe(e => { MarkAlive(); _sync?.OnAdapterBreakpointEvent(e); }));
        subs.Add(client.Module.Subscribe(OnModule));
        subs.Add(client.LoadedSource.Subscribe(OnLoadedSource));
        subs.Add(client.Process.Subscribe(OnProcess));
        subs.Add(client.CapabilitiesChanged.Subscribe(OnCapabilitiesEvent));
        subs.Add(client.ProgressStart.Subscribe(e => _progress.Fire(new DebugProgressEventArgs("start", e.ProgressId, e.Title, e.Message, e.Percentage))));
        subs.Add(client.ProgressUpdate.Subscribe(e => _progress.Fire(new DebugProgressEventArgs("update", e.ProgressId, null, e.Message, e.Percentage))));
        subs.Add(client.ProgressEnd.Subscribe(e => _progress.Fire(new DebugProgressEventArgs("end", e.ProgressId, null, e.Message, null))));
        subs.Add(client.Invalidated.Subscribe(OnInvalidatedEvent));
        subs.Add(client.RequestTimedOut.Subscribe(_ => SetUnresponsive(true)));
        subs.Add(client.ConnectionClosed.Subscribe(OnConnectionClosed));
        client.SetReverseRequestHandler("runInTerminal", OnRunInTerminalAsync);
        if (_o.StartChild is not null) client.SetReverseRequestHandler("startDebugging", OnStartDebuggingAsync);
        _client = client;
        _clientSubs = subs;
        if (_o.Memory is { } memory && transport.ProcessId is { } pid)
            _memoryTrack = memory.TrackChildProcess($"debug adapter ({_o.Configuration.Type})", pid);
        client.Start();
    }

    private DapInitializeArguments BuildInitializeArguments() => new()
    {
        ClientId = _settings.ClientId,
        ClientName = _settings.ClientName,
        AdapterId = _o.Configuration.Type,
        Locale = CultureInfo.CurrentUICulture.Name.ToLowerInvariant() is { Length: > 0 } l ? l : "en-us",
        LinesStartAt1 = _positions.LinesStartAt1,
        ColumnsStartAt1 = _positions.ColumnsStartAt1,
        PathFormat = "path",
        // Only what is really implemented below (honest capability advertisement):
        SupportsVariableType = true,
        SupportsVariablePaging = true,
        SupportsRunInTerminalRequest = true,
        SupportsMemoryReferences = true,
        SupportsProgressReporting = true,
        SupportsInvalidatedEvent = true,
        SupportsStartDebuggingRequest = _o.StartChild is not null,
        SupportsMemoryEvent = false,
        SupportsArgsCanBeInterpretedByShell = false,
    };

    /// <summary>The WHOLE resolved launch/attach object goes to the adapter untouched (+ noDebug), so adapter-specific keys just work.</summary>
    private JsonObject BuildLaunchArguments(JsonObject? restartData = null)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in _o.Configuration.Properties)
            obj[key] = JsonNode.Parse(value.GetRawText());
        if (_o.Configuration.NoDebug) obj["noDebug"] = true;
        if (restartData is not null) obj["__restart"] = restartData.DeepClone();
        return obj;
    }

    private async Task HandshakeAsync(CancellationToken ct, JsonObject? restartData = null)
    {
        var client = _client ?? throw new InvalidOperationException("No adapter connection.");
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var initializedSub = client.Initialized.Subscribe(_ => initialized.TrySetResult());

        var body = await RequestAsync("initialize", BuildInitializeArguments(), ct, _settings.RequestTimeout);
        ApplyCapabilities(DapJson.Deserialize<DapCapabilities>(body) ?? new DapCapabilities(), merge: false);

        _launchArgs = BuildLaunchArguments(restartData);
        // launch/attach is NOT awaited yet: many adapters only answer it after configurationDone.
        var launch = RequestAsync(IsAttach ? "attach" : "launch", _launchArgs, ct, _settings.LaunchTimeout);
        Observe(launch);

        Task first;
        try { first = await Task.WhenAny(initialized.Task, launch, client.Closed).WaitAsync(_settings.InitializedTimeout, ct); }
        catch (TimeoutException)
        {
            throw new DebugSessionStartException($"The debug adapter did not send 'initialized' within {_settings.InitializedTimeout.TotalSeconds:0.#}s.");
        }
        if (first == launch) await launch; // launch rejected → surface its error now
        if (first == client.Closed || client.IsClosed) throw new DebugSessionStartException(client.CloseReason ?? "The debug adapter closed the connection during startup.");
        if (!initialized.Task.IsCompleted)
        {
            // launch already succeeded but `initialized` hasn't arrived: give a (spec-violating) adapter a short grace, then continue.
            await Task.WhenAny(initialized.Task, Task.Delay(TimeSpan.FromSeconds(2), ct));
        }

        _sync = new BreakpointSync(this, _o.Breakpoints, _lane, _logger, _settings.BreakpointDebounce);
        MergeExceptionFilters();
        // Start Without Debugging on an adapter that debugs anyway (GDB): no breakpoints, no exception filters — nothing may stop the program.
        // One that refuses them then (CodeLLDB) gets none either, so they are not all shown as failed.
        if (!(_o.Configuration.NoDebug && (_quirks.IgnoresNoDebug || _quirks.RefusesBreakpointsWithoutDebugging))) await _sync.SendAllAsync(ct);

        if (_dapCaps.SupportsConfigurationDoneRequest == true)
            await RequestAsync("configurationDone", null, ct, _settings.LaunchTimeout);

        await launch;
        if (State == DebugSessionState.Initializing) SetState(DebugSessionState.Running);
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    // ------------------------------------------------------------------ capabilities

    private void ApplyCapabilities(DapCapabilities fresh, bool merge)
    {
        if (merge)
        {
            // `capabilities` events carry only the CHANGED fields: overlay onto what we know.
            var overlay = DapJson.ToElement(fresh);
            foreach (var p in overlay.EnumerateObject()) _rawCaps[p.Name] = JsonNode.Parse(p.Value.GetRawText());
            _dapCaps = JsonSerializer.Deserialize<DapCapabilities>(_rawCaps, DapJson.Options) ?? fresh;
        }
        else
        {
            _dapCaps = fresh;
            _rawCaps = JsonSerializer.SerializeToNode(fresh, DapJson.Options) as JsonObject ?? new JsonObject();
        }
        _caps = DapMapping.ToCore(_dapCaps);
        if (_quirks.SyntheticExceptionFilters is { Count: > 0 } synthetic)
        {
            // Client-side exception filters (GDB: "Rust panics") are offered next to the adapter's own, and their stops are described by the
            // session itself (exceptionInfo), which the Exception Helper asks for.
            _caps = _caps with
            {
                SupportsExceptionInfo = true,
                ExceptionBreakpointFilters = [.. synthetic.Select(f => new ExceptionBreakpointFilter(f.Filter, f.Label, f.Default, false, f.Description)), .. _caps.ExceptionBreakpointFilters ?? []],
            };
        }
        if (_client is not null) _client.SendCancelRequests = _dapCaps.SupportsCancelRequest == true;
    }

    /// <summary>
    /// The adapter's exceptionBreakpointFilters (plus the client-side ones of its quirks, first) become THE filter list (enabled state of known
    /// ids is kept; new ones use their default).
    /// </summary>
    private void MergeExceptionFilters()
    {
        var advertised = _caps.ExceptionBreakpointFilters;
        if (advertised is null || advertised.Count == 0) return;
        var existing = _o.Breakpoints.ExceptionFilters.ToDictionary(f => f.Filter);
        _o.Breakpoints.SetExceptionFilters([.. advertised.Select(a =>
            existing.TryGetValue(a.Filter, out var e)
                ? new ExceptionFilterSetting(a.Filter, a.Label, e.Enabled, e.Condition)
                : new ExceptionFilterSetting(a.Filter, a.Label, a.Default))]);
    }

    // ------------------------------------------------------------------ state machine

    private void SetState(DebugSessionState next)
    {
        var current = State;
        if (current == next || current == DebugSessionState.Terminated) return;
        Volatile.Write(ref _state, (int)next);
        _stateChanged.Fire(next);
    }

    private void SetUnresponsive(bool value)
    {
        if (Interlocked.Exchange(ref _unresponsive, value ? 1 : 0) != (value ? 1 : 0)) _unresponsiveChanged.Fire(value);
    }

    /// <summary>Anything arriving from the adapter proves it is alive.</summary>
    private void MarkAlive()
    {
        if (Volatile.Read(ref _unresponsive) != 0) SetUnresponsive(false);
    }

    private async Task<JsonElement> RequestAsync(string command, object? args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var client = _client ?? throw new InvalidOperationException("The debug session has no adapter connection.");
        try
        {
            var result = await client.SendRequestAsync(command, args, ct, timeout);
            MarkAlive();
            return result;
        }
        catch (DapRequestTimeoutException)
        {
            SetUnresponsive(true);
            throw;
        }
    }

    // ------------------------------------------------------------------ termination

    /// <summary>Idempotent. Delivers pending output first so it is ordered before the end of the session.</summary>
    private void FinishTermination(string? reason)
    {
        if (_terminatedFired) return;
        _terminatedFired = true;
        _output.Flush();
        _handles.Invalidate();
        _completions.Clear();
        Volatile.Write(ref _focusedThread, -1);
        SetState(DebugSessionState.Terminated);
        _terminatedSignal.TrySetResult();
        _terminated.Fire(new DebugTerminatedEventArgs(this, _exitCode, reason));
    }

    private void OnConnectionClosed(string reason)
    {
        if (_restarting || _terminatedFired || Volatile.Read(ref _disposeState) != 0) return;
        _logger.LogInformation("Debug adapter of session {Session} went away: {Reason}", Id, reason);
        FinishTermination(_stopping ? "stopped" : reason);
        _ = CleanupAsync(quick: true);
    }

    private void OnTerminatedEvent(DapTerminatedEvent e)
    {
        MarkAlive();
        if (_restarting) return;
        if (e.Restart is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False) } restart && !_stopping)
        {
            JsonObject? data = restart.ValueKind == JsonValueKind.Object ? JsonNode.Parse(restart.GetRawText()) as JsonObject : null;
            _ = RelaunchAsync(data);
            return;
        }
        if (_quirks.TrailingOutputQuietPeriod > TimeSpan.Zero && !_stopping && !_terminatedFired)
        {
            _ = FinishAfterTrailingOutputAsync();
            return;
        }
        FinishTermination(null);
        _ = CleanupAsync(quick: false); // spec: after `terminated` the client sends `disconnect`
    }

    /// <summary>
    /// GDB reads the program's output on a thread of its own: the last lines of a program that printed a lot can arrive after `terminated`.
    /// The session ends once the output went quiet for <see cref="DebugAdapterQuirks.TrailingOutputQuietPeriod"/> (bounded by
    /// <see cref="DebugAdapterQuirks.TrailingOutputLimit"/>), so those lines still reach the Debug Console. Runs on the lane.
    /// </summary>
    private async Task FinishAfterTrailingOutputAsync()
    {
        if (_draining) return;
        _draining = true;
        try
        {
            var quiet = _quirks.TrailingOutputQuietPeriod;
            var terminatedAt = Stopwatch.GetTimestamp();
            var deadline = terminatedAt + (long)(_quirks.TrailingOutputLimit.TotalSeconds * Stopwatch.Frequency);
            while (!_terminatedFired && Stopwatch.GetTimestamp() < deadline)
            {
                // quiet since the later of the last output and `terminated` (a program's only line may still be on its way)
                var since = Stopwatch.GetElapsedTime(Math.Max(Volatile.Read(ref _lastOutputTimestamp), terminatedAt));
                if (since >= quiet) break;
                await Task.Delay(quiet - since);
            }
        }
        finally { _draining = false; }
        if (_terminatedFired) return; // the connection closed or the user stopped meanwhile
        FinishTermination(null);
        await CleanupAsync(quick: false);
    }

    /// <summary>Graceful stop: terminate (if the adapter supports it) else disconnect{terminateDebuggee} (launch) / detach (attach); hard kill after the timeout.</summary>
    public ValueTask StopAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return new ValueTask(_lane.RunAsync(() => StopCoreAsync(quick: false, ct), CancellationToken.None));
    }

    private async Task StopCoreAsync(bool quick, CancellationToken ct)
    {
        // The program already ended (the session only waits for its trailing output): nothing to terminate.
        if (_draining && !_terminatedFired) { FinishTermination(null); await CleanupAsync(quick); return; }
        if (!_terminatedFired && _client is { IsClosed: false })
        {
            _stopping = true;
            var requestTimeout = quick ? TimeSpan.FromSeconds(1) : _settings.RequestTimeout;
            var waitTimeout = quick ? TimeSpan.FromSeconds(1) : _settings.StopTimeout;
            try
            {
                if (_dapCaps.SupportsTerminateRequest == true && !IsAttach)
                {
                    try { await RequestAsync("terminate", new DapTerminateArguments { Restart = false }, ct, requestTimeout); }
                    catch (Exception ex) when (ex is DapRequestException or DapRequestTimeoutException or DapTransportClosedException) { _logger.LogDebug(ex, "terminate failed"); }
                    await WaitSignalAsync(waitTimeout, ct);
                }
                if (!_terminatedFired && _client is { IsClosed: false })
                {
                    try
                    {
                        await RequestAsync("disconnect", new DapDisconnectArguments { TerminateDebuggee = !IsAttach, Restart = false }, ct, requestTimeout);
                    }
                    catch (Exception ex) when (ex is DapRequestException or DapRequestTimeoutException or DapTransportClosedException) { _logger.LogDebug(ex, "disconnect failed"); }
                    await WaitSignalAsync(quick ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(2), ct);
                }
            }
            catch (OperationCanceledException) { /* caller gave up: fall through to the hard path */ }
        }
        FinishTermination("stopped"); // hard path: adapter ignored us / timed out
        await CleanupAsync(quick);
    }

    private async Task WaitSignalAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await _terminatedSignal.Task.WaitAsync(timeout, ct); }
        catch (TimeoutException) { }
    }

    /// <summary>Releases everything tied to the CURRENT connection: breakpoint mirror, debuggee processes, adapter process.</summary>
    private async Task CleanupAsync(bool quick)
    {
        var sync = Interlocked.Exchange(ref _sync, null);
        sync?.Dispose(); // also clears this session's breakpoint statuses in the manager
        _debuggees.KillAll();
        var subs = Interlocked.Exchange(ref _clientSubs, null);
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            if (!quick && !client.IsClosed)
            {
                // Best-effort `disconnect` after `terminated`: lets a well-behaved adapter exit by itself.
                try { await client.SendRequestAsync("disconnect", new DapDisconnectArguments { TerminateDebuggee = !IsAttach }, CancellationToken.None, TimeSpan.FromSeconds(1)); }
                catch (Exception ex) when (ex is DapRequestException or DapRequestTimeoutException or DapTransportClosedException or OperationCanceledException) { }
            }
            await client.DisposeAsync(); // closes stdin, waits briefly, then kills the adapter's process tree
        }
        subs?.Dispose();
        Interlocked.Exchange(ref _memoryTrack, null)?.Dispose();
    }

    // ------------------------------------------------------------------ restart

    public ValueTask RestartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return new ValueTask(_lane.RunAsync(async () =>
        {
            if (_terminatedFired) throw new InvalidOperationException("The debug session has ended.");
            if (_dapCaps.SupportsRestartRequest == true)
            {
                _handles.Invalidate();
                _completions.Clear();
                await RequestAsync("restart", new DapRestartArguments { Arguments = _launchArgs is null ? null : JsonSerializer.SerializeToElement(_launchArgs) }, ct, _settings.LaunchTimeout);
                Volatile.Write(ref _focusedThread, -1);
                SetState(DebugSessionState.Running);
                return;
            }
            await RelaunchCoreAsync(null, ct);
        }, ct));
    }

    private Task RelaunchAsync(JsonObject? restartData) => _lane.RunAsync(() => RelaunchCoreAsync(restartData, CancellationToken.None));

    /// <summary>Adapters without a `restart` request: stop the old debuggee+adapter silently and run the whole handshake again with the SAME configuration.</summary>
    private async Task RelaunchCoreAsync(JsonObject? restartData, CancellationToken ct)
    {
        _restarting = true;
        try
        {
            // Detach from the old connection first so its shutdown is not mistaken for the end of the session.
            var oldClient = _client;
            if (oldClient is { IsClosed: false })
            {
                try
                {
                    if (_dapCaps.SupportsTerminateRequest == true && !IsAttach)
                        await RequestAsync("terminate", new DapTerminateArguments(), ct, TimeSpan.FromSeconds(3));
                    else await RequestAsync("disconnect", new DapDisconnectArguments { TerminateDebuggee = !IsAttach }, ct, TimeSpan.FromSeconds(3));
                }
                catch (Exception ex) when (ex is DapRequestException or DapRequestTimeoutException or DapTransportClosedException) { }
            }
            await CleanupAsync(quick: true);

            _terminatedFired = false;
            _terminatedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _exitCode = null;
            _handles.Invalidate();
            _completions.Clear();
            Volatile.Write(ref _focusedThread, -1);
            Volatile.Write(ref _state, (int)DebugSessionState.Initializing);
            _stateChanged.Fire(DebugSessionState.Initializing);
            _lastStopTimestamp = 0;

            var transport = await _o.TransportFactory(ct);
            AttachClient(transport);
            await HandshakeAsync(ct, restartData);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restart of session {Session} failed", Id);
            _restarting = false;
            FinishTermination($"restart failed: {ex.Message}");
            await CleanupAsync(quick: true);
            throw;
        }
        finally { _restarting = false; }
    }

    // ------------------------------------------------------------------ disposal

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        try
        {
            await _lane.RunAsync(async () =>
            {
                await StopCoreAsync(quick: true, CancellationToken.None);
                _output.Dispose();
                _handles.Invalidate();
                _completions.Clear();
                lock (_modules) _modules.Clear();
                lock (_loadedSources) _loadedSources.Clear();
                _debuggees.Dispose();
                return 0;
            });
        }
        catch (ObjectDisposedException) { /* lane already gone (app shutdown): nothing left to release but emitters */ }
        _o.Breakpoints.ClearStatuses(Id);
        _stopped.Clear(); _continued.Clear(); _outputEvent.Clear(); _threadChanged.Clear(); _stateChanged.Clear(); _terminated.Clear();
        _breakpointStatus.Clear(); _invalidated.Clear(); _unresponsiveChanged.Clear(); _progress.Clear(); _exceptionInfo.Clear(); _loadedSource.Clear();
        _disposed.TrySetResult();
    }
}
