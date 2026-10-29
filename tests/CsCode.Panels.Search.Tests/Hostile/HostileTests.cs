using System.Diagnostics;
using CsCode.Panels.Search.Engine;
using CsCode.Panels.Search.ViewModels;
using Xunit;

namespace CsCode.Panels.Search.Tests.Hostile;

/// <summary>
/// The responsiveness / isolation suite (ARCHITECTURE §6): a 200,000-file tree, multi-hundred-MB files, a binary file, a 10 MB line,
/// a million matches in one file and catastrophic regexes, all while a heartbeat measures the (simulated) UI thread.
/// Budgets from the brief: dispatcher latency p99 below 16 ms and max below 50 ms; cancel below 50 ms; the result cap honoured; memory
/// bounded; no worker left running.
/// </summary>
public class HostileTests(HostileTreeFixture tree, Xunit.ITestOutputHelper output) : IClassFixture<HostileTreeFixture>
{
    private HostileTree Tree => tree.Tree;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task WaitDoneAsync(FindInFilesViewModel vm, int timeoutMs = 120_000) =>
        await vm.WaitForSearchAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), Ct);

    [Fact]
    public async Task The_heartbeat_probe_really_detects_a_blocked_ui_thread()
    {
        // guards the guard: if this probe could not see a 60 ms stall, the budgets asserted below would prove nothing
        using var ui = new SingleThreadUi();
        using var probe = new HeartbeatProbe(ui);
        await Task.Delay(50, Ct);
        await ui.RunAsync(() => { Thread.Sleep(60); return 0; });
        await Task.Delay(50, Ct);
        var beats = probe.Stop();
        Assert.True(beats.Max >= 50, $"max latency seen was only {beats.Max:F1} ms");
    }

    [Fact]
    public async Task Searching_200k_files_keeps_the_ui_thread_responsive()
    {
        using var rig = new HostileRig(Tree.Many);
        var vm = await rig.NewVmAsync();
        using var probe = new HeartbeatProbe(rig.Ui);
        var sw = Stopwatch.StartNew();
        await rig.Ui.RunAsync(() => { vm.Query = "needle"; vm.Start(); return 0; });
        await WaitDoneAsync(vm);
        var beats = probe.Stop();
        output.WriteLine($"200k files: {sw.Elapsed.TotalSeconds:F1} s, heartbeat n={beats.Samples} p50={beats.P50:F2} p99={beats.P99:F2} max={beats.Max:F2} ms");
        Assert.Equal(Tree.NeedleFiles, await rig.Ui.RunAsync(() => vm.TotalFiles));
        Assert.True(beats.Samples > 100, "the probe must actually have run");
        Assert.True(beats.P99 < 16, $"dispatcher latency p99 {beats.P99:F2} ms");
        Assert.True(beats.Max < 50, $"dispatcher latency max {beats.Max:F2} ms");
        Assert.Equal(200_000, vm.LastCompletion!.Stats.FilesScanned);
        await rig.Ui.RunAsync(() => { vm.Dispose(); return 0; });
    }

    [Fact]
    public async Task Cancelling_a_search_of_200k_files_completes_within_50_ms_and_leaves_no_worker_running()
    {
        using var rig = new HostileRig(Tree.Many);
        using var session = rig.Engine.Start(new SearchQuery("this text is nowhere"));
        await Task.Delay(400, Ct);                       // well into the scan
        Assert.False(session.IsCompleted);
        var sw = Stopwatch.StartNew();
        session.Cancel();
        var done = await session.Result.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var latency = sw.Elapsed;
        await session.WorkersStopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        output.WriteLine($"cancel -> completed {latency.TotalMilliseconds:F1} ms, workers stopped {sw.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(SearchOutcome.Cancelled, done.Outcome);
        Assert.True(latency < TimeSpan.FromMilliseconds(50), $"cancel took {latency.TotalMilliseconds:F1} ms");
        Assert.Equal(0, rig.Engine.RunningWorkers);
    }

    [Fact]
    public async Task A_million_matches_in_one_file_stop_at_the_cap_quickly_and_keep_the_ui_responsive()
    {
        using var rig = new HostileRig(Tree.Specials, new SearchLimits { MaxFileSizeBytes = 64L * 1024 * 1024 });
        var vm = await rig.NewVmAsync();
        using var probe = new HeartbeatProbe(rig.Ui);
        var sw = Stopwatch.StartNew();
        await rig.Ui.RunAsync(() => { vm.Query = "x needle"; vm.Include = "matches1m.txt"; vm.Start(); return 0; });
        await WaitDoneAsync(vm);
        var beats = probe.Stop();
        output.WriteLine($"1M matches: {sw.Elapsed.TotalSeconds:F2} s, p99={beats.P99:F2} max={beats.Max:F2} ms");
        Assert.True(vm.Truncated);
        Assert.Equal(20_000, vm.TotalMatches);
        Assert.Equal(20_000, vm.Rows.OfType<MatchRow>().Count());   // exactly the cap reached the tree
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.True(beats.P99 < 16 && beats.Max < 50, $"p99={beats.P99:F2} max={beats.Max:F2}");
        await rig.Ui.RunAsync(() => { vm.Dispose(); return 0; });
    }

    [Fact]
    public async Task A_10_megabyte_line_is_scanned_without_ballooning_memory()
    {
        using var rig = new HostileRig(Tree.Specials, new SearchLimits { MaxFileSizeBytes = 64L * 1024 * 1024 });
        GC.Collect();
        var baseline = GC.GetTotalMemory(true);
        long peak = baseline;
        using var cts = new CancellationTokenSource();
        var sampler = Task.Run(async () => { while (!cts.IsCancellationRequested) { peak = Math.Max(peak, GC.GetTotalMemory(false)); await Task.Delay(5); } }, Ct);
        var sw = Stopwatch.StartNew();
        using var session = rig.Engine.Start(new SearchQuery("needle", Include: "line10mb.txt"));
        var done = await session.Result.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await cts.CancelAsync();
        await sampler;
        output.WriteLine($"10 MB line: {sw.Elapsed.TotalMilliseconds:F0} ms, heap growth {(peak - baseline) / 1024 / 1024} MB");
        Assert.Equal(SearchOutcome.Completed, done.Outcome);
        Assert.True(done.Stats.MatchCount >= 2, $"matches: {done.Stats.MatchCount}");   // the needles on either side of the split
        Assert.True(peak - baseline < 200L * 1024 * 1024, $"heap grew {(peak - baseline) / 1024 / 1024} MB");
    }

    [Fact]
    public async Task Catastrophic_regexes_finish_in_linear_time_on_hostile_input()
    {
        using var rig = new HostileRig(Tree.Specials);
        var sw = Stopwatch.StartNew();
        using var session = rig.Engine.Start(new SearchQuery("(a+)+$", IsRegex: true, Include: "evil.txt"));
        var done = await session.Result.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        output.WriteLine($"(a+)+$ over 50 x 20k chars: {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal(SearchOutcome.Completed, done.Outcome);
        Assert.Equal(0, done.Stats.SkippedTimedOut);
        Assert.Equal("regex-nonbacktracking", TextMatcher_Kind("(a+)+$"));
    }

    private static string TextMatcher_Kind(string pattern) => TextMatcher.Create(new SearchQuery(pattern, IsRegex: true), new SearchLimits()).Kind;

    [Fact]
    public async Task A_backtracking_regex_on_hostile_input_is_cancellable_and_bounded_by_its_timeout()
    {
        using var rig = new HostileRig(Tree.Specials, new SearchLimits { RegexTimeout = TimeSpan.FromMilliseconds(200), MaxTimedOutFiles = 1 });
        var sw = Stopwatch.StartNew();
        using var session = rig.Engine.Start(new SearchQuery("(?=a)(a+)+$", IsRegex: true, Include: "evil.txt"));
        var done = await session.Result.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        output.WriteLine($"lookahead regex: outcome {done.Outcome} after {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal(SearchOutcome.Failed, done.Outcome);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        await session.WorkersStopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(0, rig.Engine.RunningWorkers);
    }

    [Fact]
    public async Task A_200mb_text_file_is_streamed_not_loaded_and_cancels_within_50_ms()
    {
        using var rig = new HostileRig(Tree.Specials, new SearchLimits { MaxFileSizeBytes = 1L << 30 });
        GC.Collect();
        var baseline = GC.GetTotalMemory(true);
        long peak = baseline;
        using var cts = new CancellationTokenSource();
        var sampler = Task.Run(async () => { while (!cts.IsCancellationRequested) { peak = Math.Max(peak, GC.GetTotalMemory(false)); await Task.Delay(5); } }, Ct);
        var sw = Stopwatch.StartNew();
        using (var full = rig.Engine.Start(new SearchQuery("needle", Include: "big-text.txt")))
        {
            var done = await full.Result.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            output.WriteLine($"big text: {sw.Elapsed.TotalSeconds:F2} s, matches {done.Stats.MatchCount}, heap growth {(peak - baseline) / 1024 / 1024} MB");
            Assert.Equal(30, done.Stats.MatchCount);         // line 0, 100000, ... 2,900,000
        }
        await cts.CancelAsync();
        await sampler;
        Assert.True(peak - baseline < 100L * 1024 * 1024, $"heap grew {(peak - baseline) / 1024 / 1024} MB");

        // and a cancel in the middle of the same file
        using var session = rig.Engine.Start(new SearchQuery("this phrase does not occur", Include: "big-text.txt"));
        await Task.Delay(150, Ct);
        var c = Stopwatch.StartNew();
        session.Cancel();
        var cancelled = await session.Result.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        output.WriteLine($"cancel inside the big file: {c.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(c.Elapsed < TimeSpan.FromMilliseconds(50));
        Assert.Contains(cancelled.Outcome, new[] { SearchOutcome.Cancelled, SearchOutcome.Completed });
        await session.WorkersStopped.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(0, rig.Engine.RunningWorkers);
    }

    [Fact]
    public async Task The_binary_file_and_the_size_limit_cost_next_to_nothing()
    {
        using var rig = new HostileRig(Tree.Specials);
        var sw = Stopwatch.StartNew();
        using var s1 = rig.Engine.Start(new SearchQuery("needle", Include: "big.bin"));
        var tooLarge = await s1.Result.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(1, tooLarge.Stats.SkippedTooLarge);              // 400 MB > 5 MB default: skipped by length, never read
        using var rig2 = new HostileRig(Tree.Specials, new SearchLimits { MaxFileSizeBytes = 1L << 30 });
        using var s2 = rig2.Engine.Start(new SearchQuery("needle", Include: "big.bin"));
        var binary = await s2.Result.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(1, binary.Stats.SkippedBinary);                  // limit raised: the NUL sniff of the first 8 KB rejects it
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Memory_does_not_grow_with_the_number_of_files_scanned()
    {
        using var rig = new HostileRig(Tree.Many);
        GC.Collect();
        var baseline = GC.GetTotalMemory(true);
        long peak = baseline;
        using var cts = new CancellationTokenSource();
        var sampler = Task.Run(async () => { while (!cts.IsCancellationRequested) { peak = Math.Max(peak, GC.GetTotalMemory(false)); await Task.Delay(20); } }, Ct);
        using var session = rig.Engine.Start(new SearchQuery("this text is nowhere"));
        var done = await session.Result.WaitAsync(TimeSpan.FromSeconds(120), Ct);
        await cts.CancelAsync();
        await sampler;
        output.WriteLine($"200k files, no matches: heap growth {(peak - baseline) / 1024 / 1024} MB");
        Assert.Equal(200_000, done.Stats.FilesScanned);
        Assert.True(peak - baseline < 150L * 1024 * 1024, $"heap grew {(peak - baseline) / 1024 / 1024} MB while scanning 200,000 files");
    }

    [Fact]
    public async Task Scanning_uses_dedicated_threads_not_the_shared_thread_pool()
    {
        using var rig = new HostileRig(Tree.Many);
        using (var warm = rig.Engine.Start(new SearchQuery("zz warm"))) await warm.Result.WaitAsync(TimeSpan.FromSeconds(120), Ct);
        var before = ThreadPool.CompletedWorkItemCount;
        using var session = rig.Engine.Start(new SearchQuery("needle"));
        await session.Result.WaitAsync(TimeSpan.FromSeconds(120), Ct);
        var used = ThreadPool.CompletedWorkItemCount - before;
        output.WriteLine($"thread-pool work items during a 200k-file search: {used}");
        // 200,000 files were scanned; the pool is only touched by the test's own awaits and timers
        Assert.True(used < 1000, $"{used} thread-pool work items ran during the search");
    }

    [Fact]
    public async Task A_hundred_starts_and_cancels_leave_no_worker_session_or_thread_behind()
    {
        using var rig = new HostileRig(Tree.Many);
        var threadsBefore = Process.GetCurrentProcess().Threads.Count;
        for (var i = 0; i < 100; i++)
        {
            var s = rig.Engine.Start(new SearchQuery("needle"));
            if (i % 2 == 0) await Task.Delay(i % 7, Ct);
            s.Cancel();
            await s.Result.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await s.WorkersStopped.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }
        for (var i = 0; i < 100 && rig.Engine.ActiveSessions > 0; i++) await Task.Delay(20, Ct);
        Assert.Equal(0, rig.Engine.RunningWorkers);
        Assert.Equal(0, rig.Engine.ActiveSessions);
        var growth = Process.GetCurrentProcess().Threads.Count - threadsBefore;
        Assert.True(growth < 12, $"thread count grew by {growth}");
    }

    [Fact]
    public async Task Typing_while_a_search_floods_results_never_starves_the_ui()
    {
        // 20,000 matches arriving as fast as the engine can produce them (the cap), while the "user" keeps posting input
        using var rig = new HostileRig(Tree.Specials, new SearchLimits { MaxFileSizeBytes = 64L * 1024 * 1024 });
        var vm = await rig.NewVmAsync();
        using var probe = new HeartbeatProbe(rig.Ui);
        await rig.Ui.RunAsync(() => { vm.Query = "needle"; vm.Include = "big-text.txt, matches1m.txt"; vm.Start(); return 0; });
        for (var i = 0; i < 100; i++)
        {
            await rig.Ui.RunAsync(() => { vm.Query = "needle"; return 0; });   // keystrokes on the UI thread
            await Task.Delay(5, Ct);
        }
        await WaitDoneAsync(vm);
        var beats = probe.Stop();
        output.WriteLine($"flood: p99={beats.P99:F2} max={beats.Max:F2} ms n={beats.Samples}");
        Assert.True(beats.P99 < 16 && beats.Max < 50, $"p99={beats.P99:F2} max={beats.Max:F2}");
        await rig.Ui.RunAsync(() => { vm.Dispose(); return 0; });
    }
}

/// <summary>Builds the 200k-file tree once for the whole class and removes it afterwards.</summary>
public sealed class HostileTreeFixture : IDisposable
{
    public HostileTree Tree { get; } = new();
    public void Dispose() => Tree.Dispose();
}
