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
        view.Editor.PointerMoved += OnPointerMoved;
        view.Editor.PointerExited += OnPointerExited;
        _d.Add(view.Document.Changed.Subscribe(_ => Schedule(Debounce)));
        _d.Add(view.Services.Features.ProvidersChanged.Subscribe(t =>
        {
            if (t == typeof(IDocumentColorProvider)) _v.Post(() => Schedule(TimeSpan.Zero));
        }));
        _d.Add(Disposable.Create(() =>
        {
            view.TextArea.RemoveHandler(InputElement.PointerPressedEvent, OnTextAreaPressed);
            view.TextArea.RemoveHandler(InputElement.KeyDownEvent, OnTextAreaKeyDown);
            view.Editor.PointerMoved -= OnPointerMoved;
            view.Editor.PointerExited -= OnPointerExited;
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
        var snapshot = doc.Snapshot(); // the text the answers are for
        var version = snapshot.Version;
        var limit = _options.Limit;
        var pending = providers.Select(p => (Provider: p, Call: FeatureSupport.CallAsync(_v, c => p.ProvideDocumentColorsAsync(doc, c), cts.Token, Deadline))).ToList();
        bool Stale() => _disposed || cts.IsCancellationRequested || doc.Version != version || !_options.Enabled; // a newer request is scheduled
        try
        {
            // Each answer is placed as it arrives, replacing that provider's colours only: a slow or hung provider neither delays the others
            // nor makes the colours it showed before flicker away (they keep following the text until it answers or its deadline passes).
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending.Select(p => p.Call)).ConfigureAwait(true);
                var index = pending.FindIndex(p => ReferenceEquals(p.Call, done));
                var provider = pending[index].Provider;
                pending.RemoveAt(index);
                if (Stale()) return;
                var colors = await done.ConfigureAwait(true) ?? [];
                // ranges to offsets against the snapshot, sorted and cut to the limit, off the UI thread
                var spans = colors.Count == 0 ? [] : await Task.Run(() => Spans(snapshot, colors, limit), cts.Token).ConfigureAwait(true);
                if (Stale()) return;
                var placed = new Placement[spans.Length];
                for (var i = 0; i < spans.Length; i++)
                {
                    if (i > 0 && i % AnchorSlice == 0)
                    {
                        await Task.Yield(); // a big answer is anchored a slice per dispatcher turn; the text must not move meanwhile
                        if (Stale()) return;
                    }
                    var (start, end, color) = spans[i];
                    placed[i] = new Placement(Anchor(start, AnchorMovementType.AfterInsertion), Anchor(end, AnchorMovementType.BeforeInsertion), color, provider);
                }
                _sets[provider] = placed;
                Merge();
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer request
        }
    }

    /// <summary>Colours anchored per dispatcher turn: a thousand anchors take well under the UI thread's 4 ms budget.</summary>
    private const int AnchorSlice = 1000;

    /// <summary>One provider's colours as offsets in <paramref name="snapshot"/>: valid, non-empty, one per start, in order, at most <paramref name="limit"/>.</summary>
    private static (int Start, int End, ColorInformation Color)[] Spans(Documents.DocumentSnapshot snapshot, IReadOnlyList<ColorInformation> colors, int limit)
    {
        var spans = new List<(int Start, int End, ColorInformation Color)>(colors.Count);
        foreach (var c in colors)
        {
            if (snapshot.Validate(c.Range.Start) != c.Range.Start || snapshot.Validate(c.Range.End) != c.Range.End) continue; // not in this text
            var s = snapshot.OffsetAt(c.Range.Start);
            var e = snapshot.OffsetAt(c.Range.End);
            if (e > s) spans.Add((s, e, c));
        }
        spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        var result = new List<(int Start, int End, ColorInformation Color)>(Math.Min(spans.Count, limit));
        foreach (var span in spans)
        {
            if (result.Count > 0 && result[^1].Start == span.Start) continue; // one swatch per place
            result.Add(span);
            if (result.Count >= limit) break;
        }
        return [.. result];
    }

    /// <summary>
    /// What is drawn (UI thread): every provider's colours in document order, at most the limit; the built-in provider's only where no other
    /// provider has a colour (with <c>always</c> both serve the document, and a colour both found is drawn once).
    /// </summary>
    private void Merge()
    {
        // offsets read once (an anchor's offset is a walk up its tree); one provider's set is in order already
        var live = _sets.Values.SelectMany(x => x).Where(p => p.IsLive)
            .Select(p => (Placement: p, Start: p.Start.Offset, End: p.End.Offset, Default: ReferenceEquals(p.Provider, _default)));
        var all = _sets.Count == 1 ? live.ToList() : [.. live.OrderBy(x => x.Start).ThenBy(x => x.Default ? 1 : 0)];
        // the other providers' ranges by start, with the furthest end reached so far: [s, e) overlaps one of them iff a range starting
        // before e reaches past s
        var starts = new List<int>();
        var reach = new List<int>();
        foreach (var x in all)
        {
            if (x.Default) continue;
            starts.Add(x.Start);
            reach.Add(Math.Max(reach.Count > 0 ? reach[^1] : int.MinValue, x.End));
        }
        var merged = new List<Placement>(Math.Min(all.Count, _options.Limit));
        var lastStart = -1;
        foreach (var (p, s, e, isDefault) in all)
        {
            if (s == lastStart) continue; // one swatch per place
            if (isDefault && starts.Count > 0)
            {
                var before = starts.BinarySearch(e);
                before = before < 0 ? ~before : before; // ranges starting at or after e cannot overlap
                while (before > 0 && starts[before - 1] >= e) before--;
                if (before > 0 && reach[before - 1] > s) continue;
            }
            merged.Add(p);
            lastStart = s;
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
        else _v.Feature<HoverFeature>()?.Hide(); // the picker is the hover over a colour
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

    /// <summary>The pointer came to rest on a colour (its swatch or its text).</summary>
    private void OnColorEntered(Placement placement)
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

    private void OnColorExited(Placement placement)
    {
        _hoverOpen?.Cancel();
        if (Session is { } open && ReferenceEquals(_sessionPlacement, placement)) open.PointerLeft();
    }

    private Placement? _textHover;

    /// <summary>
    /// Resting the pointer on a colour's text opens its picker as resting on its swatch does (VS Code shows the picker in the hover of a
    /// colour); the editor's hover stays away while it is up. Costs a position lookup per move only while there are colours.
    /// </summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_placed.Length == 0 || !_options.HoverOpens)
        {
            OnPointerExited(sender, e);
            return;
        }
        Placement? over = null;
        if (_v.Editor.GetPositionFromPoint(e.GetPosition(_v.Editor)) is { IsAtEndOfLine: false } p && p.Line >= 1 && p.Line <= _v.Document.LineCount)
        {
            var offset = _v.Document.OffsetAt(new TextPosition(p.Line - 1, p.Column - 1));
            if (At(offset) is { } at && offset < at.End.Offset) over = at;
        }
        if (ReferenceEquals(over, _textHover)) return;
        if (_textHover is { } left) OnColorExited(left);
        _textHover = over;
        if (over is not null) OnColorEntered(over);
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_textHover is not { } left) return;
        _textHover = null;
        OnColorExited(left);
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
                swatch.PointerPressed += (_, e) => owner.OnSwatchPressed(p, e); // resting on it is tracked with the colour's text (OnPointerMoved)
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
