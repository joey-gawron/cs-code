using System.Text;
using System.Text.Json;
using CsCode.Testing.Engine;
using CsCode.Testing.Providers.Cpp;
using CsCode.Testing.Providers.Rust;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Providers;

/// <summary>
/// The C++ provider over stand-in executables (files with an executable's header and a framework's signature, never run: a scripted process
/// runner answers for them, writing the results files the real ones would).
/// </summary>
public sealed class CppProviderTests
{
    /// <summary>Writes a stand-in for a native executable: an ELF header (Mach-O on macOS, PE on Windows), the signature, executable by its owner.</summary>
    internal static string FakeExecutable(TempDirectory dir, string relative, string signature)
    {
        var path = dir.Combine(relative.Split('/')) + (OperatingSystem.IsWindows() ? ".exe" : "");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[256];
        if (OperatingSystem.IsWindows()) { bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; }
        else if (OperatingSystem.IsMacOS()) { bytes[0] = 0xCF; bytes[1] = 0xFA; bytes[2] = 0xED; bytes[3] = 0xFE; bytes[12] = 2; }
        else { bytes[0] = 0x7F; bytes[1] = (byte)'E'; bytes[2] = (byte)'L'; bytes[3] = (byte)'F'; bytes[16] = 3; }
        Encoding.ASCII.GetBytes("....." + signature + ".....").CopyTo(bytes, 100);
        File.WriteAllBytes(path, bytes);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static string Arg(ProcessSpec spec, string prefix) => spec.Arguments.First(a => a.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];

    private static string After(ProcessSpec spec, string option) => spec.Arguments[spec.Arguments.ToList().IndexOf(option) + 1];

    [Fact]
    public void Finds_test_executables_by_the_default_globs_and_their_framework_signature()
    {
        using var dir = new TempDirectory("cscode-cpp-find-");
        var gtest = FakeExecutable(dir, "build/math_tests", "gtest_list_tests");
        var catch2 = FakeExecutable(dir, "build/sub/catch_tests", "Catch2TestRun");
        var doctest = FakeExecutable(dir, "out/doc_test", "list-test-cases");
        FakeExecutable(dir, "build/app_tool", "gtest_list_tests");                  // not test-named
        FakeExecutable(dir, "build/plain_test", "nothing to see");                  // no framework
        FakeExecutable(dir, "build/CMakeFiles/x_test", "gtest_list_tests");         // CMake's scratch
        FakeExecutable(dir, "build/_deps/googletest-build/gtest_test", "gtest_list_tests"); // a dependency's
        FakeExecutable(dir, "src/unit_test", "gtest_list_tests");                   // not under a build directory
        dir.WriteFile("build/notes_test.txt", "gtest_list_tests");
        if (!OperatingSystem.IsWindows())
        {
            var lib = FakeExecutable(dir, "build/libgtest_test.dylib", "gtest_list_tests");
            File.SetUnixFileMode(lib, UnixFileMode.UserRead); // not executable
        }

        var found = new CppExecutables().Find(dir.Path, CppExecutables.DefaultPatterns, TestContext.Current.CancellationToken);
        Assert.Equal([(gtest, CppFramework.GoogleTest), (catch2, CppFramework.Catch2), (doctest, CppFramework.Doctest)],
            found.Select(f => (f.Path, f.Framework)).OrderBy(f => f.Path.Contains("/out/", StringComparison.Ordinal)).ThenBy(f => f.Path, StringComparer.Ordinal));
    }

    [Fact]
    public void Patterns_come_from_the_setting_then_testmate_then_the_default_and_may_be_absolute()
    {
        using var dir = new TempDirectory("cscode-cpp-globs-");
        var exe = FakeExecutable(dir, "bin/x/unit", "gtest_list_tests");
        var settings = new Dictionary<string, JsonElement>();
        var provider = new CppTestProvider(new TestProviderContext(new ScriptedProcesses()), k => settings.TryGetValue(k, out var v) ? v : null);
        Assert.Equal(CppExecutables.DefaultPatterns, provider.Patterns());
        settings[CppTestProvider.ExecutablesSetting] = JsonSerializer.SerializeToElement(CppExecutables.DefaultPatterns); // the registered default
        settings[CppTestProvider.TestMateExecutablesSetting] = JsonSerializer.SerializeToElement("bin/**/unit");
        Assert.Equal(["bin/**/unit"], provider.Patterns());
        settings[CppTestProvider.ExecutablesSetting] = JsonSerializer.SerializeToElement(new[] { "${workspaceFolder}/bin/*/unit" });
        Assert.Equal(["${workspaceFolder}/bin/*/unit"], provider.Patterns());

        var ct = TestContext.Current.CancellationToken;
        Assert.Equal([exe], new CppExecutables().Find(dir.Path, provider.Patterns(), ct).Select(f => f.Path));
        Assert.Equal([exe], new CppExecutables().Find("/somewhere/else", [Path.Combine(dir.Path, "bin", "*", "unit")], ct).Select(f => f.Path));
        Assert.Empty(new CppExecutables().Find(dir.Path, ["unit"], ct)); // anchored at the folder: "unit" is not "bin/x/unit"
    }

    [Fact]
    public async Task GoogleTest_executable_is_listed_once_per_build_and_run_with_live_results()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDirectory("cscode-cpp-gtest-");
        var exe = FakeExecutable(dir, "build/gtest_tests", "gtest_list_tests");
        var source = dir.Combine("gtest_tests.cpp");
        var processes = new ScriptedProcesses
        {
            Answer = spec =>
            {
                if (spec.Arguments.Contains("--gtest_list_tests"))
                {
                    File.WriteAllText(Arg(spec, "--gtest_output=json:"), Fixture.Read("cpp/gtest-list.json").Replace("\\/work\\/cppsample\\/gtest_tests.cpp", source.Replace("/", "\\/", StringComparison.Ordinal), StringComparison.Ordinal));
                    return (0, Fixture.Read("cpp/gtest-list.txt").Split('\n'), []);
                }
                File.WriteAllText(Arg(spec, "--gtest_output=json:"), Fixture.Read("cpp/gtest-results.json").Replace("\\/work\\/cppsample\\/gtest_tests.cpp", source.Replace("/", "\\/", StringComparison.Ordinal), StringComparison.Ordinal));
                return (1, Fixture.Read("cpp/gtest-run.txt").Replace("/work/cppsample/gtest_tests.cpp", source, StringComparison.Ordinal).Split('\n'), []);
            },
        };
        var provider = new CppTestProvider(new TestProviderContext(processes));
        var project = Assert.Single(await provider.DetectAsync(dir.Path, ct));
        Assert.Equal(("build/gtest_tests", exe, dir.Path, "GoogleTest"), (project.Name, project.FilePath, project.Directory, project.Framework));

        var tests = await provider.DiscoverAsync(project, new RecordingSink(), ct);
        Assert.Equal(11, tests.Count);
        var adds = tests.Single(t => t.Id == "gtest_tests/MathTest.Adds");
        Assert.Equal((new TestLocation(source, 5), "Adds"), (adds.Location, adds.Label));
        Assert.Equal(Path.GetDirectoryName(exe), Assert.Single(processes.Started).WorkingDirectory);

        // listed again only after a build changed the executable
        await provider.DiscoverAsync(project, new RecordingSink(), ct);
        Assert.Single(processes.Started);
        File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddMinutes(1));
        await provider.DiscoverAsync(project, new RecordingSink(), ct);
        Assert.Equal(2, processes.Started.Count);

        var sink = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, ["gtest_tests/MathTest.Fails", "gtest_tests/Numbers/Param.IsPositive/2"]), sink, ct);
        Assert.Null(outcome.Error);
        var run = processes.Started.Last();
        Assert.Contains("--gtest_filter=MathTest.Fails:Numbers/Param.IsPositive/2", run.Arguments);
        Assert.Contains("--gtest_color=no", run.Arguments);
        Assert.Contains("gtest_tests/MathTest.Adds", sink.StartedIds); // the console's [ RUN ] lines, as they come
        var failed = sink.Result("gtest_tests/MathTest.Fails");
        Assert.Equal((TestOutcome.Failed, new TestLocation(source, 8), "from the test"), (failed.Outcome, failed.FailureLocation, failed.Output));
        // what the executable reported beyond the request is kept, except "skipped" for tests it did not run (doctest reports its filtered ones so)
        Assert.Equal(TestOutcome.Passed, sink.Result("gtest_tests/MathTest.Adds").Outcome);
        Assert.DoesNotContain(sink.Results, r => r.TestId is "gtest_tests/MathTest.DISABLED_Later" or "gtest_tests/MathTest.Skips");
        Assert.Equal(9, sink.Results.Count); // the JSON settles nothing twice
        Assert.Contains(sink.Lines, l => l.StartsWith("> " + exe, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_crash_fails_the_test_that_was_running_with_the_exit_code()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDirectory("cscode-cpp-crash-");
        FakeExecutable(dir, "build/crash_test", "gtest_list_tests");
        var processes = new ScriptedProcesses { Answer = _ => (139, ["[ RUN      ] A.Ok", "[       OK ] A.Ok (0 ms)", "[ RUN      ] A.Crashes", "about to crash"], []) };
        var provider = new CppTestProvider(new TestProviderContext(processes));
        var project = Assert.Single(await provider.DetectAsync(dir.Path, ct));
        var sink = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, null), sink, ct);
        Assert.Equal((139, null), (outcome.ExitCode, outcome.Error));
        Assert.Equal(TestOutcome.Passed, sink.Result("crash_test/A.Ok").Outcome);
        var crashed = sink.Result("crash_test/A.Crashes");
        Assert.Equal((TestOutcome.Failed, "about to crash"), (crashed.Outcome, crashed.Output));
        Assert.Contains("exit code 139, SIGSEGV", crashed.Message);
    }

    [Fact]
    public async Task A_listing_that_hangs_is_stopped_and_reported_on_the_project()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDirectory("cscode-cpp-hang-");
        FakeExecutable(dir, "build/hang_test", "Catch2TestRun");
        var processes = new ScriptedProcesses { Gate = new TaskCompletionSource() };
        var provider = new CppTestProvider(new TestProviderContext(processes)) { ListTimeout = TimeSpan.FromMilliseconds(200) };
        var project = Assert.Single(await provider.DetectAsync(dir.Path, ct));
        var ex = await Assert.ThrowsAsync<TestDiscoveryException>(() => provider.DiscoverAsync(project, new RecordingSink(), ct));
        Assert.Contains("did not list its tests within", ex.Message);
    }

    [Fact]
    public async Task Catch2_and_doctest_runs_pass_their_filters_and_read_their_xml_reports()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDirectory("cscode-cpp-xml-");
        FakeExecutable(dir, "build/catch_tests", "Catch2TestRun");
        FakeExecutable(dir, "build/doctest_tests", "list-test-cases");
        var processes = new ScriptedProcesses
        {
            Answer = spec =>
            {
                var catch2 = spec.FileName.Contains("catch_tests", StringComparison.Ordinal);
                if (spec.Arguments.Contains("--list-tests")) return (0, Fixture.Read("cpp/catch2-list.xml").Split('\n'), []);
                if (spec.Arguments.Contains("--list-test-cases")) return (0, Fixture.Read("cpp/doctest-list.xml").Split('\n'), []);
                if (catch2) File.WriteAllText(After(spec, "--out"), Fixture.Read("cpp/catch2-results.xml"));
                else File.WriteAllText(Arg(spec, "--out="), Fixture.Read("cpp/doctest-results.xml"));
                return (catch2 ? 42 : 1, catch2 ? Array.Empty<string>() : new[] { "from doctest" }, Array.Empty<string>());
            },
        };
        var provider = new CppTestProvider(new TestProviderContext(processes));
        var projects = await provider.DetectAsync(dir.Path, ct);
        var catchProject = projects.Single(p => p.Framework == "Catch2");
        var docProject = projects.Single(p => p.Framework == "doctest");

        var catchTests = await provider.DiscoverAsync(catchProject, new RecordingSink(), ct);
        Assert.Equal(["--list-tests", "--reporter", "xml", "*,[.]"], processes.Started.Last().Arguments);
        var fails = catchTests.Single(t => t.Id == "catch_tests/fails");
        // grouped by source file (outside the folder here, so by its name)
        Assert.Equal(new TestGroup("catch_tests.cpp", TestNodeKind.File, new TestLocation("/work/cppsample/catch_tests.cpp", 0)), Assert.Single(fails.Groups));
        var sink = new RecordingSink();
        await provider.RunAsync(new TestRunRequest(catchProject, ["catch_tests/fails", "catch_tests/Name, with comma [and brackets]"]), sink, ct);
        Assert.Equal("fails,Name\\, with comma \\[and brackets\\]", processes.Started.Last().Arguments[^1]);
        Assert.Equal(TestOutcome.Failed, sink.Result("catch_tests/fails").Outcome);
        Assert.Equal(new TestLocation("/work/cppsample/catch_tests.cpp", 10), sink.Result("catch_tests/fails").FailureLocation);

        var docTests = await provider.DiscoverAsync(docProject, new RecordingSink(), ct);
        Assert.Equal(["--list-test-cases", "--reporters=xml", "--no-skip=true"], processes.Started.Last().Arguments);
        Assert.Equal([new TestGroup("suite", TestNodeKind.Suite)], docTests.Single(t => t.Id == "doctest_tests/suite::in suite").Groups);
        sink = new RecordingSink();
        await provider.RunAsync(new TestRunRequest(docProject, ["doctest_tests/suite::in suite"]), sink, ct);
        Assert.Contains("--test-case=in suite", processes.Started.Last().Arguments);
        Assert.Contains("--no-breaks=true", processes.Started.Last().Arguments);
        Assert.Equal(TestOutcome.Passed, sink.Result("doctest_tests/suite::in suite").Outcome);
        Assert.Contains("from doctest", sink.Lines);

        // a skipped doctest test asked for by itself runs
        await provider.RunAsync(new TestRunRequest(docProject, ["doctest_tests/skipped"]), new RecordingSink(), ct);
        Assert.Contains("--no-skip=true", processes.Started.Last().Arguments);
    }

    [Fact]
    public void Arguments_filter_each_framework_and_drop_a_filter_too_long_for_a_command_line()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "r");
        var gtest = CppTestProvider.Arguments(CppFramework.GoogleTest, ["A.B", "C/0.D"], tmp, debug: false, breakOnFailure: true);
        Assert.Equal(["--gtest_color=no", "--gtest_output=json:" + Path.Combine(tmp, "results.json"), "--gtest_filter=A.B:C/0.D"], gtest.Arguments);
        var disabled = CppTestProvider.Arguments(CppFramework.GoogleTest, ["A.DISABLED_X"], tmp, debug: true, breakOnFailure: true, new HashSet<string> { "A.DISABLED_X" });
        Assert.Contains("--gtest_also_run_disabled_tests", disabled.Arguments);
        Assert.Contains("--gtest_break_on_failure", disabled.Arguments);
        Assert.DoesNotContain("--break", CppTestProvider.Arguments(CppFramework.Catch2, null, tmp, debug: true, breakOnFailure: false).Arguments);
        Assert.Contains("--break", CppTestProvider.Arguments(CppFramework.Catch2, null, tmp, debug: true, breakOnFailure: true).Arguments);
        Assert.DoesNotContain("--no-breaks=true", CppTestProvider.Arguments(CppFramework.Doctest, null, tmp, debug: true, breakOnFailure: true).Arguments);

        var many = Enumerable.Range(0, CppTestProvider.MaxFilteredTests + 1).Select(i => $"S.T{i}").ToList();
        Assert.DoesNotContain(CppTestProvider.Arguments(CppFramework.GoogleTest, many, tmp, false, false).Arguments, a => a.StartsWith("--gtest_filter", StringComparison.Ordinal));
        var long_ = Enumerable.Range(0, 100).Select(i => new string('x', 300) + i).ToList();
        Assert.Equal(6, CppTestProvider.Arguments(CppFramework.Catch2, long_, tmp, false, false).Arguments.Count); // no spec: everything runs
        Assert.Equal("a\\,b,c", DoctestXmlFormat.Filter(["a,b", "c"]));
    }

    [Fact]
    public async Task Debugging_launches_the_executable_with_the_filter_under_the_chosen_debugger_and_reads_the_report()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDirectory("cscode-cpp-debug-");
        var exe = FakeExecutable(dir, "build/catch_tests", "Catch2TestRun");
        var processes = new ScriptedProcesses { Answer = _ => (0, Fixture.Read("cpp/catch2-list.xml").Split('\n'), []) };
        var settings = new Dictionary<string, string>();
        var provider = new CppTestProvider(new TestProviderContext(processes, k => settings.GetValueOrDefault(k)));
        var project = Assert.Single(await provider.DetectAsync(dir.Path, ct));
        await provider.DiscoverAsync(project, new RecordingSink(), ct);

        var launcher = new ReportWritingLauncher(config =>
        {
            var args = config.Properties["args"].EnumerateArray().Select(a => a.GetString()!).ToList();
            File.WriteAllText(args[args.IndexOf("--out") + 1], Fixture.Read("cpp/catch2-results.xml"));
        });
        var sink = new RecordingSink();
        var outcome = await provider.DebugAsync(new TestRunRequest(project, ["catch_tests/fails"]), sink, launcher, ct);
        Assert.Null(outcome.Error);
        var config = Assert.Single(launcher.Started);
        Assert.Equal((OperatingSystem.IsMacOS() ? "lldb" : "gdb", "launch"), (config.Type, config.Request));
        Assert.Equal(exe, config.Properties["program"].GetString());
        Assert.Equal(Path.GetDirectoryName(exe), config.Properties["cwd"].GetString());
        var args = config.Properties["args"].EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal("fails", args[^2]);
        Assert.Equal("--break", args[^1]); // stop at the failed assertion
        Assert.Equal(TestOutcome.Failed, sink.Result("catch_tests/fails").Outcome);

        settings[CppTestProvider.DebuggerSetting] = "lldb-dap";
        launcher.Started.Clear();
        await provider.DebugAsync(new TestRunRequest(project, null), new RecordingSink(), launcher, ct);
        Assert.Equal("lldb-dap", Assert.Single(launcher.Started).Type);
        Assert.False(launcher.Started[0].Properties.ContainsKey("sourceLanguages"));
        Assert.Equal("[\"cpp\"]", CppTestProvider.Configuration("lldb", project, []).Properties["sourceLanguages"].GetRawText());
    }

    [Fact]
    public async Task Debugging_googletest_reads_its_console_from_the_session_output()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDirectory("cscode-cpp-debug-gtest-");
        FakeExecutable(dir, "build/gtest_tests", "gtest_list_tests");
        var provider = new CppTestProvider(new TestProviderContext(new ScriptedProcesses()));
        var project = Assert.Single(await provider.DetectAsync(dir.Path, ct));
        var launcher = new ScriptedLauncher { Chunks = ["[ RUN      ] MathTest.Adds\n[       OK ] Math", "Test.Adds (2 ms)\n[ RUN      ] MathTest.Fails\n/w/a.cpp:9: Failure\nboom\n\n[  FAILED  ] MathTest.Fails (1 ms)\n"] };
        var sink = new RecordingSink();
        await provider.DebugAsync(new TestRunRequest(project, ["gtest_tests/MathTest.Adds", "gtest_tests/MathTest.Fails"]), sink, launcher, ct);
        Assert.Equal(TestOutcome.Passed, sink.Result("gtest_tests/MathTest.Adds").Outcome);
        var fails = sink.Result("gtest_tests/MathTest.Fails");
        Assert.Equal((TestOutcome.Failed, "a.cpp:9: boom", new TestLocation("/w/a.cpp", 8)), (fails.Outcome, fails.Message, fails.FailureLocation));
        Assert.Contains("--gtest_break_on_failure", Assert.Single(launcher.Started).Properties["args"].EnumerateArray().Select(a => a.GetString()));
    }

    [Theory]
    [InlineData(null, true, "lldb")]
    [InlineData(null, false, "gdb")]
    [InlineData("codelldb", false, "lldb")]
    [InlineData("lldb-dap", true, "lldb-dap")]
    [InlineData("gdb", true, "gdb")]
    public void Debug_type_follows_the_setting(string? setting, bool macOS, string expected) => Assert.Equal(expected, CppTestProvider.DebugType(setting, macOS));

    [Fact]
    public void Glyph_matching_uses_the_runner_name_after_the_executable()
    {
        var provider = new CppTestProvider(new TestProviderContext(new ScriptedProcesses()));
        var test = new DiscoveredTest("gtest_tests/Numbers/Param.IsPositive/1", "IsPositive/1", [], new TestLocation("/w/a.cpp", 17));
        Assert.True(provider.Matches(test, "/w/a.cpp", new SourceTest("Param.IsPositive", 17, "Param.IsPositive")));
        Assert.False(provider.Matches(test, "/w/b.cpp", new SourceTest("Param.IsPositive", 17, "Param.IsPositive")));
        Assert.False(provider.Matches(test, "/w/a.cpp", new SourceTest("Param.IsNegative", 17, "Param.IsNegative")));
    }

    /// <summary>Starts a scripted session that first lets the "program" write its report, then ends.</summary>
    private sealed class ReportWritingLauncher(Action<CsCode.Core.Debugging.DebugConfiguration> write) : ITestDebugLauncher
    {
        public List<CsCode.Core.Debugging.DebugConfiguration> Started { get; } = [];

        public ValueTask<CsCode.Core.Debugging.IDebugSession?> StartAsync(CsCode.Core.Debugging.DebugConfiguration configuration, CancellationToken ct)
        {
            Started.Add(configuration);
            write(configuration);
            var session = new ScriptedSession(configuration, []);
            session.Play();
            return ValueTask.FromResult<CsCode.Core.Debugging.IDebugSession?>(session);
        }
    }
}
