using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.CTest;

/// <summary>
/// Reads a ctest run. <b>Live</b>, from its standard output: each <c>N/M Test #K: name ....   Passed   0.01 sec</c> line reports the test at
/// once when it passed (a failure's details come later); with <c>--output-on-failure</c> the lines after a failed test's line are its output.
/// <b>At the end</b>, from the JUnit file <c>--output-junit</c> wrote (CTest 3.21+): every test not yet reported, with its captured output and
/// failure message. When there is no JUnit file (an older ctest, or the run was cut short) the live data is reported instead. Every test is
/// reported once. Not thread-safe: fed lines in order, then <see cref="Complete"/>.
/// </summary>
internal sealed partial class CTestResults(TestProject project, ITestRunSink sink)
{
    [GeneratedRegex(@"^\s*\d+/\d+\s+Test\s+#\d+:\s+(?<name>.+?)\s+\.+\s*(?<status>Passed|\*\*\*Failed|\*\*\*Not Run|Not Run|\*\*\*Skipped|\*\*\*Timeout|\*\*\*Exception:.*?|Subprocess aborted|Child aborted|Exit code 0x[0-9a-fA-F]+|\*\*\*\w+.*?)\s+(?<sec>[\d.]+)\s+sec\s*$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ResultLine();

    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TestResult> _live = new(StringComparer.Ordinal);
    private (string Name, StringBuilder Output)? _failing;

    public IReadOnlyCollection<string> Reported => _reported;

    public void OnLine(string line)
    {
        sink.Output(line);
        Match m;
        try { m = ResultLine().Match(line); }
        catch (RegexMatchTimeoutException) { return; }
        if (!m.Success)
        {
            // the output --output-on-failure prints after a failed test's line, up to the summary
            if (_failing is { } f && !line.StartsWith("% tests passed", StringComparison.Ordinal) && !line.Contains("tests passed,", StringComparison.Ordinal))
            {
                if (f.Output.Length < TestLimits.MaxOutputPerTest) f.Output.AppendLine(line);
            }
            else if (_failing is not null) EndFailing();
            return;
        }
        EndFailing();
        var name = m.Groups["name"].Value;
        var status = m.Groups["status"].Value;
        var duration = double.TryParse(m.Groups["sec"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;
        if (status == "Passed")
        {
            Report(new TestResult(name, TestOutcome.Passed, duration));
            return;
        }
        var outcome = status.Contains("Not Run", StringComparison.Ordinal) || status.Contains("Skipped", StringComparison.Ordinal) ? TestOutcome.Skipped : TestOutcome.Failed;
        _live[name] = new TestResult(name, outcome, duration, status.TrimStart('*'));
        if (outcome == TestOutcome.Failed) _failing = (name, new StringBuilder());
    }

    private void EndFailing()
    {
        if (_failing is not { } f) return;
        _failing = null;
        if (_live.TryGetValue(f.Name, out var r)) _live[f.Name] = r with { Output = TestLimits.Clip(f.Output.ToString().TrimEnd()) };
    }

    /// <summary>The run ended: everything not yet reported is, from <paramref name="junitPath"/> when it can be read, else from the live data.</summary>
    public void Complete(string? junitPath)
    {
        EndFailing();
        if (junitPath is not null && ReadJUnit(junitPath) is { } fromFile)
            foreach (var r in fromFile) Report(r);
        foreach (var r in _live.Values) Report(r);
    }

    private void Report(TestResult result)
    {
        if (!_reported.Add(result.TestId)) return;
        sink.Finished(project, result);
    }

    /// <summary>CTest's JUnit file: one <c>testcase</c> per test with <c>status</c> run / fail / disabled / notrun, <c>failure</c>, <c>skipped</c> and <c>system-out</c>.</summary>
    internal static IReadOnlyList<TestResult>? ReadJUnit(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64L * 1024 * 1024) return null;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(path, settings);
            var doc = XDocument.Load(reader);
            var results = new List<TestResult>();
            foreach (var tc in doc.Descendants("testcase"))
            {
                var name = (string?)tc.Attribute("name");
                if (string.IsNullOrEmpty(name)) continue;
                var time = double.TryParse((string?)tc.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? TimeSpan.FromSeconds(t) : (TimeSpan?)null;
                var status = (string?)tc.Attribute("status") ?? "run";
                var failure = tc.Element("failure");
                var skipped = tc.Element("skipped");
                var output = TestLimits.Clip(((string?)tc.Element("system-out"))?.TrimEnd());
                var outcome = failure is not null || status == "fail" ? TestOutcome.Failed
                    : skipped is not null || status is "disabled" or "notrun" ? TestOutcome.Skipped
                    : TestOutcome.Passed;
                var message = outcome switch
                {
                    TestOutcome.Failed => (string?)failure?.Attribute("message") ?? "Failed",
                    TestOutcome.Skipped => (string?)skipped?.Attribute("message") ?? status,
                    _ => null,
                };
                results.Add(new TestResult(name, outcome, time, message, Output: string.IsNullOrEmpty(output) ? null : output));
            }
            return results;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException) { return null; }
    }
}
