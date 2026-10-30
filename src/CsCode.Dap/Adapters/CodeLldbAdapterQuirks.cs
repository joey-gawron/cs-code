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
/// sent as <c>/cmd &lt;command&gt;</c>.</item>
/// <item><b>Frames</b>: the Rust standard library (<c>/rustc/&lt;commit&gt;/…</c> or the rust-src component) and code CodeLLDB can only show
/// as disassembly (a <c>sourceReference</c> frame: libc, frames without debug information) are de-emphasised, so a pause or a panic shows the
/// user's code first, as with GDB.</item>
/// <item><b>Start Without Debugging</b>: CodeLLDB honours <c>noDebug</c> but then refuses breakpoint requests with an error: none are sent.</item>
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
}
