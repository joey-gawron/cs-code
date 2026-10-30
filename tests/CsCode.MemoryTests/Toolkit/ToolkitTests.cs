using System.Collections.Concurrent;
using System.Diagnostics;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Profiling;
using CsCode.Profiling.Testing;
using CsCode.Profiling.Testing.Load;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.MemoryTests.Toolkit;

/// <summary>
/// A dedicated thread with a FIFO job queue: the "simulated UI thread" of the Core-level responsiveness subset (MUST 9) and of these toolkit tests.
/// <see cref="Post"/> is what a real dispatcher's post is to the probes.
/// </summary>
public sealed class SimulatedUiThread : IDisposable
{
    private readonly BlockingCollection<Action> _jobs = new();
    private readonly Thread _thread;

    public SimulatedUiThread()
    {
        _thread = new Thread(() =>
        {
            foreach (var job in _jobs.GetConsumingEnumerable()) job();
        }) { IsBackground = true, Name = "simulated-ui" };
        _thread.Start();
    }

    public int ManagedThreadId => _thread.ManagedThreadId;

    public void Post(Action job)
    {
        if (!_jobs.IsAddingCompleted) _jobs.Add(job);
    }

    public Task InvokeAsync(Action job)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { job(); done.SetResult(); } catch (Exception ex) { done.SetException(ex); } });
        return done.Task;
    }

    public void Dispose()
    {
        _jobs.CompleteAdding();
        _thread.Join();
        _jobs.Dispose();
    }
}

/// <summary>MUST 4 and 9: the leak, census, soak and responsiveness toolkit catches what it is for and stays quiet otherwise.</summary>
public sealed class ToolkitTests
{
    private sealed class Widget { public byte[] Payload { get; } = new byte[1024]; }

    [Fact]
    public void LeakProbe_counts_only_what_is_still_reachable()
    {
        var probe = new LeakProbe<Widget>();
        var kept = new List<Widget>();
        Fill(probe, kept);
        probe.AssertAliveAtMost(3);
        var ex = Assert.Throws<MemoryAssertionException>(() => probe.AssertAliveAtMost(2, "three are kept on purpose"));
        Assert.Contains("3 of 10 tracked Widget", ex.Message);
        Assert.Contains("kept-", ex.Message);
        GC.KeepAlive(kept);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Fill(LeakProbe<Widget> probe, List<Widget> kept)
    {
        for (var i = 0; i < 10; i++)
        {
            var w = probe.Track(new Widget(), i < 3 ? "kept-" + i : "dropped-" + i);
            if (i < 3) kept.Add(w);
        }
        probe.Track(kept[0]); // tracking twice counts once
    }

    [Fact]
    public void HeapCensus_flags_a_leak_and_passes_a_flat_scenario()
    {
        var leaked = new List<byte[]>();
        var leaking = HeapCensus.Run("leaky", _ => leaked.Add(new byte[64 * 1024]), runs: 20, warmup: 3);
        Assert.InRange(leaking.BytesPerRun, 60 * 1024, 70 * 1024);
        Assert.Throws<MemoryAssertionException>(() => leaking.AssertFlat(1024));

        var cache = new Dictionary<int, byte[]>();
        var flat = HeapCensus.Run("bounded", i => { cache[i % 4] = new byte[64 * 1024]; GC.KeepAlive(new byte[200 * 1024]); }, runs: 20, warmup: 5);
        flat.AssertFlat(1024);
        GC.KeepAlive(leaked);
    }

    [Fact]
    public async Task ResourceCensus_sees_threads_and_descriptors_and_waits_for_them_to_go()
    {
        var baseline = ResourceCensus.Capture();
        Assert.True(baseline.Threads > 0 && baseline.FileDescriptors > 0);
        using var stop = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, 6).Select(_ => new Thread(() => stop.Wait()) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        var files = Enumerable.Range(0, 6).Select(_ => File.OpenRead("/proc/self/status")).ToList();
        var busy = ResourceCensus.Capture();
        Assert.True(busy.Threads >= baseline.Threads + 6 && busy.FileDescriptors >= baseline.FileDescriptors + 6, $"{baseline} -> {busy}");
        await Assert.ThrowsAsync<MemoryAssertionException>(() => ResourceCensus.AssertReturnsToBaselineAsync(baseline, tolerance: 2, timeout: TimeSpan.FromMilliseconds(200)));
        stop.Set();
        threads.ForEach(t => t.Join());
        files.ForEach(f => f.Dispose());
        await ResourceCensus.AssertReturnsToBaselineAsync(baseline, tolerance: 2);
    }

    [Fact]
    public void SubscriptionAudit_finds_the_private_emitters_of_a_service_and_a_forgotten_subscription()
    {
        var documents = new DocumentManager();
        var audit = new SubscriptionAudit().Scan(documents);
        Assert.Contains(audit.Probes, p => p.Name == "DocumentManager._opened");
        var baseline = audit.Snapshot();

        var store = new DisposableStore();
        store.Add(documents.Opened.Subscribe(_ => { }));
        store.Dispose();
        audit.AssertNoGrowth(baseline);

        var forgotten = documents.Closed.Subscribe(_ => { });
        var ex = Assert.Throws<MemoryAssertionException>(() => audit.AssertNoGrowth(baseline));
        Assert.Contains("DocumentManager._closed: 0 -> 1", ex.Message);
        forgotten.Dispose();
    }

    [Fact]
    public async Task SoakRunner_reports_a_leak_per_iteration_and_per_hour_and_writes_csv_and_json()
    {
        using var output = new TempDirectory("cscode-soak-test-");
        var leaked = new List<byte[]>();
        // T56: a sample every 20 iterations, not after every one. Each sample is two compacting full collections of the WHOLE test process: 800 of
        // them took 39 s in the full MemoryTests run (the heap left by the app tests), long enough for whatever earlier tests left running to move
        // the process-wide live heap by more than the 1.4 KB/iteration this range allows. 21 exact live-heap samples fit the same slope in ~1 s.
        var report = await SoakRunner.RunAsync(Scenario.Create("leak", _ => leaked.Add(new byte[16 * 1024])),
            new SoakOptions { Iterations = 400, WarmupIterations = 10, SampleInterval = TimeSpan.FromHours(1), SampleEveryIterations = 20, OutputDirectory = output.Path });
        Assert.InRange(report.HeapPerIteration.Slope, 15 * 1024, 18 * 1024);
        Assert.True(report.HeapMbPerHour > 1);
        var ex = Assert.Throws<MemoryAssertionException>(() => report.AssertStable(new SoakThresholds { MaxHeapBytesPerIteration = 1024 }));
        Assert.Contains("live heap grows", ex.Message);
        Assert.True(File.Exists(report.CsvPath) && File.Exists(report.JsonPath));
        Assert.StartsWith("iteration,seconds,working_set", File.ReadLines(report.CsvPath!).First());
        Assert.Contains("HeapMbPerHour", File.ReadAllText(report.JsonPath!));
        GC.KeepAlive(leaked);

        var flat = await SoakRunner.RunAsync(Scenario.Create("flat", _ => GC.KeepAlive(new byte[16 * 1024])),
            new SoakOptions { Iterations = 400, WarmupIterations = 10, SampleInterval = TimeSpan.FromHours(1), SampleEveryIterations = 20, OutputDirectory = "" });
        flat.AssertStable(new SoakThresholds { MaxHeapBytesPerIteration = 256, ThreadTolerance = 6, FdTolerance = 6 });
        Assert.Null(flat.CsvPath);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(1000)]
    [InlineData(16_000)]
    [InlineData(1_000_000)]
    [InlineData(123_456_789)]
    public void LatencyHistogram_buckets_bound_the_value_within_two_percent(long us)
    {
        var index = LatencyHistogram.Index(us);
        var upper = LatencyHistogram.UpperBound(index);
        Assert.True(upper >= us, $"{us} -> bucket {index} upper {upper}");
        Assert.True(upper - us <= Math.Max(0, us * 0.02), $"{us} -> bucket {index} upper {upper}");
        if (index > 0) Assert.True(LatencyHistogram.UpperBound(index - 1) < us);
    }

    [Fact]
    public void LatencyHistogram_percentiles_and_no_allocation_per_sample()
    {
        var h = new LatencyHistogram();
        for (var i = 1; i <= 1000; i++) h.RecordMicroseconds(i * 100); // 0.1 .. 100 ms
        var r = h.Report();
        Assert.Equal(1000, r.Samples);
        Assert.InRange(r.P50Ms, 50, 51);
        Assert.InRange(r.P99Ms, 99, 100.5);
        Assert.Equal(100, r.MaxMs);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) h.RecordMicroseconds(i);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task ResponsivenessProbe_measures_queue_latency_and_sees_a_blocked_thread()
    {
        using var ui = new SimulatedUiThread();
        using var probe = new ResponsivenessProbe(ui.Post).Start();
        await Task.Delay(300);
        var calm = probe.Report();
        Assert.True(calm.Samples >= 20, calm.ToString());
        Assert.True(calm.P99Ms < 5, calm.ToString());

        probe.Reset();
        ui.Post(() => Thread.Sleep(120)); // a 120 ms callback
        await Task.Delay(400);
        var report = await probe.StopAsync();
        Assert.InRange(report.MaxMs, 100, 200);
        Assert.Equal(0, report.Pending);
        var ex = Assert.Throws<MemoryAssertionException>(() => report.AssertWithin(16, 50));
        Assert.Contains("dispatcher latency over budget", ex.Message);
    }

    [Fact]
    public async Task TypingLatencyProbe_times_key_to_mutation_through_the_thread_queue()
    {
        using var ui = new SimulatedUiThread();
        var doc = new InMemoryTextDocument(new Uri("file:///typing.txt"), "plaintext");
        TypingLatencyProbe? probe = null;
        probe = new TypingLatencyProbe(ui.Post, c => doc.ApplyEdits([TextEdit.Insert(doc.PositionAt(doc.Length), c.ToString())])) { UseMutationSignal = true };
        using var sub = doc.Changed.Subscribe(_ => probe.NotifyMutated());
        ui.Post(() => Thread.Sleep(60)); // the first keys wait behind a slow job
        await probe.TypeAsync("hello world", TimeSpan.FromMilliseconds(5));
        var report = await probe.DrainAsync();
        Assert.Equal("hello world", doc.GetText());
        Assert.Equal(11, report.Keys);
        Assert.Equal(0, report.Missing);
        Assert.InRange(report.Total.MaxMs, 40, 200);
        Assert.True(report.Handling.P50Ms < 5, report.ToString()); // (p99 includes the first key's JIT)
        Assert.True(report.Dispatch.MaxMs >= 40, report.ToString()); // the wait is the queue's, not the handler's
    }

    [Fact]
    public async Task ThreadBlockedDetector_reports_blocks_over_the_threshold_and_the_caller_cost_of_an_api()
    {
        using var ui = new SimulatedUiThread();
        using (var detector = new ThreadBlockedDetector(ui.Post, TimeSpan.FromMilliseconds(50)).Start())
        {
            await Task.Delay(100);
            detector.AssertNoBlocks();
            ui.Post(() => Thread.Sleep(150));
            await Task.Delay(400);
            var block = Assert.Single(detector.Blocks);
            Assert.InRange(block.Duration.TotalMilliseconds, 100, 300);
            Assert.Throws<MemoryAssertionException>(() => detector.AssertNoBlocks());
        }
        var cheap = ThreadBlockedDetector.AssertCallerNotBlocked(() => ui.Post(() => Thread.Sleep(5)), TimeSpan.FromMilliseconds(1));
        Assert.True(cheap < TimeSpan.FromMilliseconds(1));
        Assert.Throws<MemoryAssertionException>(() => ThreadBlockedDetector.AssertCallerNotBlocked(() => Thread.Sleep(20), TimeSpan.FromMilliseconds(5), repeat: 3));
    }

    [Fact]
    public async Task GcPauseRecorder_records_the_pauses_of_induced_collections()
    {
        using var recorder = new GcPauseRecorder();
        for (var i = 0; i < 5; i++) GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        await Task.Delay(1500); // the runtime's events arrive asynchronously
        Assert.True(recorder.Pauses.Count >= 5, recorder.Report());
        Assert.True(recorder.Collections.Gen2 >= 5, recorder.Report());
        Assert.Contains("induced", recorder.Report());
    }

    [Fact]
    public async Task Hostile_load_generators_flood_hang_crash_and_burn_and_clean_up_after_themselves()
    {
        // Flooder: paced at the requested rate
        var count = 0L;
        var flood = await Flooder.RunAsync(_ => Interlocked.Increment(ref count), 500, TimeSpan.FromMilliseconds(400));
        Assert.InRange(flood.PerSecond, 300, 700);
        var diags = new CsCode.Core.Languages.DiagnosticService();
        var floodDiags = await Flooder.DiagnosticsAsync(diags, [new Uri("file:///a.cs"), new Uri("file:///b.cs")], 100, 0, TimeSpan.FromMilliseconds(100));
        Assert.True(floodDiags.Messages > 10);
        Assert.Equal(200, diags.GetAll().Count);

        // Hanger: only cancellation ends it
        var hanger = new Hanger();
        using var cts = new CancellationTokenSource();
        var hung = hanger.HangAsync<int>(cts.Token).AsTask();
        await Task.Delay(50);
        Assert.False(hung.IsCompleted);
        Assert.Equal(1, hanger.Waiting);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hung);

        // CrashLooper: a provider that throws, a process that exits at once
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CrashLooper.Completion().ProvideCompletionAsync(null!, default, default!, default));
        var (file, args) = CrashLooper.Command(7);
        using (var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false })!)
        {
            await p.WaitForExitAsync();
            Assert.Equal(7, p.ExitCode);
        }
        Assert.Equal(3, await CrashLooper.RunAsync(() => throw new InvalidOperationException(), 3));

        // GiantPayload sizes
        Assert.Equal(50_000, GiantPayload.CompletionItems().Count);
        Assert.Equal(5_000_000, GiantPayload.SemanticTokens().Length);
        Assert.InRange(GiantPayload.LogLines(10 * 1024 * 1024).Sum(l => (long)l.Length + 1), 10L * 1024 * 1024, 10L * 1024 * 1024 + 200);

        // CpuBurner: child processes are this instance's and gone after Dispose
        int[] pids;
        using (var burner = CpuBurner.ChildProcesses(2))
        {
            pids = burner.ChildPids.ToArray();
            await Task.Delay(100);
            Assert.All(pids, pid => Assert.True(Directory.Exists($"/proc/{pid}")));
        }
        Assert.All(pids, pid => Assert.True(!Directory.Exists($"/proc/{pid}") || File.ReadAllText($"/proc/{pid}/stat").Contains(") Z", StringComparison.Ordinal)));
        using (CpuBurner.InProcess(2)) await Task.Delay(50);
    }

    [Fact]
    public void ProcessTree_lists_children_with_their_resident_sets()
    {
        using var child = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        try
        {
            var tree = ProcessTree.Descendants(Environment.ProcessId);
            var entry = Assert.Single(tree, e => e.Pid == child.Id);
            Assert.Equal("sleep", entry.Name);
            Assert.True(entry.ResidentBytes > 0);
        }
        finally { child.Kill(); }
    }

    [Fact]
    public void LinearRegression_fits_slope_r2_and_a_confidence_interval()
    {
        double[] x = [0, 1, 2, 3, 4, 5];
        double[] y = [1, 3, 5, 7, 9, 11];
        var fit = LinearRegression.Fit(x, y);
        Assert.Equal(2, fit.Slope, 6);
        Assert.Equal(1, fit.Intercept, 6);
        Assert.Equal(1, fit.RSquared, 6);
        Assert.Equal(0, fit.SlopeConfidence95, 6);
        var noisy = LinearRegression.FitIndexed([10, 12, 9, 11, 10, 12, 9, 11]);
        Assert.True(noisy.SlopeLow < 0 && noisy.SlopeHigh > 0, $"{noisy.Slope} ± {noisy.SlopeConfidence95}");
    }
}
