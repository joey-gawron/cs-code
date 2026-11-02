using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.DotNet;

/// <summary>Which test framework a Microsoft.Testing.Platform executable runs (each takes a filter of its own; docs/TESTING.md).</summary>
public enum MtpFramework { Other, MSTest, NUnit, TUnit }

/// <summary>
/// One test as a test runner lists it: <see cref="Uid"/> is what <c>--filter-uid</c> takes (MSTest: a GUID; NUnit: the fully qualified name with
/// its arguments; TUnit: a dotted path; xUnit v3: a hash), <see cref="Id"/> is CsCode's <c>Ns.Class.Method</c> (the scanner's), one per method
/// whatever its data rows, so a run's results land on the tests discovery found.
/// </summary>
public sealed record ListedTest(string Uid, string Id, string DisplayName, string Class, string Method, TestLocation? Location);

/// <summary>
/// Reading Microsoft.Testing.Platform (MTP) test executables and xUnit v3's native runner (checked 2026-11-01 with MSTest 4.5.2, NUnit 4.6.1 +
/// NUnit3TestAdapter 6.3.1, TUnit 1.78.0 on MTP 2.5.2, xunit.v3 3.2.2):
/// <list type="bullet">
/// <item><c>&lt;exe&gt; --list-tests json</c> (MTP 2.3+) prints one JSON document <c>{tests: [{uid, displayName, type: {namespace, typeName,
/// methodName}, location: {file, lineStart}}]}</c> after any banner (<see cref="ParseMtpListing"/>);</item>
/// <item>xUnit v3's <c>-list full/json</c> prints <c>[{DisplayName, ID, Class, Method}]</c> (<see cref="ParseXunitListing"/>), the way an F# or VB
/// project's xUnit tests are discovered (no scanner reads those languages);</item>
/// <item><c>--output detailed --no-ansi</c> prints <c>passed|failed|skipped &lt;display name&gt; (&lt;time&gt;)</c> per test, a failure's message and
/// stack indented below it (<see cref="ConsoleReporter"/>): what a debug run reports from, since <c>--report-trx</c> moves the tests into a child
/// test host the debugger never sees.</item>
/// </list>
/// </summary>
public static partial class TestingPlatform
{
    /// <summary>The tests of an MTP <c>--list-tests json</c> output, or null when it holds no such document (an older platform, a crash).</summary>
    public static IReadOnlyList<ListedTest>? ParseMtpListing(string output)
    {
        var start = output.IndexOf("{\"", StringComparison.Ordinal);
        if (start < 0) start = output.IndexOf('{');
        if (start < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(output[start..], new JsonDocumentOptions { AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Array) return null;
            var list = new List<ListedTest>();
            foreach (var t in tests.EnumerateArray())
            {
                var uid = Str(t, "uid");
                if (uid is null) continue;
                var display = Str(t, "displayName") ?? uid;
                string cls = "", method = display;
                if (t.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Object)
                {
                    var ns = Str(type, "namespace") ?? "";
                    var name = Str(type, "typeName") ?? "";
                    cls = ns.Length > 0 && name.Length > 0 ? ns + "." + name : name.Length > 0 ? name : ns;
                    method = Str(type, "methodName") ?? display;
                }
                TestLocation? location = null;
                if (t.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object && Str(loc, "file") is { Length: > 0 } file)
                    location = new TestLocation(file, loc.TryGetProperty("lineStart", out var ls) && ls.TryGetInt32(out var line) ? Math.Max(0, line - 1) : 0);
                list.Add(new ListedTest(uid, cls.Length > 0 ? cls + "." + method : method, display, cls, method, location));
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The tests of xUnit v3's <c>-list full/json</c> output, or null when it is not that.</summary>
    public static IReadOnlyList<ListedTest>? ParseXunitListing(string output)
    {
        var start = output.IndexOf('[');
        var end = output.LastIndexOf(']');
        if (start < 0 || end < start) return null;
        try
        {
            using var doc = JsonDocument.Parse(output[start..(end + 1)]);
            var list = new List<ListedTest>();
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                var cls = Str(t, "Class") ?? "";
                var method = Str(t, "Method") ?? "";
                if (method.Length == 0) continue;
                var id = cls.Length > 0 ? cls + "." + method : method;
                list.Add(new ListedTest(Str(t, "ID") ?? id, id, Str(t, "DisplayName") ?? id, cls, method, null));
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// The uids that run <paramref name="testIds"/> (every data row of a method), or null to run everything: no request, a test the listing does
    /// not know (renamed since: running all is better than running nothing), or more than fits a command line.
    /// </summary>
    public static IReadOnlyList<string>? UidsFor(IReadOnlyList<string>? testIds, IReadOnlyList<ListedTest> listed)
    {
        if (testIds is not { Count: > 0 }) return null;
        var byId = listed.ToLookup(t => t.Id, StringComparer.Ordinal);
        var uids = new List<string>();
        foreach (var id in testIds)
        {
            if (!byId.Contains(id)) return null;
            uids.AddRange(byId[id].Select(t => t.Uid));
        }
        return uids.Count <= DotNetTestProvider.MaxFilteredTests && uids.Sum(u => u.Length + 3L) <= DotNetTestProvider.MaxFilterChars ? uids : null;
    }

    /// <summary>
    /// The framework's own filter for <paramref name="testIds"/>, for an executable that cannot list (MTP before 2.3) or take uids (before 1.8):
    /// MSTest <c>--filter FullyQualifiedName=…</c> (a data row's name has no arguments); NUnit the same through its adapter's parser, which takes no
    /// escaped parentheses (so a parameterized NUnit test is matched with <c>~</c>, which also matches longer names); TUnit
    /// <c>--treenode-filter /*/Ns/Class/Method</c> (one test, or one method name per class). Null: run everything.
    /// </summary>
    public static IReadOnlyList<string>? NativeFilter(MtpFramework framework, IReadOnlyList<string>? testIds)
    {
        if (testIds is not { Count: > 0 and <= DotNetTestProvider.MaxFilteredTests } || testIds.Any(id => FilterOperator().IsMatch(id))) return null;
        string? filter;
        switch (framework)
        {
            case MtpFramework.TUnit:
                var classes = testIds.Select(Split).GroupBy(p => p.Class).ToArray();
                if (classes.Length != 1) return null;
                var (ns, cls) = SplitClass(classes[0].Key);
                var methods = classes[0].Select(p => p.Method).Distinct().ToArray();
                var methodPart = methods.Length == 1 ? methods[0] : "(" + string.Join('|', methods) + ")";
                return ["--treenode-filter", $"/*/{(ns.Length > 0 ? ns : "*")}/{cls}/{methodPart}"];
            case MtpFramework.NUnit:
                filter = string.Join('|', testIds.Select(id => "FullyQualifiedName~" + id));
                break;
            default:
                filter = string.Join('|', testIds.Select(id => "FullyQualifiedName=" + id));
                break;
        }
        return filter.Length <= DotNetTestProvider.MaxFilterChars ? ["--filter", filter] : null;

        static (string Class, string Method) Split(string id) =>
            id.LastIndexOf('.') is var dot and > 0 ? (id[..dot], id[(dot + 1)..]) : ("", id);
        static (string Ns, string Class) SplitClass(string cls) =>
            cls.LastIndexOf('.') is var dot and > 0 ? (cls[..dot], cls[(dot + 1)..]) : ("", cls);
    }

    // characters the VSTest-style filter parsers treat as operators: an id with one is not expressible there (NUnit's parser takes no escapes)
    [GeneratedRegex(@"[\\()&|=!~]", RegexOptions.CultureInvariant)]
    private static partial Regex FilterOperator();

    /// <summary>
    /// Results from an MTP executable's console (<c>--output detailed --no-ansi --progress off</c>), for runs without a TRX file (a debug run, a
    /// project without the TRX extension). A line <c>passed|failed|skipped &lt;display name&gt; (&lt;duration&gt;)</c> starts a result; the indented
    /// lines after a failure are its message and stack. A display name maps to the listed test of that name (the first not yet reported when
    /// several tests share it); a data row (<c>Sums (1,2,3)</c>) becomes a case of its method.
    /// </summary>
    public sealed partial class ConsoleReporter(TestProject project, ITestRunSink sink, IReadOnlyList<ListedTest> listed)
    {
        private readonly Dictionary<string, Queue<ListedTest>> _byDisplay = listed.GroupBy(t => t.DisplayName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new Queue<ListedTest>(g), StringComparer.Ordinal);
        private (ListedTest Test, TestOutcome Outcome, TimeSpan? Duration, string Display)? _pending;
        private readonly List<string> _detail = [];

        public bool SawResults { get; private set; }

        public void OnLine(string line)
        {
            sink.Output(line);
            var m = ResultLine().Match(line);
            if (m.Success)
            {
                Flush();
                var display = m.Groups["name"].Value;
                if (!_byDisplay.TryGetValue(display, out var queue) || !queue.TryDequeue(out var test)) return; // not one of ours (a summary line)
                var outcome = m.Groups["outcome"].Value switch { "passed" => TestOutcome.Passed, "failed" => TestOutcome.Failed, _ => TestOutcome.Skipped };
                _pending = (test, outcome, ParseDuration(m.Groups["time"].Value), display);
                sink.Started(project, test.Id);
                return;
            }
            if (_pending is not null && (line.StartsWith("  ", StringComparison.Ordinal) || line.Length == 0)) _detail.Add(line);
            else Flush();
        }

        /// <summary>Reports the last result (call when the output ended).</summary>
        public void Complete() => Flush();

        private void Flush()
        {
            if (_pending is not { } p) return;
            _pending = null;
            SawResults = true;
            var text = string.Join('\n', _detail).Trim('\n', ' ');
            _detail.Clear();
            var at = text.IndexOf("\n    at ", StringComparison.Ordinal);
            var message = text.Length == 0 ? null : (at >= 0 ? text[..at] : text).Trim();
            var stack = at >= 0 ? text[(at + 1)..].TrimEnd() : null;
            var isCase = p.Display != p.Test.Method;
            sink.Finished(project, new TestResult(p.Test.Id, p.Outcome, p.Duration, TestLimits.Clip(message), TestLimits.Clip(stack), null,
                p.Outcome == TestOutcome.Failed ? StackTraces.FromDotNet(stack, project.Directory) : null, isCase ? p.Display : null));
        }

        private static TimeSpan? ParseDuration(string text)
        {
            // "2ms", "1s 080ms", "4s 080ms", "1m 02s"
            double ms = 0;
            var any = false;
            foreach (Match part in DurationPart().Matches(text))
            {
                if (!double.TryParse(part.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                any = true;
                ms += part.Groups[2].Value switch { "ms" => v, "s" => v * 1000, "m" => v * 60_000, "h" => v * 3_600_000, _ => 0 };
            }
            return any ? TimeSpan.FromMilliseconds(ms) : null;
        }

        [GeneratedRegex(@"^(?<outcome>passed|failed|skipped) (?<name>.+) \((?<time>[0-9][0-9a-z .]*)\)$", RegexOptions.CultureInvariant)]
        private static partial Regex ResultLine();

        [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)(ms|s|m|h)\b", RegexOptions.CultureInvariant)]
        private static partial Regex DurationPart();
    }
}
