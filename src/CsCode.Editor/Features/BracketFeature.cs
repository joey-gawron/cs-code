using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.TextMate;
using CsCode.Editor.Theming;
using CsCode.Editor.Threading;
using CsCode.Editor.View;

namespace CsCode.Editor.Features;

/// <summary>
/// Bracket matching (the bracket next to the caret and its partner get a box) and, when <c>editor.bracketPairColorization.enabled</c>, bracket pair
/// colourisation (nesting depth cycles through <c>editorBracketHighlight.foreground1..6</c>; an unmatched closer is drawn in the "unexpected" colour).
///
/// Both are computed on the editor worker from document snapshots: the partner search is bounded (<see cref="MaxScanLines"/>/<see cref="MaxScanChars"/>) and
/// latest-wins per caret move; the depth map (nesting depth at the start of every line) is recomputed, coalesced, after edits and when tokenisation
/// improves (brackets inside strings, comments and regexes are ignored, as in VS Code). The UI only draws: the colorizer scans ONE visible line from the
/// stored depth. A map that is a version behind is still drawn (each coloured character is re-verified to be a bracket), so typing never flickers.
/// </summary>
internal sealed class BracketFeature : IThemeAware, ISettingsAware, IDisposable
{
    internal const int MaxScanLines = 20_000;
    internal const int MaxScanChars = 400_000;
    private static readonly TimeSpan MapDelay = TimeSpan.FromMilliseconds(120);

    private readonly CodeEditorControl _v;
    private readonly DisposableStore _d = new();
    private readonly object _matchKey = new(), _mapKey = new();
    private readonly MatchRenderer _renderer;
    private readonly PairColorizer _colorizer;
    private (int A, int B, int Version)? _match;
    private int[]? _depths; // nesting depth at the start of each line
    private CancellationTokenSource? _mapDebounce;
    private TokenizationSession? _subscribedTo;
    private IDisposable? _tokenSub;
    private bool _disposed;

    public BracketFeature(CodeEditorControl view)
    {
        _v = view;
        _renderer = new MatchRenderer(this);
        _colorizer = new PairColorizer(this);
        view.TextView.BackgroundRenderers.Add(_renderer);
        view.TextView.LineTransformers.Add(_colorizer);
        _d.Add(view.Controller.CaretChanged.Subscribe(_ => RequestMatch()));
        _d.Add(view.Document.Changed.Subscribe(_ =>
        {
            _match = null;
            ScheduleMap();
        }));
        OnSettingsChanged();
        RequestMatch();
    }

    private LanguageConfiguration? _pairsConfig;
    private BracketPairs _pairs = BracketPairs.None;

    /// <summary>The bracket pairs of the document's language (cached per configuration instance: queried once per drawn line).</summary>
    internal BracketPairs Pairs
    {
        get
        {
            var config = _v.LanguageConfiguration.Base;
            if (!ReferenceEquals(config, _pairsConfig))
            {
                _pairsConfig = config;
                _pairs = BracketPairs.From(config);
            }
            return _pairs;
        }
    }

    internal (int A, int B)? CurrentMatch => _match is { } m && m.Version == _v.Document.Version ? (m.A, m.B) : null;
    internal int[]? Depths => _depths;

    public void OnThemeChanged() => _v.TextView.Redraw();

    public void OnSettingsChanged()
    {
        if (_disposed) return;
        var wantMap = _v.Settings.BracketPairColorization;
        if (wantMap)
        {
            Resubscribe();
            ScheduleMap();
        }
        else
        {
            _tokenSub?.Dispose();
            _tokenSub = null;
            _subscribedTo = null;
            _depths = null;
        }
        if (!_v.Settings.BracketMatching) _match = null;
        _v.TextView.Redraw();
    }

    /// <summary>Re-colour when tokenisation of the document improves (a string that was plain a moment ago is no longer a bracket host).</summary>
    private void Resubscribe()
    {
        if (ReferenceEquals(_subscribedTo, _v.Tokens)) return;
        _tokenSub?.Dispose();
        _subscribedTo = _v.Tokens;
        _tokenSub = _v.Tokens?.TokensChanged.Subscribe(_ => ScheduleMap());
    }

    // ---- matching ----

    private void RequestMatch()
    {
        if (_disposed || !_v.Settings.BracketMatching) return;
        var doc = _v.Document;
        var pairs = Pairs;
        if (pairs.IsEmpty)
        {
            ClearMatch();
            return;
        }
        var snapshot = doc.Snapshot();
        var caret = Math.Min(_v.Editor.CaretOffset, snapshot.Length);
        var session = _v.Tokens;
        _v.Factory.Worker.Post(WorkPriority.Interactive, _matchKey, ct =>
        {
            var result = Find(snapshot, caret, pairs, session, ct);
            _v.Post(() =>
            {
                if (_disposed) return;
                var next = result is { } r ? ((int, int, int)?)(r.A, r.B, snapshot.Version) : null;
                if (_match == next) return;
                _match = next;
                _v.TextView.InvalidateLayer(KnownLayer.Selection);
            });
        });
    }

    private void ClearMatch()
    {
        if (_match is null) return;
        _match = null;
        _v.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    /// <summary>The bracket right of the caret, else the one left of it, and its partner. Null when there is none or no partner within the scan limits.</summary>
    internal static (int A, int B)? Find(DocumentSnapshot snapshot, int caret, BracketPairs pairs, TokenizationSession? tokens, CancellationToken ct)
    {
        foreach (var offset in new[] { caret, caret - 1 })
        {
            if (offset < 0 || offset >= snapshot.Length) continue;
            var ch = snapshot.GetCharAt(offset);
            if (!pairs.IsBracket(ch)) continue;
            var pos = snapshot.PositionAt(offset);
            if (IsHidden(tokens, pos.Line, pos.Character)) continue;
            var partner = pairs.IsOpen(ch) ? ScanForward(snapshot, pos, ch, pairs.Closer(ch), tokens, ct) : ScanBackward(snapshot, pos, ch, pairs.Opener(ch), tokens, ct);
            if (partner is { } p) return (offset, p);
        }
        return null;
    }

    private static bool IsHidden(TokenizationSession? tokens, int line, int character) =>
        tokens is not null && tokens.KindAt(line, character + 1) is TokenKind.String or TokenKind.Comment or TokenKind.Regex;

    private static int? ScanForward(DocumentSnapshot s, TextPosition from, char open, char close, TokenizationSession? tokens, CancellationToken ct)
    {
        var depth = 0;
        var chars = 0;
        var last = Math.Min(s.LineCount, from.Line + MaxScanLines);
        for (var line = from.Line; line < last; line++)
        {
            if ((line & 63) == 0) ct.ThrowIfCancellationRequested();
            var text = s.GetLineText(line);
            for (var c = line == from.Line ? from.Character : 0; c < text.Length; c++)
            {
                var ch = text[c];
                if (ch != open && ch != close) continue;
                if (line != from.Line || c != from.Character) { if (IsHidden(tokens, line, c)) continue; }
                depth += ch == open ? 1 : -1;
                if (depth == 0) return s.OffsetAt(new TextPosition(line, c));
            }
            chars += text.Length + 1;
            if (chars > MaxScanChars) return null;
        }
        return null;
    }

    private static int? ScanBackward(DocumentSnapshot s, TextPosition from, char close, char open, TokenizationSession? tokens, CancellationToken ct)
    {
        var depth = 0;
        var chars = 0;
        var first = Math.Max(0, from.Line - MaxScanLines);
        for (var line = from.Line; line >= first; line--)
        {
            if ((line & 63) == 0) ct.ThrowIfCancellationRequested();
            var text = s.GetLineText(line);
            for (var c = line == from.Line ? from.Character : text.Length - 1; c >= 0; c--)
            {
                var ch = text[c];
                if (ch != open && ch != close) continue;
                if (line != from.Line || c != from.Character) { if (IsHidden(tokens, line, c)) continue; }
                depth += ch == close ? 1 : -1;
                if (depth == 0) return s.OffsetAt(new TextPosition(line, c));
            }
            chars += text.Length + 1;
            if (chars > MaxScanChars) return null;
        }
        return null;
    }

    // ---- depth map (colourisation) ----

    private void ScheduleMap()
    {
        if (_disposed || !_v.Settings.BracketPairColorization) return;
        _mapDebounce?.Cancel();
        _mapDebounce?.Dispose();
        var cts = _mapDebounce = new CancellationTokenSource();
        _ = Task.Delay(MapDelay, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _v.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                ComputeMap();
            });
        }, TaskScheduler.Default);
    }

    private void ComputeMap()
    {
        if (_disposed) return;
        Resubscribe(); // the language (and with it the tokenisation session) may have changed since the settings were applied
        var snapshot = _v.Document.Snapshot();
        var pairs = Pairs;
        if (pairs.IsEmpty)
        {
            _depths = null;
            return;
        }
        var session = _v.Tokens;
        _v.Factory.Worker.Post(WorkPriority.Background, _mapKey, ct =>
        {
            var depths = BuildDepths(snapshot, pairs, session, ct);
            _v.Post(() =>
            {
                if (_disposed || !_v.Settings.BracketPairColorization) return;
                _depths = depths;
                _v.TextView.Redraw();
            });
        });
    }

    /// <summary>Nesting depth at the start of every line (0-based), brackets in strings/comments/regexes ignored.</summary>
    internal static int[] BuildDepths(DocumentSnapshot s, BracketPairs pairs, TokenizationSession? tokens, CancellationToken ct)
    {
        var depths = new int[s.LineCount];
        var depth = 0;
        for (var line = 0; line < depths.Length; line++)
        {
            if ((line & 255) == 0) ct.ThrowIfCancellationRequested();
            depths[line] = depth;
            var text = s.GetLineText(line);
            for (var c = 0; c < text.Length; c++)
            {
                var ch = text[c];
                if (!pairs.IsBracket(ch) || IsHidden(tokens, line, c)) continue;
                depth = pairs.IsOpen(ch) ? depth + 1 : Math.Max(0, depth - 1);
            }
        }
        return depths;
    }

    // ---- drawing ----

    private sealed class MatchRenderer(BracketFeature owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext context)
        {
            if (owner.CurrentMatch is not { } m || !textView.VisualLinesValid) return;
            var palette = owner._v.Palette;
            foreach (var offset in new[] { m.A, m.B })
            {
                var seg = new TextSegment { StartOffset = offset, Length = 1 };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg))
                    context.DrawRectangle(palette.BracketMatchBackground, palette.BracketMatchBorder, new Rect(rect.X + 0.5, rect.Y + 0.5, Math.Max(1, rect.Width - 1), Math.Max(1, rect.Height - 1)));
            }
        }
    }

    private sealed class PairColorizer(BracketFeature owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            var depths = owner._depths;
            if (depths is null || !owner._v.Settings.BracketPairColorization) return;
            var index = line.LineNumber - 1;
            if (index >= depths.Length) return;
            var pairs = owner.Pairs;
            if (pairs.IsEmpty) return;
            var doc = CurrentContext.Document;
            var tokens = owner._v.Tokens;
            var colors = owner._v.Palette.BracketColors;
            var depth = depths[index];
            var text = doc.GetText(line.Offset, line.Length);
            for (var c = 0; c < text.Length; c++)
            {
                var ch = text[c];
                if (!pairs.IsBracket(ch) || IsHidden(tokens, index, c)) continue;
                IBrush brush;
                if (pairs.IsOpen(ch))
                {
                    brush = colors[depth % colors.Length];
                    depth++;
                }
                else if (depth == 0) brush = owner._v.Palette.UnexpectedBracket;
                else
                {
                    depth--;
                    brush = colors[depth % colors.Length];
                }
                var offset = line.Offset + c;
                ChangeLinePart(offset, offset + 1, e => e.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _mapDebounce?.Cancel();
        _mapDebounce?.Dispose();
        _tokenSub?.Dispose();
        _d.Dispose();
        _v.TextView.BackgroundRenderers.Remove(_renderer);
        _v.TextView.LineTransformers.Remove(_colorizer);
    }
}

/// <summary>The single-character bracket pairs of a language configuration, for O(1) classification.</summary>
internal sealed class BracketPairs
{
    private readonly Dictionary<char, char> _closeOf = new();
    private readonly Dictionary<char, char> _openOf = new();

    public static readonly BracketPairs None = new();

    private BracketPairs() { }

    public bool IsEmpty => _closeOf.Count == 0;
    public bool IsOpen(char c) => _closeOf.ContainsKey(c);
    public bool IsBracket(char c) => _closeOf.ContainsKey(c) || _openOf.ContainsKey(c);
    public char Closer(char open) => _closeOf[open];
    public char Opener(char close) => _openOf[close];

    public static BracketPairs From(LanguageConfiguration config)
    {
        if (config.Brackets.Count == 0) return None;
        var p = new BracketPairs();
        foreach (var (open, close) in config.Brackets)
        {
            if (open.Length != 1 || close.Length != 1) continue; // word brackets (begin/end) are not coloured or matched
            p._closeOf[open[0]] = close[0];
            p._openOf[close[0]] = open[0];
        }
        return p;
    }
}
