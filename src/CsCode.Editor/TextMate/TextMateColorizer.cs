using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Lifecycle;
using CsCode.Core.Theming;
using CsCode.Editor.Theming;

namespace CsCode.Editor.TextMate;

/// <summary>
/// Turns scope-stack ids into render actions for one theme + font: resolved once per distinct stack with <see cref="TokenColorResolver"/>,
/// cached as a ready-made <c>Action&lt;VisualLineElement&gt;</c> so that colouring a line allocates nothing (no closure per token per frame).
/// A plain style (default colour, no emphasis) maps to null: nothing to change, nothing to split.
/// </summary>
internal sealed class ScopeStyleTable
{
    private readonly TokenColorResolver _resolver;
    private readonly ScopeInterner _interner;
    private readonly FontFamily _font;
    private readonly Rgba _defaultForeground;
    private readonly object _gate = new();
    private readonly Dictionary<ResolvedTokenStyle, Action<VisualLineElement>?> _byStyle = new();
    private Action<VisualLineElement>?[] _byScope = new Action<VisualLineElement>?[1024];
    private bool[] _resolved = new bool[1024];

    public ScopeStyleTable(ColorTheme theme, Rgba defaultForeground, ScopeInterner interner, FontFamily font)
    {
        _resolver = new TokenColorResolver(theme, defaultForeground);
        _defaultForeground = defaultForeground;
        _interner = interner;
        _font = font;
    }

    public Action<VisualLineElement>? Get(int scopeId)
    {
        var resolved = _resolved;
        if (scopeId < resolved.Length && resolved[scopeId]) return _byScope[scopeId];
        lock (_gate)
        {
            if (scopeId >= _resolved.Length)
            {
                var size = Math.Max(_resolved.Length * 2, scopeId + 1);
                Array.Resize(ref _byScope, size);
                Array.Resize(ref _resolved, size);
            }
            if (!_resolved[scopeId])
            {
                var style = _resolver.Resolve(_interner.Scopes(scopeId));
                if (!_byStyle.TryGetValue(style, out var action)) _byStyle[style] = action = Build(style);
                _byScope[scopeId] = action;
                _resolved[scopeId] = true;
            }
            return _byScope[scopeId];
        }
    }

    private Action<VisualLineElement>? Build(ResolvedTokenStyle s)
    {
        var plain = s.IsPlain || (s.Foreground == _defaultForeground && s.Background is null && !s.Bold && !s.Italic && !s.Underline && !s.Strikethrough);
        if (plain) return null;
        IBrush? fg = s.Foreground is { } f && f != _defaultForeground ? new ImmutableSolidColorBrush(Color.FromArgb(f.A, f.R, f.G, f.B)) : null;
        IBrush? bg = s.Background is { } b ? new ImmutableSolidColorBrush(Color.FromArgb(b.A, b.R, b.G, b.B)) : null;
        Typeface? face = s.Bold || s.Italic
            ? new Typeface(_font, s.Italic ? FontStyle.Italic : FontStyle.Normal, s.Bold ? FontWeight.Bold : FontWeight.Normal)
            : null;
        TextDecorationCollection? decorations = s.Underline || s.Strikethrough ? new TextDecorationCollection() : null;
        if (decorations is not null)
        {
            if (s.Underline) decorations.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
            if (s.Strikethrough) decorations.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
        }
        return element =>
        {
            var p = element.TextRunProperties;
            if (fg is not null) p.SetForegroundBrush(fg);
            if (bg is not null) p.SetBackgroundBrush(bg);
            if (face is { } t) p.SetTypeface(t);
            if (decorations is not null) p.SetTextDecorations(decorations);
        };
    }
}

/// <summary>
/// The syntax-colouring line transformer of ONE view. On the UI thread it does nothing but read the already-published token row of each
/// visual line and split/colour the matching elements (microseconds); it never tokenises. When a line has no tokens yet it is rendered plain
/// and redrawn when the worker publishes them (<see cref="TokenizationSession.TokensChanged"/>, coalesced).
/// </summary>
internal sealed class TextMateColorizer : DocumentColorizingTransformer, IDisposable
{
    private readonly TokenizationSession _session;
    private readonly TextView _textView;
    private readonly DisposableStore _subscriptions = new();
    private volatile ScopeStyleTable _table;

    public TextMateColorizer(TokenizationSession session, TextView textView, ScopeStyleTable table)
    {
        _session = session;
        _textView = textView;
        _table = table;
        textView.VisualLinesChanged += OnVisualLinesChanged;
        textView.ScrollOffsetChanged += OnVisualLinesChanged;
        _subscriptions.Add(Disposable.Create(() =>
        {
            textView.VisualLinesChanged -= OnVisualLinesChanged;
            textView.ScrollOffsetChanged -= OnVisualLinesChanged;
        }));
        _subscriptions.Add(session.TokensChanged.Subscribe(OnTokensChanged));
    }

    public void SetTable(ScopeStyleTable table)
    {
        _table = table;
        _textView.Redraw();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var tokens = _session.Store.GetTokens(line.LineNumber - 1);
        if (tokens is null) return;
        var table = _table;
        var start = line.Offset;
        var length = line.Length;
        var count = tokens.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var from = tokens[i * 2];
            if (from >= length) break; // stale tokens may reach past the (edited) line
            var to = i + 1 < count ? Math.Min(tokens[(i + 1) * 2], length) : length;
            if (to <= from) continue;
            if (table.Get(tokens[i * 2 + 1]) is { } action) ChangeLinePart(start + from, start + to, action);
        }
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e)
    {
        if (!_textView.VisualLinesValid || _textView.VisualLines.Count == 0) return;
        var lines = _textView.VisualLines;
        _session.SetViewport(lines[0].FirstDocumentLine.LineNumber - 1, lines[^1].LastDocumentLine.LineNumber - 1);
    }

    private void OnTokensChanged((int First, int Last) range)
    {
        var doc = _textView.Document;
        if (doc is null || doc.LineCount == 0) return;
        var first = Math.Clamp(range.First, 0, doc.LineCount - 1);
        var last = Math.Clamp(range.Last, first, doc.LineCount - 1);
        var a = doc.GetLineByNumber(first + 1);
        var b = doc.GetLineByNumber(last + 1);
        _textView.Redraw(a.Offset, b.EndOffset - a.Offset);
    }

    public void Dispose() => _subscriptions.Dispose();
}
