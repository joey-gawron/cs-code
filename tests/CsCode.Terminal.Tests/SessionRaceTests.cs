using System.Diagnostics;
using System.Text;
using CsCode.Core.Terminal;
using CsCode.Terminal.Pty;
using CsCode.Terminal.Sessions;
using CsCode.Terminal.Tests.Support;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Terminal.Tests;

/// <summary>
/// A session's start racing with what the UI does meanwhile (end-pass review): a size the view measured before the program could start reaches
/// the PTY (terminal-1); a terminal closed while it was being created or reused starts nothing (terminal-6); a synchronized update the
/// program never ends stops holding frames after its second (terminal-7); a focus report leaves a scrolled-back view where it is
/// (terminal-11).
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class SessionRaceTests : IDisposable
{
    private readonly ShellSandbox _sandbox = new();
    private readonly TestConfigurationService _config = new();
    private readonly List<SlowHost> _hosts = new();

    public void Dispose() => _sandbox.Dispose();

    /// <summary>A PTY host without a process whose Start takes a while (a remote round trip, a loaded machine) and can run code meanwhile.</summary>
    private sealed class SlowHost : IPtyHost
    {
        private readonly object _gate = new();
        private IPtyListener? _listener;
        public TimeSpan StartDelay = TimeSpan.Zero;
        public Action? DuringStart;
        public readonly List<(int Columns, int Rows)> Sizes = new();
        public int Starts;
        public bool Disposed;
        public readonly MemoryStream WrittenBytes = new();

        public int ProcessId => 4343;
        public bool HasExited { get; private set; }
        public int? ExitCode { get; private set; }
        public long PendingWriteBytes => 0;
        public long MaxPendingWriteBytes => 1 << 20;

        public void Start(PtyStartInfo info, IPtyListener listener)
        {
            Interlocked.Increment(ref Starts);
            lock (_gate) Sizes.Add((info.Columns, info.Rows));
            DuringStart?.Invoke();
            if (StartDelay > TimeSpan.Zero) Thread.Sleep(StartDelay);
            _listener = listener;
        }

        public bool Write(ReadOnlySpan<byte> data, bool urgent = false) { lock (_gate) WrittenBytes.Write(data); return true; }
        public void Resize(int columns, int rows) { lock (_gate) Sizes.Add((columns, rows)); }
        public void Kill(TimeSpan? grace = null) { }
        public string? GetForegroundProcessName() => "slowsh";
        public void Dispose() => Disposed = true;

        /// <summary>Still holds its listener after Dispose, as a real host's reader thread does until it has reaped the program.</summary>
        public bool HasListener => _listener is not null;
        public (int Columns, int Rows) LastSize { get { lock (_gate) return Sizes[^1]; } }
        public void Output(string text) => _listener?.OnOutput(Encoding.UTF8.GetBytes(text));
        public void Exit(int code) { HasExited = true; ExitCode = code; _listener?.OnExited(code); }
    }

    private TerminalService Service(Func<SlowHost>? make = null) =>
        new(_config, environment: () => _sandbox.Environment(), hostFactory: () =>
        {
            var h = make?.Invoke() ?? new SlowHost();
            lock (_hosts) _hosts.Add(h);
            return h;
        });

    private TerminalCreateOptions Fake(string? name = null, bool transient = false) => new()
    {
        Name = name, ShellPath = ShellSandbox.Sh, ShellArgs = [], Cwd = _sandbox.Home, Show = false, IsTransient = transient,
    };

    private TerminalCreateOptions Bash() => new() { ShellPath = ShellSandbox.Bash, ShellArgs = ["--noprofile", "--norc"], Cwd = _sandbox.Home, Show = false };

    private static async Task<string> WaitScreenAsync(TerminalSession s, string text, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        var screen = "";
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            screen = await s.InvokeAsync(e => e.ScreenText());
            if (screen.Contains(text, StringComparison.Ordinal)) return screen;
            await Task.Delay(20);
        }
        Assert.Fail($"'{text}' did not appear on the screen:\n{screen}");
        return screen;
    }

    // ------------------------------------------------------------------ terminal-1: a size measured before the start reaches the PTY

    [Fact]
    public async Task A_size_the_view_measured_before_the_program_started_is_the_ptys_size()
    {
        using var service = Service();
        var session = await service.CreateSessionAsync(Fake(), null, s => s.Resize(120, 40));
        var host = Assert.Single(_hosts);
        Assert.Equal((120, 40), host.Sizes[0]); // the PTY opened at the measured size: the program's first query sees it
        Assert.Equal((120, 40), host.LastSize);
        await session.InvokeAsync(e => 0);
        Assert.Equal((120, 40), await session.InvokeAsync(e => (e.Columns, e.Rows)));
    }

    [Fact]
    public async Task A_resize_while_a_slow_host_is_starting_is_passed_on_once_it_started()
    {
        // a remote split: the pane is laid out while the server starts the shell
        TerminalSession? session = null;
        using var service = Service(() => new SlowHost { StartDelay = TimeSpan.FromMilliseconds(300), DuringStart = () => session!.Resize(110, 33) });
        session = await service.CreateSessionAsync(Fake(), null, s => session = s);
        var host = Assert.Single(_hosts);
        Assert.Equal((110, 33), host.LastSize);
        // a later Resize to the same size is a no-op, and it was not needed
        session.Resize(110, 33);
        Assert.Equal((110, 33), host.LastSize);
    }

    [Fact]
    public async Task A_real_shell_sees_the_size_given_before_it_started()
    {
        using var service = new TerminalService(_config, environment: () => _sandbox.Environment());
        var session = await service.CreateSessionAsync(Bash(), null, s => s.Resize(120, 40));
        await session.SendTextAsync("echo SZ=$(stty size)");
        await WaitScreenAsync(session, "SZ=40 120");
    }

    // ------------------------------------------------------------------ terminal-6: closed while being created or reused

    [Fact]
    public async Task A_terminal_closed_before_its_program_starts_starts_nothing()
    {
        using var service = Service();
        var session = await service.CreateSessionAsync(Fake(), null, s => _ = service.KillAsync(s));
        Assert.True(session.IsDisposed);
        Assert.Null(session.ProcessId);
        Assert.Empty(_hosts); // no host was even made
        Assert.Empty(service.Sessions);
    }

    [Fact]
    public async Task A_terminal_closed_while_its_program_is_being_spawned_disposes_the_new_host()
    {
        TerminalSession? session = null;
        TerminalService? service = null;
        using var owned = service = Service(() => new SlowHost
        {
            StartDelay = TimeSpan.FromMilliseconds(100),
            DuringStart = () => _ = service!.KillAsync(session!), // the trash button, while the remote server starts the shell
        });
        await service.CreateSessionAsync(Fake(), null, s => session = s);
        var host = Assert.Single(_hosts);
        Assert.True(session!.IsDisposed);
        Assert.True(host.Disposed, "the host spawned for a closed terminal was left running");
    }

    [Fact]
    public async Task A_real_shell_is_never_spawned_into_a_closed_terminal()
    {
        using var service = new TerminalService(_config, environment: () => _sandbox.Environment());
        var session = await service.CreateSessionAsync(Bash(), null, s => _ = service.KillAsync(s));
        Assert.True(session.IsDisposed);
        Assert.Null(session.ProcessId);
    }

    [Fact]
    public async Task A_reused_terminal_closed_before_the_restart_runs_nothing()
    {
        using var service = Service();
        var session = await service.CreateSessionAsync(Fake("task", transient: true), null, null);
        var first = Assert.Single(_hosts);
        first.Exit(0);
        Assert.True(await TestWait.UntilAsync(() => session.HasExited));
        var restarted = await service.RestartAsync(session, Fake("task", transient: true), s => _ = service.KillAsync(s));
        Assert.True(session.IsDisposed);
        Assert.Single(_hosts); // no second host
        Assert.True(restarted); // (the program was found; it was not started because its terminal is gone)
    }

    /// <summary>A remote terminal's backend: <see cref="Start"/> is the term/create round trip, which returns when the test lets it.</summary>
    private sealed class FakeBackend : ITerminalBackend
    {
        public readonly ManualResetEventSlim InStart = new();
        public readonly ManualResetEventSlim Release = new();
        public volatile bool Disposed;
        public ITerminalBackendListener? Listener;

        public TerminalBackendStarted Start(TerminalCreateOptions options, int columns, int rows)
        {
            InStart.Set();
            Assert.True(Release.Wait(TimeSpan.FromSeconds(10)), "the test never let the round trip return");
            return new TerminalBackendStarted(7777, "/bin/remote-sh", "/remote/home");
        }

        public void Attach(ITerminalBackendListener listener) => Listener = listener;
        public int ProcessId => 7777;
        public bool HasExited => false;
        public int? ExitCode => null;
        public bool Write(ReadOnlySpan<byte> data, bool urgent = false) => true;
        public long PendingWriteBytes => 0;
        public long MaxPendingWriteBytes => 1 << 20;
        public void Resize(int columns, int rows) { }
        public void Kill(TimeSpan? grace = null) { }
        public string? GetForegroundProcessName() => "remote-sh";
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeBackends(Queue<FakeBackend> backends) : ITerminalBackendProvider
    {
        public ITerminalBackend? TryCreate(TerminalCreateOptions options) { lock (backends) return backends.Dequeue(); }
    }

    [Fact]
    public async Task A_remote_terminal_closed_during_its_start_round_trip_ends_the_program_the_server_started()
    {
        var backend = new FakeBackend();
        using var service = Service();
        service.Backends = () => new FakeBackends(new Queue<FakeBackend>([backend]));
        var creating = service.CreateSessionAsync(Fake(), null, null).AsTask();
        Assert.True(backend.InStart.Wait(TimeSpan.FromSeconds(10)), "the remote start never began");
        // the trash button on the pane (it is shown while the server starts the shell over a slow link)
        await service.KillAsync(Assert.Single(service.Sessions));
        backend.Release.Set();
        var session = await creating;
        Assert.True(session.IsDisposed);
        Assert.Null(backend.Listener); // never attached
        Assert.True(backend.Disposed, "the program the server started for the closed terminal was left running");
        Assert.Empty(_hosts);
    }

    [Fact]
    public async Task A_reused_remote_terminal_closed_during_the_restarts_round_trip_ends_the_new_program()
    {
        var first = new FakeBackend();
        var second = new FakeBackend();
        first.Release.Set();
        using var service = Service();
        service.Backends = () => new FakeBackends(new Queue<FakeBackend>([first, second]));
        var options = Fake("task", transient: true) with { ReuseWhenIdle = true };
        var session = await service.CreateSessionAsync(options, null, null);
        Assert.NotNull(first.Listener);
        first.Listener!.OnExited(0);
        Assert.True(await TestWait.UntilAsync(() => session.HasExited));

        var reusing = service.CreateSessionAsync(options, null, null).AsTask(); // the next task run claims the idle terminal
        Assert.True(second.InStart.Wait(TimeSpan.FromSeconds(10)), "the remote restart never began");
        await service.KillAsync(session);
        second.Release.Set();
        Assert.Same(session, await reusing);
        Assert.True(session.IsDisposed);
        Assert.Null(second.Listener);
        Assert.True(second.Disposed, "the program the server started for the closed terminal was left running");
    }

    // ------------------------------------------------------------------ terminal-9: a closed terminal is not kept alive by its host

    [Fact]
    public async Task A_closed_terminal_is_not_kept_alive_by_its_host_while_the_host_still_waits_for_its_program()
    {
        // A disposed UnixPtyHost's reader thread lives on until it has reaped the program (one that ignores the hang-up: until the SIGKILL after
        // the grace period, longer for one stuck in the kernel), and that thread holds the host and so its listener. SlowHost keeps its listener
        // after Dispose the same way, and the test keeps every host.
        using var service = Service();
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var objects = new List<object>();
            for (var i = 0; i < 3; i++)
            {
                var session = await service.CreateSessionAsync(Fake(), null, null);
                lock (_hosts) _hosts[^1].Output("some output\r\n");
                await session.InvokeAsync(e => 0);
                await service.KillAsync(session);
                Assert.True(session.JoinWorker(TimeSpan.FromSeconds(10)), "the terminal's worker did not end");
                objects.Add(session);
            }
            return objects.ToArray();
        });
        Assert.Equal(3, _hosts.Count);
        Assert.All(_hosts, h => Assert.True(h.Disposed && h.HasListener)); // the hosts are alive and still hold what they were given
        lock (_hosts) _hosts.ForEach(h => h.Exit(0)); // a late reap reaches nothing
    }

    // ------------------------------------------------------------------ terminal-7: synchronized output that never ends

    [Fact]
    public async Task A_synchronized_update_left_on_ends_after_its_second_and_later_output_is_drawn_at_once()
    {
        using var service = Service();
        var session = await service.CreateSessionAsync(Fake(), null, null);
        var host = Assert.Single(_hosts);
        var frames = 0;
        using var sub = session.FrameReady.Subscribe(_ => { Interlocked.Increment(ref frames); session.RequestFrame(); });
        session.RequestFrame();
        host.Output("\e[?2026hdrawing");
        var sw = Stopwatch.StartNew();
        Assert.True(await TestWait.UntilAsync(() => session.LatestSnapshot?.Rows.Any(r => r.GetText().Contains("drawing")) == true, 5_000));
        var held = sw.ElapsedMilliseconds;
        Assert.True(held is >= 800 and < 1_600, $"the update was held {held} ms (one second at most)");
        Assert.False(await session.InvokeAsync(e => e.SynchronizedOutput), "the update the program left on did not end");

        // the program never sent ESU: the next output is drawn as it comes, not a second later
        sw.Restart();
        host.Output("\r\nnext");
        Assert.True(await TestWait.UntilAsync(() => session.LatestSnapshot?.Rows.Any(r => r.GetText().Contains("next")) == true, 5_000));
        Assert.True(sw.ElapsedMilliseconds < 500, $"output after the expired update took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_reused_terminal_and_an_exited_program_end_a_synchronized_update()
    {
        using var service = Service();
        var session = await service.CreateSessionAsync(Fake("task", transient: true), null, null);
        var host = Assert.Single(_hosts);
        host.Output("\e[?2026h");
        Assert.True(await session.InvokeAsync(e => e.SynchronizedOutput));
        await session.InvokeAsync(e => { e.PrepareForNextProgram(); return 0; });
        Assert.False(await session.InvokeAsync(e => e.SynchronizedOutput), "DECSTR (a reused terminal) left the update on");

        host.Output("\e[?2026h");
        Assert.True(await session.InvokeAsync(e => e.SynchronizedOutput));
        host.Exit(137); // killed mid-update
        Assert.True(await TestWait.UntilAsync(() => session.HasExited));
        Assert.False(await session.InvokeAsync(e => e.SynchronizedOutput), "the exited program's update was left on");
    }

    // ------------------------------------------------------------------ terminal-11: focus reports do not scroll

    [Fact]
    public async Task A_focus_report_leaves_a_scrolled_back_view_where_it_is_and_typing_scrolls_to_the_bottom()
    {
        using var service = Service();
        var session = await service.CreateSessionAsync(Fake(), null, null);
        var host = Assert.Single(_hosts);
        host.Output(string.Concat(Enumerable.Range(1, 200).Select(i => $"line {i}\r\n")));
        await session.InvokeAsync(e => { e.ScrollViewportTo(e.ScrollbackTop); return 0; });
        Assert.False(await session.InvokeAsync(e => e.FollowOutput));

        session.Report("\e[I"u8, scrollToBottom: false);
        await session.InvokeAsync(e => 0);
        Assert.False(await session.InvokeAsync(e => e.FollowOutput), "a focus report scrolled the view to the bottom");
        Assert.Contains("\e[I", Encoding.ASCII.GetString(host.WrittenBytes.ToArray()));

        session.Input("x"u8);
        await session.InvokeAsync(e => 0);
        Assert.True(await session.InvokeAsync(e => e.FollowOutput), "typing did not scroll to the bottom");
    }
}

internal static class TestWait
{
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(10);
        return condition();
    }
}
