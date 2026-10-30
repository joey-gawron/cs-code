using System.Collections.Concurrent;
using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CsCode.Server;

/// <summary>
/// R2 language bridging, server side. The client's open documents are mirrored into this process's <see cref="IDocumentManager"/> (the real
/// <see cref="ILanguageServerManager"/> here starts language servers for them, next to the files); every provider those servers register is announced
/// to the client (<c>lang/registered</c>) and called on its behalf (<c>lang/invoke</c>); diagnostics, the server list and contributed commands flow back.
/// Document notifications run inline on the connection's reader (in order, before any later request is read), the rest on handler threads.
/// </summary>
internal sealed class LanguageHost : IDisposable
{
    private const int OriginalsKept = 8;

    private readonly IServiceProvider _sp;
    private readonly IRpcEndpoint _peer;
    private readonly IDocumentManager _documents;
    private readonly MirroringFeatureRegistry _registry;
    private readonly IDiagnosticService _diagnostics;
    private readonly ILogger _log;
    private readonly DisposableStore _subs = new();
    private readonly ConcurrentDictionary<Uri, (MirrorDocument Doc, IDisposable Token)> _open = new();
    private readonly ConcurrentDictionary<long, IDisposable> _definitions = new();
    private readonly object _diagGate = new();
    private readonly HashSet<Uri> _diagPending = new();
    private readonly Timer _diagTimer;
    private readonly Timer _serversTimer;
    private readonly Timer _commandsTimer;
    private readonly object _originsGate = new();
    private readonly LinkedList<(string Id, object[] Items)> _originals = new();
    private ILanguageServerManager? _manager;
    private int _diagScheduled, _disposed;

    public LanguageHost(IServiceProvider sp, IRpcEndpoint peer)
    {
        _sp = sp;
        _peer = peer;
        _documents = sp.GetRequiredService<IDocumentManager>();
        _registry = (MirroringFeatureRegistry)sp.GetRequiredService<ILanguageFeatureRegistry>();
        _diagnostics = sp.GetRequiredService<IDiagnosticService>();
        _log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CsCode.Server.Languages");
        _diagTimer = new Timer(_ => FlushDiagnostics(), null, Timeout.Infinite, Timeout.Infinite);
        _serversTimer = new Timer(_ => PublishServers(), null, Timeout.Infinite, Timeout.Infinite);
        _commandsTimer = new Timer(_ => PublishCommands(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public IReadOnlyList<OpenDocumentInfo> Documents => _open.Values.Select(d => new OpenDocumentInfo(d.Doc.Uri, d.Doc.Version, d.Doc.Length)).ToArray();

    public void Register()
    {
        var p = _peer;
        p.OnNotification<DocOpenParams>(RemoteMethods.DocOpen, Open, inline: true);
        p.OnNotification<DocChangeParams>(RemoteMethods.DocChange, Change, inline: true);
        p.OnNotification<DocUriParams>(RemoteMethods.DocClose, Close, inline: true);
        p.OnNotification<DocLanguageParams>(RemoteMethods.DocLanguage, e => { if (_open.TryGetValue(e.Uri, out var d)) d.Doc.SetLanguageId(e.LanguageId); }, inline: true);
        p.OnNotification<DocUriParams>(RemoteMethods.DocSaved, e => { if (_open.TryGetValue(e.Uri, out var d)) _documents.NotifySaved(d.Doc); }, inline: true);
        p.OnNotification<DocKeepParams>(RemoteMethods.DocKeep, Keep, inline: true);
        p.OnRequest<LangInvokeParams, JsonElement?>(RemoteMethods.LangInvoke, InvokeAsync);
        p.OnRequest<WillSaveParams, IReadOnlyList<TextEdit>?>(RemoteMethods.LangWillSave, WillSaveAsync);
        p.OnRequest<FileOperationParams, WorkspaceEdit?>(RemoteMethods.LangFileOperation, FileOperationAsync);
        p.OnRequest<LspControlParams, Empty>(RemoteMethods.LspControl, ControlAsync);
        p.OnRequest<LanguageServerDefinition, LspDefinitionResult>(RemoteMethods.LspAddDefinition, (d, _) =>
        {
            var token = Manager.AddDefinition(d);
            var id = Interlocked.Increment(ref _definitionIds);
            _definitions[id] = token;
            return ValueTask.FromResult(new LspDefinitionResult(id));
        });
        p.OnRequest<LspRemoveDefinitionParams, Empty>(RemoteMethods.LspRemoveDefinition, (d, _) =>
        {
            if (_definitions.TryRemove(d.Token, out var t)) t.Dispose();
            return ValueTask.FromResult(Empty.Value);
        });
        p.OnRequest<ExecuteCommandParams, ExecuteCommandResult>(RemoteMethods.CommandsExecute, ExecuteCommandAsync);
        p.OnRequest<Empty, IReadOnlyList<LangRegistration>>("lang/list", (_, _) => ValueTask.FromResult<IReadOnlyList<LangRegistration>>(_registry.Registrations.Select(ToWire).ToArray()));

        _subs.Add(_registry.Added.Subscribe(r => _peer.Notify(RemoteMethods.LangRegistered, ToWire(r))));
        _subs.Add(_registry.Removed.Subscribe(r => _peer.Notify(RemoteMethods.LangUnregistered, new LangUnregistration(r.Id))));
        _subs.Add(_diagnostics.Changed.Subscribe(OnDiagnosticsChanged));
        if (_sp.GetService<ICommandService>() is { } commands) _subs.Add(commands.Registered.Subscribe(_ => Schedule(_commandsTimer, 100)));
        // The manager starts observing documents when first resolved: resolve it now, before any doc/open arrives.
        _manager = _sp.GetRequiredService<ILanguageServerManager>();
        _subs.Add(_manager.ServersChanged.Subscribe(_ => Schedule(_serversTimer, 100)));
    }

    private long _definitionIds;
    private ILanguageServerManager Manager => _manager ?? throw new InvalidOperationException("Language host not started");

    private static LangRegistration ToWire(MirroringFeatureRegistry.Registration r) => new(r.Id, r.Feature.Name, r.Selector, r.Owner, r.Provider switch
    {
        ICompletionProvider c when r.Feature == typeof(ICompletionProvider) => new ProviderInfo(TriggerCharacters: c.TriggerCharacters),
        ISignatureHelpProvider s when r.Feature == typeof(ISignatureHelpProvider) => new ProviderInfo(TriggerCharacters: s.TriggerCharacters, RetriggerCharacters: s.RetriggerCharacters),
        IDocumentFormattingProvider f when r.Feature == typeof(IDocumentFormattingProvider) => new ProviderInfo(OnTypeTriggerCharacters: f.OnTypeTriggerCharacters),
        ISemanticTokensProvider t when r.Feature == typeof(ISemanticTokensProvider) => new ProviderInfo(Legend: t.Legend),
        ISemanticTokensRangeProvider t when r.Feature == typeof(ISemanticTokensRangeProvider) => new ProviderInfo(Legend: t.Legend),
        _ => null,
    });

    // ------------------------------------------------------------------------------------------------------------------- documents

    private void Open(DocOpenParams e)
    {
        if (_open.TryGetValue(e.Uri, out var existing))
        {
            // reconnect / resync: same document, new full text
            if (existing.Doc.LanguageId != e.LanguageId) existing.Doc.SetLanguageId(e.LanguageId);
            existing.Doc.Reset(e.Version, e.Text);
            return;
        }
        var doc = new MirrorDocument(e.Uri, e.LanguageId, e.Version, e.Text);
        _open[e.Uri] = (doc, _documents.Register(doc));
    }

    /// <summary>After a reconnect to this (persistent) server: documents the client closed while it was away are closed here too.</summary>
    private void Keep(DocKeepParams e)
    {
        var keep = e.Uris.ToHashSet();
        foreach (var uri in _open.Keys.Where(u => !keep.Contains(u)).ToArray())
            if (_open.TryRemove(uri, out var d)) d.Token.Dispose();
    }

    /// <summary>
    /// A client (re)attached: it dropped its mirrors when its previous connection went away, so the current diagnostics, server list and commands
    /// are announced again (registrations it fetches itself with <c>lang/list</c>).
    /// </summary>
    public void OnAttached()
    {
        var uris = _diagnostics.GetAll().Select(d => d.Uri).Distinct().ToArray();
        if (uris.Length > 0) OnDiagnosticsChanged(uris);
        Schedule(_serversTimer, 10);
        Schedule(_commandsTimer, 10);
    }

    private void Change(DocChangeParams e)
    {
        if (!_open.TryGetValue(e.Uri, out var d)) return;
        if (!d.Doc.ApplyClientChanges(e.Version, e.Changes))
            _log.LogWarning("Change to {Uri} v{Version} did not apply; waiting for a resync", e.Uri, e.Version);
    }

    private void Close(DocUriParams e)
    {
        if (_open.TryRemove(e.Uri, out var d)) d.Token.Dispose();
    }

    private ITextDocument? Doc(Uri? uri) => uri is not null && _open.TryGetValue(uri, out var d) ? d.Doc : null;

    // ------------------------------------------------------------------------------------------------------------------- invoke

    private async ValueTask<JsonElement?> InvokeAsync(LangInvokeParams p, CancellationToken ct)
    {
        var reg = _registry.Get(p.Registration);
        if (reg is null) return null; // unregistered meanwhile (server restarted): "no result"
        var a = p.Args;
        var doc = Doc(p.Uri);
        object? result = (reg.Provider, p.Op) switch
        {
            (ICompletionProvider c, LangOps.Completion) when doc is not null && a.Position is { } pos =>
                Resolvable(await c.ProvideCompletionAsync(doc, pos, a.CompletionContext ?? new CompletionContext(CompletionTriggerKind.Invoked), ct).ConfigureAwait(false), l => l.Items),
            (ICompletionProvider c, LangOps.ResolveCompletion) when a.CompletionItem is { } item =>
                await c.ResolveCompletionAsync(Original(a.Origin, item), ct).ConfigureAwait(false),
            (IHoverProvider h, LangOps.Hover) when doc is not null && a.Position is { } pos => await h.ProvideHoverAsync(doc, pos, ct).ConfigureAwait(false),
            (IDefinitionProvider d, LangOps.Definition) when doc is not null && a.Position is { } pos => await d.ProvideDefinitionAsync(doc, pos, a.DefinitionKind, ct).ConfigureAwait(false),
            (IReferenceProvider r, LangOps.References) when doc is not null && a.Position is { } pos => await r.ProvideReferencesAsync(doc, pos, a.Flag, ct).ConfigureAwait(false),
            (IDocumentHighlightProvider h, LangOps.Highlights) when doc is not null && a.Position is { } pos => await h.ProvideDocumentHighlightsAsync(doc, pos, ct).ConfigureAwait(false),
            (IRenameProvider r, LangOps.PrepareRename) when doc is not null && a.Position is { } pos => await r.PrepareRenameAsync(doc, pos, ct).ConfigureAwait(false),
            (IRenameProvider r, LangOps.Rename) when doc is not null && a.Position is { } pos && a.Text is { } name => await r.ProvideRenameAsync(doc, pos, name, ct).ConfigureAwait(false),
            (IDocumentFormattingProvider f, LangOps.Format) when doc is not null => await f.FormatDocumentAsync(doc, a.Formatting ?? new FormattingOptions(), ct).ConfigureAwait(false),
            (IDocumentFormattingProvider f, LangOps.FormatRange) when doc is not null && a.Range is { } range => await f.FormatRangeAsync(doc, range, a.Formatting ?? new FormattingOptions(), ct).ConfigureAwait(false),
            (IDocumentFormattingProvider f, LangOps.FormatOnType) when doc is not null && a.Position is { } pos && a.Text is { } ch =>
                await f.FormatOnTypeAsync(doc, pos, ch, a.Formatting ?? new FormattingOptions(), ct).ConfigureAwait(false),
            (ISignatureHelpProvider s, LangOps.SignatureHelp) when doc is not null && a.Position is { } pos && a.SignatureHelpContext is { } ctx =>
                await s.ProvideSignatureHelpAsync(doc, pos, ctx, ct).ConfigureAwait(false),
            (ICodeActionProvider c, LangOps.CodeActions) when doc is not null && a.Range is { } range && a.CodeActionContext is { } ctx =>
                Resolvable(await c.ProvideCodeActionsAsync(doc, range, ctx, ct).ConfigureAwait(false), l => l),
            (ICodeActionProvider c, LangOps.ResolveCodeAction) when a.CodeAction is { } action => await c.ResolveCodeActionAsync(Original(a.Origin, action), ct).ConfigureAwait(false),
            (IDocumentSymbolProvider s, LangOps.DocumentSymbols) when doc is not null => await s.ProvideDocumentSymbolsAsync(doc, ct).ConfigureAwait(false),
            (IWorkspaceSymbolProvider s, LangOps.WorkspaceSymbols) => await s.ProvideWorkspaceSymbolsAsync(a.Text ?? "", ct).ConfigureAwait(false),
            (IFoldingRangeProvider f, LangOps.FoldingRanges) when doc is not null => await f.ProvideFoldingRangesAsync(doc, ct).ConfigureAwait(false),
            (ISelectionRangeProvider s, LangOps.SelectionRanges) when doc is not null && a.Positions is { } ps => await s.ProvideSelectionRangesAsync(doc, ps, ct).ConfigureAwait(false),
            (ISemanticTokensProvider s, LangOps.SemanticTokens) when doc is not null => await s.ProvideSemanticTokensAsync(doc, ct).ConfigureAwait(false),
            (ISemanticTokensRangeProvider s, LangOps.SemanticTokensRange) when doc is not null && a.Range is { } range => await s.ProvideSemanticTokensRangeAsync(doc, range, ct).ConfigureAwait(false),
            (IInlayHintsProvider h, LangOps.InlayHints) when doc is not null && a.Range is { } range => await h.ProvideInlayHintsAsync(doc, range, ct).ConfigureAwait(false),
            (ICodeLensProvider l, LangOps.CodeLens) when doc is not null => Resolvable(await l.ProvideCodeLensesAsync(doc, ct).ConfigureAwait(false), x => x),
            (ICodeLensProvider l, LangOps.ResolveCodeLens) when a.CodeLens is { } lens => await l.ResolveCodeLensAsync(Original(a.Origin, lens), ct).ConfigureAwait(false),
            (IDocumentColorProvider c, LangOps.DocumentColors) when doc is not null => await c.ProvideDocumentColorsAsync(doc, ct).ConfigureAwait(false),
            (ILinkedEditingRangeProvider l, LangOps.LinkedEditing) when doc is not null && a.Position is { } pos => await l.ProvideLinkedEditingRangesAsync(doc, pos, ct).ConfigureAwait(false),
            (ICallHierarchyProvider h, LangOps.PrepareCallHierarchy) when doc is not null && a.Position is { } pos => await h.PrepareCallHierarchyAsync(doc, pos, ct).ConfigureAwait(false),
            (ICallHierarchyProvider h, LangOps.IncomingCalls) when a.HierarchyItem is { } item => await h.IncomingCallsAsync(item, ct).ConfigureAwait(false),
            (ICallHierarchyProvider h, LangOps.OutgoingCalls) when a.HierarchyItem is { } item => await h.OutgoingCallsAsync(item, ct).ConfigureAwait(false),
            (ITypeHierarchyProvider h, LangOps.PrepareTypeHierarchy) when doc is not null && a.Position is { } pos => await h.PrepareTypeHierarchyAsync(doc, pos, ct).ConfigureAwait(false),
            (ITypeHierarchyProvider h, LangOps.Supertypes) when a.HierarchyItem is { } item => await h.SupertypesAsync(item, ct).ConfigureAwait(false),
            (ITypeHierarchyProvider h, LangOps.Subtypes) when a.HierarchyItem is { } item => await h.SubtypesAsync(item, ct).ConfigureAwait(false),
            _ => null,
        };
        // Serialised here (a handler thread), with the declared shapes: the writer then only copies the element.
        return result is null ? null : JsonSerializer.SerializeToElement(result, result.GetType(), _peer.Json);
    }

    /// <summary>Remembers the items of a resolvable list (bounded: the last <see cref="OriginalsKept"/> lists) and tags the result with their id.</summary>
    private ResolvableResult<T>? Resolvable<T, TItem>(T? value, Func<T, IReadOnlyList<TItem>> items) where T : class where TItem : class
    {
        if (value is null) return new ResolvableResult<T>(null, null);
        var id = Guid.NewGuid().ToString("N")[..12];
        lock (_originsGate)
        {
            _originals.AddFirst((id, items(value).Cast<object>().ToArray()));
            while (_originals.Count > OriginalsKept) _originals.RemoveLast();
        }
        return new ResolvableResult<T>(id, value);
    }

    private T Original<T>(OriginToken? origin, T fallback) where T : class
    {
        if (origin is null) return fallback;
        lock (_originsGate)
            foreach (var (id, items) in _originals)
                if (id == origin.ResultId && origin.Index >= 0 && origin.Index < items.Length && items[origin.Index] is T t) return t;
        return fallback;
    }

    private async ValueTask<IReadOnlyList<TextEdit>?> WillSaveAsync(WillSaveParams p, CancellationToken ct)
    {
        if (Doc(p.Uri) is not { } doc) return null;
        List<TextEdit>? all = null;
        foreach (var participant in _sp.GetServices<IWillSaveParticipant>())
            if (await participant.OnWillSaveAsync(doc, p.Reason, ct).ConfigureAwait(false) is { Count: > 0 } edits) (all ??= new()).AddRange(edits);
        return all;
    }

    private async ValueTask<WorkspaceEdit?> FileOperationAsync(FileOperationParams p, CancellationToken ct)
    {
        WorkspaceEdit? result = null;
        foreach (var participant in _sp.GetServices<IFileOperationParticipant>())
        {
            switch (p.Kind)
            {
                case "willRename" when p.Renames is { } r: result = await participant.WillRenameFilesAsync(r, ct).ConfigureAwait(false) ?? result; break;
                case "didRename" when p.Renames is { } r: await participant.DidRenameFilesAsync(r, ct).ConfigureAwait(false); break;
                case "didCreate" when p.Files is { } f: await participant.DidCreateFilesAsync(f, ct).ConfigureAwait(false); break;
                case "didDelete" when p.Files is { } f: await participant.DidDeleteFilesAsync(f, ct).ConfigureAwait(false); break;
            }
        }
        return result;
    }

    private async ValueTask<Empty> ControlAsync(LspControlParams p, CancellationToken ct)
    {
        switch (p.Action)
        {
            case "start" when p.Id is { } id: await Manager.StartAsync(id, ct).ConfigureAwait(false); break;
            case "stop" when p.Id is { } id: await Manager.StopAsync(id, ct).ConfigureAwait(false); break;
            case "restart" when p.Id is { } id: await Manager.RestartAsync(id, ct).ConfigureAwait(false); break;
            case "stopAll": await Manager.StopAllAsync(ct).ConfigureAwait(false); break;
            case "refresh": PublishServers(); break;
            default: throw new ArgumentException($"Unknown language server action '{p.Action}'");
        }
        return Empty.Value;
    }

    private async ValueTask<ExecuteCommandResult> ExecuteCommandAsync(ExecuteCommandParams p, CancellationToken ct)
    {
        var commands = _sp.GetRequiredService<ICommandService>();
        var args = (p.Arguments ?? []).Select(a => (object?)a).ToArray();
        var value = await commands.ExecuteAsync(p.Command, args).ConfigureAwait(false);
        return new ExecuteCommandResult(value is null ? null : JsonSerializer.SerializeToElement(value, value.GetType(), _peer.Json));
    }

    // ------------------------------------------------------------------------------------------------------------------- outbound

    private void OnDiagnosticsChanged(IReadOnlyCollection<Uri> uris)
    {
        lock (_diagGate) foreach (var u in uris) _diagPending.Add(u);
        if (Interlocked.Exchange(ref _diagScheduled, 1) == 0) Schedule(_diagTimer, 50);
    }

    /// <summary>Coalesced (50 ms), latest state per document, in batches of at most 200 documents.</summary>
    private void FlushDiagnostics()
    {
        Uri[] uris;
        lock (_diagGate)
        {
            uris = _diagPending.ToArray();
            _diagPending.Clear();
        }
        Volatile.Write(ref _diagScheduled, 0);
        foreach (var chunk in uris.Chunk(200))
        {
            var entries = chunk.Select(u => new DiagnosticsEntry(u, _diagnostics.Get(u), Doc(u)?.Version)).ToArray();
            _peer.Notify(RemoteMethods.DiagPublish, new DiagPublishParams(entries));
        }
    }

    private void PublishServers()
    {
        if (_manager is not { } m) return;
        _peer.Notify(RemoteMethods.LspServers, new LspServersParams(m.Servers, m.Definitions));
    }

    private void PublishCommands()
    {
        if (_sp.GetService<ICommandService>() is not { } commands) return;
        _peer.Notify(RemoteMethods.CommandsChanged, new CommandsChangedParams(commands.All.Select(c => c.Id).ToArray()));
    }

    private void Schedule(Timer timer, int ms)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { timer.Change(ms, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _subs.Dispose();
        _diagTimer.Dispose();
        _serversTimer.Dispose();
        _commandsTimer.Dispose();
        foreach (var t in _definitions.Values) t.Dispose();
        _definitions.Clear();
        foreach (var uri in _open.Keys.ToArray())
            if (_open.TryRemove(uri, out var d)) d.Token.Dispose();
    }
}
