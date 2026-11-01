using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using CsCode.Core.Theming;
using CsCode.Editor.View;

namespace CsCode.Editor.Diff;

/// <summary>The diff colours of the current theme (VS Code's <c>diffEditor.*</c> keys).</summary>
internal sealed class DiffBrushes
{
    public required IBrush InsertedLine { get; init; }
    public required IBrush RemovedLine { get; init; }
    public required IBrush InsertedText { get; init; }
    public required IBrush RemovedText { get; init; }
    public required IPen InsertedMarker { get; init; }
    public required IPen RemovedMarker { get; init; }
    public required IBrush LineNumber { get; init; }

    public static DiffBrushes From(IColorThemeService themes)
    {
        IBrush B(string key) => new ImmutableSolidColorBrush(EditorPalette.ToColor(themes.GetColor(key)));
        IPen P(string key) => new ImmutablePen(new ImmutableSolidColorBrush(EditorPalette.ToColor(themes.GetColor(key))), 1);
        return new DiffBrushes
        {
            InsertedLine = B("diffEditor.insertedLineBackground"),
            RemovedLine = B("diffEditor.removedLineBackground"),
            InsertedText = B("diffEditor.insertedTextBackground"),
            RemovedText = B("diffEditor.removedTextBackground"),
            InsertedMarker = P("editorGutter.addedBackground"),
            RemovedMarker = P("editorGutter.deletedBackground"),
            LineNumber = B("editorLineNumber.foreground"),
        };
    }
}

/// <summary>
/// Paints one pane of a diff: changed lines get the line background, changed characters inside them the stronger text background, and the place
/// where the other side has lines this one lacks gets a thin marker line (VS Code draws a hatched gap there).
/// </summary>
internal sealed class DiffBackgroundRenderer : IBackgroundRenderer
{
    public DiffLineKind[]? Kinds { get; set; }
    public Dictionary<int, List<LineSpan>>? Spans { get; set; }
    /// <summary>0-based lines ABOVE which the other side has extra lines (a line index equal to the line count: below the last line).</summary>
    public HashSet<int>? Gaps { get; set; }
    /// <summary>The kind of change the gaps stand for on the other side (decides the marker colour).</summary>
    public DiffLineKind GapKind { get; set; }
    public DiffBrushes? Brushes { get; set; }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext context)
    {
        if (Brushes is not { } brushes || textView.Document is not { } doc || !textView.VisualLinesValid) return;
        var width = textView.Bounds.Width + textView.ScrollOffset.X;
        var scroll = textView.ScrollOffset;
        foreach (var vl in textView.VisualLines)
        {
            var line = vl.FirstDocumentLine.LineNumber - 1;
            var top = vl.VisualTop - scroll.Y;
            var kind = Kinds is { } k && line < k.Length ? k[line] : DiffLineKind.None;
            if (kind != DiffLineKind.None)
                context.FillRectangle(kind == DiffLineKind.Inserted ? brushes.InsertedLine : brushes.RemovedLine, new Rect(0, top, width, vl.Height));
            if (Spans is { } spans && spans.TryGetValue(line, out var list))
            {
                var docLine = vl.FirstDocumentLine;
                var brush = kind == DiffLineKind.Removed ? brushes.RemovedText : brushes.InsertedText;
                foreach (var s in list)
                {
                    var start = Math.Min(docLine.Offset + s.Start, docLine.EndOffset);
                    var len = Math.Min(s.Length, docLine.EndOffset - start);
                    if (len <= 0) continue;
                    foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = start, Length = len }))
                        context.FillRectangle(brush, r);
                }
            }
            if (Gaps is { } gaps)
            {
                var pen = GapKind == DiffLineKind.Inserted ? brushes.InsertedMarker : brushes.RemovedMarker;
                if (gaps.Contains(line)) context.DrawLine(pen, new Point(0, top + 0.5), new Point(width, top + 0.5));
                if (line == doc.LineCount - 1 && gaps.Contains(doc.LineCount)) context.DrawLine(pen, new Point(0, top + vl.Height - 0.5), new Point(width, top + vl.Height - 0.5));
            }
        }
    }
}

/// <summary>The inline diff's line numbers: two columns, the original and the modified number of each line (blank where the line does not exist on that side), plus +/-.</summary>
internal sealed class DualLineNumberMargin : AbstractMargin
{
    public UnifiedDiff? Diff { get; set; }
    public DiffBrushes? Brushes { get; set; }
    public Typeface Typeface { get; set; } = new("Menlo, Consolas, monospace");
    public double FontSize { get; set; } = 12;

    private double DigitWidth => FontSize * 0.62;

    private int Digits => Math.Max(3, (Diff?.Kinds.Length ?? 1).ToString(CultureInfo.InvariantCulture).Length);

    protected override Size MeasureOverride(Size availableSize) => new(DigitWidth * (Digits * 2 + 4), 0);

    public override void Render(DrawingContext context)
    {
        var tv = TextView;
        if (tv is null || !tv.VisualLinesValid || Diff is not { } diff || Brushes is not { } brushes) return;
        var digits = Digits;
        foreach (var vl in tv.VisualLines)
        {
            var line = vl.FirstDocumentLine.LineNumber - 1;
            if (line >= diff.Kinds.Length) continue;
            var y = vl.VisualTop - tv.ScrollOffset.Y;
            var o = diff.OriginalNumbers[line]?.ToString(CultureInfo.InvariantCulture) ?? "";
            var m = diff.ModifiedNumbers[line]?.ToString(CultureInfo.InvariantCulture) ?? "";
            var sign = diff.Kinds[line] switch { DiffLineKind.Removed => "-", DiffLineKind.Inserted => "+", _ => " " };
            var text = o.PadLeft(digits) + " " + m.PadLeft(digits) + " " + sign;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, FontSize, brushes.LineNumber);
            context.DrawText(ft, new Point(2, y + (vl.Height - ft.Height) / 2));
        }
    }

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null) { oldTextView.VisualLinesChanged -= OnVisualLinesChanged; oldTextView.ScrollOffsetChanged -= OnVisualLinesChanged; }
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null) { newTextView.VisualLinesChanged += OnVisualLinesChanged; newTextView.ScrollOffsetChanged += OnVisualLinesChanged; }
        InvalidateVisual();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();
}
