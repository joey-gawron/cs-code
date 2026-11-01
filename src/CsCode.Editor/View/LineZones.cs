using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Lifecycle;
using LogicalDirection = AvaloniaEdit.Document.LogicalDirection;

namespace CsCode.Editor.View;

/// <summary>What owns the space a <see cref="LineZones"/> opens above document lines (code lens).</summary>
internal interface ILineZoneSource
{
    /// <summary>Height of the zone above <paramref name="line"/> (1-based), 0 for none. Asked while a visual line is built (UI thread).</summary>
    double HeightAbove(int line);

    /// <summary>Draws the zone above <paramref name="line"/>; <paramref name="zone"/> is in text-view coordinates (already scrolled).</summary>
    void Render(DrawingContext context, VisualLine visualLine, int line, Rect zone);

    /// <summary>The pointer is at <paramref name="point"/> inside the zone above <paramref name="line"/>: whether it is over something clickable.</summary>
    bool IsClickable(int line, Rect zone, Point point);

    /// <summary>The zone above <paramref name="line"/> was clicked at <paramref name="point"/> (left button).</summary>
    void Click(int line, Rect zone, Point point);

    /// <summary>The pointer moved over <paramref name="point"/> of the zone above <paramref name="line"/>, or left every zone (null).</summary>
    void Hover(int line, Point? point);
}

/// <summary>
/// Space above document lines that is not text: VS Code's "view zones", here for code lens. AvaloniaEdit has no such thing, so a zone is made
/// of two parts that keep caret, selection, hit-testing, folding and the gutters right:
/// <list type="bullet">
/// <item>a zero-width, zero-length <see cref="Spacer"/> element at the start of the line whose run is as tall as the zone plus the text's
/// ascent and sits on the text's baseline, so the line's first text row grows UPWARDS by the zone's height and the text keeps its own
/// metrics: line numbers, folding markers and the caret's text position follow the baseline; the element has no caret stop, so Left at the
/// line start goes to the previous line and Home stops at the text;</item>
/// <item>a layer above the caret (<see cref="ZoneLayer"/>) that paints each zone with the editor background and the source's content: the row
/// AvaloniaEdit makes tall would otherwise show the selection, the current-line highlight and the caret over the zone. The layer is hit-testable
/// only over the zones, so clicks there go to the source and never move the caret.</item>
/// </list>
/// Lines inside a folded region show no zone (their visual line starts elsewhere); an empty line cannot carry one (AvaloniaEdit asks no element
/// generator about an empty line). Disposing removes the generator and the layer.
/// </summary>
internal sealed class LineZones : IDisposable
{
    private readonly CodeEditorControl _v;
    private readonly ILineZoneSource _source;
    private readonly Generator _generator;
    private readonly ZoneLayer _layer;
    private readonly DisposableStore _d = new();

    public LineZones(CodeEditorControl view, ILineZoneSource source)
    {
        _v = view;
        _source = source;
        _generator = new Generator(this);
        view.TextView.ElementGenerators.Insert(0, _generator); // first: a zone opens the line, before an inlay hint or a whitespace marker at column 0
        _layer = new ZoneLayer(this);
        view.TextView.InsertLayer(_layer, KnownLayer.Caret, LayerInsertionPosition.Above);
        view.TextView.VisualLinesChanged += OnVisualLinesChanged;
        view.TextView.ScrollOffsetChanged += OnVisualLinesChanged;
        _d.Add(Disposable.Create(() =>
        {
            view.TextView.VisualLinesChanged -= OnVisualLinesChanged;
            view.TextView.ScrollOffsetChanged -= OnVisualLinesChanged;
            view.TextView.ElementGenerators.Remove(_generator);
            view.TextView.Layers.Remove(_layer);
        }));
    }

    internal Control Layer => _layer;

    /// <summary>Whether <paramref name="generator"/> is a zone generator (it stays first: a zone opens its line).</summary>
    internal static bool IsZoneGenerator(VisualLineElementGenerator generator) => generator is Generator;

    /// <summary>The zone height of a built visual line (0 when it has none).</summary>
    public static double HeightOf(VisualLine visualLine) =>
        visualLine.Elements.Count > 0 && visualLine.Elements[0] is Spacer s ? s.Height : 0;

    /// <summary>Top of the first text row of <paramref name="visualLine"/> (below its zone), in document-visual coordinates (not scrolled).</summary>
    public static double TextTop(VisualLine visualLine) =>
        visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.LineTop) + HeightOf(visualLine);

    /// <summary>
    /// How far below <c>VisualYPosition.LineTop</c> the text of <paramref name="visualColumn"/> starts: the zone height when that column is on the
    /// line's first row (the row a zone makes tall), else 0. For renderers that place things by <c>LineTop</c>.
    /// </summary>
    public static double ZoneOffset(VisualLine visualLine, int visualColumn, bool isAtEndOfLine = false)
    {
        var h = HeightOf(visualLine);
        return h > 0 && ReferenceEquals(visualLine.GetTextLine(visualColumn, isAtEndOfLine), visualLine.TextLines[0]) ? h : 0;
    }

    /// <summary><see cref="ZoneOffset(VisualLine, int, bool)"/> for a document position (0 when its line is not built).</summary>
    public static double ZoneOffset(TextView textView, AvaloniaEdit.TextViewPosition position)
    {
        if (textView.GetVisualLine(position.Line) is not { } vl) return 0;
        var column = position.VisualColumn >= 0 ? position.VisualColumn : vl.GetVisualColumn(Math.Max(0, position.Column - 1));
        return ZoneOffset(vl, column, position.IsAtEndOfLine);
    }

    /// <summary>The zones changed (heights or content): rebuilds the visible lines (heights) and repaints.</summary>
    public void Invalidate()
    {
        _v.TextView.Redraw();
        _layer.InvalidateVisual();
    }

    /// <summary>The content of the zones changed but no height did: repaint only.</summary>
    public void Repaint() => _layer.InvalidateVisual();

    private void OnVisualLinesChanged(object? sender, EventArgs e) => _layer.InvalidateVisual();

    /// <summary>The zones of the visible lines: (line, visual line, zone rectangle in text-view coordinates).</summary>
    private IEnumerable<(int Line, VisualLine Visual, Rect Zone)> VisibleZones()
    {
        var tv = _v.TextView;
        if (!tv.VisualLinesValid) yield break;
        var scroll = tv.ScrollOffset.Y;
        foreach (var vl in tv.VisualLines)
        {
            var h = HeightOf(vl);
            if (h <= 0) continue;
            var top = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.LineTop) - scroll;
            yield return (vl.FirstDocumentLine.LineNumber, vl, new Rect(0, top, tv.Bounds.Width, h));
        }
    }

    private (int Line, Rect Zone)? ZoneAt(Point p)
    {
        foreach (var (line, _, zone) in VisibleZones())
            if (zone.Contains(p)) return (line, zone);
        return null;
    }

    /// <summary>The zone element: no text, no caret stop, a run that makes the line's first row taller above the text.</summary>
    internal sealed class Spacer(double height, double textBaseline) : VisualLineElement(1, 0)
    {
        public double Height { get; } = height;

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
            new SpacerRun(textBaseline + Height, context.GlobalTextRunProperties);

        // The caret never stops before the zone: Left at the text start leaves the line, Home lands on the text.
        public override bool HandlesLineBorders => true;

        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode)
        {
            var end = VisualColumn + VisualLength;
            return direction == LogicalDirection.Backward ? (visualColumn > end ? end : -1) : (visualColumn < end ? end : -1);
        }

        public override bool IsWhitespace(int visualColumn) => true;
    }

    private sealed class SpacerRun(double ascent, TextRunProperties properties) : DrawableTextRun
    {
        public override TextRunProperties? Properties => properties;
        public override Size Size => new(0, ascent);
        public override double Baseline => ascent;
        public override void Draw(DrawingContext drawingContext, Point origin) { }
    }

    private sealed class Generator(LineZones owner) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            var line = CurrentContext.VisualLine.FirstDocumentLine;
            if (startOffset > line.Offset) return -1;
            return owner._source.HeightAbove(line.LineNumber) > 0 ? line.Offset : -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var line = CurrentContext.VisualLine.FirstDocumentLine;
            if (offset != line.Offset) return null;
            var h = owner._source.HeightAbove(line.LineNumber);
            return h > 0 ? new Spacer(h, CurrentContext.TextView.DefaultBaseline) : null;
        }
    }

    /// <summary>Above the caret layer: paints the zones (covering what AvaloniaEdit drew over the tall row) and takes the pointer there.</summary>
    private sealed class ZoneLayer : Control, ICustomHitTest
    {
        private readonly LineZones _owner;
        private int _hoverLine = -1;

        public ZoneLayer(LineZones owner)
        {
            _owner = owner;
            ClipToBounds = true;
            Focusable = false;
        }

        public bool HitTest(Point point) => _owner.ZoneAt(point) is not null;

        public override void Render(DrawingContext context)
        {
            var v = _owner._v;
            var tv = v.TextView;
            if (!tv.VisualLinesValid) return;
            var background = v.Palette.Background;
            var caretLine = v.TextArea.Caret.Line;
            foreach (var (line, vl, zone) in _owner.VisibleZones())
            {
                context.FillRectangle(background, zone);
                // AvaloniaEdit's current-line box spans the tall row: its top edge went under the zone, so it is redrawn at the text's top.
                if (v.Settings.HighlightActiveLine && caretLine >= vl.FirstDocumentLine.LineNumber && caretLine <= vl.LastDocumentLine.LineNumber
                    && tv.CurrentLineBorder is { } pen && v.TextArea.Selection.IsEmpty)
                    context.DrawLine(pen, new Point(zone.Left, zone.Bottom + 0.5), new Point(zone.Right, zone.Bottom + 0.5));
                using (context.PushClip(zone)) _owner._source.Render(context, vl, line, zone);
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            if (_owner.ZoneAt(p) is { } hit)
            {
                var clickable = _owner._source.IsClickable(hit.Line, hit.Zone, p);
                Cursor = clickable ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
                _hoverLine = hit.Line;
                _owner._source.Hover(hit.Line, p);
                e.Handled = true;
            }
            else LeaveHover();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            LeaveHover();
        }

        private void LeaveHover()
        {
            if (_hoverLine < 0) return;
            _hoverLine = -1;
            Cursor = null;
            _owner._source.Hover(-1, null);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var p = e.GetPosition(this);
            if (_owner.ZoneAt(p) is not { } hit) return;
            e.Handled = true; // a zone is not text: a press there never moves the caret or starts a selection
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) _owner._source.Click(hit.Line, hit.Zone, p);
        }
    }

    public void Dispose() => _d.Dispose();
}
