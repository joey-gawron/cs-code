using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using CsCode.Core.Lifecycle;

namespace CsCode.Editor.View;

/// <summary>
/// The background decorations of one view that are not language features: the debugger's stop-line highlight and the indentation guides.
/// Both are <see cref="IBackgroundRenderer"/>s that read prepared data only; they are removed from the text view on dispose.
/// </summary>
internal sealed class EditorChromeRenderers : IDisposable, IThemeAware, ISettingsAware
{
    private readonly CodeEditorControl _v;
    private readonly StopLineRenderer _stop;
    private readonly IndentGuideRenderer _guides;

    public EditorChromeRenderers(CodeEditorControl view)
    {
        _v = view;
        _stop = new StopLineRenderer(view);
        _guides = new IndentGuideRenderer(view);
        view.TextView.BackgroundRenderers.Add(_guides);
        view.TextView.BackgroundRenderers.Add(_stop);
        view.Controller.StopLineHandler = line =>
        {
            _stop.Line = line;
            view.TextView.InvalidateLayer(KnownLayer.Background);
            if (line is { } l && l >= 0 && l < view.Document.LineCount) view.Controller.Reveal(Core.Text.TextRange.At(l, 0), center: true);
        };
    }

    public int? StopLine => _stop.Line;

    public void OnThemeChanged() => _v.TextView.InvalidateLayer(KnownLayer.Background);

    public void OnSettingsChanged() => _v.TextView.InvalidateLayer(KnownLayer.Background);

    public void Dispose()
    {
        _v.TextView.BackgroundRenderers.Remove(_stop);
        _v.TextView.BackgroundRenderers.Remove(_guides);
        _v.Controller.StopLineHandler = null;
    }

    private sealed class StopLineRenderer(CodeEditorControl view) : IBackgroundRenderer
    {
        public int? Line { get; set; }
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext context)
        {
            if (Line is not { } line || textView.Document is null || line < 0 || line >= textView.Document.LineCount) return;
            var docLine = textView.Document.GetLineByNumber(line + 1);
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, docLine))
                context.FillRectangle(view.Palette.FocusedStackFrame, new Rect(0, rect.Top, Math.Max(rect.Right, textView.Bounds.Width), rect.Height));
        }
    }

    /// <summary>Vertical guide lines at each indentation level of every visible line (blank lines take the indentation of the nearest non-blank neighbour).</summary>
    private sealed class IndentGuideRenderer(CodeEditorControl view) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext context)
        {
            var settings = view.Settings;
            if (!settings.IndentGuides || textView.Document is null || !textView.VisualLinesValid) return;
            var doc = textView.Document;
            var unit = textView.WideSpaceWidth;
            if (unit <= 0) return;
            var pen = view.Palette.IndentGuide;
            var tab = Math.Max(1, settings.TabSize);
            foreach (var vl in textView.VisualLines)
            {
                var line = vl.FirstDocumentLine;
                var columns = IndentColumns(doc, line, tab);
                if (columns <= 0) continue;
                var top = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.LineTop) - textView.VerticalOffset;
                var bottom = vl.GetTextLineVisualYPosition(vl.TextLines[^1], VisualYPosition.LineBottom) - textView.VerticalOffset;
                // a guide at the start of every complete indentation level: columns 0, tab, 2*tab, … while that level is indented on this line
                for (var level = 0; (level + 1) * tab <= columns; level++)
                {
                    var x = level * tab * unit - textView.HorizontalOffset + 0.5;
                    if (x < 0) continue;
                    context.DrawLine(pen, new Point(x, top), new Point(x, bottom));
                }
            }
        }

        private static int IndentColumns(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Document.DocumentLine line, int tab)
        {
            var width = LeadingWidth(doc, line, tab, out var blank);
            if (!blank) return width;
            // blank line: continue the guides of the nearest non-blank line above/below (max 20 lines away)
            var a = line.PreviousLine;
            var b = line.NextLine;
            for (var i = 0; i < 20 && (a is not null || b is not null); i++)
            {
                if (a is not null)
                {
                    var w = LeadingWidth(doc, a, tab, out var blankA);
                    if (!blankA) return w;
                    a = a.PreviousLine;
                }
                if (b is not null)
                {
                    var w = LeadingWidth(doc, b, tab, out var blankB);
                    if (!blankB) return w;
                    b = b.NextLine;
                }
            }
            return 0;
        }

        private static int LeadingWidth(AvaloniaEdit.Document.TextDocument doc, AvaloniaEdit.Document.DocumentLine line, int tab, out bool blank)
        {
            var width = 0;
            var i = line.Offset;
            var end = line.EndOffset;
            while (i < end)
            {
                var ch = doc.GetCharAt(i);
                if (ch == ' ') width++;
                else if (ch == '\t') width += tab - width % tab;
                else break;
                i++;
            }
            blank = i >= end;
            return width;
        }
    }
}
