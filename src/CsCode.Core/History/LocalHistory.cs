using CsCode.Core.Lifecycle;

namespace CsCode.Core.History;

// Additive Core contract added by local history (docs/LOCAL-HISTORY.md), implemented by CsCode.Panels.Timeline. Other modules use it to keep
// a file's content before they overwrite it (Source Control's Discard Changes). Optional: without it nothing is kept.

/// <summary>
/// One stored version of a file (VS Code's working-copy history entry). <see cref="Source"/> says how it came to be: a source id
/// (<see cref="LocalHistorySources"/>) or a name the user gave it; <see cref="LocalHistorySources.Label"/> turns it into the text shown.
/// <see cref="Location"/> is the stored copy (a local file).
/// </summary>
public sealed record LocalHistoryEntry(string Id, Uri Resource, DateTimeOffset Timestamp, string Source, Uri Location)
{
    public string? SourceDescription { get; init; }

    public string SourceLabel => LocalHistorySources.Label(Source);
}

/// <summary>The sources CsCode records (VS Code's save sources where VS Code has them; their ids are what <c>entries.json</c> holds).</summary>
public static class LocalHistorySources
{
    /// <summary>A save (VS Code's default source: <c>entries.json</c> leaves it out).</summary>
    public const string FileSaved = "default.source";
    public const string FileMoved = "moved.source";
    public const string FileRenamed = "renamed.source";
    public const string UndoRedo = "undoRedo.source";
    /// <summary>Written after Restore Contents put an entry back.</summary>
    public const string FileRestored = "File Restored";
    /// <summary>The file just before Restore Contents overwrote it on disk (a file that was not open in an editor).</summary>
    public const string BeforeRestore = "Before Restore";
    /// <summary>The file just before Source Control's Discard Changes overwrote or deleted it: restoring it undoes the discard.</summary>
    public const string UndoDiscard = "Undo Discard";

    /// <summary>The text shown for a source: the label of a known id, else the source itself (a name the user gave the entry).</summary>
    public static string Label(string? source) => source switch
    {
        null or "" or FileSaved => "File Saved",
        FileMoved => "File Moved",
        FileRenamed => "File Renamed",
        UndoRedo => "Undo / Redo",
        _ => source,
    };
}

/// <summary>
/// Local history (VS Code's Local History / working-copy history): every save of a file-backed document stores the saved content, bounded
/// per file. Callable from any thread; all file I/O runs on the I/O lane and never on the caller's thread.
/// </summary>
public interface ILocalHistoryService
{
    /// <summary><c>workbench.localHistory.enabled</c>.</summary>
    bool IsEnabled { get; }

    /// <summary>Whether a resource of this kind can have local history (its scheme; exclusions and size are checked when an entry is added).</summary>
    bool CanTrack(Uri resource);

    /// <summary>
    /// Stores the current content of <paramref name="resource"/> (as it is on disk) as an entry; null when nothing was stored (history off,
    /// excluded, too large, missing, or a save of the same content as the newest save). Within <c>workbench.localHistory.mergeWindow</c> of the
    /// newest entry of the same source it replaces that entry.
    /// </summary>
    ValueTask<LocalHistoryEntry?> AddEntryAsync(Uri resource, string? source = null, CancellationToken ct = default);

    /// <summary>The entries of <paramref name="resource"/>, oldest first.</summary>
    ValueTask<IReadOnlyList<LocalHistoryEntry>> GetEntriesAsync(Uri resource, CancellationToken ct = default);

    /// <summary>Every resource that has entries, most recently changed first.</summary>
    ValueTask<IReadOnlyList<Uri>> GetResourcesAsync(CancellationToken ct = default);

    /// <summary>The stored content of <paramref name="entry"/>; null when it is gone.</summary>
    ValueTask<byte[]?> ReadAsync(LocalHistoryEntry entry, CancellationToken ct = default);

    /// <summary>Gives <paramref name="entry"/> a new source (a name); the updated entry, or null when it is gone.</summary>
    ValueTask<LocalHistoryEntry?> RenameEntryAsync(LocalHistoryEntry entry, string source, CancellationToken ct = default);

    /// <summary>Deletes one entry; false when it was already gone.</summary>
    ValueTask<bool> RemoveEntryAsync(LocalHistoryEntry entry, CancellationToken ct = default);

    /// <summary>Deletes every entry of <paramref name="resource"/>, or of every file when it is null.</summary>
    ValueTask RemoveAllAsync(Uri? resource, CancellationToken ct = default);

    /// <summary>The entries of <paramref name="source"/> (and of every file under it, for a folder) follow it to <paramref name="target"/>.</summary>
    ValueTask MoveEntriesAsync(Uri source, Uri target, CancellationToken ct = default);

    /// <summary>The next save of <paramref name="resource"/> (within a few seconds) is recorded with <paramref name="source"/> instead of "File Saved".</summary>
    void SetNextSaveSource(Uri resource, string source);

    /// <summary>Fires (any thread) when the entries of a resource changed; null when any may have.</summary>
    IEventSource<Uri?> Changed { get; }
}
