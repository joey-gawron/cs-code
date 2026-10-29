using System.Diagnostics;
using CsCode.Core.Debugging;
using CsCode.Dap.Session;
using CsCode.Dap.Tests.Fakes;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Dap.Tests;

/// <summary>
/// ARCHITECTURE §8 "own your lifetime": after sessions end, nothing of them may remain — no retained session/client/handler objects,
/// no per-session state in the breakpoint manager, and thread / fd / child-process counts return to baseline.
/// </summary>
[Collection(SerialCollection.Name)]
public class LeakSoakTests
{
    private static int ThreadCount() =>
        OperatingSystem.IsLinux() ? Directory.GetDirectories("/proc/self/task").Length : Process.GetCurrentProcess().Threads.Count;

    private static int FdCount() => OperatingSystem.IsLinux() ? Directory.GetFileSystemEntries("/proc/self/fd").Length : -1;

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            await Task.Delay(100, Ct);
        }
    }

    private static async Task<DebugSession> OneCycleAsync(ServiceHarness h, int i, Uri file, bool echoWork = true)
    {
        var s = (DebugSession)await h.StartAsync(ServiceHarness.Config("s" + i, folder: h.Dir.Uri));
        var adapter = h.AdapterFor(i);
        if (echoWork)
        {
            await adapter.SendOutputAsync($"hello {i}\n", "stdout");
            await adapter.SendStoppedAsync("breakpoint", 1);
            await SessionHarness.Eventually(() => s.State == DebugSessionState.Stopped);
            var frames = await s.GetStackTraceAsync(1, 0, 10, Ct);
            var scopes = await s.GetScopesAsync(frames[0].Id, Ct);
            await s.GetVariablesAsync(scopes[0].VariablesReference, ct: Ct);
            await s.EvaluateAsync("x", frames[0].Id, "watch", Ct);
            await s.StepOverAsync(ct: Ct);
        }
        await s.StopAsync(Ct);
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 0, 10_000, "session removed");
        await s.Disposed.WaitAsync(TimeSpan.FromSeconds(10), Ct); // the service disposes ended sessions in the background: wait for that to finish
        return s;
    }

    [Fact]
    public async Task Fifty_fake_sessions_leave_no_retained_objects_and_threads_fds_and_subscribers_return_to_baseline()
    {
        await using var h = new ServiceHarness();
        var file = new Uri("file:///work/a.cs");
        h.Breakpoints.Toggle(file, 3);
        h.Breakpoints.AddFunction("Main");

        // warm up everything lazy (lane thread, JIT, thread pool, sockets) so the baseline is meaningful
        for (var i = 0; i < 3; i++) await OneCycleAsync(h, i, file);
        await SettleAsync();
        var threads0 = ThreadCount();
        var fds0 = FdCount();
        var subs0 = h.Breakpoints.ChangedSubscriberCount;
        var heap0 = GC.GetTotalMemory(true);

        await LeakAssert.NotRetainedAsync(async () =>
        {
            var sessions = new List<object>();
            for (var i = 3; i < 53; i++) sessions.Add(await OneCycleAsync(h, i, file));
            await Task.Delay(300, Ct); // debounce/flush timers (<= 50 ms) still hold the LAST session's closures for a moment
            return [.. sessions];
        }, maxAttempts: 20);

        await SettleAsync();
        var threads1 = ThreadCount();
        var fds1 = FdCount();
        var heap1 = GC.GetTotalMemory(true);
        TestMetrics.Report($"50 fake sessions: threads {threads0} -> {threads1}, fds {fds0} -> {fds1}, managed heap {heap0 / 1024} KB -> {heap1 / 1024} KB, breakpoint-manager subscribers {subs0} -> {h.Breakpoints.ChangedSubscriberCount}");

        Assert.Empty(h.Service.Sessions);
        Assert.Equal(0, h.Breakpoints.StatusCount); // no per-session verdicts left behind
        Assert.Equal(subs0, h.Breakpoints.ChangedSubscriberCount); // every session unsubscribed from the manager
        Assert.InRange(threads1, 0, threads0 + 6);
        if (fds0 >= 0) Assert.InRange(fds1, 0, fds0 + 8);
        Assert.InRange(heap1 - heap0, long.MinValue, 24L * 1024 * 1024); // flat, not growing per session
        Assert.Null(h.Service.ActiveSession);
        Assert.Null(h.Service.StopLocation);
    }

    [Fact]
    public async Task Ended_sessions_are_not_pinned_by_the_debug_console_history_but_their_lines_remain_labelled()
    {
        await using var h = new ServiceHarness();
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var s = await h.StartAsync(ServiceHarness.Config("history", folder: h.Dir.Uri));
            await h.AdapterFor(0).SendOutputAsync("kept line\n", "stdout");
            await SessionHarness.Eventually(() => h.Service.GetOutputHistory().Count > 0);
            await s.StopAsync(Ct);
            await SessionHarness.Eventually(() => h.Service.Sessions.Count == 0);
            await ((DebugSession)s).Disposed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await Task.Delay(200, Ct);
            return [s];
        }, maxAttempts: 20);

        var line = Assert.Single(h.Service.GetOutputHistory(), e => e.Output.Contains("kept line"));
        Assert.Equal("history", line.Session.Name);
        Assert.Equal(DebugSessionState.Terminated, line.Session.State);
        Assert.Null(line.Session.Parent);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await line.Session.GetThreadsAsync(Ct));
    }

    [Fact]
    public async Task A_crashed_session_is_released_too()
    {
        await using var h = new ServiceHarness();
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var s = await h.StartAsync(ServiceHarness.Config("crash", folder: h.Dir.Uri));
            h.AdapterFor(0).Crash();
            await SessionHarness.Eventually(() => h.Service.Sessions.Count == 0);
            await ((DebugSession)s).Disposed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await Task.Delay(200, Ct);
            return [s];
        }, maxAttempts: 20);
        Assert.Equal(0, h.Breakpoints.StatusCount);
    }

    [Fact]
    public async Task A_session_whose_start_fails_leaves_no_state_behind()
    {
        await using var h = new ServiceHarness(configureHost: host => host.Configure = (a, _) => a.On("launch", _ => throw new FakeAdapterError("nope")));
        h.Breakpoints.Toggle(new Uri("file:///work/a.cs"), 1);
        var before = h.Breakpoints.ChangedSubscriberCount;
        for (var i = 0; i < 10; i++) Assert.Null(await h.Service.StartAsync(ServiceHarness.Config("bad" + i, folder: h.Dir.Uri), Ct));
        await SessionHarness.Eventually(() => h.Service.Sessions.Count == 0);
        Assert.Equal(before, h.Breakpoints.ChangedSubscriberCount);
        Assert.Equal(0, h.Breakpoints.StatusCount);
        Assert.Null(h.Service.ActiveSession);
        foreach (var a in h.Host.Adapters) await a.Transport.Completion.WaitAsync(TimeSpan.FromSeconds(5), Ct); // every failed attempt's connection was closed
    }

    [Fact]
    public async Task Session_objects_created_directly_are_collectable_after_dispose()
    {
        using var lane = new CsCode.Dap.Client.DebugLane("leak-direct");
        await using var mgr = new CsCode.Dap.Breakpoints.BreakpointManager();
        mgr.Toggle(new Uri("file:///x.cs"), 2);
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var objs = new List<object>();
            for (var i = 0; i < 20; i++)
            {
                var adapter = new FakeDebugAdapter();
                var session = new DebugSession(new DebugSessionOptions("d" + i, Cfg.Make(), _ => ValueTask.FromResult<CsCode.Dap.Transport.IDapTransport>(adapter.Transport), mgr, lane));
                await session.StartAsync(Ct);
                await session.DisposeAsync();
                await adapter.DisposeAsync();
                objs.Add(session);
                objs.Add(adapter);
            }
            return [.. objs];
        }, maxAttempts: 20);
        Assert.Equal(0, mgr.StatusCount);
        Assert.Equal(0, mgr.ChangedSubscriberCount);
    }

    [Fact]
    public async Task Debuggee_processes_spawned_for_fifty_sessions_never_outlive_them()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "needs /bin/sh");
        await using var h = new ServiceHarness();
        var pids = new List<int>();
        for (var i = 0; i < 50; i++)
        {
            var s = await h.StartAsync(ServiceHarness.Config("p" + i, folder: h.Dir.Uri));
            var body = await h.AdapterFor(i).RequestFromAdapterAsync("runInTerminal", new DapRunInTerminalArgs { Cwd = "/tmp", Args = ["/bin/sh", "-c", "sleep 300"] });
            pids.Add(body.GetProperty("processId").GetInt32());
            await s.StopAsync(Ct);
        }
        Assert.Equal(50, pids.Count);
        foreach (var pid in pids)
            await SessionHarness.Eventually(() => !SessionReverseRequestTests.ProcessAlive(pid), 10_000, $"debuggee {pid} must be killed with its session");
    }

    [Fact]
    public async Task Disposing_the_service_ends_every_session_and_closes_every_connection()
    {
        var h = new ServiceHarness();
        for (var i = 0; i < 3; i++) await h.StartAsync(ServiceHarness.Config("s" + i, folder: h.Dir.Uri));
        Assert.Equal(3, h.Service.Sessions.Count);
        await h.Service.DisposeAsync();
        Assert.Empty(h.Service.Sessions);
        Assert.Equal(DebugState.Inactive, h.Service.State);
        foreach (var a in h.Host.Adapters) await a.Transport.Completion.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await h.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_the_service_unsubscribes_it_from_the_breakpoint_manager_and_unregisters_its_commands()
    {
        var h = new ServiceHarness();
        Assert.True(h.Commands.Has(CsCode.Core.Platform.KnownCommands.DebugStart));
        Assert.True(h.Commands.Has("pickProcess"));
        var subs = h.Breakpoints.ChangedSubscriberCount;
        await h.Service.DisposeAsync();
        Assert.Equal(subs - 1, h.Breakpoints.ChangedSubscriberCount);
        Assert.False(h.Commands.Has(CsCode.Core.Platform.KnownCommands.DebugStart));
        Assert.False(h.Commands.Has("pickProcess"));
        await h.DisposeAsync();
    }
}

/// <summary>Alias so tests read naturally without importing the protocol namespace here.</summary>
internal sealed class DapRunInTerminalArgs
{
    public string Cwd { get; init; } = "";
    public List<string> Args { get; init; } = [];
}
