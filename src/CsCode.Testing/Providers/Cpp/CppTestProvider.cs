using System.Collections.Concurrent;
using System.Text.Json;
using CsCode.Core.Build;
using CsCode.Core.Debugging;
using CsCode.Testing.Engine;
using CsCode.Testing.Providers.CTest;
using CsCode.Testing.Providers.Rust;

namespace CsCode.Testing.Providers.Cpp;

/// <summary>
/// C++ test executables built with GoogleTest, Catch2 (v3; v2 without locations) or doctest. <b>Detection</b>: the files matching the
/// <c>cscode.testing.cpp.executables</c> globs (else C++ TestMate's <c>testMate.cpp.test.executables</c>, else TestMate's default
/// <see cref="CppExecutables.DefaultPatterns"/>) that are native executables containing a framework's signature (<see cref="CppExecutables"/>);
/// each is a project (its framework shown beside it), except an executable whose tests CTest already lists one by one (<c>gtest_discover_tests</c>,
/// <c>catch_discover_tests</c>, <c>doctest_discover_tests</c>, <c>gtest_add_tests</c>: <see cref="RegisteredByCTestAsync"/>), which the CTest
/// provider shows instead. <b>Discovery</b> runs the executable's listing (GoogleTest's JSON listing, Catch2's and
/// doctest's XML listings, all with file and line), bounded in time and output, and caches it by the executable's size and modification time:
/// an executable is listed again only after it was rebuilt. A test's id is <c>&lt;executable&gt;/&lt;runner name&gt;</c>
/// (<c>math_tests/MathTest.Adds</c>, <c>catch_tests/adds numbers</c>, <c>doc_tests/suite::name</c>), so the same test name in two executables
/// stays two tests. <b>Running</b> runs the executable in its own directory with the framework's filter and reads its results file
/// (GoogleTest's console as it comes, too); a test the executable was running when it crashed fails with the exit code. <b>Debugging</b>
/// launches it under <c>cscode.testing.cpp.debugger</c> (unset: the CMake integration's <c>cscode.cmake.debugger</c>, CodeLLDB on macOS, GDB
/// elsewhere; lldb-dap as an alternative) with the same filter,
/// breaking into the debugger at a failed assertion (<c>cscode.testing.cpp.breakOnFailure</c>), and reads the results file when the session ends.
/// </summary>
public sealed class CppTestProvider(TestProviderContext context, Func<string, JsonElement?>? rawSetting = null, ICMakeService? cmake = null) : ITestProvider
{
    public const string ProviderId = "cpp";
    public const string ExecutablesSetting = "cscode.testing.cpp.executables";
    public const string TestMateExecutablesSetting = "testMate.cpp.test.executables";
    public const string DebuggerSetting = "cscode.testing.cpp.debugger";
    public const string BreakOnFailureSetting = "cscode.testing.cpp.breakOnFailure";

    /// <summary>How long a listing may take (tests shorten it).</summary>
    internal TimeSpan ListTimeout { get; init; } = TimeSpan.FromSeconds(20);
    internal const long MaxListOutputChars = 16L << 20;
    internal const int MaxFilteredTests = 300;
    internal const int MaxFilterChars = 24_000;
    private const int MaxCachedListings = 256;

    private readonly CppExecutables _executables = new();
    private readonly CTestProvider? _ctest = cmake is null ? null : new CTestProvider(context, cmake);
    private readonly ConcurrentDictionary<string, Listing> _listings = new(StringComparer.Ordinal);

    /// <summary>An executable's tests as of one build (size and modification time), with each test's name for the runner's filter.</summary>
    private sealed record Listing(long Size, DateTime Modified, IReadOnlyList<DiscoveredTest> Tests, IReadOnlyDictionary<string, string> FilterNames, IReadOnlySet<string> Skipped);

    public string Id => ProviderId;
    public string Label => "C++";
    public IReadOnlyList<string> LanguageIds { get; } = ["cpp", "cuda-cpp", "objective-cpp"];

    internal CppExecutables Executables => _executables;

    /// <summary>Whether the CTest provider is on (its executables are then left to it); the host passes the provider's enabled setting.</summary>
    public Func<bool> CTestEnabled { get; init; } = () => true;

    // ------------------------------------------------------------------------------------------------ detection

    /// <summary>The executables' globs: our setting when it is set to something other than its default, else TestMate's, else the default.</summary>
    internal IReadOnlyList<string> Patterns()
    {
        var own = CppExecutables.Patterns(rawSetting?.Invoke(ExecutablesSetting));
        if (own is { Count: > 0 } && !own.SequenceEqual(CppExecutables.DefaultPatterns)) return own;
        if (CppExecutables.Patterns(rawSetting?.Invoke(TestMateExecutablesSetting)) is { Count: > 0 } testMate) return testMate;
        return own is { Count: > 0 } ? own : CppExecutables.DefaultPatterns;
    }

    public async Task<IReadOnlyList<TestProject>> DetectAsync(string folder, CancellationToken ct)
    {
        var result = new List<TestProject>();
        var found = _executables.Find(folder, Patterns(), ct).ToList();
        if (found.Count == 0) return result;
        var registered = await RegisteredByCTestAsync(folder, ct).ConfigureAwait(false);
        foreach (var exe in found)
            if (registered.Count == 0 || !registered.Contains(PhysicalPaths.Resolve(exe.Path)))
                result.Add(new TestProject(ProviderId, ProjectName(folder, exe.Path), exe.Path, folder, CppExecutables.Label(exe.Framework)));
        return result;
    }

    /// <summary>
    /// The executables (resolved paths) of the folder's CMake projects whose tests CTest lists one by one, so that they are not listed twice: those
    /// a CTest test runs with a single test's filter (GoogleTest's <c>--gtest_filter=</c>, doctest's <c>--test-case=</c>), or with arguments from
    /// a test that has no <c>add_test</c> line (the discovery modules add theirs from a generated file when ctest runs). Empty when the CTest
    /// provider is off, or no project under the folder was built with CTest.
    /// </summary>
    internal async Task<IReadOnlySet<string>> RegisteredByCTestAsync(string folder, CancellationToken ct)
    {
        var result = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        if (_ctest is null || cmake is null || !CTestEnabled()) return result;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        foreach (var p in cmake.Projects)
        {
            var inFolder = ITestProvider.PathsEqual(p.SourceDirectory, root) || StackTraces.IsUnder(p.SourceDirectory, root);
            if (!inFolder || !File.Exists(Path.Combine(p.BuildDirectory, "CTestTestfile.cmake"))) continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ListTimeout);
            IReadOnlyList<CTestInfo>? tests;
            try { tests = await _ctest.ListAsync(p, Quiet.Instance, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            foreach (var t in tests ?? [])
                if (t.Executable is { Length: > 0 } exe && RunsSingleTests(t))
                    result.Add(PhysicalPaths.Resolve(Path.IsPathRooted(exe) ? exe : Path.Combine(t.WorkingDirectory ?? p.BuildDirectory, exe)));
        }
        return result;
    }

    private static bool RunsSingleTests(CTestInfo t) =>
        t.Command.Skip(1).Any(a => a.StartsWith("--gtest_filter=", StringComparison.Ordinal) || a.StartsWith("--test-case=", StringComparison.Ordinal))
        || (t.File is null && t.Command.Count > 1);

    /// <summary>Drops the CTest listing's progress lines (detection has nowhere to show them).</summary>
    private sealed class Quiet : ITestRunSink
    {
        public static readonly Quiet Instance = new();
        public void Discovered(TestProject project, DiscoveredTest test) { }
        public void Started(TestProject project, string testId) { }
        public void Finished(TestProject project, TestResult result) { }
        public void Output(string line) { }
    }

    /// <summary>The executable's path relative to the folder (<c>build/math_tests</c>), or its name when it is elsewhere.</summary>
    internal static string ProjectName(string folder, string executable) =>
        StackTraces.IsUnder(executable, folder) ? Path.GetRelativePath(folder, executable).Replace('\\', '/') : Path.GetFileName(executable);

    /// <summary>The executable's part of a test id: its file name (without <c>.exe</c>).</summary>
    internal static string Prefix(TestProject project) =>
        Path.GetFileName(project.FilePath) is var n && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;

    /// <summary>The runner's name of a test id (what follows the executable's part).</summary>
    internal static string RunnerName(string id) => id.IndexOf('/') is var slash and >= 0 ? id[(slash + 1)..] : id;

    // ------------------------------------------------------------------------------------------------ discovery

    public async Task<IReadOnlyList<DiscoveredTest>> DiscoverAsync(TestProject project, ITestRunSink log, CancellationToken ct) =>
        (await ListAsync(project, log, ct).ConfigureAwait(false)).Tests;

    private async Task<Listing> ListAsync(TestProject project, ITestRunSink log, CancellationToken ct)
    {
        var exe = project.FilePath;
        var info = new FileInfo(exe);
        if (!info.Exists) throw new TestDiscoveryException($"{exe} does not exist (build it, then Refresh).");
        if (_listings.TryGetValue(exe, out var cached) && cached.Size == info.Length && cached.Modified == info.LastWriteTimeUtc) return cached;
        var framework = _executables.Classify(exe);
        if (framework == CppFramework.None) throw new TestDiscoveryException($"{exe} is no longer a GoogleTest, Catch2 or doctest executable.");
        log.Status($"Listing the tests of {project.Name}…");
        var listed = framework switch
        {
            CppFramework.GoogleTest => await ListGoogleTestAsync(project, log, ct).ConfigureAwait(false),
            CppFramework.Catch2 => await ListCatch2Async(project, log, ct).ConfigureAwait(false),
            _ => await ListDoctestAsync(project, log, ct).ConfigureAwait(false),
        };
        var prefix = Prefix(project);
        var tests = new List<DiscoveredTest>(listed.Count);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in listed)
        {
            var location = t.File is { Length: > 0 } f ? new TestLocation(Logical(f, project), Math.Max(0, t.Line - 1)) : null;
            var groups = t.Groups.Count > 0 || location is null || framework == CppFramework.GoogleTest
                ? t.Groups
                : [new TestGroup(ProjectName(project.Directory, location.FilePath), TestNodeKind.File, location with { Line = 0 })];
            var id = prefix + "/" + t.Name;
            if (!names.TryAdd(t.Name, framework == CppFramework.Doctest ? DoctestName(t) : t.Name)) continue;
            if (t.SkipReason is not null) skipped.Add(t.Name);
            tests.Add(new DiscoveredTest(id, t.Label, groups, location, t.SkipReason));
        }
        var listing = new Listing(info.Length, info.LastWriteTimeUtc, tests, names, skipped);
        if (_listings.Count >= MaxCachedListings) _listings.Clear();
        _listings[exe] = listing;
        return listing;
    }

    /// <summary>doctest filters by the test case's own name (its suite is a separate filter): the label, untrimmed.</summary>
    private static string DoctestName(CppListedTest t) => t.Name.IndexOf("::", StringComparison.Ordinal) is var sep and >= 0 && t.Groups.Count > 0 ? t.Name[(sep + 2)..] : t.Name;

    /// <summary>A path a tool reported (absolute, or relative to the folder), put back under the folder as the editor knows it.</summary>
    private static string Logical(string file, TestProject project)
    {
        if (!Path.IsPathRooted(file))
        {
            var inFolder = Path.GetFullPath(Path.Combine(project.Directory, file));
            file = File.Exists(inFolder) ? inFolder : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project.FilePath)!, file));
        }
        return PhysicalPaths.UnderLogical(file, project.Directory);
    }

    private static string WorkingDirectory(TestProject project) => Path.GetDirectoryName(project.FilePath)!;

    /// <summary>Runs a listing: at most <see cref="ListTimeout"/> and <see cref="MaxListOutputChars"/> of standard output; stderr goes to the log.</summary>
    private async Task<List<string>> RunListingAsync(TestProject project, IReadOnlyList<string> args, ITestRunSink log, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(ListTimeout);
        var lines = new List<string>();
        long chars = 0;
        var overflow = false;
        var spec = new ProcessSpec(project.FilePath, args, WorkingDirectory(project));
        log.Output($"> {spec}");
        var exit = await context.Processes.RunAsync(spec, line =>
        {
            lock (lines)
            {
                if (overflow) return;
                chars += line.Length + 1;
                if (chars > MaxListOutputChars) { overflow = true; bounded.Cancel(); return; }
                lines.Add(line);
            }
        }, log.Output, bounded.Token).ConfigureAwait(false);
        if (exit is null)
        {
            ct.ThrowIfCancellationRequested();
            throw new TestDiscoveryException(overflow
                ? $"{project.Name} printed more than {MaxListOutputChars >> 20} MB when asked for its tests; it was stopped."
                : $"{project.Name} did not list its tests within {ListTimeout.TotalSeconds:0} s; it was stopped.");
        }
        if (exit != 0) log.Output($"> {project.Name} exited with code {exit} when listing its tests.");
        lock (lines) return lines;
    }

    private async Task<IReadOnlyList<CppListedTest>> ListGoogleTestAsync(TestProject project, ITestRunSink log, CancellationToken ct)
    {
        var tmp = TestProviderContext.NewTempDirectory();
        try
        {
            var json = Path.Combine(tmp, "list.json");
            var lines = await RunListingAsync(project, ["--gtest_list_tests", "--gtest_output=json:" + json], log, ct).ConfigureAwait(false);
            // GoogleTest 1.10+ writes the listing with each test's file and line; older ones only print the names
            if (File.Exists(json))
            {
                try { return GoogleTestFormat.ParseListingJson(await File.ReadAllTextAsync(json, ct).ConfigureAwait(false)); }
                catch (JsonException ex) { log.Output($"> could not read {project.Name}'s JSON listing ({ex.Message}); using its printed one."); }
            }
            return GoogleTestFormat.ParseListing(lines);
        }
        finally { TestProviderContext.DeleteQuietly(tmp); }
    }

    private async Task<IReadOnlyList<CppListedTest>> ListCatch2Async(TestProject project, ITestRunSink log, CancellationToken ct)
    {
        // "*,[.]": every test, the hidden ones ([.] tag) too, so they can be chosen and run by themselves
        var lines = await RunListingAsync(project, ["--list-tests", "--reporter", "xml", "*,[.]"], log, ct).ConfigureAwait(false);
        var text = string.Join('\n', lines);
        if (text.Contains("<MatchingTests", StringComparison.Ordinal))
        {
            try { return CatchXmlFormat.ParseListing(text); }
            catch (System.Xml.XmlException ex) { throw new TestDiscoveryException($"{project.Name}'s test listing is not valid XML: {ex.Message}"); }
        }
        // Catch2 v2 lists as text whatever the reporter: names only (their places come from the run's report)
        var names = await RunListingAsync(project, ["--list-test-names-only", "*,[.]"], log, ct).ConfigureAwait(false);
        return [.. names.Where(n => n.Length > 0 && !n.StartsWith(' ')).Distinct().Take(TestLimits.MaxTestsPerProject).Select(n => new CppListedTest(n, n, []))];
    }

    private async Task<IReadOnlyList<CppListedTest>> ListDoctestAsync(TestProject project, ITestRunSink log, CancellationToken ct)
    {
        var lines = await RunListingAsync(project, ["--list-test-cases", "--reporters=xml", "--no-skip=true"], log, ct).ConfigureAwait(false);
        try { return DoctestXmlFormat.ParseListing(string.Join('\n', lines)); }
        catch (System.Xml.XmlException ex) { throw new TestDiscoveryException($"{project.Name}'s test listing is not valid XML: {ex.Message}"); }
    }

    public IReadOnlyList<SourceTest> ScanSource(string filePath, string text) => CppTestScanner.Scan(text);

    public bool Matches(DiscoveredTest test, string filePath, SourceTest source) =>
        test.Location is { } loc && ITestProvider.PathsEqual(loc.FilePath, filePath) && CppTestScanner.NameMatches(RunnerName(test.Id), source.Name);

    // ------------------------------------------------------------------------------------------------ running

    /// <summary>What one run (or debug run) of an executable needs: its arguments, the results file, and how to read it.</summary>
    internal sealed record Invocation(CppFramework Framework, List<string> Arguments, string ResultsFile);

    /// <summary>
    /// The executable's arguments for <paramref name="names"/> (runner names; null: every test): GoogleTest's <c>--gtest_filter</c>, Catch2's
    /// escaped test spec, doctest's <c>--test-case</c>; none when more than <see cref="MaxFilteredTests"/> tests or a filter longer than
    /// <see cref="MaxFilterChars"/> are asked for (the whole executable runs). A single disabled / hidden / skipped test asked for by itself runs.
    /// </summary>
    internal static Invocation Arguments(CppFramework framework, IReadOnlyList<string>? names, string resultsDirectory, bool debug, bool breakOnFailure,
        IReadOnlySet<string>? skipped = null)
    {
        var filtered = names is { Count: > 0 and <= MaxFilteredTests } ? names : null;
        var one = filtered is { Count: 1 } && skipped?.Contains(filtered[0]) == true;
        switch (framework)
        {
            case CppFramework.GoogleTest:
            {
                var file = Path.Combine(resultsDirectory, "results.json");
                var args = new List<string> { "--gtest_color=no", "--gtest_output=json:" + file };
                if (filtered is not null && string.Join(':', filtered) is var filter && filter.Length <= MaxFilterChars) args.Add("--gtest_filter=" + filter);
                if (one) args.Add("--gtest_also_run_disabled_tests");
                if (debug && breakOnFailure) args.Add("--gtest_break_on_failure");
                return new Invocation(framework, args, file);
            }
            case CppFramework.Catch2:
            {
                var file = Path.Combine(resultsDirectory, "results.xml");
                var args = new List<string> { "--reporter", "xml", "--out", file, "--durations", "yes" };
                if (filtered is not null && CatchXmlFormat.Filter(filtered) is var spec && spec.Length <= MaxFilterChars) args.Add(spec);
                if (debug && breakOnFailure) args.Add("--break");
                return new Invocation(framework, args, file);
            }
            default:
            {
                var file = Path.Combine(resultsDirectory, "results.xml");
                var args = new List<string> { "--reporters=xml", "--out=" + file, "--duration=true", "--no-version=true" };
                if (filtered is not null && DoctestXmlFormat.Filter(filtered) is var filter && filter.Length <= MaxFilterChars)
                {
                    args.Add("--test-case=" + filter);
                    args.Add("--case-sensitive=true");
                }
                if (one) args.Add("--no-skip=true");
                if (!debug || !breakOnFailure) args.Add("--no-breaks=true");
                return new Invocation(framework, args, file);
            }
        }
    }

    /// <summary>The runner names of the requested ids (null: all), as the framework's filter takes them, with the skipped ones among them.</summary>
    private (IReadOnlyList<string>? Names, IReadOnlySet<string> Skipped) FilterNames(TestProject project, IReadOnlyList<string>? ids)
    {
        if (ids is null) return (null, new HashSet<string>());
        _listings.TryGetValue(project.FilePath, out var listing);
        var names = new List<string>(ids.Count);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var runner = RunnerName(id);
            var name = listing?.FilterNames.GetValueOrDefault(runner) ?? runner;
            names.Add(name);
            if (listing?.Skipped.Contains(runner) == true) skipped.Add(name);
        }
        return (names, skipped);
    }

    public async Task<TestRunOutcome> RunAsync(TestRunRequest request, ITestRunSink sink, CancellationToken ct)
    {
        var project = request.Project;
        if (!File.Exists(project.FilePath)) return new TestRunOutcome(-1, $"{project.FilePath} does not exist: build it first.");
        var framework = _executables.Classify(project.FilePath);
        if (framework == CppFramework.None) return new TestRunOutcome(-1, $"{project.FilePath} is not a GoogleTest, Catch2 or doctest executable.");
        var (names, skipped) = FilterNames(project, request.TestIds);
        var tmp = TestProviderContext.NewTempDirectory();
        try
        {
            var invocation = Arguments(framework, names, tmp, debug: false, breakOnFailure: false, skipped);
            var spec = new ProcessSpec(project.FilePath, invocation.Arguments, WorkingDirectory(project));
            sink.Status($"Running {project.Name}…");
            sink.Output($"> {spec}");
            var reporter = new Reporter(project, sink, request.TestIds);
            GoogleTestFormat.ConsoleParser? console = framework == CppFramework.GoogleTest ? reporter.Console() : null;
            var exit = await context.Processes.RunAsync(spec, line => { sink.Output(line); console?.OnLine(line); }, sink.Output, ct).ConfigureAwait(false);
            if (exit is null) return TestRunOutcome.Cancelled;
            return reporter.Complete(invocation, exit.Value, console);
        }
        finally { TestProviderContext.DeleteQuietly(tmp); }
    }

    public async Task<TestRunOutcome> DebugAsync(TestRunRequest request, ITestRunSink sink, ITestDebugLauncher debugger, CancellationToken ct)
    {
        var project = request.Project;
        if (!File.Exists(project.FilePath)) return new TestRunOutcome(-1, $"{project.FilePath} does not exist: build it first.");
        var framework = _executables.Classify(project.FilePath);
        if (framework == CppFramework.None) return new TestRunOutcome(-1, $"{project.FilePath} is not a GoogleTest, Catch2 or doctest executable.");
        var (names, skipped) = FilterNames(project, request.TestIds);
        var tmp = TestProviderContext.NewTempDirectory();
        try
        {
            var breakOnFailure = !string.Equals(context.Setting(BreakOnFailureSetting), "false", StringComparison.OrdinalIgnoreCase)
                && rawSetting?.Invoke(BreakOnFailureSetting) is not { ValueKind: JsonValueKind.False };
            var invocation = Arguments(framework, names, tmp, debug: true, breakOnFailure, skipped);
            var type = DebugType(context.Setting(DebuggerSetting), context.Setting(CTestProvider.DebuggerSetting), OperatingSystem.IsMacOS());
            var config = Configuration(type, project, invocation.Arguments);
            sink.Status($"Debugging {project.Name}…");
            sink.Output($"> {type}: {new ProcessSpec(project.FilePath, invocation.Arguments, WorkingDirectory(project))}");
            var session = await debugger.StartAsync(config, ct).ConfigureAwait(false);
            if (session is null) return new TestRunOutcome(-1, $"The debugger ({type}) did not start: see the Debug Console.");
            var reporter = new Reporter(project, sink, request.TestIds);
            var console = framework == CppFramework.GoogleTest ? reporter.Console() : null;
            var exit = console is null
                ? await DebugSessions.WaitForEndAsync(session, ct).ConfigureAwait(false)
                : await DebugSessions.PumpOutputAsync(session, console.OnLine, ct).ConfigureAwait(false);
            return reporter.Complete(invocation, exit ?? 0, console);
        }
        finally { TestProviderContext.DeleteQuietly(tmp); }
    }

    /// <summary>The launch configuration: the executable with the filter, in its own directory; CodeLLDB is told the program is C++.</summary>
    internal static DebugConfiguration Configuration(string type, TestProject project, IReadOnlyList<string> args)
    {
        var name = $"Debug tests: {project.Name}";
        var props = new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement(type),
            ["request"] = JsonSerializer.SerializeToElement("launch"),
            ["name"] = JsonSerializer.SerializeToElement(name),
            ["program"] = JsonSerializer.SerializeToElement(project.FilePath),
            ["args"] = JsonSerializer.SerializeToElement(args),
            ["cwd"] = JsonSerializer.SerializeToElement(WorkingDirectory(project)),
        };
        if (type == "lldb") props["sourceLanguages"] = JsonSerializer.SerializeToElement(new[] { "cpp" });
        return new DebugConfiguration(type, "launch", name, props, new Uri(project.Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// The debug type for <see cref="DebuggerSetting"/>, or when it is unset for the CMake integration's <c>cscode.cmake.debugger</c>, through
    /// <see cref="CTestProvider.DebugType"/>: <c>codelldb</c>/<c>lldb</c> → CodeLLDB (<c>lldb</c>), <c>lldb-dap</c>, <c>gdb</c>; both unset:
    /// CodeLLDB on macOS (GDB cannot debug arm64 macOS programs), GDB elsewhere.
    /// </summary>
    internal static string DebugType(string? setting, string? cmakeSetting, bool macOS) =>
        CTestProvider.DebugType(string.IsNullOrWhiteSpace(setting) ? cmakeSetting : setting, macOS);

    /// <summary>Turns a framework's results into the sink's, with ids and places in the folder.</summary>
    private sealed class Reporter(TestProject project, ITestRunSink sink, IReadOnlyList<string>? requested)
    {
        private readonly string _prefix = Prefix(project) + "/";
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
        private readonly HashSet<string>? _requested = requested?.Select(RunnerName).ToHashSet(StringComparer.Ordinal);

        public GoogleTestFormat.ConsoleParser Console() => new(name => sink.Started(project, _prefix + name), Report);

        public void Report(CppTestResult r)
        {
            // doctest reports the tests its filter left out as skipped: they were not run, and keep their last result
            if (r.Outcome == TestOutcome.Skipped && _requested is not null && !_requested.Contains(r.Name)) return;
            lock (_reported) if (!_reported.Add(r.Name)) return;
            var failure = r.FailureFile is { Length: > 0 } f && r.Outcome == TestOutcome.Failed ? new TestLocation(Logical(f, project), Math.Max(0, r.FailureLine - 1)) : null;
            sink.Finished(project, new TestResult(_prefix + r.Name, r.Outcome, r.Duration, r.Message, null, r.Output, failure));
        }

        /// <summary>Reads the results file (what the console did not report), fails a test the executable crashed in, and says how the run ended.</summary>
        public TestRunOutcome Complete(Invocation invocation, int exit, GoogleTestFormat.ConsoleParser? console)
        {
            string? crashed = null;
            var read = false;
            if (File.Exists(invocation.ResultsFile))
            {
                try
                {
                    if (invocation.Framework == CppFramework.GoogleTest)
                    {
                        foreach (var r in GoogleTestFormat.ParseResults(File.ReadAllText(invocation.ResultsFile))) Report(r);
                    }
                    else
                    {
                        using var reader = new StreamReader(invocation.ResultsFile);
                        crashed = invocation.Framework == CppFramework.Catch2 ? CatchXmlFormat.ParseResults(reader, Report) : DoctestXmlFormat.ParseResults(reader, Report);
                    }
                    read = true;
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    sink.Output($"> could not read the results of {project.Name}: {ex.Message}");
                }
            }
            crashed ??= console?.Running;
            if (crashed is not null)
            {
                var (output, failures) = console?.Pending() ?? (null, null);
                var why = $"The test executable stopped while this test ran (exit code {exit}{Signal(exit)})." + (failures is null ? "" : "\n\n" + failures);
                Report(new CppTestResult(crashed, TestOutcome.Failed, null, why, output));
            }
            if (!read && console?.Finished.Count is null or 0 && crashed is null && exit != 0)
                return new TestRunOutcome(exit, $"{project.Name} exited with code {exit}{Signal(exit)} without reporting results: see the Tests output.");
            return new TestRunOutcome(exit);
        }

        private static string Signal(int exit) => exit switch
        {
            134 or -6 => ", SIGABRT",
            139 or -11 => ", SIGSEGV",
            138 or -10 => ", SIGBUS",
            136 or -8 => ", SIGFPE",
            133 or -5 => ", SIGTRAP",
            _ => "",
        };
    }
}
