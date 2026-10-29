using CsCode.Core.Lifecycle;

namespace CsCode.Core.Diagnostics;

/// <summary>Severity ladder for memory pressure. Higher levels ask participants to shed more.</summary>
public enum MemoryTrimLevel
{
    /// <summary>Opportunistic housekeeping while idle (drop caches older than a minute, shrink pools).</summary>
    Idle = 0,
    /// <summary>Approaching budget: drop re-creatable caches (tokenizer line states of non-visible docs, glyph/measure caches, icon bitmaps).</summary>
    Moderate = 1,
    /// <summary>Over budget: release everything re-creatable, unload background documents' derived data, restart bloated child processes.</summary>
    Aggressive = 2,
}

/// <summary>
/// Implemented by anything that holds re-creatable memory (editor caches, syntax-token state, LSP result caches, the
/// extension-host process…). Registered with <see cref="IMemoryMonitor"/>; the monitor calls <see cref="Trim"/> from a
/// background thread, so implementations must be thread-safe and must never block the UI.
/// </summary>
public interface IMemoryTrimmable
{
    string Name { get; }
    /// <summary>Best-effort managed-bytes estimate for the diagnostics view (0 if unknown).</summary>
    long EstimatedBytes { get; }
    /// <summary>Returns approximate bytes released.</summary>
    long Trim(MemoryTrimLevel level);
}

public sealed record MemorySnapshot(
    DateTimeOffset Timestamp,
    long WorkingSetBytes,
    long PrivateBytes,
    long GcHeapBytes,
    long GcCommittedBytes,
    long Gen0Collections,
    long Gen1Collections,
    long Gen2Collections,
    long LargeObjectHeapBytes,
    long AllocatedBytesTotal,
    long ChildProcessWorkingSetBytes,
    int ThreadCount);

/// <summary>Process-wide memory telemetry + pressure response. Implemented by CsCode.Profiling.</summary>
public interface IMemoryMonitor
{
    MemorySnapshot Current { get; }
    /// <summary>Configured soft budget (cscode.memory.budgetMb); 0 = unlimited.</summary>
    long BudgetBytes { get; }
    IEventSource<MemorySnapshot> Sampled { get; }
    IDisposable Register(IMemoryTrimmable trimmable);
    IReadOnlyList<IMemoryTrimmable> Trimmables { get; }
    /// <summary>Runs all trimmables at the level, then compacts the GC heap and returns pages to the OS.</summary>
    ValueTask TrimNowAsync(MemoryTrimLevel level, CancellationToken ct = default);
    /// <summary>Registers an external child process (extension host, language server, debug adapter) so its RSS is accounted for and capped.</summary>
    IDisposable TrackChildProcess(string name, int pid, long? restartAboveBytes = null, Func<ValueTask>? onOverBudget = null);
}
