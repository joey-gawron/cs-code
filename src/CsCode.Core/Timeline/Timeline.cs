using CsCode.Core.Lifecycle;

namespace CsCode.Core.Timeline;

// Additive Core contracts added by local history (docs/LOCAL-HISTORY.md): VS Code's Timeline API in CsCode's terms. The Timeline tool window
// (CsCode.Panels.Timeline) merges the items of every registered provider by time: local history (the same module) and git commits
// (CsCode.Panels.Git). All optional: without a provider the window shows nothing for a file.

/// <summary>
/// One point in a file's history (a local-history entry, a git commit). <see cref="Label"/> is the row's text ("File Saved", a commit's
/// subject), <see cref="Description"/> the dimmed text after it (a commit's author), <see cref="Data"/> the provider's own data, opaque to the
/// Timeline and handed back to the provider unchanged.
/// </summary>
public sealed record TimelineItem(string ProviderId, string Id, Uri Resource, DateTimeOffset Timestamp, string Label)
{
    public string? Description { get; init; }
    public string? Tooltip { get; init; }
    /// <summary>Icon catalog key (a Codicon name).</summary>
    public string? IconKey { get; init; }
    public object? Data { get; init; }
}

/// <summary>A page of a provider's items, newest first; <see cref="Cursor"/> asks for the next (older) page, null when there is none.</summary>
public sealed record TimelinePage(IReadOnlyList<TimelineItem> Items, string? Cursor = null)
{
    public static readonly TimelinePage Empty = new([]);
}

/// <summary>
/// A file as one item of its timeline has it, as the diff editor shows it: a fixed text, or a file to read (<see cref="Uri"/>), and the label
/// that names it in the tab (<c>a.cs (File Saved • 10:04:31)</c>).
/// </summary>
public sealed record TimelineContent(string Label, string? Text = null, Uri? Uri = null);

/// <summary>
/// A source of timeline items (VS Code's <c>TimelineProvider</c>). Called from the UI thread: implementations do their work on their own lane
/// and return quickly; every call is cancellable.
/// </summary>
public interface ITimelineProvider
{
    /// <summary>Stable id ("timeline.localHistory", "git-history"), used by the Timeline's filter.</summary>
    string Id { get; }

    /// <summary>What the filter calls it ("Local History", "Git History").</summary>
    string Label { get; }

    /// <summary>Whether this provider can have items for <paramref name="resource"/> (cheap, no I/O).</summary>
    bool Handles(Uri resource);

    /// <summary>A page of items for <paramref name="resource"/>, newest first, at most <paramref name="limit"/> (a provider without paging may return all).</summary>
    ValueTask<TimelinePage> GetTimelineAsync(Uri resource, string? cursor, int limit, CancellationToken ct = default);

    /// <summary>The file as of <paramref name="item"/>; null when it cannot be shown (binary, gone).</summary>
    ValueTask<TimelineContent?> GetContentAsync(TimelineItem item, CancellationToken ct = default);

    /// <summary>The file just before <paramref name="item"/> (the previous entry, the commit's parent); null when nothing came before it.</summary>
    ValueTask<TimelineContent?> GetPreviousContentAsync(TimelineItem item, CancellationToken ct = default);

    /// <summary>Fires (any thread) when the items of a resource changed; null when any may have.</summary>
    IEventSource<Uri?> Changed { get; }
}
