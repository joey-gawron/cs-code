using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CsCode.Dap.Protocol;

// Hand-written subset of the Debug Adapter Protocol 1.6x (https://microsoft.github.io/debug-adapter-protocol/specification):
// every request/event/type this client sends or understands. Design rules:
//   * init-only records with nullable members: a field the adapter omits is simply null, unknown fields are ignored;
//   * DAP's "string union" enums (stop reasons, output categories, variable presentation hints...) stay strings, so an
//     adapter that invents a new value can never make deserialisation fail;
//   * names are prefixed "Dap" so they never clash with the 0-based CsCode.Core.Debugging models they are mapped to.
// Lines/columns in these types are WIRE values (1-based or 0-based per the negotiated initialize arguments).

public static class DapJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static T? Deserialize<T>(JsonElement element) where T : class =>
        element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : element.Deserialize<T>(Options);

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}

// ---------------------------------------------------------------- shared types

public sealed record DapSource
{
    public string? Name { get; init; }
    public string? Path { get; init; }
    public int? SourceReference { get; init; }
    public string? PresentationHint { get; init; }
    public string? Origin { get; init; }
    public JsonElement? AdapterData { get; init; }
}

public sealed record DapSourceBreakpoint
{
    public int Line { get; init; }
    public int? Column { get; init; }
    public string? Condition { get; init; }
    public string? HitCondition { get; init; }
    public string? LogMessage { get; init; }
}

public sealed record DapFunctionBreakpoint
{
    public string Name { get; init; } = "";
    public string? Condition { get; init; }
    public string? HitCondition { get; init; }
}

public sealed record DapDataBreakpoint
{
    public string DataId { get; init; } = "";
    public string? AccessType { get; init; }
    public string? Condition { get; init; }
    public string? HitCondition { get; init; }
}

public sealed record DapInstructionBreakpoint
{
    public string InstructionReference { get; init; } = "";
    public int? Offset { get; init; }
    public string? Condition { get; init; }
    public string? HitCondition { get; init; }
}

public sealed record DapExceptionFilterOptions
{
    public string FilterId { get; init; } = "";
    public string? Condition { get; init; }
}

public sealed record DapBreakpoint
{
    public int? Id { get; init; }
    public bool Verified { get; init; }
    public string? Message { get; init; }
    public DapSource? Source { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
    public string? InstructionReference { get; init; }
    public string? Reason { get; init; }
}

public sealed record DapExceptionBreakpointsFilter
{
    public string Filter { get; init; } = "";
    public string Label { get; init; } = "";
    public string? Description { get; init; }
    public bool? Default { get; init; }
    public bool? SupportsCondition { get; init; }
    public string? ConditionDescription { get; init; }
}

public sealed record DapCapabilities
{
    public bool? SupportsConfigurationDoneRequest { get; init; }
    public bool? SupportsFunctionBreakpoints { get; init; }
    public bool? SupportsConditionalBreakpoints { get; init; }
    public bool? SupportsHitConditionalBreakpoints { get; init; }
    public bool? SupportsEvaluateForHovers { get; init; }
    public List<DapExceptionBreakpointsFilter>? ExceptionBreakpointFilters { get; init; }
    public bool? SupportsStepBack { get; init; }
    public bool? SupportsSetVariable { get; init; }
    public bool? SupportsRestartFrame { get; init; }
    public bool? SupportsGotoTargetsRequest { get; init; }
    public bool? SupportsStepInTargetsRequest { get; init; }
    public bool? SupportsCompletionsRequest { get; init; }
    public List<string>? CompletionTriggerCharacters { get; init; }
    public bool? SupportsModulesRequest { get; init; }
    public bool? SupportsRestartRequest { get; init; }
    public bool? SupportsExceptionOptions { get; init; }
    public bool? SupportsValueFormattingOptions { get; init; }
    public bool? SupportsExceptionInfoRequest { get; init; }
    public bool? SupportTerminateDebuggee { get; init; }
    public bool? SupportSuspendDebuggee { get; init; }
    public bool? SupportsDelayedStackTraceLoading { get; init; }
    public bool? SupportsLoadedSourcesRequest { get; init; }
    public bool? SupportsLogPoints { get; init; }
    public bool? SupportsTerminateThreadsRequest { get; init; }
    public bool? SupportsSetExpression { get; init; }
    public bool? SupportsTerminateRequest { get; init; }
    public bool? SupportsDataBreakpoints { get; init; }
    public bool? SupportsReadMemoryRequest { get; init; }
    public bool? SupportsWriteMemoryRequest { get; init; }
    public bool? SupportsDisassembleRequest { get; init; }
    public bool? SupportsCancelRequest { get; init; }
    public bool? SupportsBreakpointLocationsRequest { get; init; }
    public bool? SupportsClipboardContext { get; init; }
    public bool? SupportsSteppingGranularity { get; init; }
    public bool? SupportsInstructionBreakpoints { get; init; }
    public bool? SupportsExceptionFilterOptions { get; init; }
    public bool? SupportsSingleThreadExecutionRequests { get; init; }
}

public sealed record DapThread
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
}

public sealed record DapStackFrame
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public DapSource? Source { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
    public bool? CanRestart { get; init; }
    public string? InstructionPointerReference { get; init; }
    public JsonElement? ModuleId { get; init; }
    public string? PresentationHint { get; init; }
}

public sealed record DapScope
{
    public string Name { get; init; } = "";
    public string? PresentationHint { get; init; }
    public int VariablesReference { get; init; }
    public int? NamedVariables { get; init; }
    public int? IndexedVariables { get; init; }
    public bool Expensive { get; init; }
    public DapSource? Source { get; init; }
}

public sealed record DapVariable
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string? Type { get; init; }
    public int VariablesReference { get; init; }
    public int? NamedVariables { get; init; }
    public int? IndexedVariables { get; init; }
    public string? MemoryReference { get; init; }
    public string? EvaluateName { get; init; }
}

public sealed record DapCompletionItem
{
    public string Label { get; init; } = "";
    public string? Text { get; init; }
    public string? SortText { get; init; }
    public string? Type { get; init; }
    public int? Start { get; init; }
    public int? Length { get; init; }
}

public sealed record DapModule
{
    public JsonElement Id { get; init; }
    public string Name { get; init; } = "";
    public string? Path { get; init; }
    public bool? IsOptimized { get; init; }
    public bool? IsUserCode { get; init; }
    public string? Version { get; init; }
    public string? SymbolStatus { get; init; }
    public string? SymbolFilePath { get; init; }
    public string? DateTimeStamp { get; init; }
    public string? AddressRange { get; init; }
}

public sealed record DapExceptionDetails
{
    public string? Message { get; init; }
    public string? TypeName { get; init; }
    public string? FullTypeName { get; init; }
    public string? EvaluateName { get; init; }
    public string? StackTrace { get; init; }
    public List<DapExceptionDetails>? InnerException { get; init; }
}

public sealed record DapDisassembledInstruction
{
    public string Address { get; init; } = "";
    public string? InstructionBytes { get; init; }
    public string Instruction { get; init; } = "";
    public string? Symbol { get; init; }
    public DapSource? Location { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
}

/// <summary>Error payload of a failed response (<c>body.error</c>).</summary>
public sealed record DapMessage
{
    public int Id { get; init; }
    public string Format { get; init; } = "";
    public Dictionary<string, string>? Variables { get; init; }
    public bool? SendTelemetry { get; init; }
    public bool? ShowUser { get; init; }
    public string? Url { get; init; }
    public string? UrlLabel { get; init; }
}

// ---------------------------------------------------------------- request arguments / response bodies

public sealed record DapInitializeArguments
{
    public string? ClientId { get; init; }
    public string? ClientName { get; init; }
    public string AdapterId { get; init; } = "";
    public string? Locale { get; init; }
    public bool? LinesStartAt1 { get; init; }
    public bool? ColumnsStartAt1 { get; init; }
    public string? PathFormat { get; init; }
    public bool? SupportsVariableType { get; init; }
    public bool? SupportsVariablePaging { get; init; }
    public bool? SupportsRunInTerminalRequest { get; init; }
    public bool? SupportsMemoryReferences { get; init; }
    public bool? SupportsProgressReporting { get; init; }
    public bool? SupportsInvalidatedEvent { get; init; }
    public bool? SupportsMemoryEvent { get; init; }
    public bool? SupportsStartDebuggingRequest { get; init; }
    public bool? SupportsArgsCanBeInterpretedByShell { get; init; }
}

public sealed record DapSetBreakpointsArguments
{
    public DapSource Source { get; init; } = new();
    public List<DapSourceBreakpoint>? Breakpoints { get; init; }
    public bool? SourceModified { get; init; }
}

public sealed record DapBreakpointsResponse
{
    public List<DapBreakpoint> Breakpoints { get; init; } = [];
}

public sealed record DapSetFunctionBreakpointsArguments
{
    public List<DapFunctionBreakpoint> Breakpoints { get; init; } = [];
}

public sealed record DapSetExceptionBreakpointsArguments
{
    public List<string> Filters { get; init; } = [];
    public List<DapExceptionFilterOptions>? FilterOptions { get; init; }
}

public sealed record DapSetDataBreakpointsArguments
{
    public List<DapDataBreakpoint> Breakpoints { get; init; } = [];
}

public sealed record DapSetInstructionBreakpointsArguments
{
    public List<DapInstructionBreakpoint> Breakpoints { get; init; } = [];
}

public sealed record DapDataBreakpointInfoArguments
{
    public int? VariablesReference { get; init; }
    public string Name { get; init; } = "";
    public int? FrameId { get; init; }
}

public sealed record DapDataBreakpointInfoResponse
{
    public string? DataId { get; init; }
    public string Description { get; init; } = "";
    public List<string>? AccessTypes { get; init; }
    public bool? CanPersist { get; init; }
}

public sealed record DapThreadsResponse
{
    public List<DapThread> Threads { get; init; } = [];
}

public sealed record DapStackTraceArguments
{
    public int ThreadId { get; init; }
    public int? StartFrame { get; init; }
    public int? Levels { get; init; }
}

public sealed record DapStackTraceResponse
{
    public List<DapStackFrame> StackFrames { get; init; } = [];
    public int? TotalFrames { get; init; }
}

public sealed record DapScopesArguments
{
    public int FrameId { get; init; }
}

public sealed record DapScopesResponse
{
    public List<DapScope> Scopes { get; init; } = [];
}

public sealed record DapVariablesArguments
{
    public int VariablesReference { get; init; }
    public string? Filter { get; init; }
    public int? Start { get; init; }
    public int? Count { get; init; }
}

public sealed record DapVariablesResponse
{
    public List<DapVariable> Variables { get; init; } = [];
}

public sealed record DapSetVariableArguments
{
    public int VariablesReference { get; init; }
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
}

public sealed record DapSetVariableResponse
{
    public string Value { get; init; } = "";
    public string? Type { get; init; }
    public int? VariablesReference { get; init; }
    public int? NamedVariables { get; init; }
    public int? IndexedVariables { get; init; }
}

public sealed record DapSetExpressionArguments
{
    public string Expression { get; init; } = "";
    public string Value { get; init; } = "";
    public int? FrameId { get; init; }
}

public sealed record DapEvaluateArguments
{
    public string Expression { get; init; } = "";
    public int? FrameId { get; init; }
    public string? Context { get; init; }
}

public sealed record DapEvaluateResponse
{
    public string Result { get; init; } = "";
    public string? Type { get; init; }
    public int VariablesReference { get; init; }
    public int? NamedVariables { get; init; }
    public int? IndexedVariables { get; init; }
    public string? MemoryReference { get; init; }
}

public sealed record DapCompletionsArguments
{
    public int? FrameId { get; init; }
    public string Text { get; init; } = "";
    public int Column { get; init; }
    public int? Line { get; init; }
}

public sealed record DapCompletionsResponse
{
    public List<DapCompletionItem> Targets { get; init; } = [];
}

public sealed record DapContinueArguments
{
    public int ThreadId { get; init; }
    public bool? SingleThread { get; init; }
}

public sealed record DapContinueResponse
{
    public bool? AllThreadsContinued { get; init; }
}

/// <summary>Shared by next / stepIn / stepOut / stepBack.</summary>
public sealed record DapStepArguments
{
    public int ThreadId { get; init; }
    public bool? SingleThread { get; init; }
    public string? Granularity { get; init; }
}

public sealed record DapPauseArguments
{
    public int ThreadId { get; init; }
}

public sealed record DapSourceArguments
{
    public DapSource? Source { get; init; }
    public int SourceReference { get; init; }
}

public sealed record DapSourceResponse
{
    public string Content { get; init; } = "";
    public string? MimeType { get; init; }
}

public sealed record DapTerminateArguments
{
    public bool? Restart { get; init; }
}

public sealed record DapDisconnectArguments
{
    public bool? Restart { get; init; }
    public bool? TerminateDebuggee { get; init; }
    public bool? SuspendDebuggee { get; init; }
}

public sealed record DapExceptionInfoArguments
{
    public int ThreadId { get; init; }
}

public sealed record DapExceptionInfoResponse
{
    public string ExceptionId { get; init; } = "";
    public string? Description { get; init; }
    public string BreakMode { get; init; } = "";
    public DapExceptionDetails? Details { get; init; }
}

public sealed record DapModulesArguments
{
    public int? StartModule { get; init; }
    public int? ModuleCount { get; init; }
}

public sealed record DapModulesResponse
{
    public List<DapModule> Modules { get; init; } = [];
    public int? TotalModules { get; init; }
}

public sealed record DapLoadedSourcesResponse
{
    public List<DapSource> Sources { get; init; } = [];
}

public sealed record DapReadMemoryArguments
{
    public string MemoryReference { get; init; } = "";
    public int? Offset { get; init; }
    public int Count { get; init; }
}

public sealed record DapReadMemoryResponse
{
    public string Address { get; init; } = "";
    public int? UnreadableBytes { get; init; }
    public string? Data { get; init; }
}

public sealed record DapDisassembleArguments
{
    public string MemoryReference { get; init; } = "";
    public int? Offset { get; init; }
    public int? InstructionOffset { get; init; }
    public int InstructionCount { get; init; }
    public bool? ResolveSymbols { get; init; }
}

public sealed record DapDisassembleResponse
{
    public List<DapDisassembledInstruction> Instructions { get; init; } = [];
}

public sealed record DapRestartArguments
{
    /// <summary>The (resolved) launch/attach arguments; adapters that implement <c>restart</c> typically re-read them from here.</summary>
    public JsonElement? Arguments { get; init; }
}

public sealed record DapCancelArguments
{
    public int? RequestId { get; init; }
    public string? ProgressId { get; init; }
}

// ---------------------------------------------------------------- reverse requests

public sealed record DapRunInTerminalArguments
{
    public string? Kind { get; init; }
    public string? Title { get; init; }
    public string Cwd { get; init; } = "";
    public List<string> Args { get; init; } = [];
    /// <summary>Environment overrides; a JSON null value means "remove this variable".</summary>
    public Dictionary<string, string?>? Env { get; init; }
    public bool? ArgsCanBeInterpretedByShell { get; init; }
}

public sealed record DapRunInTerminalResponse
{
    public int? ProcessId { get; init; }
    public int? ShellProcessId { get; init; }
}

public sealed record DapStartDebuggingArguments
{
    public JsonObject Configuration { get; init; } = new();
    public string Request { get; init; } = "launch";
}

// ---------------------------------------------------------------- events

public sealed record DapStoppedEvent
{
    public string Reason { get; init; } = "";
    public string? Description { get; init; }
    public int? ThreadId { get; init; }
    public bool? PreserveFocusHint { get; init; }
    public string? Text { get; init; }
    public bool? AllThreadsStopped { get; init; }
    public List<int>? HitBreakpointIds { get; init; }
}

public sealed record DapContinuedEvent
{
    public int ThreadId { get; init; }
    public bool? AllThreadsContinued { get; init; }
}

public sealed record DapExitedEvent
{
    public int ExitCode { get; init; }
}

public sealed record DapTerminatedEvent
{
    public JsonElement? Restart { get; init; }
}

public sealed record DapThreadEvent
{
    public string Reason { get; init; } = "";
    public int ThreadId { get; init; }
}

public sealed record DapOutputEvent
{
    public string? Category { get; init; }
    public string Output { get; init; } = "";
    public string? Group { get; init; }
    public int? VariablesReference { get; init; }
    public DapSource? Source { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public JsonElement? Data { get; init; }
}

public sealed record DapBreakpointEvent
{
    public string Reason { get; init; } = "";
    public DapBreakpoint Breakpoint { get; init; } = new();
}

public sealed record DapModuleEvent
{
    public string Reason { get; init; } = "";
    public DapModule Module { get; init; } = new();
}

public sealed record DapLoadedSourceEvent
{
    public string Reason { get; init; } = "";
    public DapSource Source { get; init; } = new();
}

public sealed record DapProcessEvent
{
    public string Name { get; init; } = "";
    public long? SystemProcessId { get; init; }
    public bool? IsLocalProcess { get; init; }
    public string? StartMethod { get; init; }
    public int? PointerSize { get; init; }
}

public sealed record DapCapabilitiesEvent
{
    public JsonObject Capabilities { get; init; } = new();
}

public sealed record DapProgressStartEvent
{
    public string ProgressId { get; init; } = "";
    public string Title { get; init; } = "";
    public int? RequestId { get; init; }
    public bool? Cancellable { get; init; }
    public string? Message { get; init; }
    public double? Percentage { get; init; }
}

public sealed record DapProgressUpdateEvent
{
    public string ProgressId { get; init; } = "";
    public string? Message { get; init; }
    public double? Percentage { get; init; }
}

public sealed record DapProgressEndEvent
{
    public string ProgressId { get; init; } = "";
    public string? Message { get; init; }
}

public sealed record DapInvalidatedEvent
{
    public List<string>? Areas { get; init; }
    public int? ThreadId { get; init; }
    public int? StackFrameId { get; init; }
}

public sealed record DapMemoryEvent
{
    public string MemoryReference { get; init; } = "";
    public int Offset { get; init; }
    public int Count { get; init; }
}
