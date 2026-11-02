using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Testing.Engine;
using CsCode.Testing.Providers.Rust;

namespace CsCode.Testing.Providers.Python;

/// <summary>
/// pytest and unittest (docs/PYTHON.md §5), with the folder's selected interpreter (<see cref="TestProviderContext.PythonInterpreter"/>).
/// <para><b>Detection</b>: a workspace folder with a pytest configuration (<c>pytest.ini</c>, <c>[tool.pytest.ini_options]</c> in pyproject.toml,
/// <c>[tool:pytest]</c> in setup.cfg, <c>[pytest]</c> in tox.ini, a <c>conftest.py</c>) or a test file (<c>test*.py</c>, <c>*_test.py</c>; virtual
/// environments are not searched). pytest when it is configured or the interpreter has it, otherwise unittest.</para>
/// <para><b>Discovery</b>: <c>python -m pytest -p cscode_pytest --collect-only -q</c> with <c>python.testing.pytestArgs</c>, the plugin reporting
/// each item's node id, file and line; a parametrized test is one test whose cases are its parametrizations. unittest: a discovery script over
/// <c>python.testing.unittestArgs</c> (<c>-s</c>, <c>-p</c>, <c>-t</c>). Both run in <c>python.testing.cwd</c> (default: the folder).</para>
/// <para><b>Running</b>: pytest with the node ids and <c>--junitxml</c> (results, durations, failure messages and reports, tracebacks as failure
/// locations through <see cref="StackTraces.FromPython"/>), the plugin reporting each test as it starts; unittest through the script's runner.
/// <b>Debugging</b>: the same command through debugpy (<c>module: pytest</c> or the script, <c>purpose: ["debug-test"]</c>), results read the
/// same way when the session ends.</para>
/// </summary>
public sealed class PythonTestProvider(TestProviderContext context) : ITestProvider
{
    public const string ProviderId = "python";
    public const string PytestArgsSetting = "python.testing.pytestArgs";
    public const string UnittestArgsSetting = "python.testing.unittestArgs";
    public const string CwdSetting = "python.testing.cwd";
    internal const int MaxFilteredTests = 500;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    public string Id => ProviderId;
    public string Label => "Python";
    public IReadOnlyList<string> LanguageIds { get; } = ["python"];

    // ------------------------------------------------------------------------------------------------ detection

    public async Task<IReadOnlyList<TestProject>> DetectAsync(string folder, CancellationToken ct)
    {
        var config = await PytestConfigAsync(folder, ct).ConfigureAwait(false);
        var hasTests = config is not null || SourceFiles.Find(folder, IsTestFile, 1, stopAt: n => n == "pyvenv.cfg", ct: ct).Count > 0;
        if (!hasTests) return [];
        var python = await InterpreterAsync(folder, ct).ConfigureAwait(false);
        var framework = config is not null || await HasModuleAsync(python, folder, "pytest", ct).ConfigureAwait(false) ? "pytest" : "unittest";
        return [new TestProject(ProviderId, $"{Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))} ({framework})", config ?? folder, folder, framework)];
    }

    internal static bool IsTestFile(string name) =>
        name.EndsWith(".py", StringComparison.Ordinal) && (name.StartsWith("test", StringComparison.Ordinal) || name.EndsWith("_test.py", StringComparison.Ordinal));

    /// <summary>The file that configures pytest for <paramref name="folder"/>, or null.</summary>
    internal static async Task<string?> PytestConfigAsync(string folder, CancellationToken ct)
    {
        foreach (var (file, section) in new[] { ("pytest.ini", (string?)null), ("pyproject.toml", "[tool.pytest.ini_options]"), ("setup.cfg", "[tool:pytest]"), ("tox.ini", "[pytest]"), ("conftest.py", null) })
        {
            var path = Path.Combine(folder, file);
            if (!File.Exists(path)) continue;
            if (section is null) return path;
            if (await TestProviderContext.TryReadAsync(path, ct).ConfigureAwait(false) is { } text && text.Contains(section, StringComparison.Ordinal)) return path;
        }
        return null;
    }

    private async Task<string> InterpreterAsync(string folder, CancellationToken ct) =>
        (context.PythonInterpreter is { } pick ? await pick(folder, ct).ConfigureAwait(false) : null)
        ?? ProcessRunner.FindOnPath(OperatingSystem.IsWindows() ? "python" : "python3") ?? "python3";

    private async Task<bool> HasModuleAsync(string python, string folder, string module, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        var exit = await context.Processes.RunAsync(new ProcessSpec(python, ["-I", "-c", $"import {module}"], folder), _ => { }, _ => { }, timeout.Token).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return exit == 0;
    }

    private string WorkingDirectory(TestProject project)
    {
        if (context.Setting(CwdSetting) is not { Length: > 0 } cwd) return project.Directory;
        cwd = cwd.Replace("${workspaceFolder}", project.Directory, StringComparison.Ordinal);
        return Path.IsPathRooted(cwd) ? cwd : Path.GetFullPath(Path.Combine(project.Directory, cwd));
    }

    private IReadOnlyList<string> PytestArgs() => context.ListSetting?.Invoke(PytestArgsSetting) ?? [];

    /// <summary>VS Code's unittest arguments: <c>-s</c> start directory (default <c>.</c>), <c>-p</c> pattern (default <c>test*.py</c>), <c>-t</c> top-level directory.</summary>
    internal static (string Start, string Pattern, string Top) UnittestArgs(IReadOnlyList<string>? args)
    {
        string start = ".", pattern = "test*.py", top = "";
        for (var i = 0; args is not null && i + 1 < args.Count; i++)
            switch (args[i])
            {
                case "-s" or "--start-directory": start = args[++i]; break;
                case "-p" or "--pattern": pattern = args[++i]; break;
                case "-t" or "--top-level-directory": top = args[++i]; break;
            }
        return (start, pattern, top);
    }

    private static Dictionary<string, string> Environment(string helpers) => new()
    {
        ["PYTHONPATH"] = System.Environment.GetEnvironmentVariable("PYTHONPATH") is { Length: > 0 } existing ? helpers + Path.PathSeparator + existing : helpers,
        ["PYTHONDONTWRITEBYTECODE"] = "1",
        ["PYTHONIOENCODING"] = "utf-8",
    };

    // ------------------------------------------------------------------------------------------------ discovery

    /// <summary>
    /// The helpers' event in a line, or null for the tests' own output. pytest's progress line may hold the start of one (<c>tests/test_a.py
    /// @@cscode {…}</c>: the reporter wrote the file name before the test started); <paramref name="before"/> is that text.
    /// </summary>
    internal static JsonElement? Event(string line, out string before)
    {
        before = line;
        var at = line.IndexOf(PythonTestScripts.Marker, StringComparison.Ordinal);
        if (at < 0) return null;
        try
        {
            var e = JsonDocument.Parse(line[(at + PythonTestScripts.Marker.Length)..]).RootElement.Clone();
            before = line[..at].TrimEnd();
            return e;
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    public async Task<IReadOnlyList<DiscoveredTest>> DiscoverAsync(TestProject project, ITestRunSink log, CancellationToken ct)
    {
        var helpers = PythonTestScripts.Write();
        try
        {
            return project.Framework == "unittest"
                ? await DiscoverUnittestAsync(project, helpers, log, ct).ConfigureAwait(false)
                : await DiscoverPytestAsync(project, helpers, log, ct).ConfigureAwait(false);
        }
        finally { TestProviderContext.DeleteQuietly(helpers); }
    }

    private async Task<IReadOnlyList<DiscoveredTest>> DiscoverPytestAsync(TestProject project, string helpers, ITestRunSink log, CancellationToken ct)
    {
        var python = await InterpreterAsync(project.Directory, ct).ConfigureAwait(false);
        string[] args = ["-m", "pytest", "-p", PythonTestScripts.PytestPlugin, "--collect-only", "-q", .. PytestArgs()];
        log.Status($"Collecting the pytest tests of {project.Name}…");
        log.Output($"> {python} {string.Join(' ', args)}");
        var items = new List<JsonElement>();
        var exit = await context.Processes.RunAsync(new ProcessSpec(python, args, WorkingDirectory(project), Environment(helpers)),
            line =>
            {
                var e = Event(line, out var text);
                if (text.Length > 0) log.Output(text);
                if (e is { } item && Str(item, "event") == "item") items.Add(item);
            }, log.Output, ct).ConfigureAwait(false);
        if (exit is null) ct.ThrowIfCancellationRequested();
        if (items.Count == 0 && exit is not (0 or 5))
            throw new TestDiscoveryException($"pytest could not collect the tests of {project.Name} (exit code {exit}): see the Tests output.");
        return PytestTests(project, items);
    }

    /// <summary>The collected items as tests: one per node id without its parametrization, whose cases are the parametrizations.</summary>
    internal static IReadOnlyList<DiscoveredTest> PytestTests(TestProject project, IEnumerable<JsonElement> items)
    {
        var order = new List<string>();
        var byId = new Dictionary<string, (JsonElement Item, List<string> Cases)>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (Str(item, "id") is not { } nodeId) continue;
            var (testId, caseLabel) = PytestJunit.Split(nodeId);
            if (!byId.TryGetValue(testId, out var entry))
            {
                byId[testId] = entry = (item, []);
                order.Add(testId);
            }
            if (caseLabel is not null) entry.Cases.Add(caseLabel);
            if (order.Count >= TestLimits.MaxTestsPerProject) break;
        }
        return order.Select(id =>
        {
            var (item, cases) = byId[id];
            // pytest reports real paths (/private/var/… for /var/…, a symlinked checkout's target): the folder's own spelling is what the editor opens
            var file = Str(item, "file") is { Length: > 0 } f ? PhysicalPaths.UnderLogical(f, project.Directory) : Path.Combine(project.Directory, id.Split("::")[0]);
            var location = new TestLocation(file, Int(item, "line") ?? 0);
            var groups = new List<TestGroup> { new(Relative(project.Directory, file), TestNodeKind.File, new TestLocation(file, 0)) };
            if (Str(item, "cls") is { } cls) groups.Add(new TestGroup(cls, TestNodeKind.Class));
            return new DiscoveredTest(id, Str(item, "name") ?? id.Split("::")[^1], groups, location) { Cases = cases.Count > 0 ? cases : null };
        }).ToList();
    }

    private static TestLocation? Logical(TestLocation? location, string directory) =>
        location is null ? null : location with { FilePath = PhysicalPaths.UnderLogical(location.FilePath, directory) };

    private static string Relative(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file);
        return rel.StartsWith("..", StringComparison.Ordinal) ? file : rel.Replace('\\', '/');
    }

    private async Task<IReadOnlyList<DiscoveredTest>> DiscoverUnittestAsync(TestProject project, string helpers, ITestRunSink log, CancellationToken ct)
    {
        var python = await InterpreterAsync(project.Directory, ct).ConfigureAwait(false);
        var (start, pattern, top) = UnittestArgs(context.ListSetting?.Invoke(UnittestArgsSetting));
        string[] args = [Path.Combine(helpers, PythonTestScripts.UnittestScript), "discover", start, pattern, top];
        log.Status($"Discovering the unittest tests of {project.Name}…");
        var tests = new List<DiscoveredTest>();
        var exit = await context.Processes.RunAsync(new ProcessSpec(python, args, WorkingDirectory(project), Environment(helpers)), line =>
        {
            if (Event(line, out var text) is not { } e) { log.Output(line); return; }
            if (text.Length > 0) log.Output(text);
            if (Str(e, "event") == "error") { log.Output("unittest: " + Str(e, "message")); return; }
            if (Str(e, "event") != "item" || Str(e, "id") is not { } id || tests.Count >= TestLimits.MaxTestsPerProject) return;
            var file = Str(e, "file") is { } f ? PhysicalPaths.UnderLogical(f, project.Directory) : null;
            var location = file is null ? null : new TestLocation(file, Int(e, "line") ?? 0);
            var parts = id.Split('.');
            var module = parts.Length > 2 ? string.Join('.', parts[..^2]) : id; // module.Class.method
            var groups = new List<TestGroup>
            {
                new(file is null ? module : Relative(project.Directory, file), TestNodeKind.Module, file is null ? null : new TestLocation(file, 0)),
                new(Str(e, "cls") ?? "", TestNodeKind.Class),
            };
            tests.Add(new DiscoveredTest(id, Str(e, "name") ?? id, groups, location));
        }, log.Output, ct).ConfigureAwait(false);
        if (exit is null) ct.ThrowIfCancellationRequested();
        if (tests.Count == 0 && exit != 0) throw new TestDiscoveryException($"unittest could not discover the tests of {project.Name} (exit code {exit}): see the Tests output.");
        return tests;
    }

    public IReadOnlyList<SourceTest> ScanSource(string filePath, string text) =>
        [.. PythonTestScanner.Scan(text).Select(t => new SourceTest(t.Name, t.Line, t.Label))];

    /// <summary>A scanned <c>Class::method</c> also names unittest's dotted <c>module.Class.method</c>.</summary>
    public bool Matches(DiscoveredTest test, string filePath, SourceTest source) =>
        test.Location is { } loc && ITestProvider.PathsEqual(loc.FilePath, filePath)
        && (TestIds.EndsWithSegment(test.Id, source.Name) || TestIds.EndsWithSegment(test.Id, source.Name.Replace("::", ".", StringComparison.Ordinal)));

    // ------------------------------------------------------------------------------------------------ running

    /// <summary>pytest's arguments for a run: the plugin, the report, the settings' arguments and the node ids (none, or too many: all).</summary>
    internal List<string> PytestRunArguments(IReadOnlyList<string>? ids, string junit)
    {
        var args = new List<string> { "-p", PythonTestScripts.PytestPlugin, "--junitxml=" + junit, "-o", "junit_logging=all", "-o", "junit_family=xunit2" };
        args.AddRange(PytestArgs());
        if (ids is { Count: > 0 and <= MaxFilteredTests }) args.AddRange(ids);
        return args;
    }

    public Task<TestRunOutcome> RunAsync(TestRunRequest request, ITestRunSink sink, CancellationToken ct) => ExecuteAsync(request, sink, null, ct);

    public Task<TestRunOutcome> DebugAsync(TestRunRequest request, ITestRunSink sink, ITestDebugLauncher debugger, CancellationToken ct) => ExecuteAsync(request, sink, debugger, ct);

    private async Task<TestRunOutcome> ExecuteAsync(TestRunRequest request, ITestRunSink sink, ITestDebugLauncher? debugger, CancellationToken ct)
    {
        var project = request.Project;
        var helpers = PythonTestScripts.Write();
        try
        {
            var python = await InterpreterAsync(project.Directory, ct).ConfigureAwait(false);
            var cwd = WorkingDirectory(project);
            var env = Environment(helpers);
            var pytest = project.Framework != "unittest";
            var junit = Path.Combine(helpers, "junit.xml");
            List<string> args;
            if (pytest) args = PytestRunArguments(request.TestIds, junit);
            else
            {
                var (start, pattern, top) = UnittestArgs(context.ListSetting?.Invoke(UnittestArgsSetting));
                // the top-level directory only when -t gives one: unittest compares it with the REAL path of the start directory
                args = [Path.Combine(helpers, PythonTestScripts.UnittestScript), "run", top, start, pattern, .. request.TestIds is { Count: > 0 and <= MaxFilteredTests } ids ? ids : []];
            }

            var rootdir = cwd;
            var started = new List<string>();
            void OnLine(string line)
            {
                var found = Event(line, out var text);
                if (text.Length > 0 || found is null) sink.Output(text);
                if (found is not { } e) return;
                switch (Str(e, "event"))
                {
                    case "rootdir" when Str(e, "path") is { } root: rootdir = PhysicalPaths.UnderLogical(root, project.Directory); break;
                    case "start" when Str(e, "id") is { } id:
                        started.Add(id);
                        sink.Started(project, pytest ? PytestJunit.Split(id).TestId : id);
                        break;
                    case "result" when Str(e, "id") is { } id: // unittest's runner
                        var trace = Str(e, "traceback");
                        sink.Finished(project, new TestResult(id, Str(e, "outcome") switch { "passed" => TestOutcome.Passed, "skipped" => TestOutcome.Skipped, _ => TestOutcome.Failed },
                            e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? TimeSpan.FromSeconds(d.GetDouble()) : null,
                            Str(e, "message"), TestLimits.Clip(trace), null, Logical(StackTraces.FromPython(trace, project.Directory) ?? StackTraces.FromPython(trace, PhysicalPaths.Resolve(project.Directory)), project.Directory)));
                        break;
                }
            }

            int? exit;
            if (debugger is null)
            {
                string[] full = pytest ? ["-m", "pytest", .. args] : [.. args];
                sink.Status($"Running the tests of {project.Name}…");
                sink.Output($"> {python} {string.Join(' ', full)}");
                exit = await context.Processes.RunAsync(new ProcessSpec(python, full, cwd, env), OnLine, sink.Output, ct).ConfigureAwait(false);
                if (exit is null) return TestRunOutcome.Cancelled;
            }
            else
            {
                var config = DebugConfiguration(project, python, cwd, env, pytest, args);
                sink.Status($"Debugging the tests of {project.Name}…");
                var session = await debugger.StartAsync(config, ct).ConfigureAwait(false);
                if (session is null) return new TestRunOutcome(-1, "The Python debugger (debugpy) did not start: see the Debug Console.");
                exit = await DebugSessions.PumpOutputAsync(session, OnLine, ct).ConfigureAwait(false) ?? 0;
            }

            if (pytest)
            {
                var xml = await TestProviderContext.TryReadAsync(junit, ct).ConfigureAwait(false);
                if (xml is not null) foreach (var r in PytestJunit.Parse(xml, started, rootdir)) sink.Finished(project, r);
                if (exit is 5) return new TestRunOutcome(0); // no tests matched: nothing failed
                if (xml is null && exit is not 0) return new TestRunOutcome(exit, $"pytest ended with exit code {exit} before reporting results: see the Tests output.");
            }
            return new TestRunOutcome(exit);
        }
        finally { TestProviderContext.DeleteQuietly(helpers); }
    }

    /// <summary>The run's command as a debugpy launch configuration (VS Code's <c>purpose: ["debug-test"]</c>).</summary>
    internal static DebugConfiguration DebugConfiguration(TestProject project, string python, string cwd, IReadOnlyDictionary<string, string> env, bool pytest, IReadOnlyList<string> args)
    {
        var name = $"Debug tests: {project.Name}";
        var props = new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("debugpy"),
            ["request"] = JsonSerializer.SerializeToElement("launch"),
            ["name"] = JsonSerializer.SerializeToElement(name),
            ["python"] = JsonSerializer.SerializeToElement(python),
            ["cwd"] = JsonSerializer.SerializeToElement(cwd),
            ["env"] = JsonSerializer.SerializeToElement(env),
            ["console"] = JsonSerializer.SerializeToElement("internalConsole"),
            ["purpose"] = JsonSerializer.SerializeToElement(new[] { "debug-test" }),
            ["justMyCode"] = JsonSerializer.SerializeToElement(true),
            ["args"] = JsonSerializer.SerializeToElement(pytest ? args : args.Skip(1).ToArray()),
        };
        props[pytest ? "module" : "program"] = JsonSerializer.SerializeToElement(pytest ? "pytest" : args[0]);
        return new DebugConfiguration("debugpy", "launch", name, props, new Uri(project.Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar));
    }
}
