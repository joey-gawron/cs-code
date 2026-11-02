using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.Cpp;

/// <summary>A test as a C++ framework lists it: its full runner name, where it is declared (absolute path, 1-based line) and what to show.</summary>
public sealed record CppListedTest(string Name, string Label, IReadOnlyList<TestGroup> Groups, string? File = null, int Line = 0, string? SkipReason = null);

/// <summary>A test's result as a C++ framework reports it (the provider maps names to ids and places paths under the folder).</summary>
public sealed record CppTestResult(string Name, TestOutcome Outcome, TimeSpan? Duration = null, string? Message = null, string? Output = null, string? FailureFile = null, int FailureLine = 0);

/// <summary>
/// GoogleTest's listing and results. <b>Listing</b>: <c>--gtest_list_tests --gtest_output=json:&lt;file&gt;</c> writes every test with its file and
/// line (GoogleTest 1.10 and later); older ones print only the names (<see cref="ParseListing"/>). <b>Runs</b> print a <c>[ RUN ]</c> /
/// <c>[       OK ]</c> / <c>[  FAILED  ]</c> / <c>[  SKIPPED ]</c> line per test, read as they come (<see cref="ConsoleParser"/>: the test's output and its
/// failures with their <c>file:line</c>), and write <c>--gtest_output=json:&lt;file&gt;</c> at the end (<see cref="ParseResults"/>), which settles
/// what the console did not report (disabled tests, a custom printer).
/// </summary>
public static partial class GoogleTestFormat
{
    /// <summary>The JSON listing (<c>testsuites[].testsuite[]</c> with <c>name</c>, <c>file</c>, <c>line</c>, <c>type_param</c>, <c>value_param</c>).</summary>
    public static IReadOnlyList<CppListedTest> ParseListingJson(string json)
    {
        var result = new List<CppListedTest>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("testsuites", out var suites) || suites.ValueKind != JsonValueKind.Array) return result;
        foreach (var suite in suites.EnumerateArray())
        {
            var suiteName = Str(suite, "name");
            if (suiteName is null || !suite.TryGetProperty("testsuite", out var tests) || tests.ValueKind != JsonValueKind.Array) continue;
            foreach (var t in tests.EnumerateArray())
            {
                if (Str(t, "name") is not { } name) continue;
                var line = t.TryGetProperty("line", out var l) && l.TryGetInt32(out var n) ? n : 0;
                result.Add(Listed(suiteName, name, Str(t, "type_param"), Str(t, "value_param"), Str(t, "file"), line));
                if (result.Count >= TestLimits.MaxTestsPerProject) return result;
            }
        }
        return result;
    }

    /// <summary>
    /// The plain listing: a suite per unindented line (<c>Suite.</c>, <c>Typed/0.  # TypeParam = int</c>) and its tests indented below
    /// (<c>  IsPositive/0  # GetParam() = 1</c>); other lines (<c>Running main() from …</c>) are ignored.
    /// </summary>
    public static IReadOnlyList<CppListedTest> ParseListing(IEnumerable<string> lines)
    {
        var result = new List<CppListedTest>();
        string? suite = null; string? typeParam = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var (body, comment) = SplitComment(line);
            if (!char.IsWhiteSpace(line[0]))
            {
                suite = body.EndsWith('.') && !body.Contains(' ') ? body[..^1] : null;
                typeParam = comment?.StartsWith("TypeParam = ", StringComparison.Ordinal) == true ? comment["TypeParam = ".Length..] : null;
                continue;
            }
            if (suite is null) continue;
            var name = body.Trim();
            if (name.Length == 0 || name.Contains(' ')) continue;
            var valueParam = comment?.StartsWith("GetParam() = ", StringComparison.Ordinal) == true ? comment["GetParam() = ".Length..] : null;
            result.Add(Listed(suite, name, typeParam, valueParam, null, 0));
            if (result.Count >= TestLimits.MaxTestsPerProject) break;
        }
        return result;
    }

    private static (string Body, string? Comment) SplitComment(string line)
    {
        var hash = line.IndexOf("  # ", StringComparison.Ordinal);
        return hash < 0 ? (line, null) : (line[..hash], line[(hash + 4)..].Trim());
    }

    private static CppListedTest Listed(string suite, string name, string? typeParam, string? valueParam, string? file, int line)
    {
        var label = name + (valueParam is not null ? $" (GetParam() = {valueParam})" : typeParam is not null ? $" (TypeParam = {typeParam})" : "");
        var skip = name.StartsWith("DISABLED_", StringComparison.Ordinal) || suite.Split('/').Any(p => p.StartsWith("DISABLED_", StringComparison.Ordinal))
            ? "disabled (DISABLED_ prefix): runs only when chosen by itself" : null;
        return new CppListedTest(suite + "." + name, label, [new TestGroup(suite, TestNodeKind.Class)], file, line, skip);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// The JSON results: <c>result</c> <c>COMPLETED</c> (passed, or failed with <c>failures[]</c>), <c>SKIPPED</c> (with <c>skipped[].message</c>),
    /// <c>SUPPRESSED</c> (a disabled test: skipped); <c>time</c> as <c>"0.012s"</c>; each failure starts with <c>file:line</c> (or
    /// <c>unknown file</c>).
    /// </summary>
    public static IReadOnlyList<CppTestResult> ParseResults(string json)
    {
        var result = new List<CppTestResult>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("testsuites", out var suites) || suites.ValueKind != JsonValueKind.Array) return result;
        foreach (var suite in suites.EnumerateArray())
        {
            var suiteName = Str(suite, "name");
            if (suiteName is null || !suite.TryGetProperty("testsuite", out var tests) || tests.ValueKind != JsonValueKind.Array) continue;
            foreach (var t in tests.EnumerateArray())
            {
                if (Str(t, "name") is not { } name) continue;
                var full = suiteName + "." + name;
                TimeSpan? duration = Str(t, "time") is { } time && time.EndsWith('s')
                    && double.TryParse(time[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? TimeSpan.FromSeconds(seconds) : null;
                var status = Str(t, "result");
                if (status == "SUPPRESSED" || Str(t, "status") == "NOTRUN")
                {
                    result.Add(new CppTestResult(full, TestOutcome.Skipped, null, "Disabled (DISABLED_ prefix)."));
                    continue;
                }
                if (status == "SKIPPED")
                {
                    var why = Messages(t, "skipped", "message");
                    result.Add(new CppTestResult(full, TestOutcome.Skipped, duration, why.Count > 0 ? string.Join("\n", why.Select(w => SplitLocation(w).Text)) : null));
                    continue;
                }
                var failures = Messages(t, "failures", "failure");
                if (failures.Count == 0)
                {
                    result.Add(new CppTestResult(full, TestOutcome.Passed, duration));
                    continue;
                }
                var first = failures.Select(SplitLocation).FirstOrDefault(f => f.File is not null);
                var message = string.Join("\n", failures.Select(f => SplitLocation(f) is var s && s.File is not null ? $"{s.File}:{s.Line}: {s.Text}" : s.Text));
                result.Add(new CppTestResult(full, TestOutcome.Failed, duration, message, null, first.File, first.Line));
            }
        }
        return result;
    }

    private static List<string> Messages(JsonElement test, string array, string property)
    {
        var list = new List<string>();
        if (!test.TryGetProperty(array, out var items) || items.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in items.EnumerateArray())
            if (Str(item, property) is { } text) list.Add(text);
        return list;
    }

    /// <summary>A failure's first line, <c>/path/file.cpp:9</c> or <c>unknown file</c>, split from its text.</summary>
    private static (string? File, int Line, string Text) SplitLocation(string failure)
    {
        var nl = failure.IndexOf('\n');
        var head = nl < 0 ? failure : failure[..nl];
        var rest = nl < 0 ? "" : failure[(nl + 1)..].TrimEnd();
        var colon = head.LastIndexOf(':');
        if (colon > 0 && int.TryParse(head.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var line)) return (head[..colon], line, rest);
        return (null, 0, head == "unknown file" ? rest : failure.TrimEnd());
    }

    /// <summary>
    /// Reads a run's console output as it comes: a test's <c>[ RUN      ]</c> line starts it, its <c>[       OK ]</c> / <c>[  FAILED  ]</c> /
    /// <c>[  SKIPPED ]</c> line (with <c>(N ms)</c>) ends it, <c>[ DISABLED ]</c> names a disabled test. Between them, a <c>file:line: Failure</c>
    /// (or <c>unknown file: Failure</c>, <c>file:line: Skipped</c>) line opens a failure block that runs to the next empty line; everything else
    /// is the test's output.
    /// </summary>
    public sealed class ConsoleParser(Action<string> started, Action<CppTestResult> finished)
    {
        private readonly StringBuilder _output = new();
        private readonly StringBuilder _failure = new();
        private readonly List<string> _failures = [];
        private string? _current;
        private string? _failureFile;
        private int _failureLine;
        private bool _inFailure;
        private bool _skipBlock;
        private string? _skipMessage;

        /// <summary>The names the console reported a result for.</summary>
        public HashSet<string> Finished { get; } = new(StringComparer.Ordinal);

        /// <summary>The test that started and has not finished (the one running when the executable crashed or was stopped).</summary>
        public string? Running => _current;

        public void OnLine(string line)
        {
            var m = StatusLine().Match(line);
            if (m.Success)
            {
                var status = m.Groups["status"].Value.Trim();
                var name = m.Groups["name"].Value;
                if (status == "RUN")
                {
                    Begin(name);
                    started(name);
                    return;
                }
                if (status == "DISABLED")
                {
                    Finished.Add(name);
                    finished(new CppTestResult(name, TestOutcome.Skipped, null, "Disabled (DISABLED_ prefix)."));
                    return;
                }
                if (status is "OK" or "FAILED" or "SKIPPED" && (_current == name || (_current is not null && name.StartsWith(_current + ",", StringComparison.Ordinal))))
                {
                    End(status, m.Groups["ms"].Success && long.TryParse(m.Groups["ms"].Value, out var ms) ? TimeSpan.FromMilliseconds(ms) : null);
                    return;
                }
                // the summary at the end ("[  FAILED  ] MathTest.Fails", "[  PASSED  ] 6 tests.") is not a test's own line
                return;
            }
            if (_current is null) return;
            if (_inFailure)
            {
                if (line.Length == 0) { CloseFailure(); return; }
                _failure.Append(line).Append('\n');
                return;
            }
            var f = FailureHead().Match(line);
            if (f.Success)
            {
                _inFailure = true;
                _skipBlock = f.Groups["kind"].Value == "Skipped";
                if (!_skipBlock && _failureFile is null && f.Groups["file"].Value != "unknown file"
                    && int.TryParse(f.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var failureLine))
                {
                    _failureFile = f.Groups["file"].Value;
                    _failureLine = failureLine;
                }
                _failure.Clear();
                if (f.Groups["file"].Value != "unknown file" && !_skipBlock) _failure.Append(f.Groups["file"].Value).Append(':').Append(f.Groups["line"].Value).Append(": ");
                return;
            }
            if (_output.Length < TestLimits.MaxOutputPerTest) _output.Append(line).Append('\n');
        }

        private void Begin(string name)
        {
            _current = name;
            _output.Clear();
            _failure.Clear();
            _failures.Clear();
            _failureFile = null;
            _failureLine = 0;
            _inFailure = false;
            _skipMessage = null;
        }

        private void CloseFailure()
        {
            var text = _failure.ToString().TrimEnd();
            if (_skipBlock) _skipMessage = text;
            else if (text.Length > 0) _failures.Add(text);
            _failure.Clear();
            _inFailure = false;
            _skipBlock = false;
        }

        private void End(string status, TimeSpan? duration)
        {
            if (_inFailure) CloseFailure();
            var name = _current!;
            var outcome = status switch { "OK" => TestOutcome.Passed, "SKIPPED" => TestOutcome.Skipped, _ => TestOutcome.Failed };
            var message = outcome == TestOutcome.Failed ? (_failures.Count > 0 ? string.Join("\n\n", _failures) : null) : outcome == TestOutcome.Skipped ? _skipMessage : null;
            var output = _output.Length > 0 ? _output.ToString().TrimEnd('\n') : null;
            Finished.Add(name);
            finished(new CppTestResult(name, outcome, duration, TestLimits.Clip(message), TestLimits.Clip(output),
                outcome == TestOutcome.Failed ? _failureFile : null, _failureLine));
            _current = null;
        }

        /// <summary>The output and failures seen so far of the test that is still running (for a crash report).</summary>
        public (string? Output, string? Failures) Pending()
        {
            if (_inFailure) CloseFailure();
            return (_output.Length > 0 ? _output.ToString().TrimEnd('\n') : null, _failures.Count > 0 ? string.Join("\n\n", _failures) : null);
        }
    }

    [GeneratedRegex(@"^\[\s*(?<status>RUN|OK|FAILED|SKIPPED|DISABLED|PASSED)\s*\] (?<name>\S+)(?:.*?\((?<ms>\d+) ms\))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex StatusLine();

    [GeneratedRegex(@"^(?<file>unknown file|.+?):(?:(?<line>\d+):)? (?<kind>Failure|Skipped)$", RegexOptions.CultureInvariant)]
    private static partial Regex FailureHead();
}
