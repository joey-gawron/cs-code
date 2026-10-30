using System.Runtime;
using System.Runtime.InteropServices;
using CsCode.Core.Diagnostics;

namespace CsCode.Profiling;

/// <summary>
/// The process-level part of a trim: a blocking, compacting full collection (the large object heap included) and then handing freed pages back to
/// the OS. Linux: glibc's <c>malloc_trim(0)</c> returns the native heap's free pages (Skia, ICU, HarfBuzz and the runtime's own native allocations
/// live there); with musl, which has no <c>malloc_trim</c>, the call is skipped. Windows: <c>SetProcessWorkingSetSize(-1, -1)</c> empties the
/// working set (pages come back on demand). macOS: nothing to do (the allocator returns pages itself). docs/MEMORY.md has the measured effect.
/// </summary>
public static class OsMemory
{
    private static int _mallocTrimMissing;

    /// <summary>Collects (blocking, compacting, LOH once) and returns free pages to the OS. Seconds of CPU on huge heaps: never on the UI thread.</summary>
    public static void CompactAndRelease(MemoryTrimLevel level)
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        if (level == MemoryTrimLevel.Aggressive)
        {
            // Aggressive also decommits the GC's free regions and its gen0 budget; finalizable garbage is collected by the second pass.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        else
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        ReturnFreePages();
    }

    /// <summary>Asks the OS allocator to give free pages back; true when a call was made.</summary>
    public static bool ReturnFreePages()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (Volatile.Read(ref _mallocTrimMissing) != 0) return false;
                try
                {
                    _ = Native.malloc_trim(0);
                    return true;
                }
                catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
                {
                    Volatile.Write(ref _mallocTrimMissing, 1); // musl (Alpine) has no malloc_trim
                    return false;
                }
            }
            if (OperatingSystem.IsWindows())
            {
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                return Native.SetProcessWorkingSetSize(self.Handle, -1, -1);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException) { }
        return false;
    }

    /// <summary>True when <c>malloc_trim</c> is available (Linux with glibc); probed on first use.</summary>
    public static bool HasMallocTrim => OperatingSystem.IsLinux() && Volatile.Read(ref _mallocTrimMissing) == 0;

    private static class Native
    {
        [DllImport("libc", EntryPoint = "malloc_trim")]
        public static extern int malloc_trim(nuint pad);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
    }
}
