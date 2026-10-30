using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Core.Tasks;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using CsCode.Remote.Transport;
using CsCode.Terminal.Sessions;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>
/// What a reconnect to the persistent server (a real loopback relay + daemon) must put right: files that changed during the outage are looked at
/// again (remote-1), and whatever the client no longer has — watches stopped and task runs cancelled while the link was down, everything of a
/// crashed client process — ends on the server instead of running there unseen (remote-3, remote-6, remote-10).
/// </summary>
public class ReconnectReclaimTests
{
    private static CancellationToken Ct => TestEnv.Ct;
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    private static RemoteTestHost Persistent(CuttableTransport.Control control, bool r3 = false, Action<RemoteOptions>? options = null, TempDirectory? clientHome = null) =>
        new(o => { o.Reconnect = new ReconnectPolicy(10, () => 0.5); options?.Invoke(o); }, wrap: t => new CuttableTransport(t, control), r3: r3, persistent: Grace,
            clientHome: clientHome);

    private static async Task<ServerStatus> StatusAsync(RemoteTestHost host) =>
        (await host.Manager.Session!.Peer.InvokeAsync<Empty, ServerStatus>(RemoteMethods.Status, Empty.Value, Ct))!;

    private static async Task DropAsync(RemoteTestHost host, CuttableTransport.Control control)
    {
        control.Refuse = true;
        await control.CutAsync();
        await TestEnv.WaitUntilAsync(() => host.Manager.Session is null, TimeSpan.FromSeconds(10), "the client to notice the drop");
    }

    private static async Task RestoreAsync(RemoteTestHost host, CuttableTransport.Control control)
    {
        var connects = control.Connects;
        control.Refuse = false;
        await TestEnv.WaitUntilAsync(() => control.Connects > connects && host.Manager.Session is { IsOpen: true } && host.Manager.Status.IsConnected,
            TimeSpan.FromSeconds(60), "the client to reconnect");
        Assert.True(host.Manager.Session!.Resumed);
    }

    /// <summary>A unique duration: the marker of one sleep process (a single argument, as /proc/PID/cmdline separates them with NULs).</summary>
    private static string UniqueSleep(int seconds) => $"{seconds}.{Random.Shared.Next(100_000, 999_999)}";

    [Fact]
    public async Task A_remote_file_that_changed_while_disconnected_is_reported_to_its_own_watcher_after_the_reconnect()
    {
        var control = new CuttableTransport.Control();
        await using var host = Persistent(control);
        await host.ConnectAsync();
        var path = Path.Combine(host.Workspace.Path, "a.txt");
        File.WriteAllText(path, "one");
        var self = host.Remote("a.txt");
        var matched = 0;
        // What an open editor does (EditorDocument.StartWatchingProvider): watch the file's directory, react only to its own URI.
        using var watch = host.Services.GetRequiredService<IFileWatcher>().Watch(host.Remote(), false, null, changes =>
        {
            foreach (var c in changes)
                if (c.Uri == self) Interlocked.Increment(ref matched);
        });
        await TestEnv.WaitUntilAsync(() => host.Manager.Watcher.Count == 1, TimeSpan.FromSeconds(10), "the watch");
        // armed once a change comes through
        await TestEnv.WaitUntilAsync(() =>
        {
            File.WriteAllText(path, "two " + Environment.TickCount64);
            return Volatile.Read(ref matched) > 0;
        }, TimeSpan.FromSeconds(20), "the live change");
        await Task.Delay(500, Ct); // let the last live events settle
        var before = Volatile.Read(ref matched);

        await DropAsync(host, control);
        File.WriteAllText(path, "changed on the server while the client was away");
        await Task.Delay(1000, Ct); // the server's watcher fires now, with nobody connected: that event is gone
        Assert.Equal(before, Volatile.Read(ref matched));
        await RestoreAsync(host, control);
        await TestEnv.WaitUntilAsync(() => Volatile.Read(ref matched) > before, TimeSpan.FromSeconds(15),
            "a change for a.txt itself after the reconnect (a Changed for the directory alone is never matched by an open editor)");
    }

    [Fact]
    public async Task A_watch_stopped_while_disconnected_ends_on_the_server_after_the_reconnect()
    {
        var control = new CuttableTransport.Control();
        await using var host = Persistent(control);
        await host.ConnectAsync();
        Directory.CreateDirectory(Path.Combine(host.Workspace.Path, "one"));
        Directory.CreateDirectory(Path.Combine(host.Workspace.Path, "two"));
        var first = (RemoteWatcher.Handle)host.Manager.Watcher.Watch(host.Remote("one/"), false, null, _ => { });
        var second = (RemoteWatcher.Handle)host.Manager.Watcher.Watch(host.Remote("two/"), false, null, _ => { });
        await Task.WhenAll(first.Ready, second.Ready).WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(2, (await StatusAsync(host)).Watches);

        await DropAsync(host, control);
        first.Dispose(); // a remote tab closed while the badge says Reconnecting: its watch/stop cannot be sent
        await RestoreAsync(host, control);
        Assert.Equal(1, host.Manager.Watcher.Count);
        Assert.Equal(1, (await StatusAsync(host)).Watches);
        second.Dispose();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while ((await StatusAsync(host)).Watches != 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the last watch did not stop on the server");
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task A_task_cancelled_while_disconnected_is_cancelled_on_the_server_after_the_reconnect()
    {
        var control = new CuttableTransport.Control();
        await using var host = Persistent(control, r3: true);
        var sleep = UniqueSleep(41);
        Directory.CreateDirectory(Path.Combine(host.Workspace.Path, ".vscode"));
        File.WriteAllText(Path.Combine(host.Workspace.Path, ".vscode", "tasks.json"),
            $$"""{ "version": "2.0.0", "tasks": [ { "label": "serve", "type": "shell", "command": "sleep {{sleep}}" } ] }""");
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        var tasks = host.Services.GetRequiredService<ITaskService>();
        using var cts = new CancellationTokenSource();
        var run = tasks.RunAsync("serve", null, cts.Token).AsTask();
        await TestEnv.WaitUntilAsync(() => TestEnv.ProcessesWithMarker(sleep).Count > 0, TimeSpan.FromSeconds(30), "the task's process");
        Assert.Equal(1, host.Manager.TaskRuns.Count);

        await DropAsync(host, control);
        cts.Cancel(); // Stop pressed while reconnecting: task/cancel cannot reach the server
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(result.Cancelled);
        Assert.Equal(0, host.Manager.TaskRuns.Count);
        Assert.NotEmpty(TestEnv.ProcessesWithMarker(sleep)); // still running on the server, for now

        await RestoreAsync(host, control);
        Assert.Equal(0, (await StatusAsync(host)).RunningTasks);
        await TestEnv.WaitUntilAsync(() => TestEnv.ProcessesWithMarker(sleep).Count == 0, TimeSpan.FromSeconds(15), "the cancelled task's process to end on the server");
    }

    /// <summary>The client's task presentation, except that the link drops while the first task's terminal is being opened.</summary>
    private sealed class CuttingTaskTerminals(ITaskTerminalHost inner, Func<Task> cut) : ITaskTerminalHost
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async ValueTask<ITaskTerminalRun> StartAsync(TaskTerminalRequest request, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1) await cut();
            return await inner.StartAsync(request, ct);
        }
    }

    [Fact]
    public async Task A_task_whose_terminal_is_being_opened_when_the_link_drops_runs_once_and_reports_its_exit_code()
    {
        var control = new CuttableTransport.Control();
        RemoteTestHost? self = null;
        CuttingTaskTerminals? cutting = null;
        await using var host = new RemoteTestHost(o => o.Reconnect = new ReconnectPolicy(10, () => 0.5),
            services: sc => sc.AddSingleton<ITaskTerminalHost>(sp => cutting = new CuttingTaskTerminals(sp.GetRequiredService<Terminal.Tasks.TaskTerminalHost>(), async () =>
            {
                await control.CutAsync();
                await TestEnv.WaitUntilAsync(() => self!.Manager.Session is null, TimeSpan.FromSeconds(10), "the client to notice the drop");
            })),
            wrap: t => new CuttableTransport(t, control), r3: true, persistent: Grace);
        self = host;
        Directory.CreateDirectory(Path.Combine(host.Workspace.Path, ".vscode"));
        File.WriteAllText(Path.Combine(host.Workspace.Path, ".vscode", "tasks.json"),
            """{ "version": "2.0.0", "tasks": [ { "label": "once", "type": "shell", "command": "echo run >> runs.txt; exit 4" } ] }""");
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        var result = await host.Services.GetRequiredService<ITaskService>().RunAsync("once", null, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(90), Ct);
        Assert.True(control.Connects >= 2, "the link was not cut");
        Assert.Equal(4, result.ExitCode);
        Assert.Null(result.Error);
        Assert.Equal(["run"], File.ReadAllLines(Path.Combine(host.Workspace.Path, "runs.txt"))); // asked twice, run once
        Assert.Equal(1, cutting!.Calls);
    }

    /// <summary>
    /// Holds what the server sends to the client (the link stays up, nothing arrives) until <see cref="Drop"/> — the held bytes are lost with the
    /// connection, as when the network fails right after the server answered — or <see cref="Release"/>: they arrive late (a slow link).
    /// </summary>
    private sealed class InboundHold
    {
        private readonly ManualResetEventSlim _dropped = new(false);
        private readonly ManualResetEventSlim _released = new(false);
        public volatile bool Holding;

        public void Drop() => _dropped.Set();

        public void Release()
        {
            Holding = false;
            _released.Set();
        }

        public IRemoteTransport Wrap(IRemoteTransport inner) => new HeldTransport(inner, this);

        private sealed class HeldTransport(IRemoteTransport inner, InboundHold hold) : IRemoteTransport
        {
            public string Description => inner.Description;
            public async Task<RemoteChannel> ConnectAsync(IProgress<string>? progress, CancellationToken ct) =>
                (await inner.ConnectAsync(progress, ct)).Wrap(input => new HeldStream(input, hold), output => output);
        }

        private sealed class HeldStream(Stream inner, InboundHold hold) : Stream
        {
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                var n = inner.Read(buffer);
                if (n > 0 && hold.Holding)
                {
                    WaitHandle.WaitAny([hold._dropped.WaitHandle, hold._released.WaitHandle], TimeSpan.FromSeconds(60));
                    if (hold._dropped.IsSet) return 0; // the connection ends here: what was held never arrives
                }
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }

    /// <summary>The client's task presentation; the server's answers stop arriving just before the first task's terminal is opened.</summary>
    private sealed class HoldingTaskTerminals(ITaskTerminalHost inner, InboundHold hold) : ITaskTerminalHost
    {
        public ValueTask<ITaskTerminalRun> StartAsync(TaskTerminalRequest request, CancellationToken ct = default)
        {
            hold.Holding = true;
            return inner.StartAsync(request, ct);
        }
    }

    [Fact]
    public async Task A_task_whose_terminal_started_on_the_server_but_whose_answer_was_lost_with_the_link_runs_to_its_own_end_in_that_terminal()
    {
        // T49 after the claim: the server started the task's PTY, then the link failed before the term/create answer arrived. The client asks for
        // the same terminal again on the next connection and gets that PTY (its output replayed); the server did not end it at the reconnect.
        var control = new CuttableTransport.Control();
        var hold = new InboundHold();
        await using var host = new RemoteTestHost(o => o.Reconnect = new ReconnectPolicy(10, () => 0.5),
            services: sc => sc.AddSingleton<ITaskTerminalHost>(sp => new HoldingTaskTerminals(sp.GetRequiredService<Terminal.Tasks.TaskTerminalHost>(), hold)),
            wrap: t => hold.Wrap(new CuttableTransport(t, control)), r3: true, persistent: Grace);
        var sleep = UniqueSleep(4);
        Directory.CreateDirectory(Path.Combine(host.Workspace.Path, ".vscode"));
        File.WriteAllText(Path.Combine(host.Workspace.Path, ".vscode", "tasks.json"),
            $$"""{ "version": "2.0.0", "tasks": [ { "label": "once", "type": "shell", "command": "echo run >> runs.txt; echo hello-from-the-task; sleep {{sleep}}; exit 4" } ] }""");
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        var run = host.Services.GetRequiredService<ITaskService>().RunAsync("once", null, Ct).AsTask();
        var runs = Path.Combine(host.Workspace.Path, "runs.txt");
        await TestEnv.WaitUntilAsync(() => File.Exists(runs) && TestEnv.ProcessesWithMarker(sleep).Count > 0, TimeSpan.FromSeconds(30),
            "the task's program to start on the server");
        Assert.True(hold.Holding);

        control.Refuse = true;
        await control.CutAsync();
        hold.Drop(); // the answer to the term/create is gone with the link
        await TestEnv.WaitUntilAsync(() => host.Manager.Session is null, TimeSpan.FromSeconds(10), "the client to notice the drop");
        hold.Holding = false;
        await RestoreAsync(host, control);

        var result = await run.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Null(result.Error);
        Assert.False(result.Cancelled);
        Assert.Equal(4, result.ExitCode); // the program's own end, not killed at the reconnect
        Assert.Equal(["run"], File.ReadAllLines(runs));
        var terminal = Assert.Single(host.Services.GetRequiredService<TerminalService>().Sessions, s => s.Options.Name == "once");
        await TerminalProbe.WaitForTextAsync(terminal, "hello-from-the-task"); // the output from before the drop, replayed into the client's terminal
        await TestEnv.WaitUntilAsync(() => terminal.HasExited, TimeSpan.FromSeconds(10), "the client's terminal to show the end");
        Assert.Equal(4, terminal.ExitCode);
    }

    [Fact]
    public async Task A_task_cancelled_before_the_server_answered_its_start_is_cancelled_on_the_server()
    {
        // A slow link (or a loaded machine): Stop is pressed while task/start's answer is on its way. The server started the run all the same; it
        // must not go on (its terminal would run unseen, with nobody left to stop it).
        var hold = new InboundHold();
        await using var host = new RemoteTestHost(wrap: hold.Wrap, r3: true);
        var sleep = UniqueSleep(53);
        Directory.CreateDirectory(Path.Combine(host.Workspace.Path, ".vscode"));
        File.WriteAllText(Path.Combine(host.Workspace.Path, ".vscode", "tasks.json"),
            $$"""{ "version": "2.0.0", "tasks": [ { "label": "serve", "type": "shell", "command": "sleep {{sleep}}" } ] }""");
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        var terminals = host.Services.GetRequiredService<TerminalService>();
        using var cts = new CancellationTokenSource();
        hold.Holding = true;
        var run = host.Services.GetRequiredService<ITaskService>().RunAsync("serve", null, cts.Token).AsTask();
        await TestEnv.WaitUntilAsync(() => host.Dump().Contains("Task 'serve' started", StringComparison.Ordinal), TimeSpan.FromSeconds(30),
            "the server to start the run (its answer held back)");
        cts.Cancel();
        Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Cancelled);

        hold.Release(); // the answer arrives late (and the server's request for the task's terminal, when it asked already)
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while ((await StatusAsync(host)).RunningTasks != 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the run was not cancelled on the server");
            await Task.Delay(50, Ct);
        }
        await TestEnv.WaitUntilAsync(() => TestEnv.ProcessesWithMarker(sleep).Count == 0, TimeSpan.FromSeconds(15), "no program of the cancelled task on the server");
        await Task.Delay(1000, Ct);
        Assert.Empty(TestEnv.ProcessesWithMarker(sleep));
        Assert.All(terminals.Sessions.Where(s => s.Options.Name == "serve"), s => Assert.True(s.HasExited)); // a terminal it got, if any, ran nothing
    }

    [Fact]
    public async Task A_task_still_waited_for_keeps_running_across_the_reconnect()
    {
        var control = new CuttableTransport.Control();
        await using var host = Persistent(control, r3: true);
        var sleep = UniqueSleep(3);
        Directory.CreateDirectory(Path.Combine(host.Workspace.Path, ".vscode"));
        File.WriteAllText(Path.Combine(host.Workspace.Path, ".vscode", "tasks.json"),
            $$"""{ "version": "2.0.0", "tasks": [ { "label": "long", "type": "shell", "command": "sleep {{sleep}}; exit 7" } ] }""");
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        var run = host.Services.GetRequiredService<ITaskService>().RunAsync("long", null, Ct).AsTask();
        await TestEnv.WaitUntilAsync(() => host.Manager.TaskRuns.Count == 1 && TestEnv.ProcessesWithMarker(sleep).Count > 0, TimeSpan.FromSeconds(30), "the run");
        await DropAsync(host, control);
        await RestoreAsync(host, control); // task/keep claims it
        var result = await run.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(7, result.ExitCode);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task A_restarted_client_ends_the_terminals_tasks_and_watches_a_crashed_client_left_on_the_server()
    {
        using var clientHome = new TempDirectory("cscode-remote-restart-");
        var session = ServerSessionRegistry.NewId();
        var crashed = new CuttableTransport.Control();
        var terminalSleep = UniqueSleep(98);
        var taskSleep = UniqueSleep(97);
        var a = Persistent(crashed, r3: true, options: o => { o.ServerSession = session; o.Reconnect = new ReconnectPolicy(2, () => 0.5); }, clientHome: clientHome);
        Directory.CreateDirectory(Path.Combine(a.Workspace.Path, ".vscode"));
        File.WriteAllText(Path.Combine(a.Workspace.Path, ".vscode", "tasks.json"),
            $$"""{ "version": "2.0.0", "tasks": [ { "label": "hold", "type": "shell", "command": "sleep {{taskSleep}}" } ] }""");
        await a.ConnectAsync();
        await a.OpenWorkspaceAsync();
        var terminals = a.Services.GetRequiredService<TerminalService>();
        var t = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash(), Ct);
        await t.SendTextAsync("sleep " + terminalSleep, true, Ct);
        _ = a.Services.GetRequiredService<ITaskService>().RunAsync("hold", null, Ct).AsTask();
        var watch = (RemoteWatcher.Handle)a.Manager.Watcher.Watch(a.Remote(".vscode/"), false, null, _ => { });
        await watch.Ready.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await TestEnv.WaitUntilAsync(() => TestEnv.ProcessesWithMarker(taskSleep).Count > 0, TimeSpan.FromSeconds(30), "the task's program");
        await TestEnv.WaitUntilAsync(() => TestEnv.ProcessesWithMarker(terminalSleep).Count > 0, TimeSpan.FromSeconds(30),
            "the terminal's program: " + await TerminalProbe.ScreenAsync(t));
        var before = await StatusAsync(a);
        Assert.True(before.Channels >= 1 && before.RunningTasks == 1 && before.Watches >= 1, $"{before}");
        var serverData = a.ServerDataDir;

        // A "crashes": its link dies for good and it never says goodbye.
        crashed.Refuse = true;
        await crashed.CutAsync();
        await TestEnv.WaitUntilAsync(() => a.Manager.Status.State == RemoteConnectionState.Failed, TimeSpan.FromSeconds(60), "client A to give up");

        // B (the restarted program) reattaches to the same server: nothing of A's can be shown or waited for again, so it all ends there.
        var restarted = new CuttableTransport.Control();
        await using (var b = new RemoteTestHost(o => o.ServerSession = session, wrap: tr => new CuttableTransport(tr, restarted), r3: true, persistent: Grace,
                         clientHome: clientHome, serverDataDir: serverData, marker: a.Marker))
        {
            await b.ConnectAsync();
            Assert.Equal(before.InstanceId, b.Manager.Session!.Server.InstanceId);
            var after = await StatusAsync(b);
            Assert.Equal(0, after.Channels);
            Assert.Equal(0, after.RunningTasks);
            Assert.Equal(b.Manager.Watcher.Count, after.Watches);
            await TestEnv.WaitUntilAsync(() => TestEnv.ProcessesWithMarker(terminalSleep).Count == 0 && TestEnv.ProcessesWithMarker(taskSleep).Count == 0,
                TimeSpan.FromSeconds(15), "the crashed client's terminal and task programs to end");
        }
        watch.Dispose();
        await TestEnv.AssertNoProcessesAsync(a.Marker);
        await a.DisposeAsync();
    }
}
