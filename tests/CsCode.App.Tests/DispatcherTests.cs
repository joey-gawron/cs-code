using CsCode.App.Tests.Infrastructure;
using CsCode.Ui.Threading;
using Xunit;

namespace CsCode.App.Tests;

public class DispatcherTests
{
    [Fact]
    public void PostCoalesced_runs_only_the_latest_action_per_key()
    {
        var ui = new ManualUiDispatcher();
        var seen = new List<int>();
        for (var i = 0; i < 1000; i++) { var n = i; ui.PostCoalesced("k", () => seen.Add(n)); }
        ui.PostCoalesced("other", () => seen.Add(-1));
        Assert.Equal(2, ui.PendingCount); // one runner per key, not 1001 posts
        ui.RunPending();
        Assert.Equal([999, -1], seen);
        Assert.Equal(999, ui.Stats.CoalescedDropped);
    }

    [Fact]
    public void PostCoalesced_after_a_run_schedules_again_and_the_key_table_stays_bounded()
    {
        var ui = new ManualUiDispatcher();
        var count = 0;
        for (var round = 0; round < 3; round++)
        {
            ui.PostCoalesced("k", () => count++);
            ui.RunPending();
        }
        Assert.Equal(3, count);
        for (var i = 0; i < 5000; i++) { ui.PostCoalesced("doc:" + i, () => { }); ui.RunPending(); }
        // Reaching here without OOM is not the proof; the slot table drains: a fresh post must still coalesce correctly afterwards.
        ui.PostCoalesced("doc:1", () => count++);
        ui.PostCoalesced("doc:1", () => count++);
        Assert.Equal(1, ui.PendingCount);
    }

    [Fact]
    public async Task PostBatched_slices_big_updates_across_turns_and_completes()
    {
        var ui = new ManualUiDispatcher();
        var items = Enumerable.Range(0, 5000).ToArray();
        var applied = 0;
        var task = ui.PostBatched(items, _ => { applied++; Thread.SpinWait(4000); }, maxSliceMs: 1);
        var turns = 0;
        while (!task.IsCompleted && turns < 100_000) { ui.RunOneTurn(); turns++; }
        await task;
        Assert.Equal(5000, applied);
        Assert.True(turns > 5, $"expected the update to be chunked over several turns, ran in {turns}");
    }

    [Fact]
    public async Task PostBatched_always_makes_progress_even_with_a_zero_budget()
    {
        var ui = new ManualUiDispatcher();
        var applied = 0;
        var task = ui.PostBatched(new int[50], _ => applied++, maxSliceMs: 0);
        while (!task.IsCompleted) ui.RunOneTurn();
        await task;
        Assert.Equal(50, applied);
    }

    [Fact]
    public async Task PostBatched_propagates_apply_exceptions()
    {
        var ui = new ManualUiDispatcher();
        var task = ui.PostBatched(new[] { 1, 2, 3 }, i => { if (i == 2) throw new InvalidOperationException("boom"); });
        ui.RunPending();
        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
    }

    [Fact]
    public void InvokeAsync_from_another_thread_marshals_to_the_ui_thread()
    {
        var ui = new ManualUiDispatcher();
        var uiThread = Environment.CurrentManagedThreadId;
        var task = Task.Run(() => ui.InvokeAsync(() => Environment.CurrentManagedThreadId));
        Assert.Equal(uiThread, ui.Pump(task));
    }

#if DEBUG // the asserts are [Conditional("DEBUG")] and compile away in Release
    [Fact]
    public void Ui_thread_asserts_throw_on_the_wrong_thread()
    {
        var ui = new ManualUiDispatcher();
        ui.AssertUiThread();
        Assert.Throws<InvalidOperationException>(() => ui.AssertNotUiThread());
        // A dedicated thread: Task.Run from a pool thread may be run inline by the same thread once it yields.
        Exception? error = null;
        var worker = new Thread(() => error = Record.Exception(() => ui.AssertUiThread()));
        worker.Start();
        worker.Join();
        Assert.IsType<InvalidOperationException>(error);
    }
#endif

    [Fact]
    public void Stats_track_the_longest_callback_and_the_current_callback_label()
    {
        var ui = new ManualUiDispatcher();
        string? during = null;
        ui.Post(() => { Thread.Sleep(30); during = ui.CurrentCallback; });
        ui.RunPending();
        Assert.True(ui.Stats.LongestDispatchMs >= 25, $"longest {ui.Stats.LongestDispatchMs}");
        Assert.NotNull(during);
        Assert.Null(ui.CurrentCallback);
    }

    [Fact]
    public async Task Stall_watchdog_reports_a_blocked_ui_thread_and_recovers()
    {
        var ui = new ManualUiDispatcher();
        var log = new List<string>();
        using var dog = new UiStallWatchdog(ui, m => { lock (log) log.Add(m); }, thresholdMs: 60, pollMs: 10);
        dog.Start();
        // Simulated pump: run pending work for a while (healthy), then block for 250 ms (stall), then pump again.
        var until = Environment.TickCount64 + 150;
        while (Environment.TickCount64 < until) { ui.RunPending(); await Task.Delay(5); }
        ui.Post(() => Thread.Sleep(250));
        ui.RunPending();
        until = Environment.TickCount64 + 150;
        while (Environment.TickCount64 < until) { ui.RunPending(); await Task.Delay(5); }
        lock (log)
        {
            Assert.Contains(log, m => m.Contains("has not pumped"));
            Assert.Contains(log, m => m.Contains("recovered"));
        }
        Assert.True(ui.Stats.Stalls >= 1);
    }
}
