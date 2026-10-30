using System.Globalization;
using CsCode.Core.Debugging;
using CsCode.Dap.Session;

namespace CsCode.Dap.Adapters;

/// <summary>
/// What CodeLLDB v1.12.3 (<c>codelldb</c> on stdio) does differently from what a Visual Studio user expects, and how CsCode compensates. Taken from
/// CodeLLDB's own sources at the pinned tag (<c>src/codelldb/src/…</c>); see docs/DEBUGGING-RUST-TS.md, "Rust through CodeLLDB":
/// <list type="bullet">
/// <item><b>Rust panics</b>: CodeLLDB's own "Rust: on panic" filter (<c>rust_panic</c>, on by default, offered when the session's source
/// languages include Rust) is an LLDB breakpoint on <c>rust_panic$</c>, so its stop arrives as reason <c>breakpoint</c>. It is reported as the
/// exception "Rust panics" with the panic's message from the program's output, as GDB's client-side filter is.</item>
/// <item><b>Hit counts</b>: CodeLLDB reads a bare <c>N</c> as <c>&gt;= N</c> (every hit from the Nth on); js-debug and Visual Studio's "is
/// equal to" mean the Nth hit only, so a bare number is sent as <c>=N</c>. <c>&lt;</c>, <c>&lt;=</c>, <c>=</c>, <c>&gt;=</c>, <c>&gt;</c> and
/// <c>%</c> are CodeLLDB's own and pass unchanged.</item>
/// <item><b>Debug Console</b>: the provider puts CodeLLDB in its "evaluate" console mode (input is an expression; a backtick or <c>/cmd </c>
/// runs an LLDB command), the accepted Rust default; <c>-exec &lt;command&gt;</c> (VS Code's C++ convention, which the GDB adapter takes) is
/// sent as <c>/cmd &lt;command&gt;</c>. Completions (observed): an expression is completed as LLDB's <c>frame variable</c> command, and the
/// answers' positions are put back into what was typed.</item>
/// <item><b>Frames</b>: the Rust standard library (<c>/rustc/&lt;commit&gt;/…</c> or the rust-src component) and code CodeLLDB can only show
/// as disassembly (a <c>sourceReference</c> frame: libc, frames without debug information) are de-emphasised, so a pause or a panic shows the
/// user's code first, as with GDB.</item>
/// <item><b>Start Without Debugging</b>: CodeLLDB honours <c>noDebug</c> but then refuses breakpoint requests with an error: none are sent.</item>
/// <item><b>Paging</b> (observed with the real v1.12.3): <c>variables</c> ignores <c>start</c>/<c>count</c>/<c>filter</c>; the session asks once
/// and pages itself.</item>
/// <item><b>Pause</b> (observed): the stop of a pause is reported as the exception "signal SIGSTOP"; reported as a pause.</item>
/// </list>
/// </summary>
public sealed class CodeLldbAdapterQuirks : DebugAdapterQuirks
{
    public static CodeLldbAdapterQuirks Instance { get; } = new();

    /// <summary>CodeLLDB's exception filter id for Rust panics.</summary>
    public const string RustPanicFilter = "rust_panic";

    private static readonly IReadOnlyList<SyntheticExceptionFilter> PanicFilters =
    [
        new(RustPanicFilter, "Rust panics", "rust_panic$", Default: true, Description: "Break when a Rust thread panics, before the panic unwinds or aborts."),
    ];

    /// <summary>The prefix VS Code's C++ debugger uses for debugger commands; CodeLLDB's own is <c>/cmd </c> (or a backtick).</summary>
    public const string ExecPrefix = "-exec ";
    public const string CommandPrefix = "/cmd ";

    public override IReadOnlyList<SyntheticExceptionFilter> BreakpointExceptionFilters => PanicFilters;

    public override DebugExceptionInfo? DescribeSyntheticStop(SyntheticExceptionFilter filter, string recentOutput) =>
        filter.Filter == RustPanicFilter ? GdbAdapterQuirks.DescribeRustPanic(recentOutput) : null;

    public override ReplRequest MapReplInput(string input)
    {
        var text = input.Trim();
        return text.StartsWith(ExecPrefix, StringComparison.Ordinal)
            ? new ReplRequest(CommandPrefix + text[ExecPrefix.Length..].TrimStart(), "repl")
            : new ReplRequest(input, "repl");
    }

    /// <summary>LLDB's command whose completion completes a variable path (locals, fields, elements): what an expression is completed as.</summary>
    public const string VariableCompletionCommand = "frame variable ";

    /// <summary>
    /// CodeLLDB (observed, and its <c>handle_completions</c>) completes nothing in evaluate mode but an LLDB command after a backtick or
    /// <c>/cmd </c> ("expression completions" is a TODO there), and answers with a <c>start</c> counted 0-based inside the command with its prefix
    /// stripped ("`fram" → start 0, length 4), which would replace the wrong characters. So: an expression is completed as the command
    /// <c>frame variable &lt;expression&gt;</c> (LLDB completes locals and their members: "p." → p.x, p.y), <c>-exec </c> as <c>/cmd </c>, and
    /// only the text before the caret is sent, so every answer's overlap (<c>length</c>, the end of what was typed matching the start of the
    /// completion) ends at the caret: its start is put back from that.
    /// </summary>
    public override CompletionMapping? MapCompletions(string text, int column)
    {
        var typed = text[..Math.Clamp(column, 0, text.Length)];
        var trimmed = typed.TrimStart();
        var sent = typed.StartsWith('`') || typed.StartsWith(CommandPrefix, StringComparison.Ordinal) ? typed
            : trimmed.StartsWith(ExecPrefix, StringComparison.Ordinal) ? CommandPrefix + trimmed[ExecPrefix.Length..].TrimStart()
            : CommandPrefix + VariableCompletionCommand + typed;
        var caret = typed.Length;
        return new CompletionMapping(sent, sent.Length, item => item.Length is { } length && length <= caret ? item with { Start = caret - length } : item);
    }

    public override HitConditionMapping MapHitCondition(string hitCondition)
    {
        var text = hitCondition.Trim();
        return text.Length is > 0 and <= 9 && text.All(char.IsAsciiDigit)
            ? new HitConditionMapping("=" + int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), null)
            : new HitConditionMapping(hitCondition, null);
    }

    public override DebugStackFrame MapFrame(DebugStackFrame frame) => GdbAdapterQuirks.DeemphasizeLibraryFrame(frame);

    /// <summary>In a <c>noDebug</c> launch CodeLLDB answers every breakpoint request with "Not supported in noDebug mode." (its request dispatch).</summary>
    public override bool RefusesBreakpointsWithoutDebugging => true;

    /// <summary>
    /// CodeLLDB's <c>variables</c> handler reads only the reference: <c>start</c>, <c>count</c> and <c>filter</c> are ignored and every child comes
    /// back (a 250-element Vec asked for [240, 250) answered all 250 and its <c>[raw]</c> row); it never announces <c>indexedVariables</c>.
    /// </summary>
    public override bool IgnoresVariablePaging => true;

    /// <summary>LLDB's halt on Linux and macOS: the stopped event of a pause is reason "exception", description "signal SIGSTOP".</summary>
    public const string PauseSignalDescription = "signal SIGSTOP";

    /// <summary>
    /// CodeLLDB's <c>pause</c> stops the process (LLDB sends it SIGSTOP) and its stop handler maps every signal stop to reason <c>exception</c>
    /// with the signal as the description; it has no case for a pause it asked for. Only that signal, and only while our pause is outstanding,
    /// is the pause: a real signal (SIGSEGV…) stays an exception.
    /// </summary>
    public override bool IsPauseStop(string reason, string? description) => reason == "exception" && description == PauseSignalDescription;
}
