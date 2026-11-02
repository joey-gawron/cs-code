using System.Text;
using System.Text.RegularExpressions;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.Zig;

/// <summary>
/// Reads the output of Zig's default test runner in its terminal mode (what <c>zig test</c> and a test binary print when standard error is not a
/// terminal, stable since Zig 0.11): <c>1/4 root.test.adds...OK</c>, <c>…SKIP</c>, <c>…FAIL (TestExpectedEqual)</c> followed by the error
/// trace, with whatever the test printed between the dots and its outcome (on the same line and the lines after). A test that panics ends the
/// process (<c>thread … panic: msg</c> and a trace): it is reported failed with the panic when the output ends. Compile errors (no test ran)
/// are collected for <see cref="CompileErrors"/>. Every line also goes to the output. One instance per process, fed lines in order, then
/// <see cref="Complete"/>.
/// </summary>
/// <param name="toTestId">The test id of a runner name (<c>root.test.adds</c>), or null for a name that is not reported (an unnamed test block).</param>
public sealed partial class ZigTestOutputParser(TestProject project, ITestRunSink sink, Func<string, string?> toTestId)
{
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly List<string> _compileErrors = [];
    private string? _open;              // the test whose outcome is not printed yet
    private StringBuilder _output = new();
    private (string Id, string Error, string Output)? _failed; // a failure whose trace is still being read
    private StringBuilder _trace = new();
    private bool _runnerEnded;          // zig said the test command crashed or failed: what follows is not a test's output

    /// <summary>The ids a result was reported for.</summary>
    public IReadOnlyCollection<string> Reported => _reported;

    /// <summary>Compile errors (<c>file:line:col: error: …</c>) printed before any test ran.</summary>
    public IReadOnlyList<string> CompileErrors => _compileErrors;

    /// <summary>How many tests the runner said it would run (the N of <c>i/N</c>); null before the first.</summary>
    public int? Total { get; private set; }

    public void OnLine(string line)
    {
        sink.Output(line);
        var start = StartLine().Match(line);
        if (start.Success)
        {
            FlushFailure();
            Total = int.Parse(start.Groups["total"].Value, System.Globalization.CultureInfo.InvariantCulture);
            _open = start.Groups["name"].Value;
            _output = new StringBuilder();
            if (toTestId(_open) is { } id) sink.Started(project, id);
            Outcome(start.Groups["rest"].Value);
            return;
        }
        if (_open is not null)
        {
            // zig's own "error: the following test command crashed:" (and the command) after a panic is not the test's output
            _runnerEnded |= line.StartsWith("error: the following test command", StringComparison.Ordinal);
            if (!_runnerEnded) Outcome(line, appendNewline: true);
            return;
        }
        if (_failed is not null)
        {
            if (Summary().IsMatch(line)) { FlushFailure(); return; }
            if (_trace.Length > 0) _trace.Append('\n');
            Append(_trace, line);
            return;
        }
        if (Total is null && CompileError().IsMatch(line) && _compileErrors.Count < 50) _compileErrors.Add(line);
    }

    /// <summary>Text after a test's dots (or a later line while it runs): its outcome at the end, what it printed before.</summary>
    private void Outcome(string text, bool appendNewline = false)
    {
        var name = _open!;
        string? printed;
        if (text.EndsWith("OK", StringComparison.Ordinal)) printed = text[..^2];
        else if (text.EndsWith("SKIP", StringComparison.Ordinal)) printed = text[..^4];
        else if (FailEnd().Match(text) is { Success: true } fail) printed = text[..fail.Index];
        else
        {
            if (appendNewline && _output.Length > 0) _output.Append('\n');
            Append(_output, text);
            return;
        }
        if (printed.Length > 0) { if (appendNewline && _output.Length > 0) _output.Append('\n'); Append(_output, printed); }
        var output = _output.Length > 0 ? _output.ToString() : null;
        _open = null;
        if (toTestId(name) is not { } id) return;
        if (text.EndsWith("OK", StringComparison.Ordinal)) Report(new TestResult(id, TestOutcome.Passed, Output: TestLimits.Clip(output)));
        else if (text.EndsWith("SKIP", StringComparison.Ordinal)) Report(new TestResult(id, TestOutcome.Skipped, Message: "error.SkipZigTest", Output: TestLimits.Clip(output)));
        else
        {
            _failed = (id, FailEnd().Match(text).Groups["error"].Value, output ?? "");
            _trace = new StringBuilder();
        }
    }

    /// <summary>Reports a failure with the trace read after it.</summary>
    private void FlushFailure()
    {
        if (_failed is not { } f) return;
        _failed = null;
        var trace = _trace.ToString().TrimEnd();
        var output = f.Output.Length > 0 ? f.Output : null;
        Report(new TestResult(f.Id, TestOutcome.Failed, Message: TestLimits.Clip(Message("error." + f.Error, output)), StackTrace: TestLimits.Clip(trace.Length > 0 ? trace : null),
            Output: TestLimits.Clip(output), FailureLocation: Location(trace)));
    }

    /// <summary>Ends the run: a failure still being read is reported, and a test that never printed its outcome (it panicked, or the process
    /// was killed) is reported failed with what it printed.</summary>
    public void Complete()
    {
        FlushFailure();
        if (_open is not { } name) return;
        _open = null;
        if (toTestId(name) is not { } id) return;
        var text = _output.ToString().TrimEnd();
        var panic = PanicLine().Match(text);
        var message = panic.Success ? "panic: " + panic.Groups["message"].Value : "The test did not finish: the test runner ended (see the output).";
        var printed = panic.Success ? text[..panic.Index].TrimEnd() : text;
        var trace = panic.Success ? text[(panic.Index + panic.Length)..].Trim() : null;
        Report(new TestResult(id, TestOutcome.Failed, Message: TestLimits.Clip(message), StackTrace: TestLimits.Clip(trace is { Length: > 0 } ? trace : null),
            Output: TestLimits.Clip(printed.Length > 0 ? printed : null), FailureLocation: trace is null ? null : Location(trace)));
    }

    private void Report(TestResult result)
    {
        _reported.Add(result.TestId);
        sink.Finished(project, result);
    }

    /// <summary>The failure's message: the error name, with std.testing's own explanation ("expected 3, found 2") when it printed one last.</summary>
    private static string Message(string error, string? output)
    {
        var last = output?.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return last is not null && ExpectationLine().IsMatch(last) ? $"{error}: {last}" : error;
    }

    /// <summary>The innermost frame of the trace inside the project (not Zig's own library): where the test failed.</summary>
    private TestLocation? Location(string trace)
    {
        foreach (Match m in TraceFrame().Matches(trace))
        {
            var file = m.Groups["file"].Value;
            // relative to the project (zig was run there), or absolute through symbolic links (/private/var for /var on macOS)
            file = Path.IsPathRooted(file) ? PhysicalPaths.UnderLogical(file, project.Directory) : Path.Combine(project.Directory, file);
            if (!StackTraces.IsUnder(file, project.Directory) || !File.Exists(file)) continue;
            return new TestLocation(file, int.Parse(m.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture) - 1);
        }
        return null;
    }

    private static void Append(StringBuilder sb, string text)
    {
        if (sb.Length < TestLimits.MaxOutputPerTest) sb.Append(text);
    }

    // "3/12 root.test.parses numbers...OK" — the name is up to the first "..." (a test name with "..." in it would be split there)
    [GeneratedRegex(@"^(?<index>\d+)/(?<total>\d+) (?<name>.+?)\.\.\.(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex StartLine();

    [GeneratedRegex(@"FAIL \((?<error>[^()]*)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex FailEnd();

    [GeneratedRegex(@"^(All \d+ tests passed\.|\d+ passed; \d+ skipped; \d+ failed\.|\d+ errors? (were|was) logged\.|\d+ tests? leaked memory\.|error: the following test command)", RegexOptions.CultureInvariant)]
    private static partial Regex Summary();

    [GeneratedRegex(@"^.+?:\d+:\d+: error: ", RegexOptions.CultureInvariant)]
    private static partial Regex CompileError();

    // "thread 1234 panic: boom"
    [GeneratedRegex(@"(?m)^thread \d+ panic: (?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex PanicLine();

    // "/p/src/root.zig:22:5: 0x102e9b033 in test.fails on purpose (test)"
    [GeneratedRegex(@"(?m)^(?<file>.+?):(?<line>\d+):(?<column>\d+): 0x[0-9a-fA-F]+ in ", RegexOptions.CultureInvariant)]
    private static partial Regex TraceFrame();

    // std.testing's explanations: "expected 3, found 2", "index 1 incorrect. expected 4, found 5", "expected true, found false"…
    [GeneratedRegex(@"(expected|incorrect|found|mismatch)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ExpectationLine();
}
