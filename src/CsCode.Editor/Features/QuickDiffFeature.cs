using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using CsCode.Core.Lifecycle;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Editor.Threading;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>What a gutter marker stands for (VS Code's dirty diff): lines added, lines modified, or lines deleted between two lines.</summary>
public enum QuickDiffKind { Added, Modified, Deleted }

/// <summary>
/// The editor's change markers (VS Code's "quick diff"): bars in the gutter where the document differs from its source-control original (the
/// index, from <see cref="IQuickDiffProvider"/>): green = added lines, blue = modified, a red wedge = lines deleted there. Recomputed on the
/// editor worker after edits pause and whenever the original moves (stage, commit, checkout). Clicking a marker opens a peek with the original
/// lines and Revert (puts them back in the document, one undo step), Stage (this hunk only, through the provider) and previous / next. Only for
/// file documents, only when a provider is registered, and off with <c>scm.diffDecorations: "none"</c>.
/// </summary>
internal sealed class QuickDiffFeature : IThemeAware, ISettingsAware, IDisposable
{
    public const string Setting = "scm.diffDecorations";
    internal const double MarginWidth = 6;

    private readonly CodeEditorControl _v;
    private readonly IQuickDiffProvider _provider;
    private readonly DisposableStore _d = new();
    private readonly QuickDiffMargin _margin;
    private readonly OverlayPopup _peek;
    private System.Threading.Timer? _debounce;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<DiffChange> _changes = [];
    private string? _original;
    private bool _enabled, _disposed;

    public QuickDiffFeature(CodeEditorControl view, IQuickDiffProvider provider)
    {
        _v = view;
        _provider = provider;
        _margin = new QuickDiffMargin(this);
        _peek = new OverlayPopup(view, 640, 360);
        _d.Add(_peek);
        _enabled = Enabled();
        InsertMargin();
        _d.Add(Disposable.Create(() => view.TextArea.LeftMargins.Remove(_margin)));
        _d.Add(view.Document.Changed.Subscribe(_ => Schedule(TimeSpan.FromMilliseconds(250))));
        _d.Add(provider.OriginalChanged.Subscribe(uri => { if (uri is null || uri == view.Document.Uri) Schedule(TimeSpan.FromMilliseconds(50), reloadOriginal: true); }));
        _d.Add(view.Document.Saved.Subscribe(_ => Schedule(TimeSpan.FromMilliseconds(50))));
        _d.Add(view.Services.Config.Changed.Subscribe(e => { if (e.Affects(Setting)) view.Post(OnSettingsChanged); }));
        Schedule(TimeSpan.Zero, reloadOriginal: true);
    }

    /// <summary>The current changes (document vs original), ordered. UI thread.</summary>
    internal IReadOnlyList<DiffChange> Changes => _changes;

    /// <summary>Completes when the computation in flight has been applied (tests).</summary>
    internal Task Ready { get; private set; } = Task.CompletedTask;

    internal bool IsPeekOpen => _peek.IsOpen;
    internal Control PeekRoot => _peek.Root;
    internal Control Margin => _margin;

    private bool Enabled() => !string.Equals(_v.Services.Config.Get(Setting, "all"), "none", StringComparison.OrdinalIgnoreCase);

    private void InsertMargin()
    {
        var margins = _v.TextArea.LeftMargins;
        margins.Remove(_margin);
        // after the line numbers, before the folding chevrons (where VS Code draws its bars)
        var folding = margins.ToList().FindIndex(m => m is AvaloniaEdit.Folding.FoldingMargin);
        margins.Insert(folding >= 0 ? folding : margins.Count, _margin);
    }

    public void OnThemeChanged() => _margin.InvalidateVisual();

    public void OnSettingsChanged()
    {
        if (_disposed) return;
        InsertMargin(); // the line-number margin is re-inserted when toggled
        var enabled = Enabled();
        if (enabled == _enabled) return;
        _enabled = enabled;
        if (!enabled) { _changes = []; _margin.InvalidateVisual(); _peek.Hide(); _v.NotifyMarkersChanged(); }
        else Schedule(TimeSpan.Zero, reloadOriginal: true);
    }

    private void Schedule(TimeSpan delay, bool reloadOriginal = false)
    {
        if (_disposed) return;
        if (reloadOriginal) _original = null;
        if (delay == TimeSpan.Zero) { _v.Post(() => Ready = RecomputeAsync()); return; }
        _debounce ??= new System.Threading.Timer(_ => _v.Post(() => Ready = RecomputeAsync()));
        _debounce.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private async Task RecomputeAsync()
    {
        if (_disposed || !_enabled) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        var snapshot = _v.Document.Snapshot();
        try
        {
            var original = _original;
            if (original is null)
            {
                var o = await _provider.GetOriginalAsync(_v.Document.Uri, ct).ConfigureAwait(false);
                original = o?.Text;
                if (original is null)
                {
                    _v.Post(() => { if (!ct.IsCancellationRequested) { _changes = []; _margin.InvalidateVisual(); _v.NotifyMarkersChanged(); } });
                    return;
                }
            }
            var text = snapshot.GetText();
            var changes = await _v.Factory.Worker.RunAsync(WorkPriority.Background,
                t => LineDiff.Compute(original, text, new LineDiffOptions(false, TimeSpan.FromMilliseconds(500)), t).Changes, ct, key: this).ConfigureAwait(false);
            await _v.Ui.InvokeAsync(() =>
            {
                if (_disposed || ct.IsCancellationRequested) return;
                _original = original;
                _changes = changes;
                _margin.InvalidateVisual();
                _v.NotifyMarkersChanged(); // the overview ruler and the minimap's gutter
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
    }

    // ---------------------------------------------------------------------------------- markers

    /// <summary>The marker kind of a change.</summary>
    internal static QuickDiffKind KindOf(DiffChange c) => c.OriginalLength == 0 ? QuickDiffKind.Added : c.ModifiedLength == 0 ? QuickDiffKind.Deleted : QuickDiffKind.Modified;

    /// <summary>The change whose marker is on document line <paramref name="line"/> (0-based): a deleted marker sits on the line after the deletion (on its top edge).</summary>
    internal int ChangeAt(int line)
    {
        for (var i = 0; i < _changes.Count; i++)
        {
            var c = _changes[i];
            if (c.ModifiedLength == 0 ? (line == c.ModifiedStart || line == c.ModifiedStart - 1) : (line >= c.ModifiedStart && line < c.ModifiedEnd)) return i;
        }
        return -1;
    }

    // ---------------------------------------------------------------------------------- peek, revert, stage

    /// <summary>Opens the peek of change <paramref name="index"/>: the original lines, and Revert / Stage / previous / next.</summary>
    internal void ShowPeek(int index)
    {
        if (index < 0 || index >= _changes.Count || _original is null) { _peek.Hide(); return; }
        var c = _changes[index];
        var p = _v.Palette;
        var originalLines = LineDiff.SplitLines(_original);
        var removed = new List<string>();
        for (var i = c.OriginalStart; i < c.OriginalEnd && i < originalLines.Length; i++) removed.Add(originalLines[i]);
        var brushes = Diff.DiffBrushes.From(_v.Factory.Themes);

        Button Btn(string text, string tip, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(6, 1), Margin = new Thickness(1), MinHeight = 0, FontSize = 12, Background = Brushes.Transparent, Foreground = p.WidgetForeground };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }
        var title = new TextBlock
        {
            Text = $"{KindOf(c) switch { QuickDiffKind.Added => "Added", QuickDiffKind.Deleted => "Deleted", _ => "Modified" }} lines · change {index + 1} of {_changes.Count}",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), Foreground = p.DescriptionForeground,
        };
        var bar = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Btn("↑", "Previous Change", () => ShowPeek(index == 0 ? _changes.Count - 1 : index - 1)));
        buttons.Children.Add(Btn("↓", "Next Change", () => ShowPeek((index + 1) % _changes.Count)));
        if (!_v.Document.IsReadOnly) buttons.Children.Add(Btn("Revert", "Revert this change", () => Revert(index)));
        buttons.Children.Add(Btn("Stage", "Stage this change", () => _ = StageAsync(index)));
        buttons.Children.Add(Btn("✕", "Close", () => _peek.Hide()));
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(title);

        var lines = new StackPanel();
        var font = _v.Editor.FontFamily;
        var size = _v.Editor.FontSize;
        if (removed.Count == 0) lines.Children.Add(new TextBlock { Text = "(no original lines: these lines were added)", Margin = new Thickness(6, 2), Foreground = p.DescriptionForeground, FontStyle = FontStyle.Italic });
        foreach (var l in removed.Take(200))
            lines.Children.Add(new Border { Background = brushes.RemovedLine, Child = new TextBlock { Text = "- " + l, FontFamily = font, FontSize = size, Foreground = p.Foreground, Margin = new Thickness(6, 0) } });
        if (removed.Count > 200) lines.Children.Add(new TextBlock { Text = $"… {removed.Count - 200} more lines", Margin = new Thickness(6, 2), Foreground = p.DescriptionForeground });
        var content = new DockPanel { LastChildFill = true, MinWidth = 320 };
        DockPanel.SetDock(bar, Dock.Top);
        content.Children.Add(bar);
        content.Children.Add(new ScrollViewer { Content = lines, MaxHeight = 300, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        var anchorLine = Math.Clamp(c.ModifiedLength == 0 ? c.ModifiedStart - 1 : c.ModifiedEnd - 1, 0, Math.Max(0, _v.Document.LineCount - 1));
        _peek.ShowAt(content, new TextPosition(anchorLine, 0));
    }

    /// <summary>Puts the original lines of change <paramref name="index"/> back (one undo step).</summary>
    internal bool Revert(int index)
    {
        if (index < 0 || index >= _changes.Count || _original is null || _v.Document.IsReadOnly) return false;
        var c = _changes[index];
        var original = LineDiff.SplitLines(_original);
        var doc = _v.Document;
        var eol = doc.EndOfLine;
        var replacement = string.Join(eol, original.Skip(c.OriginalStart).Take(c.OriginalLength));
        TextRange range;
        string text;
        if (c.ModifiedLength == 0)
        {
            // re-insert deleted lines before line ModifiedStart (or after the last line)
            if (c.ModifiedStart < doc.LineCount) { range = new TextRange(new TextPosition(c.ModifiedStart, 0), new TextPosition(c.ModifiedStart, 0)); text = replacement + eol; }
            else { var last = doc.LineCount - 1; var end = new TextPosition(last, doc.GetLineText(last).Length); range = new TextRange(end, end); text = eol + replacement; }
        }
        else if (c.OriginalLength == 0)
        {
            // remove added lines, with their line breaks
            var start = new TextPosition(c.ModifiedStart, 0);
            if (c.ModifiedEnd < doc.LineCount) range = new TextRange(start, new TextPosition(c.ModifiedEnd, 0));
            else if (c.ModifiedStart > 0) range = new TextRange(new TextPosition(c.ModifiedStart - 1, doc.GetLineText(c.ModifiedStart - 1).Length), new TextPosition(doc.LineCount - 1, doc.GetLineText(doc.LineCount - 1).Length));
            else range = new TextRange(start, new TextPosition(doc.LineCount - 1, doc.GetLineText(doc.LineCount - 1).Length));
            text = "";
        }
        else
        {
            var lastLine = c.ModifiedEnd - 1;
            range = new TextRange(new TextPosition(c.ModifiedStart, 0), new TextPosition(lastLine, doc.GetLineText(lastLine).Length));
            text = replacement;
        }
        var ok = doc.ApplyEdits([new TextEdit(range, text)]);
        _peek.Hide();
        return ok;
    }

    private async Task StageAsync(int index)
    {
        if (index < 0 || index >= _changes.Count) return;
        var c = _changes[index];
        var start = c.ModifiedLength == 0 ? Math.Max(0, c.ModifiedStart - 1) : c.ModifiedStart;
        var end = c.ModifiedLength == 0 ? start : c.ModifiedEnd - 1;
        _peek.Hide();
        try { await _provider.StageRangeAsync(_v.Document.Uri, start, end).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            if (_v.Services.Window is { } w) await w.ShowMessageAsync(Core.Platform.MessageSeverity.Error, "Could not stage the change: " + ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>Moves the caret to the next (previous) change and returns its index; -1 when there is none.</summary>
    internal int GoToChange(bool forward)
    {
        if (_changes.Count == 0) return -1;
        var line = _v.TextArea.Caret.Line - 1;
        var index = forward ? _changes.ToList().FindIndex(c => c.ModifiedStart > line) : _changes.ToList().FindLastIndex(c => c.ModifiedStart < line);
        if (index < 0) index = forward ? 0 : _changes.Count - 1;
        var target = Math.Clamp(_changes[index].ModifiedStart, 0, Math.Max(0, _v.Document.LineCount - 1));
        _v.Controller.SetSelection(new TextRange(new TextPosition(target, 0), new TextPosition(target, 0)));
        return index;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _debounce?.Dispose();
        _d.Dispose();
    }

    /// <summary>The bars. Click opens the peek of the change under the pointer.</summary>
    private sealed class QuickDiffMargin(QuickDiffFeature owner) : AbstractMargin
    {
        protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null) { oldTextView.VisualLinesChanged -= Invalidate; oldTextView.ScrollOffsetChanged -= Invalidate; }
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null) { newTextView.VisualLinesChanged += Invalidate; newTextView.ScrollOffsetChanged += Invalidate; }
            InvalidateVisual();
        }

        private void Invalidate(object? sender, EventArgs e) => InvalidateVisual();

        public override void Render(DrawingContext context)
        {
            var tv = TextView;
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // hit-testable
            if (tv is null || !tv.VisualLinesValid || owner._changes.Count == 0) return;
            var themes = owner._v.Factory.Themes;
            IBrush B(string key) => new ImmutableSolidColorBrush(EditorPalette.ToColor(themes.GetColor(key)));
            var added = B("editorGutter.addedBackground");
            var modified = B("editorGutter.modifiedBackground");
            var deleted = B("editorGutter.deletedBackground");
            var scroll = tv.ScrollOffset.Y;
            foreach (var vl in tv.VisualLines)
            {
                var line = vl.FirstDocumentLine.LineNumber - 1;
                var top = vl.VisualTop - scroll;
                foreach (var c in owner._changes)
                {
                    if (c.ModifiedLength > 0 && line >= c.ModifiedStart && line < c.ModifiedEnd)
                        context.FillRectangle(c.OriginalLength == 0 ? added : modified, new Rect(1, top, 3, vl.Height));
                    else if (c.ModifiedLength == 0 && line == Math.Min(c.ModifiedStart, tv.Document.LineCount - 1))
                    {
                        // a wedge on the line boundary where lines were deleted
                        var y = c.ModifiedStart < tv.Document.LineCount ? top : top + vl.Height;
                        var geo = new StreamGeometry();
                        using (var g = geo.Open())
                        {
                            g.BeginFigure(new Point(0, y - 4), true);
                            g.LineTo(new Point(5, y));
                            g.LineTo(new Point(0, y + 4));
                            g.EndFigure(true);
                        }
                        context.DrawGeometry(deleted, null, geo);
                    }
                }
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var tv = TextView;
            if (tv is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var y = e.GetPosition(tv).Y + tv.ScrollOffset.Y;
            var line = tv.GetDocumentLineByVisualTop(y);
            if (line is null) return;
            var index = owner.ChangeAt(line.LineNumber - 1);
            if (index < 0) return;
            e.Handled = true;
            if (owner.IsPeekOpen) owner._peek.Hide(); else owner.ShowPeek(index);
        }
    }
}

/// <summary>Formatting helpers shared by quick-diff tests.</summary>
internal static class QuickDiffText
{
    public static string Describe(IReadOnlyList<DiffChange> changes) =>
        string.Join(",", changes.Select(c => string.Create(CultureInfo.InvariantCulture, $"{QuickDiffFeature.KindOf(c)}@{c.ModifiedStart}+{c.ModifiedLength}")));
}
