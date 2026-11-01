using System.Text.Json;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.JavaScript;

/// <summary>
/// Reads the JSON report of Jest (<c>--json --outputFile</c>) or Vitest (<c>--reporter=json --outputFile</c>, the same shape): each file's
/// <c>assertionResults</c> with <c>ancestorTitles</c>, <c>title</c>, <c>status</c> (passed, failed, pending/skipped/todo/disabled), <c>duration</c>
/// (ms), <c>failureMessages</c> and, with <c>--testLocationInResults</c>, <c>location</c>. A test's id is its file (relative to the package, with
/// <c>/</c>) and its names: <c>src/math.test.js::math &gt; adds</c>. A file that failed to run at all (a syntax error) becomes a failed result
/// for that file's own id (<c>src/math.test.js</c>).
/// </summary>
public static class JestJsonReader
{
    public sealed record FileFailure(string FileId, string Message);

    public static (IReadOnlyList<(DiscoveredTest Test, TestResult Result)> Results, IReadOnlyList<FileFailure> FileFailures) Read(string json, string packageDirectory)
    {
        var results = new List<(DiscoveredTest, TestResult)>();
        var failures = new List<FileFailure>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("testResults", out var files) || files.ValueKind != JsonValueKind.Array) return (results, failures);
        foreach (var file in files.EnumerateArray())
        {
            var path = Str(file, "name") ?? Str(file, "testFilePath");
            if (path is null) continue;
            path = PhysicalPaths.UnderLogical(path, packageDirectory);
            var fileId = RelativeId(path, packageDirectory);
            var assertions = file.TryGetProperty("assertionResults", out var a) && a.ValueKind == JsonValueKind.Array ? a : file.TryGetProperty("testResults", out var b) ? b : default;
            var count = 0;
            if (assertions.ValueKind == JsonValueKind.Array)
                foreach (var t in assertions.EnumerateArray())
                {
                    count++;
                    var title = Str(t, "title") ?? "";
                    var ancestors = t.TryGetProperty("ancestorTitles", out var at) && at.ValueKind == JsonValueKind.Array
                        ? at.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToList()
                        : [];
                    var id = fileId + "::" + string.Join(" > ", [.. ancestors, title]);
                    var outcome = Str(t, "status") switch
                    {
                        "passed" => TestOutcome.Passed,
                        "failed" => TestOutcome.Failed,
                        "pending" or "skipped" or "todo" or "disabled" or "focused" => TestOutcome.Skipped,
                        _ => TestOutcome.None,
                    };
                    TimeSpan? duration = t.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var ms) ? TimeSpan.FromMilliseconds(ms) : null;
                    var messages = t.TryGetProperty("failureMessages", out var fm) && fm.ValueKind == JsonValueKind.Array
                        ? fm.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                        : [];
                    var full = string.Join(Environment.NewLine, messages);
                    var firstLine = full.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                    TestLocation? loc = t.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.Object && l.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number && line.TryGetInt32(out var ln)
                        ? new TestLocation(path, Math.Max(0, ln - 1)) : null;
                    var groups = new List<TestGroup> { new(fileId, TestNodeKind.File, new TestLocation(path, 0)) };
                    groups.AddRange(ancestors.Select(x => new TestGroup(x, TestNodeKind.Suite)));
                    results.Add((new DiscoveredTest(id, title, groups, loc),
                        new TestResult(id, outcome, duration, TestLimits.Clip(outcome == TestOutcome.Failed ? StripAnsi(firstLine ?? "The test failed.") : null),
                            TestLimits.Clip(outcome == TestOutcome.Failed ? StripAnsi(full) : null), null,
                            outcome == TestOutcome.Failed ? Rebase(StackTraces.FromJavaScript(full, packageDirectory) ?? StackTraces.FromJavaScript(full, PhysicalPaths.Resolve(packageDirectory)), packageDirectory) ?? loc : null)));
                }
            var status = Str(file, "status");
            var message = Str(file, "message");
            if (count == 0 && status == "failed" && !string.IsNullOrWhiteSpace(message)) failures.Add(new FileFailure(fileId, StripAnsi(message)));
        }
        return (results, failures);
    }

    /// <summary>The file's id: its path relative to the package directory, with forward slashes.</summary>
    public static string RelativeId(string path, string packageDirectory) =>
        Path.GetRelativePath(packageDirectory, path).Replace('\\', '/');

    private static TestLocation? Rebase(TestLocation? loc, string dir) => loc is null ? null : loc with { FilePath = PhysicalPaths.UnderLogical(loc.FilePath, dir) };

    internal static string StripAnsi(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\x1B\[[0-9;]*[A-Za-z]", "");

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
