using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using CsCode.Core.Lifecycle;
using CsCode.Core.Theming;
using CsCode.Editor.Features;
using CsCode.Editor.TextMate;
using CsCode.Editor.Threading;

namespace CsCode.Editor.View.Decorations;

/// <summary>
/// The minimap (<c>editor.minimap.*</c>): the document drawn small beside the text — one row per line (folded lines hidden), each character a
/// cell in its token's colour — with a slider over the rows on screen that can be dragged, a click that jumps there, and the wheel scrolling
/// the editor. Long files scroll the minimap in proportion (<see cref="MinimapGeometry"/>), so only the rows in the minimap's window are ever
/// drawn: a 1M-line file costs what a screenful does.
///
/// Threading: the UI thread captures the window's rows (line offsets and token rows, O(rows × log lines)) with the document's immutable
/// snapshot; a pool thread draws them into a pixel buffer (<see cref="MinimapRasterizer"/>); the UI thread copies that into a bitmap. One
/// render runs at a time: requests while it runs coalesce into one more, so typing costs at most one render at a time and the keystroke
/// itself only marks the minimap stale. A scroll that keeps the window (a short file, or small steps) only moves the slider. Find matches,
/// problems, the carets and source-control changes from the ruler's marks are drawn over the rows of the window.
/// </summary>
internal sealed class Minimap : IDisposable
{
    /// <summary>Width of a character cell and height of a row at scale 1 (DIPs).</summary>
    internal const double CharWidth = 1, RowHeight = 2;
    /// <summary>The minimap never takes more than this share of the editor's width, and is hidden below <see cref="MinWidth"/>.</summary>
    internal const double MaxShare = 0.3, MinWidth = 24;
    private const double GutterWidth = 2;

    private readonly ScrollDecorations _owner;
    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private readonly MinimapRasterizer _rasterizer = new();
    private readonly Action _startRender;
    private MinimapRenderJob _job = new();
    private MinimapRowMap _rows = MinimapRowMap.Empty;
    private bool _rowsDirty = true;
    private double _rowsDocumentHeight = -1;
    private MinimapFrame _frame;
    private MinimapColors? _colors;
    private TokenizationSession? _tokens;
    private IDisposable? _tokensSubscription;
    private int[] _pixels = [];
    private WriteableBitmap? _bitmap;
    private int _bitmapWindowTop = -1;
    private bool _renderPosted, _rendering, _renderAgain, _disposed;
    private MarkerKind[] _rowMarks = [], _gutterMarks = [];
    private (int WindowTop, int Rows, int MarksVersion, MinimapRowMap Map) _marksKey;
    private bool _dragging;
    private double _dragOffset;

    public Minimap(ScrollDecorations owner)
    {
        _owner = owner;
        _v = owner.View;
        _startRender = StartRender;
        Control = new MinimapControl(this) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch, ClipToBounds = true, Cursor = new Cursor(StandardCursorType.Arrow) };
        Avalonia.Automation.AutomationProperties.SetName(Control, "Minimap");
        var tv = _v.TextView;
        tv.ScrollOffsetChanged += OnScrolled;
        tv.VisualLinesChanged += OnVisualLinesChanged;
        _d.Add(Disposable.Create(() =>
        {
            tv.ScrollOffsetChanged -= OnScrolled;
            tv.VisualLinesChanged -= OnVisualLinesChanged;
        }));
        _d.Add(_v.Document.Changed.Subscribe(_ => OnDocumentChanged()));
    }

    internal Control Control { get; }
    internal MinimapRowMap Rows => _rows;
    internal MinimapFrame Frame => _frame;
    internal WriteableBitmap? Bitmap => _bitmap;
    internal bool IsDragging => _dragging;

    /// <summary>Renders finished (tests: typing must not render once per keystroke, a scroll inside the window must not render at all).</summary>
    internal int Renders { get; private set; }

    /// <summary>UI-thread milliseconds of the last render: capturing its rows, and copying its pixels into the bitmap (tests, docs/EDITOR.md §7c).</summary>
    internal double LastCaptureMs { get; private set; }
    internal double LastBlitMs { get; private set; }

    /// <summary>The window top the bitmap was drawn for (-1: nothing drawn yet).</summary>
    internal int RenderedWindowTop => _bitmapWindowTop;

    /// <summary>True while a render runs or is queued.</summary>
    internal bool IsRenderPending => _rendering || _renderPosted || _renderAgain;

    /// <summary>The pixel buffer of the last render (tests read colours from it; premultiplied BGRA, row-major).</summary>
    internal (int[] Pixels, int Width, int Height) LastPixels => (_lastPixels, _lastWidth, _lastHeight);
    private int[] _lastPixels = [];
    private int _lastWidth, _lastHeight;

    internal bool Active => _owner.Options.MinimapEnabled && Control.IsVisible && !_disposed;

    /// <summary>DIPs per character cell and per row at the current scale.</summary>
    private double CellWidth => CharWidth * _owner.Options.Scale;
    private double RowDip => Math.Max(1, RowPixels) / Scaling;
    private int RowPixels => Math.Max(1, (int)Math.Round(RowHeight * _owner.Options.Scale * Scaling));
    private int CharPixels => Math.Max(1, (int)Math.Round(CharWidth * _owner.Options.Scale * Scaling));
    private double Scaling => TopLevel.GetTopLevel(Control)?.RenderScaling is { } s && s > 0 ? s : 1;

    /// <summary>The minimap's width for an editor <paramref name="available"/> DIPs wide (0 = too narrow to show one).</summary>
    internal double WidthFor(double available)
    {
        var wanted = _owner.Options.MaxColumn * CellWidth;
        var width = Math.Floor(Math.Min(wanted, available * MaxShare));
        return width < MinWidth ? 0 : width;
    }

    // ---------------------------------------------------------------------------------------------- triggers

    internal void OnOptionsChanged(DecorationOptions old)
    {
        if (!Active)
        {
            ReleaseBitmap();
            return;
        }
        var o = _owner.Options;
        if (old.Scale != o.Scale || old.MaxColumn != o.MaxColumn || old.RenderCharacters != o.RenderCharacters || old.TabSize != o.TabSize || !old.MinimapEnabled)
            _bitmapWindowTop = -1; // what is drawn changed: draw again even where the window did not move
        UpdateFrame(forceRender: true);
    }

    internal void OnThemeChanged()
    {
        _colors = null;
        if (Active) UpdateFrame(forceRender: true);
    }

    internal void OnLayoutChanged()
    {
        if (Active) Dispatcher.UIThread.Post(() => UpdateFrame(forceRender: true), DispatcherPriority.Background);
    }

    internal void OnMarksChanged()
    {
        if (Active) Control.InvalidateVisual();
    }

    private void OnScrolled(object? sender, EventArgs e)
    {
        if (Active) UpdateFrame(forceRender: false);
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e)
    {
        if (!Active) return;
        // folding and wrapping change the document's height: the hidden lines may have changed
        if (Math.Abs(_v.TextView.DocumentHeight - _rowsDocumentHeight) > 0.01) _rowsDirty = true;
        UpdateFrame(forceRender: false);
    }

    private void OnDocumentChanged()
    {
        _rowsDirty = true;
        if (!Active) return;
        _bitmapWindowTop = -1;
        RequestRender();
    }

    private void OnTokensChanged((int First, int Last) range)
    {
        if (!Active) return;
        var first = _frame.WindowTopRow;
        var last = first + _frame.WindowRows;
        if (_rows.RowOfLine(range.Last) < first || _rows.RowOfLine(range.First) > last) return; // coloured lines off the minimap's window
        _bitmapWindowTop = -1;
        RequestRender();
    }

    // ---------------------------------------------------------------------------------------------- layout

    private void EnsureRows()
    {
        if (!_rowsDirty) return;
        _rowsDirty = false;
        var doc = _v.Editor.Document;
        if (doc is null) return;
        _rowsDocumentHeight = _v.TextView.DocumentHeight;
        var hidden = _v.Feature<FoldingFeature>()?.CollapsedLineRanges() ?? [];
        var map = MinimapRowMap.Create(doc.LineCount, hidden);
        if (map.SameAs(_rows)) return;
        _rows = map;
        _bitmapWindowTop = -1; // rows moved
    }

    /// <summary>Recomputes the window and the slider for the current scroll position; renders when the window moved (or <paramref name="forceRender"/>).</summary>
    internal void UpdateFrame(bool forceRender)
    {
        if (!Active) return;
        RefreshFrame();
        if (forceRender || _frame.WindowTopRow != _bitmapWindowTop) RequestRender();
        Control.InvalidateVisual();
    }

    private void RefreshFrame()
    {
        EnsureRows();
        var (topRow, viewportRows) = Viewport();
        _frame = MinimapGeometry.Compute(_rows.RowCount, topRow, viewportRows, RowDip, Control.Bounds.Height);
    }

    /// <summary>The editor's top row (fractional) and the number of rows it shows.</summary>
    private (double TopRow, double ViewportRows) Viewport()
    {
        var tv = _v.TextView;
        var top = _v.Controller.TopLine;
        var line = (int)Math.Floor(top);
        var topRow = _rows.RowOfLine(line) + (top - line);
        double rows;
        if (tv.VisualLinesValid && tv.VisualLines.Count > 0)
        {
            var firstLine = tv.VisualLines[0].FirstDocumentLine.LineNumber - 1;
            var lastLine = tv.VisualLines[^1].LastDocumentLine.LineNumber - 1;
            rows = _rows.RowOfLine(lastLine) - _rows.RowOfLine(firstLine) + 1;
            // the last visual line is usually cut off by the viewport's bottom
            var last = tv.VisualLines[^1];
            var visibleBottom = tv.ScrollOffset.Y + tv.Bounds.Height;
            if (last.VisualTop + last.Height > visibleBottom && last.Height > 0)
                rows -= Math.Clamp((last.VisualTop + last.Height - visibleBottom) / last.Height, 0, 1);
        }
        else rows = tv.DefaultLineHeight > 0 ? tv.Bounds.Height / tv.DefaultLineHeight : 1;
        return (topRow, Math.Max(1, rows));
    }

    // ---------------------------------------------------------------------------------------------- rendering

    internal void RequestRender()
    {
        if (!Active || _renderPosted) return;
        _renderPosted = true;
        Dispatcher.UIThread.Post(_startRender, DispatcherPriority.Background);
    }

    private void StartRender()
    {
        _renderPosted = false;
        if (!Active) return;
        if (_rendering)
        {
            _renderAgain = true;
            return;
        }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var doc = _v.Editor.Document;
        var scaling = Scaling;
        var pw = (int)Math.Ceiling(Control.Bounds.Width * scaling);
        var ph = (int)Math.Ceiling(Control.Bounds.Height * scaling);
        if (doc is null || pw <= 0 || ph <= 0) return;
        var snapshot = _v.Document.Snapshot();
        if (snapshot.Length != doc.TextLength)
        {
            RequestRender(); // inside an update: the snapshot is published when it ends
            return;
        }
        RefreshFrame();
        EnsureTokenSubscription();
        _colors ??= CreateColors();
        var frame = _frame;
        var rowPx = RowPixels;
        var windowRows = Math.Min(_rows.RowCount - frame.WindowTopRow, ph / rowPx + 1);
        var job = _job;
        job.PixelWidth = pw;
        job.PixelHeight = ph;
        job.RowPixels = rowPx;
        job.CharPixels = CharPixels;
        job.MaxColumn = _owner.Options.MaxColumn;
        job.TabSize = _owner.Options.TabSize;
        job.RenderCharacters = _owner.Options.RenderCharacters;
        job.Snapshot = snapshot;
        job.Colors = _colors;
        job.EnsureCapacity(Math.Max(0, windowRows));
        job.RowCount = Math.Max(0, windowRows);
        var store = _tokens?.Store;
        AvaloniaEdit.Document.DocumentLine? previous = null;
        var previousLine = -2;
        for (var r = 0; r < job.RowCount; r++)
        {
            var line = _rows.LineOfRow(frame.WindowTopRow + r);
            var dl = line == previousLine + 1 && previous?.NextLine is { } next ? next : doc.GetLineByNumber(line + 1);
            job.Lines[r] = new MinimapLine { Offset = dl.Offset, Length = dl.Length, Tokens = store?.GetTokens(line) };
            previous = dl;
            previousLine = line;
        }
        if (_pixels.Length < pw * ph) _pixels = new int[pw * ph];
        var pixels = _pixels;
        var windowTop = frame.WindowTopRow;
        LastCaptureMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _rendering = true;
        // on a pool thread, not the editor worker: drawing never delays a tokenisation slice, and a theme change re-colours without a worker job
        Task.Run(() => _rasterizer.Render(job, pixels)).ContinueWith(t => _v.Ui.Post(() => FinishRender(job, pixels, windowTop, t.Status == TaskStatus.RanToCompletion)), TaskScheduler.Default);
    }

    private void FinishRender(MinimapRenderJob job, int[] pixels, int windowTop, bool ok)
    {
        _rendering = false;
        job.Snapshot = null; // the rows' text is in the pixels now: do not keep the old text alive
        Array.Clear(job.Lines, 0, job.RowCount); // nor the token rows
        if (_disposed) return;
        if (ok && Active)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            Blit(pixels, job.PixelWidth, job.PixelHeight);
            LastBlitMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _bitmapWindowTop = windowTop;
            (_lastPixels, _lastWidth, _lastHeight) = (pixels, job.PixelWidth, job.PixelHeight);
            Renders++;
            Control.InvalidateVisual();
        }
        if (_renderAgain || (Active && _frame.WindowTopRow != _bitmapWindowTop))
        {
            _renderAgain = false;
            RequestRender();
        }
    }

    private void Blit(int[] pixels, int w, int h)
    {
        if (_bitmap is null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }
        using var fb = _bitmap.Lock();
        for (var y = 0; y < h; y++) Marshal.Copy(pixels, y * w, fb.Address + y * fb.RowBytes, w);
    }

    private void ReleaseBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _bitmapWindowTop = -1;
        _pixels = [];
        _lastPixels = [];
        _job = new MinimapRenderJob();
    }

    private MinimapColors CreateColors()
    {
        var themes = _v.Factory.Themes;
        return new MinimapColors(themes.Current, themes.GetColor("minimap.background"), themes.GetColor("editor.foreground"), _v.Tokens?.Interner);
    }

    private void EnsureTokenSubscription()
    {
        var session = _v.Tokens;
        if (ReferenceEquals(session, _tokens)) return;
        _tokensSubscription?.Dispose();
        _tokens = session;
        _tokensSubscription = session?.TokensChanged.Subscribe(OnTokensChanged);
        _colors = null; // a colouring session came or went: its interner resolves the colours
    }

    // ---------------------------------------------------------------------------------------------- marks over the rows

    private void EnsureRowMarks()
    {
        var key = (_frame.WindowTopRow, _frame.WindowRows, _owner.MarksVersion, _rows);
        if (key == _marksKey && _rowMarks.Length == _frame.WindowRows) return;
        _marksKey = key;
        var n = Math.Max(0, _frame.WindowRows);
        if (_rowMarks.Length != n)
        {
            _rowMarks = new MarkerKind[n];
            _gutterMarks = new MarkerKind[n];
        }
        else
        {
            Array.Clear(_rowMarks);
            Array.Clear(_gutterMarks);
        }
        var top = _frame.WindowTopRow;
        void Mark(List<LineMarker> list, MarkerKind[] into)
        {
            foreach (var m in list)
            {
                if (m.Kind is MarkerKind.Info) continue;
                var from = _rows.RowOfLine(m.Line) - top;
                var to = _rows.RowOfLine(m.EndLine) - top;
                if (to < 0 || from >= n) continue;
                for (var r = Math.Max(0, from); r <= Math.Min(n - 1, to); r++)
                    if (into[r] < m.Kind) into[r] = m.Kind;
            }
        }
        Mark(_owner.Scm, _gutterMarks);
        Mark(_owner.FindMatches, _rowMarks);
        Mark(_owner.Diagnostics, _rowMarks);
        Mark(_owner.Cursors, _rowMarks);
    }

    // ---------------------------------------------------------------------------------------------- input

    private bool SliderVisible => _owner.Options.SliderAlways || _dragging || Control.IsPointerOver;

    private void OnPressed(PointerPressedEventArgs e)
    {
        if (!Active || !e.GetCurrentPoint(Control).Properties.IsLeftButtonPressed) return;
        var y = e.GetPosition(Control).Y;
        var f = _frame;
        if (y >= f.SliderTop && y <= f.SliderTop + f.SliderHeight) _dragOffset = y - f.SliderTop;
        else
        {
            _dragOffset = f.SliderHeight / 2;
            ScrollToSliderTop(y - _dragOffset);
        }
        _dragging = true;
        e.Pointer.Capture(Control);
        e.Handled = true;
        Control.InvalidateVisual();
    }

    private void OnMoved(PointerEventArgs e)
    {
        if (!_dragging) return;
        ScrollToSliderTop(e.GetPosition(Control).Y - _dragOffset);
        e.Handled = true;
    }

    private void OnReleased(PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        Control.InvalidateVisual();
    }

    private void OnWheel(PointerWheelEventArgs e)
    {
        var scrollable = (IScrollable)_v.TextArea;
        var step = Math.Max(1, _v.TextView.DefaultLineHeight) * 3;
        var max = Math.Max(0, scrollable.Extent.Height - scrollable.Viewport.Height);
        scrollable.Offset = new Vector(scrollable.Offset.X, Math.Clamp(scrollable.Offset.Y - e.Delta.Y * step, 0, max));
        e.Handled = true;
    }

    /// <summary>Scrolls the editor so the slider's top is at <paramref name="sliderTop"/> (DIPs from the minimap's top).</summary>
    internal void ScrollToSliderTop(double sliderTop)
    {
        if (!Active) return;
        var (_, viewportRows) = Viewport();
        var topRow = MinimapGeometry.TopRowForSliderTop(sliderTop, _rows.RowCount, viewportRows, RowDip, Control.Bounds.Height);
        var row = (int)Math.Floor(topRow);
        _v.Controller.ScrollToLine(_rows.LineOfRow(row) + (topRow - row));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tokensSubscription?.Dispose();
        _tokensSubscription = null;
        _d.Dispose();
        ReleaseBitmap();
    }

    private sealed class MinimapControl(Minimap owner) : Control
    {
        private static readonly IBrush Fallback = new ImmutableSolidColorBrush(Colors.Transparent);
        private IBrush? _background;
        private Rgba _backgroundColor;

        protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); owner.OnPressed(e); }
        protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); owner.OnMoved(e); }
        protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); owner.OnReleased(e); }
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            owner._dragging = false;
            InvalidateVisual();
        }
        protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); InvalidateVisual(); }
        protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); InvalidateVisual(); }
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e) { base.OnPointerWheelChanged(e); owner.OnWheel(e); }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            owner.UpdateFrame(forceRender: true);
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            var color = owner._v.Factory.Themes.GetColor("minimap.background");
            if (_background is null || color != _backgroundColor)
            {
                _backgroundColor = color;
                _background = new ImmutableSolidColorBrush(EditorPalette.ToColor(color));
            }
            context.FillRectangle(_background ?? Fallback, bounds); // also what makes the whole minimap take the pointer
            if (!owner.Active) return;
            var f = owner._frame;
            var rowDip = owner.RowDip;
            if (owner._bitmap is { } bmp && owner._bitmapWindowTop >= 0)
            {
                var scaling = owner.Scaling;
                var dy = (owner._bitmapWindowTop - f.WindowTopRow) * rowDip; // until the new window's render lands, keep the drawn rows in place
                var size = bmp.PixelSize;
                using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                    context.DrawImage(bmp, new Rect(0, 0, size.Width, size.Height), new Rect(0, dy, size.Width / scaling, size.Height / scaling));
            }
            owner.EnsureRowMarks();
            var marks = owner._owner.Brushes.MinimapMarks;
            for (var r = 0; r < owner._rowMarks.Length; r++)
            {
                var y = r * rowDip;
                if (y > bounds.Height) break;
                if (owner._rowMarks[r] is var k and not MarkerKind.None) context.FillRectangle(marks[(int)k], new Rect(GutterWidth, y, bounds.Width - GutterWidth, rowDip));
                if (owner._gutterMarks[r] is var g and not MarkerKind.None) context.FillRectangle(marks[(int)g], new Rect(0, y, GutterWidth, rowDip));
            }
            if (owner.SliderVisible && f.SliderHeight > 0)
            {
                var b = owner._owner.Brushes;
                var brush = owner._dragging ? b.SliderActive : IsPointerOver ? b.SliderHover : b.Slider;
                context.FillRectangle(brush, new Rect(0, f.SliderTop, bounds.Width, f.SliderHeight));
            }
        }
    }
}
