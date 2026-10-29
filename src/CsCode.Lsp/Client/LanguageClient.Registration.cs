using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Features;
using CsCode.Lsp.Runtime;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Client;

// client/registerCapability + unregisterCapability, workspace/didChangeWatchedFiles, file operations.
internal sealed partial class LanguageClient
{
    private readonly FeatureTable _features;
    private readonly Dictionary<string, IDisposable> _dynamicResources = [];
    private P.FileOperationOptions? _fileOps;

    internal SemanticTokenCache TokenCache { get; } = new();

    private static DocumentSelector? ToSelector(P.DocumentFilter[]? filters) =>
        filters is null ? null : new DocumentSelector(filters.Select(f => new DocumentFilter(f.Language, f.Scheme, f.Pattern)).ToArray());

    /// <summary>A registration's documentSelector; null (absent or JSON null) means "the client's own selector" i.e. the definition's.</summary>
    private static DocumentSelector? SelectorOf(JsonElement? options)
    {
        if (options is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty("documentSelector", out var sel) || sel.ValueKind != JsonValueKind.Array) return null;
        return ToSelector(P.LspJson.Deserialize<P.DocumentFilter[]>(sel));
    }

    internal void RegisterDynamic(P.Registration r)
    {
        var selector = SelectorOf(r.RegisterOptions);
        switch (r.Method)
        {
            case P.LspMethods.WorkspaceDidChangeConfiguration:
                return; // we always push configuration changes
            case P.LspMethods.WorkspaceDidChangeWatchedFiles:
                if (r.RegisterOptions is { } wo && P.LspJson.Deserialize<P.DidChangeWatchedFilesRegistrationOptions>(wo) is { } watched) RegisterWatchers(r.Id, watched);
                return;
            case P.LspMethods.WorkspaceExecuteCommand:
                if (r.RegisterOptions is { } eo && P.LspJson.Deserialize<P.ExecuteCommandRegistrationOptions>(eo) is { Commands: { } cmds }) RegisterServerCommands(cmds, r.Id);
                return;
            case P.LspMethods.TextDocumentDidChange:
                if (r.RegisterOptions is { } co && P.LspJson.Deserialize<P.TextDocumentChangeRegistrationOptions>(co) is { } change)
                {
                    lock (_dynamicChange) _dynamicChange.Add((r.Id, selector, change.SyncKind));
                    foreach (var sd in _docs.Values) _ctx.Queue.Post(LanePriority.Interactive, () => FlushOnLane(sd), exempt: true); // resync stale documents now
                }
                return;
            case P.LspMethods.TextDocumentDidSave:
                lock (_dynamicSave) _dynamicSave.Add((r.Id, selector, r.RegisterOptions is { } so && P.LspJson.Deserialize<P.TextDocumentSaveRegistrationOptions>(so)?.IncludeText == true));
                return;
            case P.LspMethods.TextDocumentDidOpen or P.LspMethods.TextDocumentDidClose:
                _openClose = true;
                foreach (var sd in _docs.Values) { lock (sd.Gate) { if (!sd.OpenOnServer && sd.Ignored && sd.Document.Length <= _ctx.Settings().MaxDocumentBytes) sd.Ignored = false; } PostOpen(sd); }
                return;
            case P.LspMethods.TextDocumentWillSave: _willSave = true; return;
            case P.LspMethods.TextDocumentWillSaveWaitUntil: _willSaveWaitUntil = true; return;
            case P.LspMethods.TextDocumentDiagnostic:
            {
                var d = r.RegisterOptions is { } dop ? P.LspJson.Deserialize<P.DiagnosticRegistrationOptions>(dop) : null;
                EnablePullDiagnostics(d?.Identifier, selector, d?.WorkspaceDiagnostics == true);
                SchedulePullAll();
                return;
            }
            case P.LspMethods.WorkspaceWillRenameFiles or P.LspMethods.WorkspaceDidRenameFiles or P.LspMethods.WorkspaceDidCreateFiles or P.LspMethods.WorkspaceDidDeleteFiles:
                if (r.RegisterOptions is { } fo && P.LspJson.Deserialize<P.FileOperationRegistrationOptions>(fo) is { } fopts)
                    _fileOps = MergeFileOps(_fileOps, r.Method, fopts);
                return;
            case "textDocument/semanticTokens" or P.LspMethods.TextDocumentSemanticTokensFull or P.LspMethods.TextDocumentSemanticTokensFullDelta or P.LspMethods.TextDocumentSemanticTokensRange:
            {
                var o = r.RegisterOptions;
                bool Has(string name) => o is { ValueKind: JsonValueKind.Object } x && x.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.False or JsonValueKind.Null);
                if (r.Method == P.LspMethods.TextDocumentSemanticTokensRange || (r.Method == "textDocument/semanticTokens" && Has("range")))
                    _features.Add(ProviderKind.SemanticTokensRange, new FeatureEntry(r.Id, P.LspMethods.TextDocumentSemanticTokensRange, selector, o));
                if (r.Method != P.LspMethods.TextDocumentSemanticTokensRange && (r.Method != "textDocument/semanticTokens" || Has("full")))
                    _features.Add(ProviderKind.SemanticTokens, new FeatureEntry(r.Id, "textDocument/semanticTokens", selector, o));
                return;
            }
        }
        if (FeatureTable.KindOf(r.Method) is { } kind)
            _features.Add(kind, new FeatureEntry(r.Id, r.Method, selector, r.RegisterOptions));
        else
            _log.Add($"[client] ignoring registration for unsupported method {r.Method}");
    }

    internal void UnregisterDynamic(string id, string method)
    {
        _features.Remove(id, method);
        lock (_dynamicChange) _dynamicChange.RemoveAll(e => e.Id == id);
        lock (_dynamicSave) _dynamicSave.RemoveAll(e => e.Id == id);
        IDisposable? res;
        lock (_dynamicResources) _dynamicResources.Remove(id, out res);
        res?.Dispose();
        if (method == P.LspMethods.TextDocumentWillSave) _willSave = false;
        if (method == P.LspMethods.TextDocumentWillSaveWaitUntil) _willSaveWaitUntil = false;
    }

    private static P.FileOperationOptions MergeFileOps(P.FileOperationOptions? current, string method, P.FileOperationRegistrationOptions o)
    {
        var c = current ?? new P.FileOperationOptions();
        return method switch
        {
            P.LspMethods.WorkspaceWillRenameFiles => c with { WillRename = o },
            P.LspMethods.WorkspaceDidRenameFiles => c with { DidRename = o },
            P.LspMethods.WorkspaceDidCreateFiles => c with { DidCreate = o },
            _ => c with { DidDelete = o },
        };
    }

    internal void SetFileOperationOptions(P.FileOperationOptions o) => _fileOps = o;

    // ---- file operations (willRename / didRename / didCreate / didDelete) -----------------------------------------

    private static bool FileOpMatches(P.FileOperationRegistrationOptions? o, Uri uri, bool isDirectory)
    {
        if (o is null) return false;
        var path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
        foreach (var f in o.Filters ?? [])
        {
            if (f.Scheme is { } s && !string.Equals(s, uri.Scheme, StringComparison.OrdinalIgnoreCase)) continue;
            var kind = f.Pattern.Matches?.Value;
            if (kind == "file" && isDirectory) continue;
            if (kind == "folder" && !isDirectory) continue;
            if (Glob.IsMatch(f.Pattern.Glob, path)) return true;
        }
        return false;
    }

    public async ValueTask<WorkspaceEdit?> WillRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct)
    {
        var matching = files.Where(f => FileOpMatches(_fileOps?.WillRename, f.OldUri, IsDirectory(f.OldUri))).ToArray();
        if (matching.Length == 0 || !IsOperational) return null;
        return await _ctx.Queue.RunAsync<WorkspaceEdit>(LanePriority.Interactive, default, async ctx =>
        {
            var p = new P.RenameFilesParams { Files = matching.Select(f => new P.FileRename { OldUri = LspMapper.ToUriString(f.OldUri), NewUri = LspMapper.ToUriString(f.NewUri) }).ToArray() };
            var edit = await RequestAsync(ctx, P.LspRequests.WorkspaceWillRenameFiles, p, ctx.Token, null, 0, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return edit is null ? null : Mapper.ToCore(edit);
        }, ct).ConfigureAwait(false);
    }

    public ValueTask DidRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct)
    {
        var matching = files.Where(f => FileOpMatches(_fileOps?.DidRename, f.NewUri, IsDirectory(f.NewUri))).ToArray();
        if (matching.Length > 0 && IsOperational)
            _ctx.Queue.Post(LanePriority.Interactive, () => NotifyRaw(P.LspMethods.WorkspaceDidRenameFiles,
                new P.RenameFilesParams { Files = matching.Select(f => new P.FileRename { OldUri = LspMapper.ToUriString(f.OldUri), NewUri = LspMapper.ToUriString(f.NewUri) }).ToArray() }));
        return ValueTask.CompletedTask;
    }

    public ValueTask DidCreateFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct)
    {
        var matching = files.Where(f => FileOpMatches(_fileOps?.DidCreate, f, IsDirectory(f))).ToArray();
        if (matching.Length > 0 && IsOperational)
            _ctx.Queue.Post(LanePriority.Interactive, () => NotifyRaw(P.LspMethods.WorkspaceDidCreateFiles,
                new P.CreateFilesParams { Files = matching.Select(f => new P.FileCreate { Uri = LspMapper.ToUriString(f) }).ToArray() }));
        return ValueTask.CompletedTask;
    }

    public ValueTask DidDeleteFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct)
    {
        var matching = files.Where(f => FileOpMatches(_fileOps?.DidDelete, f, false)).ToArray();
        if (matching.Length > 0 && IsOperational)
            _ctx.Queue.Post(LanePriority.Interactive, () => NotifyRaw(P.LspMethods.WorkspaceDidDeleteFiles,
                new P.DeleteFilesParams { Files = matching.Select(f => new P.FileDelete { Uri = LspMapper.ToUriString(f) }).ToArray() }));
        return ValueTask.CompletedTask;
    }

    private static bool IsDirectory(Uri uri) => uri.IsFile && Directory.Exists(uri.LocalPath);

    // ---- workspace/didChangeWatchedFiles ----------------------------------------------------------------------------

    private sealed record WatchMatcher(string Pattern, string? BasePath, int Kinds);

    private void RegisterWatchers(string id, P.DidChangeWatchedFilesRegistrationOptions options)
    {
        var watcher = _ctx.Services.Watcher;
        if (watcher is null) return;
        var matchers = new List<WatchMatcher>();
        foreach (var w in options.Watchers ?? [])
        {
            var kinds = w.Kind is { } k ? (int)k : 7;
            if (w.GlobPattern.IsT0) matchers.Add(new WatchMatcher(w.GlobPattern.AsT0, null, kinds));
            else
            {
                var rel = w.GlobPattern.AsT1;
                var baseUri = rel.BaseUri.Match(f => f.Uri, s => s);
                var basePath = LspMapper.ToUri(baseUri) is { } u ? (u.IsFile ? u.LocalPath : u.AbsolutePath) : null;
                matchers.Add(new WatchMatcher(rel.Pattern, basePath, kinds));
            }
        }
        if (matchers.Count == 0) return;

        var roots = CurrentFolders();
        if (roots.Count == 0 && RootUri is not null) roots = [RootUri];
        var exclude = _ctx.Services.Configuration?.Get<Dictionary<string, bool>>(SettingKeys.FilesWatcherExclude)?.Where(kv => kv.Value).Select(kv => kv.Key).ToArray();
        var store = new DisposableStore();
        foreach (var root in roots)
            store.Add(watcher.Watch(root, true, exclude, changes => OnWatchedChanges(matchers, changes)));
        lock (_dynamicResources)
        {
            if (_dynamicResources.Remove(id, out var old)) old.Dispose();
            _dynamicResources[id] = store;
        }
    }

    private void OnWatchedChanges(List<WatchMatcher> matchers, IReadOnlyList<FileChange> changes)
    {
        // Called on the file watcher's thread: filter cheaply, hand the rest to the lane.
        List<P.FileEvent>? events = null;
        foreach (var change in changes)
        {
            var path = change.Uri.IsFile ? change.Uri.LocalPath : change.Uri.AbsolutePath;
            var flag = change.Kind switch { FileChangeKind.Created => 1, FileChangeKind.Changed => 2, _ => 4 };
            foreach (var m in matchers)
            {
                if ((m.Kinds & flag) == 0) continue;
                string target = path;
                if (m.BasePath is not null)
                {
                    if (!path.StartsWith(m.BasePath, StringComparison.Ordinal)) continue;
                    target = path[m.BasePath.Length..].TrimStart('/', '\\');
                }
                if (!Glob.IsMatch(m.Pattern, target)) continue;
                (events ??= []).Add(new P.FileEvent { Uri = LspMapper.ToUriString(change.Uri), Type = (P.FileChangeType)(int)change.Kind });
                break;
            }
            if (events is { Count: >= 1000 }) break; // one message never carries an unbounded batch
        }
        if (events is null) return;
        _ctx.Queue.Post(LanePriority.Background, () => { if (IsOperational) NotifyRaw(P.LspMethods.WorkspaceDidChangeWatchedFiles, new P.DidChangeWatchedFilesParams { Changes = events.ToArray() }); });
    }
}

/// <summary>
/// Previous semantic-token results per document (the base for <c>full/delta</c> requests). Bounded by document count and bytes; registered as
/// an <see cref="CsCode.Core.Diagnostics.IMemoryTrimmable"/> so memory pressure empties it (the next request just asks for the full tokens).
/// </summary>
internal sealed class SemanticTokenCache : CsCode.Core.Diagnostics.IMemoryTrimmable
{
    private const int MaxDocuments = 32;
    private const long MaxBytes = 32L * 1024 * 1024;

    // Seq orders LRU eviction (the millisecond tick clock ties for entries stored back to back); Ticks only drive age-based trimming.
    private sealed record Entry(string ResultId, uint[] Data, long LastUsedTicks, long Seq);

    private readonly object _gate = new();
    private readonly Dictionary<Uri, Entry> _items = new();
    private long _bytes;
    private long _seq;

    public string Name => "lsp:semanticTokens";
    public long EstimatedBytes { get { lock (_gate) return _bytes; } }
    public int Count { get { lock (_gate) return _items.Count; } }

    public bool TryGet(Uri uri, out string resultId, out uint[] data)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(uri, out var e))
            {
                _items[uri] = e with { LastUsedTicks = Environment.TickCount64, Seq = ++_seq };
                resultId = e.ResultId;
                data = e.Data;
                return true;
            }
        }
        resultId = "";
        data = [];
        return false;
    }

    public void Put(Uri uri, string resultId, uint[] data)
    {
        lock (_gate)
        {
            Remove_NoLock(uri);
            _items[uri] = new Entry(resultId, data, Environment.TickCount64, ++_seq);
            _bytes += (long)data.Length * 4;
            while ((_items.Count > MaxDocuments || _bytes > MaxBytes) && _items.Count > 1)
            {
                var oldest = _items.MinBy(kv => kv.Value.Seq).Key;
                if (oldest == uri) break;
                Remove_NoLock(oldest);
            }
        }
    }

    public void Remove(Uri uri) { lock (_gate) Remove_NoLock(uri); }

    private void Remove_NoLock(Uri uri)
    {
        if (_items.Remove(uri, out var e)) _bytes -= (long)e.Data.Length * 4;
    }

    public long Trim(CsCode.Core.Diagnostics.MemoryTrimLevel level)
    {
        lock (_gate)
        {
            var before = _bytes;
            var now = Environment.TickCount64;
            foreach (var (uri, e) in _items.ToArray())
            {
                var drop = level switch
                {
                    CsCode.Core.Diagnostics.MemoryTrimLevel.Idle => now - e.LastUsedTicks > 60_000,
                    CsCode.Core.Diagnostics.MemoryTrimLevel.Moderate => now - e.LastUsedTicks > 5_000,
                    _ => true,
                };
                if (drop) Remove_NoLock(uri);
            }
            return before - _bytes;
        }
    }

    /// <summary>Applies LSP semantic-token edits (all relative to the ORIGINAL array) and returns the new array.</summary>
    public static bool ApplyEdits(uint[] previous, P.SemanticTokensEdit[] edits, out uint[] result)
    {
        var sorted = edits.OrderBy(e => e.Start).ToArray();
        var total = previous.Length;
        foreach (var e in sorted)
            total += (e.Data?.Length ?? 0) - e.DeleteCount;
        if (total < 0) { result = []; return false; }
        result = new uint[total];
        int src = 0, dst = 0;
        foreach (var e in sorted)
        {
            if (e.Start < src || e.Start > previous.Length || e.DeleteCount < 0 || e.Start + e.DeleteCount > previous.Length) { result = []; return false; }
            var keep = e.Start - src;
            Array.Copy(previous, src, result, dst, keep);
            dst += keep;
            if (e.Data is { Length: > 0 } d) { Array.Copy(d, 0, result, dst, d.Length); dst += d.Length; }
            src = e.Start + e.DeleteCount;
        }
        Array.Copy(previous, src, result, dst, previous.Length - src);
        return true;
    }
}
