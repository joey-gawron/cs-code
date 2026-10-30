using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using CsCode.Terminal.Emulator;
using CsCode.Ui.Theming;

namespace CsCode.Panels.Terminal.Rendering;

/// <summary>The colours of one frame, read from the live theme brushes (<c>terminal.*</c>), so a theme switch shows on the next frame.</summary>
internal sealed class TerminalColors
{
    private static readonly string[] AnsiNames =
        ["Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White", "BrightBlack", "BrightRed", "BrightGreen", "BrightYellow", "BrightBlue", "BrightMagenta", "BrightCyan", "BrightWhite"];

    public Color Foreground;
    public Color Background;
    public Color Cursor;
    public Color CursorText;
    public Color Selection;
    public Color CommandDefault;
    public Color CommandSuccess;
    public Color CommandError;
    public Color FindMatch;
    public Color FindMatchHighlight;
    public readonly Color[] Ansi = new Color[16];
    public bool BoldIsBright = true;
    public double MinimumContrast = 4.5;
    public IReadOnlyDictionary<int, uint>? Overrides;

    public static TerminalColors FromTheme(bool boldIsBright, double minimumContrast)
    {
        var c = new TerminalColors { BoldIsBright = boldIsBright, MinimumContrast = minimumContrast };
        c.Background = Get("terminal.background", Get("panel.background", Color.FromRgb(0x1e, 0x1e, 0x1e)));
        c.Foreground = Get("terminal.foreground", Color.FromRgb(0xcc, 0xcc, 0xcc));
        c.Cursor = Get("terminalCursor.foreground", c.Foreground);
        c.CursorText = Get("terminalCursor.background", c.Background);
        c.Selection = Get("terminal.selectionBackground", Get("editor.selectionBackground", Color.FromArgb(0x80, 0x26, 0x4f, 0x78)));
        c.CommandDefault = Get("terminalCommandDecoration.defaultBackground", Color.FromArgb(0x40, 0xff, 0xff, 0xff));
        c.CommandSuccess = Get("terminalCommandDecoration.successBackground", Color.FromRgb(0x1b, 0x81, 0xa8));
        c.CommandError = Get("terminalCommandDecoration.errorBackground", Color.FromRgb(0xf1, 0x4c, 0x4c));
        // VS Code: the terminal's find colours default to the editor's
        c.FindMatch = Get("terminal.findMatchBackground", Get("editor.findMatchBackground", Color.FromRgb(0x51, 0x5c, 0x6a)));
        c.FindMatchHighlight = Get("terminal.findMatchHighlightBackground", Get("editor.findMatchHighlightBackground", Color.FromArgb(0x55, 0xea, 0x5c, 0x00)));
        var defaults = DefaultTerminalPalette.Instance;
        for (var i = 0; i < 16; i++)
        {
            var rgb = defaults.Ansi(i);
            c.Ansi[i] = Get("terminal.ansi" + AnsiNames[i], Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        }
        return c;
    }

    private static Color Get(string key, Color fallback) =>
        ThemeBrushes.Get(key) is ISolidColorBrush b && b.Color.A > 0 ? b.Color : fallback;

    public Color Indexed(int index)
    {
        if (Overrides is not null && Overrides.TryGetValue(index, out var o)) return Color.FromRgb((byte)(o >> 16), (byte)(o >> 8), (byte)o);
        if (index < 16) return Ansi[index];
        var rgb = DefaultTerminalPalette.Color256(DefaultTerminalPalette.Instance, index);
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>(foreground, background) of a cell's rendition: bold as bright, inverse, dim, invisible, minimum contrast.</summary>
    public (Color Fg, Color Bg, bool BgIsDefault) Resolve(in CellAttributes a, bool reverseVideo)
    {
        var bold = a.Has(CellFlags.Bold);
        var fgRaw = a.Foreground;
        Color fg = TermColor.IsDefault(fgRaw) ? Foreground
            : TermColor.IsIndexed(fgRaw) ? Indexed(BoldIsBright && bold && TermColor.Index(fgRaw) < 8 ? TermColor.Index(fgRaw) + 8 : TermColor.Index(fgRaw))
            : FromRgb(fgRaw);
        var bgDefault = TermColor.IsDefault(a.Background);
        Color bg = bgDefault ? Background : TermColor.IsIndexed(a.Background) ? Indexed(TermColor.Index(a.Background)) : FromRgb(a.Background);
        if (a.Has(CellFlags.Inverse) != reverseVideo)
        {
            (fg, bg) = (bg, fg);
            bgDefault = false;
        }
        if (a.Has(CellFlags.Dim)) fg = Color.FromArgb((byte)(fg.A / 2), fg.R, fg.G, fg.B);
        if (a.Has(CellFlags.Invisible)) fg = Colors.Transparent;
        else if (MinimumContrast > 1) fg = EnsureContrast(fg, bg, MinimumContrast);
        return (fg, bg, bgDefault);
    }

    public static Color FromRgb(uint c) { var (r, g, b) = TermColor.RgbOf(c); return Color.FromRgb(r, g, b); }

    /// <summary>VS Code's terminal.integrated.minimumContrastRatio: lighten or darken the foreground until it reaches the ratio.</summary>
    internal static Color EnsureContrast(Color fg, Color bg, double ratio)
    {
        if (fg.A < 255 || Contrast(fg, bg) >= ratio) return fg;
        var towardsWhite = Luminance(bg) < 0.5;
        var c = fg;
        for (var i = 0; i < 20 && Contrast(c, bg) < ratio; i++)
            c = towardsWhite
                ? Color.FromRgb((byte)(c.R + (255 - c.R) / 4 + 1 > 255 ? 255 : c.R + (255 - c.R) / 4 + 1), (byte)Math.Min(255, c.G + (255 - c.G) / 4 + 1), (byte)Math.Min(255, c.B + (255 - c.B) / 4 + 1))
                : Color.FromRgb((byte)(c.R - c.R / 4 - (c.R > 0 ? 1 : 0)), (byte)(c.G - c.G / 4 - (c.G > 0 ? 1 : 0)), (byte)(c.B - c.B / 4 - (c.B > 0 ? 1 : 0)));
        return c;
    }

    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a) + 0.05;
        var lb = Luminance(b) + 0.05;
        return la > lb ? la / lb : lb / la;
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}

/// <summary>
/// Draws a <see cref="TerminalSnapshot"/>: only the visible rows; per row, runs of equal rendition become one background rectangle and glyph
/// runs (built without shaping, every glyph advanced by exactly one or two cells, as terminals do; grapheme clusters are shaped on their own).
/// Built runs are cached per <see cref="TerminalRow"/> instance — an unchanged row is the same instance in the next snapshot, so a frame only
/// builds the rows that changed (row-level dirty tracking). UI thread only.
/// </summary>
internal sealed class TerminalPainter : IDisposable
{
    private sealed class Segment
    {
        public int Start;
        public int End; // inclusive
        public CellAttributes Attributes;
        public readonly List<GlyphRun> Runs = new();
        public readonly List<(int Column, FormattedText Text)> Clusters = new();
    }

    private sealed class RowVisual(List<Segment> segments)
    {
        public readonly List<Segment> Segments = segments;
        public int LastUsed;
    }

    private readonly Dictionary<TerminalRow, RowVisual> _rows = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Color, IImmutableSolidColorBrush> _brushes = new();
    private TerminalFont? _font;
    private int _frame;

    /// <summary>Rows built by the last frame (tests assert unchanged rows are reused).</summary>
    public int RowsBuiltLastFrame { get; private set; }

    public void SetFont(TerminalFont font)
    {
        if (_font is not null && _font.Key == font.Key) return;
        _font = font;
        Clear();
    }

    public void Clear()
    {
        foreach (var r in _rows.Values) Dispose(r);
        _rows.Clear();
    }

    private static void Dispose(RowVisual r)
    {
        foreach (var s in r.Segments)
            foreach (var run in s.Runs) run.Dispose();
    }

    public IImmutableSolidColorBrush Brush(Color c)
    {
        if (_brushes.TryGetValue(c, out var b)) return b;
        if (_brushes.Count > 512) _brushes.Clear();
        return _brushes[c] = new ImmutableSolidColorBrush(c);
    }

    /// <param name="Decorations">Shell-integration marks beside prompts (<c>terminal.integrated.shellIntegration.decorationsEnabled</c>).</param>
    /// <param name="FindMatches">Find's matches in view (highlighted), <paramref name="CurrentMatch"/> the one Find is on.</param>
    public readonly record struct Overlay(Func<long, int, (int Start, int End)?>? Selection, (int Row, int Start, int End)? Link, bool CursorOn, bool Focused, CursorShape CursorStyle,
        bool Decorations = true, IReadOnlyList<TerminalMatch>? FindMatches = null, TerminalMatch? CurrentMatch = null);

    public void Paint(DrawingContext ctx, TerminalSnapshot snap, TerminalColors colors, Overlay overlay, Size size)
    {
        var font = _font ?? throw new InvalidOperationException("No font.");
        _frame++;
        RowsBuiltLastFrame = 0;
        var cw = font.CellWidth;
        var ch = font.CellHeight;
        ctx.FillRectangle(Brush(snap.ReverseVideo ? colors.Foreground : colors.Background), new Rect(size));
        colors.Overrides = snap.PaletteOverrides;
        var selection = Brush(colors.Selection);
        for (var r = 0; r < snap.RowCount && r < snap.Rows.Length; r++)
        {
            var row = snap.Rows[r];
            if (!_rows.TryGetValue(row, out var visual))
            {
                visual = Build(row, snap.Columns, font);
                _rows[row] = visual;
                RowsBuiltLastFrame++;
            }
            visual.LastUsed = _frame;
            var y = r * ch;
            using (ctx.PushTransform(Matrix.CreateTranslation(0, y)))
            {
                foreach (var seg in visual.Segments)
                {
                    var (_, bg, bgDefault) = colors.Resolve(seg.Attributes, snap.ReverseVideo);
                    if (!bgDefault) ctx.FillRectangle(Brush(bg), new Rect(seg.Start * cw, 0, (seg.End - seg.Start + 1) * cw, ch));
                }
                if (overlay.FindMatches is { Count: > 0 } matches)
                {
                    // Find: every match in view, the current one stronger (VS Code's findMatchHighlight / findMatch colours)
                    var line = snap.ViewportTop + r;
                    foreach (var m in matches)
                        if (m.ColumnsOn(line, snap.Columns) is { } span)
                            ctx.FillRectangle(Brush(m == overlay.CurrentMatch ? colors.FindMatch : colors.FindMatchHighlight),
                                new Rect(span.Start * cw, 0, (span.End - span.Start + 1) * cw, ch));
                }
                if (overlay.Selection?.Invoke(snap.ViewportTop + r, snap.Columns) is { } sel)
                    ctx.FillRectangle(selection, new Rect(sel.Start * cw, 0, (sel.End - sel.Start + 1) * cw, ch));
                foreach (var seg in visual.Segments)
                {
                    var (fg, _, _) = colors.Resolve(seg.Attributes, snap.ReverseVideo);
                    if (fg.A == 0) continue;
                    var brush = Brush(fg);
                    foreach (var run in seg.Runs) ctx.DrawGlyphRun(brush, run);
                    foreach (var (col, text) in seg.Clusters)
                    {
                        text.SetForegroundBrush(brush);
                        ctx.DrawText(text, new Point(col * cw, font.Baseline - text.Baseline));
                    }
                    Decorate(ctx, seg, fg, colors, font);
                }
                if (row.Mark == RowMark.PromptStart && overlay.Decorations)
                {
                    // shell integration: a mark beside each prompt, coloured by its command's exit code (VS Code's command decorations)
                    var color = row.MarkExitCode switch { null or int.MinValue => colors.CommandDefault, 0 => colors.CommandSuccess, _ => colors.CommandError };
                    ctx.FillRectangle(Brush(color), new Rect(-3, 1, 2, ch - 2));
                }
                if (overlay.Link is { } link && link.Row == r)
                    ctx.FillRectangle(Brush(colors.Foreground), new Rect(link.Start * cw, font.UnderlineY, (link.End - link.Start + 1) * cw, font.LineThickness));
            }
        }
        if (snap.CursorVisible && snap.CursorY >= 0 && (overlay.CursorOn || !overlay.Focused)) DrawCursor(ctx, snap, colors, overlay, font);
        Evict();
    }

    private void Decorate(DrawingContext ctx, Segment seg, Color fg, TerminalColors colors, TerminalFont font)
    {
        var a = seg.Attributes;
        var x = seg.Start * font.CellWidth;
        var w = (seg.End - seg.Start + 1) * font.CellWidth;
        var t = font.LineThickness;
        if (a.Underline != UnderlineStyle.None)
        {
            var ul = TermColor.IsDefault(a.UnderlineColor) ? fg
                : TermColor.IsIndexed(a.UnderlineColor) ? colors.Indexed(TermColor.Index(a.UnderlineColor)) : TerminalColors.FromRgb(a.UnderlineColor);
            var brush = Brush(ul);
            var y = font.UnderlineY;
            switch (a.Underline)
            {
                case UnderlineStyle.Double:
                    ctx.FillRectangle(brush, new Rect(x, y - t, w, t));
                    ctx.FillRectangle(brush, new Rect(x, Math.Min(font.CellHeight - t, y + t), w, t));
                    break;
                case UnderlineStyle.Curly:
                {
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        var amp = Math.Max(1, t);
                        g.BeginFigure(new Point(x, y), false);
                        for (var px = x; px < x + w; px += 4)
                        {
                            g.LineTo(new Point(px + 2, y + amp));
                            g.LineTo(new Point(px + 4, y - amp));
                        }
                        g.EndFigure(false);
                    }
                    ctx.DrawGeometry(null, new ImmutablePen(brush, t), geo);
                    break;
                }
                case UnderlineStyle.Dotted:
                    for (var px = x; px < x + w; px += 2 * t) ctx.FillRectangle(brush, new Rect(px, y, t, t));
                    break;
                case UnderlineStyle.Dashed:
                    for (var px = x; px < x + w; px += 6 * t) ctx.FillRectangle(brush, new Rect(px, y, Math.Min(4 * t, x + w - px), t));
                    break;
                default:
                    ctx.FillRectangle(brush, new Rect(x, y, w, t));
                    break;
            }
        }
        if (a.Has(CellFlags.Strikethrough)) ctx.FillRectangle(Brush(fg), new Rect(x, font.StrikeY, w, t));
        if (a.Has(CellFlags.Overline)) ctx.FillRectangle(Brush(fg), new Rect(x, 0, w, t));
    }

    private void DrawCursor(DrawingContext ctx, TerminalSnapshot snap, TerminalColors colors, Overlay overlay, TerminalFont font)
    {
        var cw = font.CellWidth;
        var ch = font.CellHeight;
        var row = snap.Rows[snap.CursorY];
        var wide = row[snap.CursorX].IsWide ? 2 : 1;
        var rect = new Rect(snap.CursorX * cw, snap.CursorY * ch, cw * wide, ch);
        var brush = Brush(colors.Cursor);
        var shape = snap.CursorShape == CursorShape.Default ? overlay.CursorStyle : snap.CursorShape;
        if (!overlay.Focused)
        {
            // unfocused: a hollow block, as xterm.js and VS Code draw it
            ctx.DrawRectangle(null, new ImmutablePen(brush, 1), rect.Deflate(0.5));
            return;
        }
        switch (shape)
        {
            case CursorShape.Bar:
                ctx.FillRectangle(brush, new Rect(rect.X, rect.Y, Math.Max(2, font.LineThickness * 2), ch));
                break;
            case CursorShape.Underline:
                ctx.FillRectangle(brush, new Rect(rect.X, rect.Bottom - Math.Max(2, font.LineThickness * 2), rect.Width, Math.Max(2, font.LineThickness * 2)));
                break;
            default:
            {
                ctx.FillRectangle(brush, rect);
                // the character under a block cursor in the cursor's text colour
                var cell = row[snap.CursorX];
                if (cell.Rune > 0 && cell.Rune != ' ' && _font is { } f)
                {
                    var (face, glyph) = f.Glyph(cell.Rune, cell.Attributes.Has(CellFlags.Bold), cell.Attributes.Has(CellFlags.Italic));
                    if (glyph != 0)
                    {
                        var chars = char.ConvertFromUtf32(cell.Rune).AsMemory();
                        using var run = new GlyphRun(face, f.Size, chars, [new GlyphInfo(glyph, 0, cw * wide)], new Point(rect.X, rect.Y + f.Baseline));
                        ctx.DrawGlyphRun(Brush(colors.CursorText), run);
                    }
                }
                break;
            }
        }
    }

    private void Evict()
    {
        // rows not drawn for a few frames are gone from the screen (scrolled, replaced): release their runs
        if (_rows.Count < 256 && _frame % 32 != 0) return;
        var stale = _rows.Where(kv => _frame - kv.Value.LastUsed > 2).Select(kv => kv.Key).ToList();
        foreach (var key in stale)
        {
            Dispose(_rows[key]);
            _rows.Remove(key);
        }
    }

    private static RowVisual Build(TerminalRow row, int columns, TerminalFont font)
    {
        var segments = new List<Segment>();
        Segment? current = null;
        var limit = Math.Min(columns, Math.Max(row.Length, 0));
        for (var c = 0; c < limit; c++)
        {
            var cell = row[c];
            var attr = cell.Attributes.Rendition;
            if (current is null || current.Attributes != attr)
            {
                current = new Segment { Start = c, End = c, Attributes = attr };
                segments.Add(current);
            }
            else current.End = c;
        }
        foreach (var seg in segments) BuildRuns(row, seg, font);
        return new RowVisual(segments);
    }

    private static void BuildRuns(TerminalRow row, Segment seg, TerminalFont font)
    {
        var bold = seg.Attributes.Has(CellFlags.Bold);
        var italic = seg.Attributes.Has(CellFlags.Italic);
        var cw = font.CellWidth;
        GlyphTypeface? face = null;
        var glyphs = new List<GlyphInfo>();
        var chars = new System.Text.StringBuilder();
        var runStart = 0;
        void Flush()
        {
            if (face is not null && glyphs.Count > 0)
                seg.Runs.Add(new GlyphRun(face, font.Size, chars.ToString().AsMemory(), glyphs.ToArray(), new Point(runStart * cw, font.Baseline)));
            glyphs.Clear();
            chars.Clear();
            face = null;
        }
        for (var c = seg.Start; c <= seg.End; c++)
        {
            var cell = row[c];
            if (cell.IsSpacer) continue;
            var width = cell.IsWide ? 2 : 1;
            if (cell.Rune < 0)
            {
                // a grapheme cluster (combining marks, emoji sequence): shaped on its own
                Flush();
                var text = new FormattedText(row.CellText(c), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(font.Family, italic ? FontStyle.Italic : FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), font.Size, null);
                seg.Clusters.Add((c, text));
                continue;
            }
            if (cell.Rune is 0 or ' ')
            {
                Flush();
                continue;
            }
            var (f, glyph) = font.Glyph(cell.Rune, bold, italic);
            if (glyph == 0) { Flush(); continue; }
            if (!ReferenceEquals(face, f)) { Flush(); face = f; runStart = c; }
            if (glyphs.Count == 0) runStart = c;
            // one glyph per cell: the cluster index is the glyph's offset in the run's characters
            glyphs.Add(new GlyphInfo(glyph, chars.Length, width * cw));
            if (cell.Rune < 0x10000) chars.Append((char)cell.Rune); else chars.Append(char.ConvertFromUtf32(cell.Rune));
        }
        Flush();
    }

    public void Dispose() => Clear();
}
