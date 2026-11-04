using System.Globalization;
using CsCode.Core.History;
using CsCode.Core.Lifecycle;
using CsCode.Core.Timeline;

namespace CsCode.Panels.Timeline.LocalHistory;

/// <summary>Local history in the Timeline (VS Code's "Local History" source): one item per entry, its <see cref="TimelineItem.Data"/> the entry.</summary>
public sealed class LocalHistoryTimelineProvider(ILocalHistoryService history) : ITimelineProvider
{
    public const string ProviderId = "timeline.localHistory";

    public string Id => ProviderId;
    public string Label => "Local History";
    public IEventSource<Uri?> Changed => history.Changed;

    public bool Handles(Uri resource) => history.CanTrack(resource);

    public async ValueTask<TimelinePage> GetTimelineAsync(Uri resource, string? cursor, int limit, CancellationToken ct = default)
    {
        // A file has at most workbench.localHistory.maxFileEntries entries (50): one page.
        var entries = await history.GetEntriesAsync(resource, ct).ConfigureAwait(false);
        var items = new TimelineItem[entries.Count];
        for (var i = 0; i < entries.Count; i++) items[i] = ToItem(entries[entries.Count - 1 - i]);
        return new TimelinePage(items);
    }

    public static TimelineItem ToItem(LocalHistoryEntry e) => new(ProviderId, e.Id, e.Resource, e.Timestamp, e.SourceLabel)
    {
        Tooltip = $"{FormatTime(e.Timestamp)}\n{e.SourceLabel}{(e.SourceDescription is { } d ? $" ({d})" : "")}",
        IconKey = "history",
        Data = e,
    };

    public ValueTask<TimelineContent?> GetContentAsync(TimelineItem item, CancellationToken ct = default) =>
        ValueTask.FromResult(item.Data is LocalHistoryEntry e ? Content(e) : null);

    public async ValueTask<TimelineContent?> GetPreviousContentAsync(TimelineItem item, CancellationToken ct = default)
    {
        if (item.Data is not LocalHistoryEntry e) return null;
        var entries = await history.GetEntriesAsync(e.Resource, ct).ConfigureAwait(false);
        var i = -1;
        for (var k = 0; k < entries.Count; k++) if (entries[k].Id == e.Id) { i = k; break; }
        return i > 0 ? Content(entries[i - 1]) : null;
    }

    /// <summary>An entry as the diff editor shows it: its stored copy, named <c>a.cs (File Saved • 04/11/2026 10:04:31)</c>.</summary>
    public static TimelineContent Content(LocalHistoryEntry e) => new(EntryLabel(e), Uri: e.Location);

    public static string EntryLabel(LocalHistoryEntry e) => $"{FileName(e.Resource)} ({e.SourceLabel} • {FormatTime(e.Timestamp)})";

    /// <summary>Local date and time with seconds (two entries a minute apart are told apart).</summary>
    public static string FormatTime(DateTimeOffset t) => t.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);

    public static string FileName(Uri resource) => Uri.UnescapeDataString(resource.AbsolutePath.TrimEnd('/') is var p && p.LastIndexOf('/') is var i and >= 0 ? p[(i + 1)..] : p);
}
