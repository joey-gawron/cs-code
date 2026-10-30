using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// Breadcrumbs data for the shell: the chain of document symbols (namespace › class › method…) that contain the caret, for
/// <c>IEditorViewController.GetBreadcrumbsAsync</c>. The symbols come from the <see cref="IDocumentSymbolProvider"/> off the UI thread with a deadline
/// and are cached per document version, so the shell can ask on every caret move without a request per keystroke. An answer for text that changed
/// while it was computed is dropped and asked for again once; providers that fail or hang yield an empty chain.
/// <para>
/// The cache also belongs to the set of symbol providers it was computed with (T31): when one comes or goes (a language server that starts after
/// the file was opened, one that stops), the cache is dropped and an answer still in flight is asked for again, so the next request uses the new
/// set instead of the old answer until the next edit. So is a tree-sitter document's when its syntax tree lands after a request gave up on it.
/// </para>
/// </summary>
internal sealed class BreadcrumbFeature : IDisposable
{
    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private (int Version, int Providers, IReadOnlyList<DocumentSymbol> Symbols)? _cache;
    private int _providers; // bumped (any thread) when the symbol providers change: answers of an older set are stale
    private bool _disposed;

    public BreadcrumbFeature(CodeEditorControl view)
    {
        _v = view;
        view.Controller.BreadcrumbsHandler = GetAsync;
        _d.Add(view.Services.Features.ProvidersChanged.Subscribe(t =>
        {
            if (t == typeof(IDocumentSymbolProvider)) Interlocked.Increment(ref _providers);
        }));
        _d.Add(view.Factory.Tokens.SyntaxTreeArrived.Subscribe(d =>
        {
            if (ReferenceEquals(d, _v.Document)) Interlocked.Increment(ref _providers);
        }));
    }

    private async ValueTask<IReadOnlyList<string>> GetAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3 && !_disposed; attempt++)
        {
            var doc = _v.Document;
            var version = doc.Version;
            var providerSet = Volatile.Read(ref _providers);
            IReadOnlyList<DocumentSymbol>? symbols = _cache is { } c && c.Version == version && c.Providers == providerSet ? c.Symbols : null;
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
                if (_disposed || ct.IsCancellationRequested) return [];
                // providers came or went meanwhile: this answer is from the old set — ask the new one
                if (Volatile.Read(ref _providers) != providerSet) continue;
                if (symbols is null) return [];
                if (doc.Version != version) continue; // typed meanwhile: these ranges are for older text — ask again
                _cache = (version, providerSet, symbols);
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
        _d.Dispose();
        _v.Controller.BreadcrumbsHandler = null;
        _cache = null;
    }
}
