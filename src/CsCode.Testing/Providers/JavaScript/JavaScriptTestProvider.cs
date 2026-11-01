using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Debugging;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.JavaScript;

/// <summary>
/// JavaScript and TypeScript packages tested with Jest or Vitest. <b>Detection</b>: each <c>package.json</c> (outside node_modules) that declares
/// <c>jest</c> or <c>vitest</c> (dependencies, devDependencies, a <c>jest</c> key, or its <c>test</c> script). <b>Discovery</b> scans the test
/// files (<c>*.test.*</c>, <c>*.spec.*</c>, <c>__tests__/</c>; js, jsx, ts, tsx, mjs, cjs, mts, cts) with <see cref="JsTestScanner"/>.
/// <b>Running</b> runs the package's own runner with node (<c>node_modules/jest/bin/jest.js --json --outputFile … --testLocationInResults</c>, or
/// <c>node_modules/vitest/vitest.mjs run --reporter=json --outputFile …</c>), limited to the requested tests' files and a name pattern, and
/// reads the JSON report (<see cref="JestJsonReader"/>). <b>Debugging</b> launches the same command under js-debug (<c>pwa-node</c>), in band,
/// and reads the report when the session ends.
/// </summary>
public sealed partial class JavaScriptTestProvider(TestProviderContext context) : ITestProvider
{
    public const string ProviderId = "javascript";
    public const string NodePathSetting = "cscode.debug.nodePath";

    public string Id => ProviderId;
    public string Label => "JavaScript";
    public IReadOnlyList<string> LanguageIds { get; } = ["javascript", "typescript", "javascriptreact", "typescriptreact"];

    // ------------------------------------------------------------------------------------------------ detection

    public async Task<IReadOnlyList<TestProject>> DetectAsync(string folder, CancellationToken ct)
    {
        var result = new List<TestProject>();
        foreach (var file in SourceFiles.Find(folder, n => n == "package.json", 500, ct: ct))
        {
            if (await TestProviderContext.TryReadAsync(file, ct).ConfigureAwait(false) is not { } text) continue;
            if (Framework(text) is not { } framework) continue;
            var name = PackageName(text) ?? Path.GetFileName(Path.GetDirectoryName(file)!);
            result.Add(new TestProject(ProviderId, name, file, Path.GetDirectoryName(file)!, framework));
        }
        return result;
    }

    /// <summary>"jest" or "vitest" when the package.json declares one (vitest wins when both appear), else null.</summary>
    internal static string? Framework(string packageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(packageJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            bool Has(string section, string name) => root.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty(name, out _);
            var script = root.TryGetProperty("scripts", out var sc) && sc.ValueKind == JsonValueKind.Object && sc.TryGetProperty("test", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            if (Has("devDependencies", "vitest") || Has("dependencies", "vitest") || script.Contains("vitest", StringComparison.Ordinal)) return "vitest";
            if (Has("devDependencies", "jest") || Has("dependencies", "jest") || root.TryGetProperty("jest", out _) || script.Contains("jest", StringComparison.Ordinal)) return "jest";
            return null;
        }
        catch (JsonException) { return null; }
    }

    private static string? PackageName(string packageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(packageJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // ------------------------------------------------------------------------------------------------ discovery

    /// <summary>Whether a file name is a test file by the runners' default patterns (or it sits in a __tests__ directory).</summary>
    internal static bool IsTestFile(string path)
    {
        var name = Path.GetFileName(path);
        if (!SourceExtension().IsMatch(name)) return false;
        return TestName().IsMatch(name) || path.Replace('\\', '/').Contains("/__tests__/", StringComparison.Ordinal);
    }

    public async Task<IReadOnlyList<DiscoveredTest>> DiscoverAsync(TestProject project, ITestRunSink log, CancellationToken ct)
    {
        var tests = new List<DiscoveredTest>();
        foreach (var file in SourceFiles.Find(project.Directory, _ => true, 50_000, stopAt: n => n == "package.json", ct: ct).Where(IsTestFile))
        {
            if (await TestProviderContext.TryReadAsync(file, ct).ConfigureAwait(false) is not { } text) continue;
            var fileId = JestJsonReader.RelativeId(file, project.Directory);
            foreach (var f in JsTestScanner.Scan(text))
            {
                var groups = new List<TestGroup> { new(fileId, TestNodeKind.File, new TestLocation(file, 0)) };
                groups.AddRange(f.Describes.Select(d => new TestGroup(d, TestNodeKind.Suite)));
                tests.Add(new DiscoveredTest(fileId + "::" + f.Name, f.Title, groups, new TestLocation(file, f.Line), f.Skipped ? "skipped" : null));
                if (tests.Count >= TestLimits.MaxTestsPerProject) return tests;
            }
        }
        return tests;
    }

    public IReadOnlyList<SourceTest> ScanSource(string filePath, string text) =>
        IsTestFile(filePath) ? [.. JsTestScanner.Scan(text).Select(f => new SourceTest(f.Name, f.Line, f.Title))] : [];

    public bool Matches(DiscoveredTest test, string filePath, SourceTest source) =>
        test.Location is { } loc && ITestProvider.PathsEqual(loc.FilePath, filePath) && test.Id.EndsWith("::" + source.Name, StringComparison.Ordinal);

    // ------------------------------------------------------------------------------------------------ running

    /// <summary>The runner script in the package's node_modules, or null when it is not installed.</summary>
    internal static string? RunnerScript(TestProject project)
    {
        var candidates = project.Framework == "vitest"
            ? new[] { Path.Combine("node_modules", "vitest", "vitest.mjs") }
            : [Path.Combine("node_modules", "jest", "bin", "jest.js"), Path.Combine("node_modules", "jest-cli", "bin", "jest.js")];
        for (var dir = project.Directory; dir is not null; dir = Path.GetDirectoryName(dir))
            foreach (var c in candidates)
                if (File.Exists(Path.Combine(dir, c))) return Path.Combine(dir, c);
        return null;
    }

    /// <summary>
    /// The runner's arguments after the script: report options, the files of the requested tests and a name pattern matching exactly their
    /// names (Jest/Vitest match the pattern against the describe names and the test's own, joined with spaces).
    /// </summary>
    internal static List<string> RunnerArguments(TestProject project, IReadOnlyList<string>? testIds, string reportFile, bool debug)
    {
        var args = new List<string>();
        if (project.Framework == "vitest")
        {
            args.AddRange(["run", "--reporter=json", "--outputFile=" + reportFile]);
            if (debug) args.AddRange(["--no-file-parallelism", "--test-timeout=0"]);
        }
        else
        {
            args.AddRange(["--json", "--outputFile=" + reportFile, "--testLocationInResults", "--ci=false"]);
            if (debug) args.AddRange(["--runInBand", "--testTimeout=2147483647"]);
        }
        if (testIds is { Count: > 0 })
        {
            var files = new SortedSet<string>(StringComparer.Ordinal);
            var names = new List<string>();
            var wholeFiles = true;
            foreach (var id in testIds)
            {
                var sep = id.IndexOf("::", StringComparison.Ordinal);
                files.Add(sep < 0 ? id : id[..sep]);
                if (sep >= 0) { names.Add(id[(sep + 2)..]); wholeFiles = false; }
            }
            if (!wholeFiles && names.Count <= 200)
            {
                var pattern = "^(" + string.Join('|', names.Select(n => Regex.Escape(n.Replace(" > ", " ", StringComparison.Ordinal)).Replace("\\ ", " ", StringComparison.Ordinal))) + ")$";
                args.Add(project.Framework == "vitest" ? "--testNamePattern=" + pattern : "--testNamePattern=" + pattern);
            }
            if (files.Count <= 200) args.AddRange(files);
        }
        return args;
    }

    private string Node() => context.Setting(NodePathSetting) is { Length: > 0 } n ? n : ProcessRunner.FindOnPath("node") ?? "node";

    public async Task<TestRunOutcome> RunAsync(TestRunRequest request, ITestRunSink sink, CancellationToken ct)
    {
        var project = request.Project;
        if (RunnerScript(project) is not { } script)
            return new TestRunOutcome(-1, $"{Capital(project.Framework)} is not installed in {project.Directory} (run npm install there).");
        var temp = TestProviderContext.NewTempDirectory();
        try
        {
            var report = Path.Combine(temp, "report.json");
            var args = new List<string> { script };
            args.AddRange(RunnerArguments(project, request.TestIds, report, debug: false));
            sink.Status($"Running {project.Name} ({project.Framework})…");
            sink.Output($"> node {string.Join(' ', args)}");
            var env = new Dictionary<string, string> { ["CI"] = "1", ["FORCE_COLOR"] = "0", ["NO_COLOR"] = "1" };
            var exit = await context.Processes.RunAsync(new ProcessSpec(Node(), args, project.Directory, env), sink.Output, sink.Output, ct).ConfigureAwait(false);
            if (exit is null) return TestRunOutcome.Cancelled;
            return await ReportAsync(project, report, exit, sink).ConfigureAwait(false);
        }
        finally { TestProviderContext.DeleteQuietly(temp); }
    }

    private static async Task<TestRunOutcome> ReportAsync(TestProject project, string report, int? exit, ITestRunSink sink)
    {
        var json = await TestProviderContext.TryReadAsync(report, CancellationToken.None).ConfigureAwait(false);
        if (json is null) return new TestRunOutcome(exit, $"{Capital(project.Framework)} wrote no report (exit code {exit}): see the Tests output.");
        try
        {
            var (results, failures) = JestJsonReader.Read(json, project.Directory);
            foreach (var (test, result) in results)
            {
                sink.Discovered(project, test);
                sink.Finished(project, result);
            }
            foreach (var f in failures) sink.Output($"{f.FileId}: {f.Message}");
            return new TestRunOutcome(exit, failures.Count > 0 && results.Count == 0 ? $"{failures[0].FileId} could not run: {failures[0].Message.Split('\n')[0]}" : null);
        }
        catch (JsonException ex) { return new TestRunOutcome(exit, $"The {project.Framework} report could not be read: {ex.Message}"); }
    }

    public async Task<TestRunOutcome> DebugAsync(TestRunRequest request, ITestRunSink sink, ITestDebugLauncher debugger, CancellationToken ct)
    {
        var project = request.Project;
        if (RunnerScript(project) is not { } script)
            return new TestRunOutcome(-1, $"{Capital(project.Framework)} is not installed in {project.Directory} (run npm install there).");
        var temp = TestProviderContext.NewTempDirectory();
        try
        {
            var report = Path.Combine(temp, "report.json");
            var name = $"Debug tests: {project.Name}";
            var props = new Dictionary<string, JsonElement>
            {
                ["type"] = JsonSerializer.SerializeToElement("pwa-node"),
                ["request"] = JsonSerializer.SerializeToElement("launch"),
                ["name"] = JsonSerializer.SerializeToElement(name),
                ["program"] = JsonSerializer.SerializeToElement(script),
                ["args"] = JsonSerializer.SerializeToElement(RunnerArguments(project, request.TestIds, report, debug: true)),
                ["cwd"] = JsonSerializer.SerializeToElement(project.Directory),
                ["console"] = JsonSerializer.SerializeToElement("internalConsole"),
                ["autoAttachChildProcesses"] = JsonSerializer.SerializeToElement(true),
                ["skipFiles"] = JsonSerializer.SerializeToElement(new[] { "<node_internals>/**", "**/node_modules/**" }),
                ["env"] = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["CI"] = "1", ["FORCE_COLOR"] = "0" }),
            };
            sink.Status($"Debugging {project.Name}…");
            var session = await debugger.StartAsync(new DebugConfiguration("pwa-node", "launch", name, props, new Uri(project.Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)), ct).ConfigureAwait(false);
            if (session is null) return new TestRunOutcome(-1, "The debugger (js-debug) did not start: see the Debug Console.");
            var exit = await DebugSessions.PumpOutputAsync(session, sink.Output, ct).ConfigureAwait(false);
            return await ReportAsync(project, report, exit, sink).ConfigureAwait(false);
        }
        finally { TestProviderContext.DeleteQuietly(temp); }
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    [GeneratedRegex(@"\.(js|jsx|ts|tsx|mjs|cjs|mts|cts)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SourceExtension();

    [GeneratedRegex(@"\.(test|spec)\.[^.]+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TestName();
}
