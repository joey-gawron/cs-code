using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Utils;

namespace CsCode.Editor.View;

/// <summary>
/// AvaloniaEdit's line-number margin, minus the per-frame text shaping. The stock margin re-measures and re-renders after every change of the
/// visual lines (each keystroke), and each time builds a new <c>FormattedText</c> per visible line number, which formats and shapes the digits
/// again when drawn (and once more for the width in <c>MeasureOverride</c>): on a full-height editor that is ~60 shaped lines per keystroke, a
/// fifth of the UI thread's work per key in a profile on macOS. This margin keeps the laid-out number of each line it drew in the last frame
/// (a <see cref="TextLayout"/>: shaped once, drawn from its glyph runs) and the width of the widest number, both dropped when the font, size or
/// brush changes. What it draws, and where, is what the stock margin draws (<c>CachedLineNumberMarginTests</c> compares the pixels); the
/// selection by dragging over the numbers is inherited.
/// </summary>
internal sealed class CachedLineNumberMargin : LineNumberMargin
{
    private Dictionary<int, TextLayout> _layouts = new();
    private Dictionary<int, TextLayout> _next = new();
    private Typeface _typeface;
    private double _emSize;
    private IBrush? _brush;
    private (int Digits, Typeface Typeface, double EmSize) _widthKey;
    private double _width;

    /// <summary>Text layouts built since the margin was created (tests: steady typing builds none).</summary>
    internal int LayoutsBuilt { get; private set; }

    /// <summary>Puts this margin where <paramref name="editor"/>'s stock line-number margin is (after <c>ShowLineNumbers</c> inserted it), bound like it.</summary>
    internal static void ReplaceStock(TextEditor editor)
    {
        var margins = editor.TextArea.LeftMargins;
        for (var i = 0; i < margins.Count; i++)
        {
            if (margins[i].GetType() != typeof(LineNumberMargin)) continue;
            var ours = new CachedLineNumberMargin();
            ours.Bind(TemplatedControl.ForegroundProperty, editor.GetBindingObservable(TextEditor.LineNumbersForegroundProperty));
            margins[i] = ours;
            return;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Typeface = this.CreateTypeface();
        EmSize = GetValue(TextBlock.FontSizeProperty);
        var key = (MaxLineNumberLength, Typeface, EmSize);
        if (key != _widthKey)
        {
            _widthKey = key;
            using var widest = Layout(new string('9', MaxLineNumberLength), GetValue(TextBlock.ForegroundProperty));
            _width = widest.Width;
        }
        return new Size(_width, 0);
    }

    public override void Render(DrawingContext drawingContext)
    {
        var textView = TextView;
        if (textView is null || !textView.VisualLinesValid) return;
        var brush = GetValue(TextBlock.ForegroundProperty);
        if (Typeface != _typeface || EmSize != _emSize || !ReferenceEquals(brush, _brush))
        {
            Clear(_layouts);
            (_typeface, _emSize, _brush) = (Typeface, EmSize, brush);
        }
        var width = Bounds.Width;
        foreach (var line in textView.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            if (_layouts.Remove(number, out var layout)) { }
            else if (!_next.TryGetValue(number, out layout))
            {
                layout = Layout(number.ToString(CultureInfo.CurrentCulture), brush);
                LayoutsBuilt++;
            }
            _next[number] = layout;
            var y = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop);
            layout.Draw(drawingContext, new Point(width - layout.Width, y - textView.VerticalOffset));
        }
        Clear(_layouts); // numbers no longer visible
        (_layouts, _next) = (_next, _layouts);
    }

    private TextLayout Layout(string text, IBrush? brush) => new(text, Typeface, EmSize, brush);

    private static void Clear(Dictionary<int, TextLayout> layouts)
    {
        foreach (var layout in layouts.Values) layout.Dispose();
        layouts.Clear();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Clear(_layouts);
    }
}
