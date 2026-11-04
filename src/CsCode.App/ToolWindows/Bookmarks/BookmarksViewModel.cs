using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Ui.Collections;
using CsCode.Ui.Threading;

namespace CsCode.App.ToolWindows.Bookmarks;

/// <summary>A row of the Bookmarks window: a file's header, or one of its bookmarks (with the text of its line, read when the row is shown).</summary>
public sealed class BookmarkRow : ObservableObject
{
    private string? _preview;

    private BookmarkRow(Uri uri, Bookmark? bookmark, string? header)
    {
        Uri = uri;
        Bookmark = bookmark;
        HeaderText = header;
    }

    public static BookmarkRow Header(Uri uri, string text) => new(uri, null, text);
    public static BookmarkRow For(Bookmark bookmark) => new(bookmark.Uri, bookmark, null);

    public Uri Uri { get; }
    /// <summary>Null for a file's header row.</summary>
    public Bookmark? Bookmark { get; }
    public bool IsHeader => Bookmark is null;
    public string? HeaderText { get; }
    /// <summary>1-based, as the window shows it.</summary>
    public int Line => (Bookmark?.Line ?? 0) + 1;
    /// <summary>The label, or Visual Studio's "Line N" when the bookmark has none.</summary>
    public string Name => Bookmark?.Label ?? $"Line {Line}";
    public bool HasLabel => Bookmark?.Label is not null;
    public bool Enabled => Bookmark?.Enabled ?? true;
    /// <summary>The text of the line (trimmed); null until read.</summary>
    public string? Preview { get => _preview; internal set => SetProperty(ref _preview, value); }
}

/// <summary>
/// The Bookmarks window's model (Visual Studio's Bookmark Window): every bookmark of the workspace grouped by file, in navigation order, with
/// its label (renamed through an input box), whether it is enabled, its line and the line's text. Rebuilt (latest wins, once per UI turn) when
/// the bookmarks change; the text of a bookmarked line in an open document follows typing, a closed file's is read from disk when its row is
/// shown (a bounded cache of 16 files). Navigating to a bookmark whose file is gone removes that file's bookmarks and says so.
/// </summary>
public sealed class BookmarksViewModel : ObservableObject, IDisposable
{
    private const int MaxCachedFiles = 16;
    private const string RowsKey = "bookmarks.window.rows";
    private const string PreviewKey = "bookmarks.window.previews";

    private readonly IBookmarkService _service;
    private readonly IUiDispatcher _ui;
    private readonly INavigationService? _navigation;
    private readonly IDocumentManager? _documents;
    private readonly IFileSystem? _fs;
    private readonly ICommandService? _commands;
    private readonly IWindowService? _window;
    private readonly DisposableStore _subs = new();
    private readonly Dictionary<Uri, string[]> _lineCache = new();
    private readonly Queue<Uri> _cacheOrder = new();
    private HashSet<Uri> _shownUris = [];
    private string _summary = "";
    private bool _disposed;

    public BookmarksViewModel(IBookmarkService service, IUiDispatcher ui, INavigationService? navigation = null, IDocumentManager? documents = null,
        IFileSystem? fileSystem = null, ICommandService? commands = null, IWindowService? window = null)
    {
        _service = service;
        _ui = ui;
        _navigation = navigation;
        _documents = documents;
        _fs = fileSystem;
        _commands = commands;
        _window = window;
        _subs.Add(service.Changed.Subscribe(_ => _ui.PostCoalesced(RowsKey, Rebuild)));
        // the text of a bookmarked line that is being edited (no line added or removed: the bookmarks did not change)
        if (documents is not null)
            _subs.Add(documents.Changed.Subscribe(e =>
            {
                if (Volatile.Read(ref _shownUris).Contains(e.Document.Uri)) _ui.PostCoalesced(PreviewKey, RefreshOpenPreviews, UiPriority.Background);
            }));
        Rebuild();
    }

    public BulkObservableCollection<BookmarkRow> Rows { get; } = new();

    /// <summary>"3 bookmarks in 2 files", or how to add one.</summary>
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>True when any bookmark is enabled (the toolbar's Enable / Disable All shows which it will do).</summary>
    public bool AnyEnabled => _service.All.Any(b => b.Enabled);

    /// <summary>Raised on the UI thread after the rows were replaced (the view puts the selection back).</summary>
    public event Action? RowsReplaced;

    internal void Rebuild()
    {
        if (_disposed) return;
        var all = _service.All;
        var rows = new List<BookmarkRow>(all.Count + 8);
        var uris = new HashSet<Uri>();
        foreach (var group in all.GroupBy(b => b.Uri)) // All is in navigation order: by file, then line
        {
            var path = group.Key.IsFile ? group.Key.LocalPath : Uri.UnescapeDataString(group.Key.AbsolutePath);
            var count = group.Count();
            rows.Add(BookmarkRow.Header(group.Key, $"{Path.GetFileName(path)}  ({count})  {Path.GetDirectoryName(path)}"));
            foreach (var b in group)
            {
                var row = BookmarkRow.For(b);
                if (_documents?.Get(b.Uri) is { } doc) row.Preview = LineOf(doc, b.Line);
                rows.Add(row);
            }
            uris.Add(group.Key);
        }
        Volatile.Write(ref _shownUris, uris);
        _lineCache.Clear(); // the files may have changed since: read again when the rows are shown
        _cacheOrder.Clear();
        Rows.ReplaceAll(rows);
        var files = uris.Count;
        Summary = all.Count == 0 ? "No bookmarks" : $"{all.Count} bookmark{(all.Count == 1 ? "" : "s")} in {files} file{(files == 1 ? "" : "s")}";
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(AnyEnabled));
        RowsReplaced?.Invoke();
    }

    private void RefreshOpenPreviews()
    {
        if (_disposed || _documents is null) return;
        foreach (var row in Rows)
            if (row.Bookmark is { } b && _documents.Get(b.Uri) is { } doc) row.Preview = LineOf(doc, b.Line);
    }

    private static string LineOf(ITextDocument doc, int line) => line < doc.LineCount ? doc.GetLineText(line).Trim() : "";

    /// <summary>Reads the row's line from its file if it is not open (called when the row is shown).</summary>
    public async Task LoadPreviewAsync(BookmarkRow row, CancellationToken ct = default)
    {
        if (row.Bookmark is not { } b || row.Preview is not null) return;
        if (_documents?.Get(b.Uri) is { } doc)
        {
            row.Preview = LineOf(doc, b.Line);
            return;
        }
        try
        {
            var lines = await LinesOfAsync(b.Uri, ct).ConfigureAwait(true);
            row.Preview = lines is not null && b.Line < lines.Length ? lines[b.Line].Trim() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or NotSupportedException)
        {
            row.Preview = "";
        }
    }

    private async Task<string[]?> LinesOfAsync(Uri uri, CancellationToken ct)
    {
        if (_lineCache.TryGetValue(uri, out var cached)) return cached;
        if (_fs is null) return null;
        var bytes = await _fs.ReadFileAsync(uri, ct).ConfigureAwait(true);
        var lines = await Task.Run(() => Encoding.UTF8.GetString(bytes).TrimStart('﻿').Split('\n'), ct).ConfigureAwait(true);
        _lineCache[uri] = lines;
        _cacheOrder.Enqueue(uri);
        while (_cacheOrder.Count > MaxCachedFiles) _lineCache.Remove(_cacheOrder.Dequeue()); // bounded: files are re-readable
        return lines;
    }

    // ---- actions (the view's toolbar, keys and menu) ----

    /// <summary>Goes to the bookmark (a header: its file's first). A file that no longer exists loses its bookmarks, and the status bar says so.</summary>
    public async Task NavigateAsync(BookmarkRow row)
    {
        var target = row.Bookmark ?? _service.GetForUri(row.Uri).FirstOrDefault();
        if (target is null || _navigation is null) return;
        if (_documents?.Get(target.Uri) is null && _fs is not null)
        {
            FileStat? stat;
            try { stat = await _fs.StatAsync(target.Uri).ConfigureAwait(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { stat = new FileStat(FileType.File, 0, default); }
            if (stat is null)
            {
                _service.Clear(target.Uri);
                _window?.SetStatusBarMessage($"Removed the bookmarks of {Path.GetFileName(target.Uri.IsFile ? target.Uri.LocalPath : target.Uri.AbsolutePath)}: the file no longer exists.", TimeSpan.FromSeconds(4));
                return;
            }
        }
        await _navigation.OpenAsync(target.Uri, TextRange.At(target.Line, 0)).ConfigureAwait(true);
    }

    /// <summary>Visual Studio's rename in the Bookmark Window (an input box here); an empty name removes the label.</summary>
    public async Task RenameAsync(BookmarkRow row)
    {
        if (row.Bookmark is not { } b || _window is null) return;
        var name = await _window.ShowInputBoxAsync(new InputBoxOptions(Title: "Rename Bookmark", Prompt: "A name for the bookmark (empty: none, the window shows its line)", Value: b.Label)).ConfigureAwait(true);
        if (name is null) return;
        _service.Update(b with { Label = name });
    }

    public void SetEnabled(BookmarkRow row, bool enabled)
    {
        if (row.Bookmark is { } b) _service.Update(b with { Enabled = enabled });
        else foreach (var each in _service.GetForUri(row.Uri)) _service.Update(each with { Enabled = enabled });
    }

    /// <summary>Deletes the bookmark; a header row deletes its file's bookmarks.</summary>
    public void Delete(BookmarkRow row)
    {
        if (row.Bookmark is { } b) _service.Remove(b.Id);
        else _service.Clear(row.Uri);
    }

    public void ToggleAllEnabled() => _service.SetEnabledAll(!AnyEnabled);

    /// <summary>Runs one of the bookmark commands (Next, Previous, Clear All: they need the editor's caret or ask first).</summary>
    public async Task RunAsync(string commandId)
    {
        if (_commands is not null && _commands.Has(commandId)) await _commands.ExecuteAsync(commandId).ConfigureAwait(true);
    }

    /// <summary>What Copy puts on the clipboard for a row: <c>path(line): text</c>, as Visual Studio's lists copy a location.</summary>
    public static string CopyText(BookmarkRow row)
    {
        var path = row.Uri.IsFile ? row.Uri.LocalPath : row.Uri.ToString();
        return row.IsHeader ? path : $"{path}({row.Line}): {(row.HasLabel ? row.Name + ": " : "")}{row.Preview}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subs.Dispose();
        RowsReplaced = null;
        Rows.Clear();
        _lineCache.Clear();
    }
}
