using System.Diagnostics;
using CsCode.Platform.Io;
using Xunit;

namespace CsCode.Platform.Tests;

public class IoLaneTests
{
    [Fact]
    public async Task Work_runs_on_a_dedicated_lane_thread_not_the_thread_pool()
    {
        using var lane = new IoLane("test.io", maxThreads: 2);
        var (isPool, isLane, name) = await lane.RunAsync(_ =>
            (Thread.CurrentThread.IsThreadPoolThread, IoLane.IsLaneThread, Thread.CurrentThread.Name), TestContext.Current.CancellationToken);
        Assert.False(isPool);
        Assert.True(isLane);
        Assert.StartsWith("test.io-", name);
        Assert.False(IoLane.IsLaneThread); // the test thread is not a lane thread
    }

    [Fact]
    public async Task Caller_is_never_blocked_even_when_every_lane_thread_is_busy()
    {
        using var lane = new IoLane("test.io", maxThreads: 2);
        using var gate = new ManualResetEventSlim();
        using var started = new CountdownEvent(2);
        var blockers = Enumerable.Range(0, 2).Select(_ => lane.RunAsync(_ => { started.Signal(); gate.Wait(); return 0; }, TestContext.Current.CancellationToken)).ToArray();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "lane threads did not start");

        // The lane is now fully saturated: the synchronous part of a call must still be just an enqueue.
        var sw = Stopwatch.StartNew();
        var queued = new List<Task<int>>();
        for (var i = 0; i < 100; i++) queued.Add(lane.RunAsync(_ => 1, TestContext.Current.CancellationToken));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250), $"100 enqueues took {sw.Elapsed.TotalMilliseconds:F1} ms on the caller thread");
        Assert.All(queued, t => Assert.False(t.IsCompleted));

        gate.Set();
        await Task.WhenAll(blockers.Concat(queued)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Parallelism_is_bounded_by_the_thread_count()
    {
        using var lane = new IoLane("test.io", maxThreads: 3);
        var running = 0;
        var peak = 0;
        var tasks = Enumerable.Range(0, 30).Select(_ => lane.RunAsync(_ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            Thread.Sleep(5);
            Interlocked.Decrement(ref running);
        }, TestContext.Current.CancellationToken)).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.InRange(peak, 1, 3);
        Assert.True(lane.ThreadCount <= 3);
    }

    [Fact]
    public async Task Cancelling_a_queued_item_completes_it_immediately_even_when_the_lane_is_jammed()
    {
        using var lane = new IoLane("test.io", maxThreads: 1);
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var blocker = lane.RunAsync(_ => { started.Set(); gate.Wait(); }, TestContext.Current.CancellationToken);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        using var cts = new CancellationTokenSource();
        var ran = false;
        var queued = lane.RunAsync(_ => { ran = true; return 1; }, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        gate.Set();
        await blocker;
        Assert.False(ran);
    }

    [Fact]
    public async Task Exceptions_surface_on_the_returned_task_and_the_lane_keeps_working()
    {
        using var lane = new IoLane("test.io", maxThreads: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lane.RunAsync<int>(_ => throw new InvalidOperationException("boom"), TestContext.Current.CancellationToken));
        Assert.Equal(7, await lane.RunAsync(_ => 7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_full_queue_applies_backpressure_to_the_task_not_to_the_caller()
    {
        using var lane = new IoLane("test.io", maxThreads: 1, capacity: 2);
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var first = lane.RunAsync(_ => { started.Set(); gate.Wait(); return 0; }, TestContext.Current.CancellationToken);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        var sw = Stopwatch.StartNew();
        var more = Enumerable.Range(1, 8).Select(i => lane.RunAsync(_ => i, TestContext.Current.CancellationToken)).ToArray(); // 2 fit, 6 must wait for space
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250), "caller was blocked by a full queue");

        gate.Set();
        var results = await Task.WhenAll(more).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(1, 8).Sum(), results.Sum());
        await first;
    }

    [Fact]
    public async Task Disposing_the_lane_cancels_whatever_is_still_queued()
    {
        var lane = new IoLane("test.io", maxThreads: 1);
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var blocker = lane.RunAsync(_ => { started.Set(); gate.Wait(); }, TestContext.Current.CancellationToken);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var queued = lane.RunAsync(_ => 1, TestContext.Current.CancellationToken);

        var dispose = Task.Run(lane.Dispose, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        gate.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await blocker;
        Assert.True(queued.IsCompleted); // either ran or was cancelled: never left dangling
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
    }
}
