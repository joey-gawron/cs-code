using CsCode.Core.Lifecycle;

namespace CsCode.Core.Text;

/// <summary>
/// A line the user marked to come back to (Visual Studio's bookmarks; VS Code users know them from the Bookmarks extension). <see cref="Line"/>
/// is 0-based. A disabled bookmark stays in the list and in the margin (hollow) but navigation skips it, as in Visual Studio.
/// </summary>
public sealed record Bookmark(string Id, Uri Uri, int Line, string? Label = null, bool Enabled = true);

/// <summary>The documents whose bookmarks changed; empty when any may have (another folder's bookmarks were loaded).</summary>
public sealed record BookmarksChangedEventArgs(IReadOnlyCollection<Uri> AffectedUris);

/// <summary>
/// The bookmarks of the workspace: one per (document, line), kept on their lines while the text is edited (a deleted line's bookmark goes, as a
/// breakpoint's does), persisted per workspace, re-anchored by line (clamped) when a file is opened again, moved with a file that CsCode renames
/// or moves and removed with a file it deletes. Thread-safe: reads are immutable snapshots, <see cref="Changed"/> fires on the thread that made
/// the change (an edit's on the UI thread).
/// </summary>
public interface IBookmarkService
{
    /// <summary>Every bookmark in navigation order (<see cref="BookmarkNavigation.Compare"/>: by document path, then line).</summary>
    IReadOnlyList<Bookmark> All { get; }

    /// <summary>The bookmarks of <paramref name="uri"/>, by line.</summary>
    IReadOnlyList<Bookmark> GetForUri(Uri uri);

    /// <summary>Removes the bookmark at <paramref name="line"/> of <paramref name="uri"/>, or adds one there; true when it added one.</summary>
    bool Toggle(Uri uri, int line);

    /// <summary>Adds a bookmark (the existing one when the line has one already); null when the service holds its maximum.</summary>
    Bookmark? Add(Uri uri, int line, string? label = null);

    /// <summary>Takes the <see cref="Bookmark.Label"/> and <see cref="Bookmark.Enabled"/> of <paramref name="updated"/> for the bookmark with its id.</summary>
    void Update(Bookmark updated);

    void Remove(string id);

    /// <summary>Enables or disables every bookmark.</summary>
    void SetEnabledAll(bool enabled);

    /// <summary>Removes the bookmarks of one document.</summary>
    void Clear(Uri uri);

    /// <summary>Removes every bookmark.</summary>
    void ClearAll();

    IEventSource<BookmarksChangedEventArgs> Changed { get; }
}

/// <summary>Command ids of the bookmarks (VS Code's Bookmarks extension's where it has one; Visual Studio's wording in the titles).</summary>
public static class BookmarkCommandIds
{
    public const string Toggle = "bookmarks.toggle";
    public const string JumpToNext = "bookmarks.jumpToNext";
    public const string JumpToPrevious = "bookmarks.jumpToPrevious";
    public const string JumpToNextInDocument = "bookmarks.jumpToNextInDocument";
    public const string JumpToPreviousInDocument = "bookmarks.jumpToPreviousInDocument";
    /// <summary>Clears the bookmarks of the active document.</summary>
    public const string Clear = "bookmarks.clear";
    public const string ClearFromAllFiles = "bookmarks.clearFromAllFiles";
    /// <summary>A quick pick of every bookmark (file, line, line text) that goes to the one picked.</summary>
    public const string List = "bookmarks.list";
    /// <summary>Enables or disables the bookmark on the caret line (Visual Studio's Enable Bookmark).</summary>
    public const string ToggleEnabled = "bookmarks.toggleEnabled";
    /// <summary>Disables every bookmark when any is enabled, else enables them all (Visual Studio's Enable / Disable All Bookmarks).</summary>
    public const string ToggleAllEnabled = "bookmarks.toggleAllEnabled";
    /// <summary>Shows and focuses the Bookmarks tool window (Visual Studio's View &gt; Bookmark Window).</summary>
    public const string ShowWindow = "bookmarks.showWindow";

    /// <summary>The Bookmarks tool window's id (<c>IViewActivator.ShowViewAsync</c>).</summary>
    public const string WindowId = "bookmarks";
}

/// <summary>The order bookmarks are listed and navigated in, and which one Next / Previous go to (pure).</summary>
public static class BookmarkNavigation
{
    /// <summary>The path a document sorts by: the local path of a file, else the whole URI.</summary>
    public static string SortKey(Uri uri) => uri.IsFile ? uri.LocalPath : uri.ToString();

    /// <summary>By document (path, case-insensitive first so the order does not depend on the file system), then line.</summary>
    public static int Compare(Bookmark a, Bookmark b)
    {
        if (a.Uri != b.Uri)
        {
            var ka = SortKey(a.Uri);
            var kb = SortKey(b.Uri);
            var c = StringComparer.OrdinalIgnoreCase.Compare(ka, kb);
            if (c != 0) return c;
            c = string.CompareOrdinal(ka, kb);
            if (c != 0) return c;
        }
        return a.Line.CompareTo(b.Line);
    }

    /// <summary>
    /// The enabled bookmark after (<paramref name="forward"/>) or before the caret at <paramref name="line"/> of <paramref name="from"/> in
    /// <paramref name="ordered"/> (<see cref="Compare"/>'s order), wrapping around; only <paramref name="from"/>'s with
    /// <paramref name="inDocument"/>. A bookmark on the caret line itself is never the answer when there is another. Without a document
    /// (no editor) the first, or the last. Null when there is no enabled bookmark to go to.
    /// </summary>
    public static Bookmark? Next(IReadOnlyList<Bookmark> ordered, Uri? from, int line, bool forward, bool inDocument = false)
    {
        Bookmark? first = null, last = null, after = null, before = null;
        var probe = from is null ? null : new Bookmark("", from, line);
        foreach (var b in ordered)
        {
            if (!b.Enabled || (inDocument && b.Uri != from)) continue;
            first ??= b;
            last = b;
            if (probe is null) continue;
            var c = Compare(b, probe);
            if (c > 0 && after is null) after = b;
            if (c < 0) before = b;
        }
        if (inDocument && from is null) return null;
        if (probe is null) return forward ? first : last;
        return forward ? after ?? first : before ?? last;
    }
}
