using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Editor.Bookmarks;

/// <summary>A bookmark as it is saved (primitive types only, so any <see cref="IStateStorage"/> serialiser round-trips it).</summary>
public sealed record PersistedBookmark(string Id, string Uri, int Line, string? Label, bool Enabled);

/// <summary>
/// <see cref="IBookmarkService"/>: the workspace's bookmarks, one per (document, line).
/// <list type="bullet">
/// <item><b>Edits</b>: every change of an open document (<see cref="IDocumentManager.Changed"/>, whatever view, also none) moves the bookmarks
/// below it the way the editor moves breakpoints (<see cref="BreakpointShift"/>: a deleted line's bookmark goes, a joined line keeps the one of
/// the line whose text survives). A keystroke in a document without bookmarks costs one set lookup; one that adds or removes no line break, one
/// call that returns at once.</item>
/// <item><b>Persistence</b>: per workspace in <see cref="IStateStorage"/> (<see cref="StateKey"/>), written <see cref="PersistDelay"/> after the
/// last change on a pool thread, through <see cref="IWorkspaceStateScope"/> so a folder switch never writes one folder's bookmarks into
/// another's; read off the caller's thread when the service starts and when another folder opens. Bookmarks of untitled buffers are never
/// saved, and go when the buffer closes (a Save As moves them to the file).</item>
/// <item><b>Re-anchoring</b>: lines are kept while a file is closed; when it opens again a bookmark past its end moves to its last line (one per
/// line).</item>
/// <item><b>Files</b>: as an <see cref="IFileOperationParticipant"/> (registered by the shell, <c>AddBookmarkFileTracking</c>) a rename or move
/// done in CsCode moves the bookmarks of the file, or of every file under a folder, and a delete removes them.</item>
/// </list>
/// Thread-safe: writes under a lock, reads from an immutable snapshot; <see cref="Changed"/> fires on the thread that made the change.
/// </summary>
public sealed class BookmarkService : IBookmarkService, IFileOperationParticipant, IAsyncDisposable, IDisposable
{
    public const string StateKey = "bookmarks";
    /// <summary>The most bookmarks kept (and restored): a bound, not a limit anyone reaches by hand.</summary>
    public const int MaxBookmarks = 10_000;

    internal static TimeSpan PersistDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>The bookmarks in navigation order and the documents that have any (a keystroke elsewhere is one lookup).</summary>
    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new([]);

        public Snapshot(Bookmark[] all)
        {
            All = all;
            Uris = [.. all.Select(b => b.Uri)];
        }

        public Bookmark[] All { get; }
        public HashSet<Uri> Uris { get; }
    }

    private readonly object _gate = new();
    private readonly Emitter<BookmarksChangedEventArgs> _changed = new();
    private readonly IDocumentManager _documents;
    private readonly IStateStorage? _state;
    private readonly IWorkspaceService? _workspace;
    private readonly ILogger _log;
    private readonly DisposableStore _subs = new();
    private Snapshot _snap = Snapshot.Empty;
    private Timer? _persistTimer;
    private bool _dirty, _loaded;
    private Uri? _loadedFolder;
    // The document that closed last, and the untitled buffer's bookmarks it took with it: a Save As closes the old identity and opens the new one
    // right after, with the same document object, and the bookmarks follow it.
    private (WeakReference<ITextDocument> Doc, Uri Uri, Bookmark[]? Untitled)? _closed;
    private int _disposed;

    public BookmarkService(IDocumentManager documents, IStateStorage? state = null, IWorkspaceService? workspace = null, ILogger? log = null)
    {
        _documents = documents;
        _state = state;
        _workspace = workspace;
        _log = log ?? NullLogger.Instance;
        _subs.Add(documents.Changed.Subscribe(OnDocumentChanged));
        _subs.Add(documents.Opened.Subscribe(e => OnOpened(e.Document)));
        _subs.Add(documents.Closed.Subscribe(e => OnClosed(e.Document)));
        if (workspace is not null) _subs.Add(workspace.FoldersChanged.Subscribe(OnFoldersChangedEvent));
        // Reading the saved list deserialises JSON: never on the thread that resolved the service (the UI thread, when the first editor opens).
        Loaded = Task.Run(LoadInitial);
    }

    /// <summary>Completes once the saved bookmarks were read (tests; anything added before then is kept beside them).</summary>
    internal Task Loaded { get; }

    public IEventSource<BookmarksChangedEventArgs> Changed => _changed;

    /// <summary>Subscribers of <see cref="Changed"/> (leak tests: views unsubscribe when they go).</summary>
    public int ChangedSubscriberCount => _changed.SubscriberCount;

    public IReadOnlyList<Bookmark> All => Volatile.Read(ref _snap).All;

    public IReadOnlyList<Bookmark> GetForUri(Uri uri)
    {
        var snap = Volatile.Read(ref _snap);
        return snap.Uris.Contains(uri) ? [.. snap.All.Where(b => b.Uri == uri)] : [];
    }

    /// <summary>True when <paramref name="uri"/> has a bookmark (one set lookup).</summary>
    internal bool HasAny(Uri uri) => Volatile.Read(ref _snap).Uris.Contains(uri);

    // ------------------------------------------------------------------ edits by the user

    public bool Toggle(Uri uri, int line)
    {
        if (line < 0) return false;
        bool added;
        lock (_gate)
        {
            var all = _snap.All;
            var existing = Array.FindIndex(all, b => b.Line == line && b.Uri == uri);
            if (existing >= 0)
            {
                Set([.. all.Where((_, i) => i != existing)]);
                added = false;
            }
            else
            {
                if (all.Length >= MaxBookmarks) return false;
                Set(Sorted([.. all, new Bookmark(NewId(), uri, line)]));
                added = true;
            }
        }
        Raise([uri]);
        return added;
    }

    public Bookmark? Add(Uri uri, int line, string? label = null)
    {
        if (line < 0) return null;
        Bookmark result;
        lock (_gate)
        {
            var all = _snap.All;
            if (Array.Find(all, b => b.Line == line && b.Uri == uri) is { } existing) return existing;
            if (all.Length >= MaxBookmarks) return null;
            result = new Bookmark(NewId(), uri, line, Normalize(label));
            Set(Sorted([.. all, result]));
        }
        Raise([uri]);
        return result;
    }

    public void Update(Bookmark updated)
    {
        Uri uri;
        lock (_gate)
        {
            var all = _snap.All;
            var i = Array.FindIndex(all, b => b.Id == updated.Id);
            if (i < 0) return;
            var next = all[i] with { Label = Normalize(updated.Label), Enabled = updated.Enabled };
            if (next == all[i]) return;
            var copy = (Bookmark[])all.Clone();
            copy[i] = next;
            Set(copy);
            uri = next.Uri;
        }
        Raise([uri]);
    }

    public void Remove(string id)
    {
        Uri uri;
        lock (_gate)
        {
            var all = _snap.All;
            var i = Array.FindIndex(all, b => b.Id == id);
            if (i < 0) return;
            uri = all[i].Uri;
            Set([.. all.Where((_, j) => j != i)]);
        }
        Raise([uri]);
    }

    public void SetEnabledAll(bool enabled)
    {
        Uri[] uris;
        lock (_gate)
        {
            var all = _snap.All;
            uris = [.. all.Where(b => b.Enabled != enabled).Select(b => b.Uri).Distinct()];
            if (uris.Length == 0) return;
            Set([.. all.Select(b => b.Enabled == enabled ? b : b with { Enabled = enabled })]);
        }
        Raise(uris);
    }

    public void Clear(Uri uri)
    {
        lock (_gate)
        {
            var all = _snap.All;
            if (!_snap.Uris.Contains(uri)) return;
            Set([.. all.Where(b => b.Uri != uri)]);
        }
        Raise([uri]);
    }

    public void ClearAll()
    {
        Uri[] uris;
        lock (_gate)
        {
            if (_snap.All.Length == 0) return;
            uris = [.. _snap.Uris];
            Set([]);
        }
        Raise(uris);
    }

    // ------------------------------------------------------------------ files: rename, move, delete

    /// <summary>
    /// Moves the bookmarks of <paramref name="from"/> to <paramref name="to"/>; when <paramref name="from"/> is a folder, those of every file
    /// under it to the same place under <paramref name="to"/> (a rename or move done in CsCode, a Save As).
    /// </summary>
    public void Move(Uri from, Uri to)
    {
        if (from == to) return;
        var touched = new HashSet<Uri>();
        lock (_gate)
        {
            var all = _snap.All;
            Bookmark[]? next = null;
            for (var i = 0; i < all.Length; i++)
            {
                if (Relocate(all[i].Uri, from, to) is not { } moved) continue;
                next ??= (Bookmark[])all.Clone();
                touched.Add(all[i].Uri);
                touched.Add(moved);
                next[i] = all[i] with { Uri = moved };
            }
            if (next is null) return;
            Set(Sorted(Distinct(next)));
        }
        Raise(touched);
    }

    /// <summary>Removes the bookmarks of <paramref name="uri"/>, or of every file under it when it is a folder (deleted in CsCode, or gone).</summary>
    public void RemoveUnder(Uri uri)
    {
        Uri[] gone;
        lock (_gate)
        {
            var all = _snap.All;
            gone = [.. all.Where(b => IsUnder(b.Uri, uri)).Select(b => b.Uri).Distinct()];
            if (gone.Length == 0) return;
            Set([.. all.Where(b => !IsUnder(b.Uri, uri))]);
        }
        Raise(gone);
    }

    ValueTask<WorkspaceEdit?> IFileOperationParticipant.WillRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct) => ValueTask.FromResult<WorkspaceEdit?>(null);

    ValueTask IFileOperationParticipant.DidRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct)
    {
        foreach (var (oldUri, newUri) in files) Move(oldUri, newUri);
        return ValueTask.CompletedTask;
    }

    ValueTask IFileOperationParticipant.DidCreateFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct) => ValueTask.CompletedTask;

    ValueTask IFileOperationParticipant.DidDeleteFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct)
    {
        foreach (var uri in files) RemoveUnder(uri);
        return ValueTask.CompletedTask;
    }

    private static Uri? Relocate(Uri uri, Uri from, Uri to)
    {
        if (uri == from) return to;
        var root = from.AbsoluteUri.TrimEnd('/') + "/";
        var candidate = uri.AbsoluteUri;
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return Uri.TryCreate(to.AbsoluteUri.TrimEnd('/') + "/" + candidate[root.Length..], UriKind.Absolute, out var moved) ? moved : null;
    }

    private static bool IsUnder(Uri uri, Uri root) =>
        uri == root || uri.AbsoluteUri.StartsWith(root.AbsoluteUri.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ documents: edits, open, close

    private void OnDocumentChanged(TextDocumentChangedEventArgs e)
    {
        var uri = e.Document.Uri;
        if (!Volatile.Read(ref _snap).Uris.Contains(uri)) return; // the keystroke path of every document without bookmarks
        lock (_gate)
        {
            var all = _snap.All;
            List<Bookmark>? mine = null;
            var moved = false;
            foreach (var change in e.Changes)
            {
                if (BreakpointShift.Of(change) is not { } shift) continue; // no line break added or removed
                mine ??= [.. all.Where(b => b.Uri == uri)];
                var (vanishedFrom, vanishedTo, fromLine, delta) = shift;
                for (var i = mine.Count - 1; i >= 0; i--)
                {
                    var line = mine[i].Line;
                    if (line >= vanishedFrom && line <= vanishedTo)
                    {
                        mine.RemoveAt(i); // its line is gone, as a breakpoint's
                        moved = true;
                    }
                    else if (delta != 0 && line >= fromLine)
                    {
                        mine[i] = mine[i] with { Line = Math.Max(0, line + delta) };
                        moved = true;
                    }
                }
            }
            if (!moved) return; // Enter below the last bookmark, a paste above none of them...
            Set(Sorted([.. all.Where(b => b.Uri != uri), .. Distinct(mine!)]));
        }
        Raise([uri]);
    }

    private void OnOpened(ITextDocument doc)
    {
        if (TakeClosed(doc) is { } closed && closed.Uri != doc.Uri)
        {
            // Save As: the same document under its new name keeps its bookmarks (an untitled buffer's were taken out when it closed)
            if (closed.Untitled is { Length: > 0 } restored)
            {
                lock (_gate) Set(Sorted(Distinct([.. _snap.All, .. restored.Select(b => b with { Uri = doc.Uri })])));
                Raise([doc.Uri]);
            }
            else Move(closed.Uri, doc.Uri);
        }
        Reanchor(doc);
    }

    private void OnClosed(ITextDocument doc)
    {
        Bookmark[]? untitled = null;
        if (IsUntitled(doc.Uri) && HasAny(doc.Uri))
        {
            // an untitled buffer's bookmarks live only as long as it does: they go with it (back, under the file's name, after a Save As)
            lock (_gate)
            {
                untitled = [.. _snap.All.Where(b => b.Uri == doc.Uri)];
                Set([.. _snap.All.Where(b => b.Uri != doc.Uri)]);
            }
            Raise([doc.Uri]);
        }
        lock (_gate) _closed = (new WeakReference<ITextDocument>(doc), doc.Uri, untitled);
    }

    private (Uri Uri, Bookmark[]? Untitled)? TakeClosed(ITextDocument doc)
    {
        lock (_gate)
        {
            var closed = _closed;
            _closed = null;
            return closed is { } c && c.Doc.TryGetTarget(out var d) && ReferenceEquals(d, doc) ? (c.Uri, c.Untitled) : null;
        }
    }

    /// <summary>A file opened again (it may have shrunk while it was closed): a bookmark past its end moves to its last line, one per line.</summary>
    private void Reanchor(ITextDocument doc)
    {
        var uri = doc.Uri;
        if (!HasAny(uri)) return;
        var last = Math.Max(0, doc.LineCount - 1);
        lock (_gate)
        {
            var all = _snap.All;
            if (!all.Any(b => b.Uri == uri && b.Line > last)) return;
            Set(Sorted(Distinct([.. all.Select(b => b.Uri == uri && b.Line > last ? b with { Line = last } : b)])));
        }
        Raise([uri]);
    }

    private static bool IsUntitled(Uri uri) => string.Equals(uri.Scheme, "untitled", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ the snapshot

    /// <summary>Under <see cref="_gate"/>.</summary>
    private void Set(Bookmark[] all)
    {
        Volatile.Write(ref _snap, all.Length == 0 ? Snapshot.Empty : new Snapshot(all));
        _dirty = true;
    }

    private static Bookmark[] Sorted(Bookmark[] all)
    {
        Array.Sort(all, BookmarkNavigation.Compare);
        return all;
    }

    /// <summary>One bookmark per (document, line): the first one wins (its label and state).</summary>
    private static Bookmark[] Distinct(IEnumerable<Bookmark> bookmarks)
    {
        var seen = new HashSet<(Uri, int)>();
        return [.. bookmarks.Where(b => seen.Add((b.Uri, b.Line)))];
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string? Normalize(string? label) => string.IsNullOrWhiteSpace(label) ? null : label.Trim();

    private void Raise(IReadOnlyCollection<Uri> uris)
    {
        SchedulePersist();
        _changed.Fire(new BookmarksChangedEventArgs(uris));
    }

    // ------------------------------------------------------------------ persistence

    private Uri? PrimaryFolder => _workspace?.Folders is { Count: > 0 } folders ? folders[0] : null;

    private void LoadInitial()
    {
        Uri? folder;
        lock (_gate) folder = _loadedFolder = PrimaryFolder;
        var saved = ReadSaved();
        lock (_gate)
        {
            if (_loadedFolder != folder) return; // another folder opened meanwhile: its load replaces everything
            // anything added before the saved list arrived stays beside it (the saved one wins on a line both have)
            Set(Sorted(Distinct([.. saved, .. _snap.All])));
            _dirty = _snap.All.Length != saved.Count;
            _loaded = true;
        }
        AfterLoad();
    }

    private void OnFoldersChangedEvent(WorkspaceFoldersChangedEventArgs e) => _ = Task.Run(OnFoldersChanged); // reading state deserialises: off the raiser's thread

    /// <summary>Another folder opened: its bookmarks replace the previous folder's (whose pending changes were saved into that folder first).</summary>
    private void OnFoldersChanged()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var folder = PrimaryFolder;
        lock (_gate)
        {
            if (folder == _loadedFolder && _loaded) return; // a root was added or removed: same workspace, same bookmarks
        }
        PersistNow(); // refused (and so dropped) once the storage holds the new folder's scope: never written into it
        var saved = ReadSaved();
        lock (_gate)
        {
            _loadedFolder = folder;
            var untitled = _snap.All.Where(b => IsUntitled(b.Uri)); // open buffers are not part of a folder
            Set(Sorted(Distinct([.. saved, .. untitled])));
            _dirty = false;
            _loaded = true;
        }
        AfterLoad();
    }

    private void AfterLoad()
    {
        _changed.Fire(new BookmarksChangedEventArgs([]));
        foreach (var doc in _documents.Documents) Reanchor(doc);
        SchedulePersist();
    }

    private List<Bookmark> ReadSaved()
    {
        if (_state is null) return [];
        List<PersistedBookmark>? saved;
        try { saved = _state.Get<List<PersistedBookmark>>(StateKey, StorageScope.Workspace); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or NotSupportedException)
        {
            _log.LogWarning(ex, "Could not read the saved bookmarks");
            return [];
        }
        var list = new List<Bookmark>();
        foreach (var p in saved ?? [])
        {
            if (list.Count >= MaxBookmarks) break;
            if (p is null || p.Line < 0 || !Uri.TryCreate(p.Uri, UriKind.Absolute, out var uri) || IsUntitled(uri)) continue;
            list.Add(new Bookmark(string.IsNullOrEmpty(p.Id) ? NewId() : p.Id, uri, p.Line, Normalize(p.Label), p.Enabled));
        }
        return list;
    }

    private void SchedulePersist()
    {
        if (_state is null || Volatile.Read(ref _disposed) != 0) return;
        lock (_gate)
        {
            if (!_dirty || !_loaded) return; // before the saved list was read, writing would replace it
            _persistTimer ??= new Timer(static s => ((BookmarkService)s!).PersistNow(), this, Timeout.Infinite, Timeout.Infinite);
            _persistTimer.Change(PersistDelay, Timeout.InfiniteTimeSpan); // re-arming = debounce: the file is not rewritten per keystroke
        }
    }

    /// <summary>Writes the bookmarks (not the untitled buffers') into the folder they belong to; a pool thread, or the exit.</summary>
    internal void PersistNow()
    {
        if (_state is null) return;
        List<PersistedBookmark> dto;
        Uri? folder;
        lock (_gate)
        {
            if (!_dirty || !_loaded) return;
            _dirty = false;
            folder = _loadedFolder;
            dto = [.. _snap.All.Where(b => !IsUntitled(b.Uri)).Select(b => new PersistedBookmark(b.Id, b.Uri.ToString(), b.Line, b.Label, b.Enabled))];
        }
        try
        {
            if (_workspace is not null && _state is IWorkspaceStateScope scoped) scoped.TrySetForFolder(folder, StateKey, dto.Count == 0 ? null : dto);
            else _state.Set(StateKey, dto.Count == 0 ? null : dto, StorageScope.Workspace);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
        {
            _log.LogWarning(ex, "Could not save the bookmarks");
        }
    }

    /// <summary>Saves what is pending (the shell disposes this before its last state flush, so a change made just before exit is kept).</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        lock (_gate) _persistTimer?.Dispose();
        PersistNow();
        _subs.Dispose();
        _changed.Clear();
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _ = DisposeAsync();
}
