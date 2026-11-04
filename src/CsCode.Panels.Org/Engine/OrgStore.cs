using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;

namespace CsCode.Panels.Org.Engine;

/// <summary>What the store reads: the agenda's files and folders (full paths), the TODO states of files without <c>#+TODO</c>.</summary>
public sealed record OrgStoreOptions(IReadOnlyList<string> Entries, OrgTodoKeywords Keywords);

/// <summary>
/// The agenda's documents. Finds the <c>.org</c> files of the agenda's entries (a folder's own files, not its subfolders', as org reads a
/// folder), reads each from its open editor when it has one (unsaved text included) else from disk, parses it off the UI thread and keeps the
/// parse until the file or the editor's text changes. Refreshes are coalesced (at most one running and one queued) and follow the file
/// watcher, the open documents and the settings. Writes the edits of the agenda's commands to files that are not open.
/// </summary>
public sealed class OrgStore : IDisposable
{
    /// <summary>A file larger than this is left out of the agenda (an org file this big is not a to-do list).</summary>
    public const long MaxFileBytes = 8 * 1024 * 1024;

    private readonly IFileSystem _fs;
    private readonly IFileWatcher? _watcher;
    private readonly IDocumentManager? _documents;
    private readonly Func<OrgStoreOptions> _options;
    private readonly DisposableStore _store = new();
    private readonly Emitter<IReadOnlyList<OrgDocument>> _changed = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Uri, (string Stamp, OrgDocument Document)> _cache = new();
    private readonly object _lock = new();
    private readonly List<IDisposable> _watches = [];
    private string _watchedKey = "";
    private int _queued;
    private CancellationTokenSource? _debounce;
    private bool _disposed;

    public OrgStore(IFileSystem fs, IFileWatcher? watcher, IDocumentManager? documents, Func<OrgStoreOptions> options)
    {
        _fs = fs;
        _watcher = watcher;
        _documents = documents;
        _options = options;
        if (documents is not null)
        {
            _store.Add(documents.Changed.Subscribe(e => { if (IsTracked(e.Document.Uri)) ScheduleRefresh(); }));
            _store.Add(documents.Opened.Subscribe(e => { if (IsTracked(e.Document.Uri)) ScheduleRefresh(); }));
            _store.Add(documents.Closed.Subscribe(e => { if (IsTracked(e.Document.Uri)) ScheduleRefresh(); }));
            _store.Add(documents.Saved.Subscribe(e => { if (IsTracked(e.Document.Uri)) ScheduleRefresh(); }));
        }
    }

    /// <summary>The documents of the last refresh, in the order of the agenda's entries (a folder's files by name).</summary>
    public IReadOnlyList<OrgDocument> Documents { get; private set; } = [];

    /// <summary>The files the last refresh found.</summary>
    public IReadOnlyList<Uri> Files { get; private set; } = [];

    /// <summary>Entries of the agenda that do not exist (a folder not created yet), from the last refresh.</summary>
    public IReadOnlyList<string> Missing { get; private set; } = [];

    /// <summary>True once a refresh has completed.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Fires (on a worker thread) after each refresh that changed <see cref="Documents"/>.</summary>
    public IEventSource<IReadOnlyList<OrgDocument>> Changed => _changed;

    /// <summary>Whether <paramref name="uri"/> is (or would be) one of the agenda's files.</summary>
    public bool IsTracked(Uri uri)
    {
        if (!uri.IsFile || !uri.LocalPath.EndsWith(".org", StringComparison.OrdinalIgnoreCase)) return false;
        var path = Path.GetFullPath(uri.LocalPath);
        foreach (var entry in _options().Entries)
            if (string.Equals(entry, path, PathComparison) || string.Equals(Path.GetDirectoryName(path), entry.TrimEnd('/', '\\'), PathComparison)) return true;
        return false;
    }

    private static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Refreshes after a short quiet period (typing in an org file, a burst of watcher events).</summary>
    public void ScheduleRefresh(int delayMs = 400)
    {
        if (_disposed) return;
        CancellationTokenSource cts;
        lock (_lock)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = cts = new CancellationTokenSource();
        }
        _ = Task.Delay(delayMs, cts.Token).ContinueWith(t => { if (!t.IsCanceled) _ = RefreshAsync(); }, TaskScheduler.Default);
    }

    /// <summary>Reads the agenda's files again (a refresh already running is followed by one more, however many are asked for meanwhile).</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _queued, 1) == 1 && _gate.CurrentCount == 0) return; // one is queued behind the running one already
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _queued, 0);
            if (_disposed) return;
            await RefreshCoreAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var options = _options();
        var files = new List<Uri>();
        var missing = new List<string>();
        foreach (var entry in options.Entries)
        {
            var uri = new Uri(entry);
            FileStat? stat;
            try { stat = await _fs.StatAsync(uri, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stat = null; }
            if (stat is null) { missing.Add(entry); continue; }
            if (stat.Type == FileType.Directory)
            {
                IReadOnlyList<DirectoryEntry> children;
                try { children = await _fs.ReadDirectoryAsync(uri, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var c in children.Where(c => c.Type == FileType.File && IsOrgFileName(c.Name)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                    files.Add(new Uri(Path.Combine(entry, c.Name)));
            }
            else files.Add(uri);
        }
        files = [.. files.Distinct()];
        UpdateWatches(options.Entries);

        var docs = new List<OrgDocument>(files.Count);
        foreach (var uri in files)
        {
            ct.ThrowIfCancellationRequested();
            if (await LoadAsync(uri, options.Keywords, ct).ConfigureAwait(false) is { } doc) docs.Add(doc);
        }
        lock (_lock)
            foreach (var stale in _cache.Keys.Where(k => !files.Contains(k)).ToList()) _cache.Remove(stale);

        var changed = !Loaded || docs.Count != Documents.Count || docs.Where((d, i) => !ReferenceEquals(d, Documents[i])).Any() || !missing.SequenceEqual(Missing);
        Files = files;
        Missing = missing;
        Documents = docs;
        Loaded = true;
        if (changed) _changed.Fire(docs);
    }

    /// <summary>Org files and not editors' lock or autosave files (<c>.#notes.org</c>, <c>#notes.org#</c>) or hidden files.</summary>
    internal static bool IsOrgFileName(string name) =>
        name.EndsWith(".org", StringComparison.OrdinalIgnoreCase) && !name.StartsWith('.') && !name.StartsWith('#');

    private async Task<OrgDocument?> LoadAsync(Uri uri, OrgTodoKeywords keywords, CancellationToken ct)
    {
        string stamp;
        string? text = null;
        if (_documents?.Get(uri) is { } open)
        {
            stamp = "doc:" + open.Version + ":" + keywords.GetHashCode();
            lock (_lock) if (_cache.TryGetValue(uri, out var hit) && hit.Stamp == stamp) return hit.Document;
            text = open.GetText();
        }
        else
        {
            FileStat? stat;
            try { stat = await _fs.StatAsync(uri, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            if (stat is null || stat.Size > MaxFileBytes) return null;
            stamp = "disk:" + stat.ModifiedUtc.UtcTicks + ":" + stat.Size + ":" + keywords.GetHashCode();
            lock (_lock) if (_cache.TryGetValue(uri, out var hit) && hit.Stamp == stamp) return hit.Document;
        }
        if (text is null)
        {
            try { text = OrgText.Decode(await _fs.ReadFileAsync(uri, ct).ConfigureAwait(false), out _); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        var doc = OrgDocument.Parse(text, keywords, uri: uri);
        lock (_lock) _cache[uri] = (stamp, doc);
        return doc;
    }

    /// <summary>The current text of one file, parsed (its open editor's when it has one), or null when it cannot be read.</summary>
    public async Task<OrgDocument?> ReadAsync(Uri uri, CancellationToken ct = default)
    {
        var keywords = _options().Keywords;
        if (_documents?.Get(uri) is { } open) return OrgDocument.Parse(open.GetText(), keywords, uri: uri);
        try { return OrgDocument.Parse(OrgText.Decode(await _fs.ReadFileAsync(uri, ct).ConfigureAwait(false), out _), keywords, uri: uri); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Applies edits computed against <paramref name="expected"/> (the file's text as it was read) to a file that is not open: read, check that it
    /// is still that text, apply, write (keeping its line endings and byte order mark). False when the file changed meanwhile.
    /// </summary>
    public async Task<bool> WriteEditsAsync(Uri uri, OrgDocument expected, IReadOnlyList<TextEdit> edits, CancellationToken ct = default)
    {
        var bytes = await _fs.ReadFileAsync(uri, ct).ConfigureAwait(false);
        var text = OrgText.Decode(bytes, out var bom);
        if (!OrgDocument.SplitLines(text).SequenceEqual(expected.Lines)) return false;
        var updated = OrgText.Apply(text, OrgText.WithEol(edits, OrgText.DetectEol(text)));
        await _fs.WriteFileAsync(uri, OrgText.Encode(updated, bom), createParents: true, ct).ConfigureAwait(false);
        ScheduleRefresh(50);
        return true;
    }

    /// <summary>Creates <paramref name="uri"/> with <paramref name="initialText"/> (and its folder) when it does not exist yet.</summary>
    public async Task EnsureFileAsync(Uri uri, string initialText, CancellationToken ct = default)
    {
        if (await _fs.StatAsync(uri, ct).ConfigureAwait(false) is not null) return;
        await _fs.WriteFileAsync(uri, OrgText.Encode(initialText, bom: false), createParents: true, ct).ConfigureAwait(false);
        ScheduleRefresh(50);
    }

    // a watch per agenda entry (a folder's own files; a file through its folder), set again when the entries change
    private void UpdateWatches(IReadOnlyList<string> entries)
    {
        if (_watcher is null) return;
        var key = string.Join('\n', entries);
        lock (_lock)
        {
            if (key == _watchedKey || _disposed) return;
            _watchedKey = key;
            foreach (var w in _watches) w.Dispose();
            _watches.Clear();
            foreach (var folder in entries.Select(e => e.EndsWith(".org", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(e) : e).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                try
                {
                    _watches.Add(_watcher.Watch(new Uri(folder), recursive: false, null, changes =>
                    {
                        if (changes.Any(c => c.Uri.IsFile && IsOrgFileName(Path.GetFileName(c.Uri.LocalPath)))) ScheduleRefresh();
                    }));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { } // a folder that does not exist yet
            }
        }
    }

    /// <summary>Forgets the watches so the next refresh sets them again (the agenda's entries changed, or a missing folder was created).</summary>
    public void ResetWatches()
    {
        lock (_lock) _watchedKey = "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Dispose();
        lock (_lock)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            foreach (var w in _watches) w.Dispose();
            _watches.Clear();
            _cache.Clear();
        }
        _changed.Clear();
    }
}
