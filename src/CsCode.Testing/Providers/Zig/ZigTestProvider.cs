using System.Text;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.Zig;

/// <summary>
/// Zig projects (docs/ZIG.md). <b>Detection</b>: each <c>build.zig</c> under the folder. <b>Discovery</b> runs nothing: the project's
/// <c>.zig</c> files (not <c>zig-out</c>, caches or nested projects) are scanned with <see cref="ZigTestScanner"/>; a test's id is
/// <c>&lt;file relative to the project&gt;::&lt;qualified name&gt;</c> (<c>src/root.zig::test.adds</c>, <c>src/root.zig::decltest.add</c>,
/// <c>src/root.zig::Inner.test.nested</c>). <b>Running</b> uses <c>zig test</c>, whose test runner prints one line per test
/// (<see cref="ZigTestOutputParser"/>), with <c>--test-filter</c> per requested test. A file is compiled as the build compiles it: the
/// <c>zig test</c> command lines of the build's <c>test</c> step (their <c>--dep</c>/<c>-M</c> module flags, so <c>@import("mymodule")</c>
/// resolves) are learned once from <c>zig build test --verbose</c> and kept until build.zig or build.zig.zon changes (on a cached build that
/// takes a fraction of a second and runs no test); a file that is not one of the step's roots borrows the flags of the nearest root, and a
/// project without a test step runs <c>zig test &lt;file&gt;</c>. Running the whole project runs each root of the test step.
/// <b>Debugging</b> compiles the same command with <c>--test-no-exec -femit-bin</c> and launches that binary under CodeLLDB (<c>lldb</c>),
/// results read from the session's output.
/// </summary>
public sealed class ZigTestProvider(TestProviderContext context) : ITestProvider
{
    public const string ProviderId = "zig";
    /// <summary>VS Code's Zig extension setting: the zig executable (or its directory).</summary>
    public const string ZigPathSetting = "zig.path";
    /// <summary>The zig zls is told to use (forwarded to zls by workspace/configuration).</summary>
    public const string ZlsZigPathSetting = "zls.zig_exe_path";
    /// <summary>The debug type test debugging uses: CodeLLDB.</summary>
    public const string DebugType = "lldb";

    internal const int MaxFilteredTests = 200;
    internal const int MaxSourceFiles = 5000;
    /// <summary>The learning build compiles (and on a changed project runs) the test step: bounded.</summary>
    internal static readonly TimeSpan LearnTimeout = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _learnGate = new(1, 1);
    private readonly Dictionary<string, (string Stamp, IReadOnlyList<TestCommand> Commands)> _learned = new(StringComparer.Ordinal);

    public string Id => ProviderId;
    public string Label => "Zig";
    public IReadOnlyList<string> LanguageIds { get; } = ["zig"];

    /// <summary>One <c>zig test</c> compilation of the build's test step: its root file (relative to the project) and its arguments after <c>test</c>.</summary>
    public sealed record TestCommand(string Root, IReadOnlyList<string> Arguments);

    // ------------------------------------------------------------------------------------------------ detection & discovery

    public Task<IReadOnlyList<TestProject>> DetectAsync(string folder, CancellationToken ct)
    {
        var result = new List<TestProject>();
        foreach (var build in SourceFiles.Find(folder, n => n == "build.zig", 500, ct: ct))
        {
            if (IsInBuildOutput(build)) continue;
            var dir = Path.GetDirectoryName(build)!;
            result.Add(new TestProject(ProviderId, Path.GetFileName(dir), build, dir, "zig"));
        }
        return Task.FromResult<IReadOnlyList<TestProject>>(result);
    }

    public async Task<IReadOnlyList<DiscoveredTest>> DiscoverAsync(TestProject project, ITestRunSink log, CancellationToken ct)
    {
        var tests = new List<DiscoveredTest>();
        foreach (var file in Sources(project.Directory, ct))
        {
            if (await TestProviderContext.TryReadAsync(file, ct).ConfigureAwait(false) is not { } text || text.Length > TestLimits.MaxScannedFileBytes) continue;
            var relative = Relative(project, file);
            foreach (var t in ZigTestScanner.Scan(text))
            {
                var groups = new List<TestGroup> { new(relative, TestNodeKind.File, new TestLocation(file, 0)) };
                groups.AddRange(t.Containers.Select(c => new TestGroup(c, TestNodeKind.Class)));
                tests.Add(new DiscoveredTest(relative + "::" + t.Qualified, t.Name, groups, new TestLocation(file, t.Line)));
                if (tests.Count >= TestLimits.MaxTestsPerProject) return tests;
            }
        }
        return tests;
    }

    public IReadOnlyList<SourceTest> ScanSource(string filePath, string text) =>
        [.. ZigTestScanner.Scan(text).Select(t => new SourceTest(t.Qualified, t.Line, t.Name))];

    /// <summary>The project's Zig sources: not build.zig, zig-out or the caches, and not the files of a nested project.</summary>
    internal static IEnumerable<string> Sources(string directory, CancellationToken ct) =>
        SourceFiles.Find(directory, n => n.EndsWith(".zig", StringComparison.Ordinal) && n != "build.zig", MaxSourceFiles, stopAt: n => n == "build.zig", ct: ct)
            .Where(f => !IsInBuildOutput(f));

    private static bool IsInBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "zig-out" or "zig-cache" or ".zig-cache");

    internal static string Relative(TestProject project, string file) => Path.GetRelativePath(project.Directory, file).Replace('\\', '/');

    // ------------------------------------------------------------------------------------------------ the zig executable

    internal string Zig()
    {
        foreach (var key in new[] { ZigPathSetting, ZlsZigPathSetting })
            if (context.Setting(key) is { Length: > 0 } configured)
            {
                var exe = OperatingSystem.IsWindows() ? "zig.exe" : "zig";
                if (File.Exists(configured)) return configured;
                if (Directory.Exists(configured) && File.Exists(Path.Combine(configured, exe))) return Path.Combine(configured, exe);
            }
        return ProcessRunner.FindOnPath("zig") ?? "zig";
    }

    // ------------------------------------------------------------------------------------------------ the build's test step

    /// <summary>
    /// The <c>zig test</c> commands of the project's <c>test</c> step, from <c>zig build test --verbose</c>; cached until build.zig or
    /// build.zig.zon changes. Empty without a test step (or when the build script fails before compiling anything).
    /// </summary>
    internal async Task<IReadOnlyList<TestCommand>> LearnAsync(TestProject project, ITestRunSink sink, CancellationToken ct)
    {
        var stamp = Stamp(project.FilePath) + "|" + Stamp(Path.Combine(project.Directory, "build.zig.zon"));
        await _learnGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_learned.TryGetValue(project.Key, out var known) && known.Stamp == stamp) return known.Commands;
            sink.Status($"Reading the test step of {project.Name}'s build.zig…");
            string[] args = ["build", "test", "--verbose", "--summary", "none", "--color", "off"];
            sink.Output($"> zig {string.Join(' ', args)}");
            var lines = new List<string>();
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(LearnTimeout);
            void OnLine(string line)
            {
                if (lines.Count < 10_000) lines.Add(line);
                sink.Output(line);
            }
            var exit = await context.Processes.RunAsync(new ProcessSpec(Zig(), args, project.Directory), OnLine, OnLine, bounded.Token).ConfigureAwait(false);
            if (exit is null)
            {
                ct.ThrowIfCancellationRequested();
                sink.Output($"> zig build test did not finish within {LearnTimeout.TotalMinutes:0} minutes: each file runs with zig test alone.");
                return [];
            }
            var commands = ParseVerbose(lines, project.Directory);
            if (commands.Count == 0) sink.Output("> No zig test command in the build's test step: each file runs with zig test alone.");
            if (_learned.Count >= 64) _learned.Clear();
            _learned[project.Key] = (stamp, commands);
            return commands;
        }
        finally { _learnGate.Release(); }
    }

    private static string Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "-";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "?"; }
    }

    /// <summary>
    /// The <c>zig test</c> lines of <c>--verbose</c> (<c>info(verbose): /zig test -Odebug --dep m -Mroot=src/main.zig -Mm=src/root.zig … --listen=-</c>),
    /// each kept once per root, without <c>--listen=-</c> (the build runner's protocol) and the directories CsCode's own zig knows
    /// (<c>--zig-lib-dir</c>, <c>--global-cache-dir</c>).
    /// </summary>
    internal static IReadOnlyList<TestCommand> ParseVerbose(IEnumerable<string> lines, string projectDirectory)
    {
        var result = new List<TestCommand>();
        foreach (var line in lines)
        {
            const string prefix = "info(verbose): ";
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var words = SplitCommandLine(line[prefix.Length..]);
            if (words.Count < 3 || words[1] != "test") continue;
            var args = new List<string>();
            string? root = null;
            for (var i = 2; i < words.Count; i++)
            {
                var w = words[i];
                if (w == "--listen=-") continue;
                if (w is "--zig-lib-dir" or "--global-cache-dir") { i++; continue; }
                if (w.StartsWith("-Mroot=", StringComparison.Ordinal)) root = w["-Mroot=".Length..];
                args.Add(w);
            }
            if (root is null) continue;
            var relative = Path.IsPathRooted(root) ? Path.GetRelativePath(projectDirectory, root) : root;
            relative = relative.Replace('\\', '/');
            if (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
            if (result.Any(c => c.Root == relative)) continue;
            result.Add(new TestCommand(relative, args));
        }
        return result;
    }

    /// <summary>Words of a command line: spaces separate them, single or double quotes group them.</summary>
    internal static List<string> SplitCommandLine(string text)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        var any = false;
        char quote = '\0';
        foreach (var c in text)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else sb.Append(c);
                continue;
            }
            if (c is '\'' or '"') { quote = c; any = true; continue; }
            if (c == ' ')
            {
                if (any || sb.Length > 0) words.Add(sb.ToString());
                sb.Clear();
                any = false;
                continue;
            }
            sb.Append(c);
        }
        if (any || sb.Length > 0) words.Add(sb.ToString());
        return words;
    }

    // ------------------------------------------------------------------------------------------------ planning a run

    /// <summary>One <c>zig test</c> process: the root file it compiles (relative to the project) and its arguments after <c>test</c>.</summary>
    internal sealed record Run(string Root, IReadOnlyList<string> Arguments);

    /// <summary>
    /// The <c>zig test</c> processes for <paramref name="ids"/> (null: the whole project): per file, the build's command for it (or the nearest
    /// root's, with the file as the root module), with a <c>--test-filter</c> per test; the whole project runs each root of the build's test step,
    /// or each file declaring tests when there is no test step.
    /// </summary>
    internal static IReadOnlyList<Run> Plan(IReadOnlyList<string>? ids, IReadOnlyList<TestCommand> commands, IReadOnlyList<string> filesWithTests)
    {
        if (ids is null)
            return commands.Count > 0
                ? [.. commands.Select(c => new Run(c.Root, c.Arguments))]
                : [.. filesWithTests.Select(f => new Run(f, ["-Mroot=" + f]))];
        var runs = new List<Run>();
        foreach (var file in ids.Select(Split).Where(x => x is not null).GroupBy(x => x!.Value.File, StringComparer.Ordinal))
        {
            var args = new List<string>(CommandFor(file.Key, commands));
            var names = file.Select(x => x!.Value.Qualified).Distinct(StringComparer.Ordinal).ToList();
            if (names.Count <= MaxFilteredTests)
                foreach (var name in names)
                {
                    args.Add("--test-filter");
                    args.Add(RunnerPrefix(file.Key, file.Key) + "." + name);
                }
            runs.Add(new Run(file.Key, args));
        }
        return runs;
    }

    /// <summary>The arguments that compile <paramref name="file"/> as the build compiles it (see <see cref="Plan"/>).</summary>
    internal static IReadOnlyList<string> CommandFor(string file, IReadOnlyList<TestCommand> commands)
    {
        if (commands.FirstOrDefault(c => c.Root == file) is { } own) return own.Arguments;
        var dir = Dir(file);
        var nearest = commands
            .Where(c => Dir(c.Root) is var d && (d.Length == 0 || dir == d || dir.StartsWith(d + "/", StringComparison.Ordinal)))
            .OrderByDescending(c => Dir(c.Root).Length)
            .FirstOrDefault();
        if (nearest is null) return ["-Mroot=" + file];
        return [.. nearest.Arguments.Select(a => a.StartsWith("-Mroot=", StringComparison.Ordinal) ? "-Mroot=" + file : a)];

        static string Dir(string relative) => relative.LastIndexOf('/') is var i and >= 0 ? relative[..i] : "";
    }

    /// <summary>The file and qualified name of an id (<c>src/root.zig::test.adds</c>).</summary>
    internal static (string File, string Qualified)? Split(string id)
    {
        var sep = id.IndexOf("::", StringComparison.Ordinal);
        return sep > 0 ? (id[..sep], id[(sep + 2)..]) : null;
    }

    /// <summary>
    /// How the test runner names <paramref name="file"/>'s namespace when <paramref name="root"/> is the root module: its path from the root's
    /// directory, without <c>.zig</c>, with dots for slashes (<c>root</c>, <c>sub.util</c>).
    /// </summary>
    internal static string RunnerPrefix(string root, string file)
    {
        var rootDir = root.LastIndexOf('/') is var i and >= 0 ? root[..(i + 1)] : "";
        var path = file.StartsWith(rootDir, StringComparison.Ordinal) ? file[rootDir.Length..] : Path.GetFileName(file);
        if (path.EndsWith(".zig", StringComparison.Ordinal)) path = path[..^4];
        return path.Replace('/', '.');
    }

    /// <summary>
    /// The test id of a runner name, for a process whose root module is <paramref name="root"/>: the name's namespace is a file under the
    /// root's directory (longest match), then containers, then <c>test.</c>/<c>decltest.</c> and the test's name. Null for an unnamed block
    /// (<c>root.test_0</c>) and for a name whose file is not in the project.
    /// </summary>
    internal static Func<string, string?> IdMapper(TestProject project, string root)
    {
        var rootDir = Path.GetDirectoryName(Path.Combine(project.Directory, root))!;
        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        return name =>
        {
            if (cache.TryGetValue(name, out var known)) return known;
            string? id = null;
            var marker = Marker(name);
            if (marker > 0)
            {
                var segments = name[..marker].Split('.');
                var rest = name[(marker + 1)..];
                for (var take = segments.Length; take >= 1 && id is null; take--)
                {
                    var file = Path.Combine(rootDir, Path.Combine(segments[..take])) + ".zig";
                    if (!File.Exists(file) || !StackTraces.IsUnder(file, project.Directory)) continue;
                    id = Relative(project, file) + "::" + string.Concat(segments[take..].Select(s => s + ".")) + rest;
                }
            }
            if (cache.Count < 10_000) cache[name] = id;
            return id;
        };

        static int Marker(string name)
        {
            var test = name.IndexOf(".test.", StringComparison.Ordinal);
            var decl = name.IndexOf(".decltest.", StringComparison.Ordinal);
            return test < 0 ? decl : decl < 0 ? test : Math.Min(test, decl);
        }
    }

    /// <summary>The files that declare tests, when the whole project runs without a test step (nothing else needs them).</summary>
    private static async Task<IReadOnlyList<string>> FilesWithTestsAsync(TestProject project, IReadOnlyList<string>? ids, IReadOnlyList<TestCommand> commands, CancellationToken ct)
    {
        if (ids is not null || commands.Count > 0) return [];
        var files = new List<string>();
        foreach (var file in Sources(project.Directory, ct))
            if (await TestProviderContext.TryReadAsync(file, ct).ConfigureAwait(false) is { } text && ZigTestScanner.Scan(text).Count > 0)
                files.Add(Relative(project, file));
        return files;
    }

    // ------------------------------------------------------------------------------------------------ running

    public async Task<TestRunOutcome> RunAsync(TestRunRequest request, ITestRunSink sink, CancellationToken ct)
    {
        var project = request.Project;
        var commands = await LearnAsync(project, sink, ct).ConfigureAwait(false);
        var runs = Plan(request.TestIds, commands, await FilesWithTestsAsync(project, request.TestIds, commands, ct).ConfigureAwait(false));
        var worst = 0;
        string? error = null;
        foreach (var run in runs)
        {
            sink.Status($"Running the tests of {run.Root}…");
            var args = new List<string> { "test" };
            args.AddRange(run.Arguments);
            var spec = new ProcessSpec(Zig(), args, project.Directory);
            sink.Output($"> {spec}");
            var parser = new ZigTestOutputParser(project, sink, Reporting(project, run.Root, sink));
            var exit = await context.Processes.RunAsync(spec, parser.OnLine, parser.OnLine, ct).ConfigureAwait(false);
            parser.Complete();
            if (exit is null) return TestRunOutcome.Cancelled;
            worst = Math.Max(worst, exit.Value);
            if (exit != 0 && parser.Total is null)
                error ??= parser.CompileErrors.Count > 0
                    ? $"zig test could not compile {run.Root}: {parser.CompileErrors[0]}"
                    : $"zig test {run.Root} failed with exit code {exit} before running a test: see the Tests output.";
        }
        return new TestRunOutcome(worst, error);
    }

    /// <summary>The id mapper of a run, reporting a named test the scan did not find (a generated file, a test in a file outside the scan) as discovered.</summary>
    private static Func<string, string?> Reporting(TestProject project, string root, ITestRunSink sink)
    {
        var map = IdMapper(project, root);
        var discovered = new HashSet<string>(StringComparer.Ordinal);
        return name =>
        {
            if (map(name) is { } id) return id;
            if (!name.Contains(".test.", StringComparison.Ordinal) && !name.Contains(".decltest.", StringComparison.Ordinal)) return null;
            var extra = root + "::" + name;
            if (discovered.Count < 1000 && discovered.Add(extra))
                sink.Discovered(project, new DiscoveredTest(extra, name, [new TestGroup(root, TestNodeKind.File, new TestLocation(Path.Combine(project.Directory, root), 0))]));
            return extra;
        };
    }

    public async Task<TestRunOutcome> DebugAsync(TestRunRequest request, ITestRunSink sink, ITestDebugLauncher debugger, CancellationToken ct)
    {
        var project = request.Project;
        var commands = await LearnAsync(project, sink, ct).ConfigureAwait(false);
        // one session: the first file with requested tests (a debug run of several files debugs the first; the explorer says so)
        var run = Plan(request.TestIds, commands, await FilesWithTestsAsync(project, request.TestIds, commands, ct).ConfigureAwait(false)).FirstOrDefault();
        if (run is null) return new TestRunOutcome(-1, "No Zig test to debug.");
        var temp = TestProviderContext.NewTempDirectory();
        try
        {
            var binary = Path.Combine(temp, Path.GetFileNameWithoutExtension(run.Root) + "-test" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            var args = new List<string> { "test" };
            args.AddRange(run.Arguments);
            args.Add("--test-no-exec");
            args.Add("-femit-bin=" + binary);
            var spec = new ProcessSpec(Zig(), args, project.Directory);
            sink.Status($"Building the tests of {run.Root}…");
            sink.Output($"> {spec}");
            var errors = new List<string>();
            var exit = await context.Processes.RunAsync(spec, sink.Output, line => { sink.Output(line); if (errors.Count < 5 && line.Contains(": error: ", StringComparison.Ordinal)) errors.Add(line); }, ct).ConfigureAwait(false);
            if (exit is null) return TestRunOutcome.Cancelled;
            if (exit != 0 || !File.Exists(binary))
                return new TestRunOutcome(exit ?? -1, errors.Count > 0 ? $"zig test could not compile {run.Root}: {errors[0]}" : $"zig test could not build the tests of {run.Root}: see the Tests output.");

            var name = $"Debug tests: {run.Root}";
            var props = new Dictionary<string, JsonElement>
            {
                ["type"] = JsonSerializer.SerializeToElement(DebugType),
                ["request"] = JsonSerializer.SerializeToElement("launch"),
                ["name"] = JsonSerializer.SerializeToElement(name),
                ["program"] = JsonSerializer.SerializeToElement(binary),
                ["args"] = JsonSerializer.SerializeToElement(Array.Empty<string>()),
                ["cwd"] = JsonSerializer.SerializeToElement(project.Directory),
                ["sourceLanguages"] = JsonSerializer.SerializeToElement(new[] { "zig" }),
            };
            var config = new DebugConfiguration(DebugType, "launch", name, props, new Uri(project.Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar));
            sink.Status($"Debugging the tests of {run.Root}…");
            var session = await debugger.StartAsync(config, ct).ConfigureAwait(false);
            if (session is null) return new TestRunOutcome(-1, $"The debugger ({DebugType}) did not start: see the Debug Console.");
            var parser = new ZigTestOutputParser(project, sink, Reporting(project, run.Root, sink));
            var code = await DebugSessions.PumpOutputAsync(session, parser.OnLine, ct).ConfigureAwait(false);
            parser.Complete();
            return new TestRunOutcome(code ?? 0);
        }
        finally { TestProviderContext.DeleteQuietly(temp); }
    }
}
