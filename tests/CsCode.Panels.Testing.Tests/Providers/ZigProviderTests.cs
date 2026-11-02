using System.Diagnostics;
using CsCode.Testing.Engine;
using CsCode.Testing.Providers.Zig;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Providers;

/// <summary>
/// The Zig test provider (docs/ZIG.md): the source scan, the test runner's output, the build's test step as <c>--verbose</c> prints it, how a run
/// is planned, the provider's processes (scripted), and a real suite against the real zig on a <c>zig init</c> project (skips without zig).
/// </summary>
public sealed class ZigProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ scanner

    [Fact]
    public void The_scan_finds_named_tests_and_decltests_with_their_lines_and_containers()
    {
        var tests = ZigTestScanner.Scan("""
            const std = @import("std");
            // test "in a comment" {}
            const text = "test \"in a string\" {}";
            const multi =
                \\test "in a multiline string" {}
            ;
            const c = '"';
            test "adds \"two\"\tnumbers\x21 \u{1F600}" {
                try std.testing.expect(true);
            }
            test add {}
            test {
                _ = Inner;
            }
            pub const Inner = extern struct {
                test "nested" {}
                const Deeper = union(enum) {
                    a: u8,
                    test "deeper" {}
                };
            };
            fn Generic(comptime T: type) type {
                return struct {
                    test "in a generic type" {}
                };
            }
            fn f() void {
                const @"test" = 1;
                _ = @"test";
            }
            test "after" {}
            """);
        Assert.Equal(
            [
                ("test.adds \"two\"\tnumbers! \U0001F600", 7, false, ""),
                ("decltest.add", 10, true, ""),
                ("Inner.test.nested", 15, false, "Inner"),
                ("Inner.Deeper.test.deeper", 18, false, "Inner/Deeper"),
                ("test.after", 30, false, ""),
            ],
            tests.Select(t => (t.Qualified, t.Line, t.IsDecltest, string.Join('/', t.Containers))));
        Assert.Equal("add", tests[1].Name);
    }

    [Fact]
    public void Scan_source_gives_the_editor_the_qualified_name_and_matches_the_discovered_id()
    {
        var provider = new ZigTestProvider(new TestProviderContext(new ScriptedProcesses()));
        var source = Assert.Single(provider.ScanSource("/p/src/root.zig", "test \"basic add\" {}\n"));
        Assert.Equal(("test.basic add", 0, "basic add"), (source.Name, source.Line, source.Label));
        var discovered = new DiscoveredTest("src/root.zig::test.basic add", "basic add", [], new TestLocation("/p/src/root.zig", 0));
        Assert.True(((ITestProvider)provider).Matches(discovered, "/p/src/root.zig", source));
        Assert.False(((ITestProvider)provider).Matches(discovered, "/p/src/main.zig", source));
    }

    // ------------------------------------------------------------------ output parser

    private static readonly TestProject Project = new(ZigTestProvider.ProviderId, "p", "/p/build.zig", "/p", "zig");

    private static (RecordingSink Sink, ZigTestOutputParser Parser) Parse(params string[] lines)
    {
        var sink = new RecordingSink();
        var parser = new ZigTestOutputParser(Project, sink, name => name.Contains(".test.", StringComparison.Ordinal) || name.Contains(".decltest.", StringComparison.Ordinal) ? "id:" + name : null);
        foreach (var l in lines) parser.OnLine(l);
        parser.Complete();
        return (sink, parser);
    }

    [Fact]
    public void Test_runner_output_gives_each_tests_outcome_output_message_and_trace()
    {
        // zig 0.17.0's output for a file with a passing, a failing (printing first), a skipped, a decltest, an unnamed and a nested test
        var (sink, parser) = Parse(
            "1/6 root.test.basic add functionality...OK",
            "2/6 root.test.fails on purpose...some output from the test",
            "expected 3, found 2",
            "FAIL (TestExpectedEqual)",
            "/z/lib/std/testing.zig:93:17: 0x102e9af4b in expectEqualInner__func_1000 (test)",
            "                return error.TestExpectedEqual;",
            "                ^",
            "/p/src/root.zig:22:5: 0x102e9b033 in test.fails on purpose (test)",
            "    try std.testing.expectEqual(@as(i32, 3), add(1, 1));",
            "    ^",
            "3/6 root.test.skipped...SKIP",
            "4/6 root.decltest.add...OK",
            "5/6 root.test_0...OK",
            "6/6 root.Inner.test.nested one...OK",
            "4 passed; 1 skipped; 1 failed.",
            "error: the following test command failed with exit code 1:",
            ".zig-cache/o/89f6ea092c77b268392323f787850194/test --seed=0x3c7d3b97");
        Assert.Equal(6, parser.Total);
        Assert.Equal(TestOutcome.Passed, sink.Result("id:root.test.basic add functionality").Outcome);
        var failed = sink.Result("id:root.test.fails on purpose");
        Assert.Equal(TestOutcome.Failed, failed.Outcome);
        Assert.Equal("error.TestExpectedEqual: expected 3, found 2", failed.Message);
        Assert.Equal("some output from the test\nexpected 3, found 2", failed.Output);
        Assert.StartsWith("/z/lib/std/testing.zig:93:17", failed.StackTrace, StringComparison.Ordinal);
        Assert.EndsWith("    ^", failed.StackTrace, StringComparison.Ordinal);
        Assert.Equal(6, failed.StackTrace!.Split('\n').Length);
        Assert.DoesNotContain("passed;", failed.StackTrace, StringComparison.Ordinal);
        Assert.Equal(TestOutcome.Skipped, sink.Result("id:root.test.skipped").Outcome);
        Assert.Equal(TestOutcome.Passed, sink.Result("id:root.decltest.add").Outcome);
        Assert.Equal(TestOutcome.Passed, sink.Result("id:root.Inner.test.nested one").Outcome);
        Assert.Equal(5, sink.Results.Count); // the unnamed block is not reported
        Assert.Equal(5, sink.StartedIds.Count);
        Assert.Equal(17, sink.Lines.Count); // every line reaches the output
    }

    [Fact]
    public void A_panicking_test_is_reported_failed_with_the_panic_when_the_process_ends()
    {
        var (sink, _) = Parse(
            "1/2 root.test.ok...OK",
            "2/2 root.test.panics...printed first",
            "thread 13995904 panic: index out of bounds: index 5, len 3",
            "/p/src/root.zig:30:14: 0x1 in test.panics (test)",
            "    _ = items[5];",
            "error: the following test command crashed:",
            ".zig-cache/o/abc/test --seed=0x1");
        var failed = sink.Result("id:root.test.panics");
        Assert.Equal(TestOutcome.Failed, failed.Outcome);
        Assert.Equal("panic: index out of bounds: index 5, len 3", failed.Message);
        Assert.Equal("printed first", failed.Output);
        Assert.Equal("/p/src/root.zig:30:14: 0x1 in test.panics (test)\n    _ = items[5];", failed.StackTrace);
        Assert.Equal(TestOutcome.Passed, sink.Result("id:root.test.ok").Outcome);
    }

    [Fact]
    public void A_compile_error_is_collected_and_no_test_is_reported()
    {
        var (sink, parser) = Parse(
            "src/root.zig:5:12: error: use of undeclared identifier 'nope'",
            "    return nope;",
            "           ^~~~");
        Assert.Null(parser.Total);
        Assert.Empty(sink.Results);
        Assert.Equal(["src/root.zig:5:12: error: use of undeclared identifier 'nope'"], parser.CompileErrors);
    }

    [Fact]
    public void Output_without_a_newline_before_the_outcome_still_ends_the_test()
    {
        var (sink, _) = Parse("1/1 root.test.prints...no newline hereOK", "All 1 tests passed.");
        var r = sink.Result("id:root.test.prints");
        Assert.Equal((TestOutcome.Passed, "no newline here"), (r.Outcome, r.Output));
    }

    // ------------------------------------------------------------------ the build's test step, planning

    private const string VerboseInit = """
        info(verbose): /z/zig build-exe --cache-dir .zig-cache --global-cache-dir /c --build-root . --zig-lib-dir /z/lib --name configurer -fsingle-threaded --dep @build -Mroot=/z/lib/compiler/configurer.zig
        info(verbose): /z/zig test -Odebug --dep hello -Mroot=src/main.zig -Mhello=src/root.zig --cache-dir .zig-cache --global-cache-dir /Users/me/.cache/zig --build-root . --name test --zig-lib-dir /z/lib/ --listen=-
        info(verbose): /z/zig test -Mroot=src/root.zig --cache-dir .zig-cache --global-cache-dir /Users/me/.cache/zig --build-root . --name test --zig-lib-dir /z/lib/ --listen=-
        info(verbose): ./.zig-cache/o/9749b0931fc21016a0803ef907103f2b/test --cache-dir=./.zig-cache --seed=0x78e519a2 --listen=-
        info(verbose): /z/zig test -Mroot=src/root.zig --cache-dir .zig-cache --name test --listen=-
        """;

    [Fact]
    public void The_verbose_build_gives_each_test_roots_command_once_without_the_build_runners_protocol()
    {
        var commands = ZigTestProvider.ParseVerbose(VerboseInit.Split('\n'), "/p");
        Assert.Equal(["src/main.zig", "src/root.zig"], commands.Select(c => c.Root));
        Assert.Equal(["-Odebug", "--dep", "hello", "-Mroot=src/main.zig", "-Mhello=src/root.zig", "--cache-dir", ".zig-cache", "--build-root", ".", "--name", "test"], commands[0].Arguments);
        Assert.Equal(["a b", "c", "d e", ""], ZigTestProvider.SplitCommandLine("'a b' c \"d e\" ''"));
    }

    [Fact]
    public void A_run_compiles_each_file_as_the_build_does_with_a_filter_per_test()
    {
        var commands = ZigTestProvider.ParseVerbose(VerboseInit.Split('\n'), "/p");
        var runs = ZigTestProvider.Plan(["src/main.zig::test.simple test", "src/root.zig::test.basic add", "src/root.zig::decltest.add", "src/util/x.zig::Inner.test.deep"], commands, []);
        Assert.Equal(["src/main.zig", "src/root.zig", "src/util/x.zig"], runs.Select(r => r.Root));
        Assert.Equal(["-Odebug", "--dep", "hello", "-Mroot=src/main.zig", "-Mhello=src/root.zig", "--cache-dir", ".zig-cache", "--build-root", ".", "--name", "test",
            "--test-filter", "main.test.simple test"], runs[0].Arguments);
        Assert.Equal(["--test-filter", "root.test.basic add", "--test-filter", "root.decltest.add"], runs[1].Arguments.TakeLast(4));
        // a file that is no root borrows the nearest root's modules (main.zig's --dep hello), with itself as the root module
        Assert.Equal(["-Odebug", "--dep", "hello", "-Mroot=src/util/x.zig", "-Mhello=src/root.zig"], runs[2].Arguments.Take(5));
        Assert.Equal(["--test-filter", "x.Inner.test.deep"], runs[2].Arguments.TakeLast(2));

        // the whole project: each root of the test step, unfiltered; without a test step, each file declaring tests on its own
        Assert.Equal([("src/main.zig", false), ("src/root.zig", false)], ZigTestProvider.Plan(null, commands, []).Select(r => (r.Root, r.Arguments.Contains("--test-filter"))));
        Assert.Equal([["-Mroot=a.zig"], ["-Mroot=sub/b.zig"]], ZigTestProvider.Plan(null, [], ["a.zig", "sub/b.zig"]).Select(r => r.Arguments));
        Assert.Equal(["-Mroot=lone.zig", "--test-filter", "lone.test.t"], ZigTestProvider.Plan(["lone.zig::test.t"], [], []).Single().Arguments);
    }

    [Fact]
    public void Runner_names_map_back_to_the_ids_of_the_files_they_come_from()
    {
        using var dir = new TempDirectory("zigmap-");
        dir.WriteFile("build.zig", "");
        dir.WriteFile("src/root.zig", "");
        dir.WriteFile("src/sub/util.zig", "");
        var project = new TestProject(ZigTestProvider.ProviderId, "p", Path.Combine(dir.Path, "build.zig"), dir.Path, "zig");
        var map = ZigTestProvider.IdMapper(project, "src/root.zig");
        Assert.Equal("src/root.zig::test.basic add", map("root.test.basic add"));
        Assert.Equal("src/root.zig::decltest.add", map("root.decltest.add"));
        Assert.Equal("src/root.zig::Inner.test.a.b c", map("root.Inner.test.a.b c")); // dots in a test's name stay its own
        Assert.Equal("src/sub/util.zig::test.x", map("sub.util.test.x"));
        Assert.Equal("src/sub/util.zig::Box.decltest.y", map("sub.util.Box.decltest.y"));
        Assert.Null(map("root.test_0"));
        Assert.Null(map("std.test.elsewhere"));
        Assert.Equal("root", ZigTestProvider.RunnerPrefix("src/root.zig", "src/root.zig"));
        Assert.Equal("sub.util", ZigTestProvider.RunnerPrefix("src/root.zig", "src/sub/util.zig"));
    }

    // ------------------------------------------------------------------ the provider's processes (scripted)

    private static (ZigTestProvider Provider, ScriptedProcesses Processes, TestProject Project, TempDirectory Dir) Scripted()
    {
        var dir = new TempDirectory("zigprovider-");
        dir.WriteFile("build.zig", "pub fn build(b: *std.Build) void { _ = b; }\n");
        dir.WriteFile("src/root.zig", "test \"basic add\" {}\ntest \"fails\" {}\n");
        dir.WriteFile("src/main.zig", "test \"simple test\" {}\n");
        var processes = new ScriptedProcesses();
        processes.Answer = spec => spec.Arguments[0] == "build"
            ? (0, [], VerboseInit.Split('\n'))
            : spec.Arguments.Contains("-Mroot=src/root.zig")
                ? (1, [], ["1/2 root.test.basic add...OK", "2/2 root.test.fails...FAIL (TestUnexpectedResult)", "1 passed; 0 skipped; 1 failed."])
                : (0, [], ["1/1 main.test.simple test...OK", "All 1 tests passed."]);
        var settings = new Dictionary<string, string> { [ZigTestProvider.ZigPathSetting] = dir.WriteFile("tools/zig", "") };
        var provider = new ZigTestProvider(new TestProviderContext(processes, key => settings.GetValueOrDefault(key)));
        var project = new TestProject(ZigTestProvider.ProviderId, "p", Path.Combine(dir.Path, "build.zig"), dir.Path, "zig");
        return (provider, processes, project, dir);
    }

    [Fact]
    public async Task Discovery_scans_the_projects_files_and_runs_nothing()
    {
        var (provider, processes, project, dir) = Scripted();
        using var _ = dir;
        dir.WriteFile("zig-out/gen.zig", "test \"built output\" {}\n");
        dir.WriteFile(".zig-cache/o/x/options.zig", "test \"cache\" {}\n");
        dir.WriteFile("deps/lib/build.zig", "");
        dir.WriteFile("deps/lib/src/lib.zig", "test \"nested project\" {}\n");
        Assert.Equal([project.FilePath, Path.Combine(dir.Path, "deps", "lib", "build.zig")], (await provider.DetectAsync(dir.Path, Ct)).Select(p => p.FilePath).Order());
        var tests = await provider.DiscoverAsync(project, new RecordingSink(), Ct);
        Assert.Equal(["src/main.zig::test.simple test", "src/root.zig::test.basic add", "src/root.zig::test.fails"], tests.Select(t => t.Id));
        Assert.Equal(("src/root.zig", TestNodeKind.File), (tests[1].Groups[0].Label, tests[1].Groups[0].Kind));
        Assert.Equal(1, tests[2].Location!.Line);
        Assert.Empty(processes.Started);
    }

    [Fact]
    public async Task Running_learns_the_test_step_once_then_runs_each_file_with_its_filters()
    {
        var (provider, processes, project, dir) = Scripted();
        using var _ = dir;
        var sink = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, ["src/root.zig::test.basic add", "src/root.zig::test.fails"]), sink, Ct);
        Assert.Equal((1, (string?)null), (outcome.ExitCode, outcome.Error));
        var started = processes.Started.ToList();
        var zig = Path.Combine(dir.Path, "tools", "zig");
        Assert.Equal([zig + " build test --verbose --summary none --color off", zig + " test"], started.Select(s => s.FileName + " " + string.Join(' ', s.Arguments.Take(s.Arguments[0] == "build" ? 7 : 1))));
        Assert.All(started, s => Assert.Equal(dir.Path, s.WorkingDirectory));
        Assert.Equal(["--test-filter", "root.test.basic add", "--test-filter", "root.test.fails"], started[1].Arguments.TakeLast(4));
        Assert.Equal(TestOutcome.Passed, sink.Result("src/root.zig::test.basic add").Outcome);
        Assert.Equal(TestOutcome.Failed, sink.Result("src/root.zig::test.fails").Outcome);

        // learned once: the next run goes straight to zig test; a changed build.zig is read again
        await provider.RunAsync(new TestRunRequest(project, null), new RecordingSink(), Ct);
        Assert.Equal(4, processes.Started.Count);
        File.AppendAllText(project.FilePath, "// changed\n");
        File.SetLastWriteTimeUtc(project.FilePath, DateTime.UtcNow.AddMinutes(1));
        await provider.RunAsync(new TestRunRequest(project, ["src/main.zig::test.simple test"]), new RecordingSink(), Ct);
        Assert.Equal("build", processes.Started.ElementAt(4).Arguments[0]);
    }

    [Fact]
    public async Task A_file_that_does_not_compile_fails_the_run_with_the_compile_error()
    {
        var (provider, processes, project, dir) = Scripted();
        using var _ = dir;
        processes.Answer = spec => spec.Arguments[0] == "build" ? (1, [], ["error: no step named 'test'"]) : (1, [], ["src/root.zig:1:1: error: expected test, found 'x'"]);
        var outcome = await provider.RunAsync(new TestRunRequest(project, ["src/root.zig::test.fails"]), new RecordingSink(), Ct);
        Assert.Equal("zig test could not compile src/root.zig: src/root.zig:1:1: error: expected test, found 'x'", outcome.Error);
        Assert.Equal(["-Mroot=src/root.zig", "--test-filter", "root.test.fails"], processes.Started.Last().Arguments.Skip(1));
    }

    [Fact]
    public async Task Debugging_builds_the_test_binary_and_launches_it_under_codelldb_in_the_project()
    {
        var (provider, processes, project, dir) = Scripted();
        using var _ = dir;
        processes.Answer = spec =>
        {
            if (spec.Arguments[0] == "build") return (0, [], VerboseInit.Split('\n'));
            var emit = spec.Arguments.Single(a => a.StartsWith("-femit-bin=", StringComparison.Ordinal))["-femit-bin=".Length..];
            File.WriteAllText(emit, "binary");
            return (0, [], []);
        };
        var launcher = new RecordingLauncher();
        var outcome = await provider.DebugAsync(new TestRunRequest(project, ["src/root.zig::test.fails"]), new RecordingSink(), launcher, Ct);
        var compile = processes.Started.Last();
        Assert.Contains("--test-no-exec", compile.Arguments);
        Assert.Equal(["--test-filter", "root.test.fails", "--test-no-exec"], compile.Arguments.SkipLast(1).TakeLast(3));
        var config = Assert.Single(launcher.Started);
        Assert.Equal(("lldb", "launch"), (config.Type, config.Request));
        Assert.EndsWith("root-test", config.GetString("program"), StringComparison.Ordinal);
        Assert.Equal(dir.Path, config.GetString("cwd"));
        Assert.Equal(["zig"], config.Properties["sourceLanguages"].EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(-1, outcome.ExitCode); // the recording launcher starts no session
        Assert.False(File.Exists(config.GetString("program")), "the temporary binary is removed after the session");
    }

    // ------------------------------------------------------------------ real zig

    /// <summary><c>zig init</c> in <c>&lt;temp&gt;/hello</c> (zig init names the module after the folder: <c>@import("hello")</c>).</summary>
    private static async Task<(TempDirectory Temp, string Project)> ZigInitAsync(string zig)
    {
        var temp = new TempDirectory("zig-tests-");
        var project = Path.Combine(temp.Path, "hello");
        Directory.CreateDirectory(project);
        var psi = new ProcessStartInfo(zig, "init") { WorkingDirectory = project, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        using (var p = Process.Start(psi)!)
        {
            await p.WaitForExitAsync(Ct);
            Assert.Equal(0, p.ExitCode);
        }
        return (temp, project);
    }

    [Fact]
    [Trait("Category", "RealTools")]
    public async Task A_zig_init_project_is_detected_discovered_run_whole_and_test_by_test_with_its_build_modules()
    {
        Assert.SkipWhen(ZigTools.ZigSkipReason() is not null, ZigTools.ZigSkipReason() ?? "");
        var zig = ZigTools.FindZig()!;
        var (temp, root) = await ZigInitAsync(zig);
        using var _ = temp;
        // main.zig's tests use the "hello" module build.zig gives it (zig test src/main.zig alone cannot compile this), and root.zig gets a
        // failing, a skipped and a nested test
        File.AppendAllText(Path.Combine(root, "src", "main.zig"), "\ntest \"uses the module\" {\n    try std.testing.expectEqual(@as(i32, 5), hello.add(2, 3));\n}\n");
        File.AppendAllText(Path.Combine(root, "src", "root.zig"), """

            test "fails on purpose" {
                std.debug.print("some output from the test\n", .{});
                try std.testing.expectEqual(@as(i32, 3), add(1, 1));
            }

            test "skipped" {
                return error.SkipZigTest;
            }

            test add {
                try std.testing.expect(add(1, 2) == 3);
            }

            const Inner = struct {
                test "nested one" {
                    try std.testing.expect(true);
                }
            };

            test {
                _ = Inner;
            }

            """);
        var provider = new ZigTestProvider(new TestProviderContext(new ProcessRunner(), key => key == ZigTestProvider.ZigPathSetting ? zig : null));
        var project = Assert.Single(await provider.DetectAsync(temp.Path, Ct));
        var tests = await provider.DiscoverAsync(project, new RecordingSink(), Ct);
        Assert.Equal(
            ["src/main.zig::test.simple test", "src/main.zig::test.fuzz example", "src/main.zig::test.uses the module", "src/root.zig::test.basic add functionality",
             "src/root.zig::test.fails on purpose", "src/root.zig::test.skipped", "src/root.zig::decltest.add", "src/root.zig::Inner.test.nested one"],
            tests.Select(t => t.Id));

        var all = new RecordingSink();
        var outcome = await provider.RunAsync(new TestRunRequest(project, null), all, Ct);
        var log = string.Join("\n", all.Lines);
        Assert.True(outcome.Error is null, outcome.Error + "\n" + log);
        Assert.Equal(1, outcome.ExitCode);
        foreach (var id in tests.Select(t => t.Id).Except(["src/root.zig::test.fails on purpose", "src/root.zig::test.skipped"]))
            Assert.True(all.Result(id).Outcome == TestOutcome.Passed, $"{id}: {all.Result(id).Outcome}\n{log}");
        Assert.Equal(TestOutcome.Skipped, all.Result("src/root.zig::test.skipped").Outcome);
        var failed = all.Result("src/root.zig::test.fails on purpose");
        Assert.Equal(TestOutcome.Failed, failed.Outcome);
        Assert.Equal("error.TestExpectedEqual: expected 3, found 2", failed.Message);
        Assert.Contains("some output from the test", failed.Output, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(root, "src", "root.zig"), failed.FailureLocation!.FilePath);
        Assert.Equal(tests.Single(t => t.Id == failed.TestId).Location!.Line + 2, failed.FailureLocation.Line); // the expectEqual line

        // one test of main.zig, which needs build.zig's module: compiled with the learned flags and filtered to that test
        var one = new RecordingSink();
        outcome = await provider.RunAsync(new TestRunRequest(project, ["src/main.zig::test.uses the module"]), one, Ct);
        Assert.True(outcome.Error is null && outcome.ExitCode == 0, outcome.Error + "\n" + string.Join("\n", one.Lines));
        Assert.Equal(["src/main.zig::test.uses the module"], one.Results.Select(r => r.TestId));
        Assert.Equal(TestOutcome.Passed, one.Results.Single().Outcome);
        Assert.DoesNotContain(one.Lines, l => l.StartsWith("> zig build", StringComparison.Ordinal)); // learned already
    }
}
