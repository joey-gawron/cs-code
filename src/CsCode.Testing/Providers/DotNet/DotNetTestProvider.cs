using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Debugging;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.DotNet;

/// <summary>
/// .NET test projects (C#, F#, VB). <b>Detection</b>: every project file under the folder whose text (or a <c>Directory.Build.props</c> above it)
/// mentions a test framework is evaluated with <c>dotnet msbuild -getProperty</c> (no build: <c>IsTestProject</c>, <c>IsTestingPlatformApplication</c>,
/// <c>TargetPath</c>, <c>TargetFramework(s)</c>, <c>Language</c>, the package references). Either property true makes it a test project (an MSTest
/// or NUnit project on Microsoft.Testing.Platform sets only the second); an <c>xunit.v3</c> reference makes it an xUnit v3 one, otherwise
/// <c>IsTestingPlatformApplication</c> makes it a Microsoft.Testing.Platform (MTP) one (MSTest runner, NUnit runner, TUnit), and the rest run under
/// VSTest. A <c>TargetFrameworks</c> project uses its first framework, whose <c>TargetPath</c> comes from a second evaluation with <c>-p:TargetFramework</c>.
/// <b>Discovery</b> of a C# project scans its files (<see cref="CSharpTestScanner"/>): instant, no build, every test has its file and line; tests
/// the scan cannot see (inherited, generated) are added when a run reports them. An F# or VB project is built and asked for its tests: xUnit v3's
/// <c>-list full/json</c>, MTP's <c>--list-tests json</c>, VSTest's <c>dotnet test --list-tests</c> (display names); F# tests get their file and
/// line from <see cref="FSharpTestScanner"/>. <b>Running</b>: an xUnit v3 project is built (<c>dotnet build</c>) and its test assembly run
/// directly with <c>-reporter json</c> and one <c>-method</c> per requested test, so each result arrives as the test ends; an MTP project is built,
/// listed (<c>--list-tests json</c>) and run directly with <c>--report-trx</c> and <c>--filter-uid</c> (<see cref="TestingPlatform"/>), the TRX
/// read at the end; any other project (xUnit v2, NUnit, MSTest through VSTest) runs under <c>dotnet test --logger trx</c> with a
/// <c>FullyQualifiedName</c> filter and the TRX file is read at the end. <b>Debugging</b>: xUnit v3 — the assembly launched under netcoredbg
/// (<c>coreclr</c>) with the same arguments, results read from the session's output; MTP — the test executable launched under netcoredbg with the
/// uid filter and <c>--output detailed</c> (no TRX: it would move the tests into a child test host), results read from its console; VSTest —
/// <c>dotnet test</c> with <c>VSTEST_HOST_DEBUG=1</c>, netcoredbg attached to the test host it announces, results from the TRX file.
/// </summary>
public sealed partial class DotNetTestProvider(TestProviderContext context) : ITestProvider
{
    public const string ProviderId = "dotnet";
    public const string FrameworkXunitV3 = "xunit.v3";
    public const string FrameworkVsTest = "vstest";
    /// <summary>A Microsoft.Testing.Platform executable: <c>mtp</c>, or <c>mtp.mstest</c> / <c>mtp.nunit</c> / <c>mtp.tunit</c>.</summary>
    public const string FrameworkMtp = "mtp";

    /// <summary>Beyond this many requested tests the filter is dropped and the whole project runs (command lines have limits).</summary>
    internal const int MaxFilteredTests = 300;

    /// <summary>
    /// The longest test filter in characters (the <c>--filter</c> expression, or the <c>-method</c> arguments together) beyond which it is
    /// dropped and the whole project runs. Windows' CreateProcess takes a command line of at most 32,767 characters, which also holds the tool,
    /// the project and TRX paths and the options (Linux limits one argument to 128 KiB). The test count alone does not bound it: 300 VSTest
    /// ids of 80 characters make a 62,000-character filter, each id appearing twice.
    /// </summary>
    internal const int MaxFilterChars = 24_000;

    private readonly ConcurrentDictionary<string, ProjectInfo> _info = new(StringComparer.Ordinal);

    internal sealed record ProjectInfo(string TargetPath, string? TargetFramework, bool XunitV3, bool Mtp = false, MtpFramework MtpFramework = MtpFramework.Other,
        string Language = "C#")
    {
        public bool IsCSharp => Language is "C#" or "";
    }

    /// <summary>What <c>dotnet msbuild -getProperty</c> says about a project.</summary>
    internal sealed record Evaluation(bool IsTestProject, string TargetPath, string? TargetFramework, bool XunitV3, bool Mtp = false,
        MtpFramework MtpFramework = MtpFramework.Other, string Language = "C#");

    public string Id => ProviderId;
    public string Label => ".NET";
    public IReadOnlyList<string> LanguageIds { get; } = ["csharp", "fsharp"];

    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];
    private static bool IsProjectFile(string name) => ProjectExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------------------------------------ detection

    public async Task<IReadOnlyList<TestProject>> DetectAsync(string folder, CancellationToken ct)
    {
        var files = SourceFiles.Find(folder, IsProjectFile, 2000, ct: ct);
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
        || text.Contains("IsTestProject", StringComparison.Ordinal) || text.Contains("TUnit", StringComparison.Ordinal)
        || text.Contains("Expecto", StringComparison.Ordinal) || text.Contains("Microsoft.Testing.", StringComparison.Ordinal)
        || text.Contains("IsTestingPlatformApplication", StringComparison.Ordinal);

    private async Task<TestProject?> EvaluateAsync(string projectFile, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(projectFile)!;
        var json = await MsbuildAsync(projectFile, dir,
            ["-getProperty:IsTestProject", "-getProperty:IsTestingPlatformApplication", "-getProperty:EnableMSTestRunner", "-getProperty:EnableNUnitRunner",
                "-getProperty:TargetPath", "-getProperty:TargetFramework", "-getProperty:TargetFrameworks", "-getProperty:Language", "-getItem:PackageReference"], ct).ConfigureAwait(false);
        var parsed = json is null ? null : ParseEvaluation(json);
        if (parsed is null || !parsed.IsTestProject) return null;
        var targetPath = parsed.TargetPath;
        var tfm = parsed.TargetFramework;
        if (targetPath.Length == 0 && tfm is not null)
        {
            // a <TargetFrameworks> project (even with one entry): the outer, cross-targeting evaluation has no TargetPath; the inner one for the
            // framework that is built and run has it
            var inner = await MsbuildAsync(projectFile, dir, ["-p:TargetFramework=" + tfm, "-getProperty:TargetPath"], ct).ConfigureAwait(false);
            targetPath = inner is null ? "" : ParseTargetPath(inner);
        }
        var framework = parsed.XunitV3 ? FrameworkXunitV3
            : parsed.Mtp ? (parsed.MtpFramework == MtpFramework.Other ? FrameworkMtp : FrameworkMtp + "." + parsed.MtpFramework.ToString().ToLowerInvariant())
            : FrameworkVsTest;
        var project = new TestProject(ProviderId, Path.GetFileNameWithoutExtension(projectFile), projectFile, dir, framework);
        _info[project.Key] = new ProjectInfo(targetPath, tfm, parsed.XunitV3, parsed.Mtp && !parsed.XunitV3, parsed.MtpFramework, parsed.Language);
        return project;
    }

    /// <summary>
    /// The project file as MSBuild is given it: with links resolved, as <c>dotnet build</c> run in a terminal there sees it (the process's current
    /// directory is physical). A build of the same project under its linked path (macOS's /var/folders, /tmp, or a folder opened through a link)
    /// is a different project to MSBuild: its incremental clean deletes the other build's outputs (the test host's runtimeconfig.json went, and
    /// nothing could run) and the two keep rebuilding each other.
    /// </summary>
    internal static string MsbuildPath(string projectFile) => PhysicalPaths.Resolve(projectFile);

    /// <summary><c>dotnet msbuild</c> of the project with these <c>-getProperty</c>/<c>-p</c> arguments: its standard output, or null when it failed.</summary>
    private async Task<string?> MsbuildAsync(string projectFile, string dir, IEnumerable<string> arguments, CancellationToken ct)
    {
        var output = new System.Text.StringBuilder();
        var exit = await context.Processes.RunAsync(new ProcessSpec(ProcessRunner.FindDotnet(), ["msbuild", MsbuildPath(projectFile), "-nologo", .. arguments], dir, NoLogo),
            l => output.AppendLine(l), _ => { }, ct).ConfigureAwait(false);
        return exit == 0 ? output.ToString() : null;
    }

    /// <summary>
    /// The answer of an evaluation that asked for <c>TargetPath</c> alone: MSBuild prints the bare value for a single property (the JSON
    /// form, <c>{"Properties":{"TargetPath":...}}</c>, is read too). "" when there is none.
    /// </summary>
    internal static string ParseTargetPath(string output)
    {
        var text = output.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("Properties", out var p) && p.TryGetProperty("TargetPath", out var v) && v.ValueKind == JsonValueKind.String) return v.GetString()!;
                if (doc.RootElement.TryGetProperty("TargetPath", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString()!;
            }
            catch (JsonException) { }
            return "";
        }
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
    }

    /// <summary>
    /// The evaluation's answer (<c>-getProperty</c>/<c>-getItem</c> JSON), or null when it is not JSON. A test project has <c>IsTestProject</c> or
    /// <c>IsTestingPlatformApplication</c> true (MSTest with <c>EnableMSTestRunner</c> and NUnit with <c>EnableNUnitRunner</c> set only the latter;
    /// xUnit v3 and TUnit set both); which MTP framework it is comes from its package references. The packages' props set
    /// <c>IsTestingPlatformApplication</c>, so a project never restored (a fresh clone) does not have it yet: <c>EnableMSTestRunner</c>,
    /// <c>EnableNUnitRunner</c> (the project's own) or a TUnit reference also make it an MTP test project.
    /// </summary>
    internal static Evaluation? ParseEvaluation(string json)
    {
        var start = json.IndexOf('{');
        if (start < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(json[start..]);
            var props = doc.RootElement.TryGetProperty("Properties", out var p) ? p : default;
            string P(string name) => props.ValueKind == JsonValueKind.Object && props.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            static bool True(string v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
            var tfm = P("TargetFramework");
            if (tfm.Length == 0 && P("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { } first) tfm = first;
            var packages = doc.RootElement.TryGetProperty("Items", out var items) && items.TryGetProperty("PackageReference", out var refs) && refs.ValueKind == JsonValueKind.Array
                ? refs.EnumerateArray().Select(r => r.TryGetProperty("Identity", out var id) ? id.GetString() : null).OfType<string>().ToArray()
                : [];
            bool Has(Func<string, bool> match) => packages.Any(match);
            var xunitV3 = Has(n => n.Equals("xunit.v3", StringComparison.OrdinalIgnoreCase) || n.StartsWith("xunit.v3.core", StringComparison.OrdinalIgnoreCase));
            var mtp = True(P("IsTestingPlatformApplication")) || True(P("EnableMSTestRunner")) || True(P("EnableNUnitRunner"))
                || Has(n => n.StartsWith("TUnit", StringComparison.OrdinalIgnoreCase));
            var isTest = True(P("IsTestProject")) || mtp;
            var framework = Has(n => n.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase)) ? MtpFramework.MSTest
                : Has(n => n.Equals("NUnit3TestAdapter", StringComparison.OrdinalIgnoreCase) || n.Equals("NUnit", StringComparison.OrdinalIgnoreCase)) ? MtpFramework.NUnit
                : Has(n => n.StartsWith("TUnit", StringComparison.OrdinalIgnoreCase)) ? MtpFramework.TUnit
                : MtpFramework.Other;
            return new Evaluation(isTest, P("TargetPath"), tfm.Length > 0 ? tfm : null, xunitV3, mtp, framework, P("Language") is { Length: > 0 } lang ? lang : "C#");
        }
        catch (JsonException) { return null; }
    }

    // ------------------------------------------------------------------------------------------------ discovery

    public async Task<IReadOnlyList<DiscoveredTest>> DiscoverAsync(TestProject project, ITestRunSink log, CancellationToken ct)
    {
        if (!project.FilePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return await DiscoverByListingAsync(project, log, ct).ConfigureAwait(false);
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

    /// <summary>
    /// An F# or VB project's tests, from its runner after a build (no scanner reads those languages): xUnit v3's <c>-list full/json</c>, MTP's
    /// <c>--list-tests json</c>, else VSTest's <c>dotnet test --list-tests</c>. F# tests get the file and line of their function from the project's
    /// <c>.fs</c> files (<see cref="FSharpTestScanner"/>, matched by the method's name).
    /// </summary>
    private async Task<IReadOnlyList<DiscoveredTest>> DiscoverByListingAsync(TestProject project, ITestRunSink log, CancellationToken ct)
    {
        var info = await InfoAsync(project, ct).ConfigureAwait(false);
        if (info is null) return [];
        var built = await BuildAsync(project, info, log, ct).ConfigureAwait(false);
        if (built != 0) return [];
        var listed = await ListAsync(project, info, log, ct).ConfigureAwait(false) ?? [];
        var locations = LocateFSharpTests(project);
        var tests = new List<DiscoveredTest>();
        foreach (var t in listed.DistinctBy(t => t.Id))
        {
            var location = t.Location ?? (locations.TryGetValue(t.Method, out var at) ? at : null);
            tests.Add(new DiscoveredTest(t.Id, t.Method, XunitJsonReporter.DotNetGroups(t.Class), location));
            if (tests.Count >= TestLimits.MaxTestsPerProject) break;
        }
        return tests;
    }

    /// <summary>The tests the built project's runner lists (null: it could not say). The project must be built.</summary>
    private async Task<IReadOnlyList<ListedTest>?> ListAsync(TestProject project, ProjectInfo info, ITestRunSink log, CancellationToken ct)
    {
        if (info.TargetPath.Length == 0) return null;
        var output = new System.Text.StringBuilder();
        if (info.XunitV3 || info.Mtp)
        {
            List<string> args = info.XunitV3 ? [info.TargetPath, "-list", "full/json", "-noLogo", "-noColor"] : [info.TargetPath, "--list-tests", "json", "--no-ansi"];
            log.Output($"> dotnet {string.Join(' ', args)}");
            await context.Processes.RunAsync(new ProcessSpec(ProcessRunner.FindDotnet(), args, Path.GetDirectoryName(info.TargetPath) ?? project.Directory, NoLogo),
                l => output.AppendLine(l), log.Output, ct).ConfigureAwait(false);
            var parsed = info.XunitV3 ? TestingPlatform.ParseXunitListing(output.ToString()) : TestingPlatform.ParseMtpListing(output.ToString());
            // the runner reports the paths its PDB records, links resolved (/private/var on macOS): put back under the folder as it was opened
            return parsed?.Select(t => t.Location is { } l ? t with { Location = l with { FilePath = PhysicalPaths.UnderLogical(l.FilePath, project.Directory) } } : t).ToArray();
        }
        // VSTest: display names only; xUnit v2 names a test by its full name, NUnit and MSTest by the method's
        var vstest = new List<string> { "test", MsbuildPath(project.FilePath), "--no-build", "--list-tests", "--nologo" };
        if (info.TargetFramework is { } tfm) { vstest.Add("-f"); vstest.Add(tfm); }
        log.Output($"> dotnet {string.Join(' ', vstest)}");
        await context.Processes.RunAsync(new ProcessSpec(ProcessRunner.FindDotnet(), vstest, project.Directory, NoLogo), l => output.AppendLine(l), log.Output, ct).ConfigureAwait(false);
        return ParseVsTestListing(output.ToString());
    }

    /// <summary>The names after "The following Tests are available:" of <c>dotnet test --list-tests</c>.</summary>
    internal static IReadOnlyList<ListedTest> ParseVsTestListing(string output)
    {
        var list = new List<ListedTest>();
        var started = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!started) { started = line.Contains("The following Tests are available", StringComparison.Ordinal); continue; }
            if (!line.StartsWith("    ", StringComparison.Ordinal) || line.Trim().Length == 0) continue;
            var name = line.Trim();
            var paren = name.IndexOf('(');
            var method = paren > 0 ? name[..paren] : name;
            var dot = method.LastIndexOf('.');
            list.Add(new ListedTest(name, method, name, dot > 0 ? method[..dot] : "", dot > 0 ? method[(dot + 1)..] : method, null));
        }
        return list;
    }

    /// <summary>Method name → where an F# test function is declared, over the project's <c>.fs</c> files (nested projects and build output skipped).</summary>
    private static Dictionary<string, TestLocation> LocateFSharpTests(TestProject project)
    {
        var found = new Dictionary<string, TestLocation>(StringComparer.Ordinal);
        if (!project.FilePath.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)) return found;
        foreach (var file in SourceFiles.Find(project.Directory, n => n.EndsWith(".fs", StringComparison.OrdinalIgnoreCase), 20_000, stopAt: IsProjectFile))
        {
            if (new FileInfo(file).Length > TestLimits.MaxScannedFileBytes) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var t in FSharpTestScanner.Scan(text)) found.TryAdd(t.Method, new TestLocation(file, t.Line));
        }
        return found;
    }

    public IReadOnlyList<SourceTest> ScanSource(string filePath, string text) =>
        filePath.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
            ? [.. FSharpTestScanner.Scan(text).Select(f => new SourceTest(f.Method, f.Line, f.Method))]
            : [.. CSharpTestScanner.Scan(text).Select(f => new SourceTest(f.FullName, f.Line, f.Method))];

    /// <summary>C#: the scanner's full name is the id. F#: the scan knows the function's name only, so a test matches by its file and its id's last segment.</summary>
    public bool Matches(DiscoveredTest test, string filePath, SourceTest source) =>
        filePath.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
            ? test.Location is { } loc && ITestProvider.PathsEqual(loc.FilePath, filePath) && TestIds.EndsWithSegment(test.Id, source.Name)
            : test.Id == source.Name;

    // ------------------------------------------------------------------------------------------------ running

    public async Task<TestRunOutcome> RunAsync(TestRunRequest request, ITestRunSink sink, CancellationToken ct)
    {
        var info = await InfoAsync(request.Project, ct).ConfigureAwait(false);
        if (info is null) return new TestRunOutcome(-1, $"Could not evaluate {request.Project.FilePath} with dotnet msbuild.");
        return info.XunitV3 ? await RunXunitV3Async(request, info, sink, ct).ConfigureAwait(false)
            : info.Mtp ? await RunMtpAsync(request, info, sink, ct).ConfigureAwait(false)
            : await RunVsTestAsync(request, info, sink, null, ct).ConfigureAwait(false);
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
        var args = new List<string> { "build", MsbuildPath(project.FilePath), "-nologo", "-v:q", "-clp:NoSummary" };
        if (info.TargetFramework is { } tfm) { args.Add("-f"); args.Add(tfm); }
        sink.Output($"> dotnet {string.Join(' ', args)}");
        return await context.Processes.RunAsync(new ProcessSpec(ProcessRunner.FindDotnet(), args, project.Directory, NoLogo), sink.Output, sink.Output, ct).ConfigureAwait(false);
    }

    /// <summary>The runner's arguments for an xUnit v3 assembly: the JSON reporter and one <c>-method</c> per test (none: every test).</summary>
    internal static List<string> XunitArguments(string targetPath, IReadOnlyList<string>? testIds)
    {
        var args = new List<string> { targetPath, "-reporter", "json", "-noColor", "-noLogo" };
        if (testIds is { Count: > 0 and <= MaxFilteredTests } && testIds.Sum(id => " -method ".Length + id.Length + 2L) <= MaxFilterChars)
            foreach (var id in testIds) { args.Add("-method"); args.Add(id); }
        return args;
    }

    private async Task<TestRunOutcome> RunXunitV3Async(TestRunRequest request, ProjectInfo info, ITestRunSink sink, CancellationToken ct)
    {
        var project = request.Project;
        var built = await BuildAsync(project, info, sink, ct).ConfigureAwait(false);
        if (built is null) return TestRunOutcome.Cancelled;
        if (built != 0) return new TestRunOutcome(built, $"The build of {project.Name} failed: see the Tests output.");
        if (info.TargetPath.Length == 0) return NoTargetPath(project);
        sink.Status($"Running {project.Name}…");
        var reporter = new XunitJsonReporter(project, sink);
        var args = XunitArguments(info.TargetPath, request.TestIds);
        sink.Output($"> dotnet {string.Join(' ', args)}");
        var exit = await context.Processes.RunAsync(new ProcessSpec(ProcessRunner.FindDotnet(), args, Path.GetDirectoryName(info.TargetPath) ?? project.Directory, NoLogo),
            reporter.OnLine, sink.Output, ct).ConfigureAwait(false);
        if (exit is null) return TestRunOutcome.Cancelled;
        return !reporter.SawMessages && exit != 0 ? new TestRunOutcome(exit, $"The test runner of {project.Name} exited with code {exit} before reporting: see the Tests output.") : new TestRunOutcome(exit);
    }

    /// <summary>
    /// The <c>dotnet test --filter</c> expression for the tests (null: no filter). Names are escaped for the filter syntax. A test id is the
    /// scanner's <c>Ns.Class.Method</c>, but NUnit gives each case of a parameterized test (<c>[TestCase]</c>, <c>[TestCaseSource]</c>) the
    /// fully qualified name <c>Ns.Class.Method(1,2)</c>, which an exact <c>FullyQualifiedName=</c> never matches (nothing ran). So a method id
    /// also matches the names that contain it followed by <c>(</c>: the opening parenthesis is the boundary that keeps <c>Ns.Class.Add</c> from
    /// matching <c>Ns.Class.AddMany</c>. (VSTest's filter has no "starts with": a test of another class whose full name ends with this id
    /// would run too, and is reported like any other result.) A filter longer than <see cref="MaxFilterChars"/> is dropped (null).
    /// </summary>
    internal static string? VsTestFilter(IReadOnlyList<string>? testIds)
    {
        if (testIds is not { Count: > 0 and <= MaxFilteredTests }) return null;
        var filter = string.Join('|', testIds.Select(id =>
        {
            var escaped = FilterEscape().Replace(id, m => "\\" + m.Value);
            return id.Contains('(') ? "FullyQualifiedName=" + escaped : $"FullyQualifiedName={escaped}|FullyQualifiedName~{escaped}\\(";
        }));
        return filter.Length <= MaxFilterChars ? filter : null;
    }

    private async Task<TestRunOutcome> RunVsTestAsync(TestRunRequest request, ProjectInfo info, ITestRunSink sink, Func<int, Task>? onHostProcess, CancellationToken ct)
    {
        var project = request.Project;
        var temp = TestProviderContext.NewTempDirectory();
        try
        {
            var trx = Path.Combine(temp, "results.trx");
            var args = new List<string> { "test", MsbuildPath(project.FilePath), "--nologo", "--logger", "trx;LogFileName=" + trx, "--results-directory", temp };
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

    /// <summary>
    /// The arguments that select the requested tests of an MTP executable: <c>--filter-uid</c> with the uids its listing gives for them (every data
    /// row of a method; NUnit's own <c>--filter</c> parser rejects the escaped parenthesis that matches a parameterized case), else the framework's
    /// own filter (<see cref="TestingPlatform.NativeFilter"/>) when it cannot list. Empty: every test.
    /// </summary>
    private static List<string> MtpSelection(TestRunRequest request, ProjectInfo info, IReadOnlyList<ListedTest>? listed, ITestRunSink sink)
    {
        if (request.TestIds is not { Count: > 0 } ids) return [];
        if (listed is not null)
        {
            // tests the listing found that discovery did not (inherited, generated) are added before the run reports them
            foreach (var t in listed.DistinctBy(t => t.Id)) sink.Discovered(request.Project, new DiscoveredTest(t.Id, t.Method, XunitJsonReporter.DotNetGroups(t.Class), t.Location));
            return TestingPlatform.UidsFor(ids, listed) is { } uids ? ["--filter-uid", .. uids] : [];
        }
        return TestingPlatform.NativeFilter(info.MtpFramework, ids)?.ToList() ?? [];
    }

    private async Task<TestRunOutcome> RunMtpAsync(TestRunRequest request, ProjectInfo info, ITestRunSink sink, CancellationToken ct)
    {
        var project = request.Project;
        var built = await BuildAsync(project, info, sink, ct).ConfigureAwait(false);
        if (built is null) return TestRunOutcome.Cancelled;
        if (built != 0) return new TestRunOutcome(built, $"The build of {project.Name} failed: see the Tests output.");
        if (info.TargetPath.Length == 0) return NoTargetPath(project);
        var listed = await ListAsync(project, info, sink, ct).ConfigureAwait(false);
        var selection = MtpSelection(request, info, listed, sink);
        var outputDir = Path.GetDirectoryName(info.TargetPath) ?? project.Directory;
        // --report-trx is an extension (Microsoft.Testing.Extensions.TrxReport, in the MSTest and TUnit metapackages): without it the console is read
        var hasTrx = File.Exists(Path.Combine(outputDir, "Microsoft.Testing.Extensions.TrxReport.dll"));
        var temp = TestProviderContext.NewTempDirectory();
        try
        {
            var args = new List<string> { info.TargetPath, "--no-ansi", "--progress", "off", "--results-directory", temp };
            if (hasTrx) args.AddRange(["--report-trx", "--report-trx-filename", "results.trx"]);
            else args.AddRange(["--output", "detailed"]);
            args.AddRange(selection);
            sink.Status($"Running {project.Name}…");
            sink.Output($"> dotnet {string.Join(' ', args)}");
            var console = hasTrx ? null : new TestingPlatform.ConsoleReporter(project, sink, listed ?? []);
            var exit = await context.Processes.RunAsync(new ProcessSpec(ProcessRunner.FindDotnet(), args, outputDir, NoLogo),
                console is null ? sink.Output : console.OnLine, sink.Output, ct).ConfigureAwait(false);
            console?.Complete();
            if (exit is null) return TestRunOutcome.Cancelled;
            if (console is not null)
                return console.SawResults || exit is 0 or MtpExitTestsFailed ? new TestRunOutcome(exit) : new TestRunOutcome(exit, $"{project.Name} exited with code {exit} before reporting results: see the Tests output.");
            var text = await TestProviderContext.TryReadAsync(Path.Combine(temp, "results.trx"), CancellationToken.None).ConfigureAwait(false);
            if (text is null) return new TestRunOutcome(exit, exit == 0 ? null : $"{project.Name} exited with code {exit} and wrote no results: see the Tests output.");
            foreach (var r in TrxReader.Read(text, project.Directory))
            {
                sink.Discovered(project, new DiscoveredTest(r.TestId, r.TestId[(r.TestId.LastIndexOf('.') + 1)..], XunitJsonReporter.DotNetGroups(r.TestId[..Math.Max(0, r.TestId.LastIndexOf('.'))])));
                sink.Finished(project, r);
            }
            return new TestRunOutcome(exit);
        }
        finally { TestProviderContext.DeleteQuietly(temp); }
    }

    /// <summary>Microsoft.Testing.Platform's exit code when the run worked and some test failed (the platform's ExitCodes.AtLeastOneTestFailed).</summary>
    internal const int MtpExitTestsFailed = 2;

    // ------------------------------------------------------------------------------------------------ debugging

    public async Task<TestRunOutcome> DebugAsync(TestRunRequest request, ITestRunSink sink, ITestDebugLauncher debugger, CancellationToken ct)
    {
        var project = request.Project;
        var info = await InfoAsync(project, ct).ConfigureAwait(false);
        if (info is null) return new TestRunOutcome(-1, $"Could not evaluate {project.FilePath} with dotnet msbuild.");
        if (info.Mtp) return await DebugMtpAsync(request, info, sink, debugger, ct).ConfigureAwait(false);
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
        if (info.TargetPath.Length == 0) return NoTargetPath(project);
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

    /// <summary>
    /// An MTP test executable launched under netcoredbg with the run's selection. No TRX: since Microsoft.Testing.Platform 2.4 <c>--report-trx</c>
    /// starts the tests in a child test host (checked with 2.5.2: the process the debugger launched only waits for it), where no breakpoint is hit.
    /// The results come from the console (<c>--output detailed</c>) as the session prints it.
    /// </summary>
    private async Task<TestRunOutcome> DebugMtpAsync(TestRunRequest request, ProjectInfo info, ITestRunSink sink, ITestDebugLauncher debugger, CancellationToken ct)
    {
        var project = request.Project;
        var built = await BuildAsync(project, info, sink, ct).ConfigureAwait(false);
        if (built is null) return TestRunOutcome.Cancelled;
        if (built != 0) return new TestRunOutcome(built, $"The build of {project.Name} failed: see the Tests output.");
        if (info.TargetPath.Length == 0) return NoTargetPath(project);
        var listed = await ListAsync(project, info, sink, ct).ConfigureAwait(false);
        var selection = MtpSelection(request, info, listed, sink);
        var args = new List<string> { "--no-ansi", "--progress", "off", "--output", "detailed" };
        args.AddRange(selection);
        var launch = Configuration("launch", $"Debug tests: {project.Name}", project, new()
        {
            ["program"] = info.TargetPath,
            ["args"] = args.ToArray(),
            ["cwd"] = Path.GetDirectoryName(info.TargetPath) ?? project.Directory,
            ["console"] = "internalConsole",
            ["stopAtEntry"] = false,
            ["justMyCode"] = true,
            // a run opts out through NoLogo; the debugger starts the program with its own environment (MTP reports usage otherwise)
            ["env"] = new Dictionary<string, string> { ["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1" },
        });
        sink.Status($"Debugging {project.Name}…");
        var console = new TestingPlatform.ConsoleReporter(project, sink, listed ?? []);
        var session = await debugger.StartAsync(launch, ct).ConfigureAwait(false);
        if (session is null) return new TestRunOutcome(-1, "The debugger did not start (netcoredbg): see the Debug Console.");
        var exit = await DebugSessions.PumpOutputAsync(session, console.OnLine, ct).ConfigureAwait(false);
        console.Complete();
        return new TestRunOutcome(exit ?? 0);
    }

    private static TestRunOutcome NoTargetPath(TestProject project) =>
        new(-1, $"dotnet msbuild gave no TargetPath for {project.FilePath}, so there is no test assembly to run.");

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
        ["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["MSBUILDTERMINALLOGGER"] = "off",
    };

    [GeneratedRegex(@"[\\()&|=!~]", RegexOptions.CultureInvariant)]
    private static partial Regex FilterEscape();

    [GeneratedRegex(@"Process Id:\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex HostProcessId();
}
