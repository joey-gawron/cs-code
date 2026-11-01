using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// Code lens: the commands a language server (or the Test Explorer) puts above a line — "3 references", "Run Test | Debug Test" — drawn as a
/// thin line of small text in <c>editorCodeLens.foreground</c> at the line's indentation, in the space a <see cref="LineZones"/> opens above it
/// (so caret, selection, hit-testing and folding stay those of the text). A title with a command is a link: a click runs it
/// (<see cref="CodeLensCommands"/>).
/// <list type="bullet">
/// <item>Asked 500 ms after the text stops changing, when a provider comes or goes (<c>workspace/codeLens/refresh</c> re-registers the
/// server's provider) or says its lenses changed, off the UI thread with a deadline; an answer for an older version of the text is dropped (the
/// next one is already scheduled).</item>
/// <item>Each lens is anchored to its position, so it follows edits, and the lenses on screen stay until the next answer replaces them: typing
/// never makes the zones (and the text under them) jump. A new lens that still needs resolving shows the title of the lens it replaces on the
/// same line until its own arrives. When the set changes, the first visible line keeps its place on screen.</item>
/// <item>Lenses without a command are resolved (<c>codeLens/resolve</c>) only once they are near the viewport, once each.</item>
/// <item><c>editor.codeLens</c> false removes them; <c>editor.codeLensFontSize</c> / <c>editor.codeLensFontFamily</c> pick the font (90 % of the
/// editor's size and the editor's family by default, as in VS Code). At most <see cref="MaxLenses"/> are kept.</item>
/// </list>
/// </summary>
internal sealed partial class CodeLensFeature : ILineZoneSource, ISettingsAware, IThemeAware, IDisposable
{
    public const int MaxLenses = 5000;
    private const int ResolveMargin = 20; // lines above/below the viewport whose lenses are resolved too
    internal static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ResolveDelay = TimeSpan.FromMilliseconds(100);

    internal sealed class Item(TextAnchor anchor, CodeLens lens, ICodeLensProvider provider, int order)
    {
        public TextAnchor Anchor { get; } = anchor;
        public CodeLens Lens { get; set; } = lens;
        public ICodeLensProvider Provider { get; } = provider;
        public int Order { get; } = order;
        /// <summary>The title shown until this lens is resolved: the resolved command of the lens it replaced.</summary>
        public Command? Previous { get; set; }
        public bool ResolveStarted { get; set; }
        public Command? Shown => Lens.Command ?? Previous;
    }

    /// <summary>One title of a line's lenses, laid out: where it is drawn and what a click on it runs.</summary>
    private readonly record struct Segment(Rect Bounds, FormattedText Text, Item Item, FormattedText? Separator);

    private readonly CodeEditorControl _v;
    private readonly LineZones _zones;
    private readonly DisposableStore _d = new();
    private readonly Dictionary<ICodeLensProvider, IDisposable> _providerSubs = [];
    private CancellationTokenSource? _debounce, _request, _resolveDebounce, _resolve;
    private Item[] _items = [];
    private Dictionary<int, Item[]>? _byLine; // line (1-based) → its lenses in display order; rebuilt lazily after a change that moves lines
    private int _hoverLine = -1;
    private Point? _hoverPoint;
    private bool _disposed;

    public CodeLensFeature(CodeEditorControl view)
    {
        _v = view;
        _zones = new LineZones(view, this);
        _d.Add(_zones);
        _d.Add(view.Document.BeforeChange.Subscribe(OnBeforeChange));
        _d.Add(view.Document.Changed.Subscribe(_ => Schedule(Delay)));
        _d.Add(view.Document.LanguageIdChanged.Subscribe(_ => _v.Post(() => Schedule(TimeSpan.Zero))));
        _d.Add(view.Services.Features.ProvidersChanged.Subscribe(t =>
        {
            if (t == typeof(ICodeLensProvider)) _v.Post(() => Schedule(TimeSpan.Zero));
        }));
        view.TextView.ScrollOffsetChanged += OnScrolled;
        _d.Add(Disposable.Create(() =>
        {
            view.TextView.ScrollOffsetChanged -= OnScrolled;
            foreach (var s in _providerSubs.Values) s.Dispose();
            _providerSubs.Clear();
        }));
        Schedule(TimeSpan.Zero);
    }

    /// <summary>The lenses placed now: (0-based line, title or null while unresolved), in display order.</summary>
    internal IReadOnlyList<(int Line, string? Title)> Placements =>
        Ordered(_items.Where(i => !i.Anchor.IsDeleted)).Select(i => (i.Anchor.Line - 1, i.Shown?.Title)).ToList();

    internal LineZones Zones => _zones;

    /// <summary>Height of a lens zone in the current settings.</summary>
    internal double ZoneHeight
    {
        get
        {
            var editorSize = _v.Editor.FontSize;
            var lineHeight = Math.Max(1, _v.TextView.DefaultLineHeight);
            var size = _v.Settings.CodeLensFontSize;
            return size is > 0 and >= 5 ? Math.Floor(size * Math.Max(1.3, lineHeight / editorSize)) : Math.Round(lineHeight);
        }
    }

    private double FontSize => _v.Settings.CodeLensFontSize is var s and >= 5 ? s : Math.Floor(_v.Editor.FontSize * 0.9);

    private FontFamily FontFamily => string.IsNullOrWhiteSpace(_v.Settings.CodeLensFontFamily)
        ? _v.Editor.FontFamily
        : new FontFamily(FontFamilies.Normalize(_v.Settings.CodeLensFontFamily));

    public void OnThemeChanged() => _zones.Repaint();

    public void OnSettingsChanged()
    {
        if (!_v.Settings.CodeLens) Apply([]);
        else
        {
            _zones.Invalidate(); // the zone height follows the font settings
            Schedule(TimeSpan.Zero);
        }
    }

    // ---- requests ----

    private void Schedule(TimeSpan delay)
    {
        if (_disposed) return;
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = null;
        if (!_v.Settings.CodeLens) return;
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _v.Post(() =>
            {
                if (!cts.IsCancellationRequested) _ = RefreshAsync();
            });
        }, TaskScheduler.Default);
    }

    private async Task RefreshAsync()
    {
        if (_disposed || !_v.Settings.CodeLens) return;
        var doc = _v.Document;
        var providers = _v.Services.Features.GetProviders<ICodeLensProvider>(doc);
        WatchProviders(providers);
        if (providers.Count == 0)
        {
            Apply([]);
            return;
        }
        _request?.Cancel();
        _request?.Dispose();
        var cts = _request = new CancellationTokenSource();
        var version = doc.Version;
        var calls = providers.Select(p => (Provider: p, Call: FeatureSupport.CallAsync(_v, c => p.ProvideCodeLensesAsync(doc, c), cts.Token))).ToList();
        var found = new List<(CodeLens Lens, ICodeLensProvider Provider)>();
        foreach (var (provider, call) in calls)
            if (await call.ConfigureAwait(true) is { } lenses)
                found.AddRange(lenses.Select(l => (l, provider)));
        if (_disposed || cts.IsCancellationRequested || doc.Version != version) return; // the text moved on: a newer request is scheduled

        var items = new List<Item>();
        var order = 0;
        foreach (var (lens, provider) in found.Where(f => !CodeLensCommands.IsHidden(f.Lens.Command)).Take(MaxLenses))
        {
            var p = doc.Validate(lens.Range.Start);
            if (p != lens.Range.Start) continue; // past the end of the text
            var anchor = doc.Buffer.CreateAnchor(doc.OffsetAt(p));
            anchor.MovementType = AnchorMovementType.AfterInsertion;
            items.Add(new Item(anchor, lens, provider, order++));
        }
        CarryOverTitles(items);
        Apply(items);
    }

    /// <summary>Subscribes to the providers' own change events (the Test Explorer's lenses follow discovery), and drops gone ones.</summary>
    private void WatchProviders(IReadOnlyList<ICodeLensProvider> providers)
    {
        foreach (var gone in _providerSubs.Keys.Where(p => !providers.Contains(p)).ToList())
        {
            _providerSubs[gone].Dispose();
            _providerSubs.Remove(gone);
        }
        foreach (var p in providers)
            if (!_providerSubs.ContainsKey(p))
                _providerSubs[p] = p.CodeLensesChanged.Subscribe(uri =>
                {
                    if (uri is null || FeatureSupport.SameFile(uri, _v.Document.Uri)) _v.Post(() => Schedule(TimeSpan.Zero));
                });
    }

    /// <summary>A new lens that needs resolving shows, until it is, the title of the old lens at the same place on its line.</summary>
    private void CarryOverTitles(List<Item> items)
    {
        if (_items.Length == 0) return;
        var old = Ordered(_items.Where(i => !i.Anchor.IsDeleted && i.Shown is not null)).GroupBy(i => i.Anchor.Line).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var group in Ordered(items).GroupBy(i => i.Anchor.Line))
        {
            if (!old.TryGetValue(group.Key, out var before)) continue;
            var k = 0;
            foreach (var item in group)
            {
                if (k >= before.Length) break;
                if (item.Lens.Command is null) item.Previous = before[k].Shown;
                k++;
            }
        }
    }

    /// <summary>Replaces the lenses, keeping the first visible line where it is on screen.</summary>
    private void Apply(IReadOnlyList<Item> items)
    {
        if (_items.Length == 0 && items.Count == 0) return;
        _resolve?.Cancel();
        _resolve?.Dispose();
        _resolve = null;
        var tv = _v.TextView;
        DocumentLine? keep = null;
        var keepAt = 0.0;
        if (tv.VisualLinesValid && tv.VisualLines.Count > 0)
        {
            var first = tv.VisualLines[0];
            keep = first.FirstDocumentLine;
            keepAt = LineZones.TextTop(first) - tv.ScrollOffset.Y;
        }
        _items = items.ToArray();
        foreach (var item in _items) if (item.Lens.Command is null) item.ResolveStarted = false; // a carried-over lens whose resolve was cancelled
        _byLine = null;
        _zones.Invalidate();
        if (keep is { IsDeleted: false } && tv.Document is not null)
        {
            var rebuilt = tv.GetOrConstructVisualLine(keep);
            var offset = LineZones.TextTop(rebuilt) - keepAt;
            // (TextEditor.ScrollToVerticalOffset is a no-op for absolute positions: the text area's offset is set)
            if (Math.Abs(offset - tv.ScrollOffset.Y) > 0.5 && offset >= 0)
                ((Avalonia.Controls.Primitives.IScrollable)_v.TextArea).Offset = new Vector(tv.ScrollOffset.X, offset);
        }
        ScheduleResolve(TimeSpan.Zero);
    }

    // ---- resolve, for the lenses near the viewport ----

    private void OnScrolled(object? sender, EventArgs e) => ScheduleResolve(ResolveDelay);

    private void ScheduleResolve(TimeSpan delay)
    {
        if (_disposed || _items.Length == 0) return;
        _resolveDebounce?.Cancel();
        _resolveDebounce?.Dispose();
        var cts = _resolveDebounce = new CancellationTokenSource();
        _ = Task.Delay(delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _v.Post(() =>
            {
                if (!cts.IsCancellationRequested) ResolveVisible();
            });
        }, TaskScheduler.Default);
    }

    private void ResolveVisible()
    {
        if (_disposed) return;
        var tv = _v.TextView;
        if (!tv.VisualLinesValid || tv.VisualLines.Count == 0)
        {
            ScheduleResolve(ResolveDelay); // not laid out yet
            return;
        }
        var first = tv.VisualLines[0].FirstDocumentLine.LineNumber - ResolveMargin;
        var last = tv.VisualLines[^1].LastDocumentLine.LineNumber + ResolveMargin;
        var items = _items;
        _resolve ??= new CancellationTokenSource();
        var ct = _resolve.Token;
        foreach (var item in items)
        {
            if (item.ResolveStarted || item.Lens.Command is not null || item.Anchor.IsDeleted) continue;
            var line = item.Anchor.Line;
            if (line < first || line > last) continue;
            item.ResolveStarted = true; // once: a lens the server cannot resolve is not asked again
            _ = ResolveAsync(item, items, ct);
        }
    }

    private async Task ResolveAsync(Item item, Item[] generation, CancellationToken ct)
    {
        var lens = item.Lens;
        var resolved = await FeatureSupport.CallAsync(_v, async c => (CodeLens?)await item.Provider.ResolveCodeLensAsync(lens, c).ConfigureAwait(false), ct).ConfigureAwait(true);
        if (_disposed || ct.IsCancellationRequested || !ReferenceEquals(generation, _items) || resolved?.Command is null) return;
        if (CodeLensCommands.IsHidden(resolved.Command))
        {
            Apply(_items.Where(i => i != item).ToArray()); // rare: the lens turned out to be one CsCode does not show
            return;
        }
        item.Lens = resolved;
        _zones.Repaint(); // the zone exists already: only its text changes
    }

    // ---- the zones ----

    private void OnBeforeChange(TextContentChange change)
    {
        // A change inside one line moves no anchor to another line; anything else may: the line map is rebuilt when next asked.
        if (change.Range.Start.Line != change.Range.End.Line || change.Text.AsSpan().IndexOfAny('\n', '\r') >= 0) _byLine = null;
    }

    private Dictionary<int, Item[]> ByLine => _byLine ??= Ordered(_items.Where(i => !i.Anchor.IsDeleted))
        .GroupBy(i => i.Anchor.Line).ToDictionary(g => g.Key, g => g.ToArray());

    private static IEnumerable<Item> Ordered(IEnumerable<Item> items) => items.OrderBy(i => i.Anchor.Offset).ThenBy(i => i.Order);

    public double HeightAbove(int line) => _items.Length > 0 && _v.Settings.CodeLens && ByLine.TryGetValue(line, out var list) && list.Any(i => !i.Anchor.IsDeleted) ? ZoneHeight : 0;

    private IReadOnlyList<Segment> Layout(VisualLine visualLine, int line, Rect zone)
    {
        if (!ByLine.TryGetValue(line, out var items)) return [];
        var tv = _v.TextView;
        var docLine = visualLine.FirstDocumentLine;
        var text = tv.Document!;
        var indent = 0;
        while (indent < docLine.Length && text.GetCharAt(docLine.Offset + indent) is ' ' or '\t') indent++;
        var x = visualLine.GetVisualPosition(visualLine.GetVisualColumn(indent), VisualYPosition.TextTop).X - tv.ScrollOffset.X;
        var typeface = new Typeface(FontFamily);
        var size = FontSize;
        var brush = _v.Palette.CodeLens;
        var separator = Format(" | ", typeface, size, brush);
        var segments = new List<Segment>();
        foreach (var item in items)
        {
            if (item.Anchor.IsDeleted || item.Shown is not { } command) continue;
            var title = CleanTitle(command.Title);
            if (title.Length == 0) continue;
            var first = segments.Count == 0;
            if (!first) x += separator.WidthIncludingTrailingWhitespace;
            var ft = Format(title, typeface, size, brush);
            var y = zone.Top + (zone.Height - ft.Height) / 2;
            segments.Add(new Segment(new Rect(x, y, ft.WidthIncludingTrailingWhitespace, ft.Height), ft, item, first ? null : separator));
            x += ft.WidthIncludingTrailingWhitespace;
        }
        return segments;
    }

    private static FormattedText Format(string text, Typeface typeface, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush);

    /// <summary>VS Code's <c>$(icon)</c> syntax names a Codicon, which CsCode does not embed: the icon is dropped, the text kept.</summary>
    internal static string CleanTitle(string title) => IconSyntax().Replace(title, "").Trim();

    [GeneratedRegex(@"\$\([a-zA-Z0-9~-]+\)")]
    private static partial Regex IconSyntax();

    public void Render(DrawingContext context, VisualLine visualLine, int line, Rect zone)
    {
        foreach (var s in Layout(visualLine, line, zone))
        {
            if (s.Separator is { } sep) context.DrawText(sep, new Point(s.Bounds.Left - sep.WidthIncludingTrailingWhitespace, s.Bounds.Top));
            var hovered = line == _hoverLine && _hoverPoint is { } p && s.Bounds.Contains(p) && IsCommand(s.Item);
            if (hovered)
            {
                // VS Code: a lens link under the pointer takes editorLink.activeForeground, underlined
                s.Text.SetForegroundBrush(_v.Palette.Link);
                var underlineY = Math.Round(s.Bounds.Bottom - 1) + 0.5;
                context.DrawLine(new Pen(_v.Palette.Link, 1), new Point(s.Bounds.Left, underlineY), new Point(s.Bounds.Right, underlineY));
            }
            context.DrawText(s.Text, s.Bounds.TopLeft);
        }
    }

    private static bool IsCommand(Item item) => !string.IsNullOrEmpty(item.Shown?.CommandId) && item.Lens.Command is not null;

    private Item? ItemAt(int line, Rect zone, Point point)
    {
        var tv = _v.TextView;
        var visual = tv.VisualLines.FirstOrDefault(vl => vl.FirstDocumentLine.LineNumber == line);
        if (visual is null) return null;
        foreach (var s in Layout(visual, line, zone))
            if (s.Bounds.Inflate(new Thickness(0, 2)).Contains(point)) return s.Item;
        return null;
    }

    public bool IsClickable(int line, Rect zone, Point point) => ItemAt(line, zone, point) is { } item && IsCommand(item);

    public void Click(int line, Rect zone, Point point)
    {
        if (ItemAt(line, zone, point) is not { } item || !IsCommand(item)) return;
        _ = CodeLensCommands.ExecuteAsync(_v, item.Lens.Command!, item.Lens.Range);
    }

    public void Hover(int line, Point? point)
    {
        if (line == _hoverLine && point == _hoverPoint) return;
        _hoverLine = line;
        _hoverPoint = point;
        _zones.Repaint();
    }

    /// <summary>Runs the command of the lens title at (<paramref name="line"/>, title index) as a click would; for tests and the keyboard.</summary>
    internal Task ClickAsync(int line, int index)
    {
        if (!ByLine.TryGetValue(line + 1, out var items)) return Task.CompletedTask;
        var shown = items.Where(i => !i.Anchor.IsDeleted && i.Shown is not null && CleanTitle(i.Shown.Title).Length > 0).ToList();
        if (index >= shown.Count || !IsCommand(shown[index])) return Task.CompletedTask;
        return CodeLensCommands.ExecuteAsync(_v, shown[index].Lens.Command!, shown[index].Lens.Range);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce?.Cancel();
        _request?.Cancel();
        _resolveDebounce?.Cancel();
        _resolve?.Cancel();
        _items = [];
        _byLine = null;
        _d.Dispose();
    }
}
