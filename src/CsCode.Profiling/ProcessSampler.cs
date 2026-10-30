using System.Diagnostics;
using CsCode.Core.Diagnostics;

namespace CsCode.Profiling;

/// <summary>
/// One reading of the process's memory: what <see cref="MemorySnapshot"/> carries plus the Linux breakdown (anonymous / file-backed / shared
/// resident memory, swap, peak) and GC details that the Core record has no room for. A value type: reading it allocates nothing.
/// </summary>
public readonly record struct ProcessMemorySample(
    long Timestamp,
    long WorkingSetBytes,
    long PeakWorkingSetBytes,
    long AnonymousBytes,
    long FileBackedBytes,
    long SharedMemoryBytes,
    long SwapBytes,
    long GcHeapBytes,
    long GcCommittedBytes,
    long GcFragmentedBytes,
    long LargeObjectHeapBytes,
    long PinnedObjectHeapBytes,
    long Gen0Collections,
    long Gen1Collections,
    long Gen2Collections,
    long AllocatedBytesTotal,
    TimeSpan GcPauseTotal,
    double GcPauseTimePercentage,
    int ThreadCount)
{
    /// <summary>Private memory as the budget sees it: anonymous resident memory plus swap (Linux); the private bytes elsewhere.</summary>
    public long PrivateBytes => AnonymousBytes + SwapBytes;
}

/// <summary>
/// Reads the process's memory without allocating (MUST 1: the sampler itself must allocate ~nothing): <c>/proc/self/status</c> is opened once and
/// re-read into a reused buffer (Linux), runtime figures come from <see cref="GC"/>'s counters, and <see cref="GC.GetGCMemoryInfo()"/> (which
/// allocates one small object per call) is only re-read after a garbage collection happened, since its figures only change then. Child processes
/// are read through their own <see cref="ProcFile"/>s with the same buffer. Elsewhere (Windows, macOS) a cached <see cref="Process"/> is refreshed,
/// which allocates; those code paths compile but are not measured here. Thread-safe (one reading at a time).
/// </summary>
internal sealed class ProcessSampler : IDisposable
{
    private readonly object _gate = new();
    private readonly byte[] _buffer = new byte[8192];
    private readonly ProcFile? _status;
    private Process? _self;
    private long _gcKey = -1;
    private long _committed, _fragmented, _loh, _poh, _heapAfterLastGc;
    private double _pausePercent;

    public ProcessSampler()
    {
        if (OperatingSystem.IsLinux()) _status = ProcFile.TryOpen("/proc/self/status");
        if (_status is null) _self = Process.GetCurrentProcess();
    }

    /// <summary>True when the allocation-free Linux reader is in use.</summary>
    public bool IsProcfs => _status is not null;

    public ProcessMemorySample Sample()
    {
        lock (_gate)
        {
            long rss, peak, anon, file, shmem, swap;
            int threads;
            if (_status is not null && _status.Read(_buffer) is var n and > 0)
            {
                var text = _buffer.AsSpan(0, n);
                rss = ProcFile.Field(text, "VmRSS"u8);
                peak = ProcFile.Field(text, "VmHWM"u8);
                anon = ProcFile.Field(text, "RssAnon"u8);
                file = ProcFile.Field(text, "RssFile"u8);
                shmem = ProcFile.Field(text, "RssShmem"u8);
                swap = Math.Max(0, ProcFile.Field(text, "VmSwap"u8));
                threads = (int)ProcFile.Field(text, "Threads"u8, kilobytes: false);
            }
            else
            {
                var self = _self ?? Process.GetCurrentProcess();
                self.Refresh();
                rss = self.WorkingSet64;
                peak = self.PeakWorkingSet64;
                anon = self.PrivateMemorySize64;
                file = shmem = swap = 0;
                threads = self.Threads.Count;
            }
            RefreshGcInfoIfCollected();
            // Never publish a non-positive heap size (the status bar and the budget logic would show nonsense): fall back to the last GC's figure.
            var heap = GC.GetTotalMemory(forceFullCollection: false);
            if (heap <= 0) heap = _heapAfterLastGc;
            return new ProcessMemorySample(
                Stopwatch.GetTimestamp(), rss, peak, anon, file, shmem, swap,
                heap, _committed, _fragmented, _loh, _poh,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalAllocatedBytes(precise: false),
                GC.GetTotalPauseDuration(), _pausePercent, threads);
        }
    }

    /// <summary>The resident set of a child process read through its own handle (bytes); -1 when it can no longer be read (exited).</summary>
    public long ReadChildResident(ProcFile child)
    {
        lock (_gate)
        {
            var n = child.Read(_buffer);
            return n <= 0 ? -1 : ProcFile.Field(_buffer.AsSpan(0, n), "VmRSS"u8);
        }
    }

    private void RefreshGcInfoIfCollected()
    {
        var key = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        if (key == _gcKey) return;
        _gcKey = key;
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        _committed = info.TotalCommittedBytes;
        _heapAfterLastGc = Math.Max(0, info.HeapSizeBytes - info.FragmentedBytes);
        _fragmented = info.FragmentedBytes;
        var generations = info.GenerationInfo;
        _loh = generations.Length > 3 ? generations[3].SizeAfterBytes : 0;
        _poh = generations.Length > 4 ? generations[4].SizeAfterBytes : 0;
        _pausePercent = info.PauseTimePercentage;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _status?.Dispose();
            _self?.Dispose();
            _self = null; // a sample after disposal (a trim finishing late) reads a fresh Process, as the Linux reader falls back to one
        }
    }
}
