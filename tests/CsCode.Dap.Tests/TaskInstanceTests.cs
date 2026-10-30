using System.Collections.Concurrent;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Tasks;
using CsCode.Dap.Client;
using CsCode.Dap.Configuration;
using CsCode.Dap.Tasks;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// Running tasks as VS Code does (end pass): a task runs at most <c>runOptions.instanceLimit</c> times at once, so a second run of a running
/// task waits for it instead of starting the command again, a shared dependency runs once, and Terminate/Restart act on the running instance
/// (terminal-4); a background task (<c>isBackground</c>) is ready for a preLaunchTask when its matchers' background patterns go idle, not
/// when its process exits (T43); task programs do not inherit CsCode's own DOTNET_ROOT_&lt;ARCH&gt; (T27).
/// </summary>
public sealed class TaskInstanceTests
{
    /// <summary>A terminal host whose runs the test drives: output and exit when the test says.</summary>
    private sealed class Host : ITaskTerminalHost
    {
        public readonly ConcurrentQueue<Run> Runs = new();

        /// <summary>
        /// Runs after the program started and before the start returns: the real host then waits for the UI to show the terminal while the
        /// program already prints (a fast watcher's whole first cycle).
        /// </summary>
        public Func<Run, Task>? WhileStarting;

        public async ValueTask<ITaskTerminalRun> StartAsync(TaskTerminalRequest request, CancellationToken ct = default)
        {
            var run = new Run(request);
            Runs.Enqueue(run);
            if (WhileStarting is { } during) await during(run);
            return run;
        }

        public Run[] Of(string label) => Runs.Where(r => r.Request.Label == label).ToArray();
    }

    private sealed class Run(TaskTerminalRequest request) : ITaskTerminalRun
    {
        private readonly TaskCompletionSource<TaskTerminalExit> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskTerminalRequest Request => request;
        public bool Terminated;
        public Task<TaskTerminalExit> Completion => _done.Task;
        public void Output(string text) => request.Output?.Invoke(text);
        public void Exit(int code) => _done.TrySetResult(new TaskTerminalExit(code, false));
        public void Terminate() { Terminated = true; _done.TrySetResult(new TaskTerminalExit(129, true)); }
        public ValueTask RevealAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDirectory Dir = new("daptaskinst-");
        public readonly DebugLane Lane = new("task-inst-lane");
        public readonly InMemoryOutputService Output = new();
        public readonly DiagnosticService Diagnostics = new();
        public readonly Host Host = new();
        public readonly TaskService Service;

        public Rig(string tasksJson, bool withHost = true)
        {
            Dir.WriteFile(".cscode/tasks.json", tasksJson);
            Service = new TaskService(Lane, new DapPlatform
            {
                Workspace = new FakeWorkspace(Dir.Uri), Output = Output, Diagnostics = Diagnostics, TaskTerminals = withHost ? () => Host : null,
            });
        }

        public ValueTask DisposeAsync()
        {
            Service.Dispose();
            Lane.Dispose();
            Dir.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<Run> StartedAsync(Rig rig, string label, int index = 0)
    {
        await SessionHarness.Eventually(() => rig.Host.Of(label).Length > index, 5000, $"'{label}' started");
        return rig.Host.Of(label)[index];
    }

    // ------------------------------------------------------------------ terminal-4: one instance at a time

    [Fact]
    public async Task Running_a_task_that_is_already_running_waits_for_it_instead_of_starting_it_again()
    {
        await using var rig = new Rig("""{"tasks":[{"label":"build","command":"dotnet build","problemMatcher":"$msCompile"}]}""");
        var first = rig.Service.RunAsync("build", null, Ct).AsTask();
        var run = await StartedAsync(rig, "build");
        Assert.True(rig.Service.IsActive("build"));
        Assert.False(rig.Service.IsActive("other"));

        var second = rig.Service.RunAsync("build", null, Ct).AsTask(); // Ctrl+Shift+B again while it builds
        await Task.Delay(200, Ct);
        Assert.Single(rig.Host.Runs); // no second terminal running the same build
        Assert.False(second.IsCompleted);

        run.Exit(0);
        Assert.True((await first).Succeeded);
        Assert.True((await second).Succeeded); // the second run is the first one's result
        Assert.False(rig.Service.IsActive("build"));

        // once it ended, the next run starts it afresh
        var third = rig.Service.RunAsync("build", null, Ct).AsTask();
        (await StartedAsync(rig, "build", 1)).Exit(3);
        Assert.Equal(3, (await third).ExitCode);
    }

    [Fact]
    public async Task A_dependency_two_branches_share_runs_once_and_instanceLimit_allows_more()
    {
        await using var rig = new Rig("""
            {"tasks":[{"label":"all","dependsOn":["left","right"]},{"label":"left","dependsOn":["gen"],"command":"l"},{"label":"right","dependsOn":["gen"],"command":"r"},
                      {"label":"gen","command":"g"},
                      {"label":"many","command":"m","runOptions":{"instanceLimit":2}}]}
            """);
        var all = rig.Service.RunAsync("all", null, Ct).AsTask();
        var gen = await StartedAsync(rig, "gen");
        await Task.Delay(200, Ct);
        Assert.Single(rig.Host.Of("gen")); // the diamond's shared dependency once
        gen.Exit(0);
        (await StartedAsync(rig, "left")).Exit(0);
        (await StartedAsync(rig, "right")).Exit(0);
        Assert.True((await all).Succeeded);

        var m1 = rig.Service.RunAsync("many", null, Ct).AsTask();
        await StartedAsync(rig, "many");
        Assert.False(rig.Service.IsActive("many")); // one of two
        var m2 = rig.Service.RunAsync("many", null, Ct).AsTask();
        await StartedAsync(rig, "many", 1);
        Assert.True(rig.Service.IsActive("many"));
        foreach (var r in rig.Host.Of("many")) r.Exit(0);
        Assert.True((await m1).Succeeded && (await m2).Succeeded);
    }

    [Fact]
    public async Task Terminate_ends_the_running_instance_and_a_restart_runs_it_afresh()
    {
        await using var rig = new Rig("""{"tasks":[{"label":"serve","command":"serve"}]}""");
        var first = rig.Service.RunAsync("serve", null, Ct).AsTask();
        var run = await StartedAsync(rig, "serve");

        // Restart Task: terminate, wait for the end, run again
        await rig.Service.TerminateAsync("serve", null, Ct);
        Assert.True(run.Terminated);
        Assert.True((await first).Cancelled);
        Assert.False(rig.Service.IsActive("serve"));
        var again = rig.Service.RunAsync("serve", null, Ct).AsTask();
        var second = await StartedAsync(rig, "serve", 1);
        Assert.NotSame(run, second);
        second.Exit(0);
        Assert.True((await again).Succeeded);
        await rig.Service.TerminateAsync("serve", null, Ct); // nothing running: returns at once
    }

    [Fact]
    public async Task Cancelling_a_waiting_second_run_leaves_the_first_running()
    {
        await using var rig = new Rig("""{"tasks":[{"label":"build","command":"b"}]}""");
        var first = rig.Service.RunAsync("build", null, Ct).AsTask();
        var run = await StartedAsync(rig, "build");
        using var cts = new CancellationTokenSource();
        var second = rig.Service.RunAsync("build", null, cts.Token).AsTask();
        await Task.Delay(100, Ct);
        cts.Cancel();
        Assert.True((await second).Cancelled);
        Assert.False(run.Terminated);
        run.Exit(0);
        Assert.True((await first).Succeeded);
    }

    // ------------------------------------------------------------------ T43: background tasks

    private const string WatchTasks = """
        {"tasks":[
          {"label":"watch","command":"tsc --watch","isBackground":true,"problemMatcher":"$tsc-watch"},
          {"label":"custom","command":"serve","isBackground":true,
           "problemMatcher":{"owner":"srv","pattern":{"regexp":"^(.*):(\\d+): (.*)$","file":1,"line":2,"message":3},
                             "background":{"beginsPattern":"^compiling","endsPattern":"^ready"}}},
          {"label":"untracked","command":"sleep 1000","isBackground":true},
          {"label":"launch-deps","dependsOn":["watch","custom"]}]}
        """;

    [Fact]
    public async Task A_watch_task_is_ready_when_its_first_compilation_ends_and_keeps_running()
    {
        await using var rig = new Rig(WatchTasks);
        var pre = rig.Service.RunAsync("watch", null, Ct).AsTask(); // a preLaunchTask
        var run = await StartedAsync(rig, "watch");
        run.Output("[10:00:00 AM] Starting compilation in watch mode...\n");
        await Task.Delay(200, Ct);
        Assert.False(pre.IsCompleted); // compiling
        run.Output("src/a.ts(1,1): error TS1005: ';' expected.\n[10:00:02 AM] Found 1 error. Watching for file changes.\n");
        var ready = await pre.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(ready.Succeeded, ready.Error);
        Assert.False(run.Terminated); // the watch goes on
        Assert.Single(rig.Diagnostics.GetAll());
        Assert.True(rig.Service.IsActive("watch"));

        // a preLaunchTask that is already running and idle: ready at once, no second watch
        var idle = await rig.Service.RunAsync("watch", null, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(idle.Succeeded);
        Assert.Single(rig.Host.Of("watch"));

        // while it recompiles, a second run waits for the end of that compilation
        run.Output("[10:01:00 AM] File change detected. Starting incremental compilation...\n");
        await SessionHarness.Eventually(() => rig.Diagnostics.GetAll().Count == 1, 2000, "still the last cycle's problem");
        await Task.Delay(100, Ct);
        var busy = rig.Service.RunAsync("watch", null, Ct).AsTask();
        await Task.Delay(200, Ct);
        Assert.False(busy.IsCompleted);
        run.Output("[10:01:01 AM] Found 0 errors. Watching for file changes.\n");
        Assert.True((await busy.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Succeeded);
        await SessionHarness.Eventually(() => rig.Diagnostics.GetAll().Count == 0, 2000, "the fixed error cleared at the end of the cycle");

        // the process ends: the task is no longer active
        run.Exit(0);
        await SessionHarness.Eventually(() => !rig.Service.IsActive("watch"), 5000, "inactive after the process ended");
    }

    [Fact]
    public async Task Background_tasks_in_a_dependsOn_group_and_a_custom_background_matcher()
    {
        await using var rig = new Rig(WatchTasks);
        var group = rig.Service.RunAsync("launch-deps", null, Ct).AsTask();
        var watch = await StartedAsync(rig, "watch");
        var custom = await StartedAsync(rig, "custom");
        watch.Output("[1:00:00 PM] Found 0 errors. Watching for file changes.\n"); // activeBegins: busy from the start, idle now
        await Task.Delay(200, Ct);
        Assert.False(group.IsCompleted); // custom has not begun (no activeBegins) and not ended
        custom.Output("compiling\nsrc/x.c:3: bad\nready\n");
        Assert.True((await group.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Succeeded);
        Assert.Single(rig.Diagnostics.GetAll());
        watch.Exit(0);
        custom.Exit(0);
    }

    [Fact]
    public async Task A_background_task_without_background_patterns_cannot_be_tracked_and_one_that_exits_reports_its_exit()
    {
        await using var rig = new Rig(WatchTasks);
        var started = DateTime.UtcNow;
        var untracked = await rig.Service.RunAsync("untracked", null, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.False(untracked.Succeeded);
        Assert.Contains("cannot be tracked", untracked.Error);
        Assert.True(untracked.NotTracked); // (a plain Run Task shows nothing for it: only the debugger reports it)
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(4.5));
        var run = await StartedAsync(rig, "untracked");
        Assert.False(run.Terminated); // it runs on (VS Code: Debug Anyway is the user's call)
        run.Exit(0);

        // a watch task that dies before it is ready: its exit is the result
        var pre = rig.Service.RunAsync("watch", null, Ct).AsTask();
        (await StartedAsync(rig, "watch")).Exit(2);
        var died = await pre.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(2, died.ExitCode);
        Assert.False(died.NotTracked);
    }

    [Fact]
    public async Task A_watch_task_whose_first_cycle_ends_while_its_terminal_is_starting_is_ready()
    {
        await using var rig = new Rig(WatchTasks);
        // a fast watcher (esbuild --watch) prints its whole first cycle while the terminal's start still waits for the UI to show it
        rig.Host.WhileStarting = async run =>
        {
            run.Output("compiling\nsrc/x.c:3: bad\nready\n");
            await Task.Delay(300, Ct);
        };
        var custom = await rig.Service.RunAsync("custom", null, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.True(custom.Succeeded, custom.Error); // (missed, the cycle left it "cannot be tracked" after five seconds)
        Assert.False(rig.Host.Of("custom")[0].Terminated);
        Assert.Single(rig.Diagnostics.GetAll());

        // activeBegins ($tsc-watch): busy from the start, only its end line printed during the start
        rig.Host.WhileStarting = async run =>
        {
            run.Output("[10:00:02 AM] Found 0 errors. Watching for file changes.\n");
            await Task.Delay(300, Ct);
        };
        var watch = await rig.Service.RunAsync("watch", null, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.True(watch.Succeeded, watch.Error);
        Assert.True(rig.Service.IsActive("watch"));
        foreach (var r in rig.Host.Runs) r.Exit(0);
    }

    [Fact]
    public async Task Without_a_terminal_a_background_process_is_ready_on_its_end_pattern()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "needs /bin/sh");
        await using var rig = new Rig("""
            {"tasks":[{"label":"w","command":"echo compiling; echo ready; exec sleep 1000","isBackground":true,
                       "problemMatcher":{"owner":"w","pattern":{"regexp":"^(.*):(\\d+): (.*)$","file":1,"line":2,"message":3},
                                         "background":{"activeBegins":true,"endsPattern":"^ready"}}}]}
            """, withHost: false);
        var ready = await rig.Service.RunAsync("w", null, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(ready.Succeeded, ready.Error);
        var pid = Assert.Single(rig.Service.RunningProcessIds);
        await rig.Service.TerminateAsync("w", null, Ct);
        await SessionHarness.Eventually(() => rig.Service.RunningProcessIds.Count == 0, 5000, $"process {pid} ended");
    }

}

/// <summary>T27: a task's process does not inherit the DOTNET_ROOT_&lt;ARCH&gt; that `dotnet run`/`dotnet test` gave CsCode itself.</summary>
[Collection(SerialCollection.Name)] // changes this process's environment for a moment
public sealed class TaskEnvironmentTests
{
    [Fact]
    public async Task A_task_process_gets_DOTNET_ROOT_instead_of_CsCodes_own_architecture_root()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "needs /bin/sh");
        var arch = InheritedDotnetRoot.ArchRootVariable;
        var previous = (Environment.GetEnvironmentVariable(arch), Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        using var dir = new TempDirectory("daptaskenv-");
        dir.WriteFile(".cscode/tasks.json", $$"""{"tasks":[{"label":"env","command":"echo \"[$(printenv {{arch}} || echo unset)|$DOTNET_ROOT]\""}]}""");
        using var lane = new DebugLane("task-env-lane");
        var output = new InMemoryOutputService();
        using var service = new TaskService(lane, new DapPlatform { Workspace = new FakeWorkspace(dir.Uri), Output = output });
        try
        {
            // what a CsCode started by `dotnet run` has (this test process's own values come back below)
            Environment.SetEnvironmentVariable(arch, "/opt/sdk-of-cscode");
            Environment.SetEnvironmentVariable("DOTNET_ROOT", null);
            var result = await service.RunAsync("env", null, Ct);
            Assert.True(result.Succeeded, result.Error ?? output.Text(TaskService.ChannelName));
            Assert.Contains("[unset|/opt/sdk-of-cscode]", output.Text(TaskService.ChannelName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(arch, previous.Item1);
            Environment.SetEnvironmentVariable("DOTNET_ROOT", previous.Item2);
        }
    }
}
