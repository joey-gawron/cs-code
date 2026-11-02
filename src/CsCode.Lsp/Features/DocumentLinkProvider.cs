using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Lsp.Client;
using CsCode.Lsp.Runtime;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Features;

/// <summary>
/// <c>textDocument/documentLink</c> and, when the server offers it, <c>documentLink/resolve</c>. Each Core link keeps the server's link in
/// <see cref="DocumentLink.Data"/> so a resolve sends it back exactly as received. A target that is not an absolute URI (a server may send a
/// relative path) is left null and resolved, or ignored. At most <see cref="MaxLinks"/> links per document.
/// </summary>
internal sealed class DocumentLinkProvider(LanguageClient client, FeatureEntry[] entries) : LspProvider(client, ProviderKind.DocumentLink), IDocumentLinkProvider
{
    public const int MaxLinks = 5000;
    private readonly bool _resolve = entries.Any(e => e.Opt<P.DocumentLinkOptions>()?.ResolveProvider == true);

    public ValueTask<IReadOnlyList<DocumentLink>?> ProvideDocumentLinksAsync(ITextDocument document, CancellationToken ct) =>
        Client.DocRequestAsync<IReadOnlyList<DocumentLink>>(Kind, Feature(), document, LanePriority.Background, async dr =>
        {
            var r = await dr.SendAsync(P.LspRequests.TextDocumentDocumentLink, new P.DocumentLinkParams { TextDocument = dr.Id }).ConfigureAwait(false);
            return r is null ? null : r.Take(MaxLinks).Select(l => new DocumentLink(dr.Mapper.ToCore(l.Range, dr.Uri), Target(l.Target), l.Tooltip,
                P.LspJson.ToElement(l))).ToArray();
        }, ct);

    public async ValueTask<DocumentLink> ResolveDocumentLinkAsync(DocumentLink link, CancellationToken ct)
    {
        if (!_resolve || link.Target is not null || link.Data is not { ValueKind: JsonValueKind.Object } data) return link;
        var lsp = P.LspJson.Deserialize<P.DocumentLink>(data);
        if (lsp is null) return link;
        var resolved = await Client.PlainRequestAsync<P.DocumentLink>(LanePriority.Interactive, default,
            async ctx => await Client.RequestAsync(ctx, P.LspRequests.DocumentLinkResolve, lsp, ctx.Token).ConfigureAwait(false), ct).ConfigureAwait(false);
        return resolved is null ? link : link with { Target = Target(resolved.Target) ?? link.Target, Tooltip = resolved.Tooltip ?? link.Tooltip };
    }

    internal static Uri? Target(string? target) =>
        target is { Length: > 0 } && Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri : null;
}
