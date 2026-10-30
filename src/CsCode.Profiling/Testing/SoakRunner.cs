using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CsCode.Profiling.Testing;

/// <summary>How long and how densely <see cref="SoakRunner"/> runs and samples.</summary>
public sealed record SoakOptions
{
    /// <summary>Run for this long (after the warm-up) …</summary>
    public TimeSpan? Duration { get; init; }
    /// <summary>… or this many measured iterations (after the warm-up); with both set, whichever comes first.</summary>
    public int? Iterations { get; init; }
    /// <summary>Iterations run before measuring (caches fill, code is jitted, pools grow).</summary>
    public int WarmupIterations { get; init; } = 20;
    /// <summary>Time between samples (taken between iterations).</summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Also sample every this many iterations (fast scenarios finish many iterations between two time-based samples).</summary>
    public int? SampleEveryIterations { get; init; }
    /// <summary>Full collection before each sample, so the heap series is the LIVE heap (what a leak grows). Off: the series is what a user would see.</summary>
    public bool CollectBeforeSample { get; init; } = true;
    /// <summary>CSV/JSON output directory; null = <c>{repo}/.scratch/soak</c> (or the temp directory outside a checkout); "" = write nothing.</summary>
    public string? OutputDirectory { get; init; }
    /// <summary>Optional extra series: e.g. the tracked child processes' resident set (<c>monitor.Current.ChildProcessWorkingSetBytes</c>).</summary>
    public Func<long>? ChildBytes { get; init; }
}

/// <summary>One point of the soak time series (a value type, kept in a pre-sized list: the series itself must not show up as heap growth).</summary>
public readonly record struct SoakSample(
    int Iteration, double Seconds, long WorkingSet, long PrivateBytes, long GcLiveBytes, long GcCommitted, long Gen0, long Gen1, long Gen2,
    int Threads, int FileDescriptors, long AllocatedBytes, long ChildBytes);

/// <summary>Limits for <see cref="SoakReport.AssertStable"/>; null = not asserted.</summary>
public sealed record SoakThresholds
{
    public double? MaxHeapBytesPerIteration { get; init; }
    public double? MaxHeapMbPerHour { get; init; }
    public double? MaxWorkingSetMbPerHour { get; init; }
    /// <summary>The working set's peak in the last third may exceed the middle third's by this much (plateau).</summary>
    public double? MaxPlateauGrowthMb { get; init; }
    public int ThreadTolerance { get; init; } = 4;
    public int FdTolerance { get; init; } = 4;
    /// <summary>Gen2 collections per iteration in the second half may be at most this multiple of the first half's (super-linear growth).</summary>
    public double MaxGen2RateRatio { get; init; } = 3;
}

/// <summary>A soak's series and its regression: growth per iteration and per hour with confidence, plateau, gen2 rate, resources.</summary>
public sealed record SoakReport(
    string Name, IReadOnlyList<SoakSample> Samples, int WarmupIterations, int Iterations, double Seconds,
    LinearFit HeapPerIteration, LinearFit HeapPerSecond, LinearFit WorkingSetPerSecond, ResourceCounts Before, ResourceCounts After,
    string? CsvPath, string? JsonPath)
{
    public double HeapMbPerHour => HeapPerSecond.Slope * 3600 / 1024 / 1024;
    public double HeapMbPerHourConfidence => HeapPerSecond.SlopeConfidence95 * 3600 / 1024 / 1024;
    public double WorkingSetMbPerHour => WorkingSetPerSecond.Slope * 3600 / 1024 / 1024;
    public double WorkingSetMbPerHourConfidence => WorkingSetPerSecond.SlopeConfidence95 * 3600 / 1024 / 1024;

    /// <summary>Peak working set (MB) of the first, middle and last third of the measured samples.</summary>
    public (double First, double Middle, double Last) WorkingSetPeaksMb
    {
        get
        {
            if (Samples.Count < 3) return (0, 0, 0);
            var third = Samples.Count / 3;
            double Peak(IEnumerable<SoakSample> s) => s.Max(x => x.WorkingSet) / 1024.0 / 1024.0;
            return (Peak(Samples.Take(third)), Peak(Samples.Skip(third).Take(third)), Peak(Samples.Skip(2 * third)));
        }
    }

    /// <summary>Gen2 collections per iteration in the first and second half of the run.</summary>
    public (double First, double Second) Gen2Rates
    {
        get
        {
            if (Samples.Count < 4) return (0, 0);
            var mid = Samples[Samples.Count / 2];
            var first = Samples[0];
            var last = Samples[^1];
            static double Rate(SoakSample a, SoakSample b) => b.Iteration == a.Iteration ? 0 : (double)(b.Gen2 - a.Gen2) / (b.Iteration - a.Iteration);
            return (Rate(first, mid), Rate(mid, last));
        }
    }

    public string Summary()
    {
        var peaks = WorkingSetPeaksMb;
        var gen2 = Gen2Rates;
        return string.Create(CultureInfo.InvariantCulture,
            $"{Name}: {Iterations} iterations in {Seconds:F0} s after {WarmupIterations} warm-up; live heap {HeapPerIteration.Slope:F0} ± {HeapPerIteration.SlopeConfidence95:F0} B/iteration, " +
            $"{HeapMbPerHour:F2} ± {HeapMbPerHourConfidence:F2} MB/h; working set {WorkingSetMbPerHour:F1} ± {WorkingSetMbPerHourConfidence:F1} MB/h, peaks by third {peaks.First:F1}/{peaks.Middle:F1}/{peaks.Last:F1} MB; " +
            $"gen2/iteration {gen2.First:F3} -> {gen2.Second:F3}; {Before} -> {After}");
    }

    /// <summary>Throws <see cref="MemoryAssertionException"/> listing every threshold the run broke.</summary>
    public void AssertStable(SoakThresholds thresholds)
    {
        var failures = new List<string>();
        if (thresholds.MaxHeapBytesPerIteration is { } perIteration && HeapPerIteration.SlopeLow > perIteration)
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"live heap grows {HeapPerIteration.Slope:F0} ± {HeapPerIteration.SlopeConfidence95:F0} B/iteration (> {perIteration:F0})"));
        if (thresholds.MaxHeapMbPerHour is { } heapHour && HeapMbPerHour - HeapMbPerHourConfidence > heapHour)
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"live heap grows {HeapMbPerHour:F2} MB/h (> {heapHour:F2})"));
        if (thresholds.MaxWorkingSetMbPerHour is { } rssHour && WorkingSetMbPerHour - WorkingSetMbPerHourConfidence > rssHour)
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"working set grows {WorkingSetMbPerHour:F1} MB/h (> {rssHour:F1})"));
        if (thresholds.MaxPlateauGrowthMb is { } plateau && WorkingSetPeaksMb is var p && p.Last - p.Middle > plateau)
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"working set has no plateau: peak {p.Middle:F1} -> {p.Last:F1} MB (> +{plateau:F1})"));
        if (!ResourceCensus.Within(Before, After, Math.Max(thresholds.ThreadTolerance, thresholds.FdTolerance)) ||
            After.Threads > Before.Threads + thresholds.ThreadTolerance || After.FileDescriptors > Before.FileDescriptors + thresholds.FdTolerance)
            failures.Add($"resources grew: {Before} -> {After}");
        var gen2 = Gen2Rates;
        if (gen2.First > 0 && gen2.Second > gen2.First * thresholds.MaxGen2RateRatio && gen2.Second - gen2.First > 0.01)
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"gen2 collections grow super-linearly: {gen2.First:F3} -> {gen2.Second:F3} per iteration"));
        if (failures.Count > 0) throw new MemoryAssertionException(Summary() + "\n  " + string.Join("\n  ", failures));
    }
}

/// <summary>
/// The soak runner (MUST 4/5): <c>Run(scenario, duration | iterations, sampleInterval)</c> → a time series (working set, private bytes, live GC heap,
/// committed, gen0/1/2, threads, descriptors, allocated bytes, children) and a regression report (slope per iteration and per hour with a 95 %
/// interval, plateau by thirds, gen2 rate), written as CSV and JSON under <c>.scratch/soak/</c>. Samples are taken between iterations, so a
/// scenario's iteration is never interrupted by a forced collection. Continues on the caller's context (UI scenarios iterate on the UI thread).
/// </summary>
public static class SoakRunner
{
    public static async Task<SoakReport> RunAsync(IScenario scenario, SoakOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (options.Duration is null && options.Iterations is null) throw new ArgumentException("a soak needs a duration or an iteration count");
        await scenario.InitializeAsync(ct);
        try
        {
            for (var i = 0; i < options.WarmupIterations; i++) await scenario.RunIterationAsync(i, ct);
            SettleHeap();
            var before = ResourceCensus.Capture();
            var samples = new List<SoakSample>(16_384);
            var clock = Stopwatch.StartNew();
            var iteration = 0;
            samples.Add(Take(0, clock, options));
            var lastSample = clock.Elapsed;
            while (!ct.IsCancellationRequested)
            {
                if (options.Iterations is { } max && iteration >= max) break;
                if (options.Duration is { } duration && clock.Elapsed >= duration) break;
                await scenario.RunIterationAsync(options.WarmupIterations + iteration, ct);
                iteration++;
                if (clock.Elapsed - lastSample >= options.SampleInterval || (options.SampleEveryIterations is > 0 and var every && iteration % every == 0))
                {
                    samples.Add(Take(iteration, clock, options));
                    lastSample = clock.Elapsed;
                }
            }
            if (samples[^1].Iteration != iteration) samples.Add(Take(iteration, clock, options));
            var seconds = clock.Elapsed.TotalSeconds;
            FullGc.Collect();
            var after = ResourceCensus.Capture();
            return Report(scenario.Name, samples, options, iteration, seconds, before, after);
        }
        finally
        {
            await scenario.DisposeAsync();
        }
    }

    /// <summary>
    /// Full collections until the live heap stops shrinking (at most 10): what earlier work in the process left to finalize — another test's
    /// windows, a disposed host's caches — is released before the first sample instead of during the run, where it read as a NEGATIVE slope
    /// (T56: a 16 KB/iteration leak measured 14.9 KB in the full MemoryTests run, after the app tests).
    /// </summary>
    private static void SettleHeap()
    {
        FullGc.Collect();
        var previous = GC.GetTotalMemory(false);
        for (var i = 0; i < 10; i++)
        {
            FullGc.Collect();
            var now = GC.GetTotalMemory(false);
            if (previous - now < 64 * 1024) return;
            previous = now;
        }
    }

    /// <summary>Synchronous convenience for Core-level scenarios.</summary>
    public static SoakReport Run(IScenario scenario, SoakOptions options) => RunAsync(scenario, options).GetAwaiter().GetResult();

    private static SoakSample Take(int iteration, Stopwatch clock, SoakOptions options)
    {
        if (options.CollectBeforeSample) FullGc.Collect();
        var p = ProcessSnapshot();
        var resources = ResourceCensus.Capture();
        return new SoakSample(iteration, clock.Elapsed.TotalSeconds, p.WorkingSetBytes, p.PrivateBytes, GC.GetTotalMemory(false),
            GC.GetGCMemoryInfo().TotalCommittedBytes, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), resources.Threads,
            resources.FileDescriptors, GC.GetTotalAllocatedBytes(false), options.ChildBytes?.Invoke() ?? 0);
    }

    private static ProcessMemorySample ProcessSnapshot()
    {
        using var sampler = new ProcessSampler();
        return sampler.Sample();
    }

    private static SoakReport Report(string name, List<SoakSample> samples, SoakOptions options, int iterations, double seconds, ResourceCounts before, ResourceCounts after)
    {
        var it = samples.Select(s => (double)s.Iteration).ToArray();
        var t = samples.Select(s => s.Seconds).ToArray();
        var heap = samples.Select(s => (double)s.GcLiveBytes).ToArray();
        var rss = samples.Select(s => (double)s.WorkingSet).ToArray();
        string? csv = null, json = null;
        var directory = options.OutputDirectory ?? DefaultOutputDirectory();
        var report = new SoakReport(name, samples, options.WarmupIterations, iterations, seconds, LinearRegression.Fit(it, heap), LinearRegression.Fit(t, heap),
            LinearRegression.Fit(t, rss), before, after, null, null);
        if (directory.Length > 0)
        {
            Directory.CreateDirectory(directory);
            var stem = Path.Combine(directory, $"{Sanitize(name)}-{DateTime.Now:yyyyMMdd-HHmmss}");
            csv = stem + ".csv";
            json = stem + ".json";
            WriteCsv(csv, samples);
            File.WriteAllText(json, JsonSerializer.Serialize(new
            {
                report.Name, report.WarmupIterations, report.Iterations, report.Seconds,
                HeapBytesPerIteration = report.HeapPerIteration.Slope, HeapBytesPerIterationCi95 = report.HeapPerIteration.SlopeConfidence95,
                report.HeapMbPerHour, report.HeapMbPerHourConfidence, report.WorkingSetMbPerHour, report.WorkingSetMbPerHourConfidence,
                WorkingSetPeaksMb = new[] { report.WorkingSetPeaksMb.First, report.WorkingSetPeaksMb.Middle, report.WorkingSetPeaksMb.Last },
                Gen2PerIteration = new[] { report.Gen2Rates.First, report.Gen2Rates.Second },
                Before = before.ToString(), After = after.ToString(), Summary = report.Summary(),
            }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        }
        return report with { CsvPath = csv, JsonPath = json };
    }

    public static void WriteCsv(string path, IEnumerable<SoakSample> samples)
    {
        var sb = new StringBuilder("iteration,seconds,working_set,private,gc_live,gc_committed,gen0,gen1,gen2,threads,fds,allocated,children\n");
        foreach (var s in samples)
            sb.Append(CultureInfo.InvariantCulture, $"{s.Iteration},{s.Seconds:F3},{s.WorkingSet},{s.PrivateBytes},{s.GcLiveBytes},{s.GcCommitted},{s.Gen0},{s.Gen1},{s.Gen2},{s.Threads},{s.FileDescriptors},{s.AllocatedBytes},{s.ChildBytes}\n");
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary><c>{repo}/.scratch/soak</c> when running inside a checkout (found from the base directory up), else the temp directory.</summary>
    public static string DefaultOutputDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CsCode.slnx"))) return Path.Combine(dir.FullName, ".scratch", "soak");
        return Path.Combine(Path.GetTempPath(), "cscode-soak");
    }

    private static string Sanitize(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}
