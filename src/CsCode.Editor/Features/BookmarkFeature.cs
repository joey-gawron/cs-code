using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.View;
using CsCode.Editor.View.Decorations;

namespace CsCode.Editor.Features;

/// <summary>
/// The bookmarks of one view (<see cref="IBookmarkService"/>, docs/EDITOR.md §7g): a glyph on each bookmarked line in the indicator margin and a
/// mark on the overview ruler. Visual Studio draws bookmarks in the glyph margin beside breakpoints, so when the view has a breakpoint margin
/// (<see cref="DebugFeature"/>) the glyphs are drawn there, under the breakpoints, through its <see cref="DebugFeature.Underlay"/>; without one
/// (no debugger composed, large-file mode) the feature adds a margin of its own of the same width, which takes no width while the document has
/// no bookmark. A click never toggles a bookmark: the margin's click stays the breakpoint's. A glyph sits on its line's first visual row, level
/// with the line number, also when the line wraps or carries a code lens. Only documents the document manager knows (files, untitled buffers;
/// not the sides of a diff) get one.
/// </summary>
internal sealed class BookmarkFeature : IThemeAware, ISettingsAware, IDisposable
{
    internal const double MarginWidth = DebugFeature.MarginWidth;

    private readonly CodeEditorControl _v;
    private readonly IBookmarkService _service;
    private readonly DebugFeature? _debug;
    private readonly BookmarkMargin? _margin;
    private readonly DisposableStore _d = new();
    private Dictionary<int, Bookmark> _byLine = [];
    private IBrush _fill = Brushes.Transparent;
    private IPen _outline = new Pen(Brushes.Transparent);
    private bool _disposed;

    private BookmarkFeature(CodeEditorControl view, IBookmarkService service, DebugFeature? debug)
    {
        _v = view;
        _service = service;
        _debug = debug;
        BuildBrushes();
        if (debug is not null)
        {
            debug.Underlay = Render; // the breakpoint margin draws the bookmarks first, then its breakpoints and the stop arrow over them
            _d.Add(Disposable.Create(() => { if (debug.Underlay == Render) debug.Underlay = null; }));
        }
        else
        {
            _margin = new BookmarkMargin(this);
            view.TextArea.LeftMargins.Insert(0, _margin);
            _d.Add(Disposable.Create(() => view.TextArea.LeftMargins.Remove(_margin)));
        }
        _d.Add(service.Changed.Subscribe(e =>
        {
            if (e.AffectedUris.Count > 0 && !e.AffectedUris.Contains(_v.Document.Uri)) return;
            // an edit moved them (UI thread, inside the edit): redraw with the new lines before the next frame
            if (_v.Ui.CheckAccess()) Refresh();
            else _v.Post(Refresh);
        }));
        Refresh();
    }

    /// <summary>The feature for <paramref name="view"/>, or null when bookmarks are not composed or the document is not one the user opened.</summary>
    internal static BookmarkFeature? TryCreate(CodeEditorControl view, DebugFeature? debug) =>
        view.Services.Bookmarks is { } service && IsBookmarkable(view) ? new BookmarkFeature(view, service, debug) : null;

    /// <summary>A document registered with the document manager (not a diff side or a peek's read-only copy).</summary>
    internal static bool IsBookmarkable(CodeEditorControl view) => ReferenceEquals(view.Services.Documents.Get(view.Document.Uri), view.Document);

    /// <summary>The bookmarked lines (0-based) of this view's document, by line.</summary>
    internal IReadOnlyCollection<Bookmark> Bookmarks => _byLine.Values;

    internal Bookmark? BookmarkAt(int line) => _byLine.GetValueOrDefault(line);

    /// <summary>The margin the glyphs are drawn in: the breakpoint margin, else the feature's own.</summary>
    internal Control Margin => (Control?)_margin ?? _debug!.Margin;

    /// <summary>Where the glyph of <paramref name="line"/> is drawn, in the margin's coordinates; null when the line is not on screen (tests).</summary>
    internal Rect? GlyphBounds(int line)
    {
        var textView = _v.TextView;
        if (!textView.VisualLinesValid) return null;
        foreach (var vl in textView.VisualLines)
            if (vl.FirstDocumentLine.LineNumber - 1 == line) return GlyphRect(vl, textView, Margin.Bounds.Width);
        return null;
    }

    private void Refresh()
    {
        if (_disposed) return;
        var uri = _v.Document.Uri;
        var next = new Dictionary<int, Bookmark>();
        foreach (var b in _service.GetForUri(uri)) next.TryAdd(b.Line, b);
        var hadAny = _byLine.Count > 0;
        _byLine = next;
        if (_margin is not null && hadAny != next.Count > 0) _margin.InvalidateMeasure();
        Margin.InvalidateVisual();
        _v.NotifyMarkersChanged(ScrollDecorations.Sources.Bookmarks);
    }

    public void OnThemeChanged()
    {
        BuildBrushes();
        Margin.InvalidateVisual();
    }

    /// <summary>The line-number margin is re-inserted when line numbers are toggled: keep the glyph margin leftmost, as the breakpoint margin does.</summary>
    public void OnSettingsChanged()
    {
        if (_margin is null) return;
        var margins = _v.TextArea.LeftMargins;
        if (margins.IndexOf(_margin) is var i and > 0)
        {
            margins.RemoveAt(i);
            margins.Insert(0, _margin);
        }
    }

    private void BuildBrushes()
    {
        var themes = _v.Factory.Themes;
        var fill = EditorPalette.ToColor(themes.GetColor("bookmarks.iconForeground"));
        _fill = new ImmutableSolidColorBrush(fill);
        _outline = new ImmutablePen(new ImmutableSolidColorBrush(fill), 1.2);
    }

    /// <summary>
    /// The glyph's box on <paramref name="vl"/>: the first row's text (VisualYPosition.TextTop, where the line number is drawn, below a code lens),
    /// one default line high, so a wrapped line's glyph never drifts to the middle of its rows.
    /// </summary>
    private static Rect GlyphRect(VisualLine vl, TextView textView, double width)
    {
        var top = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.TextTop) - textView.VerticalOffset;
        return new Rect(0, top, width, textView.DefaultLineHeight);
    }

    /// <summary>Draws the bookmarks on screen into a margin <paramref name="width"/> wide (the breakpoint margin's underlay, or the own margin).</summary>
    internal void Render(DrawingContext context, TextView textView, double width)
    {
        if (_disposed || _byLine.Count == 0 || !textView.VisualLinesValid) return;
        foreach (var vl in textView.VisualLines)
        {
            if (!_byLine.TryGetValue(vl.FirstDocumentLine.LineNumber - 1, out var bookmark)) continue;
            DrawGlyph(context, GlyphRect(vl, textView, width), bookmark.Enabled);
        }
    }

    /// <summary>A bookmark ribbon (Visual Studio's and the Bookmarks extension's shape): filled when enabled, an outline when disabled.</summary>
    private void DrawGlyph(DrawingContext context, Rect row, bool enabled)
    {
        var h = Math.Max(6, Math.Min(row.Height - 4, 13));
        var w = Math.Max(5, Math.Min(row.Width - 8, Math.Round(h * 0.72)));
        var x = row.X + Math.Round((row.Width - w) / 2);
        var y = row.Y + Math.Round((row.Height - h) / 2);
        var ribbon = new StreamGeometry();
        using (var g = ribbon.Open())
        {
            g.BeginFigure(new Point(x, y), true);
            g.LineTo(new Point(x + w, y));
            g.LineTo(new Point(x + w, y + h));
            g.LineTo(new Point(x + w / 2, y + h - w * 0.45));
            g.LineTo(new Point(x, y + h));
            g.EndFigure(true);
        }
        context.DrawGeometry(enabled ? _fill : null, enabled ? null : _outline, ribbon);
    }

    // ---- the margin of its own (no breakpoint margin to share) ----

    private sealed class BookmarkMargin : AbstractMargin
    {
        private readonly BookmarkFeature _owner;

        public BookmarkMargin(BookmarkFeature owner)
        {
            _owner = owner;
            Avalonia.Automation.AutomationProperties.SetName(this, "Bookmark margin");
        }

        protected override Size MeasureOverride(Size availableSize) => new(_owner._byLine.Count > 0 ? MarginWidth : 0, 0);

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
            InvalidateVisual();
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

        public override void Render(DrawingContext context)
        {
            if (TextView is not { } textView || _owner._byLine.Count == 0) return;
            context.FillRectangle(_owner._v.Palette.Gutter, new Rect(0, 0, Bounds.Width, Bounds.Height));
            _owner.Render(context, textView, Bounds.Width);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _d.Dispose();
        _byLine = [];
    }
}
