using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CsCode.Profiling;
using CsCode.Profiling.Testing;

namespace CsCode.ProfileHost;

/// <summary>
/// What one run of a workload is measured by (the GC matrix's columns): peak resident set (VmHWM: the whole process lifetime, start-up included),
/// steady-state resident set (median of 100 ms samples over the second half of the workload), every GC pause (runtime events: p50/p99/max),
/// CPU time (user + system), wall time, collections and allocations.
/// </summary>
public sealed class RunMetrics : IDisposable
{
    private readonly GcPauseRecorder _pauses = new();
    private readonly List<(double Seconds, long Rss)> _rss = new(4096);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
    private readonly long _alloc0 = GC.GetTotalAllocatedBytes(true);
    private readonly int _g0 = GC.CollectionCount(0), _g1 = GC.CollectionCount(1), _g2 = GC.CollectionCount(2);
    private readonly Thread _sampler;
    private volatile bool _stop;

    public RunMetrics()
    {
        _sampler = new Thread(() =>
        {
            using var s = new ProcessSamplerHandle();
            while (!_stop)
            {
                var rss = s.WorkingSet();
                lock (_rss) _rss.Add((_clock.Elapsed.TotalSeconds, rss));
                Thread.Sleep(100);
            }
        }) { IsBackground = true, Name = "rss-sampler" };
        _sampler.Start();
    }

    public Dictionary<string, object?> Finish(string workload, IDictionary<string, object?>? extra = null)
    {
        _stop = true;
        _sampler.Join();
        Thread.Sleep(1200); // the runtime's GC events arrive asynchronously
        _pauses.Stop();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime - _cpu0;
        double[] rss;
        lock (_rss) rss = _rss.Where(r => r.Seconds >= _clock.Elapsed.TotalSeconds / 2).Select(r => (double)r.Rss).Order().ToArray();
        var p = _pauses.Pauses.Report();
        var result = new Dictionary<string, object?>
        {
            ["workload"] = workload,
            ["config"] = ConfigDescription(),
            ["peakRssMb"] = Mb(PeakRss()),
            ["steadyRssMb"] = rss.Length == 0 ? null : Math.Round(rss[rss.Length / 2] / 1024 / 1024, 1),
            ["gcPauseP50Ms"] = Math.Round(p.P50Ms, 3),
            ["gcPauseP99Ms"] = Math.Round(p.P99Ms, 3),
            ["gcPauseMaxMs"] = Math.Round(p.MaxMs, 3),
            ["gcPauses"] = p.Samples,
            ["gcTotalPauseMs"] = Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds, 1),
            ["cpuSeconds"] = Math.Round(cpu.TotalSeconds, 2),
            ["wallSeconds"] = Math.Round(_clock.Elapsed.TotalSeconds, 2),
            ["gen0"] = GC.CollectionCount(0) - _g0,
            ["gen1"] = GC.CollectionCount(1) - _g1,
            ["gen2"] = GC.CollectionCount(2) - _g2,
            ["allocatedMb"] = Mb(GC.GetTotalAllocatedBytes(true) - _alloc0),
            ["gcHeapMb"] = Mb(GC.GetTotalMemory(false)),
            ["gcCommittedMb"] = Mb(GC.GetGCMemoryInfo().TotalCommittedBytes),
        };
        if (extra is not null) foreach (var (k, v) in extra) result[k] = v;
        return result;
    }

    public static double Mb(long bytes) => Math.Round(bytes / 1024.0 / 1024.0, 1);

    public static long PeakRss()
    {
        foreach (var line in File.ReadLines("/proc/self/status"))
            if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
                return long.Parse(line[6..].Trim().Split(' ')[0], CultureInfo.InvariantCulture) * 1024;
        return 0;
    }

    /// <summary>The runtime settings this process runs with (env + runtimeconfig knobs the matrix varies).</summary>
    public static string ConfigDescription()
    {
        var parts = new List<string>
        {
            $"server={System.Runtime.GCSettings.IsServerGC}",
            $"concurrent={AppContext.GetData("System.GC.Concurrent") ?? Environment.GetEnvironmentVariable("DOTNET_gcConcurrent") ?? "default"}",
            $"latency={System.Runtime.GCSettings.LatencyMode}",
        };
        foreach (var name in new[] { "DOTNET_GCConserveMemory", "DOTNET_GCRetainVM", "DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_TC_QuickJitForLoops",
                     "DOTNET_ReadyToRun", "DOTNET_GCgen0size", "DOTNET_GCgen0MaxBudget", "DOTNET_GCHeapCount", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "DOTNET_gcServer", "DOTNET_gcConcurrent" })
            if (Environment.GetEnvironmentVariable(name) is { } v) parts.Add($"{name[7..]}={v}");
        parts.Add($"r2r={IsReadyToRun()}");
        return string.Join(' ', parts);
    }

    /// <summary>Whether this tool's own assemblies were compiled ReadyToRun (the publish-r2r build).</summary>
    public static bool IsReadyToRun()
    {
        try
        {
            using var stream = File.OpenRead(typeof(RunMetrics).Assembly.Location);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            return pe.PEHeaders.CorHeader?.ManagedNativeHeaderDirectory.Size > 0;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException) { return false; }
    }

    public static void Emit(Dictionary<string, object?> result) =>
        Console.WriteLine("RESULT " + JsonSerializer.Serialize(result, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));

    public void Dispose()
    {
        _stop = true;
        if (_sampler.IsAlive) _sampler.Join();
        _pauses.Dispose();
    }

    /// <summary>The monitor's allocation-free reader, for the sampling thread.</summary>
    private sealed class ProcessSamplerHandle : IDisposable
    {
        private readonly MemoryMonitor _monitor = new();
        public long WorkingSet() => _monitor.SampleNow().WorkingSetBytes;
        public void Dispose() => _monitor.Dispose();
    }
}
