using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Editor.View;
using LogicalDirection = AvaloniaEdit.Document.LogicalDirection;

namespace CsCode.Editor.Features;

/// <summary>
/// VS Code's colour decorators and colour picker. Every <see cref="IDocumentColorProvider"/> of the document (a language server's
/// <c>textDocument/documentColor</c>; with <c>editor.defaultColorDecorators</c> also the built-in <see cref="DefaultColorProvider"/>) is asked
/// for the document's colours 500 ms after the text stops changing (at once when providers or settings change), off the UI thread with a
/// deadline, latest-wins; an answer for older text is dropped. A small square of each colour is drawn in front of its text (bordered, over a
/// checkerboard when translucent, 0.8 em, VS Code's metrics), anchored to the text so it follows edits until the next answer; at most
/// <c>editor.colorDecoratorsLimit</c> are kept. Clicking a swatch (or resting the pointer on it, <c>editor.colorDecoratorsActivatedOn</c>)
/// or <c>editor.action.showOrFocusStandaloneColorPicker</c> opens the picker (<see cref="ColorPickerSession"/>).
/// </summary>
internal sealed class ColorDecoratorFeature : ISettingsAware, IThemeAware, IDisposable
{
    public const string EnabledKey = "editor.colorDecorators";
    public const string LimitKey = "editor.colorDecoratorsLimit";
    public const string ActivatedOnKey = "editor.colorDecoratorsActivatedOn";
    public const string DefaultDecoratorsKey = "editor.defaultColorDecorators";
    public const string ShowPickerCommand = "editor.action.showOrFocusStandaloneColorPicker";
    public const int DefaultLimit = 500;
    /// <summary>The limit's ceiling (VS Code allows 1,000,000): it bounds the anchors and swatches a view holds.</summary>
    public const int MaxLimit = 10_000;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private static ConfigurationProperty P(string key, object value, string description, string schema) =>
        new(key, JsonSerializer.SerializeToElement(value), description, JsonDocument.Parse(schema).RootElement.Clone());

    internal static IReadOnlyList<ConfigurationProperty> Properties { get; } =
    [
        P(EnabledKey, true, "Controls whether the editor should render the inline color decorators and color picker.", """{"type":"boolean"}"""),
        P(LimitKey, DefaultLimit, $"Controls the max number of color decorators that can be rendered in an editor at once (at most {MaxLimit:N0} are kept).",
            """{"type":"integer","minimum":1,"maximum":1000000}"""),
        P(ActivatedOnKey, "clickAndHover", "Controls the condition to make a color picker appear from a color decorator: clicking it, resting the pointer on it, or both.",
            """{"enum":["clickAndHover","hover","click"]}"""),
        P(DefaultDecoratorsKey, "auto",
            "Controls whether inline color decorations are shown using the default document color provider (CSS #rgb, #rrggbb, rgb(), hsl() colors in any file): auto (only when no language server provides colors for the file), always, or never.",
            """{"enum":["auto","always","never"]}"""),
    ];

    /// <summary>A colour drawn in the text: its range follows edits (anchors that survive deletion; a range deleted whole collapses).</summary>
    internal sealed class Placement(TextAnchor start, TextAnchor end, ColorInformation color, IDocumentColorProvider provider)
    {
        public TextAnchor Start { get; set; } = start;
        public TextAnchor End { get; set; } = end;
        public ColorInformation Color { get; set; } = color;
        public IDocumentColorProvider Provider { get; } = provider;
        public bool IsLive => End.Offset > Start.Offset;
    }

    private readonly record struct Options(bool Enabled, int Limit, string ActivatedOn, string DefaultMode)
    {
        public bool ClickOpens => ActivatedOn != "hover";
        public bool HoverOpens => ActivatedOn != "click";
    }

    private readonly CodeEditorControl _v;
    private readonly Generator _generator;
    private readonly DefaultColorProvider _default;
    private readonly DisposableStore _d = new();
    private readonly Timer _timer;
    private CancellationTokenSource? _request, _hoverOpen;
    private Placement[] _placed = [];
    private readonly Dictionary<IDocumentColorProvider, Placement[]> _sets = new(ReferenceEqualityComparer.Instance);
    private Options _options;
    private ColorPickerSession? _session;
    private bool _disposed;

    public ColorDecoratorFeature(CodeEditorControl view)
    {
        _v = view;
        _default = new DefaultColorProvider(view.Factory.Worker);
        _timer = new Timer(static s => ((ColorDecoratorFeature)s!).OnTimer(), this, Timeout.Infinite, Timeout.Infinite);
        _options = ReadOptions();
        _generator = new Generator(this);
        // after the code lens zone and the inlay hints (they open the line / are at least as close to the text), before AvaloniaEdit's own
        // generators (folding, whitespace and control-character markers): at one offset construction stops at the first element that covers text
        var generators = view.TextView.ElementGenerators;
        var at = 0;
        while (at < generators.Count && generators[at].GetType().Namespace?.StartsWith("AvaloniaEdit", StringComparison.Ordinal) != true) at++;
        generators.Insert(at, _generator);
        view.TextArea.AddHandler(InputElement.PointerPressedEvent, OnTextAreaPressed, RoutingStrategies.Tunnel);
        view.TextArea.AddHandler(InputElement.KeyDownEvent, OnTextAreaKeyDown, RoutingStrategies.Tunnel);
        _d.Add(view.Document.Changed.Subscribe(_ => Schedule(Debounce)));
        _d.Add(view.Services.Features.ProvidersChanged.Subscribe(t =>
        {
            if (t == typeof(IDocumentColorProvider)) _v.Post(() => Schedule(TimeSpan.Zero));
        }));
        _d.Add(Disposable.Create(() =>
        {
            view.TextArea.RemoveHandler(InputElement.PointerPressedEvent, OnTextAreaPressed);
            view.TextArea.RemoveHandler(InputElement.KeyDownEvent, OnTextAreaKeyDown);
            view.TextView.ElementGenerators.Remove(_generator);
        }));
        Schedule(TimeSpan.Zero);
    }

    /// <summary>The colours drawn now: (offset of the swatch, the colour) in document order (tests).</summary>
    internal IReadOnlyList<(int Offset, ColorInformation Color)> Placements => [.. _placed.Where(p => p.IsLive).Select(p => (p.Start.Offset, p.Color))];

    /// <summary>The open picker, or null.</summary>
    internal ColorPickerSession? Session => _session is { IsOpen: true } s ? s : null;

    /// <summary>Completes when the request in flight has been placed (tests).</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    private Options ReadOptions()
    {
        var cfg = _v.Services.Config;
        var language = _v.Document.LanguageId;
        var mode = cfg.GetOr(DefaultDecoratorsKey, language, "auto");
        var on = cfg.GetOr(ActivatedOnKey, language, "clickAndHover");
        return new Options(
            cfg.GetOr(EnabledKey, language, true),
            Math.Clamp(cfg.GetOr(LimitKey, language, DefaultLimit), 1, MaxLimit),
            on is "hover" or "click" ? on : "clickAndHover",
            mode is "always" or "never" ? mode : "auto");
    }

    public void OnSettingsChanged()
    {
        var old = _options;
        _options = ReadOptions();
        if (!_options.Enabled)
        {
            _session?.Commit();
            Clear();
        }
        else if (old != _options) Schedule(TimeSpan.Zero);
    }

    public void OnThemeChanged()
    {
        _session?.ApplyTheme();
        _v.TextView.Redraw();
    }

    private void Clear()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _request?.Cancel();
        _sets.Clear();
        if (_placed.Length == 0) return;
        _placed = [];
        _v.TextView.Redraw();
    }

    // ---- asking the providers -----------------------------------------------------------------------------------------

    /// <summary>Asks again after <paramref name="delay"/> (a later call replaces an earlier one). A keystroke costs one timer change, no allocation.</summary>
    private void Schedule(TimeSpan delay)
    {
        if (_disposed) return;
        if (!_options.Enabled)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }
        _timer.Change(delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : delay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer() => _v.Post(() =>
    {
        if (!_disposed) Pending = RefreshAsync();
    });

    /// <summary>The providers that serve this document now: the registered ones, plus the built-in one as <c>editor.defaultColorDecorators</c> says.</summary>
    private List<IDocumentColorProvider> Providers()
    {
        var list = new List<IDocumentColorProvider>(_v.Services.Features.GetProviders<IDocumentColorProvider>(_v.Document));
        if (_options.DefaultMode == "always" || (_options.DefaultMode == "auto" && list.Count == 0)) list.Add(_default);
        return list;
    }

    private async Task RefreshAsync()
    {
        if (_disposed || !_options.Enabled) return;
        var doc = _v.Document;
        var providers = Providers();
        foreach (var gone in _sets.Keys.Where(k => !providers.Contains(k)).ToList()) _sets.Remove(gone);
        if (providers.Count == 0)
        {
            Clear();
            return;
        }
        _request?.Cancel();
        _request?.Dispose();
        var cts = _request = new CancellationTokenSource();
        var version = doc.Version;
        var pending = providers.Select(p => (Provider: p, Call: FeatureSupport.CallAsync(_v, c => p.ProvideDocumentColorsAsync(doc, c), cts.Token, Deadline))).ToList();
        // Each answer is placed as it arrives, replacing that provider's colours only: a slow or hung provider neither delays the others nor
        // makes the colours it showed before flicker away (they keep following the text until it answers or its deadline passes).
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending.Select(p => p.Call)).ConfigureAwait(true);
            var index = pending.FindIndex(p => ReferenceEquals(p.Call, done));
            var provider = pending[index].Provider;
            pending.RemoveAt(index);
            if (_disposed || cts.IsCancellationRequested || doc.Version != version || !_options.Enabled) return; // older text: a newer request is scheduled
            _sets[provider] = AnchorAll(await done.ConfigureAwait(true) ?? [], provider);
            Merge();
        }
    }

    /// <summary>Anchors one provider's answer (UI thread): its first colours in document order, at most the limit.</summary>
    private Placement[] AnchorAll(IReadOnlyList<ColorInformation> colors, IDocumentColorProvider provider)
    {
        var doc = _v.Document;
        var length = doc.Buffer.TextLength;
        var spans = new List<(int Start, int End, ColorInformation Color)>(colors.Count);
        foreach (var c in colors)
        {
            if (doc.Validate(c.Range.Start) != c.Range.Start || doc.Validate(c.Range.End) != c.Range.End) continue; // not in this text
            var s = doc.OffsetAt(c.Range.Start);
            var e = doc.OffsetAt(c.Range.End);
            if (e > s && e <= length) spans.Add((s, e, c));
        }
        spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        var placed = new List<Placement>(Math.Min(spans.Count, _options.Limit));
        foreach (var (s, e, color) in spans)
        {
            if (placed.Count > 0 && placed[^1].Start.Offset == s) continue; // one swatch per place
            placed.Add(new Placement(Anchor(s, AnchorMovementType.AfterInsertion), Anchor(e, AnchorMovementType.BeforeInsertion), color, provider));
            if (placed.Count >= _options.Limit) break;
        }
        return [.. placed];
    }

    /// <summary>
    /// What is drawn (UI thread): every provider's colours in document order, at most the limit; the built-in provider's only where no other
    /// provider has a colour (with <c>always</c> both serve the document, and a colour both found is drawn once).
    /// </summary>
    private void Merge()
    {
        var all = _sets.Values.SelectMany(x => x).Where(p => p.IsLive)
            .OrderBy(p => p.Start.Offset).ThenBy(p => ReferenceEquals(p.Provider, _default) ? 1 : 0).ToList();
        // the other providers' ranges by start, with the furthest end reached so far: [s, e) overlaps one of them iff a range starting
        // before e reaches past s
        var starts = new List<int>();
        var reach = new List<int>();
        foreach (var p in all)
        {
            if (ReferenceEquals(p.Provider, _default)) continue;
            starts.Add(p.Start.Offset);
            reach.Add(Math.Max(reach.Count > 0 ? reach[^1] : int.MinValue, p.End.Offset));
        }
        var merged = new List<Placement>(Math.Min(all.Count, _options.Limit));
        foreach (var p in all)
        {
            var (s, e) = (p.Start.Offset, p.End.Offset);
            if (merged.Count > 0 && merged[^1].Start.Offset == s) continue; // one swatch per place
            if (ReferenceEquals(p.Provider, _default) && starts.Count > 0)
            {
                var before = starts.BinarySearch(e);
                before = before < 0 ? ~before : before; // ranges starting at or after e cannot overlap
                while (before > 0 && starts[before - 1] >= e) before--;
                if (before > 0 && reach[before - 1] > s) continue;
            }
            merged.Add(p);
            if (merged.Count >= _options.Limit) break;
        }
        _placed = [.. merged];
        _v.TextView.Redraw();
    }

    private TextAnchor Anchor(int offset, AnchorMovementType movement)
    {
        var anchor = _v.Document.Buffer.CreateAnchor(offset);
        anchor.MovementType = movement;
        anchor.SurviveDeletion = true; // its offset stays readable (binary search); a colour deleted whole collapses and is skipped
        return anchor;
    }

    /// <summary>Index of the first placement whose swatch is at or after <paramref name="offset"/> (anchors keep their order under edits).</summary>
    private int FirstAtOrAfter(int offset)
    {
        var placed = _placed;
        int lo = 0, hi = placed.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (placed[mid].Start.Offset < offset) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>The live placement whose text contains <paramref name="offset"/> (or ends at it), or null.</summary>
    private Placement? At(int offset)
    {
        var placed = _placed;
        for (var i = Math.Min(FirstAtOrAfter(offset + 1), placed.Length) - 1; i >= 0; i--)
        {
            var p = placed[i];
            if (!p.IsLive) continue;
            return p.Start.Offset <= offset && offset <= p.End.Offset ? p : null;
        }
        return null;
    }

    // ---- the picker -------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>editor.action.showOrFocusStandaloneColorPicker</c>: focuses the open picker, else opens it for the colour at the caret, else over the
    /// selection (VS Code's standalone picker: the presentation chosen is written there).
    /// </summary>
    public void ShowOrFocus()
    {
        if (_disposed) return;
        if (Session is { } open)
        {
            open.Focus();
            return;
        }
        var caret = _v.Editor.CaretOffset;
        if (At(caret) is { } placement)
        {
            Open(placement, hover: false);
            return;
        }
        if (_v.Document.IsReadOnly) return;
        var selection = _v.TextArea.Selection;
        var (start, end) = selection.IsEmpty ? (caret, caret) : (selection.SurroundingSegment.Offset, selection.SurroundingSegment.EndOffset);
        var provider = _v.Services.Features.GetProviders<IDocumentColorProvider>(_v.Document).FirstOrDefault() ?? _default;
        var doc = _v.Document;
        var range = new TextRange(doc.PositionAt(start), doc.PositionAt(end));
        var white = new ColorInformation(range, 1, 1, 1, 1);
        if (end > start && ColorFormats.TryParse(doc.Buffer.GetText(start, end - start), out var r, out var g, out var b, out var a))
            white = new ColorInformation(range, r, g, b, a); // a selected colour starts the picker at that colour
        OpenSession(provider, white, start, end, null, hover: false);
    }

    private void Open(Placement placement, bool hover)
    {
        if (!placement.IsLive) return;
        var doc = _v.Document;
        var (start, end) = (placement.Start.Offset, placement.End.Offset);
        var color = placement.Color with { Range = new TextRange(doc.PositionAt(start), doc.PositionAt(end)) };
        OpenSession(placement.Provider, color, start, end, placement, hover);
    }

    private Placement? _sessionPlacement;

    private void OpenSession(IDocumentColorProvider provider, ColorInformation color, int start, int end, Placement? placement, bool hover)
    {
        _session?.Commit();
        _hoverOpen?.Cancel();
        _sessionPlacement = placement;
        var session = _session = new ColorPickerSession(this, _v, provider, color, start, end, hover);
        if (!hover) session.Focus();
    }

    /// <summary>The session applied a colour: the swatch shows it at once (the providers' next answer replaces it anyway).</summary>
    internal void OnPicked(ColorPickerSession session, int start, int end, ColorInformation color)
    {
        if (!ReferenceEquals(session, _session) || _sessionPlacement is not { } p || !_placed.Contains(p)) return;
        p.Start = Anchor(start, AnchorMovementType.AfterInsertion);
        p.End = Anchor(end, AnchorMovementType.BeforeInsertion);
        p.Color = color;
        _v.TextView.Redraw(start, Math.Max(0, end - start));
    }

    internal void OnSessionClosed(ColorPickerSession session)
    {
        if (!ReferenceEquals(session, _session)) return;
        _session = null;
        _sessionPlacement = null;
    }

    // ---- input ------------------------------------------------------------------------------------------------------

    /// <summary>A press in the text (not on a swatch, which handles its own) closes the picker, keeping what it applied.</summary>
    private void OnTextAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_session is { IsOpen: true } s && e.Source is not ColorSwatch) s.Commit();
    }

    /// <summary>Escape closes the picker and undoes its edits; other keys close a picker the hover opened (typing goes on).</summary>
    private void OnTextAreaKeyDown(object? sender, KeyEventArgs e)
    {
        if (_session is not { IsOpen: true } s || e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        if (e.Key == Key.Escape)
        {
            s.Revert();
            e.Handled = true;
        }
        else if (s.IsHoverOpened) s.Commit();
    }

    private void OnSwatchPressed(Placement placement, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        e.Handled = true; // never moves the caret
        if (!_options.ClickOpens) return;
        if (Session is { } open && ReferenceEquals(_sessionPlacement, placement))
        {
            open.Focus(); // a click on the hover-opened picker's swatch keeps it, now for good
            return;
        }
        Open(placement, hover: false);
    }

    private void OnSwatchEntered(Placement placement)
    {
        if (Session is { } open && ReferenceEquals(_sessionPlacement, placement))
        {
            open.PointerBack();
            return;
        }
        if (!_options.HoverOpens || Session is { IsHoverOpened: false }) return; // a picker in use stays until it is closed
        _hoverOpen?.Cancel();
        var cts = _hoverOpen = new CancellationTokenSource();
        _ = Task.Delay(HoverDelay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _v.Post(() =>
            {
                if (!cts.IsCancellationRequested && _placed.Contains(placement)) Open(placement, hover: true);
            });
        }, TaskScheduler.Default);
    }

    private void OnSwatchExited(Placement placement)
    {
        _hoverOpen?.Cancel();
        if (Session is { } open && ReferenceEquals(_sessionPlacement, placement)) open.PointerLeft();
    }

    /// <summary>Opens the picker for the swatch at <paramref name="offset"/> as a click does (tests).</summary>
    internal ColorPickerSession? OpenAt(int offset, bool hover = false)
    {
        var i = FirstAtOrAfter(offset);
        if (i >= _placed.Length || _placed[i].Start.Offset != offset) return null;
        Open(_placed[i], hover);
        return Session;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        _request?.Cancel();
        _hoverOpen?.Cancel();
        _session?.Commit();
        _d.Dispose();
        _sets.Clear();
        _placed = [];
    }

    // ---- drawing ----------------------------------------------------------------------------------------------------

    private IBrush SwatchBorder => _v.Factory.Themes.Current.Kind is ThemeKind.Light or ThemeKind.HighContrastLight ? Brushes.Black : new ImmutableSolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));

    private sealed class Generator(ColorDecoratorFeature owner) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            var placed = owner._placed;
            if (placed.Length == 0) return -1;
            var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
            for (var i = owner.FirstAtOrAfter(startOffset); i < placed.Length; i++)
            {
                var p = placed[i];
                var offset = p.Start.Offset;
                if (offset > end) return -1;
                if (p.IsLive) return offset;
            }
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var placed = owner._placed;
            for (var i = owner.FirstAtOrAfter(offset); i < placed.Length && placed[i].Start.Offset == offset; i++)
            {
                var p = placed[i];
                if (!p.IsLive) continue;
                var c = p.Color;
                var swatch = new ColorSwatch(
                    Color.FromArgb(ColorFormats.Byte(c.Alpha), ColorFormats.Byte(c.Red), ColorFormats.Byte(c.Green), ColorFormats.Byte(c.Blue)),
                    owner.SwatchBorder, owner._v.Editor.FontSize);
                swatch.PointerPressed += (_, e) => owner.OnSwatchPressed(p, e);
                swatch.PointerEntered += (_, _) => owner.OnSwatchEntered(p);
                swatch.PointerExited += (_, _) => owner.OnSwatchExited(p);
                return new SwatchElement(swatch, CurrentContext.VisualLine);
            }
            return null;
        }
    }

    /// <summary>
    /// The swatch in the line: no text of its own (document length 0) and one caret stop on each side for the same offset, so the caret is
    /// never stopped twice there: coming from the left it stops before the swatch (where a click or typing puts it) and the next key goes past
    /// the colour's first character; coming from the right it stops after the swatch, and the next key goes before the character in front.
    /// </summary>
    internal sealed class SwatchElement(ColorSwatch swatch, VisualLine line) : InlineObjectElement(0, swatch)
    {
        public Color Color => swatch.Fill;

        // At a line start nothing may add the line's own stop in front of the swatch (Left from after it goes to the line above).
        public override bool HandlesLineBorders => RelativeTextOffset == 0;

        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode)
        {
            var before = VisualColumn;
            var after = VisualColumn + 1;
            if (direction == LogicalDirection.Forward)
            {
                if (visualColumn < before) return before;
                if (visualColumn != before) return -1;
                var next = TextUtilities.GetNextCaretPosition(line.Document, line.StartOffset + RelativeTextOffset, direction, mode);
                if (next < 0 || next > line.LastDocumentLine.EndOffset) return after;
                var column = line.GetVisualColumn(next - line.StartOffset);
                return column > after ? column : after;
            }
            if (visualColumn > after) return after;
            if (visualColumn != after) return -1;
            var previous = TextUtilities.GetNextCaretPosition(line.Document, line.StartOffset + RelativeTextOffset, direction, mode);
            if (previous < line.StartOffset) return -1; // the line above: the visual line moves there
            var back = line.GetVisualColumn(previous - line.StartOffset);
            return back < before ? back : -1;
        }
    }
}

/// <summary>
/// A colour decorator: a square of 0.8 em with a 0.1 em border, 0.2 em apart from the text (VS Code's metrics), its bottom on the text's
/// baseline; a translucent colour shows over a checkerboard. It takes the pointer (a click opens the picker) and shows a hand.
/// </summary>
internal sealed class ColorSwatch : Control
{
    private readonly IBrush _border;
    private readonly double _em;

    public ColorSwatch(Color fill, IBrush border, double fontSize)
    {
        Fill = fill;
        _border = border;
        _em = fontSize;
        var side = Math.Round(0.2 * fontSize);
        var top = Math.Round(0.1 * fontSize);
        Margin = new Thickness(side, top, side, 0);
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        // AvaloniaEdit puts an inline object's baseline offset on the text's baseline (0 when unset, which hung the swatch below the text and
        // made its line taller): the whole height, so the square's bottom sits on the baseline as an inline-block's does
        TextBlock.SetBaselineOffset(this, top + Side);
        Cursor = new Cursor(StandardCursorType.Hand);
        Avalonia.Automation.AutomationProperties.SetName(this, "Color");
    }

    public Color Fill { get; }

    /// <summary>The square's side in DIPs.</summary>
    public double Side => Math.Round(0.8 * _em);

    protected override Size MeasureOverride(Size availableSize) => new(Side, Side);

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(0, 0, Side, Side);
        var thickness = Math.Max(1, Math.Round(0.1 * _em));
        context.FillRectangle(_border, rect);
        var inner = rect.Deflate(thickness);
        if (inner.Width <= 0 || inner.Height <= 0) return;
        if (Fill.A < 255) ColorPickerWidget.DrawCheckerboard(context, inner, Math.Max(2, inner.Width / 2));
        context.FillRectangle(new ImmutableSolidColorBrush(Fill), inner);
    }
}
