using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Theming;
using CsCode.Editor.Threading;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// Occurrence highlighting (<c>editor.occurrencesHighlight</c>): the other uses of the symbol under the caret get the word-highlight colours (writes
/// the stronger one). Uses <see cref="IDocumentHighlightProvider"/> when one is registered; otherwise the same text-based fallback VS Code uses —
/// every whole-word occurrence of the word under the caret outside strings/comments' special treatment — computed on the editor worker from a snapshot.
/// Requested 250 ms after the caret settles; cleared immediately on any edit (offsets of a stale answer must never be drawn); capped at
/// <see cref="MaxHighlights"/>.
/// </summary>
internal sealed class HighlightFeature : IThemeAware, ISettingsAware, IDisposable
{
    public const int MaxHighlights = 2000;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(250);

    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private readonly Renderer _renderer;
    private readonly object _key = new();
    private CancellationTokenSource? _debounce, _request;
    private List<(int Start, int End, bool Strong)> _ranges = [];
    private bool _disposed;

    public HighlightFeature(CodeEditorControl view)
    {
        _v = view;
        _renderer = new Renderer(this);
        view.TextView.BackgroundRenderers.Add(_renderer);
        _d.Add(view.Controller.CaretChanged.Subscribe(_ => Schedule()));
        _d.Add(view.Document.Changed.Subscribe(_ =>
        {
            Clear();
            Schedule();
        }));
        Schedule();
    }

    /// <summary>Highlighted ranges (offsets) currently applied.</summary>
    internal IReadOnlyList<(int Start, int End, bool Strong)> Ranges => _ranges;

    public void OnThemeChanged() => _v.TextView.InvalidateLayer(KnownLayer.Selection);

    public void OnSettingsChanged()
    {
        if (!_v.Settings.OccurrencesHighlight) Clear();
        else Schedule();
    }

    private void Schedule()
    {
        if (_disposed) return;
        _request?.Cancel();
        _debounce?.Cancel();
        _debounce?.Dispose();
        if (!_v.Settings.OccurrencesHighlight) return;
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(Delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _v.Post(() =>
            {
                if (!cts.IsCancellationRequested) _ = RefreshAsync();
            });
        }, TaskScheduler.Default);
    }

    private void Clear()
    {
        if (_ranges.Count == 0) return;
        _ranges = [];
        _v.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private async Task RefreshAsync()
    {
        if (_disposed || _v.Editor.SelectionLength > 0)
        {
            Clear();
            return;
        }
        _request?.Cancel();
        _request?.Dispose();
        var cts = _request = new CancellationTokenSource();
        var doc = _v.Document;
        var version = doc.Version;
        var caret = _v.Editor.CaretOffset;
        var word = FeatureSupport.WordAt(_v.Editor.Document, caret);
        if (word.End <= word.Start || char.IsDigit(_v.Editor.Document.GetCharAt(word.Start)))
        {
            Clear();
            return;
        }

        List<(int Start, int End, bool Strong)>? result = null;
        var providers = _v.Services.Features.GetProviders<IDocumentHighlightProvider>(doc);
        if (providers.Count > 0)
        {
            var position = doc.PositionAt(caret);
            foreach (var call in providers.Select(p => FeatureSupport.CallAsync(_v, c => p.ProvideDocumentHighlightsAsync(doc, position, c), cts.Token, TimeSpan.FromSeconds(3))).ToList())
            {
                if (await call.ConfigureAwait(true) is { Count: > 0 } list)
                {
                    result = list.Take(MaxHighlights).Select(h => (doc.OffsetAt(h.Range.Start), doc.OffsetAt(h.Range.End), h.Kind == DocumentHighlightKind.Write)).ToList();
                    break;
                }
            }
        }
        else
        {
            var snapshot = doc.Snapshot();
            var text = _v.Editor.Document.GetText(word.Start, word.End - word.Start);
            result = await _v.Factory.Worker.RunAsync(WorkPriority.Background, ct => FindWords(snapshot, text, ct), cts.Token, _key).ConfigureAwait(true);
        }
        if (_disposed || cts.IsCancellationRequested || doc.Version != version) return; // stale: the text or the caret moved on
        if (result is null || result.Count <= 1 && providers.Count == 0)
        {
            Clear();
            return;
        }
        _ranges = result;
        _v.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    /// <summary>Whole-word occurrences of <paramref name="word"/> (case-sensitive), line by line from a snapshot; stops at <see cref="MaxHighlights"/>.</summary>
    internal static List<(int Start, int End, bool Strong)> FindWords(DocumentSnapshot snapshot, string word, CancellationToken ct)
    {
        var found = new List<(int, int, bool)>();
        for (var line = 0; line < snapshot.LineCount && found.Count < MaxHighlights; line++)
        {
            if ((line & 255) == 0) ct.ThrowIfCancellationRequested();
            var text = snapshot.GetLineText(line);
            var from = 0;
            while (from <= text.Length - word.Length)
            {
                var at = text.IndexOf(word, from, StringComparison.Ordinal);
                if (at < 0) break;
                var before = at > 0 && FeatureSupport.IsWordChar(text[at - 1]);
                var after = at + word.Length < text.Length && FeatureSupport.IsWordChar(text[at + word.Length]);
                if (!before && !after)
                {
                    var start = snapshot.OffsetAt(new TextPosition(line, at));
                    found.Add((start, start + word.Length, false));
                    if (found.Count >= MaxHighlights) break;
                }
                from = at + word.Length;
            }
        }
        return found;
    }

    private sealed class Renderer(HighlightFeature owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext context)
        {
            var ranges = owner._ranges;
            // a view with no height yet (a new split being laid out) has valid but no visual lines
            if (ranges.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
            var palette = owner._v.Palette;
            var first = textView.VisualLines[0].FirstDocumentLine.Offset;
            var last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
            foreach (var (start, end, strong) in ranges)
            {
                if (end < first || start > last) continue;
                var seg = new TextSegment { StartOffset = start, EndOffset = end };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg))
                    context.FillRectangle(strong ? palette.WordHighlightStrong : palette.WordHighlight, rect);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return; // idempotent: a view may be torn down twice (an explicit dispose, then the control's)
        _disposed = true;
        _debounce?.Cancel();
        _request?.Cancel();
        _d.Dispose();
        _v.TextView.BackgroundRenderers.Remove(_renderer);
    }
}
