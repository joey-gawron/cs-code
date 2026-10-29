using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// Breadcrumbs data for the shell: the chain of document symbols (namespace › class › method…) that contain the caret, for
/// <c>IEditorViewController.GetBreadcrumbsAsync</c>. The symbols come from the <see cref="IDocumentSymbolProvider"/> off the UI thread with a deadline
/// and are cached per document version, so the shell can ask on every caret move without a request per keystroke. An answer for text that changed
/// while it was computed is dropped and asked for again once; providers that fail or hang yield an empty chain.
/// </summary>
internal sealed class BreadcrumbFeature : IDisposable
{
    private readonly CodeEditorControl _v;
    private (int Version, IReadOnlyList<DocumentSymbol> Symbols)? _cache;
    private bool _disposed;

    public BreadcrumbFeature(CodeEditorControl view)
    {
        _v = view;
        view.Controller.BreadcrumbsHandler = GetAsync;
    }

    private async ValueTask<IReadOnlyList<string>> GetAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2 && !_disposed; attempt++)
        {
            var doc = _v.Document;
            var version = doc.Version;
            IReadOnlyList<DocumentSymbol>? symbols = _cache is { } c && c.Version == version ? c.Symbols : null;
            if (symbols is null)
            {
                var providers = _v.Services.Features.GetProviders<IDocumentSymbolProvider>(doc);
                if (providers.Count == 0) return [];
                foreach (var call in providers.Select(p => FeatureSupport.CallAsync(_v, t => p.ProvideDocumentSymbolsAsync(doc, t), ct, TimeSpan.FromSeconds(3))).ToList())
                {
                    if (await call.ConfigureAwait(true) is { } list)
                    {
                        symbols = list;
                        break;
                    }
                }
                if (symbols is null) return [];
                if (_disposed || ct.IsCancellationRequested) return [];
                if (doc.Version != version) continue; // typed meanwhile: these ranges are for older text — ask again
                _cache = (version, symbols);
            }
            return PathTo(symbols, _v.Controller.Caret);
        }
        return [];
    }

    /// <summary>The names of the symbols containing <paramref name="position"/>, outermost first (the smallest containing symbol at each level).</summary>
    internal static IReadOnlyList<string> PathTo(IReadOnlyList<DocumentSymbol> symbols, TextPosition position)
    {
        var path = new List<string>();
        var level = symbols;
        while (path.Count < 32)
        {
            DocumentSymbol? best = null;
            foreach (var s in level)
            {
                if (!s.Range.ContainsInclusive(position)) continue;
                if (best is null || Size(s.Range) < Size(best.Range)) best = s;
            }
            if (best is null) break;
            path.Add(best.Name);
            level = best.Children ?? [];
        }
        return path;
    }

    private static long Size(TextRange r) => (long)(r.End.Line - r.Start.Line) * 100_000 + (r.End.Character - r.Start.Character);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _v.Controller.BreadcrumbsHandler = null;
        _cache = null;
    }
}
