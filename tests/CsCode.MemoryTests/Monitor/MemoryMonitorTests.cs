using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using CsCode.Core.Diagnostics;
using CsCode.Core.Platform;
using CsCode.Profiling;
using CsCode.Profiling.Testing;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.MemoryTests.Monitor;

/// <summary>MUST 1, 2 and 8: sampling (allocation-free), child processes, the pressure policy driven by the setting, trims, idle trim, metrics, DI.</summary>
public sealed class MemoryMonitorTests
{
    private sealed class RecordingTrimmable(string name, Func<MemoryTrimLevel, long>? trim = null) : IMemoryTrimmable
    {
        public List<(MemoryTrimLevel Level, int Thread)> Calls { get; } = new();
        public string Name => name;
        public long EstimatedBytes => 0;

        public long Trim(MemoryTrimLevel level)
        {
            lock (Calls) Calls.Add((level, Environment.CurrentManagedThreadId));
            return trim?.Invoke(level) ?? 1024;
        }
    }

    private static TestConfigurationService Config(int budgetMb = 0, bool trimOnIdle = true)
    {
        var config = new TestConfigurationService();
        config.Set(SettingKeys.MemoryBudgetMb, budgetMb);
        config.Set(SettingKeys.MemoryTrimOnIdle, trimOnIdle);
        return config;
    }

    [Fact]
    public void A_sample_reads_the_resident_set_threads_and_runtime_counters_and_is_published()
    {
        using var monitor = new MemoryMonitor();
        MemorySnapshot? published = null;
        using var sub = monitor.Sampled.Subscribe(s => published = s);
        var s = monitor.SampleNow();

        Assert.Same(s, published);
        Assert.Same(s, monitor.Current);
        var rss = Environment.WorkingSet;
        Assert.InRange(s.WorkingSetBytes, rss / 2, rss * 2);
        Assert.True(s.PrivateBytes > 0 && s.PrivateBytes <= s.WorkingSetBytes + monitor.CurrentDetails.SwapBytes, $"private {s.PrivateBytes}, rss {s.WorkingSetBytes}");
        Assert.InRange(s.ThreadCount, Directory.GetDirectories("/proc/self/task").Length - 3, Directory.GetDirectories("/proc/self/task").Length + 3);
        Assert.True(s.GcHeapBytes > 0 && s.GcCommittedBytes >= 0 && s.AllocatedBytesTotal > 0);
        Assert.Equal(GC.CollectionCount(2), s.Gen2Collections);
        var d = monitor.CurrentDetails;
        Assert.True(d.AnonymousBytes > 0 && d.FileBackedBytes > 0 && d.PeakWorkingSetBytes >= d.WorkingSetBytes, d.ToString());
    }

    [Fact]
    public void The_sampler_allocates_nothing_and_a_monitor_sample_only_its_published_snapshot()
    {
        using var sampler = new ProcessSampler();
        Assert.True(sampler.IsProcfs);
        using var child = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        using var monitor = new MemoryMonitor();
        using var tracked = monitor.TrackChildProcess("sleep", child.Id);
        try
        {
            for (var i = 0; i < 200; i++) { sampler.Sample(); monitor.SampleNow(); } // warm-up: JIT, first GetGCMemoryInfo
            FullGc.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) sampler.Sample();
            var samplerBytes = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) monitor.SampleNow();
            var monitorBytesPerSample = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000.0;

            // A GC during the loop makes the next sample re-read GetGCMemoryInfo (one small object): allow a few of those, nothing per sample.
            Assert.True(samplerBytes < 2048, $"the raw sampler allocated {samplerBytes} bytes over 1000 samples");
            Assert.True(monitorBytesPerSample <= 160, $"a monitor sample allocated {monitorBytesPerSample:F1} bytes (one MemorySnapshot is ~120)");
            Assert.True(monitor.Current.ChildProcessWorkingSetBytes > 0);
        }
        finally { child.Kill(); }
    }

    [Fact]
    public async Task Child_processes_are_accounted_while_alive_and_dropped_when_they_exit_or_are_untracked()
    {
        using var monitor = new MemoryMonitor();
        using var child = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        try
        {
            var token = monitor.TrackChildProcess("sleep", child.Id);
            using var ghost = monitor.TrackChildProcess("gone", 999_999_999); // no such pid: tracked as exited, never read
            var s = monitor.SampleNow();
            Assert.True(s.ChildProcessWorkingSetBytes > 100 * 1024, $"child rss {s.ChildProcessWorkingSetBytes}");
            Assert.Contains(monitor.Children, c => c.Name == "gone" && c.Exited);
            Assert.Contains(monitor.Children, c => c.Pid == child.Id && !c.Exited && c.WorkingSetBytes > 0);

            child.Kill();
            await child.WaitForExitAsync();
            Assert.Equal(0, monitor.SampleNow().ChildProcessWorkingSetBytes);
            Assert.Contains(monitor.Children, c => c.Pid == child.Id && c.Exited);
            token.Dispose();
            Assert.DoesNotContain(monitor.Children, c => c.Pid == child.Id);
        }
        finally { if (!child.HasExited) child.Kill(); }
    }

    [Fact]
    public async Task A_child_above_its_cap_gets_one_over_budget_callback_per_cooldown_and_a_failing_callback_is_isolated()
    {
        using var monitor = new MemoryMonitor(new MemoryMonitorOptions { ChildOverBudgetSamples = 2, ChildCallbackCooldown = TimeSpan.FromSeconds(30) });
        using var child = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        try
        {
            var calls = 0;
            using var tracked = monitor.TrackChildProcess("sleep", child.Id, restartAboveBytes: 1, onOverBudget: () =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("the owner failed to restart it");
            });
            monitor.Tick();
            Assert.Equal(0, Volatile.Read(ref calls)); // one sample over the cap is not enough
            monitor.Tick();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, 5000));
            for (var i = 0; i < 5; i++) monitor.Tick();
            await Task.Delay(100);
            Assert.Equal(1, Volatile.Read(ref calls)); // cooldown
            Assert.Equal(1, monitor.Children.Single().OverBudgetCallbacks);
        }
        finally { child.Kill(); }
    }

    [Fact]
    public void Over_ninety_percent_of_the_budget_runs_one_aggressive_trim_and_then_waits()
    {
        var rss = Environment.WorkingSet;
        var budgetMb = (int)(rss / 0.95 / 1024 / 1024); // ~95 % used
        var config = Config(budgetMb);
        using var monitor = new MemoryMonitor(configuration: () => config);
        var cache = new RecordingTrimmable("cache");
        using var reg = monitor.Register(cache);
        Assert.Equal(budgetMb * 1024L * 1024, monitor.BudgetBytes);

        monitor.Tick();
        Assert.Equal([MemoryTrimLevel.Aggressive], cache.Calls.Select(c => c.Level));
        Assert.Equal(MemoryTrimLevel.Aggressive, monitor.LastTrim!.Level);
        Assert.Contains("90 %", monitor.LastTrim.Reason);
        monitor.Tick();
        monitor.Tick();
        Assert.Single(cache.Calls); // minimum interval: no trim storm
    }

    [Fact]
    public void Between_seventy_and_ninety_percent_trims_moderately_and_an_unlimited_budget_never_trims()
    {
        var rss = Environment.WorkingSet;
        var config = Config((int)(rss / 0.78 / 1024 / 1024));
        using (var monitor = new MemoryMonitor(configuration: () => config))
        {
            var cache = new RecordingTrimmable("cache");
            using var reg = monitor.Register(cache);
            monitor.Tick();
            Assert.Equal([MemoryTrimLevel.Moderate], cache.Calls.Select(c => c.Level));
        }
        var unlimited = Config(0, trimOnIdle: false);
        using (var monitor = new MemoryMonitor(configuration: () => unlimited))
        {
            var cache = new RecordingTrimmable("cache");
            using var reg = monitor.Register(cache);
            for (var i = 0; i < 5; i++) monitor.Tick();
            Assert.Empty(cache.Calls);
            Assert.Equal(0, monitor.BudgetBytes);
        }
    }

    [Fact]
    public void Changing_the_budget_setting_takes_effect_on_the_next_sample()
    {
        var config = Config(0, trimOnIdle: false);
        using var monitor = new MemoryMonitor(configuration: () => config);
        var cache = new RecordingTrimmable("cache");
        using var reg = monitor.Register(cache);
        monitor.Tick();
        Assert.Empty(cache.Calls);
        config.Set(SettingKeys.MemoryBudgetMb, (int)(Environment.WorkingSet / 0.95 / 1024 / 1024));
        monitor.Tick();
        Assert.Equal([MemoryTrimLevel.Aggressive], cache.Calls.Select(c => c.Level));
    }

    [Fact]
    public async Task A_trim_runs_every_trimmable_off_the_callers_thread_isolates_a_failing_one_and_reports_timings()
    {
        using var monitor = new MemoryMonitor();
        await monitor.TrimNowAsync(MemoryTrimLevel.Idle); // JIT of the trim path is not the call's cost
        var first = new RecordingTrimmable("first", _ => 4096);
        var broken = new RecordingTrimmable("broken", _ => throw new InvalidOperationException("boom"));
        var last = new RecordingTrimmable("last", _ => { Thread.Sleep(20); return 8192; });
        using var r1 = monitor.Register(first);
        using var r2 = monitor.Register(broken);
        using var r3 = monitor.Register(last);
        MemoryTrimReport? fired = null;
        using var sub = monitor.Trimmed.Subscribe(r => fired = r);

        var caller = Environment.CurrentManagedThreadId;
        var sw = Stopwatch.StartNew();
        var pending = monitor.TrimNowAsync(MemoryTrimLevel.Moderate);
        var callerCost = sw.Elapsed;
        await pending;

        Assert.True(callerCost < TimeSpan.FromMilliseconds(5), $"TrimNowAsync kept its caller {callerCost.TotalMilliseconds:F1} ms");
        Assert.All(new[] { first, broken, last }, t => Assert.NotEqual(caller, Assert.Single(t.Calls).Thread));
        var report = monitor.LastTrim!;
        Assert.Same(report, fired);
        Assert.Equal(["first", "broken", "last"], report.Trimmables.Select(t => t.Name));
        Assert.Equal(4096 + 8192, report.ReleasedByTrimmables);
        Assert.Contains("boom", report.Trimmables[1].Error);
        Assert.Equal(1, report.Failures);
        Assert.True(report.Trimmables[2].Elapsed >= TimeSpan.FromMilliseconds(15));
        Assert.True(report.CollectElapsed > TimeSpan.Zero && report.WorkingSetBefore > 0 && report.WorkingSetAfter > 0);
    }

    [Fact]
    public async Task Trims_never_overlap()
    {
        using var monitor = new MemoryMonitor();
        var running = 0;
        var maxConcurrent = 0;
        using var reg = monitor.Register(new RecordingTrimmable("slow", _ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxConcurrent, now);
            Thread.Sleep(30);
            Interlocked.Decrement(ref running);
            return 0;
        }));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => monitor.TrimNowAsync(MemoryTrimLevel.Idle).AsTask()));
        Assert.Equal(1, maxConcurrent);
        Assert.Equal(4, monitor.TrimCount);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    [Fact]
    public async Task An_aggressive_trim_returns_dropped_managed_memory_to_the_os()
    {
        using var monitor = new MemoryMonitor();
        FullGc.Collect();
        var baseline = monitor.SampleNow();
        var cache = new ArrayCache(); // 300 MB of arrays, referenced from a trimmable cache that drops them
        cache.Fill(300);
        using var reg = monitor.Register(cache);
        var full = monitor.SampleNow();
        Assert.True(full.WorkingSetBytes - baseline.WorkingSetBytes > 250L * 1024 * 1024, $"the cache did not grow the working set: {baseline.WorkingSetBytes >> 20} -> {full.WorkingSetBytes >> 20} MB");

        await monitor.TrimNowAsync(MemoryTrimLevel.Aggressive);
        var after = monitor.LastTrim!;
        Assert.Equal(300L * 1024 * 1024, after.ReleasedByTrimmables);
        Assert.True(after.WorkingSetBefore - after.WorkingSetAfter > 200L * 1024 * 1024,
            $"working set {after.WorkingSetBefore >> 20} -> {after.WorkingSetAfter >> 20} MB, GC heap {after.GcHeapBefore >> 20} -> {after.GcHeapAfter >> 20} MB");
    }

    private sealed class ArrayCache : IMemoryTrimmable
    {
        private List<byte[]> _arrays = new();
        public string Name => "arrays";
        public long EstimatedBytes => _arrays.Sum(a => (long)a.Length);

        public void Fill(int mb)
        {
            for (var i = 0; i < mb; i++)
            {
                var a = new byte[1024 * 1024];
                for (var j = 0; j < a.Length; j += 4096) a[j] = 1; // touch every page
                _arrays.Add(a);
            }
        }

        public long Trim(MemoryTrimLevel level)
        {
            var bytes = EstimatedBytes;
            _arrays = new();
            return bytes;
        }
    }

    [Fact]
    public void Malloc_trim_returns_freed_native_heap_pages()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "glibc's malloc_trim is Linux-only");
        using var sampler = new ProcessSampler();
        // 200k small native blocks (1 KB: below the mmap threshold, so they live in the malloc arenas), touched, then freed: glibc keeps the pages
        // until malloc_trim hands them back. One survivor at the end of the heap keeps free() itself from trimming the top.
        var blocks = new IntPtr[200_000];
        for (var i = 0; i < blocks.Length; i++)
        {
            blocks[i] = Marshal.AllocHGlobal(1024);
            Marshal.WriteByte(blocks[i], 1);
            Marshal.WriteByte(blocks[i], 1000, 1);
        }
        var keep = Marshal.AllocHGlobal(1024);
        var full = sampler.Sample().WorkingSetBytes;
        foreach (var b in blocks) Marshal.FreeHGlobal(b);
        var freed = sampler.Sample().WorkingSetBytes;
        Assert.True(OsMemory.ReturnFreePages());
        var trimmed = sampler.Sample().WorkingSetBytes;
        Marshal.FreeHGlobal(keep);
        Assert.True(freed - trimmed > 100L * 1024 * 1024, $"malloc_trim released too little: full {full >> 20} MB, after free {freed >> 20} MB, after malloc_trim {trimmed >> 20} MB");
    }

    [Fact]
    public async Task The_idle_trim_runs_once_per_idle_period_and_activity_rearms_it()
    {
        using var monitor = new MemoryMonitor(new MemoryMonitorOptions { IdleTrimAfter = TimeSpan.FromMilliseconds(200) });
        var cache = new RecordingTrimmable("cache");
        using var reg = monitor.Register(cache);
        monitor.NotifyActivity();
        monitor.Tick();
        Assert.Empty(cache.Calls);
        await Task.Delay(300);
        monitor.Tick();
        monitor.Tick();
        Assert.Equal([MemoryTrimLevel.Idle], cache.Calls.Select(c => c.Level));
        Assert.StartsWith("idle for", monitor.LastTrim!.Reason);

        monitor.NotifyActivity();
        Assert.True(monitor.IdleTime < TimeSpan.FromMilliseconds(100));
        monitor.Tick();
        Assert.Single(cache.Calls);
        await Task.Delay(300);
        monitor.Tick();
        Assert.Equal(2, cache.Calls.Count);
    }

    [Fact]
    public async Task Trim_on_idle_off_disables_the_idle_trim()
    {
        var config = Config(trimOnIdle: false);
        using var monitor = new MemoryMonitor(new MemoryMonitorOptions { IdleTrimAfter = TimeSpan.FromMilliseconds(100) }, () => config);
        var cache = new RecordingTrimmable("cache");
        using var reg = monitor.Register(cache);
        await Task.Delay(200);
        monitor.Tick();
        Assert.Empty(cache.Calls);
    }

    [Fact]
    public async Task Sampling_slows_down_when_idle_and_input_brings_it_back_at_once()
    {
        using var monitor = new MemoryMonitor(new MemoryMonitorOptions
        {
            SampleInterval = TimeSpan.FromMilliseconds(40), IdleSampleInterval = TimeSpan.FromSeconds(5), SlowSamplingAfter = TimeSpan.FromMilliseconds(300),
            IdleTrimAfter = TimeSpan.FromHours(1),
        });
        monitor.Start();
        await Task.Delay(250);
        Assert.True(monitor.SampleCount >= 3, $"{monitor.SampleCount} samples while active");
        await Task.Delay(400); // idle now: the loop switched to the 5 s period
        var idle = monitor.SampleCount;
        await Task.Delay(500);
        Assert.True(monitor.SampleCount - idle <= 1, $"{monitor.SampleCount - idle} samples in 500 ms while idle");
        monitor.NotifyActivity();
        Assert.True(SpinWait.SpinUntil(() => monitor.SampleCount > idle + 1, 1000), "input did not wake the sampler");
    }

    [Fact]
    public async Task Dispose_returns_at_once_even_during_a_slow_trim_and_the_thread_exits()
    {
        var monitor = new MemoryMonitor(new MemoryMonitorOptions { SampleInterval = TimeSpan.FromMilliseconds(20) });
        using var reg = monitor.Register(new RecordingTrimmable("slow", _ => { Thread.Sleep(500); return 0; }));
        monitor.Start();
        var trim = monitor.TrimNowAsync(MemoryTrimLevel.Idle).AsTask();
        await Task.Delay(50);
        var sw = Stopwatch.StartNew();
        monitor.Dispose();
        Assert.True(sw.ElapsedMilliseconds < 50, $"Dispose took {sw.ElapsedMilliseconds} ms");
        await trim;
        Assert.True(SpinWait.SpinUntil(() => !ThreadNames().Contains("cscode-memory"), 3000), "the sampling thread is still running");
        monitor.Dispose(); // idempotent
    }

    private static IReadOnlyList<string> ThreadNames() =>
        Directory.GetDirectories("/proc/self/task").Select(t => { try { return File.ReadAllText(Path.Combine(t, "comm")).Trim(); } catch (IOException) { return ""; } }).ToArray();

    [Fact]
    public void The_CsCode_meter_publishes_the_samples_for_dotnet_counters()
    {
        using var monitor = new MemoryMonitor().Start();
        monitor.SampleNow();
        var seen = new Dictionary<string, double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == "CsCode") l.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((i, v, _, _) => { lock (seen) seen[i.Name] = v; });
        listener.SetMeasurementEventCallback<int>((i, v, _, _) => { lock (seen) seen[i.Name] = v; });
        listener.SetMeasurementEventCallback<double>((i, v, _, _) => { lock (seen) seen[i.Name] = v; });
        listener.Start();
        listener.RecordObservableInstruments();
        Assert.True(seen["cscode.memory.working_set"] > 10 * 1024 * 1024);
        Assert.True(seen["cscode.memory.gc_heap"] > 0);
        Assert.True(seen["cscode.threads"] > 0);
        Assert.True(seen["cscode.gc.allocated"] > 0);
        Assert.Contains("cscode.memory.private", seen.Keys);
        Assert.Contains("cscode.gc.pause_time", seen.Keys);
    }

    [Fact]
    public async Task AddCsCodeProfiling_composes_with_or_without_optional_services_and_StartCsCodeProfiling_writes_the_Memory_channel()
    {
        using (var bare = new ServiceCollection().AddCsCodeProfiling().BuildServiceProvider())
        {
            var monitor = bare.GetRequiredService<MemoryMonitor>();
            Assert.Same(monitor, bare.GetRequiredService<IMemoryMonitor>());
            Assert.Same(monitor, bare.GetRequiredService<IUserActivityMonitor>());
            Assert.False(monitor.IsStarted); // creating it starts nothing
            Assert.Equal(0, monitor.BudgetBytes);
            using var started = bare.StartCsCodeProfiling(); // no output service: just the monitor
            Assert.True(monitor.IsStarted);
        }

        var config = Config(512);
        var output = new InMemoryOutputService();
        using var sp = new ServiceCollection()
            .AddSingleton<IConfigurationService>(config).AddSingleton<IOutputService>(output)
            .AddSingleton(new MemoryMonitorOptions { SampleInterval = TimeSpan.FromMilliseconds(50) })
            .AddCsCodeProfiling().BuildServiceProvider();
        var m = sp.GetRequiredService<MemoryMonitor>();
        Assert.Equal(512L * 1024 * 1024, m.BudgetBytes);
        Assert.Contains(config.Properties, p => p.Key == MemoryMonitor.IdleTrimSecondsSetting);
        using (sp.StartCsCodeProfiling())
        {
            Assert.DoesNotContain(MemoryLog.ChannelName, output.ChannelNames); // created on the first line only
            await m.TrimNowAsync(MemoryTrimLevel.Moderate);
            Assert.Contains("Moderate trim (requested)", output.Text(MemoryLog.ChannelName));
        }
    }

    [Fact]
    public void The_Memory_channel_writes_a_summary_line_per_interval()
    {
        var config = Config();
        config.Set(MemoryMonitor.LogIntervalSecondsSetting, 1);
        var output = new InMemoryOutputService();
        using var monitor = new MemoryMonitor(configuration: () => config);
        using var log = new MemoryLog(monitor, output, config);
        monitor.SampleNow();
        Assert.DoesNotContain(MemoryLog.ChannelName, output.ChannelNames);
        Thread.Sleep(1100);
        monitor.SampleNow();
        var text = output.Text(MemoryLog.ChannelName);
        Assert.Matches(@"working set \d+ MB \(anon \d+, file \d+, shmem \d+\).* GC heap \d+ MB.*alloc [\d.]+ MB/s.*\d+ threads", text);
    }

    [Fact]
    public void The_breakdown_splits_resident_memory_into_managed_native_and_file_backed_parts()
    {
        var b = MemoryBreakdown.Capture();
        Assert.True(b.ResidentBytes > 0 && b.AnonymousBytes > 0 && b.FileBackedBytes > 0);
        Assert.Equal(b.AnonymousBytes, b.ManagedBytes + b.NativeAnonymousBytes);
        Assert.Contains(b.Regions, r => r.Name.StartsWith("managed assemblies", StringComparison.Ordinal) && r.ResidentBytes > 0);
        Assert.Contains(b.Regions, r => r.Name.StartsWith("native libraries", StringComparison.Ordinal) && r.ResidentBytes > 0);
        Assert.InRange(b.Regions.Sum(r => r.ResidentBytes), b.ResidentBytes * 9 / 10, b.ResidentBytes * 11 / 10);
        Assert.Contains("Resident", b.ToText());
    }
}
