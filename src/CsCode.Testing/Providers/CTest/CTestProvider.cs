using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Build;
using CsCode.Core.Debugging;
using CsCode.Testing.Engine;
using CsCode.Testing.Providers.Rust;

namespace CsCode.Testing.Providers.CTest;

/// <summary>
/// CTest, for the workspace's CMake projects (<see cref="ICMakeService"/>, stage 3). <b>Detection</b>: each CMake project under the folder.
/// <b>Discovery</b> configures the project when it never was, then runs <c>ctest --test-dir &lt;build&gt; --show-only=json-v1</c>
/// (<see cref="CTestJson"/>): a test's id is its CTest name, it is grouped under the executable it runs, and its location is the <c>add_test</c>
/// line its backtrace names (so the explorer's glyphs sit in CMakeLists.txt). <b>Runs</b> build the tests' targets through the CMake
/// integration first, then run <c>ctest -R '^(a|b)$' --output-on-failure --output-junit &lt;file&gt;</c> (JUnit needs CTest 3.21; older ctest
/// is read from its console output, <see cref="CTestResults"/>). <b>Debugging</b> starts one test's command from the JSON under the C debugger
/// (<c>cscode.cmake.debugger</c>: CodeLLDB on macOS, GDB elsewhere) in its <c>WORKING_DIRECTORY</c> (default: the build directory of the
/// CMakeLists.txt that added it) with its <c>ENVIRONMENT</c>; the result is the program's exit code.
/// </summary>
public sealed partial class CTestProvider(TestProviderContext context, ICMakeService? cmake) : ITestProvider
{
    public const string ProviderId = "ctest";
    public const string DebuggerSetting = "cscode.cmake.debugger";
    internal const int MaxFilteredTests = 400;
    private static readonly Version JUnitVersion = new(3, 21);
    private readonly Dictionary<string, Version?> _versions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    // per source directory: the configure (generation) a discovery last built for
    private readonly Dictionary<string, long> _discoveryBuilds = new(StringComparer.Ordinal);

    public string Id => ProviderId;
    public string Label => "CTest";
    public IReadOnlyList<string> LanguageIds { get; } = ["cmake"];

    /// <summary>
    /// Fires when a configure changed the projects (new targets, a project that came or went) and after every finished build: a build can change
    /// what ctest lists (gtest_discover_tests' <c>&lt;target&gt;_NOT_BUILT</c> placeholder becomes the executable's tests; a test added to its
    /// source joins them; a command appears once its executable exists). The controller then detects and discovers the projects again, after a
    /// run that is going; the tests keep their results.
    /// </summary>
    public Core.Lifecycle.IEventSource<bool> ProjectsChanged => cmake is null ? Core.Lifecycle.EventSource.Never<bool>() : new CMakeChanges(cmake);

    // ------------------------------------------------------------------------------------------------ detection

    public async Task<IReadOnlyList<TestProject>> DetectAsync(string folder, CancellationToken ct)
    {
        if (cmake is null) return [];
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        IReadOnlyList<TestProject> Find() => [.. cmake.Projects
            .Where(p => IsUnder(p.SourceDirectory, root))
            .Select(p => new TestProject(ProviderId, p.Name, Path.Combine(p.SourceDirectory, "CMakeLists.txt"), p.SourceDirectory, "ctest"))];
        var projects = Find();
        if (projects.Count > 0 || !File.Exists(Path.Combine(root, "CMakeLists.txt"))) return projects;
        // the CMake integration looks at the folders in the background: when it has not reported this one yet, give it a moment
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cmake.ProjectsChanged.Subscribe(_ => { if (Find().Count > 0) seen.TrySetResult(); }))
        {
            if (Find().Count == 0)
            {
                try { await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
        }
        return Find();
    }

    private static bool IsUnder(string path, string dir) =>
        string.Equals(path, dir, StringComparison.Ordinal) || path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>The CMake project behind a test project, configured first when it never was (null: gone, or the configure failed; the reason went to <paramref name="sink"/>).</summary>
    private async Task<CMakeProject?> ConfiguredAsync(TestProject project, ITestRunSink sink, CancellationToken ct)
    {
        if (cmake?.Projects.FirstOrDefault(p => p.SourceDirectory == project.Directory) is not { } p)
        {
            sink.Output($"> {project.Name} is no longer a CMake project of the workspace.");
            return null;
        }
        if (p.State == CMakeProjectState.Configured) return p;
        sink.Status($"Configuring {project.Name}…");
        sink.Output($"> Configuring {project.Name} (see the CMake output)");
        return await cmake.ConfigureAsync(p, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------ discovery

    public async Task<IReadOnlyList<DiscoveredTest>> DiscoverAsync(TestProject project, ITestRunSink log, CancellationToken ct)
    {
        var p = await ConfiguredAsync(project, log, ct).ConfigureAwait(false)
            ?? throw new TestDiscoveryException($"{project.Name} could not be configured: see the CMake output.");
        var tests = await ListAsync(p, log, ct).ConfigureAwait(false)
            ?? throw new TestDiscoveryException($"ctest could not list the tests of {project.Name}: see the Tests output.");
        // ctest leaves a test's command out while its executable is not built (right after the first configure): build the project once, as
        // cargo's discovery builds its tests, so each test is listed under the executable it runs and can be debugged from the start
        if (tests.Any(t => t.Executable is null) && p.Targets.Any(t => t.Executable is { } exe && !File.Exists(exe)) && cmake is not null && FirstDiscoveryBuild(p))
        {
            log.Status($"Building {project.Name} to list its tests…");
            log.Output($"> Some tests' executables are not built yet: building {project.Name} (see the CMake output)");
            var built = await cmake.BuildAsync(p, null, ct).ConfigureAwait(false);
            if (built.Succeeded && cmake.Projects.FirstOrDefault(x => x.SourceDirectory == p.SourceDirectory) is { } now && await ListAsync(now, log, ct).ConfigureAwait(false) is { } again)
            {
                p = now;
                tests = again;
            }
            else log.Output($"> The build did not succeed: the tests whose executables are missing are listed without them.");
        }
        return [.. tests.Take(TestLimits.MaxTestsPerProject).Select(t => Discovered(t, p))];
    }

    /// <summary>
    /// True once per configure of <paramref name="p"/>: a discovery builds at most once per configure, so a build that fails (which, finished,
    /// makes the explorer discover again: <see cref="ProjectsChanged"/>) does not lead to another discovery that builds again.
    /// </summary>
    private bool FirstDiscoveryBuild(CMakeProject p)
    {
        lock (_gate)
        {
            if (_discoveryBuilds.TryGetValue(p.SourceDirectory, out var generation) && generation == p.Generation) return false;
            if (_discoveryBuilds.Count >= 64) _discoveryBuilds.Clear(); // bounded: a handful of projects in practice
            _discoveryBuilds[p.SourceDirectory] = p.Generation;
            return true;
        }
    }

    /// <summary>The project's tests from <c>ctest --show-only=json-v1</c>, or null when ctest failed (the reason went to <paramref name="log"/>).</summary>
    internal async Task<IReadOnlyList<CTestInfo>?> ListAsync(CMakeProject p, ITestRunSink log, CancellationToken ct)
    {
        var ctest = CTest(p);
        var args = new List<string> { "--test-dir", p.BuildDirectory, "--show-only=json-v1" };
        AddConfiguration(args, p);
        log.Status($"Listing the tests of {p.Name}…");
        log.Output($"> {ctest} {string.Join(' ', args)}");
        var json = new StringBuilder();
        var exit = await context.Processes.RunAsync(new ProcessSpec(ctest, args, p.BuildDirectory), l => json.AppendLine(l), log.Output, ct).ConfigureAwait(false);
        if (exit is null) ct.ThrowIfCancellationRequested();
        if (exit != 0)
        {
            log.Output($"> ctest exited with code {exit}.");
            return null;
        }
        try { return CTestJson.Parse(json.ToString()); }
        catch (JsonException ex)
        {
            log.Output($"> ctest's test list could not be read: {ex.Message}");
            return null;
        }
    }

    internal static DiscoveredTest Discovered(CTestInfo t, CMakeProject project)
    {
        var exe = t.Executable is { } e ? Path.GetFileName(e) : "(no command)";
        var target = project.Targets.FirstOrDefault(x => x.Executable is { } path && t.Executable is { } te && PathsEqual(path, te));
        var groups = new List<TestGroup> { new(target?.Name ?? exe, TestNodeKind.Target, target?.Sources.FirstOrDefault() is { } src ? new TestLocation(src, 0) : null) };
        var location = t.File is { } f && t.Line is { } line ? new TestLocation(f, Math.Max(0, line - 1)) : null;
        var label = t.Labels.Count > 0 ? $"{t.Name} [{string.Join(", ", t.Labels)}]" : t.Name;
        return new DiscoveredTest(t.Name, label, groups, location, t.Disabled ? "disabled (DISABLED property)" : null);
    }

    // ------------------------------------------------------------------------------------------------ source scan

    [GeneratedRegex(@"^\s*add_test\s*\(\s*(?:NAME\s+)?(?<name>""[^""]+""|[^\s)]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline, matchTimeoutMilliseconds: 200)]
    private static partial Regex AddTest();

    /// <summary>The <c>add_test(NAME x …)</c> (and old-style <c>add_test(x …)</c>) calls of a CMakeLists.txt or *.cmake file.</summary>
    public IReadOnlyList<SourceTest> ScanSource(string filePath, string text)
    {
        var name = Path.GetFileName(filePath);
        if (name != "CMakeLists.txt" && !name.EndsWith(".cmake", StringComparison.OrdinalIgnoreCase)) return [];
        var result = new List<SourceTest>();
        try
        {
            foreach (Match m in AddTest().Matches(text))
            {
                var test = m.Groups["name"].Value.Trim('"');
                if (test.Contains("${", StringComparison.Ordinal)) continue; // a name built from variables: only ctest knows it
                var line = 0;
                for (var i = 0; i < m.Index; i++) if (text[i] == '\n') line++;
                result.Add(new SourceTest(test, line, test));
            }
        }
        catch (RegexMatchTimeoutException) { }
        return result;
    }

    public bool Matches(DiscoveredTest test, string filePath, SourceTest source) =>
        test.Id == source.Name && test.Location is { } loc && ITestProvider.PathsEqual(loc.FilePath, filePath);

    // ------------------------------------------------------------------------------------------------ running

    /// <summary>ctest's <c>-R</c> for exactly these tests (CMake's regex syntax: special characters escaped, anchored, alternatives).</summary>
    internal static string Filter(IEnumerable<string> names) => "^(" + string.Join('|', names.Select(EscapeRegex)) + ")$";

    private static string EscapeRegex(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        foreach (var c in name)
        {
            if ("\\^$.|?*+()[]{}".Contains(c, StringComparison.Ordinal)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void AddConfiguration(List<string> args, CMakeProject p)
    {
        if (p.Configuration is { Length: > 0 } config) { args.Add("-C"); args.Add(config); }
    }

    private string CTest(CMakeProject p) => p.CTestPath ?? ProcessRunner.FindOnPath("ctest") ?? "ctest";

    /// <summary>Builds the targets whose executables the tests run (all targets when a test runs something else), configuring first if needed.</summary>
    private async Task<(CMakeProject? Project, IReadOnlyList<CTestInfo>? Tests, string? Error)> PrepareAsync(TestRunRequest request, ITestRunSink sink, CancellationToken ct)
    {
        var project = request.Project;
        if (await ConfiguredAsync(project, sink, ct).ConfigureAwait(false) is not { } p)
            return (null, null, ct.IsCancellationRequested ? null : $"{project.Name} could not be configured: see the CMake output.");
        var all = await ListAsync(p, sink, ct).ConfigureAwait(false);
        if (all is null) return (null, null, ct.IsCancellationRequested ? null : $"ctest could not list the tests of {project.Name}: see the Tests output.");
        var wanted = request.TestIds is null ? all : [.. all.Where(t => request.TestIds.Contains(t.Name))];
        var targets = wanted.Select(t => p.Targets.FirstOrDefault(x => x.Executable is { } e && t.Executable is { } te && PathsEqual(e, te))).ToList();
        IReadOnlyList<string> names = targets.Any(t => t is null) ? [] : [.. targets.Select(t => t!.Name).Distinct(StringComparer.Ordinal)];
        sink.Status($"Building {(names.Count == 0 ? project.Name : string.Join(", ", names))}…");
        sink.Output($"> Building {(names.Count == 0 ? "all targets" : string.Join(", ", names))} (see the CMake output)");
        var built = await cmake!.BuildAsync(p, names, ct).ConfigureAwait(false);
        if (built.ExitCode is null) return (null, null, null);
        if (!built.Succeeded) return (null, null, built.Error ?? $"The build of {project.Name} failed{(built.Errors > 0 ? $" with {built.Errors} error(s) (see the Error List)" : "")}.");
        // the build may have reconfigured: run against the current snapshot
        p = cmake.Projects.FirstOrDefault(x => x.SourceDirectory == p.SourceDirectory) ?? p;
        return (p, wanted, null);
    }

    public async Task<TestRunOutcome> RunAsync(TestRunRequest request, ITestRunSink sink, CancellationToken ct)
    {
        var (p, tests, error) = await PrepareAsync(request, sink, ct).ConfigureAwait(false);
        if (p is null || tests is null) return ct.IsCancellationRequested || error is null ? TestRunOutcome.Cancelled : new TestRunOutcome(-1, error);
        if (tests.Count == 0) return new TestRunOutcome(0);
        var ctest = CTest(p);
        var temp = TestProviderContext.NewTempDirectory();
        try
        {
            var junit = await SupportsJUnitAsync(ctest, ct).ConfigureAwait(false) ? Path.Combine(temp, "ctest.xml") : null;
            var args = new List<string> { "--test-dir", p.BuildDirectory, "--output-on-failure", "--no-tests=ignore" };
            AddConfiguration(args, p);
            if (junit is not null) { args.Add("--output-junit"); args.Add(junit); }
            if (request.TestIds is not null && tests.Count <= MaxFilteredTests) { args.Add("-R"); args.Add(Filter(tests.Select(t => t.Name))); }
            foreach (var t in tests) sink.Started(request.Project, t.Name);
            sink.Status($"Running the tests of {p.Name}…");
            sink.Output($"> {ctest} {string.Join(' ', args.Select(Quote))}");
            var results = new CTestResults(request.Project, sink);
            var exit = await context.Processes.RunAsync(new ProcessSpec(ctest, args, p.BuildDirectory), results.OnLine, results.OnLine, ct).ConfigureAwait(false);
            if (exit is null) return TestRunOutcome.Cancelled;
            results.Complete(junit);
            // ctest exits 8 when a test failed: the results say so; any other failure is ctest's own
            return new TestRunOutcome(exit, exit is 0 or 8 ? null : results.Reported.Count == 0 ? $"ctest exited with code {exit}: see the Tests output." : null);
        }
        finally { TestProviderContext.DeleteQuietly(temp); }
    }

    /// <summary>Whether <paramref name="ctest"/> has <c>--output-junit</c> (3.21+): asked once per program.</summary>
    private async Task<bool> SupportsJUnitAsync(string ctest, CancellationToken ct)
    {
        Version? version;
        lock (_versions) if (_versions.TryGetValue(ctest, out version)) return version >= JUnitVersion;
        string? first = null;
        await context.Processes.RunAsync(new ProcessSpec(ctest, ["--version"], Path.GetTempPath()), l => first ??= l, _ => { }, ct).ConfigureAwait(false);
        version = ParseVersion(first);
        lock (_versions) _versions[ctest] = version;
        return version is null || version >= JUnitVersion; // unknown: try it; ctest then reports the option it does not know
    }

    internal static Version? ParseVersion(string? line)
    {
        var m = line is null ? null : Regex.Match(line, @"(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        return m is { Success: true } ? new Version(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)) : null;
    }

    // ------------------------------------------------------------------------------------------------ debugging

    public async Task<TestRunOutcome> DebugAsync(TestRunRequest request, ITestRunSink sink, ITestDebugLauncher debugger, CancellationToken ct)
    {
        var (p, tests, error) = await PrepareAsync(request, sink, ct).ConfigureAwait(false);
        if (p is null || tests is null) return ct.IsCancellationRequested || error is null ? TestRunOutcome.Cancelled : new TestRunOutcome(-1, error);
        var test = tests.FirstOrDefault(t => !t.Disabled && t.Executable is not null);
        if (test is null) return new TestRunOutcome(-1, "None of the selected tests has a command to debug.");
        if (tests.Count > 1) sink.Output($"> Debugging {test.Name} only: a debug run starts one test.");
        var config = DebugConfiguration(test, p, context.Setting(DebuggerSetting), OperatingSystem.IsMacOS());
        sink.Started(request.Project, test.Name);
        sink.Status($"Debugging {test.Name}…");
        var session = await debugger.StartAsync(config, ct).ConfigureAwait(false);
        if (session is null) return new TestRunOutcome(-1, $"The debugger ({config.Type}) did not start: see the Debug Console.");
        var output = new StringBuilder();
        var exit = await DebugSessions.PumpOutputAsync(session, line =>
        {
            sink.Output(line);
            if (output.Length < TestLimits.MaxOutputPerTest) output.AppendLine(line);
        }, ct).ConfigureAwait(false);
        var passed = exit is null or 0;
        sink.Finished(request.Project, new TestResult(test.Name, passed ? TestOutcome.Passed : TestOutcome.Failed, null,
            passed ? null : $"Exited with code {exit}", Output: TestLimits.Clip(output.ToString().TrimEnd()) is { Length: > 0 } o ? o : null));
        return new TestRunOutcome(exit ?? 0);
    }

    /// <summary>The launch configuration for a test: its command, working directory and environment, under the configured C debugger.</summary>
    internal static DebugConfiguration DebugConfiguration(CTestInfo test, CMakeProject project, string? debuggerSetting, bool macOS)
    {
        var type = DebugType(debuggerSetting, macOS);
        var cwd = test.WorkingDirectory ?? DefaultWorkingDirectory(test, project);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in test.Environment)
        {
            var eq = kv.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0) env[kv[..eq]] = kv[(eq + 1)..];
        }
        var props = new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement(type),
            ["request"] = JsonSerializer.SerializeToElement("launch"),
            ["name"] = JsonSerializer.SerializeToElement($"Debug test: {test.Name}"),
            ["program"] = JsonSerializer.SerializeToElement(test.Command[0]),
            ["args"] = JsonSerializer.SerializeToElement(test.Command.Skip(1).ToArray()),
            ["cwd"] = JsonSerializer.SerializeToElement(cwd),
        };
        if (env.Count > 0) props["env"] = JsonSerializer.SerializeToElement(env);
        if (type == "lldb")
        {
            var cpp = project.Targets.FirstOrDefault(t => t.Executable is { } e && PathsEqual(e, test.Command[0]))?.Sources
                .Any(s => Path.GetExtension(s).ToLowerInvariant() is ".cpp" or ".cc" or ".cxx" or ".c++") == true;
            props["sourceLanguages"] = JsonSerializer.SerializeToElement(new[] { cpp ? "cpp" : "c" });
        }
        return new DebugConfiguration(type, "launch", $"Debug test: {test.Name}", props,
            new Uri(project.SourceDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Where ctest runs a test without <c>WORKING_DIRECTORY</c>: the build directory of the CMakeLists.txt that added it (its backtrace file,
    /// mapped from the source tree into the build tree), else the top build directory.
    /// </summary>
    internal static string DefaultWorkingDirectory(CTestInfo test, CMakeProject project)
    {
        if (test.File is { } file && Path.GetDirectoryName(file) is { } dir && IsUnder(dir, project.SourceDirectory))
            return Path.GetFullPath(Path.Combine(project.BuildDirectory, Path.GetRelativePath(project.SourceDirectory, dir)));
        return project.BuildDirectory;
    }

    /// <summary>The debug type for <see cref="DebuggerSetting"/>: <c>lldb</c> (CodeLLDB), <c>lldb-dap</c> or <c>gdb</c>; unset: lldb on macOS, gdb elsewhere.</summary>
    internal static string DebugType(string? setting, bool macOS)
    {
        var chosen = setting?.Trim().ToLowerInvariant() is { Length: > 0 } s ? s : macOS ? "lldb" : "gdb";
        return chosen switch { "codelldb" or "lldb" => "lldb", "lldb-dap" or "lldbdap" => "lldb-dap", _ => "gdb" };
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static string Quote(string a) => a.Any(c => char.IsWhiteSpace(c) || c is '|' or '(' or ')' or '^' or '$' or '\'') ? "'" + a + "'" : a;
}
