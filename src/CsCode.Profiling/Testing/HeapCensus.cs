using System.Globalization;

namespace CsCode.Profiling.Testing;

/// <summary>The live heap after each measured run of a scenario, and the fitted growth per run.</summary>
public sealed record HeapCensusResult(string Name, int Warmup, IReadOnlyList<long> LiveBytes, LinearFit Fit, long CommittedBefore, long CommittedAfter)
{
    /// <summary>Fitted growth of the live heap per run (bytes).</summary>
    public double BytesPerRun => Fit.Slope;

    /// <summary>
    /// Throws when the live heap grows by more than <paramref name="maxBytesPerRun"/> per run. Noise tolerance: the slope's 95 % interval must lie
    /// above the limit to fail when <paramref name="requireSignificance"/> is set (a noisy but flat series passes); otherwise the slope itself counts.
    /// </summary>
    public void AssertFlat(double maxBytesPerRun, bool requireSignificance = true)
    {
        var growing = requireSignificance ? Fit.SlopeLow > maxBytesPerRun : Fit.Slope > maxBytesPerRun;
        if (growing) throw new MemoryAssertionException($"{Name}: live heap grows {Describe()} (allowed {maxBytesPerRun:F0} bytes/run)");
    }

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Fit.Slope:F0} ± {Fit.SlopeConfidence95:F0} bytes/run over {LiveBytes.Count} runs after {Warmup} warm-up (R² {Fit.RSquared:F2}; first {LiveBytes[0] / 1024} KB, last {LiveBytes[^1] / 1024} KB)");
}

/// <summary>
/// Heap census (MUST 4): runs a scenario N times after a warm-up, takes the live heap after each run (<see cref="GC.GetTotalMemory(bool)"/> with a
/// full collection, plus <see cref="GC.GetGCMemoryInfo()"/>'s committed bytes before and after) and fits a line through the series. A leak shows
/// as a positive slope whose confidence interval excludes zero; caches filling up show during the warm-up, which is excluded.
/// </summary>
public static class HeapCensus
{
    public static HeapCensusResult Run(string name, Action<int> scenario, int runs = 30, int warmup = 10)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        for (var i = 0; i < warmup; i++) scenario(i);
        FullGc.Collect();
        var committedBefore = GC.GetGCMemoryInfo().TotalCommittedBytes;
        var live = new long[runs];
        for (var i = 0; i < runs; i++)
        {
            scenario(warmup + i);
            FullGc.Collect();
            live[i] = GC.GetTotalMemory(forceFullCollection: false);
        }
        return Result(name, warmup, live, committedBefore);
    }

    /// <summary>Async variant (UI scenarios: the continuation stays on the caller's context, so a headless UI test keeps running on its UI thread).</summary>
    public static async Task<HeapCensusResult> RunAsync(string name, Func<int, Task> scenario, int runs = 30, int warmup = 10)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        for (var i = 0; i < warmup; i++) await scenario(i);
        FullGc.Collect();
        var committedBefore = GC.GetGCMemoryInfo().TotalCommittedBytes;
        var live = new long[runs];
        for (var i = 0; i < runs; i++)
        {
            await scenario(warmup + i);
            FullGc.Collect();
            live[i] = GC.GetTotalMemory(forceFullCollection: false);
        }
        return Result(name, warmup, live, committedBefore);
    }

    private static HeapCensusResult Result(string name, int warmup, long[] live, long committedBefore)
    {
        var y = live.Select(v => (double)v).ToArray();
        return new HeapCensusResult(name, warmup, live, LinearRegression.FitIndexed(y), committedBefore, GC.GetGCMemoryInfo().TotalCommittedBytes);
    }
}
