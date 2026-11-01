using System.Globalization;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using CsCode.Core.Lifecycle;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// GitLens-style inline blame: after the end of the caret's line, in a dimmed italic, who changed the line last and when (<c>Jane Doe, 3 weeks
/// ago • Fix the parser</c>), from <see cref="ILineBlameProvider"/>. Off by default (<c>git.blame.editorDecoration.enabled</c>). Asked for when
/// the caret has rested on a line for a moment (never per keystroke), with the editor's text when it has unsaved changes; the last answers
/// are cached per document version. Only for file documents and only while the provider says it is on.
/// </summary>
internal sealed class InlineBlameFeature : IThemeAware, IDisposable
{
    internal static TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(350);

    private readonly CodeEditorControl _v;
    private readonly ILineBlameProvider _provider;
    private readonly DisposableStore _d = new();
    private readonly Renderer _renderer;
    private readonly Dictionary<(int Line, int Version), LineBlame?> _cache = new();
    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public InlineBlameFeature(CodeEditorControl view, ILineBlameProvider provider)
    {
        _v = view;
        _provider = provider;
        _renderer = new Renderer(this);
        view.TextView.BackgroundRenderers.Add(_renderer);
        _d.Add(Disposable.Create(() => view.TextView.BackgroundRenderers.Remove(_renderer)));
        _d.Add(view.Controller.CaretChanged.Subscribe(_ => OnCaretMoved()));
        _d.Add(view.Document.Changed.Subscribe(_ => { _cache.Clear(); Clear(); }));
        _d.Add(provider.Changed.Subscribe(_ => view.Post(() => { _cache.Clear(); Clear(); OnCaretMoved(); })));
        OnCaretMoved();
    }

    /// <summary>The annotation on screen and its line (tests).</summary>
    internal (int Line, LineBlame Blame)? Current { get; private set; }

    /// <summary>Completes when the request in flight has been answered (tests).</summary>
    internal Task Ready { get; private set; } = Task.CompletedTask;

    public void OnThemeChanged() => _v.TextView.InvalidateLayer(KnownLayer.Background);

    private void Clear()
    {
        if (Current is null) return;
        Current = null;
        _v.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private void OnCaretMoved()
    {
        if (_disposed) return;
        var line = _v.TextArea.Caret.Line - 1;
        if (Current is { } c && c.Line != line) Clear();
        if (!_provider.IsEnabled || !_v.Document.Uri.IsFile) { Clear(); return; }
        _timer ??= new System.Threading.Timer(_ => _v.Post(() => Ready = RequestAsync()));
        _timer.Change(Delay, Timeout.InfiniteTimeSpan);
    }

    private async Task RequestAsync()
    {
        if (_disposed || !_provider.IsEnabled) return;
        var line = _v.TextArea.Caret.Line - 1;
        var version = _v.Document.Version;
        if (_cache.TryGetValue((line, version), out var cached)) { Show(line, cached); return; }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var text = _v.Document.IsDirty ? _v.Document.GetText() : null;
        try
        {
            var blame = await _provider.GetLineBlameAsync(_v.Document.Uri, line, text, cts.Token).ConfigureAwait(false);
            await _v.Ui.InvokeAsync(() =>
            {
                if (_disposed || cts.IsCancellationRequested) return;
                if (_cache.Count > 256) _cache.Clear();
                _cache[(line, version)] = blame;
                if (_v.TextArea.Caret.Line - 1 == line && _v.Document.Version == version) Show(line, blame);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
    }

    private void Show(int line, LineBlame? blame)
    {
        Current = blame is null ? null : (line, blame);
        _v.TextView.InvalidateLayer(KnownLayer.Background);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _timer?.Dispose();
        _d.Dispose();
    }

    private sealed class Renderer(InlineBlameFeature owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext context)
        {
            if (owner.Current is not { } current || textView.Document is null || !textView.VisualLinesValid) return;
            if (current.Line < 0 || current.Line >= textView.Document.LineCount) return;
            var docLine = textView.Document.GetLineByNumber(current.Line + 1);
            var visual = textView.GetVisualLine(docLine.LineNumber);
            if (visual is null) return;
            var endPos = visual.GetVisualPosition(visual.VisualLengthWithEndOfLineMarker, VisualYPosition.LineTop);
            var x = endPos.X - textView.ScrollOffset.X + textView.WideSpaceWidth * 3;
            var y = endPos.Y - textView.ScrollOffset.Y;
            var p = owner._v.Palette;
            var ft = new FormattedText(current.Blame.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(owner._v.Editor.FontFamily, FontStyle.Italic), owner._v.Editor.FontSize, p.CodeLens);
            context.DrawText(ft, new Point(x, y + (textView.DefaultLineHeight - ft.Height) / 2));
        }
    }
}
