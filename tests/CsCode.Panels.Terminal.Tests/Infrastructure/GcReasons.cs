using System.Diagnostics.Tracing;

namespace CsCode.Panels.Terminal.Tests.Infrastructure;

/// <summary>Counts the runtime's GC start events by reason (in-process EventListener), to tell allocation-triggered collections from induced ones.</summary>
public sealed class GcReasons : EventListener
{
    private readonly Dictionary<string, int> _counts = new();

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Informational, (EventKeywords)0x1 /* GC */);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is not { } name || !name.StartsWith("GCStart", StringComparison.Ordinal) || e.PayloadNames is null) return;
        var i = e.PayloadNames.IndexOf("Reason");
        var reason = i >= 0 ? e.Payload?[i]?.ToString() ?? "?" : "?";
        var label = reason switch
        {
            "0" => "alloc-small", "1" => "induced", "2" => "low-memory", "3" => "empty", "4" => "alloc-large", "5" => "oos-small", "6" => "oos-large",
            "7" => "induced-noforce", "8" => "stress", "9" => "low-memory-host", _ => reason,
        };
        lock (_counts) _counts[label] = _counts.GetValueOrDefault(label) + 1;
    }

    public override string ToString()
    {
        lock (_counts) return string.Join(", ", _counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
    }
}
