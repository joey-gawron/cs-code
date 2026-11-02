using CsCode.Build.CMake;
using CsCode.Core.Build;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Testing.Engine;
using CsCode.Testing.Providers.CTest;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Providers;

/// <summary>The CTest provider: ctest's JSON listing, its console and JUnit results, the debug configuration, and a real ctest run.</summary>
public sealed class CTestProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Listing = """
        {
          "backtraceGraph": { "commands": ["add_test"], "files": ["/src/CMakeLists.txt", "/src/tests/CMakeLists.txt"],
            "nodes": [ { "file": 0 }, { "command": 0, "file": 0, "line": 10, "parent": 0 }, { "file": 1 }, { "command": 0, "file": 1, "line": 3, "parent": 2 } ] },
          "kind": "ctestInfo",
          "tests": [
            { "backtrace": 1, "command": ["/build/math_tests", "adds"], "name": "adds",
              "properties": [ { "name": "LABELS", "value": ["fast", "math"] }, { "name": "ENVIRONMENT", "value": ["A=1", "B=x=y"] } ] },
            { "backtrace": 3, "command": ["/build/tests/other"], "name": "nested", "properties": [ { "name": "DISABLED", "value": true } ] },
            { "backtrace": 1, "command": ["/build/math_tests", "cwd"], "name": "in.dir",
              "properties": [ { "name": "WORKING_DIRECTORY", "value": "/src/tests" } ] }
          ],
          "version": { "major": 1, "minor": 0 }
        }
        """;

    private static CMakeProject Project(params CMakeTarget[] targets) =>
        new("/src", "/build", CMakeProjectState.Configured, null, null, "Debug", "Ninja", targets, "/usr/bin/ctest", 1);

    [Fact]
    public void Listing_gives_names_commands_labels_locations_and_properties()
    {
        var tests = CTestJson.Parse(Listing);
        Assert.Equal(["adds", "nested", "in.dir"], tests.Select(t => t.Name));
        var adds = tests[0];
        Assert.Equal(["/build/math_tests", "adds"], adds.Command);
        Assert.Equal(["fast", "math"], adds.Labels);
        Assert.Equal(["A=1", "B=x=y"], adds.Environment);
        Assert.Equal(("/src/CMakeLists.txt", 10), (adds.File, adds.Line));
        Assert.True(tests[1].Disabled);
        Assert.Equal("/src/tests", tests[2].WorkingDirectory);

        var target = new CMakeTarget("math_tests", CMakeTargetType.Executable, ["/build/math_tests"], "/src", [], [], ["/src/tests/math_tests.c"]);
        var discovered = CTestProvider.Discovered(adds, Project(target));
        Assert.Equal("adds", discovered.Id);
        Assert.Equal("adds [fast, math]", discovered.Label);
        Assert.Equal("math_tests", Assert.Single(discovered.Groups).Label);
        Assert.Equal(new TestLocation("/src/CMakeLists.txt", 9), discovered.Location);
        Assert.NotNull(CTestProvider.Discovered(tests[1], Project()).SkipReason);
    }

    [Fact]
    public void Debug_configuration_runs_the_command_where_ctest_would()
    {
        var tests = CTestJson.Parse(Listing);
        var adds = CTestProvider.DebugConfiguration(tests[0], Project(), null, macOS: true);
        Assert.Equal("lldb", adds.Type);
        Assert.Equal("/build/math_tests", adds.GetString("program"));
        Assert.Equal(["adds"], adds.Properties["args"].EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("/build", adds.GetString("cwd")); // added in the top CMakeLists.txt: the top build directory
        Assert.Equal("x=y", adds.Properties["env"].GetProperty("B").GetString());
        Assert.Equal("c", adds.Properties["sourceLanguages"][0].GetString());
        Assert.Equal("/build/tests", CTestProvider.DebugConfiguration(tests[1], Project(), "lldb-dap", true).GetString("cwd")); // tests/CMakeLists.txt → build/tests
        Assert.Equal("lldb-dap", CTestProvider.DebugConfiguration(tests[1], Project(), "lldb-dap", true).Type);
        Assert.Equal("/src/tests", CTestProvider.DebugConfiguration(tests[2], Project(), null, false).GetString("cwd"));
        Assert.Equal("gdb", CTestProvider.DebugConfiguration(tests[2], Project(), null, false).Type);
    }

    [Fact]
    public void Filter_is_anchored_and_escapes_regex_characters()
    {
        Assert.Equal(@"^(adds|in\.dir|a\(b\)\+)$", CTestProvider.Filter(["adds", "in.dir", "a(b)+"]));
        Assert.Equal(new Version(3, 31), CTestProvider.ParseVersion("ctest version 3.31.12"));
        Assert.Null(CTestProvider.ParseVersion(null));
    }

    [Fact]
    public void Results_come_live_from_the_console_and_details_from_junit()
    {
        var project = new TestProject("ctest", "csample", "/src/CMakeLists.txt", "/src");
        var sink = new RecordingSink();
        var results = new CTestResults(project, sink);
        foreach (var line in new[]
        {
            "Test project /build",
            "    Start 1: adds",
            "1/3 Test #1: adds .............................   Passed    0.01 sec",
            "    Start 2: fails",
            "2/3 Test #2: fails ............................***Failed    0.02 sec",
            "expected 5, got 4",
            "    Start 3: never",
            "3/3 Test #3: never ............................***Not Run (Disabled)   0.00 sec",
            "",
            "67% tests passed, 1 tests failed out of 3",
        }) results.OnLine(line);
        Assert.Equal(TestOutcome.Passed, sink.Result("adds").Outcome); // reported as soon as its line came
        Assert.Single(sink.Results);

        using var dir = new TempDirectory();
        var junit = dir.WriteFile("ctest.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <testsuite name="host" tests="3" failures="1" disabled="1" skipped="0" time="0.03">
              <testcase name="adds" classname="adds" time="0.01" status="run"><system-out>ok</system-out></testcase>
              <testcase name="fails" classname="fails" time="0.02" status="fail"><failure message="Failed"/><system-out>expected 5, got 4
            </system-out></testcase>
              <testcase name="never" classname="never" time="0" status="disabled"><skipped message="Disabled"/></testcase>
            </testsuite>
            """);
        results.Complete(junit);

        Assert.Equal(3, sink.Results.Count); // each test once
        var fails = sink.Result("fails");
        Assert.Equal((TestOutcome.Failed, "Failed", "expected 5, got 4"), (fails.Outcome, fails.Message, fails.Output));
        Assert.Equal(TestOutcome.Skipped, sink.Result("never").Outcome);
    }

    [Fact]
    public void Without_junit_the_console_results_are_kept()
    {
        var sink = new RecordingSink();
        var results = new CTestResults(new TestProject("ctest", "p", "/p/CMakeLists.txt", "/p"), sink);
        results.OnLine("1/1 Test #1: fails ............................***Failed    0.02 sec");
        results.OnLine("expected 5, got 4");
        results.OnLine("0% tests passed, 1 tests failed out of 1");
        results.Complete(null);
        var fails = sink.Result("fails");
        Assert.Equal((TestOutcome.Failed, "expected 5, got 4"), (fails.Outcome, fails.Output));
    }

    [Fact]
    public void Scans_add_test_calls_in_cmake_files_only()
    {
        var provider = new CTestProvider(new TestProviderContext(new ScriptedProcesses()), null);
        var found = provider.ScanSource("/src/CMakeLists.txt", "project(x)\nadd_test(NAME adds COMMAND t adds)\n  add_test(old_style t)\nadd_test(NAME ${generated} COMMAND t)\n");
        Assert.Equal([("adds", 1), ("old_style", 2)], found.Select(t => (t.Name, t.Line)));
        Assert.Empty(provider.ScanSource("/src/main.c", "add_test(NAME x COMMAND y)"));
    }

    // ------------------------------------------------------------------------------------------------ real ctest

    internal sealed class Folder(string path) : IWorkspaceService
    {
        public IReadOnlyList<Uri> Folders { get; } = [new Uri(path.TrimEnd('/') + "/")];
        public string? Name => "test";
        public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => EventSource.Never<WorkspaceFoldersChangedEventArgs>();
        public IFileSystem FileSystem => throw new NotSupportedException();
        public IFileWatcher Watcher => throw new NotSupportedException();
        public IReadOnlyList<Uri> RecentFolders => [];
        public ValueTask OpenFolderAsync(Uri folder, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask CloseFolderAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, CancellationToken ct = default) => AsyncEnumerable.Empty<Uri>();
    }

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task Real_ctest_lists_runs_and_reports_the_sample_tests()
    {
        if (CMakeCSample.SkipReason() is { } why) Assert.Skip(why);
        using var dir = new TempDirectory();
        CMakeCSample.Write(dir);
        var output = new InMemoryOutputService();
        using var cmake = new CMakeService(new Folder(dir.Path), new TestConfigurationService(), null, null, output);
        var provider = new CTestProvider(new TestProviderContext(new ProcessRunner()), cmake);

        var project = Assert.Single(await provider.DetectAsync(dir.Path, Ct));
        var log = new RecordingSink();
        var tests = await provider.DiscoverAsync(project, log, Ct).WaitAsync(TimeSpan.FromMinutes(2), Ct);

        Assert.Equal(["adds", "subtracts", "fails", "in_dir"], tests.Select(t => t.Id));
        Assert.Equal("subtracts [fast, math]", tests[1].Label);
        Assert.Equal(Path.Combine(dir.Path, "CMakeLists.txt"), tests[0].Location?.FilePath);
        Assert.Equal(9, tests[0].Location?.Line); // add_test(NAME adds …) is line 10

        var sink = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, null), sink, Ct).WaitAsync(TimeSpan.FromMinutes(2), Ct);
        Assert.True(outcome.Error is null, outcome.Error + "\n" + string.Join('\n', sink.Lines) + output.Text(CMakeService.OutputChannelName));
        Assert.Equal(8, outcome.ExitCode); // ctest: a test failed
        Assert.Equal(TestOutcome.Passed, sink.Result("adds").Outcome);
        Assert.Equal(TestOutcome.Passed, sink.Result("in_dir").Outcome); // ran in its WORKING_DIRECTORY
        var fails = sink.Result("fails");
        Assert.Equal(TestOutcome.Failed, fails.Outcome);
        Assert.Contains("expected 5, got 4", fails.Output ?? "", StringComparison.Ordinal);
        Assert.Contains(sink.Lines, l => l.Contains("--output-junit", StringComparison.Ordinal));

        var one = new RecordingSink();
        var single = await provider.RunAsync(new TestRunRequest(project, ["subtracts"]), one, Ct).WaitAsync(TimeSpan.FromMinutes(2), Ct);
        Assert.Equal(0, single.ExitCode);
        Assert.Equal(["subtracts"], one.Results.Select(r => r.TestId));
    }
}
