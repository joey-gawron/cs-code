using System.Diagnostics.Tracing;

namespace CsCode.Profiling.Testing;

/// <summary>
/// Records every GC pause of this process (suspension begin to restart end) from the runtime's own events, in-process: an
/// <see cref="EventListener"/> on <c>Microsoft-Windows-DotNETRuntime</c> with the GC keyword. Gives per-pause p50/p99/max, collections by
/// generation and the triggers (alloc_small, induced, low_memory…), which the GC matrix and budget tests report. The events arrive on the
/// runtime's dispatch thread a little after the fact; <see cref="Report"/> after the measured work. Allocates per event: a measuring tool, not for
/// production.
/// </summary>
public sealed class GcPauseRecorder : EventListener
{
    private const int GcKeyword = 0x1;
    private readonly LatencyHistogram _pauses = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _reasons = new(StringComparer.Ordinal);
    private readonly int[] _generations = new int[3];
    private DateTime _suspendedAt;
    private volatile bool _enabled = true;

    public LatencyHistogram Pauses => _pauses;

    public void Stop() => _enabled = false;

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(eventSource, EventLevel.Informational, (EventKeywords)GcKeyword);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!_enabled) return;
        switch (e.EventName)
        {
            case { } n when n.StartsWith("GCSuspendEEBegin", StringComparison.Ordinal):
                _suspendedAt = e.TimeStamp;
                break;
            case { } n when n.StartsWith("GCRestartEEEnd", StringComparison.Ordinal):
                if (_suspendedAt != default) _pauses.RecordMicroseconds((long)((e.TimeStamp - _suspendedAt).TotalMilliseconds * 1000));
                _suspendedAt = default;
                break;
            case { } n when n.StartsWith("GCStart", StringComparison.Ordinal):
                var depth = Payload(e, "Depth");
                var reason = Payload(e, "Reason");
                lock (_gate)
                {
                    if (depth is >= 0 and < 3) _generations[depth]++;
                    var name = ReasonName(reason);
                    _reasons[name] = _reasons.TryGetValue(name, out var c) ? c + 1 : 1;
                }
                break;
        }
    }

    private static int Payload(EventWrittenEventArgs e, string name)
    {
        var i = e.PayloadNames?.IndexOf(name) ?? -1;
        return i >= 0 && e.Payload?[i] is { } v ? Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture) : -1;
    }

    private static string ReasonName(int reason) => reason switch
    {
        0 => "alloc_small", 1 => "induced", 2 => "low_memory", 3 => "empty", 4 => "alloc_large", 5 => "oos_soh", 6 => "oos_loh", 7 => "induced_noforce",
        8 => "internal_tuning", 9 => "induced_low_memory", 10 => "induced_compacting", 11 => "low_memory_host", 12 => "pm_full_gc", 13 => "low_memory_host_blocking",
        _ => "reason " + reason,
    };

    public string Report()
    {
        lock (_gate)
            return $"GC pauses {_pauses.Report()}; collections gen0/1/2 {_generations[0]}/{_generations[1]}/{_generations[2]} ({string.Join(", ", _reasons.OrderByDescending(r => r.Value).Select(r => $"{r.Key} {r.Value}"))})";
    }

    public (int Gen0, int Gen1, int Gen2) Collections { get { lock (_gate) return (_generations[0], _generations[1], _generations[2]); } }
}
