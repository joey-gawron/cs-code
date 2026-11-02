using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// Code the preprocessor skips (<see cref="IInactiveRegionsProvider"/>: clangd's <c>textDocument/inactiveRegions</c>) drawn faded, its text at
/// 55% opacity as vscode-clangd draws it by default. The server pushes regions after each parse: when a provider says this document's changed,
/// the feature reads them on the UI thread, places them as anchored segments (they follow edits until the next report) and redraws. A stopped
/// server's provider clears its documents, so their fading goes with it. At most <see cref="MaxRegions"/> regions per document.
/// </summary>
internal sealed class InactiveRegionsFeature : IThemeAware, IDisposable
{
    internal const double Opacity = 0.55;
    internal const int MaxRegions = 10_000;

    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private readonly Dictionary<IInactiveRegionsProvider, IDisposable> _subs = [];
    private readonly Fade _fade;
    private TextSegmentCollection<TextSegment> _segments;
    private bool _scheduled, _disposed;

    public InactiveRegionsFeature(CodeEditorControl view)
    {
        _v = view;
        _segments = new TextSegmentCollection<TextSegment>(view.Document.Buffer);
        _fade = new Fade(this);
        view.TextView.LineTransformers.Add(_fade);
        _d.Add(Disposable.Create(() => view.TextView.LineTransformers.Remove(_fade)));
        _d.Add(view.Services.Features.ProvidersChanged.Subscribe(t => { if (t == typeof(IInactiveRegionsProvider)) _v.Post(Schedule); }));
        _d.Add(view.Document.LanguageIdChanged.Subscribe(_ => _v.Post(Schedule)));
        _d.Add(Disposable.Create(() => { foreach (var s in _subs.Values) s.Dispose(); _subs.Clear(); }));
        Refresh();
    }

    /// <summary>The faded ranges now, as (start, end) offsets in document order.</summary>
    internal IReadOnlyList<(int Start, int End)> Regions => [.. _segments.Select(s => (s.StartOffset, s.EndOffset))];

    public void OnThemeChanged() => _v.TextView.Redraw();

    /// <summary>UI thread: one refresh for any number of reports that arrive before it runs.</summary>
    private void Schedule()
    {
        if (_disposed || _scheduled) return;
        _scheduled = true;
        _v.Post(() =>
        {
            _scheduled = false;
            Refresh();
        });
    }

    /// <summary>Reads the providers' regions for this document now (UI thread; tests call it directly).</summary>
    internal void Refresh()
    {
        if (_disposed) return;
        var doc = _v.Document;
        var providers = _v.Services.Features.GetProviders<IInactiveRegionsProvider>(doc);
        Sync(providers);
        var fresh = new TextSegmentCollection<TextSegment>(doc.Buffer);
        foreach (var region in providers.SelectMany(p => p.GetInactiveRegions(doc.Uri)).Take(MaxRegions))
        {
            var start = doc.OffsetAt(region.Start);
            var end = doc.OffsetAt(region.End);
            if (end > start) fresh.Add(new TextSegment { StartOffset = start, EndOffset = end });
        }
        if (fresh.Count == 0 && _segments.Count == 0) return;
        _segments = fresh;
        _v.TextView.Redraw();
    }

    private void Sync(IReadOnlyList<IInactiveRegionsProvider> providers)
    {
        foreach (var gone in _subs.Keys.Where(p => !providers.Contains(p)).ToList())
        {
            _subs[gone].Dispose();
            _subs.Remove(gone);
        }
        foreach (var p in providers)
            if (!_subs.ContainsKey(p))
                _subs[p] = p.Changed.Subscribe(uri => { if (FeatureSupport.SameFile(uri, _v.Document.Uri)) _v.Post(Schedule); });
    }

    private sealed class Fade(InactiveRegionsFeature owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            var segments = owner._segments;
            if (segments.Count == 0) return;
            foreach (var seg in segments.FindOverlappingSegments(line.Offset, line.Length))
            {
                var from = Math.Max(seg.StartOffset, line.Offset);
                var to = Math.Min(seg.EndOffset, line.EndOffset);
                if (to > from)
                    ChangeLinePart(from, to, e =>
                    {
                        // keep the token's own colour (a keyword stays blue, only fainter), as vscode-clangd's opacity decoration does
                        var color = e.TextRunProperties.ForegroundBrush is ISolidColorBrush b ? b.Color : owner._v.Palette.DefaultForeground;
                        e.TextRunProperties.SetForegroundBrush(new SolidColorBrush(color, Opacity));
                    });
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _d.Dispose();
        _segments = new TextSegmentCollection<TextSegment>(); // drop the anchors
    }
}
