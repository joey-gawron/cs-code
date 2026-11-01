using System.Globalization;
using System.Xml.Linq;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.DotNet;

/// <summary>
/// Reads a Visual Studio test results file (<c>dotnet test --logger trx</c>): each <c>UnitTestResult</c> joined with its <c>UnitTest</c> definition
/// (class and method name), so a result's id is <c>Ns.Class.Method</c> like the scanner's; a data row (a test name other than that) becomes a case.
/// Outcomes: Passed, Failed (and Error, Timeout, Aborted), NotExecuted / Inconclusive / Skipped (skipped).
/// </summary>
public static class TrxReader
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static IReadOnlyList<TestResult> Read(string xml, string projectDirectory)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root;
        if (root is null) return [];
        var ns = root.Name.Namespace == XNamespace.None ? XNamespace.None : Ns;
        var methods = new Dictionary<string, (string Class, string Method)>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in root.Descendants(ns + "UnitTest"))
        {
            var id = (string?)unit.Attribute("id");
            var tm = unit.Element(ns + "TestMethod");
            if (id is null || tm is null) continue;
            var cls = (string?)tm.Attribute("className") ?? "";
            var comma = cls.IndexOf(',');
            if (comma >= 0) cls = cls[..comma].Trim(); // older MSTest: "Ns.Class, Assembly, Version=…"
            methods[id] = (cls, (string?)tm.Attribute("name") ?? "");
        }
        var results = new List<TestResult>();
        foreach (var r in root.Descendants(ns + "UnitTestResult"))
        {
            var testName = (string?)r.Attribute("testName") ?? "";
            var testDefId = (string?)r.Attribute("testId");
            string id;
            if (testDefId is not null && methods.TryGetValue(testDefId, out var m) && m.Class.Length > 0)
            {
                var method = m.Method;
                var paren = method.IndexOf('(');
                if (paren > 0) method = method[..paren];
                id = m.Class + "." + method;
            }
            else
            {
                var paren = testName.IndexOf('(');
                id = paren > 0 ? testName[..paren] : testName;
            }
            var outcome = ((string?)r.Attribute("outcome")) switch
            {
                "Passed" => TestOutcome.Passed,
                "Failed" or "Error" or "Timeout" or "Aborted" => TestOutcome.Failed,
                "NotExecuted" or "Inconclusive" or "Skipped" or "Pending" or "Disconnected" or "Warning" => TestOutcome.Skipped,
                _ => TestOutcome.None,
            };
            TimeSpan? duration = TimeSpan.TryParse((string?)r.Attribute("duration"), CultureInfo.InvariantCulture, out var d) ? d : null;
            var output = r.Element(ns + "Output");
            var stdout = (string?)output?.Element(ns + "StdOut");
            var errorInfo = output?.Element(ns + "ErrorInfo");
            var message = (string?)errorInfo?.Element(ns + "Message");
            var stack = (string?)errorInfo?.Element(ns + "StackTrace");
            if (outcome == TestOutcome.Skipped && message is null)
                message = output?.Element(ns + "TextMessages")?.Elements(ns + "Message").Select(x => x.Value).FirstOrDefault();
            var lastDot = id.LastIndexOf('.');
            var plainName = lastDot >= 0 && testName.StartsWith(id[..lastDot] + ".", StringComparison.Ordinal) ? testName[(lastDot + 1)..] : testName;
            var isCase = testName != id && plainName != id[(lastDot + 1)..];
            results.Add(new TestResult(id, outcome, duration, TestLimits.Clip(message?.Trim()), TestLimits.Clip(stack?.TrimEnd()), TestLimits.Clip(stdout),
                outcome == TestOutcome.Failed ? StackTraces.FromDotNet(stack, projectDirectory) : null,
                isCase ? plainName : null));
        }
        return results;
    }
}
