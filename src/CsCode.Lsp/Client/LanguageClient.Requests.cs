using CsCode.Core.Text;
using CsCode.Lsp.Features;
using CsCode.Lsp.Runtime;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Client;

/// <summary>Context of one document-scoped request, handed to the provider's lane-side body.</summary>
internal sealed class DocRequest(LanguageClient client, SyncedDocument doc, WorkContext ctx, CancellationToken token, int version)
{
    public LanguageClient Client { get; } = client;
    public SyncedDocument Doc { get; } = doc;
    public ITextDocument Document => Doc.Document;
    public Uri Uri => Doc.Uri;
    public WorkContext Ctx { get; } = ctx;
    /// <summary>Cancelled when superseded, when the document changes, or when the session ends.</summary>
    public CancellationToken Token { get; } = token;
    public int Version { get; } = version;
    public LspMapper Mapper => Client.Mapper;
    public P.TextDocumentIdentifier Id => new() { Uri = Doc.UriString };

    public ValueTask<TResult?> SendAsync<TParams, TResult>(P.LspRequest<TParams, TResult> request, TParams @params, int contentModifiedRetries = 0)
        where TParams : class =>
        Client.RequestAsync(Ctx, request, @params, Token, Doc, contentModifiedRetries);
}

internal sealed partial class LanguageClient
{
    /// <summary>
    /// Entry point of every document-scoped provider call. The synchronous part (on the caller's thread — possibly the UI thread) is a
    /// handful of field reads plus one enqueue. On the lane: stale-check (cancelled before it is ever sent when the text moved on), run
    /// the body, stale-check again (a response for an older document version is dropped).
    /// </summary>
    internal ValueTask<T?> DocRequestAsync<T>(ProviderKind kind, int feature, ITextDocument document, LanePriority priority, Func<DocRequest, ValueTask<T?>> body, CancellationToken ct) =>
        !_features.Allows(kind, document) ? default : DocRequestCoreAsync(document, priority, sd => new WorkKey(sd, feature), body, ct);

    /// <summary>
    /// A server extension's request about a document the server has open (Expand Macro, Matching Brace…): no feature registration gates it, and
    /// two calls never supersede each other. Otherwise <see cref="DocRequestAsync{T}"/>.
    /// </summary>
    internal ValueTask<T?> ExtensionDocRequestAsync<T>(ITextDocument document, Func<DocRequest, ValueTask<T?>> body, CancellationToken ct) =>
        DocRequestCoreAsync(document, LanePriority.Interactive, _ => default, body, ct);

    private ValueTask<T?> DocRequestCoreAsync<T>(ITextDocument document, LanePriority priority, Func<SyncedDocument, WorkKey> key, Func<DocRequest, ValueTask<T?>> body, CancellationToken ct)
    {
        if (!IsOperational || !_docs.TryGetValue(document.Uri, out var sd) || sd.Ignored) return default;
        if (_ctx.Queue.ShouldShed(priority)) return default;
        var version = document.Version;
        return _ctx.Queue.RunAsync<T>(priority, key(sd), async ctx =>
        {
            if (sd.Detached || Volatile.Read(ref _tornDown) != 0 || document.Version != version) return default;
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token, sd.GetVersionToken());
            T? result;
            try
            {
                result = await body(new DocRequest(this, sd, ctx, link.Token, version)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ctx.Token.IsCancellationRequested)
            {
                return default; // the document changed while the request was in flight: abandoned, not an error
            }
            return document.Version != version ? default : result;
        }, ct);
    }

    /// <summary>Non-document request (workspace symbols, resolve, hierarchy items...).</summary>
    internal ValueTask<T?> PlainRequestAsync<T>(LanePriority priority, WorkKey key, Func<WorkContext, ValueTask<T?>> body, CancellationToken ct)
    {
        if (!IsOperational || _ctx.Queue.ShouldShed(priority)) return default;
        return _ctx.Queue.RunAsync<T>(priority, key, body, ct);
    }
}
