using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Bookmarks;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// <see cref="BookmarkService"/> without a view (docs/EDITOR.md §7g): toggling, the order Next / Previous follow, bookmarks moved by edits of an
/// open document as breakpoints are, the round trip through the workspace state, re-anchoring a file that shrank while closed, files renamed,
/// moved and deleted in CsCode, another folder's bookmarks, and that nothing per document is retained.
/// </summary>
public sealed class BookmarkServiceTests
{
    private static readonly Uri A = new("file:///work/src/a.cs");
    private static readonly Uri B = new("file:///work/src/b.cs");
    private static readonly Uri C = new("file:///work/zeta/c.cs");

    /// <summary>Workspace state as the Platform's is: values serialised on Set, one scope per folder, a write for a folder no longer open refused.</summary>
    private sealed class FolderState : IStateStorage, IWorkspaceStateScope
    {
        private readonly Dictionary<(Uri?, string), JsonElement> _workspace = new();
        private readonly Dictionary<string, JsonElement> _app = new();
        public Uri? Folder { get; set; }
        public int Writes { get; private set; }

        public T? Get<T>(string key, StorageScope scope = StorageScope.Application)
        {
            lock (_app)
            {
                var found = scope == StorageScope.Workspace ? _workspace.TryGetValue((Folder, key), out var e) : _app.TryGetValue(key, out e);
                return found ? e.Deserialize<T>(CoreJson.Options) : default;
            }
        }

        public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application)
        {
            lock (_app)
            {
                Writes++;
                if (scope == StorageScope.Workspace)
                {
                    if (value is null) _workspace.Remove((Folder, key));
                    else _workspace[(Folder, key)] = JsonSerializer.SerializeToElement(value, CoreJson.Options);
                }
                else if (value is null) _app.Remove(key);
                else _app[key] = JsonSerializer.SerializeToElement(value, CoreJson.Options);
            }
        }

        public bool TrySetForFolder<T>(Uri? folder, string key, T? value)
        {
            lock (_app)
            {
                if (folder != Folder) return false;
                Set(key, value, StorageScope.Workspace);
                return true;
            }
        }

        public void Remove(string key, StorageScope scope = StorageScope.Application) => Set<object>(key, null, scope);
        public Task FlushAsync() => Task.CompletedTask;
        public string? Raw(Uri? folder) { lock (_app) return _workspace.TryGetValue((folder, BookmarkService.StateKey), out var e) ? e.GetRawText() : null; }
    }

    private sealed class FakeFolders : IWorkspaceService
    {
        private readonly Emitter<WorkspaceFoldersChangedEventArgs> _changed = new();
        private readonly FolderState _state;
        public FakeFolders(FolderState state, Uri? folder) { _state = state; Current = folder; state.Folder = folder; }
        public Uri? Current { get; private set; }
        public IReadOnlyList<Uri> Folders => Current is null ? [] : [Current];
        public string? Name => Current?.Segments[^1];
        public ValueTask OpenFolderAsync(Uri folder, CancellationToken ct = default)
        {
            var old = Current;
            _state.Folder = folder; // the storage swaps its scope before the change is announced (Platform's IWorkspaceBound)
            Current = folder;
            _changed.Fire(new WorkspaceFoldersChangedEventArgs([folder], old is null ? [] : [old]));
            return ValueTask.CompletedTask;
        }
        public ValueTask CloseFolderAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => _changed;
        public IFileSystem FileSystem => throw new NotSupportedException();
        public IFileWatcher Watcher => throw new NotSupportedException();
        public IReadOnlyList<Uri> RecentFolders => [];
        public IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static InMemoryTextDocument Doc(Uri uri, int lines) =>
        new(uri, "csharp", string.Join("\n", Enumerable.Range(0, lines).Select(i => $"line {i}")) + "\n");

    private static IEnumerable<(string, int)> Lines(IBookmarkService s) => s.All.Select(b => (b.Uri.Segments[^1], b.Line));

    // ------------------------------------------------------------------ toggling and order

    [Fact]
    public async Task Toggle_adds_one_bookmark_per_line_and_removes_it_again_and_says_which_document_changed()
    {
        await using var service = new BookmarkService(new DocumentManager());
        await service.Loaded;
        var changed = new List<Uri>();
        using var sub = service.Changed.Subscribe(e => changed.AddRange(e.AffectedUris));

        Assert.True(service.Toggle(A, 3));
        Assert.Same(service.Add(A, 3), service.GetForUri(A).Single()); // adding where one is: the one that is there
        Assert.True(service.Toggle(B, 0));
        Assert.Equal([("a.cs", 3), ("b.cs", 0)], Lines(service));
        Assert.False(service.Toggle(A, 3));
        Assert.Equal([("b.cs", 0)], Lines(service));
        Assert.Equal([A, B, A], changed);
        Assert.False(service.Toggle(A, -1)); // no such line
        Assert.Single(service.All);
    }

    [Fact]
    public async Task Bookmarks_are_listed_by_file_path_then_line_whatever_order_they_were_added_in()
    {
        await using var service = new BookmarkService(new DocumentManager());
        service.Toggle(C, 1);
        service.Toggle(B, 9);
        service.Toggle(A, 20);
        service.Toggle(A, 2);
        service.Toggle(B, 1);
        Assert.Equal([("a.cs", 2), ("a.cs", 20), ("b.cs", 1), ("b.cs", 9), ("c.cs", 1)], Lines(service));
    }

    [Fact]
    public void Next_and_previous_wrap_across_files_skip_disabled_bookmarks_and_stay_in_the_document_when_asked()
    {
        Bookmark Bm(Uri uri, int line, bool enabled = true) => new(uri.Segments[^1] + line, uri, line, Enabled: enabled);
        var all = new[] { Bm(A, 2), Bm(A, 20), Bm(B, 1, enabled: false), Bm(B, 9), Bm(C, 1) };
        Array.Sort(all, BookmarkNavigation.Compare);
        string? Next(Uri? from, int line, bool forward, bool inDocument = false) => BookmarkNavigation.Next(all, from, line, forward, inDocument)?.Id;

        Assert.Equal("a.cs20", Next(A, 2, forward: true)); // on a bookmark: the next one, not itself
        Assert.Equal("a.cs20", Next(A, 5, forward: true));
        Assert.Equal("b.cs9", Next(A, 20, forward: true)); // to the next file; b.cs:1 is disabled
        Assert.Equal("a.cs2", Next(C, 1, forward: true)); // past the last: wraps to the first
        Assert.Equal("c.cs1", Next(A, 2, forward: false)); // before the first: wraps to the last
        Assert.Equal("a.cs20", Next(B, 5, forward: false)); // b.cs:1 skipped
        Assert.Equal("b.cs9", Next(new Uri("file:///work/src/aa.cs"), 0, forward: true)); // a document without bookmarks: by its place in the order
        Assert.Equal("a.cs2", Next(null, 0, forward: true)); // no editor: the first, or the last
        Assert.Equal("c.cs1", Next(null, 0, forward: false));

        Assert.Equal("a.cs2", Next(A, 20, forward: true, inDocument: true)); // wraps inside the document
        Assert.Equal("a.cs20", Next(A, 2, forward: false, inDocument: true));
        Assert.Equal("b.cs9", Next(B, 9, forward: true, inDocument: true)); // the only enabled one of b.cs
        Assert.Null(Next(new Uri("file:///work/none.cs"), 0, forward: true, inDocument: true));
        Assert.Null(BookmarkNavigation.Next([Bm(A, 1, enabled: false)], A, 0, forward: true));
    }

    // ------------------------------------------------------------------ edits

    [Fact]
    public async Task Edits_move_the_bookmarks_below_them_and_a_deleted_line_takes_its_bookmark_with_it()
    {
        var documents = new DocumentManager();
        await using var service = new BookmarkService(documents);
        var doc = Doc(A, 10);
        using var registration = documents.Register(doc);
        service.Toggle(A, 2);
        service.Toggle(A, 5);
        service.Toggle(A, 8);
        service.Toggle(B, 5); // another file: never moved by edits of this one

        doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "// one\n// two\n")]); // two whole lines above all of them
        Assert.Equal([2 + 2, 5 + 2, 8 + 2], service.GetForUri(A).Select(b => b.Line));

        doc.ApplyEdits([TextEdit.Delete(TextRange.FromLines(7, 0, 8, 0))]); // the whole line 7 (old line 5) and its bookmark go
        Assert.Equal([4, 9], service.GetForUri(A).Select(b => b.Line));

        doc.ApplyEdits([TextEdit.Insert(new TextPosition(4, 3), "\n")]); // Enter inside a bookmarked line: its text before the break keeps it
        Assert.Equal([4, 10], service.GetForUri(A).Select(b => b.Line));

        doc.ApplyEdits([TextEdit.Delete(TextRange.FromLines(9, 6, 10, 0))]); // Backspace at the start of line 10: joined to line 9, which survives
        Assert.Equal([4], service.GetForUri(A).Select(b => b.Line));
        Assert.Equal([5], service.GetForUri(B).Select(b => b.Line));
    }

    [Fact]
    public async Task Typing_without_a_line_break_or_below_the_bookmarks_changes_nothing()
    {
        var documents = new DocumentManager();
        await using var service = new BookmarkService(documents);
        var doc = Doc(A, 10);
        using var registration = documents.Register(doc);
        service.Toggle(A, 3);
        var events = 0;
        using var sub = service.Changed.Subscribe(_ => events++);
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(1, 2), "abc")]);
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(3, 0), "x")]);
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(6, 0), "below\n")]);
        Assert.Equal(0, events);
        Assert.Equal(3, service.GetForUri(A).Single().Line);
    }

    // ------------------------------------------------------------------ persistence

    [Fact]
    public async Task Bookmarks_labels_and_states_survive_a_restart_through_the_workspace_state()
    {
        var state = new FolderState { Folder = new Uri("file:///work/") };
        var folders = new FakeFolders(state, state.Folder);
        var first = new BookmarkService(new DocumentManager(), state, folders);
        await first.Loaded;
        first.Toggle(A, 4);
        var named = first.Add(B, 7, "  entry point  ")!;
        first.Update(named with { Enabled = false });
        first.Toggle(new Uri("untitled:Untitled-1"), 0); // an untitled buffer's are never saved
        await first.DisposeAsync(); // the exit saves what the debounce had not written yet

        Assert.Contains("a.cs", state.Raw(state.Folder));
        Assert.DoesNotContain("untitled", state.Raw(state.Folder));
        await using var second = new BookmarkService(new DocumentManager(), state, folders);
        await second.Loaded;
        Assert.Equal([("a.cs", 4), ("b.cs", 7)], Lines(second));
        var restored = second.GetForUri(B).Single();
        Assert.Equal(("entry point", false, named.Id), (restored.Label, restored.Enabled, restored.Id));
    }

    [Fact]
    public async Task Changes_are_written_once_typing_settles_not_once_per_keystroke()
    {
        var old = BookmarkService.PersistDelay;
        BookmarkService.PersistDelay = TimeSpan.FromMilliseconds(150);
        try
        {
            var state = new FolderState();
            var documents = new DocumentManager();
            await using var service = new BookmarkService(documents, state);
            await service.Loaded;
            var doc = Doc(A, 5);
            using var registration = documents.Register(doc);
            service.Toggle(A, 4);
            for (var i = 0; i < 20; i++) doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "\n")]); // Enter, Enter, ... above the bookmark
            var writes = state.Writes;
            await Support.TestHost.WaitUntil(() => state.Raw(null)?.Contains("\"line\":24") == true, "the moved bookmark saved");
            Assert.True(state.Writes - writes <= 1, $"{state.Writes - writes} writes for one burst of typing");
        }
        finally { BookmarkService.PersistDelay = old; }
    }

    [Fact]
    public async Task A_file_that_shrank_while_it_was_closed_gets_its_bookmarks_on_its_last_line()
    {
        var documents = new DocumentManager();
        await using var service = new BookmarkService(documents);
        service.Toggle(A, 1);
        service.Toggle(A, 30);
        service.Add(A, 40, "end");
        using var registration = documents.Register(Doc(A, 10)); // opened again with 11 lines (the last one empty)
        Assert.Equal([1, 10], service.GetForUri(A).Select(b => b.Line)); // clamped, one per line: the first of the two that land there stays
        Assert.Null(service.GetForUri(A)[1].Label);
    }

    // ------------------------------------------------------------------ files

    [Fact]
    public async Task A_file_or_folder_renamed_in_CsCode_keeps_its_bookmarks_and_a_deleted_one_loses_them()
    {
        await using var service = new BookmarkService(new DocumentManager());
        IFileOperationParticipant files = service;
        service.Toggle(A, 1);
        service.Toggle(B, 2);
        service.Toggle(C, 3);

        var renamed = new Uri("file:///work/src/renamed.cs");
        await files.DidRenameFilesAsync([(A, renamed)], default);
        Assert.Equal([("b.cs", 2), ("renamed.cs", 1), ("c.cs", 3)], Lines(service));

        // a folder moved: every file under it (not zeta/, whose name only starts the same way as src/ does not)
        var moved = new Uri("file:///work/lib/");
        await files.DidRenameFilesAsync([(new Uri("file:///work/src"), moved)], default);
        Assert.Equal(["file:///work/lib/b.cs", "file:///work/lib/renamed.cs", "file:///work/zeta/c.cs"], service.All.Select(b => b.Uri.AbsoluteUri));

        await files.DidDeleteFilesAsync([new Uri("file:///work/lib/renamed.cs")], default);
        Assert.Equal(["b.cs", "c.cs"], service.All.Select(b => b.Uri.Segments[^1]));
        await files.DidDeleteFilesAsync([new Uri("file:///work/lib")], default);
        Assert.Equal(["c.cs"], service.All.Select(b => b.Uri.Segments[^1]));
    }

    [Fact]
    public async Task An_untitled_buffer_loses_its_bookmarks_when_it_closes()
    {
        var documents = new DocumentManager();
        await using var service = new BookmarkService(documents);
        var untitled = new Uri("untitled:Untitled-3");
        var registration = documents.Register(Doc(untitled, 5));
        service.Toggle(untitled, 2);
        service.Toggle(A, 1);
        registration.Dispose();
        Assert.Equal([A], service.All.Select(b => b.Uri));
    }

    [Fact]
    public async Task Another_folder_shows_its_own_bookmarks_and_the_previous_folder_s_are_never_written_into_it()
    {
        var one = new Uri("file:///one/");
        var two = new Uri("file:///two/");
        var state = new FolderState();
        var folders = new FakeFolders(state, one);
        await using var service = new BookmarkService(new DocumentManager(), state, folders);
        await service.Loaded;
        service.Toggle(A, 1);
        service.PersistNow();
        var reloaded = new TaskCompletionSource();
        using var sub = service.Changed.Subscribe(e => { if (e.AffectedUris.Count == 0) reloaded.TrySetResult(); });

        await folders.OpenFolderAsync(two);
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(service.All);
        service.Toggle(B, 2);
        service.PersistNow();
        Assert.Contains("b.cs", state.Raw(two));
        Assert.DoesNotContain("a.cs", state.Raw(two));
        Assert.Contains("a.cs", state.Raw(one));
        Assert.DoesNotContain("b.cs", state.Raw(one));
    }

    // ------------------------------------------------------------------ memory

    [Fact]
    public async Task A_closed_document_with_bookmarks_is_not_retained_by_the_service()
    {
        var documents = new DocumentManager();
        await using var service = new BookmarkService(documents);
        LeakAssert.NotRetained(() =>
        {
            var doc = Doc(A, 20);
            var registration = documents.Register(doc);
            service.Toggle(A, 3);
            doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "\n")]);
            registration.Dispose();
            return [doc];
        });
        Assert.Equal(4, service.GetForUri(A).Single().Line); // kept, by line, for when the file opens again
    }
}
