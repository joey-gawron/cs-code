using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;

namespace CsCode.Editor.Tests.Support;

/// <summary>In-memory <see cref="IBreakpointManager"/>: the editor's breakpoint margin and edit tracking are tested against it.</summary>
public sealed class FakeBreakpointManager : IBreakpointManager
{
    private readonly object _gate = new();
    private readonly List<SourceBreakpoint> _bps = new();
    private readonly Dictionary<string, DebugBreakpointStatus> _status = new();
    private readonly Emitter<BreakpointsChangedEventArgs> _changed = new();
    private int _next;

    public List<(Uri Uri, int FromLine, int Delta)> Shifts { get; } = new();
    public IReadOnlyList<SourceBreakpoint> SourceBreakpoints { get { lock (_gate) return _bps.ToArray(); } }
    public IReadOnlyList<SourceBreakpoint> GetForUri(Uri uri) { lock (_gate) return _bps.Where(b => b.Uri == uri).ToArray(); }
    public IReadOnlyList<FunctionBreakpoint> FunctionBreakpoints => [];
    public IReadOnlyList<ExceptionFilterSetting> ExceptionFilters => [];
    public IEventSource<BreakpointsChangedEventArgs> Changed => _changed;

    public bool Toggle(Uri uri, int line)
    {
        bool added;
        lock (_gate)
        {
            var existing = _bps.FirstOrDefault(b => b.Uri == uri && b.Line == line);
            if (existing is not null)
            {
                _bps.Remove(existing);
                added = false;
            }
            else
            {
                _bps.Add(new SourceBreakpoint("bp" + ++_next, uri, line));
                added = true;
            }
        }
        _changed.Fire(new BreakpointsChangedEventArgs([uri], false));
        return added;
    }

    public SourceBreakpoint Add(Uri uri, int line, string? condition = null, string? hitCondition = null, string? logMessage = null)
    {
        var bp = new SourceBreakpoint("bp" + Interlocked.Increment(ref _next), uri, line, null, condition, hitCondition, logMessage);
        lock (_gate) _bps.Add(bp);
        _changed.Fire(new BreakpointsChangedEventArgs([uri], false));
        return bp;
    }

    public void Update(SourceBreakpoint updated)
    {
        lock (_gate)
        {
            var i = _bps.FindIndex(b => b.Id == updated.Id);
            if (i >= 0) _bps[i] = updated;
        }
        _changed.Fire(new BreakpointsChangedEventArgs([updated.Uri], false));
    }

    public void Remove(string id)
    {
        Uri? uri;
        lock (_gate)
        {
            uri = _bps.FirstOrDefault(b => b.Id == id)?.Uri;
            _bps.RemoveAll(b => b.Id == id);
        }
        if (uri is not null) _changed.Fire(new BreakpointsChangedEventArgs([uri], false));
    }

    public void RemoveAll()
    {
        lock (_gate) _bps.Clear();
        _changed.Fire(new BreakpointsChangedEventArgs([], false));
    }

    public void SetEnabledAll(bool enabled) { }
    public FunctionBreakpoint AddFunction(string name, string? condition = null) => throw new NotSupportedException();
    public void RemoveFunction(string id) { }
    public void SetExceptionFilters(IReadOnlyList<ExceptionFilterSetting> filters) { }

    public DebugBreakpointStatus? GetStatus(string breakpointId) { lock (_gate) return _status.GetValueOrDefault(breakpointId); }

    public void ReportStatus(DebugBreakpointStatus status)
    {
        lock (_gate) _status[status.BreakpointId] = status;
        _changed.Fire(new BreakpointsChangedEventArgs([], false));
    }

    public void ClearStatuses(string? sessionId = null) { lock (_gate) _status.Clear(); }

    /// <summary>Semantics the editor relies on: every breakpoint of <paramref name="uri"/> at a line &gt;= <paramref name="fromLine"/> moves by <paramref name="delta"/>.</summary>
    public void ApplyLineShift(Uri uri, int fromLine, int delta)
    {
        lock (_gate)
        {
            Shifts.Add((uri, fromLine, delta));
            for (var i = 0; i < _bps.Count; i++)
                if (_bps[i].Uri == uri && _bps[i].Line >= fromLine) _bps[i] = _bps[i] with { Line = _bps[i].Line + delta };
        }
        _changed.Fire(new BreakpointsChangedEventArgs([uri], false));
    }
}

public sealed class FakeDebugSession : IDebugSession
{
    public string Id => "fake-session";
    public string Name => "Fake";
    public DebugConfiguration Configuration { get; } = new("fake", "launch", "Fake", new Dictionary<string, JsonElement>());
    public DebugSessionState State { get; set; } = DebugSessionState.Stopped;
    public IDebugSession? Parent => null;
    public DebugCapabilities Capabilities { get; } = new(SupportsConditionalBreakpoints: true, SupportsEvaluateForHovers: true);
    public int? FocusedThreadId => 1;
    public Func<string, int?, string, DebugEvaluateResult> Evaluate { get; set; } = (e, _, _) => new DebugEvaluateResult("42", "int");
    public List<(string Expression, string Context)> Evaluations { get; } = new();

    public IEventSource<DebugStoppedEventArgs> Stopped { get; } = new Emitter<DebugStoppedEventArgs>();
    public IEventSource<DebugContinuedEventArgs> Continued { get; } = new Emitter<DebugContinuedEventArgs>();
    public IEventSource<DebugOutputEventArgs> Output { get; } = new Emitter<DebugOutputEventArgs>();
    public IEventSource<DebugThreadEventArgs> ThreadChanged { get; } = new Emitter<DebugThreadEventArgs>();
    public IEventSource<DebugSessionState> StateChanged { get; } = new Emitter<DebugSessionState>();
    public IEventSource<DebugTerminatedEventArgs> Terminated { get; } = new Emitter<DebugTerminatedEventArgs>();
    public IEventSource<DebugBreakpointStatus> BreakpointStatusChanged { get; } = new Emitter<DebugBreakpointStatus>();
    public IEventSource<IDebugSession> Invalidated { get; } = new Emitter<IDebugSession>();

    public ValueTask<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<DebugThread>>([]);
    public ValueTask<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(int threadId, int startFrame = 0, int levels = 64, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<DebugStackFrame>>([]);
    public ValueTask<IReadOnlyList<DebugScope>> GetScopesAsync(int frameId, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<DebugScope>>([]);
    public ValueTask<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, int? start = null, int? count = null, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<DebugVariable>>([]);

    public ValueTask<DebugEvaluateResult> EvaluateAsync(string expression, int? frameId, string context = "repl", CancellationToken ct = default)
    {
        lock (Evaluations) Evaluations.Add((expression, context));
        return ValueTask.FromResult(Evaluate(expression, frameId, context));
    }

    public ValueTask<DebugVariable> SetVariableAsync(int variablesReference, string name, string value, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<DebugCompletionItem>> CompletionsAsync(int? frameId, string text, int column, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<DebugCompletionItem>>([]);
    public ValueTask ContinueAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask StepOverAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask StepIntoAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask StepOutAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask PauseAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask RestartAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<JsonElement?> CustomRequestAsync(string command, JsonElement? arguments = null, CancellationToken ct = default) => ValueTask.FromResult<JsonElement?>(null);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A debug service the editor tests drive by hand: set <see cref="Stop"/> to "stop" somewhere, <see cref="Resume"/> to continue.</summary>
public sealed class FakeDebugService : IDebugService
{
    private readonly Emitter<DebugState> _state = new();
    private readonly Emitter<DebugStopLocation?> _stop = new();

    public IReadOnlyList<IDebugSession> Sessions => Session is null ? [] : [Session];
    public IDebugSession? ActiveSession { get; set; }
    public FakeDebugSession? Session { get; set; }
    public DebugState State { get; private set; } = DebugState.Inactive;
    public DebugStopLocation? StopLocation { get; private set; }
    public FakeBreakpointManager Manager { get; } = new();
    public IBreakpointManager Breakpoints => Manager;
    public IDebugConfigurationService Configurations => throw new NotSupportedException();
    public IReadOnlyList<DebuggerType> DebuggerTypes => [];
    public IReadOnlyList<WatchExpression> Watches => [];
    public IEventSource<IDebugSession> SessionStarted { get; } = new Emitter<IDebugSession>();
    public IEventSource<IDebugSession> SessionTerminated { get; } = new Emitter<IDebugSession>();
    public IEventSource<IDebugSession?> ActiveSessionChanged { get; } = new Emitter<IDebugSession?>();
    public IEventSource<DebugState> StateChanged => _state;
    public IEventSource<DebugStopLocation?> StopLocationChanged => _stop;
    public IEventSource<DebugOutputEventArgs> Output { get; } = new Emitter<DebugOutputEventArgs>();

    public ValueTask<IDebugSession?> StartAsync(DebugConfiguration configuration, CancellationToken ct = default) => ValueTask.FromResult<IDebugSession?>(Session);
    public ValueTask StopAllAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public IDisposable RegisterAdapterFactory(string debugType, IDebugAdapterFactory factory) => Disposable.Empty;
    public IDisposable RegisterConfigurationProvider(string debugType, IDebugConfigurationProvider provider) => Disposable.Empty;
    public WatchExpression AddWatch(string expression) => new(expression);
    public void RemoveWatch(WatchExpression watch) { }

    public void Stop(Uri uri, int line)
    {
        Session ??= new FakeDebugSession();
        ActiveSession = Session;
        State = DebugState.Stopped;
        StopLocation = new DebugStopLocation(Session, 1, 7, uri, line, 0, "breakpoint");
        ((Emitter<DebugState>)StateChanged).Fire(State);
        _stop.Fire(StopLocation);
    }

    public void Resume()
    {
        State = DebugState.Running;
        StopLocation = null;
        _stop.Fire(null);
        _state.Fire(State);
    }
}
