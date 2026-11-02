using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.Python;

/// <summary>
/// pytest's <c>--junitxml</c> report as test results. A <c>&lt;testcase&gt;</c> names its test by <c>classname</c> and <c>name</c>, which pytest derives
/// from the node id (<c>mangle_test_address</c> in _pytest/junitxml.py: the file part with <c>/</c> as <c>.</c> and without <c>.py</c>, the other
/// <c>::</c> parts, the parametrization kept on the name); <see cref="Mangle"/> does the same, so each case maps back to the node id the plugin
/// reported when it started.
/// </summary>
internal static partial class PytestJunit
{
    /// <summary>(classname, name) of a node id: <c>tests/test_m.py::TestM::test_add[1-2]</c> → (<c>tests.test_m.TestM</c>, <c>test_add[1-2]</c>).</summary>
    public static (string ClassName, string Name) Mangle(string nodeId)
    {
        var bracket = nodeId.IndexOf('[');
        var path = bracket < 0 ? nodeId : nodeId[..bracket];
        var parameters = bracket < 0 ? "" : nodeId[bracket..];
        var names = path.Split("::");
        names[0] = PyExtension().Replace(names[0].Replace('/', '.'), "");
        names[^1] += parameters;
        return (string.Join('.', names[..^1]), names[^1]);
    }

    [GeneratedRegex(@"\.py$")]
    private static partial Regex PyExtension();

    /// <summary>
    /// The id the explorer knows a node id by (the parametrization dropped) and the case label (the parametrization, or null). The parametrization
    /// starts at the first <c>[</c>, as pytest's own <c>partition("[")</c> takes it: it may itself hold <c>::</c> and brackets.
    /// </summary>
    public static (string TestId, string? Case) Split(string nodeId)
    {
        var open = nodeId.IndexOf('[');
        return open < 0 || !nodeId.EndsWith(']') ? (nodeId, null) : (nodeId[..open], nodeId[(open + 1)..^1]);
    }

    /// <summary>
    /// The results of a report: each testcase whose (classname, name) is one of <paramref name="nodeIds"/>' (unknown ones are left out). Failures and
    /// errors carry pytest's message and report text, whose last frame under <paramref name="rootDirectory"/> becomes the failure location.
    /// </summary>
    public static IReadOnlyList<TestResult> Parse(string xml, IEnumerable<string> nodeIds, string rootDirectory)
    {
        var byName = new Dictionary<(string, string), string>();
        foreach (var id in nodeIds) byName.TryAdd(Mangle(id), id);
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (XmlException) { return []; }
        var results = new List<TestResult>();
        foreach (var tc in doc.Descendants("testcase"))
        {
            var key = ((string?)tc.Attribute("classname") ?? "", (string?)tc.Attribute("name") ?? "");
            if (!byName.TryGetValue(key, out var nodeId)) continue;
            var (testId, caseLabel) = Split(nodeId);
            TimeSpan? duration = double.TryParse((string?)tc.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? TimeSpan.FromSeconds(seconds) : null;
            var failure = tc.Element("failure") ?? tc.Element("error");
            var skipped = tc.Element("skipped");
            var output = string.Join("\n", new[] { tc.Element("system-out")?.Value, tc.Element("system-err")?.Value }.Where(s => !string.IsNullOrEmpty(s)));
            if (failure is not null)
            {
                var text = failure.Value;
                // a traceback names files by their real path: /private/var/… for a folder opened as /var/…
                var location = StackTraces.FromPython(text, rootDirectory)
                    ?? (StackTraces.FromPython(text, PhysicalPaths.Resolve(rootDirectory)) is { } physical ? physical with { FilePath = PhysicalPaths.UnderLogical(physical.FilePath, rootDirectory) } : null);
                results.Add(new TestResult(testId, TestOutcome.Failed, duration, (string?)failure.Attribute("message") ?? FirstLine(text), TestLimits.Clip(text),
                    TestLimits.Clip(output.Length > 0 ? output : null), location, caseLabel));
            }
            else if (skipped is not null)
                results.Add(new TestResult(testId, TestOutcome.Skipped, duration, (string?)skipped.Attribute("message"), null, null, null, caseLabel));
            else
                results.Add(new TestResult(testId, TestOutcome.Passed, duration, null, null, TestLimits.Clip(output.Length > 0 ? output : null), null, caseLabel));
        }
        return results;
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}
