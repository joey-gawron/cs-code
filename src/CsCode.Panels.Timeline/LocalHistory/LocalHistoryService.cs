using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using CsCode.Core.Diagnostics;
using CsCode.Core.History;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using Microsoft.Extensions.Logging;

namespace CsCode.Panels.Timeline.LocalHistory;

/// <summary>
/// Local history (VS Code's working-copy history), stored in VS Code's layout under <c>{UserDataDir}/History</c> (<see cref="HistoryFormat"/>).
/// <para>Every save of a file-backed document (<see cref="IDocumentManager.Saved"/>) stores the file as saved; a rename or move inside CsCode
/// (<see cref="IFileOperationParticipant.DidRenameFilesAsync"/>) takes the entries along. Settings: <see cref="LocalHistorySettings"/>.</para>
/// <para>Threading: every operation is a work item on one queue, run in order by one consumer (so a file's entries are never changed by two
/// operations at once); all file I/O goes through <see cref="IFileSystem"/>, whose local implementation runs on the I/O lane. The save hook only
/// enqueues (bounded; a full queue drops the entry and logs it): a save never waits for history. Reads of the saved file happen after the save,
/// from disk, so the entry is exactly what was written (encoding and line endings included).</para>
/// <para>Several CsCode processes (one per session) share the folder: every operation reads the file's <c>entries.json</c> again before it
/// changes it, so an entry another process added is kept; only the folder a file maps to and the newest copy's hash are cached.</para>
/// </summary>
public sealed class LocalHistoryService : ILocalHistoryService, IFileOperationParticipant, IMemoryTrimmable, IDisposable
{
    public const string HistoryFolderName = "History";
    internal const int QueueCapacity = 4096;
    private const int MaxCachedModels = 256;
    private static readonly TimeSpan PendingSourceLifetime = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> TrackedSchemes = new(StringComparer.OrdinalIgnoreCase) { "file", "ssh", "docker", "wsl" };

    private readonly IFileSystem _fs;
    private readonly IConfigurationService? _config;
    private readonly IWorkspaceService? _workspace;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly string _home;
    private readonly Channel<Func<Task>> _queue = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly Emitter<Uri?> _changed = new();
    private readonly DisposableStore _store = new();
    private readonly ConcurrentDictionary<string, (string Source, DateTimeOffset Expires)> _pendingSources = new(StringComparer.Ordinal);

    // Touched only by the queue's consumer.
    private readonly Dictionary<string, Model> _models = new(StringComparer.Ordinal);
    private readonly LinkedList<Model> _recent = new();

    private long _dropped;
    private volatile bool _disposed;

    public LocalHistoryService(IFileSystem fileSystem, string historyHome, IConfigurationService? config = null, IDocumentManager? documents = null,
        IWorkspaceService? workspace = null, TimeProvider? time = null, ILogger? log = null, IMemoryMonitor? memory = null)
    {
        _fs = fileSystem;
        _home = Path.GetFullPath(historyHome);
        _config = config;
        _workspace = workspace;
        _time = time ?? TimeProvider.System;
        _log = log;
        if (config is not null) _store.Add(config.RegisterProperties(LocalHistorySettings.Properties()));
        if (documents is not null) _store.Add(documents.Saved.Subscribe(OnSaved));
        if (memory is not null) _store.Add(memory.Register(this));
        _ = PumpAsync();
    }

    // ------------------------------------------------------------------------------------------------ memory

    string IMemoryTrimmable.Name => "Local history";

    /// <summary>The cached files (≤ 256): their folder, their last entry list and the newest copy's hash; all read again from disk when needed.</summary>
    long IMemoryTrimmable.EstimatedBytes => Volatile.Read(ref _cachedModels) * 2048L;

    long IMemoryTrimmable.Trim(MemoryTrimLevel level)
    {
        if (level == MemoryTrimLevel.Idle || _disposed) return 0;
        var estimate = ((IMemoryTrimmable)this).EstimatedBytes;
        // the caches belong to the queue's consumer: dropped there, between two operations
        return _queue.Writer.TryWrite(() =>
        {
            _models.Clear();
            _recent.Clear();
            Volatile.Write(ref _cachedModels, 0);
            return Task.CompletedTask;
        }) ? estimate : 0;
    }

    private int _cachedModels;

    /// <summary>The folder that holds every file's history (<c>{UserDataDir}/History</c>).</summary>
    public string Home => _home;

    /// <summary>Entries not stored because the queue was full (a burst of more than <see cref="QueueCapacity"/> pending operations).</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public IEventSource<Uri?> Changed => _changed;

    public bool IsEnabled => _config?.Get(LocalHistorySettings.Enabled, true) ?? true;

    private long MaxFileBytes => Math.Max(1, (long)(_config?.Get(LocalHistorySettings.MaxFileSize, LocalHistorySettings.DefaultMaxFileSizeKb) ?? LocalHistorySettings.DefaultMaxFileSizeKb)) * 1024;
    private int MaxEntries => Math.Max(0, _config?.Get(LocalHistorySettings.MaxFileEntries, LocalHistorySettings.DefaultMaxFileEntries) ?? LocalHistorySettings.DefaultMaxFileEntries);
    private long MergeWindowMs => (long)(Math.Max(0, _config?.Get(LocalHistorySettings.MergeWindow, (double)LocalHistorySettings.DefaultMergeWindowSeconds) ?? LocalHistorySettings.DefaultMergeWindowSeconds) * 1000);

    public bool CanTrack(Uri resource) => resource.IsAbsoluteUri && TrackedSchemes.Contains(resource.Scheme) && !IsUnderHome(resource);

    // ------------------------------------------------------------------------------------------------ the save hook (never waits)

    private void OnSaved(DocumentEvent e)
    {
        var uri = e.Document.Uri;
        if (_disposed || !CanTrack(uri) || !IsEnabled) return;
        var source = TakePendingSource(uri) ?? LocalHistorySources.FileSaved;
        var when = _time.GetUtcNow();
        if (_queue.Writer.TryWrite(() => AddCoreAsync(uri, source, when, CancellationToken.None))) return;
        Interlocked.Increment(ref _dropped);
        _log?.LogWarning("Local history: too many pending operations; the save of {Resource} was not recorded", HistoryFormat.Describe(uri));
    }

    public void SetNextSaveSource(Uri resource, string source) =>
        _pendingSources[HistoryFormat.Key(resource)] = (source, _time.GetUtcNow() + PendingSourceLifetime);

    private string? TakePendingSource(Uri resource) =>
        _pendingSources.TryRemove(HistoryFormat.Key(resource), out var pending) && pending.Expires >= _time.GetUtcNow() ? pending.Source : null;

    // ------------------------------------------------------------------------------------------------ public API (queued)

    public ValueTask<LocalHistoryEntry?> AddEntryAsync(Uri resource, string? source = null, CancellationToken ct = default)
    {
        var when = _time.GetUtcNow();
        return RunAsync(token => AddCoreAsync(resource, string.IsNullOrEmpty(source) ? LocalHistorySources.FileSaved : source, when, token), ct);
    }

    public ValueTask<IReadOnlyList<LocalHistoryEntry>> GetEntriesAsync(Uri resource, CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<LocalHistoryEntry>>(async token =>
        {
            if (!CanTrack(resource)) return [];
            var model = await LoadAsync(resource, token).ConfigureAwait(false);
            return model.Entries.Select(e => ToEntry(model, e)).ToArray();
        }, ct);

    public ValueTask<IReadOnlyList<Uri>> GetResourcesAsync(CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<Uri>>(async token =>
        {
            var all = await ScanAsync(token).ConfigureAwait(false);
            return all.OrderByDescending(p => p.Newest).Select(p => new Uri(p.Resource)).ToArray();
        }, ct);

    public async ValueTask<byte[]?> ReadAsync(LocalHistoryEntry entry, CancellationToken ct = default)
    {
        // Entry files are replaced atomically (temp file + rename): a read needs no turn on the queue.
        try { return await _fs.ReadFileAsync(entry.Location, ct).ConfigureAwait(false); }
        catch (Exception ex) when (IsIoFailure(ex)) { return null; }
    }

    public ValueTask<LocalHistoryEntry?> RenameEntryAsync(LocalHistoryEntry entry, string source, CancellationToken ct = default) =>
        RunAsync<LocalHistoryEntry?>(async token =>
        {
            var model = await LoadAsync(entry.Resource, token).ConfigureAwait(false);
            var i = model.Entries.FindIndex(e => e.Id == entry.Id);
            if (i < 0) return null;
            var renamed = model.Entries[i] with { Source = string.IsNullOrWhiteSpace(source) ? null : source };
            model.Entries[i] = renamed;
            await SaveModelAsync(model, token).ConfigureAwait(false);
            Notify(model.Resource);
            return ToEntry(model, renamed);
        }, ct);

    public ValueTask<bool> RemoveEntryAsync(LocalHistoryEntry entry, CancellationToken ct = default) =>
        RunAsync(async token =>
        {
            var model = await LoadAsync(entry.Resource, token).ConfigureAwait(false);
            var i = model.Entries.FindIndex(e => e.Id == entry.Id);
            if (i < 0) return false;
            model.Entries.RemoveAt(i);
            await DeleteQuietlyAsync(EntryUri(model, entry.Id), recursive: false, token).ConfigureAwait(false);
            await SaveModelAsync(model, token).ConfigureAwait(false);
            Notify(model.Resource);
            return true;
        }, ct);

    public ValueTask RemoveAllAsync(Uri? resource, CancellationToken ct = default) =>
        new(RunAsync(async token =>
        {
            if (resource is null)
            {
                await DeleteQuietlyAsync(new Uri(_home), recursive: true, token).ConfigureAwait(false);
                _models.Clear();
                _recent.Clear();
                CountCaches();
                Notify(null);
                return true;
            }
            var model = await LoadAsync(resource, token).ConfigureAwait(false);
            model.Entries.Clear();
            await SaveModelAsync(model, token).ConfigureAwait(false);
            Notify(model.Resource);
            return true;
        }, ct).AsTask());

    public ValueTask MoveEntriesAsync(Uri source, Uri target, CancellationToken ct = default) =>
        new(RunAsync(async token => { await MoveCoreAsync(source, target, token).ConfigureAwait(false); return true; }, ct).AsTask());

    /// <summary>Completes once every operation queued before it has run (tests; a shutdown that wants pending entries written).</summary>
    public Task WhenIdleAsync(CancellationToken ct = default) => RunAsync(_ => Task.FromResult(true), ct).AsTask();

    // ------------------------------------------------------------------------------------------------ file operations (renames follow)

    public ValueTask<WorkspaceEdit?> WillRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct) => ValueTask.FromResult<WorkspaceEdit?>(null);

    public ValueTask DidRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct)
    {
        foreach (var (from, to) in files)
        {
            if (_disposed || !CanTrack(from) || !CanTrack(to)) continue;
            if (!_queue.Writer.TryWrite(() => MoveCoreAsync(from, to, CancellationToken.None)))
            {
                Interlocked.Increment(ref _dropped);
                _log?.LogWarning("Local history: too many pending operations; the history of {Resource} did not follow its rename", HistoryFormat.Describe(from));
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DidCreateFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct) => ValueTask.CompletedTask;

    // A deleted file keeps its history: Find Entry to Restore brings it back.
    public ValueTask DidDeleteFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct) => ValueTask.CompletedTask;

    // ------------------------------------------------------------------------------------------------ the queue

    private async ValueTask<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _queue.Writer.WriteAsync(async () =>
            {
                if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
                try { tcs.TrySetResult(await work(ct).ConfigureAwait(false)); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(LocalHistoryService));
        }
        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
            while (reader.TryRead(out var work))
            {
                if (_disposed) return;
                try { await work().ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _log?.LogWarning(ex, "Local history: an operation failed"); }
            }
    }

    // ------------------------------------------------------------------------------------------------ adding

    private async Task<LocalHistoryEntry?> AddCoreAsync(Uri resource, string source, DateTimeOffset when, CancellationToken ct)
    {
        if (!IsEnabled || !CanTrack(resource) || IsExcluded(resource)) return null;
        var max = MaxFileBytes;
        byte[] bytes;
        try
        {
            var stat = await _fs.StatAsync(resource, ct).ConfigureAwait(false);
            if (stat is null || (stat.Type & FileType.File) == 0) return null;
            if (stat.Size > max) { _log?.LogDebug("Local history: {Resource} is larger than {Max} bytes; not stored", HistoryFormat.Describe(resource), max); return null; }
            bytes = await _fs.ReadFileAsync(resource, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            _log?.LogInformation("Local history: {Resource} could not be read ({Message}); not stored", HistoryFormat.Describe(resource), ex.Message);
            return null;
        }
        if (bytes.Length > max) return null;
        return await StoreAsync(resource, bytes, source, when.ToUnixTimeMilliseconds(), ct).ConfigureAwait(false);
    }

    private async Task<LocalHistoryEntry?> StoreAsync(Uri resource, byte[] bytes, string source, long timestamp, CancellationToken ct)
    {
        var model = await LoadAsync(resource, ct).ConfigureAwait(false);
        var hash = HistoryFormat.Sha256(bytes);
        if (model.Entries.Count > 0 && model.Entries[^1] is var last && last.SourceOrDefault == source)
        {
            var newest = model.KnownNewestHash ?? model.RememberNewest(await HashOfAsync(model, last, ct).ConfigureAwait(false));
            // A save of the same content as the newest save adds nothing (Save All of unchanged files, the old file of a Save As). Other sources
            // record an event (a move, a discard, a name given), so they are added even when the content did not change.
            if (source == LocalHistorySources.FileSaved && newest is not null && newest.AsSpan().SequenceEqual(hash)) return null;
            // mergeWindow: a save soon after the previous one of the same source replaces it (auto save would otherwise flood the history).
            if (timestamp >= last.Timestamp && timestamp - last.Timestamp <= MergeWindowMs)
            {
                await WriteEntryFileAsync(model, last.Id, bytes, ct).ConfigureAwait(false);
                var replaced = last with { Timestamp = timestamp };
                model.Entries[^1] = replaced;
                model.RememberNewest(hash);
                await SaveModelAsync(model, ct).ConfigureAwait(false);
                Notify(model.Resource);
                return ToEntry(model, replaced);
            }
        }

        var id = HistoryFormat.NewId(resource, model.Entries.Select(e => e.Id));
        await WriteEntryFileAsync(model, id, bytes, ct).ConfigureAwait(false);
        var entry = new StoredEntry(id, timestamp, source == LocalHistorySources.FileSaved ? null : source, null);
        var at = model.Entries.FindLastIndex(e => e.Timestamp <= timestamp) + 1; // a clock that stepped back still keeps the list in order
        model.Entries.Insert(at, entry);
        if (at == model.Entries.Count - 1) model.RememberNewest(hash);
        var kept = await PruneAsync(model, ct).ConfigureAwait(false);
        await SaveModelAsync(model, ct).ConfigureAwait(false);
        Notify(model.Resource);
        return kept.Contains(entry) ? ToEntry(model, entry) : null;
    }

    /// <summary>Oldest first, down to <c>maxFileEntries</c>; the pruned copies are deleted. Returns the entries kept.</summary>
    private async Task<IReadOnlyList<StoredEntry>> PruneAsync(Model model, CancellationToken ct)
    {
        var max = MaxEntries;
        while (model.Entries.Count > max)
        {
            var oldest = model.Entries[0];
            model.Entries.RemoveAt(0);
            await DeleteQuietlyAsync(EntryUri(model, oldest.Id), recursive: false, ct).ConfigureAwait(false);
        }
        return model.Entries;
    }

    private async Task<byte[]?> HashOfAsync(Model model, StoredEntry entry, CancellationToken ct)
    {
        try { return HistoryFormat.Sha256(await _fs.ReadFileAsync(EntryUri(model, entry.Id), ct).ConfigureAwait(false)); }
        catch (Exception ex) when (IsIoFailure(ex)) { return null; }
    }

    // ------------------------------------------------------------------------------------------------ moving (renames)

    private async Task MoveCoreAsync(Uri source, Uri target, CancellationToken ct)
    {
        var from = HistoryFormat.Key(source).TrimEnd('/');
        var to = HistoryFormat.Key(target).TrimEnd('/');
        if (from == to) return;
        var keys = new List<string>();
        if ((await LoadAsync(new Uri(from), ct).ConfigureAwait(false)).Entries.Count > 0) keys.Add(from);
        // A folder: every file under it with history follows (found by reading every entries.json: a folder rename is rare).
        FileStat? stat = null;
        try { stat = await _fs.StatAsync(target, ct).ConfigureAwait(false); }
        catch (Exception ex) when (IsIoFailure(ex)) { }
        if (stat is not null && (stat.Type & FileType.Directory) != 0)
        {
            var all = await ScanAsync(ct).ConfigureAwait(false);
            keys.AddRange(all.Select(p => p.Resource).Where(k => k.StartsWith(from + "/", StringComparison.Ordinal)));
        }
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            var newKey = to + key[from.Length..];
            await MoveOneAsync(new Uri(key), new Uri(newKey), ct).ConfigureAwait(false);
        }
    }

    private async Task MoveOneAsync(Uri source, Uri target, CancellationToken ct)
    {
        var src = await LoadAsync(source, ct).ConfigureAwait(false);
        if (src.Entries.Count == 0) return;
        var dst = await LoadAsync(target, ct).ConfigureAwait(false);
        await _fs.CreateDirectoryAsync(FolderUri(dst), ct).ConfigureAwait(false);
        foreach (var e in src.Entries)
        {
            var id = dst.Entries.Any(x => string.Equals(x.Id, e.Id, StringComparison.OrdinalIgnoreCase)) ? HistoryFormat.NewId(target, dst.Entries.Select(x => x.Id)) : e.Id;
            try { await _fs.RenameAsync(EntryUri(src, e.Id), EntryUri(dst, id), overwrite: false, ct).ConfigureAwait(false); }
            catch (Exception ex) when (IsIoFailure(ex)) { continue; } // its copy is gone: the entry goes with it
            dst.Entries.Add(e with { Id = id });
        }
        dst.Entries.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        await PruneAsync(dst, ct).ConfigureAwait(false);
        await SaveModelAsync(dst, ct).ConfigureAwait(false);
        src.Entries.Clear();
        await SaveModelAsync(src, ct).ConfigureAwait(false);
        Notify(src.Resource);
        Notify(dst.Resource);
        // As VS Code: the moved file gets an entry saying so (its content now, which is also what a later Compare shows).
        var sameFolder = string.Equals(ParentOf(HistoryFormat.Key(source)), ParentOf(HistoryFormat.Key(target)), StringComparison.Ordinal);
        await AddCoreAsync(target, sameFolder ? LocalHistorySources.FileRenamed : LocalHistorySources.FileMoved, _time.GetUtcNow(), ct).ConfigureAwait(false);
    }

    private static string ParentOf(string key) => key.TrimEnd('/') is var k && k.LastIndexOf('/') is var i and > 0 ? k[..i] : k;

    // ------------------------------------------------------------------------------------------------ models (one per file, cached)

    private sealed class Model(Uri resource, string key, string folder)
    {
        private byte[]? _newestHash;
        private (string Id, long Timestamp) _newestHashOf;

        public Uri Resource { get; } = resource;
        public string Key { get; } = key;
        public string Folder { get; } = folder;
        /// <summary>The entries as last read or written, oldest first.</summary>
        public List<StoredEntry> Entries { get; } = [];
        public LinkedListNode<Model>? Node { get; set; }

        /// <summary>SHA-256 of the newest entry's copy, while that entry is the one it was computed for (another process may have merged into it).</summary>
        public byte[]? KnownNewestHash => Entries.Count > 0 && _newestHashOf == (Entries[^1].Id, Entries[^1].Timestamp) ? _newestHash : null;

        public byte[]? RememberNewest(byte[]? hash)
        {
            _newestHash = hash;
            _newestHashOf = Entries.Count > 0 ? (Entries[^1].Id, Entries[^1].Timestamp) : default;
            return hash;
        }
    }

    /// <summary>The model of <paramref name="resource"/>, its entries read from disk now.</summary>
    private async Task<Model> LoadAsync(Uri resource, CancellationToken ct)
    {
        var key = HistoryFormat.Key(resource);
        if (_models.TryGetValue(key, out var cached))
        {
            Touch(cached);
            if (await ReloadAsync(cached, ct).ConfigureAwait(false)) return cached;
            _recent.Remove(cached.Node!); // its folder was deleted and given to another file meanwhile: look again
            cached.Node = null;
            _models.Remove(key);
        }
        var baseName = HistoryFormat.Hash(key);
        Model? model = null;
        // VS Code's hash is 32 bits: two files can share a folder name. The folder whose entries.json names another resource is someone else's;
        // the next candidate (<hash>_1, <hash>_2 ...) is tried.
        for (var probe = 0; model is null; probe++)
        {
            var folder = probe == 0 ? baseName : $"{baseName}_{probe}";
            var folderPath = Path.Combine(_home, folder);
            FileStat? stat;
            try { stat = await _fs.StatAsync(new Uri(folderPath), ct).ConfigureAwait(false); }
            catch (Exception ex) when (IsIoFailure(ex)) { stat = null; }
            if (stat is null)
            {
                model = new Model(new Uri(key), key, folder);
                break;
            }
            var stored = await ReadEntriesFileAsync(folderPath, ct).ConfigureAwait(false);
            if (stored is not null && !string.Equals(stored.Resource, key, StringComparison.Ordinal)) continue;
            model = new Model(new Uri(key), key, folder);
            model.Entries.AddRange(stored?.Entries ?? await RebuildAsync(folderPath, ct).ConfigureAwait(false));
        }
        _models[key] = model;
        Touch(model);
        while (_models.Count > MaxCachedModels && _recent.Last is { } oldest)
        {
            _recent.RemoveLast();
            oldest.Value.Node = null;
            _models.Remove(oldest.Value.Key);
        }
        CountCaches();
        return model;
    }

    /// <summary>Reads the model's entries again; false when its folder now belongs to another file.</summary>
    private async Task<bool> ReloadAsync(Model model, CancellationToken ct)
    {
        var folderPath = Path.Combine(_home, model.Folder);
        var stored = await ReadEntriesFileAsync(folderPath, ct).ConfigureAwait(false);
        if (stored is not null && !string.Equals(stored.Resource, model.Key, StringComparison.Ordinal)) return false;
        IReadOnlyList<StoredEntry> entries = stored?.Entries ?? [];
        if (stored is null)
        {
            FileStat? stat;
            try { stat = await _fs.StatAsync(new Uri(folderPath), ct).ConfigureAwait(false); }
            catch (Exception ex) when (IsIoFailure(ex)) { stat = null; }
            if (stat is not null) entries = await RebuildAsync(folderPath, ct).ConfigureAwait(false); // damaged (gone: no entries, deleted)
        }
        model.Entries.Clear();
        model.Entries.AddRange(entries);
        return true;
    }

    private void Touch(Model model)
    {
        if (model.Node is { } node) _recent.Remove(node);
        model.Node = _recent.AddFirst(model);
    }

    private async Task<StoredHistory?> ReadEntriesFileAsync(string folderPath, CancellationToken ct)
    {
        try { return HistoryFormat.Parse(await _fs.ReadFileAsync(new Uri(Path.Combine(folderPath, HistoryFormat.EntriesFile)), ct).ConfigureAwait(false)); }
        catch (Exception ex) when (IsIoFailure(ex)) { return null; }
    }

    /// <summary>A folder whose entries.json is missing or damaged: its copies are the entries (time = the copy's modification time).</summary>
    private async Task<IReadOnlyList<StoredEntry>> RebuildAsync(string folderPath, CancellationToken ct)
    {
        IReadOnlyList<DirectoryEntry> files;
        try { files = await _fs.ReadDirectoryAsync(new Uri(folderPath), ct).ConfigureAwait(false); }
        catch (Exception ex) when (IsIoFailure(ex)) { return []; }
        var entries = new List<StoredEntry>();
        foreach (var f in files)
        {
            if ((f.Type & FileType.File) == 0 || !HistoryFormat.IsSafeId(f.Name) || f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            FileStat? stat;
            try { stat = await _fs.StatAsync(new Uri(Path.Combine(folderPath, f.Name)), ct).ConfigureAwait(false); }
            catch (Exception ex) when (IsIoFailure(ex)) { continue; }
            if (stat is null) continue;
            entries.Add(new StoredEntry(f.Name, stat.ModifiedUtc.ToUnixTimeMilliseconds(), null, null));
        }
        if (entries.Count > 0) _log?.LogInformation("Local history: {Folder} had no readable {File}; rebuilt {Count} entries from its files", folderPath, HistoryFormat.EntriesFile, entries.Count);
        entries.Sort(static (a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return entries;
    }

    private async Task SaveModelAsync(Model model, CancellationToken ct)
    {
        if (model.Entries.Count == 0)
        {
            await DeleteQuietlyAsync(FolderUri(model), recursive: true, ct).ConfigureAwait(false);
            return;
        }
        var bytes = HistoryFormat.Serialize(new StoredHistory(model.Key, model.Entries));
        await _fs.WriteFileAsync(new Uri(Path.Combine(_home, model.Folder, HistoryFormat.EntriesFile)), bytes, createParents: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every file with history and the time of its newest entry, from every entries.json (Find Entry to Restore, a folder renamed: rare, and
    /// never cached, so files another process gave history to are found too).
    /// </summary>
    private async Task<IReadOnlyList<(string Resource, long Newest)>> ScanAsync(CancellationToken ct)
    {
        var found = new Dictionary<string, long>(StringComparer.Ordinal);
        IReadOnlyList<DirectoryEntry> folders;
        try { folders = await _fs.ReadDirectoryAsync(new Uri(_home), ct).ConfigureAwait(false); }
        catch (Exception ex) when (IsIoFailure(ex)) { folders = []; }
        foreach (var f in folders)
        {
            if ((f.Type & FileType.Directory) == 0) continue;
            if (await ReadEntriesFileAsync(Path.Combine(_home, f.Name), ct).ConfigureAwait(false) is not { Entries.Count: > 0 } stored) continue;
            found[stored.Resource] = stored.Entries[^1].Timestamp;
        }
        return found.Select(p => (p.Key, p.Value)).ToArray();
    }

    private void CountCaches() => Volatile.Write(ref _cachedModels, _models.Count);

    // ------------------------------------------------------------------------------------------------ helpers

    private Uri FolderUri(Model model) => new(Path.Combine(_home, model.Folder) + Path.DirectorySeparatorChar);

    private Uri EntryUri(Model model, string id) => new(Path.Combine(_home, model.Folder, id));

    private LocalHistoryEntry ToEntry(Model model, StoredEntry e) =>
        new(e.Id, model.Resource, DateTimeOffset.FromUnixTimeMilliseconds(e.Timestamp), e.SourceOrDefault, EntryUri(model, e.Id)) { SourceDescription = e.SourceDescription };

    private Task WriteEntryFileAsync(Model model, string id, byte[] bytes, CancellationToken ct) =>
        _fs.WriteFileAsync(EntryUri(model, id), bytes, createParents: true, ct).AsTask();

    private async Task DeleteQuietlyAsync(Uri uri, bool recursive, CancellationToken ct)
    {
        try { await _fs.DeleteAsync(uri, recursive, useTrash: false, ct).ConfigureAwait(false); }
        catch (Exception ex) when (IsIoFailure(ex)) { }
    }

    private void Notify(Uri? resource) => _changed.Fire(resource);

    private bool IsUnderHome(Uri resource) =>
        resource.IsFile && (resource.LocalPath.StartsWith(_home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(resource.LocalPath.TrimEnd(Path.DirectorySeparatorChar), _home, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>workbench.localHistory.exclude</c>: a pattern is matched against the path relative to the workspace folder that holds the file (else the
    /// full path), an absolute pattern against the full path (VS Code's rule).
    /// </summary>
    internal bool IsExcluded(Uri resource)
    {
        if (_config?.GetRaw(LocalHistorySettings.Exclude) is not { ValueKind: JsonValueKind.Object } globs) return false;
        var full = resource.IsFile ? resource.LocalPath.Replace('\\', '/') : Uri.UnescapeDataString(resource.AbsolutePath);
        string? relative = null;
        foreach (var folder in _workspace?.Folders ?? [])
        {
            if (!string.Equals(folder.Scheme, resource.Scheme, StringComparison.OrdinalIgnoreCase) || !string.Equals(folder.Authority, resource.Authority, StringComparison.OrdinalIgnoreCase)) continue;
            var root = (folder.IsFile ? folder.LocalPath.Replace('\\', '/') : Uri.UnescapeDataString(folder.AbsolutePath)).TrimEnd('/') + "/";
            if (full.StartsWith(root, StringComparison.Ordinal)) { relative = full[root.Length..]; break; }
        }
        foreach (var p in globs.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.True || string.IsNullOrWhiteSpace(p.Name)) continue;
            var pattern = p.Name.Replace('\\', '/');
            var absolute = pattern.StartsWith('/') || (pattern.Length > 2 && pattern[1] == ':' && pattern[2] == '/');
            if (Glob.IsMatch(pattern, absolute ? full : relative ?? full)) return true;
        }
        return false;
    }

    private static bool IsIoFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException
        || ex is OperationCanceledException { CancellationToken.IsCancellationRequested: false };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Dispose();
        _queue.Writer.TryComplete();
        _changed.Clear();
    }
}
