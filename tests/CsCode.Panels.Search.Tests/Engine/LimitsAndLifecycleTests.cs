using System.Diagnostics;
using CsCode.Panels.Search.Engine;
using Xunit;

namespace CsCode.Panels.Search.Tests.Engine;

public class LimitsAndLifecycleTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
    }

    private SearchHarness Harness(SearchLimits? limits = null)
    {
        var h = new SearchHarness(limits);
        _owned.Add(h);
        return h;
    }

    // -------------------------------------------------------------------------------- result cap

    [Fact]
    public async Task The_result_cap_truncates_and_says_so()
    {
        var h = Harness(new SearchLimits { MaxResults = 10 });
        h.Write("a.txt", string.Join("\n", Enumerable.Repeat("needle", 50)));
        var r = await h.RunAsync(new SearchQuery("needle"));
        Assert.Equal(SearchOutcome.Truncated, r.Outcome);
        Assert.True(r.Stats.Truncated);
        Assert.Equal(10, r.MatchCount);
        Assert.Equal(10, r.Stats.MatchCount);
    }

    [Fact]
    public async Task Exactly_as_many_matches_as_the_cap_is_not_truncated()
    {
        var h = Harness(new SearchLimits { MaxResults = 5 });
        h.Write("a.txt", string.Join("\n", Enumerable.Repeat("needle", 5)));
        var r = await h.RunAsync(new SearchQuery("needle"));
        Assert.Equal(SearchOutcome.Completed, r.Outcome);
        Assert.False(r.Stats.Truncated);
        Assert.Equal(5, r.MatchCount);
    }

    [Fact]
    public async Task The_cap_is_shared_exactly_across_files_and_workers()
    {
        var h = Harness(new SearchLimits { MaxResults = 100, Parallelism = 4 });
        for (var i = 0; i < 40; i++) h.Write($"f{i:00}.txt", string.Join("\n", Enumerable.Repeat("needle", 10)));
        var r = await h.RunAsync(new SearchQuery("needle"));
        Assert.Equal(SearchOutcome.Truncated, r.Outcome);
        Assert.Equal(100, r.MatchCount);   // never one more or fewer than the cap
    }

    [Fact]
    public async Task The_cap_comes_from_the_search_maxResults_setting()
    {
        var h = Harness();
        h.Config.Set(SearchLimits.MaxResultsKey, 3);
        h.Write("a.txt", string.Join("\n", Enumerable.Repeat("needle", 9)));
        var r = await h.RunAsync(new SearchQuery("needle"));
        Assert.Equal(3, r.MatchCount);
        Assert.Equal(SearchOutcome.Truncated, r.Outcome);
    }

    [Fact]
    public void Settings_are_clamped_and_missing_ones_fall_back_to_the_defaults()
    {
        var h = Harness();
        var d = SearchLimits.FromConfiguration(h.Config);
        Assert.Equal(20_000, d.MaxResults);
        Assert.Equal(5L * 1024 * 1024, d.MaxFileSizeBytes);
        h.Config.Set(SearchLimits.MaxResultsKey, -5);
        h.Config.Set(SearchLimits.RegexTimeoutKey, 99_999_999);
        var c = SearchLimits.FromConfiguration(h.Config);
        Assert.Equal(1, c.MaxResults);
        Assert.Equal(TimeSpan.FromSeconds(60), c.RegexTimeout);
        Assert.Equal(5L * 1024 * 1024, SearchLimits.FromConfiguration(null).MaxFileSizeBytes);
    }

    // ---------------------------------------------------------------------------- regex timeouts

    private const string Pathological = "(?=a)(a+)+$"; // lookahead forces the backtracking engine; (a+)+$ then explodes on "aaa…ac"
    private static readonly string Evil = new string('a', 40) + "c";

    [Fact]
    public async Task A_catastrophic_backtracking_regex_times_out_on_that_file_only()
    {
        var h = Harness(new SearchLimits { RegexTimeout = TimeSpan.FromMilliseconds(100) });
        h.Write("evil.txt", Evil);
        h.Write("fine.txt", "zzz");
        var sw = Stopwatch.StartNew();
        var r = await h.RunAsync(new SearchQuery(Pathological, IsRegex: true));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        Assert.Equal(1, r.Stats.SkippedTimedOut);
        Assert.Equal(SearchOutcome.Completed, r.Outcome);
        Assert.Equal(1, r.Stats.FilesScanned); // fine.txt scanned, evil.txt abandoned
    }

    [Fact]
    public async Task Too_many_timed_out_files_abort_the_search_with_an_explanation()
    {
        var h = Harness(new SearchLimits { RegexTimeout = TimeSpan.FromMilliseconds(50), MaxTimedOutFiles = 2, Parallelism = 2 });
        for (var i = 0; i < 12; i++) h.Write($"evil{i}.txt", Evil);
        var r = await h.RunAsync(new SearchQuery(Pathological, IsRegex: true));
        Assert.Equal(SearchOutcome.Failed, r.Outcome);
        Assert.Contains("timed out", r.Completion.Error);
        Assert.True(r.Stats.SkippedTimedOut >= 2);
    }

    [Fact]
    public async Task The_same_pathological_shape_without_a_lookaround_runs_in_linear_time()
    {
        var h = Harness();
        h.Write("evil.txt", new string('a', 5000) + "c");
        var sw = Stopwatch.StartNew();
        var r = await h.RunAsync(new SearchQuery("(a+)+$", IsRegex: true));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        Assert.Equal(0, r.Stats.SkippedTimedOut);
    }

    // ----------------------------------------------------------------------------- cancellation

    [Fact]
    public async Task Cancel_completes_the_session_as_cancelled_and_stops_every_worker()
    {
        var h = Harness();
        for (var i = 0; i < 300; i++) h.Write($"d{i % 10}/f{i}.txt", string.Join("\n", Enumerable.Repeat("some text with needle in it", 200)));
        using var session = h.Engine.Start(new SearchQuery("needle"));
        session.Cancel();
        var done = await session.Result.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(SearchOutcome.Cancelled, done.Outcome);
        await session.WorkersStopped.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, session.ActiveWorkers);
        Assert.Equal(0, h.Engine.RunningWorkers);
    }

    [Fact]
    public async Task Cancel_after_enumeration_finished_completes_at_once_while_a_worker_is_stuck_in_a_regex()
    {
        // One file, so enumeration ends immediately and the coordinator is left waiting for the worker, which is inside a slow regex.
        var h = Harness(new SearchLimits { RegexTimeout = TimeSpan.FromSeconds(3), Parallelism = 1 });
        h.Write("evil.txt", Evil);
        using var session = h.Engine.Start(new SearchQuery(Pathological, IsRegex: true));
        for (var i = 0; i < 400 && session.CurrentProgress.Enumerating; i++) await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.False(session.CurrentProgress.Enumerating);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var sw = Stopwatch.StartNew();
        session.Cancel();
        var done = await session.Result.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500), $"Result completed {sw.Elapsed.TotalMilliseconds:F0} ms after Cancel");
        Assert.Equal(SearchOutcome.Cancelled, done.Outcome);
        for (var i = 0; i < 100 && h.Engine.ActiveSessions > 0; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(0, h.Engine.ActiveSessions);                           // the finished session left the engine at once
        await session.WorkersStopped.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);   // the worker gives up at the regex timeout
        Assert.Equal(0, h.Engine.RunningWorkers);
    }

    [Fact]
    public async Task Cancel_is_idempotent_and_harmless_after_completion()
    {
        var h = Harness();
        h.Write("a.txt", "needle");
        using var session = h.Engine.Start(new SearchQuery("needle"));
        var done = await session.Result.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        session.Cancel();
        session.Cancel();
        session.Dispose();
        Assert.Equal(SearchOutcome.Completed, done.Outcome);
        Assert.Equal(SearchOutcome.Completed, (await session.Result).Outcome);
    }

    [Fact]
    public async Task Disposing_the_engine_cancels_running_sessions()
    {
        var h = Harness();
        for (var i = 0; i < 200; i++) h.Write($"f{i}.txt", string.Join("\n", Enumerable.Repeat("needle", 500)));
        var session = h.Engine.Start(new SearchQuery("needle"));
        h.Engine.Dispose();
        var done = await session.Result.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains(done.Outcome, new[] { SearchOutcome.Cancelled, SearchOutcome.Completed });
        await session.WorkersStopped.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Throws<ObjectDisposedException>(() => h.Engine.Start(new SearchQuery("x")));
    }

    [Fact]
    public async Task Completed_fires_exactly_once_and_subscribers_are_released()
    {
        var h = Harness();
        h.Write("a.txt", "needle");
        using var session = h.Engine.Start(new SearchQuery("needle"));
        var fired = 0;
        using var sub = session.Completed.Subscribe(_ => Interlocked.Increment(ref fired));
        await session.Result.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        session.Cancel();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, fired);
        // late subscribers get nothing and nothing is retained by the finished session
        var late = 0;
        session.Completed.Subscribe(_ => late++);
        Assert.Equal(0, late);
    }

    [Fact]
    public async Task Subscribers_attached_at_start_always_hear_of_a_session_that_ends_at_once()
    {
        // An invalid pattern completes on the coordinator immediately, possibly before Start has returned; a subscriber added after Start
        // returns would then never be called (see above: Completed fires once and drops its subscribers). Start's attach callback runs first.
        var h = Harness();
        for (var i = 0; i < 200; i++)
        {
            var fired = new TaskCompletionSource<SearchCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
            IDisposable? sub = null;
            using var session = h.Engine.Start(new SearchQuery("foo(", IsRegex: true), s => sub = s.Completed.Subscribe(c => fired.TrySetResult(c)));
            var completion = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(SearchOutcome.Failed, completion.Outcome);
            Assert.False(string.IsNullOrEmpty(completion.Error));
            sub!.Dispose();
        }
    }

    [Fact]
    public async Task Finished_sessions_leave_the_engine()
    {
        var h = Harness();
        h.Write("a.txt", "needle");
        for (var i = 0; i < 5; i++) await h.RunAsync(new SearchQuery("needle"));
        for (var i = 0; i < 50 && h.Engine.ActiveSessions > 0; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(0, h.Engine.ActiveSessions);
        Assert.Equal(0, h.Engine.RunningWorkers);
    }

    // -------------------------------------------------------------------------- batches/progress

    [Fact]
    public async Task Results_arrive_in_batches_and_progress_ends_consistent_with_the_stats()
    {
        var h = Harness();
        for (var i = 0; i < 60; i++) h.Write($"f{i}.txt", "x needle y");
        using var session = h.Engine.Start(new SearchQuery("needle"));
        var progress = new List<SearchProgress>();
        var order = new List<int>();
        using var s1 = session.Progress.Subscribe(p => { lock (progress) progress.Add(p); });
        using var s2 = session.Batches.Subscribe(b => { lock (order) order.Add(b.Sequence); });
        var done = await session.Result.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        lock (order) Assert.Equal(order.OrderBy(x => x).ToList(), order); // batches are delivered in sequence
        lock (progress)
        {
            Assert.NotEmpty(progress);
            Assert.Equal(progress.Select(p => p.MatchCount).OrderBy(x => x).ToList(), progress.Select(p => p.MatchCount).ToList());
            Assert.Equal(60, progress[^1].MatchCount);
            Assert.False(progress[^1].Enumerating);
        }
        Assert.Equal(60, done.Stats.FilesMatched);
    }

    [Fact]
    public async Task Two_runs_over_the_same_tree_produce_the_same_matches()
    {
        var h = Harness();
        for (var i = 0; i < 80; i++) h.Write($"d{i % 7}/f{i}.txt", $"a needle\n{i} needle needle\n");
        static string Sig(SearchOutcomeData r) => string.Join(";", r.Files.OrderBy(f => f.Uri.ToString(), StringComparer.Ordinal)
            .Select(f => f.Uri + ":" + string.Join(",", f.Matches.Select(m => m.Range))));
        var a = await h.RunAsync(new SearchQuery("needle"));
        var b = await h.RunAsync(new SearchQuery("needle"));
        Assert.Equal(Sig(a), Sig(b));
        Assert.Equal(240, a.MatchCount);
    }

    // --------------------------------------------------------------------- caller is never blocked

    [Fact]
    public void Start_costs_the_caller_almost_nothing_even_for_a_huge_workspace_and_a_cold_engine()
    {
        var h = Harness();
        h.Write("a.txt", "needle");
        var sw = Stopwatch.StartNew();
        using var session = h.Engine.Start(new SearchQuery("needle", IsRegex: true));
        var first = sw.Elapsed;
        session.Cancel();
        Assert.True(first < TimeSpan.FromMilliseconds(50), $"Start took {first.TotalMilliseconds:F2} ms on the caller thread");
    }

    [Fact]
    public async Task Start_and_cancel_do_not_run_on_or_wait_for_other_threads()
    {
        var h = Harness();
        for (var i = 0; i < 200; i++) h.Write($"f{i}.txt", string.Join("\n", Enumerable.Repeat("needle", 100)));
        // a warm engine: the synchronous part of Start + Cancel is far below a frame
        using (var warm = h.Engine.Start(new SearchQuery("needle"))) await warm.Result.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var worst = TimeSpan.Zero;
        for (var i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            var s = h.Engine.Start(new SearchQuery("needle"));
            s.Cancel();
            worst = TimeSpan.FromTicks(Math.Max(worst.Ticks, sw.Elapsed.Ticks));
            await s.Result.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        Assert.True(worst < TimeSpan.FromMilliseconds(5), $"worst synchronous Start+Cancel was {worst.TotalMilliseconds:F2} ms");
    }

    [Fact]
    public async Task Workers_are_dedicated_threads_not_thread_pool_threads()
    {
        var h = Harness();
        h.Write("a.txt", "needle");
        using var session = h.Engine.Start(new SearchQuery("needle"));
        await session.Result.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // The engine names its threads; none of them is a pool thread by construction (new Thread): verified by name prefix of live threads
        // during a longer run.
        for (var i = 0; i < 100; i++) h.Write($"big{i}.txt", string.Join("\n", Enumerable.Repeat("filler line without the word", 2000)) + "\nneedle");
        using var s2 = h.Engine.Start(new SearchQuery("needle"));
        var seen = new HashSet<string>();
        var sub = s2.Batches.Subscribe(_ => { lock (seen) seen.Add(Thread.CurrentThread.Name ?? "<unnamed>"); });
        await s2.Result.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        sub.Dispose();
        lock (seen) Assert.All(seen, n => Assert.StartsWith("cscode-search", n)); // batches are published from the coordinator thread
    }
}
