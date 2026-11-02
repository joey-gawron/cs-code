using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.Cpp;

/// <summary>
/// Reads the <c>TestCase</c> elements of a framework's XML report one at a time, tolerating a report that stops in the middle (the executable
/// crashed, or the debug session was stopped): every complete test case is read, and the one left open is named.
/// </summary>
internal static class XmlTestCases
{
    /// <summary>
    /// Calls <paramref name="onCase"/> for each complete <c>TestCase</c> (with the <c>name</c> of the enclosing <c>TestSuite</c>, doctest's) and
    /// returns the <c>name</c> of a test case the report started and never finished (null when it is complete).
    /// </summary>
    public static string? Read(TextReader text, Action<XElement, string?> onCase)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, CheckCharacters = false };
        using var reader = XmlReader.Create(text, settings);
        string? suite = null;
        string? open = null;
        try
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "TestSuite") { suite = null; continue; }
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "TestSuite") { suite = reader.IsEmptyElement ? null : reader.GetAttribute("name"); continue; }
                if (reader.LocalName != "TestCase") continue;
                open = reader.GetAttribute("name");
                using (var sub = reader.ReadSubtree())
                {
                    sub.MoveToContent();
                    var element = (XElement)XNode.ReadFrom(sub);
                    open = null;
                    onCase(element, suite);
                }
            }
        }
        catch (XmlException) { }
        return open;
    }

    public static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

    public static int IntAttr(XElement e, string name) =>
        int.TryParse(Attr(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    public static TimeSpan? Seconds(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? TimeSpan.FromSeconds(s) : null;

    /// <summary>An element's text with the indentation the reporters add to every line removed.</summary>
    public static string Text(XElement e)
    {
        var lines = e.Value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }

    /// <summary>An <c>Expression</c> element as a failure line: <c>CHECK( a == b ) with expansion: 4 == 5</c>.</summary>
    public static string Expression(XElement e)
    {
        var original = e.Element("Original") is { } o ? Text(o) : "";
        var expanded = e.Element("Expanded") is { } x ? Text(x) : "";
        var type = Attr(e, "type") ?? "CHECK";
        var sb = new StringBuilder(type).Append("( ").Append(original).Append(" )");
        if (expanded.Length > 0 && expanded != original) sb.Append(" with expansion: ").Append(expanded);
        foreach (var inner in e.Elements().Where(c => c.Name.LocalName is "Exception" or "Info"))
            sb.Append('\n').Append(Text(inner));
        return sb.ToString();
    }
}

/// <summary>
/// Catch2's XML: the listing of <c>--list-tests --reporter xml</c> (v3; each <c>TestCase</c> with <c>Name</c>, <c>ClassName</c>, <c>Tags</c> and
/// <c>SourceInfo/File</c>, <c>/Line</c>) and the report of <c>--reporter xml</c> (v3, and v2's <c>Catch</c>/<c>Group</c> report): each
/// <c>TestCase</c> (<c>name</c>, <c>filename</c>, <c>line</c>) with its failed <c>Expression</c>s, <c>Exception</c>s, <c>FatalErrorCondition</c>s
/// and <c>Failure</c>s (also inside <c>Section</c>s), its <c>Skip</c>s, and its <c>OverallResult</c> (<c>success</c>, <c>skips</c>,
/// <c>durationInSeconds</c>, the captured <c>StdOut</c>/<c>StdErr</c>).
/// </summary>
public static class CatchXmlFormat
{
    public static IReadOnlyList<CppListedTest> ParseListing(string xml)
    {
        var result = new List<CppListedTest>();
        var start = xml.IndexOf('<');
        if (start < 0) return result;
        var doc = XDocument.Parse(xml[start..]);
        foreach (var tc in doc.Descendants("TestCase"))
        {
            var name = tc.Element("Name")?.Value;
            if (string.IsNullOrEmpty(name)) continue;
            var tags = tc.Element("Tags")?.Value ?? "";
            var source = tc.Element("SourceInfo");
            var file = source?.Element("File")?.Value;
            var line = int.TryParse(source?.Element("Line")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0;
            var hidden = tags.Contains("[.", StringComparison.Ordinal) || tags.Contains("[!hide]", StringComparison.Ordinal);
            var label = tags.Length > 0 ? $"{name}  {tags}" : name;
            result.Add(new CppListedTest(name, label, [], file, line, hidden ? "hidden ([.] tag): runs only when chosen" : null));
            if (result.Count >= TestLimits.MaxTestsPerProject) break;
        }
        return result;
    }

    /// <summary>Reads a run's report; returns the test case the report left open (crash), else null.</summary>
    public static string? ParseResults(TextReader xml, Action<CppTestResult> onResult) =>
        XmlTestCases.Read(xml, (tc, _) =>
        {
            if (XmlTestCases.Attr(tc, "name") is not { } name) return;
            var failures = new List<string>();
            string? failureFile = null;
            var failureLine = 0;
            var skips = new List<string>();
            Collect(tc, null, failures, skips, ref failureFile, ref failureLine);
            var overall = tc.Element("OverallResult");
            var success = XmlTestCases.Attr(overall ?? tc, "success") != "false" && failures.Count == 0;
            var skipped = overall is not null && XmlTestCases.IntAttr(overall, "skips") > 0 || skips.Count > 0;
            var outcome = !success ? TestOutcome.Failed : skipped ? TestOutcome.Skipped : TestOutcome.Passed;
            var output = new StringBuilder();
            if (overall?.Element("StdOut") is { } so) output.Append(XmlTestCases.Text(so));
            if (overall?.Element("StdErr") is { } se) output.Append(output.Length > 0 ? "\n" : "").Append(XmlTestCases.Text(se));
            var message = outcome == TestOutcome.Failed ? string.Join("\n\n", failures) : outcome == TestOutcome.Skipped ? string.Join("\n", skips) : null;
            onResult(new CppTestResult(name, outcome, XmlTestCases.Seconds(XmlTestCases.Attr(overall ?? tc, "durationInSeconds")),
                TestLimits.Clip(message is { Length: > 0 } ? message : outcome == TestOutcome.Failed ? "Failed." : null),
                TestLimits.Clip(output.Length > 0 ? output.ToString() : null), failureFile, failureLine));
        });

    private static void Collect(XElement parent, string? section, List<string> failures, List<string> skips, ref string? file, ref int line)
    {
        foreach (var e in parent.Elements())
        {
            string? text = e.Name.LocalName switch
            {
                "Expression" when XmlTestCases.Attr(e, "success") == "false" => XmlTestCases.Expression(e),
                "Exception" => "Unexpected exception: " + XmlTestCases.Text(e),
                "FatalErrorCondition" => "Fatal error: " + XmlTestCases.Text(e),
                "Failure" => XmlTestCases.Text(e),
                _ => null,
            };
            if (e.Name.LocalName == "Section")
            {
                var name = XmlTestCases.Attr(e, "name");
                Collect(e, section is null ? name : section + " / " + name, failures, skips, ref file, ref line);
                continue;
            }
            if (e.Name.LocalName == "Skip")
            {
                skips.Add(XmlTestCases.Text(e));
                continue;
            }
            if (text is null) continue;
            var where = XmlTestCases.Attr(e, "filename") is { } f ? $"{f}:{XmlTestCases.IntAttr(e, "line")}: " : "";
            failures.Add((section is null ? "" : $"In section \"{section}\":\n") + where + text);
            if (file is null && XmlTestCases.Attr(e, "filename") is { } ff)
            {
                file = ff;
                line = XmlTestCases.IntAttr(e, "line");
            }
        }
    }

    /// <summary>
    /// A test spec naming exactly these test cases: each name with Catch2's special characters escaped (<c>\</c>, <c>,</c>, <c>[</c>, <c>]</c>,
    /// <c>*</c>, <c>"</c>, <c>~</c>), joined by commas (OR; separate arguments would be AND).
    /// </summary>
    public static string Filter(IEnumerable<string> names)
    {
        var sb = new StringBuilder();
        foreach (var name in names)
        {
            if (sb.Length > 0) sb.Append(',');
            foreach (var c in name)
            {
                if (c is '\\' or ',' or '[' or ']' or '*' or '"' or '~') sb.Append('\\');
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// doctest's XML: the listing of <c>--list-test-cases --reporters=xml</c> (each <c>TestCase</c> with <c>name</c>, <c>testsuite</c>,
/// <c>filename</c>, <c>line</c>, <c>skipped</c>) and the report of <c>--reporters=xml</c>: <c>TestSuite</c>s of <c>TestCase</c>s, each with
/// its failed <c>Expression</c>s, <c>Exception</c>s (<c>crash="true"</c> for a signal) and <c>Message</c>s (also inside <c>SubCase</c>s), and
/// <c>OverallResultsAsserts</c> (<c>test_case_success</c>, <c>duration</c>); a skipped test is an empty <c>TestCase skipped="true"</c>. A test's id
/// is its name, or <c>suite::name</c> in a test suite.
/// </summary>
public static class DoctestXmlFormat
{
    public static string Id(string? suite, string name) => string.IsNullOrEmpty(suite) ? name : suite + "::" + name;

    public static IReadOnlyList<CppListedTest> ParseListing(string xml)
    {
        var result = new List<CppListedTest>();
        var start = xml.IndexOf('<');
        if (start < 0) return result;
        var doc = XDocument.Parse(xml[start..]);
        foreach (var tc in doc.Descendants("TestCase"))
        {
            if (XmlTestCases.Attr(tc, "name") is not { Length: > 0 } name) continue;
            var suite = XmlTestCases.Attr(tc, "testsuite");
            var skipped = XmlTestCases.Attr(tc, "skipped") == "true";
            IReadOnlyList<TestGroup> groups = string.IsNullOrEmpty(suite) ? [] : [new TestGroup(suite, TestNodeKind.Suite)];
            result.Add(new CppListedTest(Id(suite, name), name.Trim(), groups, XmlTestCases.Attr(tc, "filename"), XmlTestCases.IntAttr(tc, "line"),
                skipped ? "skipped (doctest::skip): runs only when chosen by itself" : null));
            if (result.Count >= TestLimits.MaxTestsPerProject) break;
        }
        return result;
    }

    /// <summary>Reads a run's report; returns the test case the report left open (crash), else null.</summary>
    public static string? ParseResults(TextReader xml, Action<CppTestResult> onResult) =>
        XmlTestCases.Read(xml, (tc, suite) =>
        {
            if (XmlTestCases.Attr(tc, "name") is not { } name) return;
            var id = Id(suite, name);
            if (XmlTestCases.Attr(tc, "skipped") == "true")
            {
                onResult(new CppTestResult(id, TestOutcome.Skipped, null, "Skipped (doctest::skip)."));
                return;
            }
            var failures = new List<string>();
            string? failureFile = null;
            var failureLine = 0;
            Collect(tc, null, failures, ref failureFile, ref failureLine);
            var overall = tc.Element("OverallResultsAsserts");
            var success = overall is null ? failures.Count == 0 : XmlTestCases.Attr(overall, "test_case_success") != "false";
            var outcome = success ? TestOutcome.Passed : TestOutcome.Failed;
            // what the test printed lands in the report as text between the elements (doctest does not capture it)
            var printed = string.Join("\n", tc.Nodes().OfType<XText>().Select(t => t.Value.Trim()).Where(t => t.Length > 0));
            onResult(new CppTestResult(id, outcome, overall is null ? null : XmlTestCases.Seconds(XmlTestCases.Attr(overall, "duration")),
                TestLimits.Clip(outcome == TestOutcome.Failed ? (failures.Count > 0 ? string.Join("\n\n", failures) : "Failed.") : null),
                TestLimits.Clip(printed.Length > 0 ? printed : null), failureFile, failureLine));
        });

    private static void Collect(XElement parent, string? subcase, List<string> failures, ref string? file, ref int line)
    {
        foreach (var e in parent.Elements())
        {
            if (e.Name.LocalName == "SubCase")
            {
                var name = XmlTestCases.Attr(e, "name");
                Collect(e, subcase is null ? name : subcase + " / " + name, failures, ref file, ref line);
                continue;
            }
            string? text = e.Name.LocalName switch
            {
                "Expression" when XmlTestCases.Attr(e, "success") == "false" => XmlTestCases.Expression(e),
                "Exception" => (XmlTestCases.Attr(e, "crash") == "true" ? "Crashed: " : "Unexpected exception: ") + XmlTestCases.Text(e),
                "Message" when (XmlTestCases.Attr(e, "type") ?? "").Contains("ERROR", StringComparison.Ordinal) =>
                    (XmlTestCases.Attr(e, "type") ?? "") + ": " + (e.Element("Text") is { } t ? XmlTestCases.Text(t) : XmlTestCases.Text(e)),
                _ => null,
            };
            if (text is null) continue;
            var where = XmlTestCases.Attr(e, "filename") is { } f ? $"{f}:{XmlTestCases.IntAttr(e, "line")}: " : "";
            failures.Add((subcase is null ? "" : $"In subcase \"{subcase}\":\n") + where + text);
            if (file is null && XmlTestCases.Attr(e, "filename") is { } ff)
            {
                file = ff;
                line = XmlTestCases.IntAttr(e, "line");
            }
        }
    }

    /// <summary>A <c>--test-case</c> filter naming these test cases (commas escaped; doctest's <c>*</c> and <c>?</c> wildcards cannot be).</summary>
    public static string Filter(IEnumerable<string> names) => string.Join(",", names.Select(n => n.Replace(",", "\\,", StringComparison.Ordinal)));
}
