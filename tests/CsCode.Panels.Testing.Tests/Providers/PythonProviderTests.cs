using System.Diagnostics;
using System.Text.Json;
using CsCode.Core.Debugging;
using CsCode.Testing.Engine;
using CsCode.Testing.Providers.Python;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Providers;

/// <summary>
/// Language stage 1: the pytest / unittest provider. The parsers and the scanner on their own, then the REAL pytest (the tool venv of
/// tools/fetch-debugpy.sh) and unittest on sample projects; the real-tool tests skip with the reason when the venv is missing.
/// </summary>
public class PythonProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- parsers ----

    [Theory]
    [InlineData("tests/test_m.py::TestM::test_add[1-2]", "tests.test_m.TestM", "test_add[1-2]")]
    [InlineData("test_x.py::test_y", "test_x", "test_y")]
    [InlineData("pkg/sub/test_z.py::test_p[a::b]", "pkg.sub.test_z", "test_p[a::b]")]
    public void Node_ids_map_to_pytest_s_junit_names(string nodeId, string className, string name) =>
        Assert.Equal((className, name), PytestJunit.Mangle(nodeId));

    [Fact]
    public void A_parametrized_node_id_splits_into_its_test_and_case()
    {
        Assert.Equal(("t.py::test_add", "1-2"), PytestJunit.Split("t.py::test_add[1-2]"));
        Assert.Equal(("t.py::C::test", (string?)null), PytestJunit.Split("t.py::C::test"));
        Assert.Equal(("t.py::test_p", "a::b]x"), PytestJunit.Split("t.py::test_p[a::b]x]"));
    }

    [Fact]
    public void The_junit_report_becomes_results_with_cases_failure_locations_and_skips()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?><testsuites><testsuite name="pytest">
            <testcase classname="tests.test_m" name="test_ok" time="0.010"><system-out>hello</system-out></testcase>
            <testcase classname="tests.test_m" name="test_add[2-3]" time="0.002"><failure message="assert 5 == 6">def test_add(a, b):
            &gt;       assert a + b == 6
            E       assert 5 == 6

            tests/test_m.py:9: AssertionError</failure></testcase>
            <testcase classname="tests.test_m.TestK" name="test_skip" time="0"><skipped type="pytest.skip" message="not now">tests/test_m.py:14: not now</skipped></testcase>
            <testcase classname="other" name="unknown" time="0"/>
            </testsuite></testsuites>
            """;
        var root = Path.Combine(Path.GetTempPath(), "proj");
        var results = PytestJunit.Parse(xml, ["tests/test_m.py::test_ok", "tests/test_m.py::test_add[2-3]", "tests/test_m.py::TestK::test_skip"], root);
        Assert.Equal(3, results.Count);
        var ok = results.Single(r => r.TestId == "tests/test_m.py::test_ok");
        Assert.Equal((TestOutcome.Passed, "hello"), (ok.Outcome, ok.Output));
        Assert.Equal(TimeSpan.FromMilliseconds(10), ok.Duration);
        var failed = results.Single(r => r.TestId == "tests/test_m.py::test_add");
        Assert.Equal((TestOutcome.Failed, "2-3", "assert 5 == 6"), (failed.Outcome, failed.CaseLabel, failed.Message));
        Assert.Equal(new TestLocation(Path.Combine(root, "tests", "test_m.py"), 8), failed.FailureLocation);
        Assert.Equal((TestOutcome.Skipped, "not now"), (results.Single(r => r.TestId.EndsWith("test_skip", StringComparison.Ordinal)).Outcome, results.Single(r => r.TestId.EndsWith("test_skip", StringComparison.Ordinal)).Message));
    }

    [Fact]
    public void A_traceback_s_last_frame_under_the_project_is_the_failure_location()
    {
        var trace = "Traceback (most recent call last):\n  File \"/p/tests/test_a.py\", line 5, in test_x\n    helper()\n  File \"/p/src/lib.py\", line 12, in helper\n    raise ValueError\n  File \"/usr/lib/python3/x.py\", line 1\n";
        Assert.Equal(new TestLocation("/p/src/lib.py", 11), StackTraces.FromPython(trace, "/p"));
        Assert.Null(StackTraces.FromPython("no frames here", "/p"));
    }

    [Fact]
    public void The_scanner_finds_test_functions_and_the_test_methods_of_test_classes()
    {
        var found = PythonTestScanner.Scan("""
            import unittest

            def test_top():
                def test_nested():
                    pass

            async def test_async():
                pass

            def helper_test():
                pass

            class TestThing:
                def test_one(self):
                    pass

                def helper(self):
                    pass

            class Calc(unittest.TestCase):
                def test_add(self):
                    pass

            class NotATest:
                def test_ignored(self):
                    pass
            """);
        Assert.Equal(["test_top", "test_async", "TestThing::test_one", "Calc::test_add"], found.Select(f => f.Name));
        Assert.Equal([2, 6, 13, 20], found.Select(f => f.Line));
    }

    [Fact]
    public void Unittest_arguments_follow_VS_Code_s_flags()
    {
        Assert.Equal(("src", "*_test.py", "."), PythonTestProvider.UnittestArgs(["-v", "-s", "src", "-p", "*_test.py", "-t", "."]));
        Assert.Equal((".", "test*.py", ""), PythonTestProvider.UnittestArgs(null));
    }

    // ---- the real pytest and unittest ----

    private static string ToolsDir
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "CsCode.slnx"))) return Path.Combine(d.FullName, ".scratch", "tools");
            return "";
        }
    }

    private static string VenvPython => Path.Combine(ToolsDir, "python", "venv", "bin", "python");

    private static void RequirePytest()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("written for unix");
        if (!File.Exists(VenvPython)) Assert.Skip("the tool venv is missing: run tools/fetch-debugpy.sh");
    }

    private static PythonTestProvider Provider(string python, Dictionary<string, string[]>? lists = null) =>
        new(new TestProviderContext(new ProcessRunner()) { PythonInterpreter = (_, _) => Task.FromResult<string?>(python), ListSetting = k => lists?.GetValueOrDefault(k) });

    private const string PytestSample = """
        import pytest


        def test_ok():
            print("hello from test_ok")
            assert 1 + 1 == 2


        @pytest.mark.parametrize("a,b,total", [(1, 2, 3), (2, 3, 6)])
        def test_add(a, b, total):
            assert a + b == total


        @pytest.mark.skip(reason="not now")
        def test_skipped():
            pass


        class TestThing:
            def test_method(self):
                assert "x".upper() == "X"
        """;

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task Pytest_discovers_runs_and_reports_cases_failures_with_locations_and_skips()
    {
        RequirePytest();
        using var dir = new TempDirectory("cscode-pytest-");
        dir.WriteFile("pyproject.toml", "[tool.pytest.ini_options]\ntestpaths = [\"tests\"]\n");
        var file = dir.WriteFile("tests/test_sample.py", PytestSample);
        var provider = Provider(VenvPython);

        var project = Assert.Single(await provider.DetectAsync(dir.Path, Ct));
        Assert.Equal("pytest", project.Framework);
        var sink = new RecordingSink();
        var tests = await provider.DiscoverAsync(project, sink, Ct);
        Assert.Equal(["tests/test_sample.py::test_ok", "tests/test_sample.py::test_add", "tests/test_sample.py::test_skipped", "tests/test_sample.py::TestThing::test_method"],
            tests.Select(t => t.Id));
        var add = tests.Single(t => t.Id.EndsWith("test_add", StringComparison.Ordinal));
        Assert.Equal(["1-2-3", "2-3-6"], add.Cases);
        Assert.Equal(8, add.Location!.Line); // 0-based; a decorated function starts at its first decorator (co_firstlineno), as pytest reports it
        Assert.Equal(TempDirectory.PhysicalPath(file), TempDirectory.PhysicalPath(add.Location.FilePath));
        Assert.Equal(["tests/test_sample.py", "TestThing"], tests[^1].Groups.Select(g => g.Label));
        Assert.True(provider.Matches(add, file, new SourceTest("test_add", 9, "test_add")));

        var run = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, null), run, Ct);
        Assert.Equal(1, outcome.ExitCode); // one case fails
        Assert.Equal(TestOutcome.Passed, run.Result("tests/test_sample.py::test_ok").Outcome);
        Assert.Contains("hello from test_ok", run.Result("tests/test_sample.py::test_ok").Output, StringComparison.Ordinal);
        Assert.Equal(TestOutcome.Passed, run.Result("tests/test_sample.py::test_add", "1-2-3").Outcome);
        var failed = run.Result("tests/test_sample.py::test_add", "2-3-6");
        Assert.Equal(TestOutcome.Failed, failed.Outcome);
        Assert.Contains("assert", failed.Message, StringComparison.Ordinal);
        Assert.Equal(10, failed.FailureLocation!.Line); // the assert line (0-based)
        Assert.Equal(TestOutcome.Skipped, run.Result("tests/test_sample.py::test_skipped").Outcome);
        Assert.Equal(TestOutcome.Passed, run.Result("tests/test_sample.py::TestThing::test_method").Outcome);
        Assert.Contains("tests/test_sample.py::TestThing::test_method", run.StartedIds);

        // one test: only it runs
        var single = new RecordingSink();
        Assert.Equal(0, (await provider.RunAsync(new TestRunRequest(project, ["tests/test_sample.py::TestThing::test_method"]), single, Ct)).ExitCode);
        Assert.Equal(["tests/test_sample.py::TestThing::test_method"], single.Results.Select(r => r.TestId));
    }

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task Debugging_runs_the_same_pytest_command_as_a_debugpy_launch_and_reads_the_results()
    {
        RequirePytest();
        using var dir = new TempDirectory("cscode-pytest-debug-");
        dir.WriteFile("test_one.py", PytestSample);
        var provider = Provider(VenvPython, new() { [PythonTestProvider.PytestArgsSetting] = ["-x"] });
        var project = Assert.Single(await provider.DetectAsync(dir.Path, Ct));
        var launcher = new RunningLauncher();
        var sink = new RecordingSink();
        await provider.DebugAsync(new TestRunRequest(project, ["test_one.py::TestThing::test_method"]), sink, launcher, Ct);
        var config = Assert.Single(launcher.Configurations);
        Assert.Equal(("debugpy", "launch", "pytest"), (config.Type, config.Request, config.GetString("module")));
        Assert.Equal(VenvPython, config.GetString("python"));
        Assert.Equal(["debug-test"], config.Properties["purpose"].EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("-x", config.Properties["args"].EnumerateArray().Select(e => e.GetString()));
        Assert.True(sink.Results.Any(r => r.TestId == "test_one.py::TestThing::test_method"), "no result; output:\n" + string.Join("\n", sink.Lines));
        Assert.Equal(TestOutcome.Passed, sink.Result("test_one.py::TestThing::test_method").Outcome);
    }

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task Unittest_is_used_without_pytest_and_discovers_and_runs()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("written for unix"); return; }
        if (await PythonWithoutPytestAsync() is not { } python) { Assert.Skip("no interpreter without pytest on this machine"); return; }
        using var dir = new TempDirectory("cscode-unittest-");
        dir.WriteFile("tests/__init__.py", "");
        var file = dir.WriteFile("tests/test_calc.py", """
            import unittest


            class CalcTests(unittest.TestCase):
                def test_add(self):
                    self.assertEqual(1 + 1, 2)

                def test_broken(self):
                    self.assertEqual(1 + 1, 3)

                @unittest.skip("later")
                def test_later(self):
                    pass
            """);
        var provider = Provider(python);
        var project = Assert.Single(await provider.DetectAsync(dir.Path, Ct));
        Assert.Equal("unittest", project.Framework);
        var tests = await provider.DiscoverAsync(project, new RecordingSink(), Ct);
        Assert.Equal(["tests.test_calc.CalcTests.test_add", "tests.test_calc.CalcTests.test_broken", "tests.test_calc.CalcTests.test_later"], tests.Select(t => t.Id).Order());
        var add = tests.Single(t => t.Id.EndsWith("test_add", StringComparison.Ordinal));
        Assert.Equal(4, add.Location!.Line);
        Assert.True(provider.Matches(add, file, new SourceTest("CalcTests::test_add", 4, "test_add")));

        var sink = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, null), sink, Ct);
        Assert.True(sink.Results.Count == 3, $"exit {outcome.ExitCode} {outcome.Error}; results {sink.Results.Count}; output:\n" + string.Join("\n", sink.Lines));
        Assert.Equal(TestOutcome.Passed, sink.Result("tests.test_calc.CalcTests.test_add").Outcome);
        var broken = sink.Result("tests.test_calc.CalcTests.test_broken");
        Assert.Equal(TestOutcome.Failed, broken.Outcome);
        Assert.Contains("AssertionError", broken.Message, StringComparison.Ordinal);
        Assert.Equal(8, broken.FailureLocation!.Line);
        Assert.Equal(TestOutcome.Skipped, sink.Result("tests.test_calc.CalcTests.test_later").Outcome);
    }

    private static async Task<string?> PythonWithoutPytestAsync()
    {
        foreach (var p in new[] { "/opt/homebrew/bin/python3", "/usr/local/bin/python3", "/usr/bin/python3" })
        {
            if (!File.Exists(p) || (OperatingSystem.IsMacOS() && p == "/usr/bin/python3" && !File.Exists("/Library/Developer/CommandLineTools/usr/bin/python3"))) continue;
            using var probe = Process.Start(new ProcessStartInfo(p, ["-I", "-c", "import pytest"]) { RedirectStandardError = true, RedirectStandardOutput = true })!;
            await probe.WaitForExitAsync(Ct);
            if (probe.ExitCode != 0) return p;
        }
        return null;
    }

    /// <summary>Stands in for debugpy: runs the configuration's interpreter and module with its arguments, environment and directory for real.</summary>
    private sealed class RunningLauncher : ITestDebugLauncher
    {
        public List<DebugConfiguration> Configurations { get; } = [];

        public ValueTask<IDebugSession?> StartAsync(DebugConfiguration configuration, CancellationToken ct)
        {
            Configurations.Add(configuration);
            var psi = new ProcessStartInfo(configuration.GetString("python")!) { WorkingDirectory = configuration.GetString("cwd"), RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(configuration.GetString("module")!);
            foreach (var a in configuration.Properties["args"].EnumerateArray()) psi.ArgumentList.Add(a.GetString()!);
            foreach (var p in configuration.Properties["env"].EnumerateObject()) psi.Environment[p.Name] = p.Value.GetString();
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            var session = new ScriptedSession(configuration, [output], process.ExitCode);
            session.Play();
            return ValueTask.FromResult<IDebugSession?>(session);
        }
    }
}
