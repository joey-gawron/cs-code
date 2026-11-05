using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// The ranges <see cref="IMatchHighlightProvider"/>s report (the search editor's matches) painted with <c>searchEditor.findMatchBackground</c>,
/// whatever the caret does. Asked off the UI thread (<see cref="FeatureSupport.CallAsync{T}"/>) once the text has been still for
/// <see cref="Delay"/>, merged and sorted there, at most <see cref="MaxRanges"/>; an answer for older text is dropped. Between answers the ranges
/// move with the edits (lines added above them shift them; a range an edit touches goes), so results streaming in do not flicker. Only the visible
/// lines are drawn (a binary search into the sorted ranges). A document no provider serves costs one flag test per edit.
/// </summary>
internal sealed class MatchHighlightFeature : IThemeAware, IDisposable
{
    internal const int MaxRanges = 50_000;
    internal static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(80);

    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private readonly Dictionary<IMatchHighlightProvider, IDisposable> _subs = [];
    private readonly Renderer _renderer;
    private TextRange[] _ranges = [];
    private CancellationTokenSource? _pending;
    private bool _hasProviders, _disposed;

    public MatchHighlightFeature(CodeEditorControl view)
    {
        _v = view;
        _renderer = new Renderer(this);
        view.TextView.BackgroundRenderers.Add(_renderer);
        _d.Add(Disposable.Create(() => view.TextView.BackgroundRenderers.Remove(_renderer)));
        _d.Add(view.Services.Features.ProvidersChanged.Subscribe(t => { if (t == typeof(IMatchHighlightProvider)) _v.Post(Refresh); }));
        _d.Add(view.Document.LanguageIdChanged.Subscribe(_ => _v.Post(Refresh)));
        _d.Add(view.Document.Changed.Subscribe(OnChanged));
        _d.Add(Disposable.Create(() => { foreach (var s in _subs.Values) s.Dispose(); _subs.Clear(); }));
        Refresh();
    }

    /// <summary>The highlighted ranges now, sorted (tests).</summary>
    internal IReadOnlyList<TextRange> Ranges => _ranges;

    public void OnThemeChanged() => _v.TextView.InvalidateLayer(KnownLayer.Selection);

    /// <summary>The providers changed (or the language did): subscribe to theirs and ask them (UI thread).</summary>
    private void Refresh()
    {
        if (_disposed) return;
        var providers = _v.Services.Features.GetProviders<IMatchHighlightProvider>(_v.Document);
        foreach (var gone in _subs.Keys.Where(p => !providers.Contains(p)).ToList())
        {
            _subs[gone].Dispose();
            _subs.Remove(gone);
        }
        foreach (var p in providers)
            if (!_subs.ContainsKey(p))
                _subs[p] = p.Changed.Subscribe(uri => { if (FeatureSupport.SameFile(uri, _v.Document.Uri)) _v.Post(Schedule); });
        _hasProviders = providers.Count > 0;
        if (!_hasProviders)
        {
            _pending?.Cancel();
            if (_ranges.Length > 0) Set([]);
            return;
        }
        Schedule();
    }

    private void OnChanged(TextDocumentChangedEventArgs e)
    {
        if (_disposed || (!_hasProviders && _ranges.Length == 0)) return;
        if (_ranges.Length > 0)
        {
            var shifted = _ranges;
            foreach (var c in e.Changes) shifted = Shift(shifted, c);
            if (!ReferenceEquals(shifted, _ranges)) Set(shifted);
        }
        if (_hasProviders) Schedule();
    }

    /// <summary>Asks the providers again once the text has been still for <see cref="Delay"/> (latest wins).</summary>
    private void Schedule()
    {
        if (_disposed) return;
        _pending?.Cancel();
        _pending?.Dispose();
        var cts = _pending = new CancellationTokenSource();
        _ = Task.Delay(Delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _v.Post(() => { if (!cts.IsCancellationRequested) _ = RequestAsync(cts.Token); });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RequestAsync(CancellationToken ct)
    {
        var doc = _v.Document;
        var version = doc.Version;
        var providers = _v.Services.Features.GetProviders<IMatchHighlightProvider>(doc);
        if (providers.Count == 0) return;
        // merging, sorting and capping happen on the worker too: the UI thread only swaps the array in
        var ranges = await FeatureSupport.CallAsync<TextRange[]>(_v, async c =>
        {
            var all = new List<TextRange>();
            foreach (var p in providers)
                if (await p.ProvideMatchHighlightsAsync(doc, c).ConfigureAwait(false) is { } r) all.AddRange(r);
            all.RemoveAll(r => r.End <= r.Start);
            all.Sort((a, b) => a.Start.CompareTo(b.Start));
            if (all.Count > MaxRanges) all.RemoveRange(MaxRanges, all.Count - MaxRanges);
            return all.ToArray();
        }, ct, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        if (_disposed || ct.IsCancellationRequested || ranges is null || doc.Version != version) return;
        Set(ranges);
    }

    private void Set(TextRange[] ranges)
    {
        _ranges = ranges;
        _v.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    /// <summary>
    /// The ranges after one change (expressed against the text before it): ranges before it stay, ranges after it move (by the lines it added or
    /// removed, and along the line it ends on), ranges it touches go. Returns the same array when nothing moved.
    /// </summary>
    internal static TextRange[] Shift(TextRange[] ranges, TextContentChange c)
    {
        var breaks = 0;
        var lastBreak = -1;
        for (var i = 0; i < c.Text.Length; i++)
        {
            if (c.Text[i] == '\n' || (c.Text[i] == '\r' && (i + 1 >= c.Text.Length || c.Text[i + 1] != '\n')))
            {
                breaks++;
                lastBreak = i;
            }
        }
        var lineDelta = breaks - (c.Range.End.Line - c.Range.Start.Line);
        // where the text after the change's end lands, on the line the change ends on
        var endCol = breaks == 0 ? c.Range.Start.Character + c.Text.Length : c.Text.Length - lastBreak - 1;

        TextPosition Move(TextPosition p) => p.Line == c.Range.End.Line
            ? new TextPosition(p.Line + lineDelta, endCol + (p.Character - c.Range.End.Character))
            : new TextPosition(p.Line + lineDelta, p.Character);

        List<TextRange>? result = null;
        for (var i = 0; i < ranges.Length; i++)
        {
            var r = ranges[i];
            var keep = true;
            TextRange next;
            if (r.End <= c.Range.Start) next = r;                                                        // before the change
            else if (r.Start >= c.Range.End)                                                           // after it
                next = lineDelta == 0 && r.Start.Line > c.Range.End.Line ? r : new TextRange(Move(r.Start), Move(r.End));
            else { keep = false; next = default; }                                                     // touched: it goes
            if (result is null)
            {
                if (keep && next == r) continue; // nothing changed so far (the common keystroke: no allocation)
                result = new List<TextRange>(ranges.Length);
                for (var j = 0; j < i; j++) result.Add(ranges[j]);
            }
            if (keep) result.Add(next);
        }
        return result is null ? ranges : [.. result];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
        _ranges = [];
        _d.Dispose();
    }

    /// <summary>Paints the ranges on the visible lines with the search editor's find-match colour.</summary>
    private sealed class Renderer(MatchHighlightFeature owner) : IBackgroundRenderer
    {
        private const int MaxDrawn = 5_000;

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext context)
        {
            var ranges = owner._ranges;
            if (ranges.Length == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
            var doc = textView.Document;
            var first = textView.VisualLines[0].FirstDocumentLine.LineNumber - 1;
            var last = textView.VisualLines[^1].LastDocumentLine.LineNumber - 1;
            // the first range that starts on a visible line, then back over the few that start above and reach into it
            int lo = 0, hi = ranges.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (ranges[mid].Start.Line < first) lo = mid + 1;
                else hi = mid;
            }
            for (var back = 0; back < 16 && lo > 0 && ranges[lo - 1].End.Line >= first; back++) lo--;
            var brush = owner._v.Palette.SearchEditorFindMatch;
            var drawn = 0;
            for (var i = lo; i < ranges.Length && ranges[i].Start.Line <= last && drawn < MaxDrawn; i++)
            {
                var r = ranges[i];
                if (r.End.Line >= doc.LineCount) break; // ranges for text that is gone (an answer is on its way)
                var start = Offset(doc, r.Start);
                var end = Offset(doc, r.End);
                if (end <= start) continue;
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = start, EndOffset = end }))
                    context.FillRectangle(brush, rect);
                drawn++;
            }
        }

        private static int Offset(TextDocument doc, TextPosition p)
        {
            var line = doc.GetLineByNumber(p.Line + 1);
            return line.Offset + Math.Clamp(p.Character, 0, line.Length);
        }
    }
}
