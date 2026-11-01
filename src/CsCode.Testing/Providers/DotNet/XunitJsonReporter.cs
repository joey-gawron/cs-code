using System.Text.Json;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.DotNet;

/// <summary>
/// Reads the progress messages of an xUnit v3 test executable run with <c>-reporter json</c> (one JSON object per line, <c>$type</c> naming the
/// message) and turns them into starts and results. A test's id is its class and method (<c>Ns.Class.Method</c>, what <c>-method</c> takes and what
/// <see cref="CSharpTestScanner"/> finds); a theory's data rows (a display name other than that) are reported as cases of it. Lines that are not
/// JSON (what a test writes to the console itself) go to the output. Not thread-safe: one instance per run, fed lines in order.
/// </summary>
public sealed class XunitJsonReporter(TestProject project, ITestRunSink sink)
{
    private sealed record CaseInfo(string TestId, string DisplayName, TestLocation? Location);

    private readonly Dictionary<string, CaseInfo> _cases = new(StringComparer.Ordinal);   // TestCaseUniqueID -> case
    private readonly Dictionary<string, string> _tests = new(StringComparer.Ordinal);     // TestUniqueID -> display name
    private readonly Dictionary<string, string> _output = new(StringComparer.Ordinal);    // TestUniqueID -> output
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);

    /// <summary>Number of results reported.</summary>
    public int Results { get; private set; }

    /// <summary>Whether any line was an xUnit message (false: the program is not an xUnit v3 runner, or it failed before reporting).</summary>
    public bool SawMessages { get; private set; }

    /// <summary>The test ids the run reported a result for.</summary>
    public IReadOnlyCollection<string> Reported => _seen;

    public void OnLine(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{' || !line.Contains("\"$type\"", StringComparison.Ordinal))
        {
            sink.Output(line);
            return;
        }
        JsonElement e;
        try
        {
            using var doc = JsonDocument.Parse(line);
            e = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            sink.Output(line);
            return;
        }
        if (e.ValueKind != JsonValueKind.Object || Str(e, "$type") is not { } type) { sink.Output(line); return; }
        SawMessages = true;
        switch (type)
        {
            case "test-case-starting":
            {
                var cls = Str(e, "TestClassName");
                var method = Str(e, "TestMethodName");
                var caseId = Str(e, "TestCaseUniqueID");
                if (cls is null || method is null || caseId is null) break;
                var id = cls + "." + method;
                TestLocation? loc = Str(e, "SourceFilePath") is { Length: > 0 } file && e.TryGetProperty("SourceLineNumber", out var ln) && ln.ValueKind == JsonValueKind.Number && ln.TryGetInt32(out var lineNo)
                    ? new TestLocation(file, Math.Max(0, lineNo - 1)) : null;
                _cases[caseId] = new CaseInfo(id, Str(e, "TestCaseDisplayName") ?? id, loc);
                if (_started.Add(id))
                {
                    sink.Discovered(project, new DiscoveredTest(id, method, DotNetGroups(cls), loc));
                    sink.Started(project, id);
                }
                break;
            }
            case "test-starting":
                if (Str(e, "TestUniqueID") is { } testId) _tests[testId] = Str(e, "TestDisplayName") ?? "";
                break;
            case "test-output":
                if (Str(e, "TestUniqueID") is { } outId && Str(e, "Output") is { } text)
                    _output[outId] = TestLimits.Clip(_output.GetValueOrDefault(outId) + text)!;
                break;
            case "test-passed":
                Report(e, TestOutcome.Passed);
                break;
            case "test-failed":
                Report(e, TestOutcome.Failed);
                break;
            case "test-skipped":
                Report(e, TestOutcome.Skipped);
                break;
            case "test-not-run":
                Report(e, TestOutcome.None);
                break;
            case "error-message":
                sink.Output("[xUnit error] " + string.Join(Environment.NewLine, Strings(e, "Messages")) + Environment.NewLine + string.Join(Environment.NewLine, Strings(e, "StackTraces")));
                break;
        }
    }

    private void Report(JsonElement e, TestOutcome outcome)
    {
        if (Str(e, "TestCaseUniqueID") is not { } caseId || !_cases.TryGetValue(caseId, out var info)) return;
        var testUid = Str(e, "TestUniqueID");
        var display = testUid is not null && _tests.TryGetValue(testUid, out var d) && d.Length > 0 ? d : info.DisplayName;
        TimeSpan? duration = e.TryGetProperty("ExecutionTime", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetDouble(out var secs) ? TimeSpan.FromSeconds(secs) : null;
        string? message = null, stack = null;
        if (outcome == TestOutcome.Failed)
        {
            var types = Strings(e, "ExceptionTypes");
            var messages = Strings(e, "Messages");
            message = messages.Count > 0 ? (types.Count > 0 && !messages[0].StartsWith("Assert.", StringComparison.Ordinal) ? types[0] + ": " : "") + messages[0] : types.FirstOrDefault();
            stack = string.Join(Environment.NewLine + "----- Inner Stack Trace -----" + Environment.NewLine, Strings(e, "StackTraces"));
        }
        else if (outcome == TestOutcome.Skipped) message = Str(e, "Reason");
        var output = Str(e, "Output") is { Length: > 0 } o ? o : testUid is not null ? _output.GetValueOrDefault(testUid) : null;
        var caseLabel = display == info.TestId || display == info.DisplayName && info.DisplayName == info.TestId ? null : CaseLabel(display, info.TestId);
        Results++;
        _seen.Add(info.TestId);
        sink.Finished(project, new TestResult(info.TestId, outcome, duration, TestLimits.Clip(message), TestLimits.Clip(stack), TestLimits.Clip(output),
            outcome == TestOutcome.Failed ? StackTraces.FromDotNet(stack, project.Directory) ?? info.Location : null, caseLabel));
    }

    /// <summary>A data row's label: the display name without the class prefix (<c>Ns.C.Th(x: 1)</c> → <c>Th(x: 1)</c>).</summary>
    internal static string CaseLabel(string display, string testId)
    {
        var lastDot = testId.LastIndexOf('.');
        var classPrefix = lastDot > 0 ? testId[..(lastDot + 1)] : "";
        return classPrefix.Length > 0 && display.StartsWith(classPrefix, StringComparison.Ordinal) ? display[classPrefix.Length..] : display;
    }

    /// <summary>Namespace and class groups for a fully qualified class name (<c>Ns.Sub.Outer+Inner</c>).</summary>
    public static IReadOnlyList<TestGroup> DotNetGroups(string fullClassName, TestLocation? classLocation = null)
    {
        var plus = fullClassName.IndexOf('+');
        var outer = plus >= 0 ? fullClassName[..plus] : fullClassName;
        var dot = outer.LastIndexOf('.');
        var ns = dot > 0 ? outer[..dot] : "";
        var cls = dot > 0 ? fullClassName[(dot + 1)..] : fullClassName;
        return ns.Length > 0
            ? [new TestGroup(ns, TestNodeKind.Namespace), new TestGroup(cls, TestNodeKind.Class, classLocation)]
            : [new TestGroup(cls, TestNodeKind.Class, classLocation)];
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
}
