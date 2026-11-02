using System.Text.Json;
using CsCode.Core.Text;

namespace CsCode.Core.Languages;

/// <summary>
/// A link in a document (LSP <c>textDocument/documentLink</c>): clangd's <c>#include</c> targets, later HTML <c>href</c>s and CSS <c>url()</c>s.
/// <see cref="Target"/> is null until <see cref="IDocumentLinkProvider.ResolveDocumentLinkAsync"/> fills it in; <see cref="Data"/> is the
/// provider's own (an LSP provider keeps the server's link in it for <c>documentLink/resolve</c>). Additive (stage 3, C).
/// </summary>
public sealed record DocumentLink(TextRange Range, Uri? Target, string? Tooltip = null, JsonElement? Data = null);

/// <summary>The links of a document, for the editor's Ctrl/⌘+Click (underlined while the modifier is held over one). Additive (stage 3, C).</summary>
public interface IDocumentLinkProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<DocumentLink>?> ProvideDocumentLinksAsync(ITextDocument document, CancellationToken ct);

    /// <summary>Fills in a link's <see cref="DocumentLink.Target"/> (and tooltip) when the provider left it out; returns the link unchanged when it cannot.</summary>
    ValueTask<DocumentLink> ResolveDocumentLinkAsync(DocumentLink link, CancellationToken ct) => ValueTask.FromResult(link);
}
