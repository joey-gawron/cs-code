using System.Runtime;

namespace CsCode.Editor.Tests.Support;

/// <summary>
/// The bytes of LIVE objects on the managed heap, for memory budgets that compare two moments of the process (T58).
/// <c>GC.GetTotalMemory(true)</c> counts the free space between live objects too, and how much of it there is depends on what earlier tests
/// left behind: after the hostile-provider runs (hundreds of MB allocated, objects pinned that compaction cannot move) the same allocation
/// measured 11.7 MB instead of 8.5 MB, and opening a 10 MB file "cost" 116 MB instead of 26 MB in the full Editor run. A compacting full
/// collection (the large-object heap included) followed by heap size minus fragmentation measures the same thing whatever ran before.
/// </summary>
internal static class LiveHeap
{
    public static long Bytes()
    {
        for (var i = 0; i < 2; i++) // a second pass for what finalizers released
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return info.HeapSizeBytes - info.FragmentedBytes;
    }
}
