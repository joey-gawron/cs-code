using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Editor.Features;

namespace CsCode.Editor.View.Decorations;

/// <summary>
/// The overview ruler and the minimap of one view (VS Code's scrollbar decorations and minimap; docs/EDITOR.md §7c). Owns both controls, which
/// sit in the view's grid over the editor's right edge (the minimap left of the ruler, or at the left edge with <c>editor.minimap.side: left</c>),
/// and reserves their width with the text area's margin so no text runs under them. Collects the marks both draw from the view's features
/// (diagnostics, find matches, occurrence highlights, source-control changes, breakpoints, the carets), offsets turned into lines on the UI
/// thread, recollected per source only when that source changed and, after an edit, once typing pauses for <see cref="EditDelay"/>.
/// Settings and theme apply live; the minimap is off in diff panes (<see cref="CodeEditorControl.AllowMinimap"/>).
/// </summary>
internal sealed class ScrollDecorations : IThemeAware, ISettingsAware, IDisposable
{
    public const string MinimapSideKey = "editor.minimap.side";
    public const string MinimapRenderCharactersKey = "editor.minimap.renderCharacters";
    public const string MinimapMaxColumnKey = "editor.minimap.maxColumn";
    public const string MinimapShowSliderKey = "editor.minimap.showSlider";
    public const string MinimapScaleKey = "editor.minimap.scale";
    public const string RulerLanesKey = "editor.overviewRulerLanes";
    public const string RulerBorderKey = "editor.overviewRulerBorder";
    public const string HideCursorKey = "editor.hideCursorInOverviewRuler";
    public const string BreakpointsKey = "debug.showBreakpointsInOverviewRuler";

    /// <summary>How long after the last edit the marks are recollected (they move with the text meanwhile only as far as their anchors do).</summary>
    internal static TimeSpan EditDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    private static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);
    private static JsonElement S(string schema) => JsonDocument.Parse(schema).RootElement.Clone();

    /// <summary>The settings this component reads (VS Code's ids and defaults), registered by the factory so the Settings panel lists them.</summary>
    internal static IReadOnlyList<ConfigurationProperty> Properties { get; } =
    [
        new(MinimapSideKey, J("right"), "Where the minimap is drawn: right (beside the scrollbar) or left (before the line numbers).", S("""{"type":"string","enum":["right","left"]}""")),
        new(MinimapRenderCharactersKey, J(true), "Draw each character's shape in the minimap; off draws runs of text as coloured blocks.", S("""{"type":"boolean"}""")),
        new(MinimapMaxColumnKey, J(120), "The minimap draws at most this many columns of each line.", S("""{"type":"integer","minimum":1,"maximum":300}""")),
        new(MinimapShowSliderKey, J("mouseover"), "When the minimap's viewport slider is shown: mouseover (while the pointer is over the minimap or dragging) or always.", S("""{"type":"string","enum":["mouseover","always"]}""")),
        new(MinimapScaleKey, J(1), "Scale of the minimap's content: 1, 2 or 3.", S("""{"type":"integer","enum":[1,2,3]}""")),
        new(RulerLanesKey, J(3), "Lanes of the overview ruler on the vertical scrollbar (0 hides it): source control and breakpoints, find and occurrences, problems.", S("""{"type":"integer","enum":[0,1,2,3]}""")),
        new(RulerBorderKey, J(true), "Draw a border on the left of the overview ruler.", S("""{"type":"boolean"}""")),
        new(HideCursorKey, J(false), "Do not mark the caret lines on the overview ruler.", S("""{"type":"boolean"}""")),
        new(BreakpointsKey, J(false), "Mark breakpoints on the editor's overview ruler.", S("""{"type":"boolean"}""")),
    ];

    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private readonly Grid _host;
    private readonly DispatcherTimer _editTimer;
    private Sources _dirty = Sources.All;
    private bool _collectPosted, _disposed;

    [Flags]
    internal enum Sources
    {
        None = 0,
        Diagnostics = 1, Highlights = 2, Find = 4, Scm = 8, Breakpoints = 16, Cursors = 32,
        All = Diagnostics | Highlights | Find | Scm | Breakpoints | Cursors,
    }

    public ScrollDecorations(CodeEditorControl view, Grid host)
    {
        _v = view;
        _host = host;
        Options = ReadOptions();
        Brushes = DecorationBrushes.From(view.Factory.Themes);
        Ruler = new OverviewRuler(this);
        Minimap = new Minimap(this);
        // above the editor, below the popup overlay
        var at = Math.Max(0, host.Children.IndexOf(view.Overlay));
        host.Children.Insert(at, Minimap.Control);
        host.Children.Insert(at + 1, Ruler.Control);
        _d.Add(Disposable.Create(() =>
        {
            host.Children.Remove(Ruler.Control);
            host.Children.Remove(Minimap.Control);
        }));
        _editTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = EditDelay };
        _editTimer.Tick += OnEditTimer;
        _d.Add(Disposable.Create(() => { _editTimer.Stop(); _editTimer.Tick -= OnEditTimer; }));

        view.MarkersChanged = OnMarkersChanged;
        _d.Add(Disposable.Create(() => { if (view.MarkersChanged == OnMarkersChanged) view.MarkersChanged = null; }));
        _d.Add(view.Document.Changed.Subscribe(_ => OnDocumentChanged()));
        _d.Add(view.Controller.CaretChanged.Subscribe(_ => Invalidate(Sources.Cursors)));
        _d.Add(view.Controller.SelectionChanged.Subscribe(_ => Invalidate(Sources.Cursors)));
        _d.Add(view.Services.Config.Changed.Subscribe(e => { if (e.Affects(BreakpointsKey)) view.Post(OnSettingsChanged); }));
        host.SizeChanged += OnHostSizeChanged;
        _d.Add(Disposable.Create(() => host.SizeChanged -= OnHostSizeChanged));
        ApplyLayout();
        Invalidate(Sources.All);
    }

    internal CodeEditorControl View => _v;
    internal OverviewRuler Ruler { get; }
    internal Minimap Minimap { get; }
    internal DecorationOptions Options { get; private set; }
    internal DecorationBrushes Brushes { get; private set; }
    internal bool IsDisposed => _disposed;

    /// <summary>The current marks, per source, as document lines (UI thread). <see cref="MarksVersion"/> changes whenever any of them does.</summary>
    internal List<LineMarker> Diagnostics { get; } = [];
    internal List<LineMarker> Highlights { get; } = [];
    internal List<LineMarker> FindMatches { get; } = [];
    internal List<LineMarker> Scm { get; } = [];
    internal List<LineMarker> Breakpoints { get; } = [];
    internal List<LineMarker> Cursors { get; } = [];
    internal int MarksVersion { get; private set; }

    /// <summary>The sources the last collection read again (tests).</summary>
    internal Sources LastCollected { get; private set; }

    /// <summary>Every list of marks, lowest priority first (the order they are drawn in).</summary>
    internal IEnumerable<List<LineMarker>> AllMarks()
    {
        yield return Scm;
        yield return Breakpoints;
        yield return Highlights;
        yield return FindMatches;
        yield return Diagnostics;
        yield return Cursors;
    }

    /// <summary>Completes the pending collection now (tests).</summary>
    internal void FlushForTests()
    {
        _editTimer.Stop();
        Collect();
        Ruler.Refresh();
        Minimap.RequestRender();
    }

    // ---------------------------------------------------------------------------------------------- settings, theme, layout

    private DecorationOptions ReadOptions()
    {
        var cfg = _v.Services.Config;
        var lang = _v.Document.LanguageId;
        T G<T>(string key, T fallback) => cfg.GetOr(key, lang, fallback);
        return new DecorationOptions
        {
            MinimapEnabled = _v.Settings.Minimap && _v.AllowMinimap,
            MinimapLeft = string.Equals(G(MinimapSideKey, "right"), "left", StringComparison.OrdinalIgnoreCase),
            RenderCharacters = G(MinimapRenderCharactersKey, true),
            MaxColumn = Math.Clamp(G(MinimapMaxColumnKey, 120), 1, 300),
            SliderAlways = string.Equals(G(MinimapShowSliderKey, "mouseover"), "always", StringComparison.OrdinalIgnoreCase),
            Scale = Math.Clamp(G(MinimapScaleKey, 1), 1, 3),
            RulerEnabled = G(RulerLanesKey, 3) > 0,
            RulerBorder = G(RulerBorderKey, true),
            HideCursor = G(HideCursorKey, false),
            ShowBreakpoints = cfg.GetOr(BreakpointsKey, false),
            TabSize = _v.Settings.TabSize,
        };
    }

    public void OnSettingsChanged()
    {
        if (_disposed) return;
        var old = Options;
        Options = ReadOptions();
        ApplyLayout();
        if (old.ShowBreakpoints != Options.ShowBreakpoints) Invalidate(Sources.Breakpoints);
        Ruler.OnOptionsChanged();
        Minimap.OnOptionsChanged(old);
    }

    /// <summary>The diff editor turned the minimap off (or on) for this view.</summary>
    internal void OnAllowMinimapChanged() => OnSettingsChanged();

    public void OnThemeChanged()
    {
        if (_disposed) return;
        Brushes = DecorationBrushes.From(_v.Factory.Themes);
        Ruler.Control.InvalidateVisual();
        Minimap.OnThemeChanged();
    }

    private void OnHostSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyLayout();

    /// <summary>Places both controls and reserves their width in the text area's margin.</summary>
    internal void ApplyLayout()
    {
        if (_disposed) return;
        var o = Options;
        var rulerWidth = o.RulerEnabled ? Ruler.Width : 0;
        Ruler.Control.IsVisible = o.RulerEnabled;
        Ruler.Control.Width = Math.Max(1, rulerWidth);
        var minimapWidth = o.MinimapEnabled ? Minimap.WidthFor(_host.Bounds.Width - rulerWidth) : 0;
        Minimap.Control.IsVisible = minimapWidth > 0;
        if (minimapWidth > 0)
        {
            Minimap.Control.Width = minimapWidth;
            Minimap.Control.HorizontalAlignment = o.MinimapLeft ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;
            Minimap.Control.Margin = o.MinimapLeft ? default : new Thickness(0, 0, rulerWidth, 0);
        }
        var left = o.MinimapLeft ? minimapWidth : 0;
        var right = rulerWidth + (o.MinimapLeft ? 0 : minimapWidth);
        var margin = new Thickness(left, 0, right, 0);
        if (_v.TextArea.Margin != margin) _v.TextArea.Margin = margin;
        _v.Feature<StickyScrollFeature>()?.SetHorizontalInsets(left, right);
        Minimap.OnLayoutChanged();
    }

    // ---------------------------------------------------------------------------------------------- marks

    private void OnMarkersChanged(Sources source) => Invalidate(source & ~Sources.Cursors);

    private void OnDocumentChanged()
    {
        // offsets of the anchored marks (diagnostics, find matches) move with the text; recollect once typing pauses, not per keystroke
        _dirty |= Sources.Diagnostics | Sources.Highlights | Sources.Find | Sources.Cursors;
        if (!_editTimer.IsEnabled)
        {
            _editTimer.Interval = EditDelay;
            _editTimer.Start();
        }
    }

    private void OnEditTimer(object? sender, EventArgs e)
    {
        _editTimer.Stop();
        Collect();
    }

    /// <summary>Marks <paramref name="sources"/> stale and collects them in a moment (one pending collection, background priority).</summary>
    internal void Invalidate(Sources sources)
    {
        if (_disposed) return;
        _dirty |= sources;
        if (_collectPosted || _editTimer.IsEnabled) return; // an edit is pending: its timer collects everything
        _collectPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _collectPosted = false;
            if (!_disposed && !_editTimer.IsEnabled) Collect();
        }, DispatcherPriority.Background);
    }

    private void Collect()
    {
        if (_disposed) return;
        var dirty = _dirty;
        _dirty = Sources.None;
        if (dirty == Sources.None) return;
        var doc = _v.Editor.Document;
        if (doc is null) return;
        LastCollected = dirty;
        if (dirty.HasFlag(Sources.Diagnostics)) CollectDiagnostics(doc);
        if (dirty.HasFlag(Sources.Highlights)) CollectRanges(doc, Highlights, _v.Feature<HighlightFeature>()?.Ranges.Select(r => (r.Start, r.End, r.Strong ? MarkerKind.WordHighlightStrong : MarkerKind.WordHighlight)));
        if (dirty.HasFlag(Sources.Find)) CollectRanges(doc, FindMatches, _v.Feature<FindSearch>()?.Matches.Select(m => (m.Start, m.End, MarkerKind.FindMatch)));
        if (dirty.HasFlag(Sources.Scm)) CollectScm(doc.LineCount);
        if (dirty.HasFlag(Sources.Breakpoints)) CollectBreakpoints(doc.LineCount);
        if (dirty.HasFlag(Sources.Cursors)) CollectCursors(doc);
        MarksVersion++;
        Ruler.Refresh();
        Minimap.OnMarksChanged();
    }

    private void CollectDiagnostics(AvaloniaEdit.Document.TextDocument doc)
    {
        Diagnostics.Clear();
        if (_v.Feature<DiagnosticsFeature>() is not { } f) return;
        foreach (var (start, end, severity) in f.Marks)
        {
            var kind = severity switch
            {
                DiagnosticSeverity.Error => MarkerKind.Error,
                DiagnosticSeverity.Warning => MarkerKind.Warning,
                DiagnosticSeverity.Information => MarkerKind.Info,
                _ => MarkerKind.None, // hints are not on VS Code's ruler either
            };
            if (kind != MarkerKind.None) Add(doc, Diagnostics, start, end, kind);
        }
    }

    private static void CollectRanges(AvaloniaEdit.Document.TextDocument doc, List<LineMarker> into, IEnumerable<(int Start, int End, MarkerKind Kind)>? ranges)
    {
        into.Clear();
        if (ranges is null) return;
        foreach (var (start, end, kind) in ranges) Add(doc, into, start, end, kind);
    }

    /// <summary>Adds the lines a range covers; consecutive ranges on the same lines and kind become one mark.</summary>
    private static void Add(AvaloniaEdit.Document.TextDocument doc, List<LineMarker> into, int start, int end, MarkerKind kind)
    {
        var length = doc.TextLength;
        start = Math.Clamp(start, 0, length);
        end = Math.Clamp(Math.Max(start, end), 0, length);
        // same line as the previous mark's: no tree lookup
        if (into.Count > 0 && into[^1] is var last && last.Kind == kind)
        {
            var lastLine = doc.GetLineByNumber(last.EndLine + 1);
            if (start >= lastLine.Offset && end <= lastLine.EndOffset + lastLine.DelimiterLength) return;
        }
        var first = doc.GetLineByOffset(start).LineNumber - 1;
        var lastNumber = end == start ? first : doc.GetLineByOffset(end).LineNumber - 1;
        // a range ending at a line's start does not cover that line
        if (lastNumber > first && doc.GetLineByNumber(lastNumber + 1).Offset == end) lastNumber--;
        into.Add(new LineMarker(first, lastNumber, kind));
    }

    private void CollectScm(int lineCount)
    {
        Scm.Clear();
        if (_v.Feature<QuickDiffFeature>() is not { } f) return;
        foreach (var c in f.Changes)
        {
            var kind = QuickDiffFeature.KindOf(c) switch { QuickDiffKind.Added => MarkerKind.Added, QuickDiffKind.Deleted => MarkerKind.Deleted, _ => MarkerKind.Modified };
            var first = Math.Clamp(kind == MarkerKind.Deleted ? Math.Min(c.ModifiedStart, lineCount - 1) : c.ModifiedStart, 0, Math.Max(0, lineCount - 1));
            var last = kind == MarkerKind.Deleted ? first : Math.Clamp(c.ModifiedEnd - 1, first, Math.Max(0, lineCount - 1));
            Scm.Add(new LineMarker(first, last, kind));
        }
    }

    private void CollectBreakpoints(int lineCount)
    {
        Breakpoints.Clear();
        if (!Options.ShowBreakpoints || _v.Feature<DebugFeature>() is not { } f) return;
        foreach (var bp in f.Breakpoints.OrderBy(b => b.Line))
            if (bp.Line >= 0 && bp.Line < lineCount) Breakpoints.Add(new LineMarker(bp.Line, bp.Line, MarkerKind.Breakpoint));
    }

    private void CollectCursors(AvaloniaEdit.Document.TextDocument doc)
    {
        Cursors.Clear();
        var area = _v.TextArea;
        if (_v.MultiCursor.IsActive)
        {
            foreach (var caret in _v.MultiCursor.Current.Carets)
            {
                var l = LineOf(doc, caret.Active);
                if (Cursors.Count == 0 || Cursors[^1].Line != l) Cursors.Add(new LineMarker(l, l, MarkerKind.Cursor));
            }
            return;
        }
        var line = area.Caret.Line - 1;
        Cursors.Add(new LineMarker(line, line, MarkerKind.Cursor));
    }

    private static int LineOf(AvaloniaEdit.Document.TextDocument doc, int offset) => doc.GetLineByOffset(Math.Clamp(offset, 0, doc.TextLength)).LineNumber - 1;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Ruler.Dispose();
        Minimap.Dispose();
        _d.Dispose();
        _v.TextArea.Margin = default;
    }
}

/// <summary>The settings both decorations read, as one immutable value.</summary>
internal sealed record DecorationOptions
{
    public bool MinimapEnabled { get; init; }
    public bool MinimapLeft { get; init; }
    public bool RenderCharacters { get; init; } = true;
    public int MaxColumn { get; init; } = 120;
    public bool SliderAlways { get; init; }
    public int Scale { get; init; } = 1;
    public bool RulerEnabled { get; init; } = true;
    public bool RulerBorder { get; init; } = true;
    public bool HideCursor { get; init; }
    public bool ShowBreakpoints { get; init; }
    public int TabSize { get; init; } = 4;
}

/// <summary>The theme colours of the ruler and the minimap (VS Code's <c>editorOverviewRuler.*</c>, <c>minimap*</c> keys).</summary>
internal sealed class DecorationBrushes
{
    public required IBrush[] Markers { get; init; } // by MarkerKind
    public required IBrush[] MinimapMarks { get; init; } // by MarkerKind (find, selection, errors, warnings, gutter)
    public required IBrush RulerBorder { get; init; }
    public required IBrush? RulerBackground { get; init; }
    public required IBrush Slider { get; init; }
    public required IBrush SliderHover { get; init; }
    public required IBrush SliderActive { get; init; }

    public static DecorationBrushes From(Theming.ColorThemeService themes)
    {
        IBrush B(string key) => new ImmutableSolidColorBrush(EditorPalette.ToColor(themes.GetColor(key)));
        IBrush A(string key, double alpha)
        {
            var c = themes.GetColor(key);
            return new ImmutableSolidColorBrush(Color.FromArgb((byte)Math.Round(c.A * alpha), c.R, c.G, c.B));
        }
        var markers = new IBrush[(int)MarkerKind.Cursor + 1];
        markers[(int)MarkerKind.None] = Avalonia.Media.Brushes.Transparent;
        markers[(int)MarkerKind.Added] = B("editorOverviewRuler.addedForeground");
        markers[(int)MarkerKind.Modified] = B("editorOverviewRuler.modifiedForeground");
        markers[(int)MarkerKind.Deleted] = B("editorOverviewRuler.deletedForeground");
        markers[(int)MarkerKind.Breakpoint] = B("debugIcon.breakpointForeground");
        markers[(int)MarkerKind.WordHighlight] = B("editorOverviewRuler.wordHighlightForeground");
        markers[(int)MarkerKind.WordHighlightStrong] = B("editorOverviewRuler.wordHighlightStrongForeground");
        markers[(int)MarkerKind.FindMatch] = B("editorOverviewRuler.findMatchForeground");
        markers[(int)MarkerKind.Info] = B("editorOverviewRuler.infoForeground");
        markers[(int)MarkerKind.Warning] = B("editorOverviewRuler.warningForeground");
        markers[(int)MarkerKind.Error] = B("editorOverviewRuler.errorForeground");
        markers[(int)MarkerKind.Cursor] = B("editorCursor.foreground");
        var mini = new IBrush[(int)MarkerKind.Cursor + 1];
        Array.Fill(mini, Avalonia.Media.Brushes.Transparent);
        mini[(int)MarkerKind.Added] = B("minimapGutter.addedBackground");
        mini[(int)MarkerKind.Modified] = B("minimapGutter.modifiedBackground");
        mini[(int)MarkerKind.Deleted] = B("minimapGutter.deletedBackground");
        mini[(int)MarkerKind.FindMatch] = A("minimap.findMatchHighlight", 0.6);
        mini[(int)MarkerKind.Warning] = A("minimap.warningHighlight", 0.6);
        mini[(int)MarkerKind.Error] = A("minimap.errorHighlight", 0.6);
        mini[(int)MarkerKind.Cursor] = A("minimap.selectionHighlight", 0.7);
        var background = themes.GetColor("editorOverviewRuler.background");
        return new DecorationBrushes
        {
            Markers = markers,
            MinimapMarks = mini,
            RulerBorder = B("editorOverviewRuler.border"),
            RulerBackground = background.A == 0 ? null : B("editorOverviewRuler.background"),
            Slider = B("minimapSlider.background"),
            SliderHover = B("minimapSlider.hoverBackground"),
            SliderActive = B("minimapSlider.activeBackground"),
        };
    }
}
