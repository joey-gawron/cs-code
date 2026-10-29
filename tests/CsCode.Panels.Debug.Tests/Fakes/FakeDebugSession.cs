using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;

namespace CsCode.Panels.Debug.Tests.Fakes;

/// <summary>How a <see cref="FakeDebugSession"/> answers requests (to model slow, hung and hostile adapters).</summary>
public enum FakeLatency
{
    /// <summary>Completes immediately on a pool thread (like the real Debug lane).</summary>
    Instant,
    /// <summary>Completes after <see cref="FakeDebugSession.Delay"/>; honours cancellation.</summary>
    Slow,
    /// <summary>Never completes and ignores cancellation (a hung adapter).</summary>
    Hang,
}

/// <summary>
/// A scriptable <see cref="IDebugSession"/>: threads, frames per thread, scopes per frame, variables per reference, evaluation, setVariable
/// and completions, with real handle semantics (everything handed out before a resume throws afterwards) and per-request counters.
/// Events are public emitters so a test can fire any sequence.
/// </summary>
public class FakeDebugSession : IDebugSession
{
    private readonly object _gate = new();
    private long _stopGeneration = 1;
    private DebugSessionState _state = DebugSessionState.Running;

    public FakeDebugSession(string id = "s1", string name = "Fake")
    {
        Id = id;
        Name = name;
        Configuration = new DebugConfiguration("fake", "launch", name, new Dictionary<string, JsonElement>());
        Capabilities = new DebugCapabilities(SupportsSetVariable: true, SupportsCompletionsRequest: true, SupportsEvaluateForHovers: true, SupportsExceptionInfo: true);
    }

    public string Id { get; }
    public string Name { get; }
    public DebugConfiguration Configuration { get; }
    public IDebugSession? Parent => null;
    public DebugCapabilities Capabilities { get; set; }
    public int? FocusedThreadId { get; set; } = 1;
    public DebugSessionState State { get => _state; private set => _state = value; }

    public Emitter<DebugStoppedEventArgs> StoppedEmitter { get; } = new();
    public Emitter<DebugContinuedEventArgs> ContinuedEmitter { get; } = new();
    public Emitter<DebugOutputEventArgs> OutputEmitter { get; } = new();
    public Emitter<DebugThreadEventArgs> ThreadEmitter { get; } = new();
    public Emitter<DebugSessionState> StateEmitter { get; } = new();
    public Emitter<DebugTerminatedEventArgs> TerminatedEmitter { get; } = new();
    public Emitter<DebugBreakpointStatus> BreakpointStatusEmitter { get; } = new();
    public Emitter<IDebugSession> InvalidatedEmitter { get; } = new();

    public IEventSource<DebugStoppedEventArgs> Stopped => StoppedEmitter;
    public IEventSource<DebugContinuedEventArgs> Continued => ContinuedEmitter;
    public IEventSource<DebugOutputEventArgs> Output => OutputEmitter;
    public IEventSource<DebugThreadEventArgs> ThreadChanged => ThreadEmitter;
    public IEventSource<DebugSessionState> StateChanged => StateEmitter;
    public IEventSource<DebugTerminatedEventArgs> Terminated => TerminatedEmitter;
    public IEventSource<DebugBreakpointStatus> BreakpointStatusChanged => BreakpointStatusEmitter;
    public IEventSource<IDebugSession> Invalidated => InvalidatedEmitter;

    // ------------------------------------------------------------------ script

    public List<DebugThread> Threads { get; } = [new DebugThread(1, "Main Thread")];
    public Dictionary<int, List<DebugStackFrame>> Frames { get; } = new();
    public Dictionary<int, List<DebugScope>> Scopes { get; } = new();
    public Dictionary<int, List<DebugVariable>> Variables { get; } = new();
    public Func<string, int?, string, DebugEvaluateResult> Evaluator { get; set; } = (e, _, _) => new DebugEvaluateResult("<" + e + ">", "string");
    public Func<int, string, string, DebugVariable>? SetVariableHandler { get; set; }
    public Func<string, int, IReadOnlyList<DebugCompletionItem>> CompletionHandler { get; set; } = (_, _) => [];

    public FakeLatency Latency { get; set; } = FakeLatency.Instant;
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(200);
    /// <summary>A hostile adapter that ignores <c>start</c>/<c>count</c> and always answers with everything.</summary>
    public bool IgnorePaging { get; set; }

    /// <summary>
    /// Frame ids behave like in CsCode.Dap's DebugSession: only ids a <c>stackTrace</c> returned since the last stop/invalidation are valid
    /// (scopes/evaluate with any other id fail as stale). A stop registers the stopped thread's frames (the debug service fetches them to
    /// publish the stop location); <see cref="Invalidate"/> forgets all of them. Off by default.
    /// </summary>
    public bool TrackFrameIds { get; set; }
    private readonly HashSet<int> _liveFrames = new();

    /// <summary>The adapter sent <c>invalidated</c>: frame ids are forgotten (and, for adapters without stable ids, renumbered).</summary>
    public void Invalidate(bool renumberFrames = false)
    {
        lock (_gate)
        {
            _liveFrames.Clear();
            if (renumberFrames)
                foreach (var frames in Frames.Values)
                    for (var i = 0; i < frames.Count; i++)
                    {
                        var renumbered = frames[i] with { Id = frames[i].Id + 1000 };
                        if (Scopes.TryGetValue(frames[i].Id, out var scopes)) Scopes[renumbered.Id] = scopes;
                        frames[i] = renumbered;
                    }
        }
        InvalidatedEmitter.Fire(this);
    }

    private void RequireLiveFrame(int frameId)
    {
        if (!TrackFrameIds) return;
        lock (_gate)
            if (!_liveFrames.Contains(frameId)) throw new InvalidOperationException("The stack frame is no longer valid: the debuggee has resumed since it was obtained.");
    }

    // ------------------------------------------------------------------ counters

    private int _threadCalls, _stackCalls, _scopeCalls, _variableCalls, _evaluateCalls, _setCalls, _completionCalls, _resumeCalls;
    private int _outstanding, _maxOutstanding;
    public int ThreadCalls => _threadCalls;
    public int StackCalls => _stackCalls;
    public int ScopeCalls => _scopeCalls;
    public int VariableCalls => _variableCalls;
    public int EvaluateCalls => _evaluateCalls;
    public int SetVariableCalls => _setCalls;
    public int CompletionCalls => _completionCalls;
    public int ResumeCalls => _resumeCalls;
    /// <summary>Highest number of requests in flight at once.</summary>
    public int MaxOutstanding => _maxOutstanding;
    public List<(int Reference, int? Start, int? Count)> VariableRequests { get; } = new();
    public List<(string Expression, int? FrameId, string Context)> EvaluateRequests { get; } = new();
    public List<string> StepLog { get; } = new();
    /// <summary>Number of requests that are still waiting (hung or slow) right now.</summary>
    public int Outstanding => _outstanding;

    // ------------------------------------------------------------------ state changes (call from tests)

    /// <summary>The debuggee stops: new handle generation, state Stopped, <c>Stopped</c> then <c>StateChanged</c> fired.</summary>
    public void Stop(string reason = "breakpoint", int threadId = 1, bool allThreads = true, string? description = null, string? text = null)
    {
        lock (_gate)
        {
            _stopGeneration++;
            State = DebugSessionState.Stopped;
            _liveFrames.Clear();
            if (Frames.TryGetValue(threadId, out var stopped)) foreach (var f in stopped) _liveFrames.Add(f.Id);
        }
        FocusedThreadId = threadId;
        StateEmitter.Fire(DebugSessionState.Stopped);
        StoppedEmitter.Fire(new DebugStoppedEventArgs(this, reason, threadId, allThreads, description, text));
    }

    /// <summary>The debuggee resumes: handles die, state Running, <c>Continued</c> fired.</summary>
    public void Resume(int threadId = 1)
    {
        lock (_gate)
        {
            _stopGeneration++;
            State = DebugSessionState.Running;
        }
        ContinuedEmitter.Fire(new DebugContinuedEventArgs(this, threadId, true));
        StateEmitter.Fire(DebugSessionState.Running);
    }

    public void Terminate()
    {
        lock (_gate) State = DebugSessionState.Terminated;
        StateEmitter.Fire(DebugSessionState.Terminated);
        TerminatedEmitter.Fire(new DebugTerminatedEventArgs(this, 0, null));
    }

    // ------------------------------------------------------------------ helpers to build a typical stop

    /// <summary>One thread, frames Main → Outer → Add, a "Locals" scope holding x, s and an object o with fields.</summary>
    public FakeDebugSession WithDefaultScript()
    {
        Frames[1] =
        [
            new DebugStackFrame(100, "Sample.Program.Add(int a, int b)", new DebugSource("Program.cs", "/work/Program.cs"), 8, 0),
            new DebugStackFrame(101, "Sample.Program.Outer(int n)", new DebugSource("Program.cs", "/work/Program.cs"), 14, 0),
            new DebugStackFrame(102, "Sample.Program.Main()", new DebugSource("Program.cs", "/work/Program.cs"), 24, 0),
        ];
        Scopes[100] = [new DebugScope("Locals", 1000, false)];
        Scopes[101] = [new DebugScope("Locals", 1100, false)];
        Scopes[102] = [new DebugScope("Locals", 1200, false)];
        Variables[1000] =
        [
            new DebugVariable("a", "41", "int", EvaluateName: "a"),
            new DebugVariable("b", "1", "int", EvaluateName: "b"),
            new DebugVariable("sum", "42", "int", EvaluateName: "sum"),
        ];
        Variables[1100] = [new DebugVariable("n", "41", "int", EvaluateName: "n"), new DebugVariable("inner", "0", "int", EvaluateName: "inner")];
        Variables[1200] =
        [
            new DebugVariable("x", "41", "int", EvaluateName: "x"),
            new DebugVariable("s", "\"hello\"", "string", EvaluateName: "s"),
            new DebugVariable("o", "{Sample.Item}", "Sample.Item", 2000, "o", 2, 0),
        ];
        Variables[2000] = [new DebugVariable("Name", "\"widget\"", "string", EvaluateName: "o.Name"), new DebugVariable("Count", "3", "int", EvaluateName: "o.Count")];
        return this;
    }

    // ------------------------------------------------------------------ IDebugSession

    private async ValueTask<T> RunAsync<T>(Func<T> produce, CancellationToken ct, bool needsStopped = true)
    {
        var gen = Interlocked.Read(ref _stopGeneration);
        var now = Interlocked.Increment(ref _outstanding);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxOutstanding))) { if (Interlocked.CompareExchange(ref _maxOutstanding, now, seen) == seen) break; }
        try
        {
            ct.ThrowIfCancellationRequested();
            switch (Latency)
            {
                case FakeLatency.Hang:
                    await new TaskCompletionSource().Task.ConfigureAwait(false); // never, and ignores ct
                    break;
                case FakeLatency.Slow:
                    await Task.Delay(Delay, ct).ConfigureAwait(false);
                    break;
                default:
                    await Task.Yield();
                    break;
            }
            if (needsStopped && (State != DebugSessionState.Stopped || Interlocked.Read(ref _stopGeneration) != gen))
                throw new InvalidOperationException("The debuggee has resumed since this handle was obtained.");
            return produce();
        }
        finally { Interlocked.Decrement(ref _outstanding); }
    }

    public ValueTask<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _threadCalls);
        return RunAsync<IReadOnlyList<DebugThread>>(() => [.. Threads], ct, needsStopped: false);
    }

    public ValueTask<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(int threadId, int startFrame = 0, int levels = 64, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _stackCalls);
        return RunAsync<IReadOnlyList<DebugStackFrame>>(() =>
        {
            lock (_gate)
            {
                IReadOnlyList<DebugStackFrame> page = Frames.TryGetValue(threadId, out var all) ? [.. all.Skip(startFrame).Take(levels <= 0 ? int.MaxValue : levels)] : [];
                foreach (var f in page) _liveFrames.Add(f.Id);
                return page;
            }
        }, ct);
    }

    public ValueTask<IReadOnlyList<DebugScope>> GetScopesAsync(int frameId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _scopeCalls);
        return RunAsync<IReadOnlyList<DebugScope>>(() =>
        {
            RequireLiveFrame(frameId);
            return Scopes.TryGetValue(frameId, out var s) ? [.. s] : [];
        }, ct);
    }

    public ValueTask<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, int? start = null, int? count = null, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _variableCalls);
        lock (VariableRequests) VariableRequests.Add((variablesReference, start, count));
        return RunAsync<IReadOnlyList<DebugVariable>>(() =>
        {
            if (!Variables.TryGetValue(variablesReference, out var all)) return [];
            if (IgnorePaging) return all;
            IEnumerable<DebugVariable> page = all.Skip(start ?? 0);
            if (count is > 0) page = page.Take(count.Value);
            return [.. page];
        }, ct);
    }

    public ValueTask<DebugEvaluateResult> EvaluateAsync(string expression, int? frameId, string context = "repl", CancellationToken ct = default)
    {
        Interlocked.Increment(ref _evaluateCalls);
        lock (EvaluateRequests) EvaluateRequests.Add((expression, frameId, context));
        return RunAsync(() =>
        {
            if (frameId is { } f) RequireLiveFrame(f);
            return Evaluator(expression, frameId, context);
        }, ct, needsStopped: frameId is not null);
    }

    public ValueTask<DebugVariable> SetVariableAsync(int variablesReference, string name, string value, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _setCalls);
        return RunAsync(() => SetVariableHandler?.Invoke(variablesReference, name, value) ?? new DebugVariable(name, value), ct);
    }

    public ValueTask<IReadOnlyList<DebugCompletionItem>> CompletionsAsync(int? frameId, string text, int column, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _completionCalls);
        return RunAsync(() => CompletionHandler(text, column), ct, needsStopped: false);
    }

    private ValueTask Step(string what, int? threadId)
    {
        Interlocked.Increment(ref _resumeCalls);
        lock (StepLog) StepLog.Add(what);
        Resume(threadId ?? FocusedThreadId ?? 1);
        return ValueTask.CompletedTask;
    }

    public ValueTask ContinueAsync(int? threadId = null, CancellationToken ct = default) => Step("continue", threadId);
    public ValueTask StepOverAsync(int? threadId = null, CancellationToken ct = default) => Step("next", threadId);
    public ValueTask StepIntoAsync(int? threadId = null, CancellationToken ct = default) => Step("stepIn", threadId);
    public ValueTask StepOutAsync(int? threadId = null, CancellationToken ct = default) => Step("stepOut", threadId);
    public ValueTask PauseAsync(int? threadId = null, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask RestartAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken ct = default) { Terminate(); return ValueTask.CompletedTask; }

    public Func<string, JsonElement?, JsonElement?>? CustomHandler { get; set; }

    public ValueTask<JsonElement?> CustomRequestAsync(string command, JsonElement? arguments = null, CancellationToken ct = default) =>
        RunAsync(() => CustomHandler?.Invoke(command, arguments), ct, needsStopped: false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Total number of live subscribers across all emitters (leak assertions).</summary>
    public int SubscriberCount =>
        StoppedEmitter.SubscriberCount + ContinuedEmitter.SubscriberCount + OutputEmitter.SubscriberCount + ThreadEmitter.SubscriberCount
        + StateEmitter.SubscriberCount + TerminatedEmitter.SubscriberCount + BreakpointStatusEmitter.SubscriberCount + InvalidatedEmitter.SubscriberCount;
}

/// <summary>
/// A session that keeps its module list like the DAP module's (<see cref="IDebugSessionModules"/>): <see cref="ModuleList"/> plays the modules
/// the adapter reported with <c>module</c> events. Nothing here answers the raw <c>modules</c> request unless the test sets a CustomHandler.
/// </summary>
public sealed class FakeModulesDebugSession : FakeDebugSession, IDebugSessionModules
{
    private int _modulesCalls;

    public List<DebugModule> ModuleList { get; } = new();
    public int ModulesCalls => Volatile.Read(ref _modulesCalls);

    public IReadOnlyList<DebugModule> Modules
    {
        get { lock (ModuleList) return [.. ModuleList]; }
    }

    public ValueTask<IReadOnlyList<DebugModule>> GetModulesAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _modulesCalls);
        return ValueTask.FromResult(Modules);
    }
}
