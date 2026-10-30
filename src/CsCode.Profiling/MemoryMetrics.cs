using System.Diagnostics.Metrics;

namespace CsCode.Profiling;

/// <summary>
/// The monitor's figures as <see cref="System.Diagnostics.Metrics"/> instruments on the <c>CsCode</c> meter, so
/// <c>dotnet-counters monitor --process-id PID --counters CsCode</c> shows them live (docs/MEMORY.md). Observable instruments read the last sample:
/// a listener's collection adds no sampling of its own, and with no listener attached nothing runs at all.
/// </summary>
internal sealed class MemoryMetrics : IDisposable
{
    public const string MeterName = "CsCode";

    private readonly Meter _meter;
    private readonly Counter<long> _trims;
    private readonly Counter<long> _trimReleased;
    private readonly Histogram<double> _trimDuration;

    public MemoryMetrics(MemoryMonitor monitor)
    {
        _meter = new Meter(MeterName, typeof(MemoryMetrics).Assembly.GetName().Version?.ToString(3));
        _meter.CreateObservableGauge("cscode.memory.working_set", () => monitor.CurrentDetails.WorkingSetBytes, "By", "Resident set of the process");
        _meter.CreateObservableGauge("cscode.memory.private", () => monitor.CurrentDetails.PrivateBytes, "By", "Anonymous resident memory + swap (Linux)");
        _meter.CreateObservableGauge("cscode.memory.file_backed", () => monitor.CurrentDetails.FileBackedBytes, "By", "File-backed resident memory (code, mapped files)");
        _meter.CreateObservableGauge("cscode.memory.gc_heap", () => monitor.CurrentDetails.GcHeapBytes, "By", "Managed heap in use");
        _meter.CreateObservableGauge("cscode.memory.gc_committed", () => monitor.CurrentDetails.GcCommittedBytes, "By", "Memory committed by the GC");
        _meter.CreateObservableGauge("cscode.memory.gc_fragmented", () => monitor.CurrentDetails.GcFragmentedBytes, "By", "Free space inside the managed heap");
        _meter.CreateObservableGauge("cscode.memory.loh", () => monitor.CurrentDetails.LargeObjectHeapBytes, "By", "Large object heap after the last GC");
        _meter.CreateObservableGauge("cscode.memory.children", () => monitor.Current.ChildProcessWorkingSetBytes, "By", "Resident sets of the tracked child processes");
        _meter.CreateObservableGauge("cscode.memory.budget", () => monitor.BudgetBytes, "By", "cscode.memory.budgetMb (0 = unlimited)");
        _meter.CreateObservableGauge("cscode.threads", () => monitor.CurrentDetails.ThreadCount, "{thread}", "Threads of the process");
        _meter.CreateObservableCounter("cscode.gc.allocated", () => monitor.CurrentDetails.AllocatedBytesTotal, "By", "Bytes allocated (the rate is the allocation rate)");
        _meter.CreateObservableCounter("cscode.gc.pause_time", () => monitor.CurrentDetails.GcPauseTotal.TotalMilliseconds, "ms", "Time the runtime was paused for GC");
        _meter.CreateObservableCounter("cscode.gc.gen0_collections", () => monitor.CurrentDetails.Gen0Collections, "{collection}");
        _meter.CreateObservableCounter("cscode.gc.gen1_collections", () => monitor.CurrentDetails.Gen1Collections, "{collection}");
        _meter.CreateObservableCounter("cscode.gc.gen2_collections", () => monitor.CurrentDetails.Gen2Collections, "{collection}");
        _meter.CreateObservableGauge("cscode.gc.pause_time_percentage", () => monitor.CurrentDetails.GcPauseTimePercentage, "%", "GC pause share of the last GC's interval");
        _trims = _meter.CreateCounter<long>("cscode.memory.trims", "{trim}", "Trims run (policy, idle or requested)");
        _trimReleased = _meter.CreateCounter<long>("cscode.memory.trim_released", "By", "Bytes the trimmables reported released");
        _trimDuration = _meter.CreateHistogram<double>("cscode.memory.trim_duration", "ms", "Trimmables + compacting collection + page release");
    }

    public void OnTrimmed(MemoryTrimReport report)
    {
        var level = new KeyValuePair<string, object?>("level", report.Level.ToString());
        _trims.Add(1, level);
        _trimReleased.Add(report.ReleasedByTrimmables, level);
        _trimDuration.Record((report.TrimmablesElapsed + report.CollectElapsed).TotalMilliseconds, level);
    }

    public void Dispose() => _meter.Dispose();
}
