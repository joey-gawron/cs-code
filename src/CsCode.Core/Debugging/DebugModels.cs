using System.Text.Json;

namespace CsCode.Core.Debugging;

/// <summary>
/// A launch.json entry (or programmatic equivalent). <see cref="Properties"/> holds the whole raw object (including
/// type/request/name) so adapter-specific keys round-trip untouched to the DAP launch/attach request.
/// </summary>
public sealed record DebugConfiguration(
    string Type,
    string Request,
    string Name,
    IReadOnlyDictionary<string, JsonElement> Properties,
    Uri? WorkspaceFolder = null,
    string? PreLaunchTask = null,
    string? PostDebugTask = null,
    bool NoDebug = false)
{
    public string? GetString(string key) =>
        Properties.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public bool IsAttach => string.Equals(Request, "attach", StringComparison.OrdinalIgnoreCase);

    public static DebugConfiguration FromJson(JsonElement obj, Uri? folder = null)
    {
        var props = obj.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        string S(string k, string d) => props.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : d;
        return new DebugConfiguration(S("type", ""), S("request", "launch"), S("name", S("type", "debug")), props, folder,
            props.TryGetValue("preLaunchTask", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
            props.TryGetValue("postDebugTask", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null);
    }
}

/// <summary>How to reach a debug adapter. Exactly the three VS Code descriptor kinds.</summary>
public abstract record DebugAdapterDescriptor;
public sealed record DebugAdapterExecutable(string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string>? Env = null, string? Cwd = null) : DebugAdapterDescriptor;
public sealed record DebugAdapterServer(string Host, int Port) : DebugAdapterDescriptor;
public sealed record DebugAdapterNamedPipe(string Path) : DebugAdapterDescriptor;

public sealed record DebugSource(string? Name, string? Path, int? SourceReference = null, string? PresentationHint = null)
{
    public Uri? ToUri() => Path is not null && Path.Length > 0 && Path[0] is '/' or '\\' || (Path is { Length: > 2 } && Path[1] == ':')
        ? new Uri(Path)
        : null;
}

public sealed record DebugThread(int Id, string Name);

/// <summary>Line/Column are 0-based here (the DAP client converts from the adapter's linesStartAt1/columnsStartAt1).</summary>
public sealed record DebugStackFrame(int Id, string Name, DebugSource? Source, int Line, int Column, int? EndLine = null, int? EndColumn = null, string? PresentationHint = null, string? ModuleId = null);

public sealed record DebugScope(string Name, int VariablesReference, bool Expensive, int? NamedVariables = null, int? IndexedVariables = null, string? PresentationHint = null);

public sealed record DebugVariable(string Name, string Value, string? Type = null, int VariablesReference = 0, string? EvaluateName = null, int? NamedVariables = null, int? IndexedVariables = null, string? MemoryReference = null);

public sealed record DebugEvaluateResult(string Result, string? Type = null, int VariablesReference = 0, int? NamedVariables = null, int? IndexedVariables = null, string? MemoryReference = null);

public sealed record DebugCompletionItem(string Label, string? Text, string? Type, int? Start, int? Length);

public sealed record DebugCapabilities(
    bool SupportsConfigurationDoneRequest = false,
    bool SupportsFunctionBreakpoints = false,
    bool SupportsConditionalBreakpoints = false,
    bool SupportsHitConditionalBreakpoints = false,
    bool SupportsLogPoints = false,
    bool SupportsEvaluateForHovers = false,
    bool SupportsSetVariable = false,
    bool SupportsCompletionsRequest = false,
    bool SupportsRestartRequest = false,
    bool SupportsTerminateRequest = false,
    bool SupportsStepBack = false,
    bool SupportsDataBreakpoints = false,
    bool SupportsReadMemory = false,
    bool SupportsDisassemble = false,
    bool SupportsExceptionInfo = false,
    bool SupportsDelayedStackTraceLoading = false,
    IReadOnlyList<ExceptionBreakpointFilter>? ExceptionBreakpointFilters = null);

public sealed record ExceptionBreakpointFilter(string Filter, string Label, bool Default = false, bool SupportsCondition = false, string? Description = null);

public enum DebugSessionState { Initializing, Running, Stopped, Terminated }

/// <summary>Aggregate state across sessions for context keys (debugState) and toolbar/status bar.</summary>
public enum DebugState { Inactive, Initializing, Running, Stopped }

public sealed record DebugStoppedEventArgs(IDebugSession Session, string Reason, int? ThreadId, bool AllThreadsStopped, string? Description = null, string? Text = null, IReadOnlyList<int>? HitBreakpointIds = null);
public sealed record DebugContinuedEventArgs(IDebugSession Session, int ThreadId, bool AllThreadsContinued);
public sealed record DebugOutputEventArgs(IDebugSession Session, string Category, string Output, DebugSource? Source = null, int? Line = null);
public sealed record DebugThreadEventArgs(IDebugSession Session, int ThreadId, string Reason);
public sealed record DebugTerminatedEventArgs(IDebugSession Session, int? ExitCode, string? Reason);
/// <summary>The adapter's verdict on one breakpoint (verified, moved to another line, rejected with a message).</summary>
public sealed record DebugBreakpointStatus(IDebugSession Session, string BreakpointId, bool Verified, int? ActualLine, string? Message);

/// <summary>Where the debugger is currently stopped (drives the yellow arrow/line highlight and editor reveal).</summary>
public sealed record DebugStopLocation(IDebugSession Session, int ThreadId, int FrameId, Uri? Uri, int Line, int Column, string Reason);
