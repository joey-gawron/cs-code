using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCode.Core.Debugging;
using CsCode.Core.Remote;
using CsCode.Core.Tasks;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using CsCode.Remote.Transport;
using CsCode.TestDoubles;
using CsCode.Terminal.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>
/// A link the test can cut, like a network that drops: the current connection's carrier (the relay process standing in for ssh) is killed, so the
/// client sees the connection die and reconnects through a new one. <see cref="Refuse"/> makes later connection attempts fail (the network stays
/// down). Every connection attempt is counted.
/// </summary>
public sealed class CuttableTransport(IRemoteTransport inner, CuttableTransport.Control control) : IRemoteTransport
{
    public sealed class Control
    {
        private RemoteChannel? _current;
        public int Connects;
        public volatile bool Refuse;
        internal void Set(RemoteChannel channel) => Volatile.Write(ref _current, channel);

        /// <summary>Drops the current connection (kills its carrier process; the persistent server behind it stays).</summary>
        public Task CutAsync() => Volatile.Read(ref _current) is { } c ? c.StopAsync(TimeSpan.Zero) : Task.CompletedTask;
    }

    public string Description => "cuttable(" + inner.Description + ")";

    public async Task<RemoteChannel> ConnectAsync(IProgress<string>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref control.Connects);
        if (control.Refuse) throw new IOException("network unreachable (test)");
        var channel = await inner.ConnectAsync(progress, ct).ConfigureAwait(false);
        control.Set(channel);
        return channel;
    }
}

/// <summary>
/// R4 through a real persistent loopback server (relay + detached daemon, as over ssh): a dropped link reconnects to the SAME server process and
/// terminals, debug sessions and tasks carry on; file operations wait for the reconnect; a deliberate disconnect ends the server, a lost client is
/// given up after the grace period; a restarted client reattaches to the server a crashed one left behind.
/// </summary>
public class R4Tests
{
    private static CancellationToken Ct => TestEnv.Ct;
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    private static RemoteTestHost Persistent(CuttableTransport.Control control, string? machineSettings = null, Action<RemoteOptions>? options = null, TempDirectory? clientHome = null) =>
        new(options, wrap: t => new CuttableTransport(t, control), r3: true, persistent: Grace, machineSettings: machineSettings, clientHome: clientHome);

    private static async Task<ServerStatus> StatusAsync(RemoteTestHost host) =>
        (await host.Manager.Session!.Peer.InvokeAsync<Empty, ServerStatus>(RemoteMethods.Status, Empty.Value, Ct))!;

    private static async Task ReconnectedAsync(RemoteTestHost host, int connects, CuttableTransport.Control control) =>
        await TestEnv.WaitUntilAsync(() => control.Connects >= connects && host.Manager.Session is { IsOpen: true } && host.Manager.Status.IsConnected,
            TimeSpan.FromSeconds(60), "the client to reconnect");

    [Fact]
    public async Task A_dropped_link_reconnects_to_the_same_server_and_a_running_terminal_continues_without_losing_or_repeating_output()
    {
        var control = new CuttableTransport.Control();
        await using var host = Persistent(control);
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        var first = host.Manager.Session!.Server;
        Assert.Contains(RemoteProtocol.Capabilities.Persistent, first.Capabilities);
        var terminals = host.Services.GetRequiredService<TerminalService>();
        var t = (TerminalSession)await terminals.CreateAsync(TerminalProbe.Bash() with { InitialSize = (80, 700) }, Ct);
        var shell = t.ProcessId;

        // A program that keeps printing while the link is down, then waits for input typed while it was down.
        await t.SendTextAsync("for i in $(seq 1 300); do echo line-$i; sleep 0.01; done; read x; echo got-$x", true, Ct);
        await TerminalProbe.WaitForTextAsync(t, "line-40");
        await control.CutAsync();
        Assert.True(t.Input("typed-while-down\r"u8)); // waits in the channel's ring, sent after the reconnect

        await ReconnectedAsync(host, 2, control);
        var second = host.Manager.Session!.Server;
        Assert.Equal(first.InstanceId, second.InstanceId); // the same server process: resumed
        Assert.Equal(first.ProcessId, second.ProcessId);
        Assert.True(host.Manager.Session!.Resumed);
        Assert.Contains("resumed", host.Manager.Status.Message, StringComparison.Ordinal);

        var screen = await TerminalProbe.WaitForTextAsync(t, "\ngot-typed-while-down", TimeSpan.FromSeconds(60)); // the output, not the echo
        var lines = Regex.Matches(screen, @"^line-(\d+)$", RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.True(Enumerable.Range(1, 300).SequenceEqual(lines), // every line once, in order: nothing lost, nothing replayed twice
            $"missing: {string.Join(',', Enumerable.Range(1, 300).Except(lines))}; repeated: {string.Join(',', lines.GroupBy(l => l).Where(g => g.Count() > 1).Select(g => g.Key))}; count {lines.Length}\n{screen}");
        Assert.False(t.HasExited);
        Assert.Equal(shell, t.ProcessId);
        Assert.Equal(2, (await StatusAsync(host)).Connections);
        await terminals.KillAsync(t, Ct);
    }

    [Fact]
    public async Task A_debug_session_and_a_task_survive_a_dropped_link()
    {
        Assert.SkipUnless(File.Exists("/usr/bin/gdb") && File.Exists("/usr/bin/gcc"), "needs gdb (14+) and gcc");
        var control = new CuttableTransport.Control();
        await using var host = Persistent(control, """{ "cscode.debug.gdbArgs": ["-nx"] }""");
        var source = Path.Combine(host.Workspace.Path, "main.c");
        File.WriteAllText(source, """
            #include <stdio.h>
            int main(void) {
                int a = 1;
                a = a + 1;
                a = a * 10;
                printf("final %d\n", a);
                return 0;
            }
            """);
        R3Tests.Compile(source, Path.Combine(host.Workspace.Path, "main"));
        Directory.CreateDirectory(Path.Combine(host.Workspace.Path, ".vscode"));
        File.WriteAllText(Path.Combine(host.Workspace.Path, ".vscode", "tasks.json"), """
            { "version": "2.0.0", "tasks": [ { "label": "long", "type": "shell", "command": "sleep 3; exit 5" } ] }
            """);
        await host.ConnectAsync();
        await host.OpenWorkspaceAsync();
        var debug = host.Services.GetRequiredService<IDebugService>();
        var uri = host.Remote("main.c");
        debug.Breakpoints.Add(uri, 3); // a = a + 1;
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.SessionStarted.Subscribe(s => s.Terminated.Subscribe(_ => terminated.TrySetResult()));
        var config = DebugConfiguration.FromJson(JsonDocument.Parse("""{ "type": "gdb", "request": "launch", "name": "c", "program": "${workspaceFolder}/main" }""").RootElement, host.Remote());
        var session = await debug.StartAsync(config, Ct) ?? throw new Xunit.Sdk.XunitException("no session");
        await TestEnv.WaitUntilAsync(() => debug.StopLocation is { Line: 3 }, TimeSpan.FromSeconds(60), "the breakpoint");

        var task = host.Services.GetRequiredService<ITaskService>().RunAsync("long", null, Ct).AsTask();
        await Task.Delay(500, Ct);
        await control.CutAsync(); // while stopped in the debugger and while the task runs
        await ReconnectedAsync(host, 2, control);

        // the same gdb session goes on: step, read a variable, run to the end
        var thread = debug.StopLocation!.ThreadId;
        await session.StepOverAsync(thread, Ct);
        await TestEnv.WaitUntilAsync(() => debug.StopLocation is { Line: 4 }, TimeSpan.FromSeconds(30), "the step after the reconnect");
        var frame = (await session.GetStackTraceAsync(thread, 0, 1, Ct))[0];
        Assert.Equal("2", (await session.EvaluateAsync("a", frame.Id, "watch", Ct)).Result);
        await session.ContinueAsync(null, Ct);
        await terminated.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(5, result.ExitCode); // the task kept running on the server and its result was collected after the reconnect
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task File_operations_wait_for_a_reconnect_instead_of_failing()
    {
        var control = new CuttableTransport.Control();
        await using var host = Persistent(control);
        await host.ConnectAsync();
        var uri = host.Remote("saved-while-reconnecting.txt");
        await control.CutAsync();
        await TestEnv.WaitUntilAsync(() => host.Manager.Session is null, TimeSpan.FromSeconds(10), "the client to notice the drop");
        // written while the link is down: the save waits for the reconnect (bounded) and then lands on the remote disk
        await host.FileSystem.WriteFileAsync(uri, "after the reconnect"u8.ToArray(), true, Ct);
        Assert.Equal("after the reconnect", File.ReadAllText(Path.Combine(host.Workspace.Path, "saved-while-reconnecting.txt")));
        Assert.True(control.Connects >= 2);
    }

    [Fact]
    public async Task A_deliberate_disconnect_ends_the_persistent_server_at_once()
    {
        var control = new CuttableTransport.Control();
        await using var host = Persistent(control);
        await host.ConnectAsync();
        var daemon = host.Manager.Session!.Server.ProcessId;
        Assert.True(TestEnv.IsAlive(daemon));
        Assert.Contains("--daemon", R3Tests.Cmdline(daemon));
        await host.Manager.DisconnectAsync(Ct);
        await TestEnv.AssertNoProcessesAsync(host.Marker, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_server_whose_client_never_comes_back_exits_after_the_grace_period()
    {
        var control = new CuttableTransport.Control();
        var marker = "";
        await using (var host = new RemoteTestHost(wrap: t => new CuttableTransport(t, control), r3: true, persistent: TimeSpan.FromSeconds(10)))
        {
            marker = host.Marker;
            await host.ConnectAsync();
            var daemon = host.Manager.Session!.Server.ProcessId;
            control.Refuse = true;
            await control.CutAsync(); // the network goes down for good: the client gives up after its attempts
            await TestEnv.WaitUntilAsync(() => host.Manager.Status.State == RemoteConnectionState.Failed, TimeSpan.FromSeconds(60), "the client to give up");
            Assert.True(TestEnv.IsAlive(daemon), "the server must wait for its client during the grace period");
            var sw = Stopwatch.StartNew();
            await TestEnv.WaitUntilAsync(() => !TestEnv.IsAlive(daemon), TimeSpan.FromSeconds(30), "the server to give up after its grace period");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(25), $"{sw.Elapsed.TotalSeconds:0.0} s");
        }
        await TestEnv.AssertNoProcessesAsync(marker);
    }

    [Fact]
    public async Task A_restarted_client_reattaches_to_the_server_a_crashed_client_left_running()
    {
        using var clientHome = new TempDirectory("cscode-remote-restart-");
        var session = ServerSessionRegistry.NewId();
        var crashed = new CuttableTransport.Control();
        string? instance;
        int daemon;
        var serverData = "";
        // Client A connects, opens a document, then "crashes": its link dies and it never says goodbye (no server/shutdown).
        var a = Persistent(crashed, options: o => o.ServerSession = session, clientHome: clientHome);
        await a.ConnectAsync();
        await a.OpenWorkspaceAsync();
        File.WriteAllText(Path.Combine(a.Workspace.Path, "a.txt"), "from A");
        instance = a.Manager.Session!.Server.InstanceId;
        daemon = a.Manager.Session!.Server.ProcessId;
        serverData = a.ServerDataDir;
        crashed.Refuse = true;
        await crashed.CutAsync();
        await TestEnv.WaitUntilAsync(() => a.Manager.Status.State == RemoteConnectionState.Failed, TimeSpan.FromSeconds(60), "client A to give up");
        Assert.True(TestEnv.IsAlive(daemon));

        // Client B (the restarted CsCode) reaches the same server through the same session id: language servers, documents and state are warm.
        var restarted = new CuttableTransport.Control();
        await using (var b = new RemoteTestHost(o => o.ServerSession = session, wrap: t => new CuttableTransport(t, restarted), r3: true, persistent: Grace, clientHome: clientHome,
                         serverDataDir: serverData, marker: a.Marker))
        {
            await b.ConnectAsync();
            Assert.Equal(instance, b.Manager.Session!.Server.InstanceId);
            Assert.Equal(daemon, b.Manager.Session!.Server.ProcessId);
            var status = await StatusAsync(b);
            Assert.Equal(2, status.Connections);
        }
        // B's orderly exit ended the server; A's leftovers go with the test.
        await TestEnv.AssertNoProcessesAsync(a.Marker);
        await a.DisposeAsync();
    }

    [Fact]
    public void The_session_registry_reuses_only_a_dead_owners_id_and_never_a_live_windows()
    {
        using var dir = new TempDirectory("cscode-remote-registry-");
        var registry = new ServerSessionRegistry(dir.Path);
        var mine = registry.Claim("ssh://me@box/");
        Assert.Equal(mine, registry.Claim("ssh://me@box/")); // stable within a process
        Assert.Matches("^[0-9a-f]{32}$", mine);
        // another process of this user (alive: this one) owns the record: a second client gets a new id
        Assert.NotEqual(mine, new ServerSessionRegistry(dir.Path).Claim("ssh://me@box/"));

        // the owner died (a crashed client): its id is taken over
        var file = Directory.GetFiles(dir.Path).Single();
        using (var dead = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false })!) { dead.WaitForExit(); File.WriteAllText(file, $$"""{"Id":"{{mine}}","Pid":{{dead.Id}},"StartTicks":1}"""); }
        Assert.Equal(mine, new ServerSessionRegistry(dir.Path).Claim("ssh://me@box/"));
        Assert.NotEqual(mine, new ServerSessionRegistry(dir.Path).Claim("ssh://other/")); // another host: its own
    }
}
