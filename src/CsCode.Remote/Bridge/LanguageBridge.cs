using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Bridge;

/// <summary>
/// Mirrors the server's language-feature providers into the local <see cref="ILanguageFeatureRegistry"/>: for every <c>lang/registered</c> it registers
/// one bridge provider (same feature, the server's selector restricted to this connection's scheme, same trigger characters / legend) that forwards
/// each call as <c>lang/invoke</c>. Core models cross the wire as they are, with document URIs mapped by the connection. Remote failures and
/// timeouts answer "no result" like a slow local server does; cancellation propagates. Every bridge disappears when the connection does.
/// </summary>
public sealed class LanguageBridge : IRemoteParticipant, IDisposable
{
    private readonly ILanguageFeatureRegistry _registry;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<long, IDisposable> _tokens = new();
    private volatile RemoteSession? _session;

    public LanguageBridge(ILanguageFeatureRegistry registry, UriMapper mapper, string? ownerPrefix = null, ILogger? log = null)
    {
        _registry = registry;
        Mapper = mapper;
        OwnerPrefix = ownerPrefix ?? "remote";
        _log = log ?? NullLogger.Instance;
    }

    public UriMapper Mapper { get; }
    public string OwnerPrefix { get; }
    internal RpcPeer? Peer => _session is { IsOpen: true } s ? s.Peer : null;

    /// <summary>Interactive calls (hover, completion…) give up after this and answer null; the server's own language client times out first.</summary>
    public TimeSpan InteractiveTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan BackgroundTimeout { get; init; } = TimeSpan.FromSeconds(35);

    /// <summary>Bridged registrations (tests/diagnostics).</summary>
    public int Count => _tokens.Count;

    public void Attach(RpcPeer peer)
    {
        peer.OnNotification<LangRegistration>(RemoteMethods.LangRegistered, Register);
        peer.OnNotification<LangUnregistration>(RemoteMethods.LangUnregistered, u => { if (_tokens.TryRemove(u.Id, out var t)) t.Dispose(); });
    }

    public async Task OnConnectedAsync(RemoteSession session, CancellationToken ct)
    {
        _session = session;
        // Anything the server registered before our handlers saw it (a resync after reconnect re-opens documents, so normally nothing).
        try
        {
            var existing = await session.Peer.InvokeAsync<Empty, IReadOnlyList<LangRegistration>>("lang/list", Empty.Value, ct).ConfigureAwait(false) ?? [];
            foreach (var r in existing) Register(r);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { _log.LogDebug("lang/list failed: {Error}", ex.Message); }
    }

    public void OnDisconnected()
    {
        _session = null;
        foreach (var id in _tokens.Keys.ToArray())
            if (_tokens.TryRemove(id, out var t)) t.Dispose();
    }

    /// <summary>The server's selector, restricted to what this connection serves (ssh: scheme of that host; local files for the process host).</summary>
    internal DocumentSelector MapSelector(DocumentSelector selector)
    {
        if (Mapper is not SshUriMapper remote) return selector;
        return new DocumentSelector(selector.Filters.Select(f => f with { Scheme = remote.Authority.Scheme }).ToArray());
    }

    private void Register(LangRegistration r)
    {
        if (_tokens.ContainsKey(r.Id)) return;
        var selector = MapSelector(r.Selector);
        var owner = OwnerPrefix + ":" + (r.Owner ?? "server");
        IDisposable? token = r.Feature switch
        {
            nameof(ICompletionProvider) => _registry.Register<ICompletionProvider>(selector, new CompletionBridge(this, r), owner),
            nameof(IHoverProvider) => _registry.Register<IHoverProvider>(selector, new HoverBridge(this, r), owner),
            nameof(IDefinitionProvider) => _registry.Register<IDefinitionProvider>(selector, new DefinitionBridge(this, r), owner),
            nameof(IReferenceProvider) => _registry.Register<IReferenceProvider>(selector, new ReferenceBridge(this, r), owner),
            nameof(IDocumentHighlightProvider) => _registry.Register<IDocumentHighlightProvider>(selector, new HighlightBridge(this, r), owner),
            nameof(IRenameProvider) => _registry.Register<IRenameProvider>(selector, new RenameBridge(this, r), owner),
            nameof(IDocumentFormattingProvider) => _registry.Register<IDocumentFormattingProvider>(selector, new FormattingBridge(this, r), owner),
            nameof(ISignatureHelpProvider) => _registry.Register<ISignatureHelpProvider>(selector, new SignatureHelpBridge(this, r), owner),
            nameof(ICodeActionProvider) => _registry.Register<ICodeActionProvider>(selector, new CodeActionBridge(this, r), owner),
            nameof(IDocumentSymbolProvider) => _registry.Register<IDocumentSymbolProvider>(selector, new DocumentSymbolBridge(this, r), owner),
            nameof(IWorkspaceSymbolProvider) => _registry.Register<IWorkspaceSymbolProvider>(DocumentSelector.All, new WorkspaceSymbolBridge(this, r), owner),
            nameof(IFoldingRangeProvider) => _registry.Register<IFoldingRangeProvider>(selector, new FoldingBridge(this, r), owner),
            nameof(ISelectionRangeProvider) => _registry.Register<ISelectionRangeProvider>(selector, new SelectionRangeBridge(this, r), owner),
            nameof(ISemanticTokensProvider) => _registry.Register<ISemanticTokensProvider>(selector, new SemanticTokensBridge(this, r), owner),
            nameof(ISemanticTokensRangeProvider) => _registry.Register<ISemanticTokensRangeProvider>(selector, new SemanticTokensRangeBridge(this, r), owner),
            nameof(IInlayHintsProvider) => _registry.Register<IInlayHintsProvider>(selector, new InlayHintsBridge(this, r), owner),
            nameof(ICodeLensProvider) => _registry.Register<ICodeLensProvider>(selector, new CodeLensBridge(this, r), owner),
            nameof(IDocumentColorProvider) => _registry.Register<IDocumentColorProvider>(selector, new ColorBridge(this, r), owner),
            nameof(ILinkedEditingRangeProvider) => _registry.Register<ILinkedEditingRangeProvider>(selector, new LinkedEditingBridge(this, r), owner),
            nameof(ICallHierarchyProvider) => _registry.Register<ICallHierarchyProvider>(selector, new CallHierarchyBridge(this, r), owner),
            nameof(ITypeHierarchyProvider) => _registry.Register<ITypeHierarchyProvider>(selector, new TypeHierarchyBridge(this, r), owner),
            _ => null,
        };
        if (token is null) { _log.LogDebug("No bridge for remote feature {Feature}", r.Feature); return; }
        if (!_tokens.TryAdd(r.Id, token)) token.Dispose();
        else if (_session is null) { if (_tokens.TryRemove(r.Id, out var late)) late.Dispose(); } // raced with a disconnect
    }

    /// <summary>One forwarded provider call; null when the document is not this connection's, the link is down, or the call failed/timed out.</summary>
    internal async ValueTask<T?> CallAsync<T>(long registration, string op, ITextDocument? doc, LangArgs args, bool interactive, CancellationToken ct) where T : class
    {
        if (doc is not null && !Mapper.Handles(doc.Uri)) return null;
        if (Peer is not { } peer) return null;
        try
        {
            return await peer.InvokeAsync<LangInvokeParams, T>(RemoteMethods.LangInvoke, new LangInvokeParams(registration, op, doc?.Uri, doc?.Version ?? 0, args), ct,
                interactive ? InteractiveTimeout : BackgroundTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.Text.Json.JsonException)
        {
            _log.LogDebug("Remote {Op} failed: {Error}", op, ex.Message);
            return null;
        }
    }

    // Items of resolvable results remember where they came from, so the server resolves its own original instance (language clients need it).
    private readonly ConditionalWeakTable<object, OriginToken> _origins = new();

    internal void Remember<TItem>(string? resultId, IReadOnlyList<TItem>? items) where TItem : class
    {
        if (resultId is null || items is null) return;
        for (var i = 0; i < items.Count; i++) _origins.AddOrUpdate(items[i], new OriginToken(resultId, i));
    }

    internal OriginToken? OriginOf(object item) => _origins.TryGetValue(item, out var t) ? t : null;

    public void Dispose() => OnDisconnected();
}
