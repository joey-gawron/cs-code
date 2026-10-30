using System.Text.Json.Nodes;
using CsCode.Core.Contributions;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Dap.Adapters;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Breakpoints;
using CsCode.Dap.Session;
using CsCode.Dap.Transport;
using Microsoft.Extensions.Logging;

namespace CsCode.Dap.Service;

/// <summary>
/// Everything the debug UI binds to. Sessions, stop location, state and the Debug Console history are mutated on the Debug lane
/// (session events arrive there) and published as immutable snapshots, so any thread — in particular the UI thread — can read them
/// without locks or waiting. <see cref="StartAsync"/> and friends only enqueue. Events fire on the lane: subscribers must be cheap and
/// marshal themselves (the shell's UiDispatcher does).
/// </summary>
public sealed partial class DebugService : IDebugService, IDebugFrameFocus, IDebugOutputHistory, IDebugSourceContentProvider, IDebugKnownCapabilities, IAsyncDisposable, IDisposable
{
    public const int MaxHistoryLines = 5_000;
    private const int MaxHistoryChars = 4 * 1024 * 1024;

    private readonly DebugLane _lane;
    private readonly DapPlatform _p;
    private readonly BreakpointManager _breakpoints;
    private readonly DebugConfigurationService _configurations;
    private readonly ITaskService _tasks;
    private readonly ILogger _logger;
    private readonly DisposableStore _subs = new();
    private readonly object _gate = new();

    private readonly Dictionary<string, List<IDebugAdapterFactory>> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IDebugConfigurationProvider>> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BuiltinDebugAdapter> _builtins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IDebugSession, SessionHooks> _hooks = new();
    private readonly Dictionary<IDebugSession, CompoundGroup> _compounds = new();
    private readonly Dictionary<string, DebugCapabilities> _knownCapabilities = new(StringComparer.OrdinalIgnoreCase);

    private readonly Emitter<IDebugSession> _sessionStarted = new(), _sessionTerminated = new();
    private readonly Emitter<IDebugSession?> _activeChanged = new();
    private readonly Emitter<DebugState> _stateChanged = new();
    private readonly Emitter<DebugStopLocation?> _stopLocationChanged = new();
    private readonly Emitter<DebugOutputEventArgs> _output = new();
    private readonly Emitter<object?> _outputCleared = new();

    private IDebugSession[] _sessions = [];
    // Sessions that ended and are still being released (adapter killed, private socket deleted): DisposeAsync waits for them, so an app that
    // exits right after its shutdown leaves no adapter socket behind. Guarded by _gate; each removes itself when done.
    private readonly List<Task> _endingSessions = new();
    private IDebugSession? _active;
    private int _state = (int)DebugState.Inactive;
    private DebugStopLocation? _stopLocation;
    private int _focusedFrameId = -1;
    private WatchExpression[] _watches = [];
    private DebuggerType[] _debuggerTypes = [];
    private long _stopEpoch;
    private int _sessionCounter;
    private int _disposed;

    private readonly OutputHistory _history = new(MaxHistoryLines, MaxHistoryChars);

    private sealed record SessionHooks(IDisposable Subscriptions, string? PostDebugTask, Uri? Folder);
    private sealed class CompoundGroup(bool stopAll) { public bool StopAll { get; } = stopAll; public List<IDebugSession> Members { get; } = new(); }

    public DebugService(DebugLane lane, DapPlatform platform, BreakpointManager breakpoints, DebugConfigurationService configurations, ITaskService tasks, IEnumerable<BuiltinDebugAdapter>? builtins = null)
    {
        _lane = lane;
        _p = platform;
        _breakpoints = breakpoints;
        _configurations = configurations;
        _tasks = tasks;
        _logger = platform.Logger("CsCode.Dap.Service");

        foreach (var b in builtins ?? [])
        {
            _builtins[b.Type] = b;
            Add(_factories, b.Type, b.Factory);
            if (b.Provider is not null)
            {
                Add(_providers, b.Type, b.Provider);
                _subs.Add(_configurations.AddProvider(b.Type, b.Provider));
            }
        }
        RebuildDebuggerTypes();
        InitializeContributions();
        _subs.Add(breakpoints.Changed.Subscribe(_ => UpdateContextKeys()));
        RegisterCommands();
        UpdateContextKeys();
    }

    // ------------------------------------------------------------------ snapshots (any thread)

    public IReadOnlyList<IDebugSession> Sessions => Volatile.Read(ref _sessions);
    public DebugState State => (DebugState)Volatile.Read(ref _state);
    public DebugStopLocation? StopLocation => Volatile.Read(ref _stopLocation);
    public IBreakpointManager Breakpoints => _breakpoints;
    public IDebugConfigurationService Configurations => _configurations;
    public IReadOnlyList<DebuggerType> DebuggerTypes => Volatile.Read(ref _debuggerTypes);
    public IReadOnlyList<WatchExpression> Watches => Volatile.Read(ref _watches);
    public int? FocusedFrameId => Volatile.Read(ref _focusedFrameId) is var f && f >= 0 ? f : null;

    public IEventSource<IDebugSession> SessionStarted => _sessionStarted;
    public IEventSource<IDebugSession> SessionTerminated => _sessionTerminated;
    public IEventSource<IDebugSession?> ActiveSessionChanged => _activeChanged;
    public IEventSource<DebugState> StateChanged => _stateChanged;
    public IEventSource<DebugStopLocation?> StopLocationChanged => _stopLocationChanged;
    public IEventSource<DebugOutputEventArgs> Output => _output;
    public IEventSource<object?> OutputCleared => _outputCleared;

    public IDebugSession? ActiveSession
    {
        get { lock (_gate) return _active; }
        set
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, value)) return;
                if (value is not null && !_sessions.Contains(value)) return; // only live sessions can be focused
                _active = value;
            }
            _activeChanged.Fire(value);
            Volatile.Write(ref _focusedFrameId, -1);
            RecomputeState();
            UpdateContextKeys();
            if (value is DebugSession { State: DebugSessionState.Stopped } s) _lane.Post(() => _ = RefreshStopLocationAsync(s, s.FocusedThreadId, "focus"));
            else SetStopLocation(null);
        }
    }

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T item)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new();
        list.Add(item);
    }

    // ------------------------------------------------------------------ registration

    public IDisposable RegisterAdapterFactory(string debugType, IDebugAdapterFactory factory)
    {
        lock (_gate) Add(_factories, debugType, factory);
        _lane.Post(() => { RebuildDebuggerTypes(); UpdateContextKeys(); });
        return Disposable.Create(() =>
        {
            lock (_gate) { if (_factories.TryGetValue(debugType, out var l)) l.Remove(factory); }
            _lane.Post(() => { RebuildDebuggerTypes(); UpdateContextKeys(); });
        });
    }

    public IDisposable RegisterConfigurationProvider(string debugType, IDebugConfigurationProvider provider)
    {
        lock (_gate) Add(_providers, debugType, provider);
        var forwarded = _configurations.AddProvider(debugType, provider);
        return Disposable.Create(() =>
        {
            lock (_gate) { if (_providers.TryGetValue(debugType, out var l)) l.Remove(provider); }
            forwarded.Dispose();
        });
    }

    // ------------------------------------------------------------------ starting

    public ValueTask<IDebugSession?> StartAsync(DebugConfiguration configuration, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new ValueTask<IDebugSession?>(_lane.RunAsync(() => StartCoreAsync(configuration, null, ct), ct));
    }

    private async Task<IDebugSession?> StartCoreAsync(DebugConfiguration configuration, DebugSession? parent, CancellationToken ct, CompoundGroup? group = null)
    {
        if (string.Equals(configuration.Type, LaunchJson.CompoundType, StringComparison.Ordinal))
            return await StartCompoundAsync(configuration, ct);

        // 1. ${variables}
        DebugConfiguration? resolved;
        try { resolved = await _configurations.ResolveVariablesAsync(configuration, ct); }
        catch (VariableResolutionException ex) { await ReportAsync($"Cannot start '{configuration.Name}': {ex.Message}"); return null; }
        catch (OperationCanceledException) { return null; } // an ${input:…} prompt was dismissed

        // 2. configuration providers (fill in / validate; null aborts)
        var folder = resolved.WorkspaceFolder ?? _p.PrimaryFolder;
        IDebugConfigurationProvider[] providers;
        lock (_gate) providers = _providers.TryGetValue(resolved.Type, out var pl) ? [.. pl] : [];
        foreach (var provider in providers)
        {
            resolved = await provider.ResolveAsync(folder, resolved, ct);
            if (resolved is null) return null;
        }

        // 3. preLaunchTask
        if (resolved.PreLaunchTask is { Length: > 0 } pre && !await RunTaskGateAsync(pre, "preLaunchTask", folder, ct)) return null;

        // 4. adapter. A child of a session connected to an adapter SERVER opens its own connection to that same server: js-debug runs the
        // launched program as a child session (`startDebugging`) on the server the parent started, and only that server knows the pending target.
        // A child of ANOTHER debug type (without js-debug's pending target) belongs to that type's adapter: its factory creates it.
        DebugAdapterDescriptor? descriptor;
        if (parent?.AdapterEndpoint is { } endpoint && SharesParentAdapter(parent, resolved)) descriptor = endpoint;
        else
        {
            try { descriptor = await CreateDescriptorAsync(resolved, ct); }
            catch (DebugSessionStartException ex) { await ReportAsync(ex.Message); return null; }
        }
        if (descriptor is null)
        {
            await ReportAsync($"No debug adapter is available for type '{resolved.Type}'. Install one and register it in the 'cscode.debuggers' setting.");
            return null;
        }

        // 5. session
        var id = "s" + Interlocked.Increment(ref _sessionCounter);
        var final = resolved;
        var session = new DebugSession(new DebugSessionOptions(id, final,
            tct => DapTransportFactory.OpenAsync(descriptor, _logger, TimeSpan.FromSeconds(15), tct),
            _breakpoints, _lane)
        {
            Parent = parent,
            Logger = _p.Logger("CsCode.Dap.Session"),
            Memory = _p.Memory,
            Terminals = _p.Terminals,
            Settings = SettingsFor(final),
            StartChild = StartChildAsync,
            Quirks = _builtins.GetValueOrDefault(final.Type)?.Quirks ?? DebugAdapterQuirks.None,
            PathMapper = _p.PathMapper,
        });
        Attach(session, final.PostDebugTask, folder, group, configuration.Type);
        try
        {
            await session.StartAsync(ct);
        }
        catch (DebugSessionStartException ex)
        {
            await ReportAsync(ex.Message);
            return null;
        }
        catch (OperationCanceledException) { return null; }
        return session;
    }

    /// <summary>
    /// Whether a child session goes to its parent's adapter server: when it asks for a target only that server holds (js-debug's
    /// <c>__pendingTargetId</c>), or is of the parent's own debug type.
    /// </summary>
    private static bool SharesParentAdapter(DebugSession parent, DebugConfiguration child) =>
        child.Properties.ContainsKey(JsDebugAdapterFactory.PendingTargetKey)
        || string.Equals(child.Type, parent.Configuration.Type, StringComparison.OrdinalIgnoreCase);

    private DebugSessionSettings SettingsFor(DebugConfiguration config)
    {
        var timeout = _p.Configuration?.Get<double?>("cscode.debug.requestTimeoutSeconds");
        return timeout is > 0 ? new DebugSessionSettings { RequestTimeout = TimeSpan.FromSeconds(timeout.Value) } : new DebugSessionSettings();
    }

    private async Task<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration config, CancellationToken ct)
    {
        // VS Code's dev convenience: a running adapter server given in the configuration itself.
        // (`"debugServer": 4711`, or `"host:4711"` for a remote/attach-style adapter server.)
        if (config.Properties.TryGetValue("debugServer", out var ds))
        {
            if (ds.ValueKind == System.Text.Json.JsonValueKind.Number && ds.TryGetInt32(out var port)) return new DebugAdapterServer("127.0.0.1", port);
            if (ds.ValueKind == System.Text.Json.JsonValueKind.String && ds.GetString() is { } text)
            {
                var colon = text.LastIndexOf(':');
                if (colon > 0 && int.TryParse(text[(colon + 1)..], out var hostPort)) return new DebugAdapterServer(text[..colon], hostPort);
                if (int.TryParse(text, out var onlyPort)) return new DebugAdapterServer("127.0.0.1", onlyPort);
            }
        }

        IDebugAdapterFactory[] factories;
        lock (_gate) factories = _factories.TryGetValue(config.Type, out var l) ? [.. l.AsEnumerable().Reverse()] : []; // latest registration wins
        foreach (var f in factories)
            if (await f.CreateDescriptorAsync(config, ct) is { } d) return d;
        return await ContributedFactory.CreateDescriptorAsync(config, ct);
    }

    private async Task<bool> RunTaskGateAsync(string task, string kind, Uri? folder, CancellationToken ct)
    {
        var result = await _tasks.RunAsync(task, folder, ct);
        if (result.Succeeded) return true;
        if (result.Cancelled) return false;
        var why = result.Error ?? $"terminated with exit code {result.ExitCode}";
        if (_p.Window is not { } window) return false;
        var choice = await window.ShowMessageAsync(MessageSeverity.Error, $"The {kind} '{task}' {(result.Error is null ? why : "failed: " + why)}.",
            [new MessageAction("Debug Anyway"), new MessageAction("Abort", IsCloseAffordance: true)], ct);
        return choice == "Debug Anyway";
    }

    private async Task<IDebugSession?> StartChildAsync(DebugSession parent, string request, JsonObject configuration, CancellationToken ct)
    {
        var config = DebugConfiguration.FromJson(System.Text.Json.JsonSerializer.SerializeToElement(configuration), parent.Configuration.WorkspaceFolder);
        _compounds.TryGetValue(parent, out var group);
        // The child is a session of its own with Parent set; the adapter factory for the (inherited) type creates its connection.
        return await StartCoreAsync(config with { PreLaunchTask = null }, parent, ct, group);
    }

    private async Task<IDebugSession?> StartCompoundAsync(DebugConfiguration compound, CancellationToken ct)
    {
        var folder = compound.WorkspaceFolder ?? _p.PrimaryFolder;
        var all = await _configurations.GetLaunchConfigurationsAsync(folder, ct);
        var members = new List<DebugConfiguration>();
        foreach (var name in LaunchJson.CompoundMembers(compound))
        {
            var found = all.FirstOrDefault(c => c.Name == name && c.Type != LaunchJson.CompoundType);
            if (found is null) { await ReportAsync($"The compound '{compound.Name}' refers to the unknown configuration '{name}'."); return null; }
            members.Add(found);
        }
        if (compound.PreLaunchTask is { Length: > 0 } pre && !await RunTaskGateAsync(pre, "preLaunchTask", folder, ct)) return null;

        var group = new CompoundGroup(LaunchJson.CompoundStopsAll(compound));
        // VS Code starts compound members concurrently.
        var started = await Task.WhenAll(members.Select(m => StartCoreAsync(m, null, ct, group)));
        return started.FirstOrDefault(s => s is not null);
    }

    private Task ReportAsync(string message)
    {
        _logger.LogWarning("{Message}", message);
        return _p.Window is { } w ? w.ShowMessageAsync(MessageSeverity.Error, message, null, CancellationToken.None).AsTask() : Task.CompletedTask;
    }

    // ------------------------------------------------------------------ session wiring

    /// <param name="requestedType">The type the configuration asked for, before a provider resolved it (launch.json "node" → js-debug's
    /// "pwa-node"): the last-known capabilities are recorded under both, since the UI looks them up by the selected configuration's type.</param>
    private void Attach(DebugSession session, string? postDebugTask, Uri? folder, CompoundGroup? group, string requestedType)
    {
        var subs = new DisposableStore();
        subs.Add(session.Stopped.Subscribe(e => OnSessionStopped(session, e)));
        subs.Add(session.Continued.Subscribe(e => OnSessionContinued(session, e)));
        subs.Add(session.Invalidated.Subscribe(_ => OnSessionInvalidated(session)));
        subs.Add(session.StateChanged.Subscribe(state =>
        {
            if (state is DebugSessionState.Running or DebugSessionState.Stopped)
                lock (_gate)
                {
                    _knownCapabilities[session.Configuration.Type] = session.Capabilities;
                    if (requestedType.Length > 0) _knownCapabilities[requestedType] = session.Capabilities;
                }
            RecomputeState();
            UpdateContextKeys();
        }));
        subs.Add(session.Output.Subscribe(OnSessionOutput));
        subs.Add(session.Terminated.Subscribe(_ => OnSessionTerminated(session)));
        lock (_gate)
        {
            _hooks[session] = new SessionHooks(subs, postDebugTask, folder);
            _sessions = [.. _sessions, session];
            if (group is not null) { group.Members.Add(session); _compounds[session] = group; }
        }
        ActiveSession = session; // a new session takes the focus
        RebuildAfterSessionChange();
        _sessionStarted.Fire(session);
    }

    private void OnSessionStopped(DebugSession session, DebugStoppedEventArgs e)
    {
        var epoch = Interlocked.Increment(ref _stopEpoch);
        ActiveSession = session; // a stop brings that session to the front (like VS Code)
        RecomputeState();
        UpdateContextKeys();
        _ = RefreshStopLocationAsync(session, e.ThreadId ?? session.FocusedThreadId, e.Reason, epoch);
    }

    private void OnSessionContinued(DebugSession session, DebugContinuedEventArgs e)
    {
        Interlocked.Increment(ref _stopEpoch); // any in-flight stack fetch for the previous stop is now stale
        if (ReferenceEquals(ActiveSession, session) && session.State != DebugSessionState.Stopped)
        {
            Volatile.Write(ref _focusedFrameId, -1); // its frame died with the stop
            SetStopLocation(null);
        }
        RecomputeState();
        UpdateContextKeys();
    }

    /// <summary>
    /// The adapter dropped (part of) the data of the current stop. The published stop location must keep pointing at a frame that is still
    /// valid. After an invalidation of stacks the old FrameId is dead, so the stack is fetched again (re-registering the frames) and the
    /// location follows it, like a new stop (VS Code re-runs the stop as well).
    /// When only variables or threads were invalidated, the frame survives and the location is unchanged, so nothing is announced:
    /// <see cref="StopLocationChanged"/> means "the stop location moved", and the editor reveals the stop line on it, so re-announcing the
    /// same location would scroll every view of the stop file back to that line. Data views re-read through their own subscription to
    /// <see cref="IDebugSession.Invalidated"/> (the panels' focus tracker starts a new epoch on it and accepts the unchanged location).
    /// Posted, so it runs after every subscriber has seen the <c>invalidated</c> event.
    /// </summary>
    private void OnSessionInvalidated(DebugSession session) => _lane.Post(() =>
    {
        if (session.State != DebugSessionState.Stopped || !ReferenceEquals(ActiveSession, session)) return;
        var location = StopLocation;
        if (location is not null && ReferenceEquals(location.Session, session) && session.IsFrameValid(location.FrameId))
            return; // only variables (or threads) were invalidated: same frame, same location

        var epoch = Interlocked.Increment(ref _stopEpoch); // a stack fetch still in flight would publish a dead frame
        Volatile.Write(ref _focusedFrameId, -1);
        SetStopLocation(null); // never leave a dead FrameId published while the stack is fetched again
        _ = RefreshStopLocationAsync(session, location?.ThreadId ?? session.FocusedThreadId, location?.Reason ?? "invalidated", epoch);
    });

    private void OnSessionOutput(DebugOutputEventArgs e)
    {
        lock (_gate) _history.Add(e); // keeps the NEWEST lines up to the budget, cutting the oldest event's head in place (OutputHistory)
        _output.Fire(e);
    }

    /// <summary>History lines of an ended session keep their label but must not keep the session alive.</summary>
    private void ArchiveHistory(DebugSession session)
    {
        var ended = new EndedSession(session.Id, session.Name, session.Configuration);
        lock (_gate) _history.Relabel(session, ended);
    }

    private void OnSessionTerminated(DebugSession session)
    {
        SessionHooks? hooks;
        CompoundGroup? group;
        bool wasActive;
        lock (_gate)
        {
            if (!_hooks.Remove(session, out hooks)) return; // already handled
            _sessions = [.. _sessions.Where(s => !ReferenceEquals(s, session))];
            wasActive = ReferenceEquals(_active, session);
            _compounds.Remove(session, out group);
            group?.Members.Remove(session);
        }
        hooks.Subscriptions.Dispose();
        ArchiveHistory(session);
        if (wasActive)
        {
            IDebugSession? next;
            lock (_gate) { next = _sessions.LastOrDefault(); _active = next; }
            _activeChanged.Fire(next);
            Interlocked.Increment(ref _stopEpoch);
            SetStopLocation(null);
            if (next is DebugSession { State: DebugSessionState.Stopped } stopped) _ = RefreshStopLocationAsync(stopped, stopped.FocusedThreadId, "focus");
        }
        RebuildAfterSessionChange();
        _sessionTerminated.Fire(session);

        // Release the session (and kill its adapter) on the lane without blocking the event that told us it ended.
        TrackEnding(DisposeSessionAsync(session));
        // Its child sessions live on its adapter (js-debug's children are connections to the server the parent owns): they end with it.
        foreach (var child in Sessions.Where(s => ReferenceEquals(s.Parent, session)).ToArray()) _ = StopQuietlyAsync(child, CancellationToken.None);
        if (group is { StopAll: true } && group.Members.Count > 0)
            foreach (var sibling in group.Members.ToArray()) _ = sibling.StopAsync();
        if (hooks.PostDebugTask is { Length: > 0 } post && session.Parent is null)
            _ = RunPostTaskAsync(post, hooks.Folder);
    }

    private void TrackEnding(Task disposal)
    {
        lock (_gate) _endingSessions.Add(disposal);
        _ = disposal.ContinueWith(static (t, state) =>
        {
            var self = (DebugService)state!;
            lock (self._gate) self._endingSessions.Remove(t);
        }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task DisposeSessionAsync(DebugSession session)
    {
        try { await session.DisposeAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Disposing session {Session}", session.Id); }
    }

    private async Task RunPostTaskAsync(string task, Uri? folder)
    {
        try
        {
            var r = await _tasks.RunAsync(task, folder, CancellationToken.None);
            if (!r.Succeeded && !r.Cancelled) await ReportAsync($"The postDebugTask '{task}' {(r.Error ?? $"terminated with exit code {r.ExitCode}")}.");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "postDebugTask failed"); }
    }

    // ------------------------------------------------------------------ stop location

    private async Task RefreshStopLocationAsync(DebugSession session, int? threadId, string reason, long? epoch = null)
    {
        epoch ??= Interlocked.Read(ref _stopEpoch);
        try
        {
            var tid = threadId ?? (await session.GetThreadsAsync()).FirstOrDefault()?.Id;
            if (tid is not { } t) return;
            var frames = await session.GetStackTraceAsync(t, 0, 20);
            if (Interlocked.Read(ref _stopEpoch) != epoch || session.State != DebugSessionState.Stopped) return; // resumed or stopped again meanwhile
            if (frames.Count == 0) { SetStopLocation(null); return; }
            var frame = PreferredStopFrame(session, frames);
            if (!ReferenceEquals(ActiveSession, session)) return; // focus moved to another session meanwhile: its frame ids are not ours
            Volatile.Write(ref _focusedFrameId, frame.Id);
            SetStopLocation(ToLocation(session, t, frame, reason));
        }
        catch (Exception ex) when (ex is DebugHandleInvalidatedException or InvalidOperationException or IOException or TimeoutException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Could not determine the stop location");
        }
    }

    /// <summary>
    /// The frame a stop shows: the first one with a source we can show that the adapter does not de-emphasise (VS Code's rule: a pause in a
    /// libc sleep or a Rust panic inside the standard library shows the user's code that called it), else the first with a source (adapters
    /// may start with runtime frames that have none), else the top frame.
    /// </summary>
    internal static DebugStackFrame PreferredStopFrame(DebugSession session, IReadOnlyList<DebugStackFrame> frames) =>
        frames.FirstOrDefault(f => f.Source?.PresentationHint != "deemphasize" && f.PresentationHint is not ("deemphasize" or "label") && session.ResolveSourceUri(f.Source) is not null)
        ?? frames.FirstOrDefault(f => session.ResolveSourceUri(f.Source) is not null)
        ?? frames[0];

    private static DebugStopLocation ToLocation(DebugSession session, int threadId, DebugStackFrame frame, string reason) =>
        new(session, threadId, frame.Id, session.ResolveSourceUri(frame.Source), frame.Line, frame.Column, reason);

    /// <summary>
    /// Focuses a frame the UI got from <paramref name="sessionId"/>'s <c>GetStackTraceAsync</c> during the current stop, however deep (the Call
    /// Stack window pages 50+ frames, other threads, "load more"). Fails with <see cref="DebugHandleInvalidatedException"/> when the frame is no
    /// longer valid (resumed, stopped again, invalidated) and <see cref="InvalidOperationException"/> when the session has ended or is no longer
    /// the focused one, so the caller never believes focus moved when it did not.
    /// </summary>
    public ValueTask SetFocusedFrameAsync(string sessionId, int threadId, int frameId, CancellationToken ct = default) =>
        new(_lane.RunAsync(async () =>
        {
            var session = Sessions.OfType<DebugSession>().FirstOrDefault(s => s.Id == sessionId)
                ?? throw new InvalidOperationException($"The debug session '{sessionId}' has ended.");
            if (session.State != DebugSessionState.Stopped) throw StaleFrame();
            if (!session.TryGetFrame(frameId, out var frame))
            {
                // Every frame id a caller can hold came from this session, which remembers what it handed out during this stop. Only when that
                // bounded registry overflowed is the id possibly valid yet unknown: then ask for the whole (capped) stack of the thread.
                if (!session.IsFrameValid(frameId)) throw StaleFrame();
                frame = (await session.GetStackTraceAsync(threadId, 0, 0, ct)).FirstOrDefault(f => f.Id == frameId);
                if (frame is null || session.State != DebugSessionState.Stopped) throw StaleFrame();
            }
            // FocusedFrameId and StopLocation describe the ACTIVE session (the REPL evaluates in that frame): a frame of another session
            // (the focus moved on while this call was queued) must not end up there.
            if (!ReferenceEquals(ActiveSession, session))
                throw new InvalidOperationException($"The debug session '{session.Name}' is no longer the focused session.");
            Volatile.Write(ref _focusedFrameId, frameId);
            SetStopLocation(ToLocation(session, threadId, frame, "frame"));
            return 0;
        }, ct));

    private static DebugHandleInvalidatedException StaleFrame() =>
        new("The stack frame is no longer valid: the debuggee has resumed or stopped again since it was obtained.");

    private void SetStopLocation(DebugStopLocation? location)
    {
        var previous = Interlocked.Exchange(ref _stopLocation, location);
        if (Equals(previous, location)) return;
        _stopLocationChanged.Fire(location);
    }

    // ------------------------------------------------------------------ state, context keys

    private void RecomputeState()
    {
        DebugState next;
        lock (_gate)
        {
            var live = _sessions.Where(s => s.State != DebugSessionState.Terminated).ToArray();
            var basis = _active is { State: not DebugSessionState.Terminated } a ? a : live.LastOrDefault();
            next = basis is null ? DebugState.Inactive : basis.State switch
            {
                DebugSessionState.Initializing => DebugState.Initializing,
                DebugSessionState.Running => DebugState.Running,
                DebugSessionState.Stopped => DebugState.Stopped,
                _ => DebugState.Inactive,
            };
        }
        if (Interlocked.Exchange(ref _state, (int)next) != (int)next) _stateChanged.Fire(next);
    }

    private void RebuildAfterSessionChange()
    {
        RecomputeState();
        UpdateContextKeys();
    }

    private void UpdateContextKeys()
    {
        if (_p.ContextKeys is not { } keys || Volatile.Read(ref _disposed) != 0) return;
        var state = State;
        var active = ActiveSession;
        keys.Set("debugState", state.ToString().ToLowerInvariant());
        keys.Set("inDebugMode", state != DebugState.Inactive);
        keys.Set("debugType", active?.Configuration.Type ?? "");
        keys.Set("debuggerAvailable", DebuggerTypes.Count > 0);
        keys.Set("focusedSessionIsAttach", active?.Configuration.IsAttach ?? false);
        keys.Set("breakpointsExist", _breakpoints.Any);
    }

    public DebugCapabilities? GetKnownCapabilities(string debugType)
    {
        lock (_gate) return _knownCapabilities.GetValueOrDefault(debugType);
    }

    // ------------------------------------------------------------------ watches

    public WatchExpression AddWatch(string expression)
    {
        var w = new WatchExpression(expression);
        lock (_gate) _watches = [.. _watches, w];
        return w;
    }

    public void RemoveWatch(WatchExpression watch)
    {
        lock (_gate) _watches = [.. _watches.Where(w => !ReferenceEquals(w, watch))];
    }

    // ------------------------------------------------------------------ debug console history

    public IReadOnlyList<DebugOutputEventArgs> GetOutputHistory()
    {
        lock (_gate) return _history.Snapshot();
    }

    public void ClearOutputHistory()
    {
        lock (_gate) _history.Clear();
        _outputCleared.Fire(null);
    }

    // ------------------------------------------------------------------ virtual sources

    public async ValueTask<string?> GetSourceContentAsync(Uri debugUri, CancellationToken ct = default)
    {
        if (!DebugSourceUri.TryParse(debugUri, out var sessionId, out var reference, out _)) return null;
        var session = Sessions.OfType<DebugSession>().FirstOrDefault(s => s.Id == sessionId);
        return session is null ? null : await session.GetSourceContentAsync(reference, ct);
    }

    // ------------------------------------------------------------------ stopping / disposal

    public ValueTask StopAllAsync(CancellationToken ct = default) =>
        new(_lane.RunAsync(async () =>
        {
            await Task.WhenAll(Sessions.Select(s => StopQuietlyAsync(s, ct)));
            return 0;
        }, CancellationToken.None));

    private static async Task StopQuietlyAsync(IDebugSession session, CancellationToken ct)
    {
        try { await session.StopAsync(ct); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await StopAllAsync(); } catch (ObjectDisposedException) { }
        foreach (var s in Sessions.OfType<DebugSession>().ToArray())
        {
            try { await s.DisposeAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Disposing session"); }
        }
        // The sessions StopAllAsync ended are released in the background (see OnSessionTerminated): wait for them too (bounded), or the app
        // could exit before their adapter servers' sockets are deleted.
        Task[] ending;
        lock (_gate) ending = [.. _endingSessions];
        try { await Task.WhenAll(ending).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { _logger.LogDebug("Sessions still being released after 10 s at shutdown"); }
        _subs.Dispose();
        _contributionTokens.Dispose();
        lock (_gate)
        {
            foreach (var h in _hooks.Values) h.Subscriptions.Dispose();
            _hooks.Clear();
            _sessions = [];
            _compounds.Clear();
            _history.Clear();
        }
        _sessionStarted.Clear(); _sessionTerminated.Clear(); _activeChanged.Clear(); _stateChanged.Clear();
        _stopLocationChanged.Clear(); _output.Clear(); _outputCleared.Clear();
    }

    /// <summary>
    /// For a container disposed synchronously (the shell disposes its container on the UI thread after awaiting the graceful shutdown, so
    /// normally no session is left): starts <see cref="DisposeAsync"/> without waiting for it, and kills every adapter process and debuggee
    /// still running RIGHT NOW. The async shutdown may never get to run — the process can exit first (a test host after a failed assertion did,
    /// and left js-debug servers running: they never exit by themselves). Killing is synchronous but does not wait for the processes.
    /// A no-op after <see cref="DisposeAsync"/>.
    /// </summary>
    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var live = Sessions.OfType<DebugSession>().ToArray();
        var pending = DisposeAsync();
        foreach (var session in live)
        {
            try { session.KillProcessesNow(); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { _logger.LogDebug(ex, "Killing the processes of {Session}", session.Id); }
        }
        if (!pending.IsCompleted)
            _ = pending.AsTask().ContinueWith(t => _logger.LogDebug(t.Exception, "Disposing the debug service"), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
}
