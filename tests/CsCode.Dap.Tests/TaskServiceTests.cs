using System.Diagnostics;
using CsCode.Core.Tasks;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Tasks;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

public sealed class TaskFixture : IAsyncDisposable
{
    public TempDirectory Dir { get; } = new("daptask-");
    public DebugLane Lane { get; } = new("task-lane");
    public InMemoryOutputService Output { get; } = new();
    public FakeWorkspace Workspace { get; }
    public TaskService Service { get; }

    public TaskFixture(string? tasksJson = null, string location = ".cscode/tasks.json")
    {
        Workspace = new FakeWorkspace(Dir.Uri);
        if (tasksJson is not null) Dir.WriteFile(location, tasksJson);
        Service = new TaskService(Lane, new DapPlatform { Workspace = Workspace, Output = Output });
    }

    public string OutputText => Output.Text(TaskService.ChannelName);

    public ValueTask DisposeAsync()
    {
        Lane.Dispose();
        Dir.Dispose();
        return ValueTask.CompletedTask;
    }
}

public class TaskServiceTests
{
    private static void NeedsShell() => Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "needs /bin/sh");

    [Fact]
    public async Task Tasks_are_listed_with_type_group_and_default_flag()
    {
        await using var f = new TaskFixture("""
            // comments and trailing commas are fine
            {"version":"2.0.0","tasks":[
              {"label":"build","type":"shell","command":"dotnet build","group":{"kind":"build","isDefault":true},},
              {"label":"test","type":"process","command":"dotnet","args":["test"],"group":"test"},
              {"label":"misc","command":"true"},
            ]}
            """);
        var tasks = await f.Service.GetTasksAsync(null, Ct);
        Assert.Equal(["build", "test", "misc"], tasks.Select(t => t.Label));
        Assert.Equal(("shell", "build", true), (tasks[0].Type, tasks[0].Group, tasks[0].IsDefaultBuild));
        Assert.Equal(("process", "test", false), (tasks[1].Type, tasks[1].Group, tasks[1].IsDefault));
        Assert.Equal("shell", tasks[2].Type); // shell is the default type
    }

    [Fact]
    public async Task Falls_back_to_vscode_tasks_json()
    {
        await using var f = new TaskFixture("""{"tasks":[{"label":"t","command":"true"}]}""", ".vscode/tasks.json");
        Assert.Equal(["t"], (await f.Service.GetTasksAsync(null, Ct)).Select(t => t.Label));
    }

    [Fact]
    public async Task No_tasks_json_lists_nothing()
    {
        await using var f = new TaskFixture();
        Assert.Empty(await f.Service.GetTasksAsync(null, Ct));
    }

    [Fact]
    public async Task A_shell_task_runs_and_its_output_goes_to_the_Tasks_channel()
    {
        NeedsShell();
        await using var f = new TaskFixture("""{"tasks":[{"label":"hello","type":"shell","command":"echo out-text; echo err-text 1>&2"}]}""");
        var r = await f.Service.RunAsync("hello", null, Ct);
        Assert.True(r.Succeeded);
        Assert.Equal(0, r.ExitCode);
        await SessionHarness.Eventually(() => f.OutputText.Contains("out-text") && f.OutputText.Contains("err-text") && f.OutputText.Contains("finished with exit code 0"));
        Assert.Contains("> Executing task: hello <", f.OutputText);
    }

    [Fact]
    public async Task Exit_codes_are_reported_and_non_zero_is_failure()
    {
        NeedsShell();
        await using var f = new TaskFixture("""{"tasks":[{"label":"fail","command":"exit 3"}]}""");
        var r = await f.Service.RunAsync("fail", null, Ct);
        Assert.False(r.Succeeded);
        Assert.Equal(3, r.ExitCode);
        Assert.Null(r.Error);
        Assert.False(r.Cancelled);
    }

    [Fact]
    public async Task Process_tasks_pass_args_without_a_shell()
    {
        NeedsShell();
        await using var f = new TaskFixture("""{"tasks":[{"label":"p","type":"process","command":"/bin/sh","args":["-c","printf '%s' \"$0 $1\" > out.txt","a b","$HOME"]}]}""");
        var r = await f.Service.RunAsync("p", null, Ct);
        Assert.True(r.Succeeded);
        Assert.Equal("a b $HOME", File.ReadAllText(Path.Combine(f.Dir.Path, "out.txt"))); // no shell expansion, args intact
    }

    [Fact]
    public async Task Shell_task_args_with_spaces_are_quoted()
    {
        NeedsShell();
        await using var f = new TaskFixture("""{"tasks":[{"label":"q","type":"shell","command":"printf","args":["%s|","two words","it's"]}]}""");
        var r = await f.Service.RunAsync("q", null, Ct);
        Assert.True(r.Succeeded);
        await SessionHarness.Eventually(() => f.OutputText.Contains("two words|"));
    }

    [Fact]
    public async Task Default_cwd_is_the_workspace_folder_and_options_cwd_and_env_are_honoured_with_variables()
    {
        NeedsShell();
        await using var f = new TaskFixture("""
            {"tasks":[
              {"label":"pwd","command":"pwd > where.txt"},
              {"label":"sub","command":"pwd > where2.txt; echo $GREETING > env.txt","options":{"cwd":"${workspaceFolder}/sub","env":{"GREETING":"hi-${workspaceFolderBasename}"}}}
            ]}
            """);
        Directory.CreateDirectory(Path.Combine(f.Dir.Path, "sub"));
        Assert.True((await f.Service.RunAsync("pwd", null, Ct)).Succeeded);
        Assert.True((await f.Service.RunAsync("sub", null, Ct)).Succeeded);
        var real = new DirectoryInfo(f.Dir.Path).ResolveLinkTarget(true)?.FullName ?? f.Dir.Path;
        Assert.EndsWith(Path.GetFileName(f.Dir.Path), File.ReadAllText(Path.Combine(f.Dir.Path, "where.txt")).Trim());
        Assert.EndsWith(Path.Combine(Path.GetFileName(f.Dir.Path), "sub"), File.ReadAllText(Path.Combine(f.Dir.Path, "sub", "where2.txt")).Trim());
        Assert.Equal("hi-" + Path.GetFileName(f.Dir.Path), File.ReadAllText(Path.Combine(f.Dir.Path, "sub", "env.txt")).Trim());
        _ = real;
    }

    [Fact]
    public async Task Env_variables_substitute_into_commands()
    {
        NeedsShell();
        Environment.SetEnvironmentVariable("CSCODE_TASK_TEST", "substituted");
        try
        {
            await using var f = new TaskFixture("""{"tasks":[{"label":"e","command":"echo ${env:CSCODE_TASK_TEST} > e.txt"}]}""");
            Assert.True((await f.Service.RunAsync("e", null, Ct)).Succeeded);
            Assert.Equal("substituted", File.ReadAllText(Path.Combine(f.Dir.Path, "e.txt")).Trim());
        }
        finally { Environment.SetEnvironmentVariable("CSCODE_TASK_TEST", null); }
    }

    [Fact]
    public async Task Platform_blocks_override_the_command_for_this_os()
    {
        NeedsShell();
        var key = OperatingSystem.IsMacOS() ? "osx" : "linux";
        await using var f = new TaskFixture("""{"tasks":[{"label":"x","command":"exit 9","KEY":{"command":"exit 0"}}]}""".Replace("KEY", key));
        Assert.True((await f.Service.RunAsync("x", null, Ct)).Succeeded);
    }

    [Fact]
    public async Task DependsOn_sequence_runs_dependencies_in_order_before_the_task()
    {
        NeedsShell();
        await using var f = new TaskFixture("""
            {"tasks":[
              {"label":"a","command":"echo a >> order.txt"},
              {"label":"b","command":"sleep 0.3; echo b >> order.txt"},
              {"label":"c","command":"echo c >> order.txt"},
              {"label":"main","command":"echo main >> order.txt","dependsOn":["a","b","c"],"dependsOrder":"sequence"}
            ]}
            """);
        Assert.True((await f.Service.RunAsync("main", null, Ct)).Succeeded);
        Assert.Equal(["a", "b", "c", "main"], File.ReadAllLines(Path.Combine(f.Dir.Path, "order.txt")));
    }

    [Fact]
    public async Task DependsOn_defaults_to_parallel_and_still_precedes_the_task()
    {
        NeedsShell();
        await using var f = new TaskFixture("""
            {"tasks":[
              {"label":"slow","command":"sleep 0.4; echo slow >> order.txt"},
              {"label":"fast","command":"echo fast >> order.txt"},
              {"label":"main","command":"echo main >> order.txt","dependsOn":["slow","fast"]}
            ]}
            """);
        var sw = Stopwatch.StartNew();
        Assert.True((await f.Service.RunAsync("main", null, Ct)).Succeeded);
        var lines = File.ReadAllLines(Path.Combine(f.Dir.Path, "order.txt"));
        Assert.Equal("main", lines[^1]);
        Assert.Equal(["fast", "slow"], lines[..2]); // parallel: the fast one finished first
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_dependency_without_a_command_just_groups_others_and_a_single_string_dependsOn_works()
    {
        NeedsShell();
        await using var f = new TaskFixture("""
            {"tasks":[{"label":"x","command":"echo x >> o.txt"},{"label":"y","command":"echo y >> o.txt"},
                      {"label":"group","dependsOn":["x","y"],"dependsOrder":"sequence"},
                      {"label":"top","command":"echo top >> o.txt","dependsOn":"group"}]}
            """);
        Assert.True((await f.Service.RunAsync("top", null, Ct)).Succeeded);
        Assert.Equal(["x", "y", "top"], File.ReadAllLines(Path.Combine(f.Dir.Path, "o.txt")));
    }

    [Fact]
    public async Task A_failing_dependency_stops_the_task_from_running()
    {
        NeedsShell();
        await using var f = new TaskFixture("""
            {"tasks":[{"label":"dep","command":"exit 4"},
                      {"label":"main","command":"echo ran > ran.txt","dependsOn":["dep"],"dependsOrder":"sequence"}]}
            """);
        var r = await f.Service.RunAsync("main", null, Ct);
        Assert.False(r.Succeeded);
        Assert.Equal(4, r.ExitCode);
        Assert.Equal("main", r.Label);
        Assert.False(File.Exists(Path.Combine(f.Dir.Path, "ran.txt")));
    }

    [Fact]
    public async Task Circular_dependencies_are_detected()
    {
        await using var f = new TaskFixture("""{"tasks":[{"label":"a","command":"true","dependsOn":["b"]},{"label":"b","command":"true","dependsOn":["a"]}]}""");
        var r = await f.Service.RunAsync("a", null, Ct);
        Assert.False(r.Succeeded);
        Assert.Contains("Circular", r.Error);
    }

    [Fact]
    public async Task A_task_may_be_depended_on_twice_without_being_a_cycle()
    {
        NeedsShell();
        await using var f = new TaskFixture("""
            {"tasks":[{"label":"base","command":"echo base >> o.txt"},
                      {"label":"l","command":"true","dependsOn":["base"]},{"label":"r","command":"true","dependsOn":["base"]},
                      {"label":"top","command":"true","dependsOn":["l","r"],"dependsOrder":"sequence"}]}
            """);
        Assert.True((await f.Service.RunAsync("top", null, Ct)).Succeeded);
    }

    [Fact]
    public async Task Unknown_labels_and_missing_files_are_reported_as_errors_not_exceptions()
    {
        await using var f = new TaskFixture("""{"tasks":[{"label":"a","command":"true"}]}""");
        var r = await f.Service.RunAsync("nope", null, Ct);
        Assert.False(r.Succeeded);
        Assert.Contains("nope", r.Error);
        await using var none = new TaskFixture();
        Assert.Contains("no task", (await none.Service.RunAsync("a", null, Ct)).Error);
    }

    [Fact]
    public async Task A_malformed_tasks_json_is_an_error_result()
    {
        await using var f = new TaskFixture("{ not json");
        var r = await f.Service.RunAsync("a", null, Ct);
        Assert.NotNull(r.Error);
        Assert.Contains("tasks.json", r.Error);
    }

    [Fact]
    public async Task A_missing_executable_is_an_error_result()
    {
        await using var f = new TaskFixture("""{"tasks":[{"label":"bad","type":"process","command":"/definitely/not/a/program"}]}""");
        var r = await f.Service.RunAsync("bad", null, Ct);
        Assert.False(r.Succeeded);
        Assert.NotNull(r.Error);
        await SessionHarness.Eventually(() => f.OutputText.Contains("could not be started"));
    }

    [Fact]
    public async Task Unresolvable_variables_fail_the_task_without_running_it()
    {
        await using var f = new TaskFixture("""{"tasks":[{"label":"v","command":"echo ${file}"}]}""");
        var r = await f.Service.RunAsync("v", null, Ct);
        Assert.False(r.Succeeded);
        Assert.Contains("no file is open", r.Error);
    }

    [Fact]
    public async Task Problem_matchers_in_any_shape_are_accepted_and_ignored()
    {
        NeedsShell();
        await using var f = new TaskFixture("""
            {"tasks":[
              {"label":"s","command":"true","problemMatcher":"$msCompile"},
              {"label":"a","command":"true","problemMatcher":["$tsc","$eslint-stylish"]},
              {"label":"o","command":"true","problemMatcher":{"owner":"x","pattern":{"regexp":"^(.*)$","file":1}},"presentation":{"reveal":"always"},"isBackground":false}
            ]}
            """);
        foreach (var l in new[] { "s", "a", "o" }) Assert.True((await f.Service.RunAsync(l, null, Ct)).Succeeded, l);
    }

    [Fact]
    public async Task Cancelling_kills_the_whole_process_tree()
    {
        NeedsShell();
        await using var f = new TaskFixture("""{"tasks":[{"label":"long","command":"sleep 300 & echo $! > child.pid; wait"}]}""");
        using var cts = new CancellationTokenSource();
        var run = f.Service.RunAsync("long", null, cts.Token).AsTask();
        var pidFile = Path.Combine(f.Dir.Path, "child.pid");
        await SessionHarness.Eventually(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0);
        var childPid = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(SessionReverseRequestTests.ProcessAlive(childPid));

        cts.Cancel();
        var r = await run;
        Assert.True(r.Cancelled);
        Assert.False(r.Succeeded);
        await SessionHarness.Eventually(() => !SessionReverseRequestTests.ProcessAlive(childPid), 5000, "the grandchild (sleep) must die with its parent");
    }

    [Fact]
    public async Task Cancelling_before_the_lane_runs_it_does_not_start_anything()
    {
        NeedsShell();
        await using var f = new TaskFixture("""{"tasks":[{"label":"x","command":"echo hi > ran.txt"}]}""");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await f.Service.RunAsync("x", null, cts.Token));
        Assert.False(File.Exists(Path.Combine(f.Dir.Path, "ran.txt")));
    }

    [Fact]
    public async Task A_task_flooding_output_completes_quickly_and_the_channel_stays_bounded()
    {
        NeedsShell();
        await using var f = new TaskFixture("""{"tasks":[{"label":"flood","command":"yes 0123456789 | head -c 20000000"}]}""");
        var sw = Stopwatch.StartNew();
        var r = await f.Service.RunAsync("flood", null, Ct);
        Assert.True(r.Succeeded);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
        await Task.Delay(300, Ct);
        Assert.True(f.OutputText.Length <= 2_100_000, $"channel holds {f.OutputText.Length} chars"); // 2 MB ring of the output service
    }

    [Fact]
    public async Task The_call_returns_to_the_caller_immediately()
    {
        NeedsShell();
        await using var f = new TaskFixture("""{"tasks":[{"label":"s","command":"sleep 0.5"}]}""");
        var sw = Stopwatch.StartNew();
        var pending = f.Service.RunAsync("s", null, Ct);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(50), $"caller blocked for {sw.Elapsed}");
        Assert.True((await pending).Succeeded);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "'two words'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("", "''")]
    [InlineData("\"already\"", "\"already\"")]
    [InlineData("$HOME/x", "$HOME/x")]
    [InlineData("a;b", "'a;b'")]
    public void ShellQuote_follows_posix_rules(string arg, string expected)
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "posix quoting");
        Assert.Equal(expected, TaskService.ShellQuote(arg));
    }
}
