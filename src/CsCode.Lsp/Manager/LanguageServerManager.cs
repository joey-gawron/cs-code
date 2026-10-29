using System.Collections.Concurrent;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Lsp.Client;
using CsCode.Lsp.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Lsp.Manager;

/// <summary>
/// Owns every native language client. Registers definitions (built-in + added + <c>cscode.languageServers</c> overrides), routes open
/// documents to the right server — one server per (definition, workspace root) — starts servers lazily on the first matching document,
/// forwards edits, and shuts everything down. It also implements <see cref="IWillSaveParticipant"/> / <see cref="IFileOperationParticipant"/>
/// for the shell to call.
///
/// Threading: every IDocumentManager event handler only enqueues (open/close/language change are routed ON THE LANGUAGE LANE because root
/// detection probes the file system) or appends to a per-document pending list (change). Nothing here blocks the calling thread.
/// No static state, and documents are only reached through Core interfaces, so the manager can later run in a separate headless process.
/// </summary>
public sealed class LanguageServerManager : ILanguageServerManager, IWillSaveParticipant, IFileOperationParticipant, IAsyncDisposable, IDisposable
{
    private sealed class DocRoute(ServerSlot[] slots) { public readonly ServerSlot[] Slots = slots; }

    private readonly LspServices _services;
    private readonly DisposableStore _store = new();
    private readonly LanguageWorkQueue _queue;
    private readonly ClientContext _ctx;
    private readonly RootDetector _roots = new();
    private readonly object _gate = new();
    private readonly List<(long Seq, LanguageServerDefinition Def)> _registered = new();
    private readonly Dictionary<(string Id, string Key), ServerSlot> _slots = new();
    private readonly ConcurrentDictionary<Uri, DocRoute> _routes = new();
    private readonly List<Task> _pendingRemovals = new();
    private readonly Emitter<IReadOnlyList<LanguageServerInfo>> _changed = new();
    private Dictionary<string, DefinitionOverrides.Effective> _effective = new();
    private volatile LspSettings _settings;
    private long _seq;
    private int _disposed;

    public LanguageServerManager(LspServices services)
    {
        _services = services;
        _settings = LspSettings.Read(services.Configuration);
        var logger = services.Loggers?.CreateLogger("CsCode.Lsp") ?? NullLogger.Instance;
        _queue = new LanguageWorkQueue(_settings.WorkerThreads, _settings.QueueCapacity) { OnUnhandled = ex => logger.LogError(ex, "Unhandled exception on the language lane") };
        _ctx = new ClientContext
        {
            Services = services,
            Settings = () => _settings,
            Queue = _queue,
            Connections = services.Connections ?? new DefaultLspConnectionFactory(),
            Logger = logger,
        };

        if (services.Configuration is { } cfg)
        {
            _store.Add(cfg.RegisterProperties(LspSettings.Properties()));
            _store.Add(cfg.Changed.Subscribe(OnConfigurationChanged));
        }
        if (services.Workspace is { } ws) _store.Add(ws.FoldersChanged.Subscribe(OnFoldersChanged));

        foreach (var d in BuiltinLanguageServers.All) AddDefinitionCore(d);
        RecomputeDefinitions();

        var docs = services.Documents;
        _store.Add(docs.Opened.Subscribe(e => PostRoute(e.Document)));
        _store.Add(docs.Closed.Subscribe(e => Unroute(e.Document)));
        _store.Add(docs.Changed.Subscribe(OnDocumentChanged));
        _store.Add(docs.Saved.Subscribe(e => OnDocumentSaved(e.Document)));
        _store.Add(docs.LanguageChanged.Subscribe(e => PostRoute(e.Document)));
        foreach (var d in docs.Documents) PostRoute(d);
    }

    // =================================================================================================================
    // ILanguageServerManager
    // =================================================================================================================

    public IReadOnlyList<LanguageServerDefinition> Definitions
    {
        get { lock (_gate) return _effective.Values.Select(e => e.Definition).ToArray(); }
    }

    public IReadOnlyList<LanguageServerInfo> Servers
    {
        get { lock (_gate) return _slots.Values.Select(s => s.Info).OrderBy(i => i.Id).ThenBy(i => i.RootUri?.AbsoluteUri).ToArray(); }
    }

    public IEventSource<IReadOnlyList<LanguageServerInfo>> ServersChanged => _changed;

    public IDisposable AddDefinition(LanguageServerDefinition definition)
    {
        long seq;
        lock (_gate) seq = AddDefinitionCore(definition);
        RecomputeDefinitions();
        PostRouteAll();
        return Disposable.Create(() =>
        {
            lock (_gate) _registered.RemoveAll(r => r.Seq == seq);
            RecomputeDefinitions();
            PostRouteAll();
        });
    }

    private long AddDefinitionCore(LanguageServerDefinition d)
    {
        var seq = Interlocked.Increment(ref _seq);
        _registered.Add((seq, d));
        return seq;
    }

    public async ValueTask StartAsync(string id, CancellationToken ct = default)
    {
        var slots = SlotsOf(id);
        if (slots.Length == 0)
        {
            DefinitionOverrides.Effective eff;
            lock (_gate) if (!_effective.TryGetValue(id, out eff)) return;
            if (!eff.Enabled) return;
            var root = _roots.Resolve(eff.Definition, new Uri("file:///"), _services.Workspace?.Folders ?? []);
            slots = [GetOrCreateSlot(eff.Definition, root)];
        }
        foreach (var s in slots) await s.StartAsync(user: true, ct).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(string id, CancellationToken ct = default)
    {
        foreach (var s in SlotsOf(id)) await s.StopAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask RestartAsync(string id, CancellationToken ct = default)
    {
        var slots = SlotsOf(id);
        if (slots.Length == 0) { await StartAsync(id, ct).ConfigureAwait(false); return; }
        foreach (var s in slots) await s.RestartAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask StopAllAsync(CancellationToken ct = default)
    {
        ServerSlot[] slots;
        lock (_gate) slots = _slots.Values.ToArray();
        await Task.WhenAll(slots.Select(s => s.StopAsync(ct))).ConfigureAwait(false);
    }

    private ServerSlot[] SlotsOf(string id)
    {
        lock (_gate) return _slots.Where(kv => kv.Key.Id == id).Select(kv => kv.Value).ToArray();
    }

    internal void OnSlotChanged()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _changed.Fire(Servers);
    }

    // =================================================================================================================
    // Definitions
    // =================================================================================================================

    private void RecomputeDefinitions()
    {
        List<(ServerSlot Slot, DefinitionOverrides.Effective? New)> updates = [];
        lock (_gate)
        {
            var latest = _registered.GroupBy(r => r.Def.Id).Select(g => g.MaxBy(x => x.Seq).Def);
            _effective = DefinitionOverrides.Apply(latest, _services.Configuration?.GetRaw(LspSettingKeys.LanguageServers));
            foreach (var slot in _slots.Values)
                updates.Add((slot, _effective.TryGetValue(slot.Definition.Id, out var e) ? e : null));
        }
        foreach (var (slot, eff) in updates)
        {
            if (eff is not { } e || !e.Enabled) { RemoveSlot(slot); continue; }
            slot.UpdateDefinition(e.Definition, slot.Folders);
        }
    }

    /// <summary>Starts removing a slot (stop + dispose) and remembers the task, so disposing the manager waits for stops already in flight.</summary>
    private void RemoveSlot(ServerSlot slot)
    {
        var task = RemoveSlotAsync(slot);
        lock (_gate)
        {
            _pendingRemovals.RemoveAll(t => t.IsCompleted);
            _pendingRemovals.Add(task);
        }
    }

    private async Task RemoveSlotAsync(ServerSlot slot)
    {
        lock (_gate) _slots.Remove((slot.Definition.Id, slot.Key));
        foreach (var kv in _routes.ToArray())
            if (kv.Value.Slots.Contains(slot)) _routes[kv.Key] = new DocRoute(kv.Value.Slots.Where(s => s != slot).ToArray());
        try { await slot.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
        OnSlotChanged();
    }

    private void OnConfigurationChanged(ConfigurationChangedEventArgs e)
    {
        if (e.Affects("cscode.lsp")) _settings = LspSettings.Read(_services.Configuration);
        if (e.Affects(LspSettingKeys.LanguageServers))
        {
            _queue.Post(LanePriority.Background, () => { RecomputeDefinitions(); RouteAll(); }, exempt: true);
        }
    }

    private void OnFoldersChanged(WorkspaceFoldersChangedEventArgs e)
    {
        if (e.Removed.Count == 0) return;
        ServerSlot[] doomed;
        lock (_gate)
            doomed = _slots.Values.Where(s => s.RootUri is { } r && e.Removed.Any(f => IsUnder(r, f))).ToArray();
        foreach (var s in doomed) RemoveSlot(s);
    }

    private static bool IsUnder(Uri child, Uri parent) =>
        child.IsFile && parent.IsFile &&
        (child.LocalPath.TrimEnd('/') + "/").StartsWith(parent.LocalPath.TrimEnd('/') + "/", StringComparison.Ordinal);

    // =================================================================================================================
    // Document routing
    // =================================================================================================================

    private void PostRoute(ITextDocument doc) => _queue.Post(LanePriority.Interactive, () => Route(doc), exempt: true);

    private void PostRouteAll() => _queue.Post(LanePriority.Background, RouteAll, exempt: true);

    private void RouteAll()
    {
        foreach (var d in _services.Documents.Documents) Route(d);
    }

    /// <summary>Brings the document's server set in line with the current definitions (idempotent; runs on the lane).</summary>
    private void Route(ITextDocument doc)
    {
        if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(_services.Documents.Get(doc.Uri), doc)) return; // closed before we got here

        List<DefinitionOverrides.Effective> candidates;
        lock (_gate) candidates = _effective.Values.Where(e => e.Enabled && e.Definition.Selector.Score(doc) > 0).ToList();
        var folders = _services.Workspace?.Folders ?? [];

        var desired = new List<ServerSlot>(candidates.Count);
        foreach (var c in candidates)
        {
            var root = _roots.Resolve(c.Definition, doc.Uri, folders);
            desired.Add(GetOrCreateSlot(c.Definition, root));
        }

        _routes.TryGetValue(doc.Uri, out var previous);
        var before = previous?.Slots ?? [];
        foreach (var gone in before.Except(desired)) gone.Detach(doc.Uri);
        if (desired.Count == 0) _routes.TryRemove(doc.Uri, out _);
        else _routes[doc.Uri] = new DocRoute([.. desired]);
        foreach (var s in desired) s.Attach(doc); // idempotent for slots that already have it

        // The document may have been closed while we were attaching: never leave a closed document attached anywhere.
        if (!ReferenceEquals(_services.Documents.Get(doc.Uri), doc)) Unroute(doc);
    }

    private ServerSlot GetOrCreateSlot(LanguageServerDefinition def, RootDetector.Root root)
    {
        lock (_gate)
        {
            var key = (def.Id, root.Key);
            if (_slots.TryGetValue(key, out var existing)) return existing;
            var folders = root.FollowsWorkspaceFolders ? (_services.Workspace?.Folders ?? []) : (root.Uri is null ? [] : [root.Uri]);
            var slot = new ServerSlot(this, _ctx, def, root.Key, root.Uri, folders);
            _slots[key] = slot;
            return slot;
        }
    }

    private void Unroute(ITextDocument doc)
    {
        if (!_routes.TryRemove(doc.Uri, out var route)) return;
        foreach (var s in route.Slots) s.Detach(doc.Uri);
    }

    private void OnDocumentChanged(TextDocumentChangedEventArgs args)
    {
        if (!_routes.TryGetValue(args.Document.Uri, out var route)) return;
        foreach (var s in route.Slots) s.OnChanged(args);
    }

    private void OnDocumentSaved(ITextDocument doc)
    {
        if (!_routes.TryGetValue(doc.Uri, out var route)) return;
        foreach (var s in route.Slots) s.OnSaved(doc);
    }

    // =================================================================================================================
    // Participants called by the shell
    // =================================================================================================================

    public async ValueTask<IReadOnlyList<TextEdit>?> OnWillSaveAsync(ITextDocument document, DocumentSaveReason reason, CancellationToken ct)
    {
        if (!_routes.TryGetValue(document.Uri, out var route)) return null;
        List<TextEdit>? edits = null;
        foreach (var slot in route.Slots)
            if (slot.Client is { } client && await client.WillSaveAsync(document, reason, ct).ConfigureAwait(false) is { Count: > 0 } e)
                (edits ??= []).AddRange(e);
        return edits;
    }

    private IEnumerable<LanguageClient> RunningClients()
    {
        lock (_gate) return _slots.Values.Select(s => s.Client).Where(c => c is { IsOperational: true }).Select(c => c!).ToArray();
    }

    public async ValueTask<WorkspaceEdit?> WillRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct)
    {
        var merged = new Dictionary<Uri, IReadOnlyList<TextEdit>>();
        List<ResourceOperation>? ops = null;
        foreach (var c in RunningClients())
        {
            if (await c.WillRenameFilesAsync(files, ct).ConfigureAwait(false) is not { } edit) continue;
            foreach (var (uri, list) in edit.Changes) merged[uri] = merged.TryGetValue(uri, out var ex) ? [.. ex, .. list] : list;
            if (edit.ResourceOperations is { Count: > 0 } o) (ops ??= []).AddRange(o);
        }
        return merged.Count == 0 && ops is null ? null : new WorkspaceEdit(merged, ops);
    }

    public async ValueTask DidRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct)
    {
        foreach (var c in RunningClients()) await c.DidRenameFilesAsync(files, ct).ConfigureAwait(false);
    }

    public async ValueTask DidCreateFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct)
    {
        foreach (var c in RunningClients()) await c.DidCreateFilesAsync(files, ct).ConfigureAwait(false);
    }

    public async ValueTask DidDeleteFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct)
    {
        foreach (var c in RunningClients()) await c.DidDeleteFilesAsync(files, ct).ConfigureAwait(false);
    }

    // =================================================================================================================
    // Introspection for tests/diagnostics
    // =================================================================================================================

    internal LanguageWorkQueue Queue => _queue;
    internal IReadOnlyList<ServerSlot> Slots { get { lock (_gate) return _slots.Values.ToArray(); } }
    internal int RoutedDocumentCount => _routes.Count;

    // =================================================================================================================
    // Disposal
    // =================================================================================================================

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _store.Dispose(); } catch (AggregateException) { }
        ServerSlot[] slots;
        Task[] removals;
        lock (_gate) { slots = _slots.Values.ToArray(); _slots.Clear(); removals = _pendingRemovals.ToArray(); }
        await Task.WhenAll(slots.Select(async s =>
        {
            try { await s.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
        }).Concat(removals)).ConfigureAwait(false); // stops that were already in flight (definition removed / folder closed) must finish too
        _routes.Clear();
        _queue.Dispose();
        _changed.Clear();
    }

    public void Dispose() => Task.Run(() => DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(15));
}
