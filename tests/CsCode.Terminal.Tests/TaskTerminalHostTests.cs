using System.Diagnostics;
using System.Text;
using CsCode.Core.Tasks;
using CsCode.Core.Terminal;
using CsCode.Terminal.Sessions;
using CsCode.Terminal.Tasks;
using CsCode.Terminal.Tests.Support;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Terminal.Tests;

/// <summary>
/// Tasks in terminals (<see cref="TaskTerminalHost"/>) with real /bin/sh tasks: a terminal named after the task, the echoed command line, the
/// output handed to the task runner, exit codes, VS Code's presentation rules (shared/dedicated/new reuse, group splits, clear, close,
/// reveal), "press any key to close", terminate, and a task whose terminal is closed under it.
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class TaskTerminalHostTests : IDisposable
{
    private readonly ShellSandbox _sandbox = new();
    private readonly TestConfigurationService _config = new();
    private readonly TerminalService _service;
    private readonly TaskTerminalHost _host;

    public TaskTerminalHostTests()
    {
        _service = new TerminalService(_config, environment: () => _sandbox.Environment());
        _host = new TaskTerminalHost(_service);
    }

    public void Dispose()
    {
        _host.Dispose();
        _service.Dispose();
        _sandbox.Dispose();
    }

    private TaskTerminalRequest Sh(string label, string command, TaskPresentation? presentation = null, Action<string>? output = null) =>
        new(label, ShellSandbox.Sh, ["-c", command], _sandbox.Home, new Dictionary<string, string?> { ["TASK_VAR"] = "from-task" }, presentation ?? new TaskPresentation())
        {
            CommandLine = command,
            Output = output,
        };

    private static async Task<string> ScreenAsync(TerminalSession s, string text, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        var screen = "";
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            screen = await s.InvokeAsync(e =>
            {
                // the scrollback and the screen, soft-wrapped rows joined
                var sb = new StringBuilder();
                for (var line = e.ScrollbackTop; line < e.ScreenTop + e.Rows; line++)
                {
                    if (e.GetRow(line) is not { } row) continue;
                    if (!row.IsWrapped && sb.Length > 0) sb.Append('\n');
                    row.AppendText(sb);
                }
                return sb.ToString();
            });
            if (screen.Contains(text, StringComparison.Ordinal)) return screen;
            await Task.Delay(20);
        }
        Assert.Fail($"'{text}' did not appear:\n{screen}");
        return screen;
    }

    private static TerminalSession SessionOf(ITaskTerminalRun run) => ((TaskTerminalHost.Run)run).Session!;

    [Fact]
    public async Task A_task_runs_in_a_terminal_named_after_it_with_its_command_line_output_and_exit_code()
    {
        var output = new StringBuilder();
        var run = await _host.StartAsync(Sh("build", "echo \"out $TASK_VAR\"; printf 'tail-no-newline'; exit 3", output: t => { lock (output) output.Append(t); }));
        var exit = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new TaskTerminalExit(3, false), exit);
        var session = SessionOf(run);
        Assert.Equal("build", session.Title);
        Assert.True(session.IsTransient);
        Assert.Same(session, _service.ActiveSession);
        var screen = await ScreenAsync(session, "press any key to close it");
        Assert.Contains(" *  Executing task: echo \"out $TASK_VAR\"; printf 'tail-no-newline'; exit 3", screen);
        Assert.Contains("out from-task", screen);
        Assert.Contains($" *  The terminal process \"{ShellSandbox.Sh}, '-c', 'echo \"out $TASK_VAR\"; printf 'tail-no-newline'; exit 3'\" terminated with exit code: 3.", screen);
        // the output reached the task runner, as the program wrote it
        lock (output) Assert.Equal("out from-task\r\ntail-no-newline", output.ToString());
    }

    [Fact]
    public async Task Shared_tasks_reuse_an_idle_task_terminal_and_rename_it()
    {
        var first = await _host.StartAsync(Sh("one", "echo first-output"));
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await _host.StartAsync(Sh("two", "echo second-output"));
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(SessionOf(first), SessionOf(second));
        Assert.Equal("two", SessionOf(second).Title);
        var screen = await ScreenAsync(SessionOf(second), "second-output");
        Assert.Contains("first-output", screen); // the buffer stays
        Assert.Single(_service.Terminals);
    }

    [Fact]
    public async Task A_busy_terminal_is_not_reused_and_dedicated_and_new_panels_keep_their_own()
    {
        var running = await _host.StartAsync(Sh("watch", "sleep 30"));
        var other = await _host.StartAsync(Sh("watch", "echo meanwhile"));
        Assert.NotSame(SessionOf(running), SessionOf(other));
        await other.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        running.Terminate();
        Assert.True((await running.Completion.WaitAsync(TimeSpan.FromSeconds(10))).Terminated);

        var dedicated = new TaskPresentation { Panel = TaskPanelKind.Dedicated };
        var a1 = await _host.StartAsync(Sh("a", "echo a1", dedicated));
        await a1.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var b1 = await _host.StartAsync(Sh("b", "echo b1", dedicated));
        await b1.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(SessionOf(a1), SessionOf(b1)); // another task's dedicated terminal is not taken
        var a2 = await _host.StartAsync(Sh("a", "echo a2", dedicated));
        await a2.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(SessionOf(a1), SessionOf(a2)); // its own is

        var fresh = new TaskPresentation { Panel = TaskPanelKind.New };
        var n1 = await _host.StartAsync(Sh("n", "true", fresh));
        await n1.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var n2 = await _host.StartAsync(Sh("n", "true", fresh));
        await n2.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(SessionOf(n1), SessionOf(n2));
    }

    [Fact]
    public async Task Tasks_of_one_group_split_into_one_terminal_group()
    {
        var grouped = new TaskPresentation { Group = "watchers" };
        var server = await _host.StartAsync(Sh("server", "sleep 30", grouped));
        var client = await _host.StartAsync(Sh("client", "sleep 30", grouped));
        var group = _service.GroupOf(SessionOf(server));
        Assert.NotNull(group);
        Assert.Equal([SessionOf(server), SessionOf(client)], group.Terminals);
        var lone = await _host.StartAsync(Sh("lone", "sleep 30"));
        Assert.NotSame(group, _service.GroupOf(SessionOf(lone)));
        foreach (var r in new[] { server, client, lone }) r.Terminate();
        await Task.WhenAll(server.Completion, client.Completion, lone.Completion).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Clear_empties_a_reused_terminal_and_close_closes_it_when_the_task_ends()
    {
        var first = await _host.StartAsync(Sh("t", "echo old-output"));
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await ScreenAsync(SessionOf(first), "old-output");
        var cleared = await _host.StartAsync(Sh("t", "echo new-output", new TaskPresentation { Clear = true }));
        await cleared.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var screen = await ScreenAsync(SessionOf(cleared), "new-output");
        Assert.DoesNotContain("old-output", screen);

        var removed = new TaskCompletionSource<ITerminal>();
        using var sub = _service.Removed.Subscribe(t => removed.TrySetResult(t));
        var closing = await _host.StartAsync(Sh("c", "echo bye", new TaskPresentation { Close = true, Panel = TaskPanelKind.New }));
        Assert.Equal(0, (await closing.Completion.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode);
        Assert.Same(SessionOf(closing), await removed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Any_key_closes_a_finished_task_terminal()
    {
        var run = await _host.StartAsync(Sh("k", "echo done"));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var session = SessionOf(run);
        await ScreenAsync(session, "press any key to close it");
        var removed = new TaskCompletionSource<ITerminal>();
        using var sub = _service.Removed.Subscribe(t => removed.TrySetResult(t));
        // a mouse or focus report (the pointer moving over it, a click into it) does not close it
        Assert.False(session.Report("\e[I"u8));
        await Task.Delay(100);
        Assert.False(removed.Task.IsCompleted);
        Assert.True(session.Input("x"u8));
        Assert.Same(session, await removed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(_service.Terminals);
    }

    [Fact]
    public async Task Reveal_always_shows_the_terminal_and_silent_or_never_do_not()
    {
        var shows = new List<TerminalPresentation>();
        using var sub = _service.PresentationRequested.Subscribe(p => { lock (shows) shows.Add(p); });
        var always = await _host.StartAsync(Sh("always", "true"));
        await always.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        lock (shows) Assert.Contains(shows, p => ReferenceEquals(p.Terminal, SessionOf(always)) && p.PreserveFocus); // shown, keyboard kept
        lock (shows) shows.Clear();
        var silent = await _host.StartAsync(Sh("silent", "false", new TaskPresentation { Reveal = TaskRevealKind.Silent, Panel = TaskPanelKind.New }));
        await silent.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        lock (shows) Assert.Empty(shows);
        await silent.RevealAsync(); // the task runner reveals a failed silent task
        lock (shows) Assert.Contains(shows, p => ReferenceEquals(p.Terminal, SessionOf(silent)));
        lock (shows) shows.Clear();
        var focused = await _host.StartAsync(Sh("focused", "true", new TaskPresentation { Focus = true, Panel = TaskPanelKind.New }));
        lock (shows) Assert.Contains(shows, p => ReferenceEquals(p.Terminal, SessionOf(focused)) && !p.PreserveFocus);
    }

    [Fact]
    public async Task Terminate_ends_the_process_group_and_killing_the_terminal_ends_its_task()
    {
        var pidFile = _sandbox.Dir.Combine("child.pid");
        var run = await _host.StartAsync(Sh("long", $"sleep 300 & echo $! > '{pidFile}'; wait"));
        var sw = Stopwatch.StartNew();
        while (!(File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0) && sw.ElapsedMilliseconds < 10_000) await Task.Delay(20);
        var child = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.Single(_host.Running);
        run.Terminate();
        var exit = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(exit.Terminated);
        sw.Restart();
        while (Directory.Exists($"/proc/{child}") && sw.ElapsedMilliseconds < 5_000) await Task.Delay(20);
        Assert.False(Directory.Exists($"/proc/{child}"), "the task's background child survived");
        Assert.Empty(_host.Running);
        Assert.DoesNotContain("terminated with exit code", await ScreenAsync(SessionOf(run), "press any key")); // terminating is not a failure

        var killed = await _host.StartAsync(Sh("killed", "sleep 300", new TaskPresentation { Panel = TaskPanelKind.New }));
        await _service.KillAsync(SessionOf(killed));
        Assert.Equal(new TaskTerminalExit(-1, true), await killed.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Finished_and_closed_task_terminals_retain_no_run_or_session()
    {
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var objects = new List<object>();
            for (var i = 0; i < 10; i++)
            {
                var run = await _host.StartAsync(Sh($"t{i}", "echo x", new TaskPresentation { Panel = TaskPanelKind.New, Close = i % 2 == 0 }));
                await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                objects.Add(run);
                objects.Add(SessionOf(run));
                if (i % 2 == 1) await _service.KillAsync(SessionOf(run)); // the others closed themselves (close: true)
            }
            var sw = Stopwatch.StartNew();
            while (_service.Terminals.Count > 0 && sw.ElapsedMilliseconds < 10_000) await Task.Delay(20);
            Assert.Empty(_service.Terminals);
            return objects.ToArray();
        });
        Assert.Empty(_host.Running);
    }

    [Fact]
    public async Task A_program_that_does_not_exist_completes_with_minus_one_and_says_why()
    {
        var run = await _host.StartAsync(new TaskTerminalRequest("missing", "/no/such/program", [], _sandbox.Home, new Dictionary<string, string?>(), new TaskPresentation()));
        Assert.Equal(-1, (await run.Completion.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode);
        await ScreenAsync(SessionOf(run), "/no/such/program");
        // reused for the next task, which fails the same way
        var again = await _host.StartAsync(new TaskTerminalRequest("missing", "/no/such/program", [], _sandbox.Home, new Dictionary<string, string?>(), new TaskPresentation()));
        Assert.Equal(-1, (await again.Completion.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode);
    }
}
