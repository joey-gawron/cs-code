using System.Runtime;
using CsCode.Sessions.Ipc;

namespace CsCode.Sessions;

/// <summary>
/// The minimal trim path of a hidden session (the Profiling module's <c>IMemoryMonitor.TrimNowAsync(Aggressive)</c> machinery is parked, so this
/// stands in for its process-level part): a blocking, compacting full collection including the large object heap, then <c>malloc_trim</c> so the
/// native heap returns freed pages too. Expensive (tens of milliseconds, all threads paused): only for hidden sessions and the warm spare, never on
/// the UI thread of a visible window.
/// </summary>
public static class ProcessMemory
{
    /// <summary>Returns the working set after the trim (bytes).</summary>
    public static long TrimNow()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        UnixNative.TryMallocTrim();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return self.WorkingSet64;
    }

    /// <summary>Leaves the launching terminal's session (Unix), so closing that terminal does not end a background CsCode process.</summary>
    public static bool DetachFromTerminal() => UnixNative.TryDetachFromTerminal();
}
