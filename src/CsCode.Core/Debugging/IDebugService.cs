using System.Text.Json;
using CsCode.Core.Lifecycle;

namespace CsCode.Core.Debugging;

/// <summary>One live debug session (one adapter process/connection). Implemented by CsCode.Dap.</summary>
public interface IDebugSession : IAsyncDisposable
{
    string Id { get; }
    string Name { get; }
    DebugConfiguration Configuration { get; }
    DebugSessionState State { get; }
    IDebugSession? Parent { get; }
    DebugCapabilities Capabilities { get; }
    /// <summary>Thread that most recently stopped (the one the UI should show), if any.</summary>
    int? FocusedThreadId { get; }

    IEventSource<DebugStoppedEventArgs> Stopped { get; }
    IEventSource<DebugContinuedEventArgs> Continued { get; }
    IEventSource<DebugOutputEventArgs> Output { get; }
    IEventSource<DebugThreadEventArgs> ThreadChanged { get; }
    IEventSource<DebugSessionState> StateChanged { get; }
    IEventSource<DebugTerminatedEventArgs> Terminated { get; }
    IEventSource<DebugBreakpointStatus> BreakpointStatusChanged { get; }
    /// <summary>Adapter asked UI to refresh variables/stacks (DAP `invalidated`).</summary>
    IEventSource<IDebugSession> Invalidated { get; }

    ValueTask<IReadOnlyList<DebugThread>> GetThreadsAsync(CancellationToken ct = default);
    ValueTask<IReadOnlyList<DebugStackFrame>> GetStackTraceAsync(int threadId, int startFrame = 0, int levels = 64, CancellationToken ct = default);
    ValueTask<IReadOnlyList<DebugScope>> GetScopesAsync(int frameId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<DebugVariable>> GetVariablesAsync(int variablesReference, int? start = null, int? count = null, CancellationToken ct = default);
    ValueTask<DebugEvaluateResult> EvaluateAsync(string expression, int? frameId, string context = "repl", CancellationToken ct = default);
    ValueTask<DebugVariable> SetVariableAsync(int variablesReference, string name, string value, CancellationToken ct = default);
    ValueTask<IReadOnlyList<DebugCompletionItem>> CompletionsAsync(int? frameId, string text, int column, CancellationToken ct = default);

    ValueTask ContinueAsync(int? threadId = null, CancellationToken ct = default);
    ValueTask StepOverAsync(int? threadId = null, CancellationToken ct = default);
    ValueTask StepIntoAsync(int? threadId = null, CancellationToken ct = default);
    ValueTask StepOutAsync(int? threadId = null, CancellationToken ct = default);
    ValueTask PauseAsync(int? threadId = null, CancellationToken ct = default);
    ValueTask RestartAsync(CancellationToken ct = default);
    /// <summary>Graceful stop: terminate (if supported) or disconnect with terminateDebuggee for launch / detach for attach.</summary>
    ValueTask StopAsync(CancellationToken ct = default);
    ValueTask<JsonElement?> CustomRequestAsync(string command, JsonElement? arguments = null, CancellationToken ct = default);
}

public interface IDebugAdapterFactory
{
    /// <summary>Resolve how to start/reach the adapter for this configuration; null = not handled.</summary>
    ValueTask<DebugAdapterDescriptor?> CreateDescriptorAsync(DebugConfiguration configuration, CancellationToken ct);
}

public interface IDebugConfigurationProvider
{
    /// <summary>Fill in / validate a configuration before launch (vscode DebugConfigurationProvider.resolveDebugConfiguration); null aborts.</summary>
    ValueTask<DebugConfiguration?> ResolveAsync(Uri? folder, DebugConfiguration configuration, CancellationToken ct);
    /// <summary>Initial configurations to offer in "Add Configuration…" / when no launch.json exists.</summary>
    ValueTask<IReadOnlyList<DebugConfiguration>> ProvideInitialAsync(Uri? folder, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<DebugConfiguration>>([]);
}

public sealed record DebuggerType(string Type, string Label, IReadOnlyList<string> Languages, string? ExtensionId);

/// <summary>launch.json (JSONC, .cscode/ then .vscode/) loading, compounds, variable substitution.</summary>
public interface IDebugConfigurationService
{
    ValueTask<IReadOnlyList<DebugConfiguration>> GetLaunchConfigurationsAsync(Uri? folder = null, CancellationToken ct = default);
    /// <summary>Selected entry shown in the toolbar dropdown.</summary>
    DebugConfiguration? Selected { get; set; }
    IEventSource<DebugConfiguration?> SelectedChanged { get; }
    IEventSource<object?> ConfigurationsChanged { get; }
    /// <summary>Substitutes ${workspaceFolder}, ${file}, ${fileBasename}, ${relativeFile}, ${env:X}, ${config:x}, ${command:id}, ${input:id}, ${cwd}…</summary>
    ValueTask<DebugConfiguration> ResolveVariablesAsync(DebugConfiguration configuration, CancellationToken ct = default);
    /// <summary>Generates a launch.json for a debug type from contributions (initialConfigurations / snippets) and writes it.</summary>
    ValueTask<Uri> CreateLaunchJsonAsync(string debugType, Uri? folder = null, CancellationToken ct = default);
}

/// <summary>Everything the shell's debug UI binds to. Implemented by CsCode.Dap.</summary>
public interface IDebugService
{
    IReadOnlyList<IDebugSession> Sessions { get; }
    IDebugSession? ActiveSession { get; set; }
    DebugState State { get; }
    DebugStopLocation? StopLocation { get; }
    IBreakpointManager Breakpoints { get; }
    IDebugConfigurationService Configurations { get; }
    IReadOnlyList<DebuggerType> DebuggerTypes { get; }
    IReadOnlyList<WatchExpression> Watches { get; }

    ValueTask<IDebugSession?> StartAsync(DebugConfiguration configuration, CancellationToken ct = default);
    ValueTask StopAllAsync(CancellationToken ct = default);

    IDisposable RegisterAdapterFactory(string debugType, IDebugAdapterFactory factory);
    IDisposable RegisterConfigurationProvider(string debugType, IDebugConfigurationProvider provider);

    WatchExpression AddWatch(string expression);
    void RemoveWatch(WatchExpression watch);

    IEventSource<IDebugSession> SessionStarted { get; }
    IEventSource<IDebugSession> SessionTerminated { get; }
    IEventSource<IDebugSession?> ActiveSessionChanged { get; }
    IEventSource<DebugState> StateChanged { get; }
    IEventSource<DebugStopLocation?> StopLocationChanged { get; }
    /// <summary>Output from any session (Debug Console).</summary>
    IEventSource<DebugOutputEventArgs> Output { get; }
}

public sealed class WatchExpression(string expression)
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Expression { get; set; } = expression;
}

public sealed record SourceBreakpoint(string Id, Uri Uri, int Line, int? Column = null, string? Condition = null, string? HitCondition = null, string? LogMessage = null, bool Enabled = true);
public sealed record FunctionBreakpoint(string Id, string Name, string? Condition = null, string? HitCondition = null, bool Enabled = true);
public sealed record ExceptionFilterSetting(string Filter, string Label, bool Enabled, string? Condition = null);

public sealed record BreakpointsChangedEventArgs(IReadOnlyCollection<Uri> AffectedUris, bool FunctionsOrExceptionsChanged);

/// <summary>Source of truth for breakpoints (persisted in workspace state). DAP sessions mirror it to their adapters.</summary>
public interface IBreakpointManager
{
    IReadOnlyList<SourceBreakpoint> SourceBreakpoints { get; }
    IReadOnlyList<SourceBreakpoint> GetForUri(Uri uri);
    IReadOnlyList<FunctionBreakpoint> FunctionBreakpoints { get; }
    IReadOnlyList<ExceptionFilterSetting> ExceptionFilters { get; }

    /// <summary>Adds if absent, removes if present at that (uri,line); returns true if now present. Line is 0-based.</summary>
    bool Toggle(Uri uri, int line);
    SourceBreakpoint Add(Uri uri, int line, string? condition = null, string? hitCondition = null, string? logMessage = null);
    void Update(SourceBreakpoint updated);
    void Remove(string id);
    void RemoveAll();
    void SetEnabledAll(bool enabled);
    FunctionBreakpoint AddFunction(string name, string? condition = null);
    void RemoveFunction(string id);
    void SetExceptionFilters(IReadOnlyList<ExceptionFilterSetting> filters);

    /// <summary>Latest adapter verdict for a breakpoint (verified/moved line/failed); null if no session reported yet.</summary>
    DebugBreakpointStatus? GetStatus(string breakpointId);
    void ReportStatus(DebugBreakpointStatus status);
    void ClearStatuses(string? sessionId = null);

    /// <summary>Documents whose lines moved: the editor calls this so breakpoint lines track edits.</summary>
    void ApplyLineShift(Uri uri, int fromLine, int delta);

    IEventSource<BreakpointsChangedEventArgs> Changed { get; }
}
