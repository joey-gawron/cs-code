using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Panels.Debug.Tests.Fakes;

/// <summary>In-memory <see cref="IBreakpointManager"/> with settable adapter statuses.</summary>
public sealed class FakeBreakpointManager : IBreakpointManager
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DebugBreakpointStatus> _statuses = new();
    private readonly Emitter<BreakpointsChangedEventArgs> _changed = new();
    private List<SourceBreakpoint> _source = new();
    private List<FunctionBreakpoint> _functions = new();
    private List<ExceptionFilterSetting> _filters = new();
    private int _ids;

    public IReadOnlyList<SourceBreakpoint> SourceBreakpoints { get { lock (_gate) return [.. _source]; } }
    public IReadOnlyList<FunctionBreakpoint> FunctionBreakpoints { get { lock (_gate) return [.. _functions]; } }
    public IReadOnlyList<ExceptionFilterSetting> ExceptionFilters { get { lock (_gate) return [.. _filters]; } }
    public IEventSource<BreakpointsChangedEventArgs> Changed => _changed;
    public int SubscriberCount => _changed.SubscriberCount;

    public IReadOnlyList<SourceBreakpoint> GetForUri(Uri uri) { lock (_gate) return [.. _source.Where(b => b.Uri == uri)]; }

    public bool Toggle(Uri uri, int line)
    {
        bool now;
        lock (_gate)
        {
            var existing = _source.FirstOrDefault(b => b.Uri == uri && b.Line == line);
            if (existing is not null) { _source.Remove(existing); now = false; }
            else { _source.Add(new SourceBreakpoint("bp" + ++_ids, uri, line)); now = true; }
        }
        Raise([uri]);
        return now;
    }

    public SourceBreakpoint Add(Uri uri, int line, string? condition = null, string? hitCondition = null, string? logMessage = null)
    {
        SourceBreakpoint bp;
        lock (_gate) { bp = new SourceBreakpoint("bp" + ++_ids, uri, line, null, condition, hitCondition, logMessage); _source.Add(bp); }
        Raise([uri]);
        return bp;
    }

    public void Update(SourceBreakpoint updated)
    {
        lock (_gate) _source = [.. _source.Select(b => b.Id == updated.Id ? updated : b)];
        Raise([updated.Uri]);
    }

    public void Remove(string id)
    {
        lock (_gate) _source.RemoveAll(b => b.Id == id);
        Raise([]);
    }

    public void RemoveAll()
    {
        lock (_gate) { _source.Clear(); _functions.Clear(); }
        Raise([]);
    }

    public void SetEnabledAll(bool enabled)
    {
        lock (_gate) _source = [.. _source.Select(b => b with { Enabled = enabled })];
        Raise([]);
    }

    public FunctionBreakpoint AddFunction(string name, string? condition = null)
    {
        FunctionBreakpoint fn;
        lock (_gate) { fn = new FunctionBreakpoint("fn" + ++_ids, name, condition); _functions.Add(fn); }
        Raise([]);
        return fn;
    }

    public void RemoveFunction(string id)
    {
        lock (_gate) _functions.RemoveAll(f => f.Id == id);
        Raise([]);
    }

    public void SetExceptionFilters(IReadOnlyList<ExceptionFilterSetting> filters)
    {
        lock (_gate) _filters = [.. filters];
        Raise([]);
    }

    public DebugBreakpointStatus? GetStatus(string breakpointId) { lock (_gate) return _statuses.GetValueOrDefault(breakpointId); }

    public void ReportStatus(DebugBreakpointStatus status)
    {
        lock (_gate) _statuses[status.BreakpointId] = status;
        Raise([]);
    }

    public void ClearStatuses(string? sessionId = null)
    {
        lock (_gate) _statuses.Clear();
        Raise([]);
    }

    public void ApplyLineShift(Uri uri, int fromLine, int delta) { }

    private void Raise(IReadOnlyCollection<Uri> uris) => _changed.Fire(new BreakpointsChangedEventArgs(uris, uris.Count == 0));
}

public sealed class FakeConfigurationService : IDebugConfigurationService
{
    public DebugConfiguration? Selected { get; set; }
    public IEventSource<DebugConfiguration?> SelectedChanged { get; } = new Emitter<DebugConfiguration?>();
    public IEventSource<object?> ConfigurationsChanged { get; } = new Emitter<object?>();
    public ValueTask<IReadOnlyList<DebugConfiguration>> GetLaunchConfigurationsAsync(Uri? folder = null, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<DebugConfiguration>>([]);
    public ValueTask<DebugConfiguration> ResolveVariablesAsync(DebugConfiguration configuration, CancellationToken ct = default) => ValueTask.FromResult(configuration);
    public ValueTask<Uri> CreateLaunchJsonAsync(string debugType, Uri? folder = null, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>An <see cref="IDebugService"/> (+ frame focus + output history) whose state a test drives directly.</summary>
public sealed class FakeDebugService : IDebugService, IDebugFrameFocus, IDebugOutputHistory
{
    private readonly object _gate = new();
    private readonly List<IDebugSession> _sessions = new();
    private readonly List<WatchExpression> _watches = new();
    private readonly List<DebugOutputEventArgs> _history = new();
    private IDebugSession? _active;
    private DebugStopLocation? _stopLocation;
    private int? _focusedFrame;

    public FakeBreakpointManager FakeBreakpoints { get; } = new();
    public Emitter<IDebugSession> SessionStartedEmitter { get; } = new();
    public Emitter<IDebugSession> SessionTerminatedEmitter { get; } = new();
    public Emitter<IDebugSession?> ActiveSessionChangedEmitter { get; } = new();
    public Emitter<DebugState> StateChangedEmitter { get; } = new();
    public Emitter<DebugStopLocation?> StopLocationChangedEmitter { get; } = new();
    public Emitter<DebugOutputEventArgs> OutputEmitter { get; } = new();
    public Emitter<object?> OutputClearedEmitter { get; } = new();
    public List<(string SessionId, int ThreadId, int FrameId)> FocusCalls { get; } = new();

    public IReadOnlyList<IDebugSession> Sessions { get { lock (_gate) return [.. _sessions]; } }
    public DebugState State { get; private set; } = DebugState.Inactive;
    public DebugStopLocation? StopLocation => Volatile.Read(ref _stopLocation);
    public IBreakpointManager Breakpoints => FakeBreakpoints;
    public IDebugConfigurationService Configurations { get; } = new FakeConfigurationService();
    public IReadOnlyList<DebuggerType> DebuggerTypes { get; } = [];
    public IReadOnlyList<WatchExpression> Watches { get { lock (_gate) return [.. _watches]; } }

    public IEventSource<IDebugSession> SessionStarted => SessionStartedEmitter;
    public IEventSource<IDebugSession> SessionTerminated => SessionTerminatedEmitter;
    public IEventSource<IDebugSession?> ActiveSessionChanged => ActiveSessionChangedEmitter;
    public IEventSource<DebugState> StateChanged => StateChangedEmitter;
    public IEventSource<DebugStopLocation?> StopLocationChanged => StopLocationChangedEmitter;
    public IEventSource<DebugOutputEventArgs> Output => OutputEmitter;
    public IEventSource<object?> OutputCleared => OutputClearedEmitter;

    public IDebugSession? ActiveSession
    {
        get { lock (_gate) return _active; }
        set
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, value)) return;
                _active = value;
            }
            ActiveSessionChangedEmitter.Fire(value);
        }
    }

    public int? FocusedFrameId => _focusedFrame;

    /// <summary>Registers a session like <c>StartAsync</c> would (it becomes active; state follows the session).</summary>
    public void AddSession(FakeDebugSession session)
    {
        lock (_gate) _sessions.Add(session);
        session.StateEmitter.Subscribe(_ => RecomputeState());
        session.StoppedEmitter.Subscribe(e => { RecomputeState(); });
        SessionStartedEmitter.Fire(session);
        ActiveSession = session;
        RecomputeState();
    }

    public void RemoveSession(FakeDebugSession session)
    {
        lock (_gate)
        {
            _sessions.Remove(session);
            if (ReferenceEquals(_active, session)) _active = _sessions.LastOrDefault();
        }
        SessionTerminatedEmitter.Fire(session);
        ActiveSessionChangedEmitter.Fire(ActiveSession);
        SetStopLocation(null);
        RecomputeState();
    }

    private void RecomputeState()
    {
        var basis = ActiveSession;
        var next = basis is null ? DebugState.Inactive : basis.State switch
        {
            DebugSessionState.Initializing => DebugState.Initializing,
            DebugSessionState.Running => DebugState.Running,
            DebugSessionState.Stopped => DebugState.Stopped,
            _ => DebugState.Inactive,
        };
        if (next != State) { State = next; StateChangedEmitter.Fire(next); }
    }

    /// <summary>Publishes the stop location the real service derives from the stack (call after <c>session.Stop</c>).</summary>
    public void SetStopLocation(DebugStopLocation? location)
    {
        var previous = Interlocked.Exchange(ref _stopLocation, location);
        if (Equals(previous, location)) return;
        _focusedFrame = location?.FrameId;
        StopLocationChangedEmitter.Fire(location);
    }

    /// <summary>Stops the session, then publishes the top frame as the stop location (what the real service does a moment later).</summary>
    public void StopAt(FakeDebugSession session, string reason = "breakpoint", int frameIndex = 0, int threadId = 1)
    {
        session.Stop(reason, threadId);
        var frame = session.Frames[threadId][frameIndex];
        SetStopLocation(new DebugStopLocation(session, threadId, frame.Id, frame.Source?.ToUri(), frame.Line, frame.Column, reason));
    }

    public ValueTask SetFocusedFrameAsync(string sessionId, int threadId, int frameId, CancellationToken ct = default)
    {
        FocusCalls.Add((sessionId, threadId, frameId));
        var session = Sessions.OfType<FakeDebugSession>().FirstOrDefault(s => s.Id == sessionId);
        var frame = session?.Frames.GetValueOrDefault(threadId)?.FirstOrDefault(f => f.Id == frameId);
        if (session is not null && frame is not null)
            SetStopLocation(new DebugStopLocation(session, threadId, frameId, frame.Source?.ToUri(), frame.Line, frame.Column, "frame"));
        return ValueTask.CompletedTask;
    }

    public ValueTask<IDebugSession?> StartAsync(DebugConfiguration configuration, CancellationToken ct = default) => ValueTask.FromResult<IDebugSession?>(null);
    public ValueTask StopAllAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public IDisposable RegisterAdapterFactory(string debugType, IDebugAdapterFactory factory) => Disposable.Empty;
    public IDisposable RegisterConfigurationProvider(string debugType, IDebugConfigurationProvider provider) => Disposable.Empty;

    public WatchExpression AddWatch(string expression)
    {
        var w = new WatchExpression(expression);
        lock (_gate) _watches.Add(w);
        return w;
    }

    public void RemoveWatch(WatchExpression watch)
    {
        lock (_gate) _watches.RemoveAll(w => ReferenceEquals(w, watch));
    }

    // ------------------------------------------------------------------ output + history (like the real service)

    public void EmitOutput(DebugOutputEventArgs e)
    {
        lock (_gate)
        {
            _history.Add(e);
            if (_history.Count > 5_000) _history.RemoveRange(0, _history.Count - 5_000);
        }
        OutputEmitter.Fire(e);
    }

    public IReadOnlyList<DebugOutputEventArgs> GetOutputHistory() { lock (_gate) return [.. _history]; }

    public void ClearOutputHistory()
    {
        lock (_gate) _history.Clear();
        OutputClearedEmitter.Fire(null);
    }

    /// <summary>Total live subscribers on the service's own emitters (leak assertions).</summary>
    public int SubscriberCount =>
        SessionStartedEmitter.SubscriberCount + SessionTerminatedEmitter.SubscriberCount + ActiveSessionChangedEmitter.SubscriberCount
        + StateChangedEmitter.SubscriberCount + StopLocationChangedEmitter.SubscriberCount + OutputEmitter.SubscriberCount
        + OutputClearedEmitter.SubscriberCount + FakeBreakpoints.SubscriberCount;
}
