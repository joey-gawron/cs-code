using System.Globalization;
using System.Text.RegularExpressions;
using CsCode.Core.Debugging;
using CsCode.Dap.Session;

namespace CsCode.Dap.Adapters;

/// <summary>
/// What GDB's native DAP interpreter (<c>gdb -i=dap</c>, checked against GDB 18.1's <c>/usr/share/gdb/python/gdb/dap</c>) does differently
/// from what a Visual Studio user expects, and how CsCode compensates (docs/DEBUGGING-RUST-TS.md, "GDB quirks"):
/// <list type="bullet">
/// <item><b>Debug Console</b>: GDB runs <c>evaluate</c> with context <c>repl</c> as a GDB COMMAND (<c>x * 2</c> fails, <c>print x * 2</c>
/// prints <c>$1 = 104</c>). Input is evaluated as an EXPRESSION (context <c>watch</c>: an expandable value); <c>-exec &lt;command&gt;</c> (VS
/// Code's C++ convention) or a leading backtick runs a GDB command, and an input that is not an expression but starts with a GDB command word
/// (<c>info locals</c>, <c>p x</c>, <c>bt</c>…) is retried as one. Completions are GDB's command completions, so an expression is completed as
/// <c>print &lt;expression&gt;</c> and mapped back.</item>
/// <item><b>Hit counts</b> are GDB IGNORE counts (<c>hitCondition</c> "3" ignores three hits and stops at the fourth, then on every later
/// hit): <c>N</c>/<c>=N</c>/<c>==N</c> → ignore N−1, <c>&gt;N</c> → N, <c>&gt;=N</c> → N−1; <c>%N</c>, <c>&lt;N</c> cannot be expressed and are
/// reported unsupported rather than sent (GDB would reject them and the breakpoint would never be set).</item>
/// <item><b>Rust panics</b>: GDB's exception filters are Ada and C++ only. "Rust panics" (on by default, like CodeLLDB's "Rust: on panic") is a
/// breakpoint on <c>std::panicking::rust_panic</c> — GDB 18 does NOT resolve the bare <c>rust_panic</c> through DAP (its explicit-location
/// lookup does not match Rust paths outside the Rust language) — reported as an exception with the panic's message from the output.</item>
/// <item><b>Output</b>: the program's stdout AND stderr and GDB's own messages share one pipe and all arrive as <c>stdout</c>; GDB's messages
/// ("Breakpoint 1 (…) pending.", "[Thread … exited]"…) are shown as debugger messages (<c>console</c>). Logpoint messages come without a line
/// break. The program's last lines can arrive AFTER <c>terminated</c> (GDB reads the pipe on a thread of its own): the session waits for the
/// output to go quiet.</item>
/// <item><b>Frames</b> of the Rust standard library (<c>/rustc/&lt;commit&gt;/library/…</c> or the rust-src component) and of code whose source
/// is not on disk (libc: GDB then hands out a <c>sourceReference</c> it cannot serve) are de-emphasised: the Call Stack folds them into
/// [External Code] and a stop (pause, panic) shows the first frame of the user's code.</item>
/// </list>
/// </summary>
public sealed partial class GdbAdapterQuirks : DebugAdapterQuirks
{
    public static GdbAdapterQuirks Instance { get; } = new();

    /// <summary>The function GDB 18 resolves for Rust's panic entry point (the bare <c>rust_panic</c> stays pending through DAP).</summary>
    public const string RustPanicFunction = "std::panicking::rust_panic";
    public const string RustPanicFilter = "rust_panic";

    private static readonly IReadOnlyList<SyntheticExceptionFilter> Filters =
    [
        new(RustPanicFilter, "Rust panics", RustPanicFunction, Default: true, Description: "Break when a Rust thread panics, before the panic unwinds or aborts."),
    ];

    /// <summary>Command prefixes that send the rest of the input to GDB as a command.</summary>
    public static readonly IReadOnlyList<string> CommandPrefixes = ["-exec ", "`"];

    // GDB commands a user may type in the Debug Console out of habit: when such an input is not an expression it is run as a command.
    private static readonly HashSet<string> CommandWords = new(StringComparer.Ordinal)
    {
        "info", "i", "p", "print", "output", "call", "ptype", "whatis", "x", "bt", "backtrace", "where", "frame", "f", "list", "l", "disassemble",
        "display", "undisplay", "set", "show", "help", "thread", "explore", "printf", "echo", "pipe", "maint", "maintenance", "python", "py",
    };

    public override IReadOnlyList<SyntheticExceptionFilter> SyntheticExceptionFilters => Filters;

    public override TimeSpan TrailingOutputQuietPeriod => TimeSpan.FromMilliseconds(150);

    /// <summary>GDB's <c>launch</c> takes no <c>noDebug</c>: Ctrl+F5 would stop at breakpoints and panics.</summary>
    public override bool IgnoresNoDebug => true;

    // ------------------------------------------------------------------ Debug Console

    public override ReplRequest MapReplInput(string input)
    {
        var text = input.Trim();
        foreach (var prefix in CommandPrefixes)
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return new ReplRequest(text[prefix.Length..].TrimStart(), "repl");
        var first = FirstWord(text);
        if (first is null || !CommandWords.Contains(first)) return new ReplRequest(text, "watch");
        // Retried as a command only when it is no expression at all ("p x", "info locals": a syntax error), or is just a command word that
        // names no variable ("bt"): "x + 1" with an undefined x keeps its own error instead of running GDB's x (examine memory) command.
        var single = first.Length == text.Length && first.Length > 1;
        return new ReplRequest(text, "watch", new ReplRequest(text, "repl"),
            error => IsSyntaxError(error) || (single && error.TrimEnd('.') == $"No symbol '{first}' in current context"));
    }

    /// <summary>GDB 18's parsers word it differently: "A syntax error in expression, near `x'." (C), "Syntax error near 's'" (Rust).</summary>
    private static bool IsSyntaxError(string error) =>
        error.StartsWith("A syntax error in expression", StringComparison.Ordinal) || error.StartsWith("Syntax error", StringComparison.Ordinal);

    private static string? FirstWord(string text)
    {
        var end = 0;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
        // "p.x" or "x(1)" is an expression; "p x" / "info locals" / "bt" (the whole input) are commands
        return end > 0 && (end == text.Length || char.IsWhiteSpace(text[end])) ? text[..end] : null;
    }

    /// <summary>A command's output ends with a line break the console would show as an empty line.</summary>
    public override string MapReplResult(ReplRequest request, string result) => request.Context == "repl" ? result.TrimEnd('\n', '\r') : result;

    public override CompletionMapping? MapCompletions(string text, int column)
    {
        column = Math.Clamp(column, 0, text.Length);
        foreach (var prefix in CommandPrefixes)
            if (text.StartsWith(prefix, StringComparison.Ordinal) && column >= prefix.Length)
                return Mapping(text[prefix.Length..], column - prefix.Length, prefix, "", column); // a command: completed as one, prefix put back
        // an expression: GDB completes the command `print <expression>`
        return Mapping("print " + text, column + 6, "", "print ", column);

        // GDB answers with the completed LINE ("print p.x") and no start: it replaces everything before the caret.
        static CompletionMapping Mapping(string sent, int sentColumn, string typedPrefix, string gdbPrefix, int caret) =>
            new(sent, sentColumn, item =>
            {
                var line = item.Label;
                if (gdbPrefix.Length > 0)
                {
                    if (!line.StartsWith(gdbPrefix, StringComparison.Ordinal)) return null;
                    line = line[gdbPrefix.Length..];
                }
                if (line.Length == 0) return null;
                return new DebugCompletionItem(Shown(line), typedPrefix + line, item.Type, 0, caret);
            });

        // the list shows the completed word, not the whole line ("info locals" → "locals"; "p.x" and "sample::util::add" stay)
        static string Shown(string line)
        {
            var cut = line.LastIndexOf(' ');
            return cut >= 0 && cut < line.Length - 1 ? line[(cut + 1)..] : line;
        }
    }

    // ------------------------------------------------------------------ breakpoints

    [GeneratedRegex(@"^\s*(?<op>==|=|>=|>)?\s*(?<n>\d{1,9})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HitCount();

    public override HitConditionMapping MapHitCondition(string hitCondition)
    {
        var m = HitCount().Match(hitCondition);
        if (!m.Success)
            return new HitConditionMapping(null,
                $"GDB cannot break on the hit count '{hitCondition.Trim()}': it can only start breaking after a number of hits ('5', '>5' or '>=5').");
        var n = int.Parse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var ignore = m.Groups["op"].Value == ">" ? n : Math.Max(0, n - 1);
        return new HitConditionMapping(ignore.ToString(CultureInfo.InvariantCulture), null);
    }

    // ------------------------------------------------------------------ panics

    // Rust 1.73+: "thread 'main' panicked at src/util.rs:15:9:\nboom\n" (1.88+ adds the thread id: "thread 'main' (1234) panicked at …").
    [GeneratedRegex(@"thread '(?<thread>[^'\n]*)'(?: \(\d+\))? panicked at (?<location>[^\n]+?):\r?\n(?<message>[\s\S]*?)(?=\r?\nnote: |\r?\nthread '|\r?\n\r?\n|\r?\n?\z)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ModernPanic();

    // Rust before 1.73: "thread 'main' panicked at 'boom', src/util.rs:15:9"
    [GeneratedRegex(@"thread '(?<thread>[^'\n]*)' panicked at '(?<message>[\s\S]*?)', (?<location>[^\n]+?:\d+:\d+)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LegacyPanic();

    public override DebugExceptionInfo? DescribeSyntheticStop(SyntheticExceptionFilter filter, string recentOutput) =>
        filter.Filter == RustPanicFilter ? DescribeRustPanic(recentOutput) : null;

    /// <summary>The last Rust panic message in the program's output (the exception a panic stop stands for); null when there is none yet.</summary>
    internal static DebugExceptionInfo? DescribeRustPanic(string recentOutput)
    {
        Match? last = null;
        try
        {
            foreach (Match m in ModernPanic().Matches(recentOutput)) last = m;
            if (last is null) foreach (Match m in LegacyPanic().Matches(recentOutput)) last = m;
        }
        catch (RegexMatchTimeoutException) { return null; }
        if (last is null) return null;
        var message = last.Groups["message"].Value.Trim();
        var where = $"thread '{last.Groups["thread"].Value}' panicked at {last.Groups["location"].Value.Trim()}";
        return new DebugExceptionInfo("panic", message, "always", message, "panic", where);
    }

    // ------------------------------------------------------------------ output

    // GDB's own messages, which arrive on the program's output pipe as "stdout" (spaces are written [ ]: the pattern ignores whitespace).
    [GeneratedRegex("""
        ^(?:No[ ]source[ ]file[ ]named[ ].*\.
        |Breakpoint[ ]\d+[ ]\(.*\)[ ]pending\.
        |Breakpoint[ ]\d+[ ]at[ ]0x[0-9a-f]+(?::[ ]file[ ].*,[ ]line[ ]\d+\.|:[ ].*)?
        |Function[ ]".*"[ ]not[ ]defined\.
        |No[ ]symbol[ ]table[ ]is[ ]loaded\.[ ][ ]Use[ ]the[ ]"file"[ ]command\.
        |Reading[ ]symbols[ ]from[ ].*
        |\[(?:New[ ]Thread|Thread)[ ]0x[0-9a-f]+[ ]\(LWP[ ]\d+\)(?:[ ]\(id[ ]\d+\))?(?:[ ]exited)?\]
        |\[Inferior[ ]\d+[ ]\(process[ ]\d+\)[ ](?:exited[ ]normally|exited[ ]with[ ]code[ ]\d+|killed|detached)\]
        |\[Thread[ ]debugging[ ]using[ ]libthread_db[ ]enabled\]
        |Using[ ]host[ ]libthread_db[ ]library[ ]".*"\.
        |\[Detaching[ ]after[ ]v?fork[ ]from[ ]child[ ]process[ ]\d+\]
        |\[Switching[ ]to[ ](?:thread|Thread)[ ].*\]
        |warning:[ ]Error[ ]disabling[ ]address[ ]space[ ]randomization:[ ].*
        )\r?\n?$
        """, RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace, 1000)]
    private static partial Regex GdbMessage();

    public override string MapOutputCategory(string category, string output)
    {
        if (category != "stdout" || output.Length > 400) return category;
        try { return GdbMessage().IsMatch(output) ? "console" : category; }
        catch (RegexMatchTimeoutException) { return category; }
    }

    /// <summary>GDB sends a logpoint's message (category <c>console</c>) without a line break: consecutive messages would run together.</summary>
    public override string MapOutputText(string category, string output) =>
        category == "console" && output.Length > 0 && output[^1] != '\n' ? output + "\n" : output;

    // ------------------------------------------------------------------ frames

    public override DebugStackFrame MapFrame(DebugStackFrame frame) => DeemphasizeLibraryFrame(frame);

    /// <summary>The frame, de-emphasised when it is library code (<see cref="IsLibrarySource"/>) or has no source at all.</summary>
    internal static DebugStackFrame DeemphasizeLibraryFrame(DebugStackFrame frame)
    {
        // A frame without debug information (std's __rust_try, a C library without its debug package) has no source at all: not the user's
        // code either, and it belongs in the same [External Code] run as the library frames around it.
        if (frame.Source is not { } source) return frame.PresentationHint is null ? frame with { PresentationHint = "deemphasize" } : frame;
        if (source.PresentationHint == "deemphasize" || !IsLibrarySource(source)) return frame;
        return frame with { Source = source with { PresentationHint = "deemphasize" } };
    }

    /// <summary>
    /// The Rust standard library (its DWARF paths are <c>/rustc/&lt;commit&gt;/library/…</c>, which the factory maps onto the rust-src component
    /// under the sysroot's <c>lib/rustlib/src/rust</c> when it is installed), and anything whose source is not on disk: GDB hands out a
    /// <c>sourceReference</c> exactly then (its <c>source</c> request can only read files, so the content cannot be shown either).
    /// </summary>
    internal static bool IsLibrarySource(DebugSource source)
    {
        if (source.SourceReference is > 0) return true;
        var path = source.Path?.Replace('\\', '/');
        if (string.IsNullOrEmpty(path)) return false;
        return path.StartsWith("/rustc/", StringComparison.Ordinal) || path.Contains("/lib/rustlib/src/rust/", StringComparison.Ordinal);
    }
}
