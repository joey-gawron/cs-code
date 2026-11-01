using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;

namespace CsCode.Editor.View.Decorations;

/// <summary>
/// The overview ruler (VS Code's decorations on the vertical scrollbar): three lanes over the scrollbar's track — source-control changes and
/// breakpoints on the left, find matches and occurrence highlights in the centre, problems on the right — and the caret lines across all of
/// them. A mark is placed where its line is in the scrollable height (AvaloniaEdit's height tree: folded lines take no room, wrapped lines
/// their rows), so marks line up with the scrollbar's thumb. Marks are bucketed per pixel row (<see cref="OverviewRulerModel"/>): drawing costs
/// O(height) and laying out O(marks × log lines), whatever the document's size. The ruler takes no pointer input itself (the scrollbar under it
/// keeps its thumb and hover); a press on the scrollbar's track jumps there, centring that position (VS Code's default) instead of paging.
/// </summary>
internal sealed class OverviewRuler : IDisposable
{
    private readonly ScrollDecorations _owner;
    private readonly OverviewRulerModel _model = new();
    private readonly List<(int Top, int Height, MarkerKind Kind)> _runs = [];
    private ScrollBar? _scrollBar;
    private Track? _track;
    private double _extent, _trackTop, _trackHeight;
    private bool _disposed;

    public OverviewRuler(ScrollDecorations owner)
    {
        _owner = owner;
        Control = new RulerControl(this) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch, IsHitTestVisible = false, ClipToBounds = true };
        var tv = owner.View.TextView;
        tv.ScrollOffsetChanged += OnTextViewChanged; // the extent changes with edits, folding and wrapping; checked cheaply below
        tv.VisualLinesChanged += OnTextViewChanged;
    }

    internal Control Control { get; }
    internal OverviewRulerModel Model => _model;

    /// <summary>Times the marks were laid out into pixel rows (tests: a scroll must not do it).</summary>
    internal int Layouts { get; private set; }

    /// <summary>The ruler's width: the scrollbar's (Fluent's <c>ScrollBarSize</c> resource; 14, VS Code's, when there is none).</summary>
    internal double Width => _owner.View.TryFindResource("ScrollBarSize", out var v) && v is double d && d > 0 ? d : 14;

    /// <summary>Top and height of the area marks are placed in (the scrollbar's track, between its arrow buttons), in ruler coordinates.</summary>
    internal (double Top, double Height) Track => (_trackTop, _trackHeight);

    internal void OnOptionsChanged() => Refresh();

    private void OnTextViewChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        HookScrollBar();
        // re-layout only when what marks are placed against changed: the scrollable height or the track
        var (extent, top, height) = Measure();
        if (Math.Abs(extent - _extent) > 0.5 || Math.Abs(top - _trackTop) > 0.5 || Math.Abs(height - _trackHeight) > 0.5) Refresh();
    }

    private (double Extent, double TrackTop, double TrackHeight) Measure()
    {
        var extent = ((IScrollable)_owner.View.TextArea).Extent.Height;
        var h = Control.Bounds.Height;
        double top = 0, height = h;
        if (_scrollBar is { IsVisible: true } bar && (_track ??= bar.GetVisualDescendants().OfType<Track>().FirstOrDefault()) is { } track
            && track.Bounds.Height > 0 && track.TranslatePoint(default, Control) is { } origin)
        {
            top = Math.Clamp(origin.Y, 0, h);
            height = Math.Clamp(track.Bounds.Height, 0, h - top);
        }
        return (extent, top, height);
    }

    /// <summary>Lays the current marks out into pixel rows and redraws (UI thread; O(marks × log lines)).</summary>
    internal void Refresh()
    {
        if (_disposed) return;
        HookScrollBar();
        var (extent, trackTop, trackHeight) = Measure();
        _extent = extent;
        _trackTop = trackTop;
        _trackHeight = trackHeight;
        var height = (int)Math.Ceiling(Control.Bounds.Height);
        _model.Reset(height);
        var tv = _owner.View.TextView;
        var doc = tv.Document;
        if (!_owner.Options.RulerEnabled || doc is null || extent <= 0 || trackHeight <= 0 || height <= 0)
        {
            Control.InvalidateVisual();
            return;
        }
        Layouts++;
        // Which line is at the top of each pixel row of the track: one height-tree lookup per pixel row (not one per mark: 20,000 find matches
        // would cost 20,000 lookups), then each mark is placed by binary search in this table.
        var rows = (int)Math.Ceiling(trackHeight);
        if (_lineAtRow.Length != rows) _lineAtRow = new int[rows];
        var lineCount = doc.LineCount;
        var documentHeight = tv.DocumentHeight;
        for (var k = 0; k < rows; k++)
        {
            var y = k * extent / trackHeight;
            // past the last line (the room AllowScrollBelowDocument adds) no line starts: a sentinel after every line
            _lineAtRow[k] = y >= documentHeight ? lineCount : (tv.GetDocumentLineByVisualTop(y)?.LineNumber ?? lineCount + 1) - 1;
        }
        const int minHeight = 2;
        var top0 = (int)Math.Floor(trackTop);
        foreach (var list in _owner.AllMarks())
        {
            if (ReferenceEquals(list, _owner.Cursors) && _owner.Options.HideCursor) continue;
            foreach (var m in list)
            {
                var (top, bottom) = PixelRows(m.Line, m.EndLine);
                var h = Math.Max(minHeight, bottom - top);
                if (m.Kind == MarkerKind.Deleted) h = minHeight; // a deletion is between lines: a thin mark at its place
                _model.Add(top0 + top, h, m.Kind);
            }
        }
        Control.InvalidateVisual();
    }

    private int[] _lineAtRow = [];

    /// <summary>The track's pixel rows [top, bottom) lines [<paramref name="first"/>, <paramref name="last"/>] cover.</summary>
    private (int Top, int Bottom) PixelRows(int first, int last)
    {
        var a = _lineAtRow;
        // the first row whose top is in (or past) the line; the line itself starts inside the row before it
        var top = LowerBound(a, first);
        if (top > 0) top--;
        var bottom = Math.Max(top + 1, LowerBound(a, last + 1));
        return (Math.Min(top, Math.Max(0, a.Length - 1)), bottom);
    }

    private static int LowerBound(int[] a, int value)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (a[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // ---------------------------------------------------------------------------------------------- the scrollbar: clicks jump

    private void HookScrollBar()
    {
        if (_scrollBar is { } current && ReferenceEquals(current.FindAncestorOfType<AvaloniaEdit.TextEditor>(), _owner.View.Editor))
        {
            if (_track is { } t && !ReferenceEquals(t.FindAncestorOfType<ScrollBar>(), current)) _track = null; // the scrollbar's template was re-applied
            return;
        }
        if (_scrollBar is not null) _scrollBar.RemoveHandler(InputElement.PointerPressedEvent, OnScrollBarPressed);
        _track = null;
        _scrollBar = _owner.View.Editor.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(s => s.Orientation == Orientation.Vertical);
        _scrollBar?.AddHandler(InputElement.PointerPressedEvent, OnScrollBarPressed, RoutingStrategies.Tunnel);
    }

    private void OnScrollBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_disposed || !_owner.Options.RulerEnabled || sender is not ScrollBar bar || !e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed) return;
        // the thumb drags and the arrow buttons step, as always; only a press on the track itself jumps
        for (var v = e.Source as Visual; v is not null && !ReferenceEquals(v, bar); v = v.GetVisualParent())
        {
            if (v is Thumb) return;
            if (v is RepeatButton { Name: "PART_LineUpButton" or "PART_LineDownButton" }) return;
        }
        var y = e.GetPosition(Control).Y;
        if (JumpTo(y)) e.Handled = true;
    }

    /// <summary>Scrolls so that the position at ruler y-coordinate <paramref name="y"/> is in the middle of the view.</summary>
    internal bool JumpTo(double y)
    {
        var (extent, top, height) = Measure();
        if (extent <= 0 || height <= 0) return false;
        var scrollable = (IScrollable)_owner.View.TextArea;
        var viewport = scrollable.Viewport.Height;
        var target = Math.Clamp((y - top) / height, 0, 1) * extent - viewport / 2;
        target = Math.Clamp(target, 0, Math.Max(0, extent - viewport));
        scrollable.Offset = new Vector(scrollable.Offset.X, target);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var tv = _owner.View.TextView;
        tv.ScrollOffsetChanged -= OnTextViewChanged;
        tv.VisualLinesChanged -= OnTextViewChanged;
        _scrollBar?.RemoveHandler(InputElement.PointerPressedEvent, OnScrollBarPressed);
        _scrollBar = null;
    }

    private sealed class RulerControl(OverviewRuler owner) : Control
    {
        private Size _lastSize;

        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = base.ArrangeOverride(finalSize);
            if (size != _lastSize)
            {
                _lastSize = size;
                Avalonia.Threading.Dispatcher.UIThread.Post(owner.Refresh, Avalonia.Threading.DispatcherPriority.Background);
            }
            return size;
        }

        public override void Render(DrawingContext context)
        {
            var o = owner._owner;
            if (owner._disposed || !o.Options.RulerEnabled) return;
            var b = o.Brushes;
            var w = Bounds.Width;
            if (b.RulerBackground is { } bg) context.FillRectangle(bg, new Rect(0, 0, w, Bounds.Height));
            if (o.Options.RulerBorder) context.FillRectangle(b.RulerBorder, new Rect(0, 0, 1, Bounds.Height));
            var model = owner._model;
            if (model.Height == 0) return;
            var x0 = o.Options.RulerBorder ? 1.0 : 0.0;
            var laneWidth = (w - x0) / 3;
            void Lane(MarkerLane lane, double x, double width)
            {
                model.Runs(lane, owner._runs);
                foreach (var (top, height, kind) in owner._runs)
                    context.FillRectangle(b.Markers[(int)kind], new Rect(x, top, width, height));
            }
            Lane(MarkerLane.Left, x0, laneWidth);
            Lane(MarkerLane.Center, x0 + laneWidth, laneWidth);
            Lane(MarkerLane.Right, x0 + 2 * laneWidth, w - x0 - 2 * laneWidth);
            Lane(MarkerLane.Full, x0, w - x0);
        }
    }
}
