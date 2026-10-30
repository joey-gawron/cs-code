using System.Globalization;
using CsCode.Core.Diagnostics;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Profiling;

/// <summary>
/// The "Memory" output channel (SHOULD): a summary line every <c>cscode.memory.logIntervalSeconds</c> (default 60; 0 = summaries off) — resident
/// set split into anonymous / file-backed / shared, GC heap and committed, collections, allocation rate and GC pause share since the previous line,
/// threads and the tracked children — and one line per trim with its measured effect. Writes from the monitor's thread (output channels are
/// thread-safe and bounded); the channel is created on the first line, so a session that never reaches the first minute shows no extra channel.
/// </summary>
public sealed class MemoryLog : IDisposable
{
    public const string ChannelName = "Memory";

    private readonly MemoryMonitor _monitor;
    private readonly IOutputService _output;
    private readonly DisposableStore _subscriptions = new();
    private readonly object _gate = new();
    private IOutputChannel? _channel;
    private int _intervalSeconds;
    private long _lastLineTicks;
    private ProcessMemorySample _lastLine;
    private bool _disposed;

    public MemoryLog(MemoryMonitor monitor, IOutputService output, IConfigurationService? config = null)
    {
        _monitor = monitor;
        _output = output;
        _intervalSeconds = ReadInterval(config);
        _lastLineTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastLine = monitor.CurrentDetails;
        if (config is not null)
            _subscriptions.Add(config.Changed.Subscribe(e =>
            {
                if (e.Affects(MemoryMonitor.LogIntervalSecondsSetting)) Volatile.Write(ref _intervalSeconds, ReadInterval(config));
            }));
        _subscriptions.Add(monitor.Sampled.Subscribe(OnSampled));
        _subscriptions.Add(monitor.Trimmed.Subscribe(r => Write(Stamp() + " " + r)));
    }

    private static int ReadInterval(IConfigurationService? config) => Math.Max(0, config?.Get(MemoryMonitor.LogIntervalSecondsSetting, 60) ?? 60);

    private void OnSampled(MemorySnapshot snapshot)
    {
        var interval = Volatile.Read(ref _intervalSeconds);
        if (interval <= 0) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        string line;
        lock (_gate)
        {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_lastLineTicks, now);
            if (elapsed.TotalSeconds < interval) return;
            var d = _monitor.CurrentDetails;
            var allocRate = (d.AllocatedBytesTotal - _lastLine.AllocatedBytesTotal) / elapsed.TotalSeconds;
            var pause = (d.GcPauseTotal - _lastLine.GcPauseTotal).TotalMilliseconds / elapsed.TotalMilliseconds * 100;
            line = Summary(d, snapshot.ChildProcessWorkingSetBytes, allocRate, pause,
                (d.Gen0Collections - _lastLine.Gen0Collections, d.Gen1Collections - _lastLine.Gen1Collections, d.Gen2Collections - _lastLine.Gen2Collections));
            _lastLine = d;
            _lastLineTicks = now;
        }
        Write(line);
    }

    /// <summary>One summary line (public for the diagnostics view and tests).</summary>
    public string Summary(ProcessMemorySample d, long childrenBytes, double allocBytesPerSecond, double gcPausePercent, (long G0, long G1, long G2) collections)
    {
        static string Mb(long b) => (b / 1024.0 / 1024.0).ToString("F0", CultureInfo.InvariantCulture);
        var children = _monitor.Children;
        var budget = _monitor.BudgetBytes;
        var swap = d.SwapBytes > 0 ? FormattableString.Invariant($", swap {Mb(d.SwapBytes)}") : "";
        var ofBudget = budget > 0 ? $" of {Mb(budget)} MB budget" : "";
        var kids = children.Count > 0
            ? $" | children {Mb(childrenBytes)} MB: " + string.Join(", ", children.Select(c => $"{c.Name} {(c.Exited ? "exited" : Mb(c.WorkingSetBytes) + " MB")}"))
            : "";
        return FormattableString.Invariant(
            $"{Stamp()} working set {Mb(d.WorkingSetBytes)} MB (anon {Mb(d.AnonymousBytes)}, file {Mb(d.FileBackedBytes)}, shmem {Mb(d.SharedMemoryBytes)}{swap}){ofBudget}") +
            FormattableString.Invariant(
            $" | GC heap {Mb(d.GcHeapBytes)} MB, committed {Mb(d.GcCommittedBytes)}, LOH {Mb(d.LargeObjectHeapBytes)} | GCs +{collections.G0}/{collections.G1}/{collections.G2}") +
            FormattableString.Invariant($", alloc {allocBytesPerSecond / 1024 / 1024:F1} MB/s, paused {gcPausePercent:F2} % | {d.ThreadCount} threads") + kids;
    }

    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private void Write(string line)
    {
        IOutputChannel channel;
        lock (_gate)
        {
            if (_disposed) return;
            channel = _channel ??= _output.GetOrCreate(ChannelName);
        }
        channel.AppendLine(line);
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _subscriptions.Dispose();
    }
}
