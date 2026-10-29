using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;

namespace CsCode.Dap.Service;

/// <summary>
/// Stand-in for a session that has ended, kept in the bounded Debug Console history instead of the real session so that up to 5,000
/// retained lines can never pin a dead session (its client, buffers, handle caches…) in memory. Identity (id, name, configuration)
/// survives so the console can still label the lines; every operation reports that the session is gone.
/// </summary>
internal sealed class EndedSession(string id, string name, DebugConfiguration configuration) : IDebugSession
{
    private static readonly Emitter<DebugStoppedEventArgs> NoStopped = new();
    private static readonly Emitter<DebugContinuedEventArgs> NoContinued = new();
    private static readonly Emitter<DebugOutputEventArgs> NoOutput = new();
    private static readonly Emitter<DebugThreadEventArgs> NoThread = new();
    private static readonly Emitter<DebugSessionState> NoState = new();
    private static readonly Emitter<DebugTerminatedEventArgs> NoTerminated = new();
    private static readonly Emitter<DebugBreakpointStatus> NoStatus = new();
    private static readonly Emitter<IDebugSession> NoInvalidated = new();

    public string Id { get; } = id;
    public string Name { get; } = name;
    public DebugConfiguration Configuration { get; } = configuration;
    public DebugSessionState State => DebugSessionState.Terminated;
    public IDebugSession? Parent => null; // a parent link would pin a live (or dead) parent session from the history
    public DebugCapabilities Capabilities { get; } = new();
    public int? FocusedThreadId => null;

    public IEventSource<DebugStoppedEventArgs> Stopped => NoStopped;
    public IEventSource<DebugContinuedEventArgs> Continued => NoContinued;
    public IEventSource<DebugOutputEventArgs> Output => NoOutput;
    public IEventSource<DebugThreadEventArgs> ThreadChanged => NoThread;
    public IEventSource<DebugSessionState> StateChanged => NoState;
    public IEventSource<DebugTerminatedEventArgs> Terminated => NoTerminated;
    public IEventSource<DebugBreakpointStatus> BreakpointStatusChanged => NoStatus;
    public IEventSource<IDebugSession> Invalidated => NoInvalidated;

    private static InvalidOperationException Gone() => new("The debug session has ended.");

    public ValueTask<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken ct = default) => throw Gone();
    public ValueTask<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(int threadId, int startFrame = 0, int levels = 64, CancellationToken ct = default) => throw Gone();
    public ValueTask<IReadOnlyList<DebugScope>> GetScopesAsync(int frameId, CancellationToken ct = default) => throw Gone();
    public ValueTask<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, int? start = null, int? count = null, CancellationToken ct = default) => throw Gone();
    public ValueTask<DebugEvaluateResult> EvaluateAsync(string expression, int? frameId, string context = "repl", CancellationToken ct = default) => throw Gone();
    public ValueTask<DebugVariable> SetVariableAsync(int variablesReference, string name, string value, CancellationToken ct = default) => throw Gone();
    public ValueTask<IReadOnlyList<DebugCompletionItem>> CompletionsAsync(int? frameId, string text, int column, CancellationToken ct = default) => throw Gone();
    public ValueTask ContinueAsync(int? threadId = null, CancellationToken ct = default) => throw Gone();
    public ValueTask StepOverAsync(int? threadId = null, CancellationToken ct = default) => throw Gone();
    public ValueTask StepIntoAsync(int? threadId = null, CancellationToken ct = default) => throw Gone();
    public ValueTask StepOutAsync(int? threadId = null, CancellationToken ct = default) => throw Gone();
    public ValueTask PauseAsync(int? threadId = null, CancellationToken ct = default) => throw Gone();
    public ValueTask RestartAsync(CancellationToken ct = default) => throw Gone();
    public ValueTask StopAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<JsonElement?> CustomRequestAsync(string command, JsonElement? arguments = null, CancellationToken ct = default) => throw Gone();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
