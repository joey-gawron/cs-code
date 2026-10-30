using System.Diagnostics.Metrics;
using System.Globalization;

namespace CsCode.Profiling.Testing;

/// <summary>
/// What ONE thread did over a measured window (T55): its CPU time and how long it sat runnable waiting for a CPU (Linux
/// <c>/proc/self/task/&lt;tid&gt;/schedstat</c>: the scheduler's own accounting), plus the durations a toolkit reports through
/// <see cref="System.Diagnostics.Metrics"/> histograms (Avalonia's <c>Avalonia.Diagnostic.Meter</c>: <c>avalonia.ui.measure.time</c>,
/// <c>.arrange.time</c>, <c>.render.time</c>, <c>.input.time</c>…). Put next to a latency report it says where a stall came from: the thread's own
/// work (layout, render, input handlers), waiting for a CPU (a loaded machine), or neither (GC pauses, see <see cref="GcPauseRecorder"/>).
/// Create it ON the thread to observe. A measuring tool: allocates per measurement.
/// </summary>
public sealed class ThreadActivityRecorder : IDisposable
{
    private readonly string? _schedstat;
    private readonly (long Cpu, long Wait) _start;
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, (long Count, double Sum, double Max)> _instruments = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private (long Cpu, long Wait)? _end;

    /// <param name="meters">Names of the meters whose histogram instruments are recorded (e.g. <c>Avalonia.Diagnostic.Meter</c>).</param>
    public ThreadActivityRecorder(params string[] meters)
    {
        _schedstat = CurrentThreadSchedstat();
        _start = Read(_schedstat);
        var names = new HashSet<string>(meters, StringComparer.Ordinal);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (names.Contains(instrument.Meter.Name) && instrument.GetType().Name.StartsWith("Histogram", StringComparison.Ordinal)) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>((i, v, _, _) => Add(i.Name, v));
        _listener.SetMeasurementEventCallback<float>((i, v, _, _) => Add(i.Name, v));
        _listener.SetMeasurementEventCallback<long>((i, v, _, _) => Add(i.Name, v));
        _listener.SetMeasurementEventCallback<int>((i, v, _, _) => Add(i.Name, v));
        _listener.Start();
    }

    private readonly int _thread = Environment.CurrentManagedThreadId;

    /// <summary>The thread's CPU time over the window (ms), or null where schedstat is unavailable.</summary>
    public double? CpuMs => _schedstat is null ? null : ((_end ?? Read(_schedstat)).Cpu - _start.Cpu) / 1e6;

    /// <summary>Time the thread was runnable but not running (ms): CPU contention, not work.</summary>
    public double? RunQueueWaitMs => _schedstat is null ? null : ((_end ?? Read(_schedstat)).Wait - _start.Wait) / 1e6;

    /// <summary>Ends the window (later reports use the values at this point; measurements keep being counted until disposal).</summary>
    public void Stop()
    {
        _end ??= Read(_schedstat);
        _listener.Dispose();
    }

    private void Add(string name, double value)
    {
        // Measurements are reported on the thread that made them: keep the observed thread's apart from a render thread's.
        if (Environment.CurrentManagedThreadId != _thread) name += " (other thread)";
        lock (_gate)
        {
            var (count, sum, max) = _instruments.TryGetValue(name, out var v) ? v : default;
            _instruments[name] = (count + 1, sum + value, Math.Max(max, value));
        }
    }

    public string Report()
    {
        var parts = new List<string>();
        if (CpuMs is { } cpu && RunQueueWaitMs is { } wait)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"thread CPU {cpu:F0} ms, waited for a CPU {wait:F0} ms"));
        lock (_gate)
            foreach (var (name, (count, sum, max)) in _instruments.OrderByDescending(kv => kv.Value.Sum))
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Short(name)} {count}x {sum:F0} ms (max {max:F1})"));
        return string.Join(", ", parts);
    }

    private static string Short(string name) => name.StartsWith("avalonia.", StringComparison.Ordinal) ? name["avalonia.".Length..] : name;

    public void Dispose() => Stop();

    private static string? CurrentThreadSchedstat()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            // /proc/thread-self -> "<pid>/task/<tid>"
            var target = new FileInfo("/proc/thread-self").LinkTarget;
            if (target is null) return null;
            var path = Path.Combine("/proc", target, "schedstat");
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static (long Cpu, long Wait) Read(string? path)
    {
        if (path is null) return default;
        try
        {
            var fields = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return (long.Parse(fields[0], CultureInfo.InvariantCulture), long.Parse(fields[1], CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException) { return default; }
    }
}
