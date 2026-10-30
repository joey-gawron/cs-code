using System.Globalization;
using System.Numerics;

namespace CsCode.Profiling.Testing;

/// <summary>p50/p90/p99/max of a latency series, in milliseconds.</summary>
public sealed record LatencyReport(long Samples, double P50Ms, double P90Ms, double P99Ms, double MaxMs, double MeanMs)
{
    public static readonly LatencyReport Empty = new(0, 0, 0, 0, 0, 0);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"p50 {P50Ms:F2} ms, p90 {P90Ms:F2} ms, p99 {P99Ms:F2} ms, max {MaxMs:F2} ms ({Samples} samples)");

    /// <summary>Throws <see cref="MemoryAssertionException"/> unless p99 and max are within the budgets.</summary>
    public void AssertWithin(double p99Ms, double maxMs, string what = "latency")
    {
        if (Samples == 0) throw new MemoryAssertionException($"{what}: no samples");
        if (P99Ms > p99Ms || MaxMs > maxMs)
            throw new MemoryAssertionException(string.Create(CultureInfo.InvariantCulture, $"{what} over budget (p99 ≤ {p99Ms} ms, max ≤ {maxMs} ms): {this}"));
    }
}

/// <summary>
/// HdrHistogram-style fixed buckets over microseconds (MUST 9: no allocation per sample): values below 128 µs are exact, above that each power of
/// two is split into 64 linear sub-buckets (≤ 1.6 % error); the range covers any <see cref="long"/>. Recording is a few integer operations and one
/// interlocked increment, from any thread; percentiles report the bucket's upper bound (never optimistic), capped at the exact maximum.
/// </summary>
public sealed class LatencyHistogram
{
    private const int Linear = 128;
    private const int SubBuckets = 64;
    private readonly long[] _counts = new long[Linear + SubBuckets * 57];
    private long _count, _sum, _max;

    public long Count => Interlocked.Read(ref _count);

    public void RecordTicks(long stopwatchTicks) => RecordMicroseconds(stopwatchTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency);

    public void RecordMilliseconds(double ms) => RecordMicroseconds((long)(ms * 1000));

    public void RecordMicroseconds(long us)
    {
        if (us < 0) us = 0;
        Interlocked.Increment(ref _counts[Index(us)]);
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _sum, us);
        long seen;
        while (us > (seen = Interlocked.Read(ref _max)) && Interlocked.CompareExchange(ref _max, us, seen) != seen) { }
    }

    public void Reset()
    {
        for (var i = 0; i < _counts.Length; i++) Interlocked.Exchange(ref _counts[i], 0);
        Interlocked.Exchange(ref _count, 0);
        Interlocked.Exchange(ref _sum, 0);
        Interlocked.Exchange(ref _max, 0);
    }

    /// <summary>The value (ms) at or below which <paramref name="quantile"/> of the samples lie.</summary>
    public double PercentileMs(double quantile)
    {
        var total = Count;
        if (total == 0) return 0;
        var target = Math.Max(1, (long)Math.Ceiling(quantile * total));
        long seen = 0;
        for (var i = 0; i < _counts.Length; i++)
        {
            seen += Interlocked.Read(ref _counts[i]);
            if (seen >= target) return Math.Min(UpperBound(i), Interlocked.Read(ref _max)) / 1000.0;
        }
        return Interlocked.Read(ref _max) / 1000.0;
    }

    public LatencyReport Report()
    {
        var n = Count;
        return n == 0 ? LatencyReport.Empty
            : new LatencyReport(n, PercentileMs(0.5), PercentileMs(0.9), PercentileMs(0.99), Interlocked.Read(ref _max) / 1000.0, Interlocked.Read(ref _sum) / 1000.0 / n);
    }

    internal static int Index(long us)
    {
        if (us < Linear) return (int)us;
        var shift = 63 - BitOperations.LeadingZeroCount((ulong)us) - 6; // keeps the top 7 bits: (us >> shift) in [64, 128)
        return Linear + (shift - 1) * SubBuckets + (int)((us >> shift) - SubBuckets);
    }

    internal static long UpperBound(int index)
    {
        if (index < Linear) return index;
        var k = index - Linear;
        var shift = k / SubBuckets + 1;
        var top = (long)(k % SubBuckets + SubBuckets);
        return ((top + 1) << shift) - 1;
    }
}
