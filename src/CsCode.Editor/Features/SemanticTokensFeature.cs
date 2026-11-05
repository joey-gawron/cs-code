using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Editor.TextMate;
using CsCode.Editor.Theming;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// Semantic highlighting: the language server's semantic tokens (<see cref="ISemanticTokensProvider"/>, full with delta inside the LSP client,
/// or <see cref="ISemanticTokensRangeProvider"/> for the viewport) coloured on top of the grammar's (TextMate / tree-sitter) colours with the
/// theme's <c>semanticTokenColors</c> and VS Code's type → TextMate-scope fallbacks (<see cref="SemanticTokenStyleResolver"/>). A token whose type
/// no rule styles keeps the grammar's colour.
/// <list type="bullet">
/// <item>On when <c>editor.semanticHighlighting.enabled</c> says so: <c>configuredByTheme</c> (the default) follows the theme's
/// <c>semanticHighlighting</c> flag, as in VS Code.</item>
/// <item>Asked 300 ms after the text stops changing, when a provider comes or goes (<c>workspace/semanticTokens/refresh</c> re-registers the
/// server's provider), and after the theme or the setting changes; off the UI thread, latest-wins; an answer for an older version of the text is
/// dropped. A document of more than <see cref="RangeThresholdLines"/> lines with a range provider asks for the visible lines (plus a margin)
/// instead, again after scrolling, and keeps tokens only near the viewport.</item>
/// <item>No flicker: the tokens on screen stay, adjusted by every edit (<see cref="SemanticTokenStore"/>), until the next answer replaces them.</item>
/// <item>Bounded: at most <see cref="SemanticTokenStore.MaxTokens"/> tokens; the styles are cached per (type, modifiers) of the legend.</item>
/// </list>
/// </summary>
internal sealed class SemanticTokensFeature : ISettingsAware, IThemeAware, IDisposable
{
    public const int RangeThresholdLines = 5000;
    private const int RangeMargin = 100; // lines asked above/below the viewport in range mode
    private const int RangeKeep = 400;   // lines kept around the asked range in range mode
    internal static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ScrollDelay = TimeSpan.FromMilliseconds(150);

    private readonly CodeEditorControl _v;
    private readonly SemanticTokenStore _store = new();
    private readonly Colorizer _colorizer;
    private readonly DisposableStore _d = new();
    private CancellationTokenSource? _debounce, _request;
    private StyleTable? _styles;
    private SemanticTokensLegend? _legend;
    private bool _rangeMode, _disposed, _wasEnabled;

    public SemanticTokensFeature(CodeEditorControl view)
    {
        _v = view;
        _colorizer = new Colorizer(this);
        PlaceColorizer();
        _d.Add(view.Document.BeforeChange.Subscribe(c => _store.Apply(c)));
        _d.Add(view.Document.Changed.Subscribe(_ => Schedule(Delay)));
        _d.Add(view.Document.LanguageIdChanged.Subscribe(_ => _v.Post(() =>
        {
            PlaceColorizer(); // the view re-attached its grammar colouring: stay after it
            _styles = null;
            Schedule(TimeSpan.Zero);
        })));
        _d.Add(view.Services.Features.ProvidersChanged.Subscribe(t =>
        {
            if (t == typeof(ISemanticTokensProvider) || t == typeof(ISemanticTokensRangeProvider)) _v.Post(() => Schedule(TimeSpan.Zero));
        }));
        view.TextView.ScrollOffsetChanged += OnScrolled;
        _wasEnabled = Enabled;
        _d.Add(Disposable.Create(() =>
        {
            view.TextView.ScrollOffsetChanged -= OnScrolled;
            view.TextView.LineTransformers.Remove(_colorizer);
        }));
        Schedule(TimeSpan.Zero);
    }

    /// <summary>The tokens of a 0-based line as (start, length, type name, modifier names), for tests.</summary>
    internal IReadOnlyList<(int Start, int Length, string Type, string[] Modifiers)> TokensOf(int line)
    {
        var row = _store.Get(line);
        if (row is null || _legend is not { } legend) return [];
        var list = new List<(int, int, string, string[])>();
        for (var i = 0; i < row.Length; i += 4)
            list.Add((row[i], row[i + 1], row[i + 2] < legend.TokenTypes.Count ? legend.TokenTypes[row[i + 2]] : "?", ModifierNames(legend, row[i + 3])));
        return list;
    }

    internal int TokenCount => _store.TokenCount;
    internal bool RangeMode => _rangeMode;

    private bool Enabled => LanguageRenderSettings.SemanticHighlightingOn(_v.Settings.SemanticHighlighting, _v.Factory.Themes.Current);

    public void OnSettingsChanged() => Refresh();

    public void OnThemeChanged() => Refresh();

    private void Refresh()
    {
        _styles = null; // the theme's rules or the font changed
        var was = _wasEnabled;
        _wasEnabled = Enabled;
        if (!_wasEnabled) Clear();
        else
        {
            _v.TextView.Redraw();
            if (!was) Schedule(TimeSpan.Zero); // turned on: ask (a change that leaves it on only restyles what is there)
        }
    }

    /// <summary>Right after the grammar colouring (so its colours are the base) and before everything else (bracket pair colours win, as in VS Code).</summary>
    private void PlaceColorizer()
    {
        var transformers = _v.TextView.LineTransformers;
        transformers.Remove(_colorizer);
        var grammar = -1;
        for (var i = 0; i < transformers.Count; i++)
            if (transformers[i] is TextMateColorizer) grammar = i;
        transformers.Insert(grammar + 1, _colorizer);
    }

    private void Clear()
    {
        _debounce?.Cancel();
        _request?.Cancel();
        if (_store.IsEmpty) return;
        _store.Clear();
        _v.TextView.Redraw();
    }

    private void OnScrolled(object? sender, EventArgs e)
    {
        if (_rangeMode) Schedule(ScrollDelay);
    }

    private void Schedule(TimeSpan delay)
    {
        if (_disposed) return;
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = null;
        if (!Enabled) return;
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(delay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _v.Post(() =>
            {
                if (!cts.IsCancellationRequested) _ = RequestAsync();
            });
        }, TaskScheduler.Default);
    }

    private async Task RequestAsync()
    {
        if (_disposed || !Enabled) return;
        var doc = _v.Document;
        var full = _v.Services.Features.GetProviders<ISemanticTokensProvider>(doc).FirstOrDefault();
        var range = _v.Services.Features.GetProviders<ISemanticTokensRangeProvider>(doc).FirstOrDefault();
        if (full is null && range is null)
        {
            _legend = null;
            Clear();
            return;
        }
        _request?.Cancel();
        _request?.Dispose();
        var cts = _request = new CancellationTokenSource();
        var version = doc.Version;
        var useRange = range is not null && (full is null || doc.LineCount > RangeThresholdLines);
        _rangeMode = useRange;
        SemanticTokens? result;
        SemanticTokensLegend legend;
        int first = 0, last = doc.LineCount - 1;
        if (useRange)
        {
            var tv = _v.TextView;
            if (!tv.VisualLinesValid || tv.VisualLines.Count == 0)
            {
                Schedule(ScrollDelay); // not laid out yet
                return;
            }
            first = Math.Max(0, tv.VisualLines[0].FirstDocumentLine.LineNumber - 1 - RangeMargin);
            last = Math.Min(doc.LineCount - 1, tv.VisualLines[^1].LastDocumentLine.LineNumber - 1 + RangeMargin);
            var asked = new TextRange(new TextPosition(first, 0), new TextPosition(last, doc.GetLineText(last).Length));
            legend = range!.Legend;
            result = await FeatureSupport.CallAsync(_v, c => range.ProvideSemanticTokensRangeAsync(doc, asked, c), cts.Token, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        }
        else
        {
            legend = full!.Legend;
            result = await FeatureSupport.CallAsync(_v, c => full.ProvideSemanticTokensAsync(doc, c), cts.Token, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        }
        if (_disposed || cts.IsCancellationRequested || doc.Version != version || !Enabled) return; // the text moved on: a newer request is scheduled
        if (result is null) return; // no answer (failed, timed out): keep what is shown
        if (!ReferenceEquals(legend, _legend))
        {
            _legend = legend;
            _styles = null;
        }
        if (useRange) _store.SetRange(result.Data, first, last, doc.LineCount, RangeKeep);
        else _store.SetFull(result.Data, doc.LineCount);
        _v.TextView.Redraw();
    }

    private static string[] ModifierNames(SemanticTokensLegend legend, int bits)
    {
        var names = new List<string>();
        for (var b = 0; b < 32 && b < legend.TokenModifiers.Count; b++)
            if ((bits & (1 << b)) != 0) names.Add(legend.TokenModifiers[b]);
        return [.. names];
    }

    private StyleTable? Styles
    {
        get
        {
            if (_styles is not null || _legend is null) return _styles;
            var themes = _v.Factory.Themes;
            var resolver = new SemanticTokenStyleResolver(themes.Current, new TokenColorResolver(themes.Current, themes.GetColor("editor.foreground")));
            return _styles = new StyleTable(resolver, _legend, _v.Document.LanguageId, _v.Editor.FontFamily);
        }
    }

    /// <summary>(type, modifiers) of one legend → a ready-made colouring action, resolved once each; no allocation when colouring.</summary>
    private sealed class StyleTable(SemanticTokenStyleResolver resolver, SemanticTokensLegend legend, string language, FontFamily font)
    {
        private readonly Action<VisualLineElement>?[] _plain = new Action<VisualLineElement>?[legend.TokenTypes.Count];
        private readonly bool[] _plainResolved = new bool[legend.TokenTypes.Count];
        private readonly Dictionary<long, Action<VisualLineElement>?> _withModifiers = [];

        public Action<VisualLineElement>? Get(int type, int modifiers)
        {
            if ((uint)type >= (uint)_plain.Length) return null;
            if (modifiers == 0)
            {
                if (!_plainResolved[type])
                {
                    _plain[type] = Build(type, 0);
                    _plainResolved[type] = true;
                }
                return _plain[type];
            }
            var key = ((long)type << 32) | (uint)modifiers;
            if (!_withModifiers.TryGetValue(key, out var action))
            {
                if (_withModifiers.Count > 4096) _withModifiers.Clear(); // a server inventing combinations cannot grow it without bound
                _withModifiers[key] = action = Build(type, modifiers);
            }
            return action;
        }

        private Action<VisualLineElement>? Build(int type, int modifiers)
        {
            if (resolver.Resolve(legend.TokenTypes[type], ModifierNames(legend, modifiers), language) is not { } s) return null;
            IBrush? fg = s.Foreground is { } f ? new ImmutableSolidColorBrush(Color.FromArgb(f.A, f.R, f.G, f.B)) : null;
            var setsDecorations = s.Underline is not null || s.Strikethrough is not null;
            // An explicit false still sets (clears) the decorations: AvaloniaEdit throws on a null collection.
            TextDecorationCollection decorations = [];
            if (s.Underline == true) decorations.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
            if (s.Strikethrough == true) decorations.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
            return element =>
            {
                var p = element.TextRunProperties;
                if (fg is not null) p.SetForegroundBrush(fg);
                if (s.Bold is not null || s.Italic is not null)
                {
                    var face = p.Typeface;
                    var weight = s.Bold is { } b ? (b ? FontWeight.Bold : FontWeight.Normal) : face.Weight;
                    var style = s.Italic is { } i ? (i ? FontStyle.Italic : FontStyle.Normal) : face.Style;
                    p.SetTypeface(new Typeface(font, style, weight));
                }
                if (setsDecorations) p.SetTextDecorations(decorations);
            };
        }
    }

    /// <summary>Colours the published tokens of each line being built: reads the store, never asks the server.</summary>
    private sealed class Colorizer(SemanticTokensFeature owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            var row = owner._store.Get(line.LineNumber - 1);
            if (row is null || owner.Styles is not { } styles) return;
            var start = line.Offset;
            var length = line.Length;
            for (var i = 0; i < row.Length; i += 4)
            {
                var from = row[i];
                if (from >= length) break;
                var to = Math.Min(from + row[i + 1], length);
                if (to <= from) continue;
                if (styles.Get(row[i + 2], row[i + 3]) is { } action) ChangeLinePart(start + from, start + to, action);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce?.Cancel();
        _request?.Cancel();
        _d.Dispose();
        _store.Clear();
    }
}
