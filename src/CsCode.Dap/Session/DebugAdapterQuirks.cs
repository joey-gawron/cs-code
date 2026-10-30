using CsCode.Core.Debugging;

namespace CsCode.Dap.Session;

/// <summary>
/// An exception filter the CLIENT implements for an adapter that has none for it, as a function breakpoint: GDB's native DAP offers Ada and
/// C++ filters only, so "Rust panics" is a breakpoint on <c>std::panicking::rust_panic</c> that CsCode adds to the function breakpoints while
/// the filter is enabled, and whose stops it reports as exceptions (reason <c>exception</c>, with the panic's message).
/// </summary>
/// <param name="Filter">Filter id (persisted with the user's choice, like an adapter's own filters).</param>
/// <param name="FunctionName">The function breakpoint that implements it, exactly as the adapter resolves it (for an adapter's own filter in
/// <see cref="DebugAdapterQuirks.BreakpointExceptionFilters"/>: what the adapter breaks on, for the record only).</param>
public sealed record SyntheticExceptionFilter(string Filter, string Label, string FunctionName, bool Default = true, string? Description = null);

/// <summary>How a hit condition the user typed reaches the adapter: <see cref="Wire"/> to send, or why it cannot be expressed.</summary>
public readonly record struct HitConditionMapping(string? Wire, string? Unsupported);

/// <summary>
/// One Debug Console input as an <c>evaluate</c> request, with a second form to try when the adapter rejects the first with an error
/// <see cref="FallbackWhen"/> accepts (any error when it is null).
/// </summary>
public sealed record ReplRequest(string Expression, string Context, ReplRequest? Fallback = null, Func<string, bool>? FallbackWhen = null);

/// <summary>
/// A Debug Console completion request rewritten for the adapter: the text and (0-based) column to send, and how each answer maps back onto
/// what the user typed (null drops it).
/// </summary>
public sealed record CompletionMapping(string Text, int Column, Func<DebugCompletionItem, DebugCompletionItem?> Map);

/// <summary>
/// What a built-in debug adapter does differently from what the client (and the user) expect, compensated in ONE place per adapter. The base
/// class changes nothing; <see cref="CsCode.Dap.Adapters.GdbAdapterQuirks"/> is the GDB (native DAP) set. A session gets the quirks of its
/// built-in debug type (<see cref="CsCode.Dap.Adapters.BuiltinDebugAdapter.Quirks"/>). Every hook runs on the debug lane and must be cheap.
/// </summary>
public class DebugAdapterQuirks
{
    public static DebugAdapterQuirks None { get; } = new();

    /// <summary>A Debug Console input (context <c>repl</c>) as the request to send.</summary>
    public virtual ReplRequest MapReplInput(string input) => new(input, "repl");

    /// <summary>The text a successful Debug Console evaluation shows.</summary>
    public virtual string MapReplResult(ReplRequest request, string result) => result;

    /// <summary>Debug Console completions rewritten for the adapter; null sends the text as typed.</summary>
    public virtual CompletionMapping? MapCompletions(string text, int column) => null;

    /// <summary>A hit condition as typed (<c>5</c>, <c>&gt;5</c>, <c>%3</c>…) → what the adapter understands, or why it cannot.</summary>
    public virtual HitConditionMapping MapHitCondition(string hitCondition) => new(hitCondition, null);

    /// <summary>Exception filters the client implements itself (as function breakpoints); listed after the adapter's own.</summary>
    public virtual IReadOnlyList<SyntheticExceptionFilter> SyntheticExceptionFilters => [];

    /// <summary>
    /// The adapter's OWN exception filters (by <see cref="SyntheticExceptionFilter.Filter"/> id) whose stops it reports as plain breakpoint stops:
    /// CodeLLDB's "rust_panic" is an LLDB breakpoint on <c>rust_panic$</c>, so its stop comes as reason <c>breakpoint</c> with the filter's
    /// breakpoint id (from the <c>setExceptionBreakpoints</c> response) in <c>hitBreakpointIds</c>. The session reports such a stop as the exception
    /// it stands for, described as a client-side filter's (<see cref="DescribeSyntheticStop"/>); the adapter keeps listing the filter itself.
    /// </summary>
    public virtual IReadOnlyList<SyntheticExceptionFilter> BreakpointExceptionFilters => [];

    /// <summary>
    /// The exception a stop of <paramref name="filter"/> stands for, from the program's recent output (a Rust panic prints its message before
    /// it reaches the breakpoint); null when the output does not tell yet.
    /// </summary>
    public virtual DebugExceptionInfo? DescribeSyntheticStop(SyntheticExceptionFilter filter, string recentOutput) => null;

    /// <summary>The category an <c>output</c> event is shown with (the adapter's own messages may arrive as program output).</summary>
    public virtual string MapOutputCategory(string category, string output) => category;

    /// <summary>The text an <c>output</c> event adds to the console (a logpoint message without its line break…).</summary>
    public virtual string MapOutputText(string category, string output) => output;

    /// <summary>A stack frame as the UI should see it (library code de-emphasised…).</summary>
    public virtual DebugStackFrame MapFrame(DebugStackFrame frame) => frame;

    /// <summary>
    /// After the adapter's <c>terminated</c>, keep collecting the program's output until none arrived for this long (zero: end at once). For an
    /// adapter that reads the program's output on a thread of its own and can deliver its last lines after <c>terminated</c>.
    /// </summary>
    public virtual TimeSpan TrailingOutputQuietPeriod => TimeSpan.Zero;

    /// <summary>The most <see cref="TrailingOutputQuietPeriod"/> may delay the end of a session.</summary>
    public virtual TimeSpan TrailingOutputLimit => TimeSpan.FromSeconds(2);

    /// <summary>
    /// The adapter runs a <c>noDebug</c> launch (Start Without Debugging, Ctrl+F5) under the debugger anyway (GDB ignores the flag): the session
    /// then sends it no breakpoints and no exception filters, so the program runs to its end as it would without a debugger.
    /// </summary>
    public virtual bool IgnoresNoDebug => false;

    /// <summary>
    /// The adapter answers breakpoint requests of a <c>noDebug</c> launch with an error (CodeLLDB: "Not supported in noDebug mode."): the session
    /// sends it none, rather than show every breakpoint as failed while the program runs.
    /// </summary>
    public virtual bool RefusesBreakpointsWithoutDebugging => false;
}
