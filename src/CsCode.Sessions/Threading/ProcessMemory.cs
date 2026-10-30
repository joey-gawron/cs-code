using System.Runtime;
using CsCode.Sessions.Ipc;

namespace CsCode.Sessions;

/// <summary>
/// The process-level trim: a blocking, compacting full collection including the large object heap, then <c>malloc_trim</c> so the native heap
/// returns freed pages too. The memory monitor (CsCode.Profiling) does the same after its trimmables; this is for processes and paths without one
/// (the warm spare before it is claimed, the shell's stand-in monitor). Expensive (tens of milliseconds, all threads paused): only for hidden
/// sessions and the warm spare, never on the UI thread of a visible window.
/// </summary>
public static class ProcessMemory
{
    /// <summary>Compacts the heaps (<paramref name="aggressive"/>: the GC also decommits its free regions) and returns free native pages.</summary>
    public static void Compact(bool aggressive)
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        if (aggressive)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        UnixNative.TryMallocTrim();
    }

    /// <summary>The aggressive <see cref="Compact"/>; returns the working set after it (bytes).</summary>
    public static long TrimNow()
    {
        Compact(aggressive: true);
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return self.WorkingSet64;
    }

    /// <summary>Leaves the launching terminal's session (Unix), so closing that terminal does not end a background CsCode process.</summary>
    public static bool DetachFromTerminal() => UnixNative.TryDetachFromTerminal();
}
