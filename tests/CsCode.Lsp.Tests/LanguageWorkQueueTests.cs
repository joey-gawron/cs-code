using System.Diagnostics;
using CsCode.Lsp.Runtime;
using Xunit;

namespace CsCode.Lsp.Tests;

public class LanguageWorkQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Occupies every lane thread until released, so queue ordering/shedding can be observed deterministically.</summary>
    private sealed class Blocker : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly CountdownEvent _entered;
        public Blocker(LanguageWorkQueue q)
        {
            _entered = new CountdownEvent(q.ThreadCount);
            var ct = TestContext.Current.CancellationToken;
            for (var i = 0; i < q.ThreadCount; i++)
                q.Post(LanePriority.Interactive, () => { _entered.Signal(); _release.Wait(ct); });
            Assert.True(_entered.Wait(Timeout, ct), "lane threads did not pick up the blockers");
        }
        public void Release() => _release.Set();
        public void Dispose() { _release.Set(); _release.Dispose(); _entered.Dispose(); }
    }

    [Fact]
    public async Task Strict_priority_interactive_before_background_before_idle()
    {
        using var q = new LanguageWorkQueue(threadCount: 1);
        var order = new List<string>();
        using var block = new Blocker(q);
        var t1 = q.Run<int>(LanePriority.Idle, default, _ => { lock (order) order.Add("idle"); return ValueTask.FromResult(1); });
        var t2 = q.Run<int>(LanePriority.Background, default, _ => { lock (order) order.Add("background"); return ValueTask.FromResult(2); });
        var t3 = q.Run<int>(LanePriority.Interactive, default, _ => { lock (order) order.Add("interactive"); return ValueTask.FromResult(3); });
        block.Release();
        await Task.WhenAll(t1.AsTask(), t2.AsTask(), t3.AsTask()).Within();
        Assert.Equal(["interactive", "background", "idle"], order);
    }

    [Fact]
    public async Task Latest_wins_supersedes_queued_item_before_it_runs()
    {
        using var q = new LanguageWorkQueue(threadCount: 1);
        var ran = new List<int>();
        using var block = new Blocker(q);
        var key = new WorkKey("doc", 1);
        var first = q.Run<int>(LanePriority.Interactive, key, _ => { ran.Add(1); return ValueTask.FromResult(1); });
        var second = q.Run<int>(LanePriority.Interactive, key, _ => { ran.Add(2); return ValueTask.FromResult(2); });
        block.Release();
        Assert.Equal(0, await first.Within()); // default(int) => "no result"
        Assert.Equal(2, await second.Within());
        Assert.Equal([2], ran);
        Assert.Equal(1, q.SupersededCount);
    }

    [Fact]
    public async Task Different_keys_do_not_supersede_each_other()
    {
        using var q = new LanguageWorkQueue(threadCount: 1);
        using var block = new Blocker(q);
        var a = q.Run<string>(LanePriority.Interactive, new WorkKey("a", 1), _ => ValueTask.FromResult<string?>("a"));
        var b = q.Run<string>(LanePriority.Interactive, new WorkKey("b", 1), _ => ValueTask.FromResult<string?>("b"));
        var c = q.Run<string>(LanePriority.Interactive, new WorkKey("a", 2), _ => ValueTask.FromResult<string?>("c"));
        block.Release();
        var r = await Task.WhenAll(a.AsTask(), b.AsTask(), c.AsTask()).Within();
        Assert.Equal(["a", "b", "c"], r.Select(x => x!).ToArray());
    }

    [Fact]
    public async Task Running_item_is_cancelled_when_superseded()
    {
        using var q = new LanguageWorkQueue(threadCount: 2);
        var key = new WorkKey("doc", 7);
        var started = new TaskCompletionSource();
        CancellationToken seen = default;
        var first = q.Run<string>(LanePriority.Interactive, key, async ctx =>
        {
            seen = ctx.Token;
            started.SetResult();
            await Task.Delay(Timeout, ctx.Token).ConfigureAwait(false);
            return "never";
        });
        await started.Task.Within();
        var second = q.Run<string>(LanePriority.Interactive, key, _ => ValueTask.FromResult<string?>("second"));
        Assert.Equal("second", await second.Within());
        Assert.Null(await first.Within()); // superseded => null result, not an exception
        Assert.True(seen.IsCancellationRequested);
    }

    [Fact]
    public async Task Caller_cancellation_completes_task_as_cancelled_without_running()
    {
        using var q = new LanguageWorkQueue(threadCount: 1);
        using var block = new Blocker(q);
        using var cts = new CancellationTokenSource();
        var ran = false;
        var t = q.RunAsync<int>(LanePriority.Interactive, default, _ => { ran = true; return ValueTask.FromResult(1); }, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await t.Within());
        block.Release();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(ran);
    }

    [Fact]
    public async Task Queue_depth_is_hard_capped_and_sheds_idle_first()
    {
        using var q = new LanguageWorkQueue(threadCount: 1, capacity: 64);
        using var block = new Blocker(q);
        var idle = new List<ValueTask<int>>();
        for (var i = 0; i < 10_000; i++)
            idle.Add(q.Run<int>(LanePriority.Idle, default, _ => ValueTask.FromResult(1)));
        Assert.True(q.Depth <= 64, $"depth {q.Depth}");
        Assert.True(q.MaxObservedDepth <= 64, $"max depth {q.MaxObservedDepth}");
        Assert.True(q.ShedCount >= 10_000 - 64);

        // Interactive work still gets in (evicting Idle), and runs first.
        var interactive = q.Run<int>(LanePriority.Interactive, default, _ => ValueTask.FromResult(42));
        Assert.True(q.Depth <= 64);
        block.Release();
        Assert.Equal(42, await interactive.Within());
        foreach (var t in idle) await t.Within(); // every shed item completed (no hung awaiters)
    }

    [Fact]
    public async Task Unkeyed_replace_storm_does_not_grow_the_queue()
    {
        using var q = new LanguageWorkQueue(threadCount: 1, capacity: 256);
        using var block = new Blocker(q);
        var key = new WorkKey("doc", 1);
        var tasks = new List<ValueTask<int>>();
        for (var i = 0; i < 50_000; i++) tasks.Add(q.Run<int>(LanePriority.Interactive, key, _ => ValueTask.FromResult(i)));
        Assert.Equal(1, q.Depth);
        block.Release();
        foreach (var t in tasks) await t.Within();
        Assert.Equal(49_999, q.SupersededCount);
    }

    [Fact]
    public async Task Work_runs_on_dedicated_background_lane_threads_not_the_pool()
    {
        using var q = new LanguageWorkQueue(threadCount: 2);
        var info = await q.Run<(bool pool, bool bg, ThreadPriority prio, string? name)>(LanePriority.Interactive, default,
            _ => ValueTask.FromResult<(bool, bool, ThreadPriority, string?)>((Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.IsBackground, Thread.CurrentThread.Priority, Thread.CurrentThread.Name))).Within();
        Assert.False(info.pool);
        Assert.True(info.bg);
        Assert.Equal(ThreadPriority.BelowNormal, info.prio);
        Assert.StartsWith("cscode-lsp-lane", info.name);
    }

    [Fact]
    public async Task Lane_threads_get_a_positive_nice_value_on_linux()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("per-thread nice is Linux-only");
        using var q = new LanguageWorkQueue(threadCount: 1, linuxNice: 7);
        var nice = await q.Run<int>(LanePriority.Interactive, default, _ => ValueTask.FromResult<int>(NativePriority.GetCurrentThreadNice() ?? -99)).Within();
        Assert.Equal(7, nice);
        // and the test thread itself is unaffected (nice is per-thread)
        Assert.NotEqual(7, NativePriority.GetCurrentThreadNice());
    }

    [Fact]
    public async Task ResumeOnLane_returns_continuations_to_a_lane_thread()
    {
        using var q = new LanguageWorkQueue(threadCount: 2);
        var onLane = await q.Run<bool>(LanePriority.Interactive, default, async ctx =>
        {
            await Task.Delay(20).ConfigureAwait(false); // continuation now on the pool
            Assert.True(Thread.CurrentThread.IsThreadPoolThread);
            await ctx.ResumeOnLane();
            return !Thread.CurrentThread.IsThreadPoolThread && LanguageWorkQueue.IsOnLaneThread(q);
        }).Within();
        Assert.True(onLane);
    }

    private sealed class ThrowingSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => throw new InvalidOperationException("posted to the UI context");
        public override void Send(SendOrPostCallback d, object? state) => throw new InvalidOperationException("sent to the UI context");
    }

    [Fact]
    public void Enqueue_cost_on_the_calling_thread_is_tiny_even_when_the_lane_is_saturated()
    {
        using var q = new LanguageWorkQueue(threadCount: 1, capacity: 4096);
        using var block = new Blocker(q);
        double avgMs = 0, maxMs = 0;
        var t = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new ThrowingSyncContext());
            for (var i = 0; i < 200; i++) _ = q.Run<int>(LanePriority.Interactive, new WorkKey("warm", i), _ => ValueTask.FromResult(0)); // warm up JIT
            const int n = 2000;
            var total = Stopwatch.StartNew();
            for (var i = 0; i < n; i++)
            {
                var sw = Stopwatch.StartNew();
                _ = q.Run<int>(LanePriority.Interactive, new WorkKey("doc", i % 7), _ => ValueTask.FromResult(0));
                maxMs = Math.Max(maxMs, sw.Elapsed.TotalMilliseconds);
            }
            avgMs = total.Elapsed.TotalMilliseconds / n;
        });
        t.Start();
        Assert.True(t.Join(Timeout));
        Assert.True(avgMs < 0.1, $"average enqueue {avgMs:F4} ms");
        Assert.True(maxMs < 20, $"worst enqueue {maxMs:F3} ms"); // GC pause tolerance; the budget that matters is the average
    }

    [Fact]
    public async Task Dispose_completes_pending_work_and_joins_threads()
    {
        var q = new LanguageWorkQueue(threadCount: 2);
        var threads = q.Threads.ToArray();
        var block = new Blocker(q);
        var pending = Enumerable.Range(0, 20).Select(i => q.Run<int>(LanePriority.Background, default, _ => ValueTask.FromResult(i))).ToArray();
        var disposer = Task.Run(q.Dispose, TestContext.Current.CancellationToken);
        block.Release();
        await disposer.Within();
        foreach (var p in pending) await p.Within(); // none hangs
        Assert.All(threads, t => Assert.False(t.IsAlive));
        // after dispose, new work is refused but still completes (default)
        Assert.Equal(0, await q.Run<int>(LanePriority.Interactive, default, _ => ValueTask.FromResult(5)).Within());
        block.Dispose();
    }

    [Fact]
    public async Task Unhandled_exceptions_surface_on_the_task_and_do_not_kill_the_lane()
    {
        using var q = new LanguageWorkQueue(threadCount: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await q.Run<int>(LanePriority.Interactive, default, _ => throw new InvalidOperationException("boom")).Within());
        Assert.Equal(9, await q.Run<int>(LanePriority.Interactive, default, _ => ValueTask.FromResult(9)).Within());
        var posted = new TaskCompletionSource<Exception>();
        q.OnUnhandled = e => posted.TrySetResult(e);
        q.Post(LanePriority.Interactive, () => throw new ApplicationException("post"));
        Assert.IsType<ApplicationException>(await posted.Task.Within());
        Assert.Equal(1, await q.Run<int>(LanePriority.Interactive, default, _ => ValueTask.FromResult(1)).Within());
    }

    [Fact]
    public void ShouldShed_reports_pressure_by_priority()
    {
        using var q = new LanguageWorkQueue(threadCount: 1, capacity: 64);
        using var block = new Blocker(q);
        Assert.False(q.ShouldShed(LanePriority.Idle));
        for (var i = 0; i < 20; i++) _ = q.Run<int>(LanePriority.Background, default, _ => ValueTask.FromResult(0));
        Assert.True(q.ShouldShed(LanePriority.Idle));      // > capacity/8
        Assert.False(q.ShouldShed(LanePriority.Background)); // < capacity/2
        for (var i = 0; i < 20; i++) _ = q.Run<int>(LanePriority.Background, default, _ => ValueTask.FromResult(0));
        Assert.True(q.ShouldShed(LanePriority.Background));
        Assert.False(q.ShouldShed(LanePriority.Interactive)); // interactive is never optional
    }
}
