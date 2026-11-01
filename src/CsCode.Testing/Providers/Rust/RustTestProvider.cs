using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.Rust;

/// <summary>
/// Cargo packages. <b>Detection</b>: each <c>Cargo.toml</c> with a <c>[package]</c> under the folder. <b>Discovery</b> and every run start with
/// <c>cargo test --no-run --message-format=json-render-diagnostics</c> (cargo builds what changed and names each test executable with its
/// target); each executable is then asked <c>--list --format terse</c>, and each test's file and line found from the target's crate root
/// (<see cref="RustTestLocator"/>). A test's id is <c>&lt;target&gt;/&lt;libtest path&gt;</c> (<c>lib/tests::adds</c>,
/// <c>test:integ/integration_works</c>). <b>Running</b> runs the executables directly (with the package directory as working directory and
/// cargo's <c>CARGO_MANIFEST_DIR</c>/<c>CARGO_PKG_NAME</c>), with <c>--exact</c> and the requested names, and reads libtest's plain output
/// (<see cref="LibtestOutputParser"/>); a single requested test also runs if it is <c>#[ignore]</c>d. <b>Debugging</b> launches the test
/// executable under the Rust debugger (<c>cscode.debug.rustDebugger</c>: CodeLLDB <c>lldb</c> on macOS, <c>gdb</c> on Linux) with
/// <c>--exact --nocapture --test-threads=1</c>, results read from the session's output.
/// </summary>
public sealed class RustTestProvider(TestProviderContext context) : ITestProvider
{
    public const string ProviderId = "rust";
    public const string RustDebuggerSetting = "cscode.debug.rustDebugger";

    internal const int MaxFilteredTests = 500;

    public string Id => ProviderId;
    public string Label => "Rust";
    public IReadOnlyList<string> LanguageIds { get; } = ["rust"];

    /// <summary>A test executable cargo built: its target name and kinds, its crate root and the executable.</summary>
    public sealed record Target(string Name, IReadOnlyList<string> Kinds, string SourcePath, string Executable)
    {
        /// <summary>The target's part of a test id: <c>lib</c>, or <c>kind:name</c> for the others (<c>test:integ</c>, <c>bin:tool</c>).</summary>
        public string Key => Kinds.Contains("lib") || Kinds.Contains("rlib") || Kinds.Contains("proc-macro") ? "lib" : $"{Kinds.FirstOrDefault() ?? "bin"}:{Name}";

        public string Label => $"{Name} ({(Key == "lib" ? "lib" : Kinds.FirstOrDefault() ?? "bin")})";
    }

    // ------------------------------------------------------------------------------------------------ detection

    public async Task<IReadOnlyList<TestProject>> DetectAsync(string folder, CancellationToken ct)
    {
        var result = new List<TestProject>();
        foreach (var toml in SourceFiles.Find(folder, n => n == "Cargo.toml", 500, ct: ct))
        {
            var text = await TestProviderContext.TryReadAsync(toml, ct).ConfigureAwait(false);
            if (text is null || !text.Contains("[package]", StringComparison.Ordinal)) continue;
            result.Add(new TestProject(ProviderId, PackageName(text) ?? Path.GetFileName(Path.GetDirectoryName(toml)!), toml, Path.GetDirectoryName(toml)!, "cargo"));
        }
        return result;
    }

    /// <summary>The <c>name</c> of the manifest's <c>[package]</c> table.</summary>
    internal static string? PackageName(string toml)
    {
        var inPackage = false;
        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inPackage = line == "[package]"; continue; }
            if (!inPackage || !line.StartsWith("name", StringComparison.Ordinal)) continue;
            var eq = line.IndexOf('=');
            if (eq < 0 || line[..eq].Trim() != "name") continue;
            return line[(eq + 1)..].Trim().Trim('"', '\'');
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------------ building

    /// <summary>Builds the package's tests and returns the test executables (null: the build failed or was cancelled; the reason went to <paramref name="sink"/>).</summary>
    internal async Task<IReadOnlyList<Target>?> BuildAsync(TestProject project, ITestRunSink sink, CancellationToken ct)
    {
        sink.Status($"Building {project.Name}…");
        var args = new List<string> { "test", "--manifest-path", project.FilePath, "--no-run", "--message-format=json-render-diagnostics" };
        sink.Output($"> cargo {string.Join(' ', args)}");
        var stdout = new List<string>();
        var exit = await context.Processes.RunAsync(new ProcessSpec(ProcessRunner.FindCargo(), args, project.Directory), stdout.Add, sink.Output, ct).ConfigureAwait(false);
        if (exit is null) return null;
        if (exit != 0)
        {
            sink.Output($"> cargo exited with code {exit}.");
            return null;
        }
        return ParseArtifacts(stdout);
    }

    /// <summary>cargo's <c>compiler-artifact</c> messages for test executables (profile.test with an executable).</summary>
    internal static IReadOnlyList<Target> ParseArtifacts(IEnumerable<string> lines)
    {
        var result = new List<Target>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!line.StartsWith('{') || !line.Contains("\"compiler-artifact\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (!e.TryGetProperty("executable", out var exe) || exe.ValueKind != JsonValueKind.String) continue;
                if (!e.TryGetProperty("profile", out var profile) || !profile.TryGetProperty("test", out var isTest) || isTest.ValueKind != JsonValueKind.True) continue;
                var target = e.GetProperty("target");
                var kinds = target.TryGetProperty("kind", out var k) ? k.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
                var t = new Target(target.GetProperty("name").GetString() ?? "", kinds, target.TryGetProperty("src_path", out var sp) ? sp.GetString() ?? "" : "", exe.GetString()!);
                if (seen.Add(t.Key)) result.Add(t);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        return result;
    }

    // ------------------------------------------------------------------------------------------------ discovery

    public async Task<IReadOnlyList<DiscoveredTest>> DiscoverAsync(TestProject project, ITestRunSink log, CancellationToken ct)
    {
        var targets = await BuildAsync(project, log, ct).ConfigureAwait(false);
        if (targets is null) throw new TestDiscoveryException($"cargo could not build the tests of {project.Name}: see the Tests output.");
        var locator = new RustTestLocator(p => TestProviderContext.TryReadAsync(p, ct));
        var tests = new List<DiscoveredTest>();
        foreach (var target in targets)
        {
            log.Status($"Listing the tests of {target.Label}…");
            var names = new List<string>();
            var exit = await context.Processes.RunAsync(new ProcessSpec(target.Executable, ["--list", "--format", "terse"], project.Directory, Environment(project)),
                l => { if (l.EndsWith(": test", StringComparison.Ordinal)) names.Add(l[..^": test".Length]); }, log.Output, ct).ConfigureAwait(false);
            if (exit is null) ct.ThrowIfCancellationRequested();
            foreach (var name in names)
            {
                var loc = await locator.LocateAsync(target.SourcePath, name, ct).ConfigureAwait(false);
                tests.Add(Discovered(target, name, loc));
                if (tests.Count >= TestLimits.MaxTestsPerProject) return tests;
            }
        }
        return tests;
    }

    internal static DiscoveredTest Discovered(Target target, string libtestPath, TestLocation? location)
    {
        var segments = libtestPath.Split("::");
        var groups = new List<TestGroup> { new(target.Label, TestNodeKind.Target, new TestLocation(target.SourcePath, 0)) };
        groups.AddRange(segments[..^1].Select(s => new TestGroup(s, TestNodeKind.Module)));
        return new DiscoveredTest(target.Key + "/" + libtestPath, segments[^1], groups, location);
    }

    public IReadOnlyList<SourceTest> ScanSource(string filePath, string text) =>
        [.. RustTestScanner.Scan(text).Tests.Select(t => new SourceTest(t.Path, t.Line, t.Name))];

    // ------------------------------------------------------------------------------------------------ running

    private static Dictionary<string, string> Environment(TestProject project) => new()
    {
        ["CARGO_MANIFEST_DIR"] = project.Directory,
        ["CARGO_PKG_NAME"] = project.Name,
        ["RUST_BACKTRACE"] = "0",
    };

    /// <summary>Splits test ids (<c>target/path</c>) by target key; null ids mean every target, every test.</summary>
    internal static Dictionary<string, List<string>>? ByTarget(IReadOnlyList<string>? ids)
    {
        if (ids is null) return null;
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var slash = id.IndexOf('/');
            if (slash <= 0) continue;
            var key = id[..slash];
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(id[(slash + 1)..]);
        }
        return map;
    }

    /// <summary>The test binary's arguments: <c>--exact</c> and the names (none: all), <c>--include-ignored</c> for a single test.</summary>
    internal static List<string> RunArguments(IReadOnlyList<string>? names)
    {
        var args = new List<string>();
        if (names is { Count: > 0 and <= MaxFilteredTests })
        {
            args.AddRange(names);
            args.Add("--exact");
            if (names.Count == 1) args.Add("--include-ignored");
        }
        return args;
    }

    public async Task<TestRunOutcome> RunAsync(TestRunRequest request, ITestRunSink sink, CancellationToken ct)
    {
        var project = request.Project;
        var targets = await BuildAsync(project, sink, ct).ConfigureAwait(false);
        if (targets is null) return ct.IsCancellationRequested ? TestRunOutcome.Cancelled : new TestRunOutcome(101, $"cargo could not build the tests of {project.Name}: see the Tests output.");
        var wanted = ByTarget(request.TestIds);
        var worst = 0;
        foreach (var target in targets)
        {
            List<string>? names = null;
            if (wanted is not null && !wanted.TryGetValue(target.Key, out names)) continue;
            sink.Status($"Running {target.Label}…");
            var args = RunArguments(names);
            sink.Output($"> {target.Executable} {string.Join(' ', args)}");
            var parser = new LibtestOutputParser(project, sink, n => target.Key + "/" + n);
            var exit = await context.Processes.RunAsync(new ProcessSpec(target.Executable, args, project.Directory, Environment(project)), parser.OnLine, parser.OnLine, ct).ConfigureAwait(false);
            parser.Complete();
            if (exit is null) return TestRunOutcome.Cancelled;
            worst = Math.Max(worst, exit.Value);
        }
        return new TestRunOutcome(worst);
    }

    public async Task<TestRunOutcome> DebugAsync(TestRunRequest request, ITestRunSink sink, ITestDebugLauncher debugger, CancellationToken ct)
    {
        var project = request.Project;
        var targets = await BuildAsync(project, sink, ct).ConfigureAwait(false);
        if (targets is null) return ct.IsCancellationRequested ? TestRunOutcome.Cancelled : new TestRunOutcome(101, $"cargo could not build the tests of {project.Name}: see the Tests output.");
        var wanted = ByTarget(request.TestIds);
        // one session: the first target that has requested tests (a debug run of several targets debugs the first; the explorer says so)
        var target = wanted is null ? targets.FirstOrDefault() : targets.FirstOrDefault(t => wanted.ContainsKey(t.Key));
        if (target is null) return new TestRunOutcome(-1, "cargo built no test executable for the selected tests.");
        List<string>? names = null;
        wanted?.TryGetValue(target.Key, out names);
        var args = RunArguments(names);
        args.Add("--nocapture");
        args.Add("--test-threads=1");
        var type = DebugType(context.Setting(RustDebuggerSetting), OperatingSystem.IsMacOS());
        var props = new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement(type),
            ["request"] = JsonSerializer.SerializeToElement("launch"),
            ["name"] = JsonSerializer.SerializeToElement($"Debug tests: {target.Label}"),
            ["program"] = JsonSerializer.SerializeToElement(target.Executable),
            ["args"] = JsonSerializer.SerializeToElement(args),
            ["cwd"] = JsonSerializer.SerializeToElement(project.Directory),
            ["env"] = JsonSerializer.SerializeToElement(Environment(project)),
        };
        var config = new DebugConfiguration(type, "launch", $"Debug tests: {target.Label}", props, new Uri(project.Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar));
        sink.Status($"Debugging {target.Label}…");
        var session = await debugger.StartAsync(config, ct).ConfigureAwait(false);
        if (session is null) return new TestRunOutcome(-1, $"The debugger ({type}) did not start: see the Debug Console.");
        var parser = new LibtestOutputParser(project, sink, n => target.Key + "/" + n);
        var exit = await DebugSessions.PumpOutputAsync(session, parser.OnLine, ct).ConfigureAwait(false);
        parser.Complete();
        return new TestRunOutcome(exit ?? 0);
    }

    /// <summary>The debug type for <see cref="RustDebuggerSetting"/> (as the debugger module reads it): <c>lldb</c> (CodeLLDB) or <c>gdb</c>; unset: lldb on macOS, gdb elsewhere.</summary>
    internal static string DebugType(string? setting, bool macOS)
    {
        var chosen = setting?.Trim() is { Length: > 0 } s ? s : macOS ? "lldb" : "gdb";
        return chosen.Equals("lldb", StringComparison.OrdinalIgnoreCase) || chosen.Equals("codelldb", StringComparison.OrdinalIgnoreCase) ? "lldb" : "gdb";
    }
}

/// <summary>A discovery that could not complete (a build failure): the explorer shows the message on the project.</summary>
public sealed class TestDiscoveryException(string message) : Exception(message);
