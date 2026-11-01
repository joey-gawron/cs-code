using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Debugging;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.DotNet;

/// <summary>
/// .NET test projects (C#). <b>Detection</b>: every project file under the folder whose text (or a <c>Directory.Build.props</c> above it) mentions a
/// test framework is evaluated with <c>dotnet msbuild -getProperty</c> (no build: <c>IsTestProject</c>, <c>TargetPath</c>, <c>TargetFramework(s)</c>,
/// the package references); <c>IsTestProject=true</c> makes it a test project, and an <c>xunit.v3</c> reference makes it an xUnit v3 one.
/// <b>Discovery</b> scans the project's C# files (<see cref="CSharpTestScanner"/>): instant, no build, every test has its file and line; tests the
/// scan cannot see (inherited, generated) are added when a run reports them. <b>Running</b>: an xUnit v3 project is built
/// (<c>dotnet build</c>) and its test assembly run directly with <c>-reporter json</c> and one <c>-method</c> per requested test, so each result
/// arrives as the test ends; any other project (xUnit v2, NUnit, MSTest through VSTest) runs under <c>dotnet test --logger trx</c> with a
/// <c>FullyQualifiedName</c> filter and the TRX file is read at the end. <b>Debugging</b>: xUnit v3 — the assembly launched under netcoredbg
/// (<c>coreclr</c>) with the same arguments, results read from the session's output; VSTest — <c>dotnet test</c> with
/// <c>VSTEST_HOST_DEBUG=1</c>, netcoredbg attached to the test host it announces, results from the TRX file.
/// </summary>
public sealed partial class DotNetTestProvider(TestProviderContext context) : ITestProvider
{
    public const string ProviderId = "dotnet";
    public const string FrameworkXunitV3 = "xunit.v3";
    public const string FrameworkVsTest = "vstest";

    /// <summary>Beyond this many requested tests the filter is dropped and the whole project runs (command lines have limits).</summary>
    internal const int MaxFilteredTests = 300;

    private readonly ConcurrentDictionary<string, ProjectInfo> _info = new(StringComparer.Ordinal);

    internal sealed record ProjectInfo(string TargetPath, string? TargetFramework, bool XunitV3);

    public string Id => ProviderId;
    public string Label => ".NET";
    public IReadOnlyList<string> LanguageIds { get; } = ["csharp"];

    // ------------------------------------------------------------------------------------------------ detection

    public async Task<IReadOnlyList<TestProject>> DetectAsync(string folder, CancellationToken ct)
    {
        var files = SourceFiles.Find(folder, n => n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase), 2000, ct: ct);
        var candidates = new List<string>();
        foreach (var f in files)
            if (await LooksLikeTestProjectAsync(f, folder, ct).ConfigureAwait(false)) candidates.Add(f);
        var found = new ConcurrentBag<TestProject>();
        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (file, token) =>
        {
            if (await EvaluateAsync(file, token).ConfigureAwait(false) is { } project) found.Add(project);
        }).ConfigureAwait(false);
        return [.. found.OrderBy(p => p.FilePath, StringComparer.Ordinal)];
    }

    /// <summary>A cheap filter before evaluating: the project file, or a Directory.Build.props/targets between it and the folder, mentions a test framework or IsTestProject.</summary>
    internal static async Task<bool> LooksLikeTestProjectAsync(string projectFile, string folder, CancellationToken ct)
    {
        if (await TestProviderContext.TryReadAsync(projectFile, ct).ConfigureAwait(false) is { } text && MentionsTests(text)) return true;
        for (var dir = Path.GetDirectoryName(projectFile); dir is not null && dir.Length >= folder.TrimEnd(Path.DirectorySeparatorChar).Length; dir = Path.GetDirectoryName(dir))
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
                if (await TestProviderContext.TryReadAsync(Path.Combine(dir, name), ct).ConfigureAwait(false) is { } props && props.Contains("IsTestProject", StringComparison.Ordinal))
                    return true;
        return false;
    }

    internal static bool MentionsTests(string text) =>
        text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) || text.Contains("xunit", StringComparison.OrdinalIgnoreCase)
        || text.Contains("NUnit", StringComparison.OrdinalIgnoreCase) || text.Contains("MSTest", StringComparison.OrdinalIgnoreCase)
        || text.Contains("IsTestProject", StringComparison.Ordinal) || text.Contains("TUnit", StringComparison.Ordinal);

    private async Task<TestProject?> EvaluateAsync(string projectFile, CancellationToken ct)
    {
        var json = new System.Text.StringBuilder();
        var dir = Path.GetDirectoryName(projectFile)!;
        var exit = await context.Processes.RunAsync(new ProcessSpec(ProcessRunner.FindDotnet(),
            ["msbuild", projectFile, "-nologo", "-getProperty:IsTestProject", "-getProperty:TargetPath", "-getProperty:TargetFramework", "-getProperty:TargetFrameworks", "-getItem:PackageReference"],
            dir, NoLogo), l => json.AppendLine(l), _ => { }, ct).ConfigureAwait(false);
        if (exit != 0) return null;
        var parsed = ParseEvaluation(json.ToString());
        if (parsed is null || !parsed.Value.IsTestProject) return null;
        var (_, targetPath, tfm, xunitV3) = parsed.Value;
        var project = new TestProject(ProviderId, Path.GetFileNameWithoutExtension(projectFile), projectFile, dir, xunitV3 ? FrameworkXunitV3 : FrameworkVsTest);
        _info[project.Key] = new ProjectInfo(targetPath, tfm, xunitV3);
        return project;
    }

    /// <summary>The evaluation's answer (<c>-getProperty</c>/<c>-getItem</c> JSON), or null when it is not JSON.</summary>
    internal static (bool IsTestProject, string TargetPath, string? TargetFramework, bool XunitV3)? ParseEvaluation(string json)
    {
        var start = json.IndexOf('{');
        if (start < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(json[start..]);
            var props = doc.RootElement.TryGetProperty("Properties", out var p) ? p : default;
            string P(string name) => props.ValueKind == JsonValueKind.Object && props.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var isTest = string.Equals(P("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase);
            var tfm = P("TargetFramework");
            if (tfm.Length == 0 && P("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { } first) tfm = first;
            var xunitV3 = doc.RootElement.TryGetProperty("Items", out var items) && items.TryGetProperty("PackageReference", out var refs) && refs.ValueKind == JsonValueKind.Array
                && refs.EnumerateArray().Any(r => r.TryGetProperty("Identity", out var id) && id.GetString() is { } name
                    && (name.Equals("xunit.v3", StringComparison.OrdinalIgnoreCase) || name.StartsWith("xunit.v3.core", StringComparison.OrdinalIgnoreCase)));
            return (isTest, P("TargetPath"), tfm.Length > 0 ? tfm : null, xunitV3);
        }
        catch (JsonException) { return null; }
    }

    // ------------------------------------------------------------------------------------------------ discovery

    public async Task<IReadOnlyList<DiscoveredTest>> DiscoverAsync(TestProject project, ITestRunSink log, CancellationToken ct)
    {
        var files = SourceFiles.Find(project.Directory, n => n.EndsWith(".cs", StringComparison.OrdinalIgnoreCase), 20_000,
            stopAt: n => n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase), ct);
        var tests = new List<DiscoveredTest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (new FileInfo(file).Length > TestLimits.MaxScannedFileBytes) continue;
            if (await TestProviderContext.TryReadAsync(file, ct).ConfigureAwait(false) is not { } text) continue;
            if (!text.Contains('[')) continue;
            foreach (var f in CSharpTestScanner.Scan(text))
            {
                if (!seen.Add(f.FullName)) continue; // a partial class's other half, an overload
                var full = (f.Namespace.Length > 0 ? f.Namespace + "." : "") + f.ClassName;
                tests.Add(new DiscoveredTest(f.FullName, f.Method, XunitJsonReporter.DotNetGroups(full, new TestLocation(file, f.ClassLine)), new TestLocation(file, f.Line), f.SkipReason));
                if (tests.Count >= TestLimits.MaxTestsPerProject) return tests;
            }
        }
        return tests;
    }

    public IReadOnlyList<SourceTest> ScanSource(string filePath, string text) =>
        [.. CSharpTestScanner.Scan(text).Select(f => new SourceTest(f.FullName, f.Line, f.Method))];

    public bool Matches(DiscoveredTest test, string filePath, SourceTest source) => test.Id == source.Name;

    // ------------------------------------------------------------------------------------------------ running

    public async Task<TestRunOutcome> RunAsync(TestRunRequest request, ITestRunSink sink, CancellationToken ct)
    {
        var info = await InfoAsync(request.Project, ct).ConfigureAwait(false);
        if (info is null) return new TestRunOutcome(-1, $"Could not evaluate {request.Project.FilePath} with dotnet msbuild.");
        return info.XunitV3 ? await RunXunitV3Async(request, info, sink, ct).ConfigureAwait(false) : await RunVsTestAsync(request, info, sink, null, ct).ConfigureAwait(false);
    }

    private async Task<ProjectInfo?> InfoAsync(TestProject project, CancellationToken ct)
    {
        if (_info.TryGetValue(project.Key, out var info)) return info;
        await EvaluateAsync(project.FilePath, ct).ConfigureAwait(false);
        return _info.GetValueOrDefault(project.Key);
    }

    private async Task<int?> BuildAsync(TestProject project, ProjectInfo info, ITestRunSink sink, CancellationToken ct)
    {
        sink.Status($"Building {project.Name}…");
        var args = new List<string> { "build", project.FilePath, "-nologo", "-v:q", "-clp:NoSummary" };
        if (info.TargetFramework is { } tfm) { args.Add("-f"); args.Add(tfm); }
        sink.Output($"> dotnet {string.Join(' ', args)}");
        return await context.Processes.RunAsync(new ProcessSpec(ProcessRunner.FindDotnet(), args, project.Directory, NoLogo), sink.Output, sink.Output, ct).ConfigureAwait(false);
    }

    /// <summary>The runner's arguments for an xUnit v3 assembly: the JSON reporter and one <c>-method</c> per test (none: every test).</summary>
    internal static List<string> XunitArguments(string targetPath, IReadOnlyList<string>? testIds)
    {
        var args = new List<string> { targetPath, "-reporter", "json", "-noColor", "-noLogo" };
        if (testIds is { Count: > 0 and <= MaxFilteredTests })
            foreach (var id in testIds) { args.Add("-method"); args.Add(id); }
        return args;
    }

    private async Task<TestRunOutcome> RunXunitV3Async(TestRunRequest request, ProjectInfo info, ITestRunSink sink, CancellationToken ct)
    {
        var project = request.Project;
        var built = await BuildAsync(project, info, sink, ct).ConfigureAwait(false);
        if (built is null) return TestRunOutcome.Cancelled;
        if (built != 0) return new TestRunOutcome(built, $"The build of {project.Name} failed: see the Tests output.");
        sink.Status($"Running {project.Name}…");
        var reporter = new XunitJsonReporter(project, sink);
        var args = XunitArguments(info.TargetPath, request.TestIds);
        sink.Output($"> dotnet {string.Join(' ', args)}");
        var exit = await context.Processes.RunAsync(new ProcessSpec(ProcessRunner.FindDotnet(), args, Path.GetDirectoryName(info.TargetPath) ?? project.Directory, NoLogo),
            reporter.OnLine, sink.Output, ct).ConfigureAwait(false);
        if (exit is null) return TestRunOutcome.Cancelled;
        return !reporter.SawMessages && exit != 0 ? new TestRunOutcome(exit, $"The test runner of {project.Name} exited with code {exit} before reporting: see the Tests output.") : new TestRunOutcome(exit);
    }

    /// <summary>The <c>dotnet test --filter</c> expression for the tests (null: no filter). Names are escaped for the filter syntax.</summary>
    internal static string? VsTestFilter(IReadOnlyList<string>? testIds)
    {
        if (testIds is not { Count: > 0 and <= MaxFilteredTests }) return null;
        return string.Join('|', testIds.Select(id => "FullyQualifiedName=" + FilterEscape().Replace(id, m => "\\" + m.Value)));
    }

    private async Task<TestRunOutcome> RunVsTestAsync(TestRunRequest request, ProjectInfo info, ITestRunSink sink, Func<int, Task>? onHostProcess, CancellationToken ct)
    {
        var project = request.Project;
        var temp = TestProviderContext.NewTempDirectory();
        try
        {
            var trx = Path.Combine(temp, "results.trx");
            var args = new List<string> { "test", project.FilePath, "--nologo", "--logger", "trx;LogFileName=" + trx, "--results-directory", temp };
            if (info.TargetFramework is { } tfm) { args.Add("-f"); args.Add(tfm); }
            if (VsTestFilter(request.TestIds) is { } filter) { args.Add("--filter"); args.Add(filter); }
            sink.Status($"Running {project.Name}…");
            sink.Output($"> dotnet {string.Join(' ', args)}");
            var env = new Dictionary<string, string>(NoLogo);
            Task? attach = null;
            Action<string> stdout = sink.Output;
            if (onHostProcess is not null)
            {
                env["VSTEST_HOST_DEBUG"] = "1";
                stdout = line =>
                {
                    sink.Output(line);
                    if (attach is null && HostProcessId().Match(line) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var pid)) attach = onHostProcess(pid);
                };
            }
            var exit = await context.Processes.RunAsync(new ProcessSpec(ProcessRunner.FindDotnet(), args, project.Directory, env), stdout, sink.Output, ct).ConfigureAwait(false);
            if (attach is not null) await attach.ConfigureAwait(false);
            if (exit is null) return TestRunOutcome.Cancelled;
            var text = await TestProviderContext.TryReadAsync(trx, CancellationToken.None).ConfigureAwait(false);
            if (text is null) return new TestRunOutcome(exit, exit == 0 ? null : $"dotnet test of {project.Name} exited with code {exit} and wrote no results: see the Tests output.");
            foreach (var r in TrxReader.Read(text, project.Directory))
            {
                sink.Discovered(project, new DiscoveredTest(r.TestId, r.TestId[(r.TestId.LastIndexOf('.') + 1)..], XunitJsonReporter.DotNetGroups(r.TestId[..Math.Max(0, r.TestId.LastIndexOf('.'))])));
                sink.Finished(project, r);
            }
            return new TestRunOutcome(exit);
        }
        finally { TestProviderContext.DeleteQuietly(temp); }
    }

    // ------------------------------------------------------------------------------------------------ debugging

    public async Task<TestRunOutcome> DebugAsync(TestRunRequest request, ITestRunSink sink, ITestDebugLauncher debugger, CancellationToken ct)
    {
        var project = request.Project;
        var info = await InfoAsync(project, ct).ConfigureAwait(false);
        if (info is null) return new TestRunOutcome(-1, $"Could not evaluate {project.FilePath} with dotnet msbuild.");
        if (!info.XunitV3)
        {
            return await RunVsTestAsync(request, info, sink, async pid =>
            {
                sink.Status($"Attaching the debugger to the test host ({pid})…");
                var config = Configuration("attach", $"Debug tests: {project.Name}", project, new() { ["processId"] = pid });
                if (await debugger.StartAsync(config, ct).ConfigureAwait(false) is { } session) await DebugSessions.WaitForEndAsync(session, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        var built = await BuildAsync(project, info, sink, ct).ConfigureAwait(false);
        if (built is null) return TestRunOutcome.Cancelled;
        if (built != 0) return new TestRunOutcome(built, $"The build of {project.Name} failed: see the Tests output.");
        var args = XunitArguments(info.TargetPath, request.TestIds);
        var launch = Configuration("launch", $"Debug tests: {project.Name}", project, new()
        {
            ["program"] = info.TargetPath,
            ["args"] = args.Skip(1).ToArray(),
            ["cwd"] = Path.GetDirectoryName(info.TargetPath) ?? project.Directory,
            ["console"] = "internalConsole",
            ["stopAtEntry"] = false,
            ["justMyCode"] = true,
        });
        sink.Status($"Debugging {project.Name}…");
        var reporter = new XunitJsonReporter(project, sink);
        var session = await debugger.StartAsync(launch, ct).ConfigureAwait(false);
        if (session is null) return new TestRunOutcome(-1, "The debugger did not start (netcoredbg): see the Debug Console.");
        var exit = await DebugSessions.PumpOutputAsync(session, reporter.OnLine, ct).ConfigureAwait(false);
        return new TestRunOutcome(exit ?? 0);
    }

    private static DebugConfiguration Configuration(string request, string name, TestProject project, Dictionary<string, object> props)
    {
        var all = new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("coreclr"),
            ["request"] = JsonSerializer.SerializeToElement(request),
            ["name"] = JsonSerializer.SerializeToElement(name),
        };
        foreach (var (k, v) in props) all[k] = JsonSerializer.SerializeToElement(v);
        return new DebugConfiguration("coreclr", request, name, all, new Uri(project.Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar));
    }

    private static readonly IReadOnlyDictionary<string, string> NoLogo = new Dictionary<string, string>
    {
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["MSBUILDTERMINALLOGGER"] = "off",
    };

    [GeneratedRegex(@"[\\()&|=!~]", RegexOptions.CultureInvariant)]
    private static partial Regex FilterEscape();

    [GeneratedRegex(@"Process Id:\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex HostProcessId();
}
