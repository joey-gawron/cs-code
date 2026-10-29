using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// Inlay hints (parameter names, inferred types) shown inline in the text, dimmed, from <see cref="IInlayHintsProvider"/>. Requested for the visible
/// lines (plus a margin) 400 ms after scrolling or typing settles, off the UI thread, latest-wins; at most <see cref="MaxHints"/> are kept. Each hint
/// is anchored to its text position, so it follows edits until the next answer replaces the set; a hint that cannot be placed (its position is past the
/// end of the document) is dropped. Setting <c>editor.inlayHints.enabled</c> = off removes them.
/// </summary>
internal sealed class InlayHintFeature : ISettingsAware, IThemeAware, IDisposable
{
    public const int MaxHints = 1000;
    private const int Margin = 30;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);

    private sealed record Placed(TextAnchor Anchor, InlayHint Hint);

    private readonly CodeEditorControl _v;
    private readonly Generator _generator;
    private readonly DisposableStore _d = new();
    private CancellationTokenSource? _debounce, _request;
    private Placed[] _hints = [];
    private bool _disposed;

    public InlayHintFeature(CodeEditorControl view)
    {
        _v = view;
        _generator = new Generator(this);
        view.TextView.ElementGenerators.Add(_generator);
        view.TextView.ScrollOffsetChanged += OnScrolled;
        _d.Add(view.Document.Changed.Subscribe(_ => Schedule()));
        _d.Add(view.Services.Features.ProvidersChanged.Subscribe(t =>
        {
            if (t == typeof(IInlayHintsProvider)) _v.Post(Schedule);
        }));
        _d.Add(Disposable.Create(() => view.TextView.ScrollOffsetChanged -= OnScrolled));
        Schedule();
    }

    /// <summary>Hints currently placed: (offset, label) in document order.</summary>
    internal IReadOnlyList<(int Offset, string Label)> Placements => _hints.Where(p => !p.Anchor.IsDeleted).Select(p => (p.Anchor.Offset, p.Hint.Label)).OrderBy(x => x.Offset).ToList();

    public void OnThemeChanged() => _v.TextView.Redraw();

    public void OnSettingsChanged()
    {
        if (!_v.Settings.InlayHints) Clear();
        else Schedule();
    }

    private void OnScrolled(object? sender, EventArgs e) => Schedule();

    private void Clear()
    {
        if (_hints.Length == 0) return;
        _hints = [];
        _v.TextView.Redraw();
    }

    private void Schedule()
    {
        if (_disposed) return;
        _debounce?.Cancel();
        _debounce?.Dispose();
        if (!_v.Settings.InlayHints) return;
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(Delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _v.Post(() =>
            {
                if (!cts.IsCancellationRequested) _ = RefreshAsync();
            });
        }, TaskScheduler.Default);
    }

    private async Task RefreshAsync()
    {
        if (_disposed) return;
        var doc = _v.Document;
        var providers = _v.Services.Features.GetProviders<IInlayHintsProvider>(doc);
        if (providers.Count == 0 || !_v.TextView.VisualLinesValid || _v.TextView.VisualLines.Count == 0)
        {
            Clear();
            return;
        }
        _request?.Cancel();
        _request?.Dispose();
        var cts = _request = new CancellationTokenSource();
        var version = doc.Version;
        var first = Math.Max(0, _v.TextView.VisualLines[0].FirstDocumentLine.LineNumber - 1 - Margin);
        var last = Math.Min(doc.LineCount - 1, _v.TextView.VisualLines[^1].LastDocumentLine.LineNumber - 1 + Margin);
        var range = new TextRange(new TextPosition(first, 0), new TextPosition(last, doc.GetLineText(last).Length));

        var all = new List<InlayHint>();
        foreach (var call in providers.Select(p => FeatureSupport.CallAsync(_v, c => p.ProvideInlayHintsAsync(doc, range, c), cts.Token, TimeSpan.FromSeconds(3))).ToList())
            if (await call.ConfigureAwait(true) is { } list) all.AddRange(list);
        if (_disposed || cts.IsCancellationRequested || doc.Version != version) return; // text moved on: a newer request is already scheduled

        var placed = new List<Placed>();
        foreach (var hint in all.Take(MaxHints))
        {
            var p = doc.Validate(hint.Position);
            if (p != hint.Position) continue;
            var anchor = doc.Buffer.CreateAnchor(doc.OffsetAt(p));
            anchor.MovementType = AnchorMovementType.AfterInsertion;
            placed.Add(new Placed(anchor, hint));
        }
        _hints = placed.OrderBy(h => h.Anchor.Offset).ToArray();
        _v.TextView.Redraw();
    }

    private sealed class Generator(InlayHintFeature owner) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            var hints = owner._hints;
            if (hints.Length == 0) return -1;
            var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
            foreach (var h in hints)
            {
                if (h.Anchor.IsDeleted) continue;
                var offset = h.Anchor.Offset;
                if (offset >= startOffset) return offset <= end ? offset : -1;
            }
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var hint = owner._hints.FirstOrDefault(h => !h.Anchor.IsDeleted && h.Anchor.Offset == offset)?.Hint;
            if (hint is null) return null;
            var p = owner._v.Palette;
            var text = new TextBlock
            {
                Text = hint.Label,
                FontSize = Math.Max(9, owner._v.Editor.FontSize - 2),
                FontFamily = owner._v.Editor.FontFamily,
                Foreground = p.InlayForeground,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var border = new Border
            {
                Child = text,
                Background = p.InlayBackground,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0),
                Margin = new Thickness(hint.PaddingLeft ? 4 : 0, 0, hint.PaddingRight ? 4 : 0, 0),
                IsHitTestVisible = false,
            };
            return new InlineObjectElement(0, border);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce?.Cancel();
        _request?.Cancel();
        _d.Dispose();
        _v.TextView.ElementGenerators.Remove(_generator);
        _hints = [];
    }
}
