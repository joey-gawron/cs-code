using CsCode.Core.Diagnostics;

namespace CsCode.Profiling;

/// <summary>How one <see cref="IMemoryTrimmable"/> did in a trim: bytes it reported released, how long it took, and its failure if it threw.</summary>
public sealed record TrimmableResult(string Name, long ReleasedBytes, TimeSpan Elapsed, string? Error);

/// <summary>
/// One trim, as run by <see cref="MemoryMonitor"/>: why, what each trimmable did, and the measured effect of the whole trim on the process (resident
/// set and GC heap before and after the compacting collection and the page release).
/// </summary>
public sealed record MemoryTrimReport(
    MemoryTrimLevel Level,
    string Reason,
    DateTimeOffset Timestamp,
    long ReleasedByTrimmables,
    IReadOnlyList<TrimmableResult> Trimmables,
    long WorkingSetBefore,
    long WorkingSetAfter,
    long GcHeapBefore,
    long GcHeapAfter,
    TimeSpan TrimmablesElapsed,
    TimeSpan CollectElapsed)
{
    public int Failures => Trimmables.Count(t => t.Error is not null);

    public override string ToString() =>
        $"{Level} trim ({Reason}): {Trimmables.Count} caches released {ReleasedByTrimmables / 1024.0 / 1024.0:F1} MB in {TrimmablesElapsed.TotalMilliseconds:F0} ms" +
        (Failures > 0 ? $" ({Failures} failed)" : "") +
        $"; compacting GC + page release {CollectElapsed.TotalMilliseconds:F0} ms; working set {WorkingSetBefore / 1024 / 1024} -> {WorkingSetAfter / 1024 / 1024} MB, " +
        $"GC heap {GcHeapBefore / 1024 / 1024} -> {GcHeapAfter / 1024 / 1024} MB";
}

/// <summary>A tracked child process as the monitor last saw it.</summary>
/// <param name="WorkingSetBytes">The resident set of the child AND its descendants (Linux; the sum of their VmRSS, shared pages counted per process).</param>
/// <param name="Processes">How many processes that sum covers (the child plus the descendants found at the last tree scan that are still alive).</param>
public sealed record ChildProcessInfo(string Name, int Pid, long WorkingSetBytes, long? RestartAboveBytes, bool Exited, int OverBudgetCallbacks, int Processes = 1);
