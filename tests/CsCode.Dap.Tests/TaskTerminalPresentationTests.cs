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
/// The task runner's terminal presentation seam: with an <see cref="ITaskTerminalHost"/> a task's process is handed to it (the same program,
/// its command line, the task's environment and the parsed <c>presentation</c>, tasks.json's top-level one merged under the task's), its exit
/// code is the task's result, a terminated run is a cancelled task, problem matchers read the output it reports, a failed silent task is
/// revealed and revealProblems shows the Error List. Without a host (and with one) problem matchers feed the diagnostics.
/// </summary>
public sealed class TaskTerminalPresentationTests
{
    /// <summary>A terminal host that records the request and plays the program's output and exit.</summary>
    private sealed class FakeHost : ITaskTerminalHost
    {
        public readonly ConcurrentQueue<TaskTerminalRequest> Requests = new();
        public Func<TaskTerminalRequest, FakeRun>? Behaviour;

        public ValueTask<ITaskTerminalRun> StartAsync(TaskTerminalRequest request, CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            var run = Behaviour?.Invoke(request) ?? new FakeRun(request, "", 0);
            return ValueTask.FromResult<ITaskTerminalRun>(run);
        }
    }

    private sealed class FakeRun : ITaskTerminalRun
    {
        private readonly TaskCompletionSource<TaskTerminalExit> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reveals;
        public bool Terminated;

        public FakeRun(TaskTerminalRequest request, string output, int? exitCode)
        {
            if (output.Length > 0) request.Output?.Invoke(output);
            if (exitCode is { } code) _done.SetResult(new TaskTerminalExit(code, false));
        }

        public Task<TaskTerminalExit> Completion => _done.Task;
        public void Terminate() { Terminated = true; _done.TrySetResult(new TaskTerminalExit(143, true)); }
        public ValueTask RevealAsync(CancellationToken ct = default) { Interlocked.Increment(ref Reveals); return ValueTask.CompletedTask; }
    }

    private sealed class RecordingViews : IViewActivator
    {
        public readonly ConcurrentQueue<string> Shown = new();
        public ValueTask<bool> ShowViewAsync(string viewId, CancellationToken ct = default) { Shown.Enqueue(viewId); return ValueTask.FromResult(true); }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly TempDirectory Dir = new("daptaskterm-");
        public readonly DebugLane Lane = new("task-term-lane");
        public readonly InMemoryOutputService Output = new();
        public readonly DiagnosticService Diagnostics = new();
        public readonly RecordingViews Views = new();
        public readonly FakeHost Host = new();
        public readonly TaskService Service;

        public Rig(string tasksJson, bool withHost = true)
        {
            Dir.WriteFile(".cscode/tasks.json", tasksJson);
            Service = new TaskService(Lane, new DapPlatform
            {
                Workspace = new FakeWorkspace(Dir.Uri), Output = Output, Diagnostics = Diagnostics, Views = () => Views,
                TaskTerminals = withHost ? () => Host : null,
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

    [Fact]
    public async Task A_shell_task_is_handed_to_the_terminal_with_its_command_line_environment_and_presentation()
    {
        await using var rig = new Rig("""
            {"version":"2.0.0","presentation":{"reveal":"silent","panel":"dedicated","clear":true},
             "tasks":[{"label":"build","type":"shell","command":"make","args":["all","with space"],"options":{"env":{"MODE":"${workspaceFolder}"}},
                       "presentation":{"panel":"new","group":"g1","focus":true,"echo":false,"showReuseMessage":false,"close":true,"revealProblems":"onProblem"}}]}
            """);
        var result = await rig.Service.RunAsync("build", null, Ct);
        Assert.True(result.Succeeded);
        var request = Assert.Single(rig.Host.Requests);
        Assert.Equal("build", request.Label);
        Assert.Equal("/bin/sh", request.FileName);
        Assert.Equal(["-c", "make all 'with space'"], request.Arguments);
        Assert.Equal("make all 'with space'", request.CommandLine);
        Assert.Equal(rig.Dir.Path.TrimEnd('/'), request.WorkingDirectory?.TrimEnd('/'));
        Assert.Equal(rig.Dir.Path.TrimEnd('/'), request.Environment["MODE"]?.TrimEnd('/'));
        Assert.Equal(new TaskPresentation
        {
            Reveal = TaskRevealKind.Silent, Panel = TaskPanelKind.New, Clear = true, Group = "g1", Focus = true, Echo = false, ShowReuseMessage = false,
            Close = true, RevealProblems = TaskRevealProblemsKind.OnProblem,
        }, request.Presentation);
        Assert.Contains("> Executing task in a terminal: build <", rig.Output.Text(TaskService.ChannelName));
    }

    [Fact]
    public async Task Exit_codes_terminated_runs_and_process_tasks()
    {
        await using var rig = new Rig("""{"tasks":[{"label":"fail","command":"false"},{"label":"proc","type":"process","command":"/usr/bin/env","args":["a b"]}]}""");
        rig.Host.Behaviour = r => new FakeRun(r, "", r.Label == "fail" ? 2 : 0);
        var failed = await rig.Service.RunAsync("fail", null, Ct);
        Assert.Equal((2, false, false), (failed.ExitCode, failed.Succeeded, failed.Cancelled));
        Assert.True((await rig.Service.RunAsync("proc", null, Ct)).Succeeded);
        var proc = rig.Host.Requests.Last();
        Assert.Equal(("/usr/bin/env", "/usr/bin/env 'a b'"), (proc.FileName, proc.CommandLine));
        Assert.Equal(["a b"], proc.Arguments);

        // closing the terminal (or Terminate Task) under a running task cancels it; so does cancelling the run
        FakeRun? pending = null;
        rig.Host.Behaviour = r => pending = new FakeRun(r, "", null);
        using var cts = new CancellationTokenSource();
        var run = rig.Service.RunAsync("fail", null, cts.Token).AsTask();
        await SessionHarness.Eventually(() => pending is not null);
        cts.Cancel();
        var cancelled = await run;
        Assert.True(cancelled.Cancelled);
        Assert.True(pending!.Terminated);
    }

    [Fact]
    public async Task Problem_matchers_read_the_terminals_output_and_a_failed_silent_task_is_revealed_with_the_error_list()
    {
        await using var rig = new Rig("""
            {"tasks":[{"label":"cc","command":"cc main.c","problemMatcher":"$gcc","presentation":{"reveal":"silent","revealProblems":"onProblem"}}]}
            """);
        var file = Path.Combine(rig.Dir.Path, "main.c");
        FakeRun? run = null;
        rig.Host.Behaviour = r => run = new FakeRun(r, $"\e[1m{file}:3:7:\e[0m \e[31merror:\e[0m expected ';'\r\n", 1);
        var result = await rig.Service.RunAsync("cc", null, Ct);
        Assert.Equal(1, result.ExitCode);
        var (uri, d) = Assert.Single(rig.Diagnostics.GetAll());
        Assert.Equal(file, uri.LocalPath);
        Assert.Equal(("expected ';'", 2, 6, "gcc"), (d.Message, d.Range.Start.Line, d.Range.Start.Character, d.Source));
        Assert.Equal(1, run!.Reveals); // reveal: silent, and the task failed
        Assert.Contains("errorlist", rig.Views.Shown); // revealProblems: onProblem
    }

    [Fact]
    public async Task A_silent_task_that_succeeds_without_problems_stays_hidden_and_one_that_fails_is_revealed()
    {
        await using var rig = new Rig("""{"tasks":[{"label":"t","command":"x","presentation":{"reveal":"silent"}}]}""");
        var runs = new List<FakeRun>();
        rig.Host.Behaviour = r => { var run = new FakeRun(r, "fine\n", runs.Count == 0 ? 0 : 5); runs.Add(run); return run; };
        await rig.Service.RunAsync("t", null, Ct);
        await rig.Service.RunAsync("t", null, Ct);
        Assert.Equal([0, 1], runs.Select(r => r.Reveals));
        Assert.Empty(rig.Views.Shown); // revealProblems defaults to never
    }

    [Fact]
    public async Task Without_a_terminal_problem_matchers_read_the_processs_output_too()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "needs /bin/sh");
        await using var rig = new Rig("""
            {"tasks":[{"label":"lint","command":"printf '%s\\n' 'src/a.ts(1,2): error TS1005: oops' 1>&2; exit 1","problemMatcher":["$tsc"]}]}
            """, withHost: false);
        var result = await rig.Service.RunAsync("lint", null, Ct);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(rig.Host.Requests);
        Assert.True(rig.Diagnostics.GetAll().Count == 1, rig.Output.Text(TaskService.ChannelName));
        var (uri, d) = Assert.Single(rig.Diagnostics.GetAll());
        Assert.Equal(Path.Combine(rig.Dir.Path, "src", "a.ts"), uri.LocalPath);
        Assert.Equal(("oops", "1005"), (d.Message, d.Code));
        // the next run of the task clears them first
        File.WriteAllText(Path.Combine(rig.Dir.Path, ".cscode", "tasks.json"), """{"tasks":[{"label":"lint","command":"true","problemMatcher":["$tsc"]}]}""");
        Assert.True((await rig.Service.RunAsync("lint", null, Ct)).Succeeded);
        Assert.Empty(rig.Diagnostics.GetAll());
    }
}
