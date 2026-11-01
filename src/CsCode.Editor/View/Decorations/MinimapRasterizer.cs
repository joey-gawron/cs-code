using CsCode.Core.Theming;
using CsCode.Editor.Documents;
using CsCode.Editor.TextMate;
using CsCode.Editor.Theming;

namespace CsCode.Editor.View.Decorations;

/// <summary>One row of a minimap render: where its line is in the snapshot, and its token row (null: not coloured yet, or plain text).</summary>
internal struct MinimapLine
{
    public int Offset;
    public int Length;
    public int[]? Tokens;
}

/// <summary>
/// Colours of scope stacks for the minimap: <see cref="TokenColorResolver"/> once per distinct stack, cached as premultiplied BGRA. Built for one
/// theme on the UI thread, then used by ONE render at a time on a pool thread (the minimap never runs two), so the cache needs no lock.
/// </summary>
internal sealed class MinimapColors
{
    private readonly TokenColorResolver _resolver;
    private readonly ScopeInterner? _interner;
    private Rgba?[] _byScope = new Rgba?[256];
    private bool[] _resolved = new bool[256];

    public MinimapColors(ColorTheme theme, Rgba background, Rgba foreground, ScopeInterner? interner)
    {
        _resolver = new TokenColorResolver(theme, foreground);
        _interner = interner;
        Background = background;
        Foreground = foreground;
    }

    public Rgba Background { get; }
    public Rgba Foreground { get; }

    /// <summary>The foreground of scope stack <paramref name="scopeId"/> (the default foreground when the theme leaves it plain).</summary>
    public Rgba Of(int scopeId)
    {
        if (_interner is null || scopeId <= 0) return Foreground;
        if (scopeId >= _resolved.Length)
        {
            var size = Math.Max(_resolved.Length * 2, scopeId + 1);
            Array.Resize(ref _byScope, size);
            Array.Resize(ref _resolved, size);
        }
        if (!_resolved[scopeId])
        {
            _byScope[scopeId] = _resolver.Resolve(_interner.Scopes(scopeId)).Foreground;
            _resolved[scopeId] = true;
        }
        return _byScope[scopeId] ?? Foreground;
    }
}

/// <summary>Everything one render needs, captured on the UI thread (the snapshot and the token rows are immutable); reused between renders.</summary>
internal sealed class MinimapRenderJob
{
    public int PixelWidth, PixelHeight;
    /// <summary>Device pixels per row and per character cell.</summary>
    public int RowPixels, CharPixels;
    public int MaxColumn = 120, TabSize = 4;
    public bool RenderCharacters = true;
    public int RowCount;
    public MinimapLine[] Lines = [];
    public DocumentSnapshot? Snapshot;
    public MinimapColors? Colors;

    public void EnsureCapacity(int rows)
    {
        if (Lines.Length < rows) Lines = new MinimapLine[Math.Max(rows, Lines.Length * 2)];
    }
}

/// <summary>
/// Draws minimap rows into a premultiplied BGRA pixel buffer (a pool thread): each non-blank character is a cell <see cref="MinimapRenderJob.CharPixels"/>
/// wide in its token's colour, blended over the background. <c>renderCharacters</c> on: cells are shaped by the character's class (capitals,
/// digits and brackets full height, lower case shorter, punctuation a dot), so words read as words; off: runs of non-blank characters are
/// solid blocks (VS Code's block rendering). Tabs advance to the next tab stop; a line is cut at <c>maxColumn</c>. Costs O(rows × maxColumn);
/// allocates nothing (the character buffer is reused).
/// </summary>
internal sealed class MinimapRasterizer
{
    private char[] _chars = new char[256];

    public void Render(MinimapRenderJob job, int[] pixels)
    {
        var w = job.PixelWidth;
        var h = job.PixelHeight;
        var colors = job.Colors!;
        var bg = Premultiplied(colors.Background);
        pixels.AsSpan(0, w * h).Fill(bg);
        var snapshot = job.Snapshot;
        if (snapshot is null || job.RowPixels <= 0 || job.CharPixels <= 0) return;
        var rowPx = job.RowPixels;
        var cellW = job.CharPixels;
        var blockH = Math.Max(1, rowPx - rowPx / 4); // a row of 4 px leaves a 1 px gap between lines
        var maxColumn = Math.Max(1, Math.Min(job.MaxColumn, (w + cellW - 1) / cellW));
        var tab = Math.Max(1, job.TabSize);
        var length = snapshot.Length;
        for (var r = 0; r < job.RowCount; r++)
        {
            var y0 = r * rowPx;
            if (y0 >= h) break;
            ref var line = ref job.Lines[r];
            var count = Math.Min(line.Length, maxColumn); // tabs only ever widen a line, so the first maxColumn characters are enough
            if (count <= 0 || line.Offset < 0 || line.Offset + count > length) continue;
            if (_chars.Length < count) _chars = new char[Math.Max(count, _chars.Length * 2)];
            snapshot.CopyTo(line.Offset, _chars, 0, count);
            var tokens = line.Tokens;
            var tokenIndex = 0;
            var color = colors.Foreground;
            var nextTokenAt = 0;
            var column = 0;
            var runStart = -1;
            uint runColor = 0;
            for (var i = 0; i < count && column < maxColumn; i++)
            {
                if (tokens is not null && i >= nextTokenAt)
                {
                    // advance to the token holding character i (pairs of startColumn, scopeId)
                    while (tokenIndex + 2 < tokens.Length && tokens[tokenIndex + 2] <= i) tokenIndex += 2;
                    if (tokenIndex < tokens.Length && tokens[tokenIndex] <= i)
                    {
                        color = colors.Of(tokens[tokenIndex + 1]);
                        nextTokenAt = tokenIndex + 2 < tokens.Length ? tokens[tokenIndex + 2] : int.MaxValue;
                    }
                    else nextTokenAt = tokens.Length > 0 ? tokens[tokenIndex] : int.MaxValue;
                }
                var c = _chars[i];
                if (c is ' ' or '\t' or '\r' or '\n' || char.IsWhiteSpace(c))
                {
                    if (runStart >= 0) { FillCells(pixels, w, h, runStart, column, y0, blockH, cellW, runColor); runStart = -1; }
                    column = c == '\t' ? (column / tab + 1) * tab : column + 1;
                    continue;
                }
                if (job.RenderCharacters)
                {
                    var (top, height, alpha) = Shape(c, blockH);
                    FillRect(pixels, w, h, column * cellW, y0 + top, Math.Max(1, cellW - (cellW >= 3 ? 1 : 0)), height, Blend(colors.Background, color, alpha));
                }
                else
                {
                    var blended = Blend(colors.Background, color, 0.55);
                    if (runStart >= 0 && blended != runColor) { FillCells(pixels, w, h, runStart, column, y0, blockH, cellW, runColor); runStart = -1; }
                    if (runStart < 0) { runStart = column; runColor = blended; }
                }
                column++;
            }
            if (runStart >= 0) FillCells(pixels, w, h, runStart, column, y0, blockH, cellW, runColor);
        }
    }

    /// <summary>The cell of a character: (top offset, height, opacity) within a row's block.</summary>
    private static (int Top, int Height, double Alpha) Shape(char c, int blockH)
    {
        if (char.IsUpper(c) || char.IsDigit(c) || c is '(' or ')' or '[' or ']' or '{' or '}' or '|' or '/' or '\\' or '"' or '\'')
            return (0, blockH, 0.8);
        if (char.IsLetter(c))
        {
            var hgt = Math.Max(1, blockH - Math.Max(1, blockH / 3));
            return (blockH - hgt, hgt, 0.7);
        }
        // punctuation and symbols: a lighter, lower mark
        var ph = Math.Max(1, blockH / 2);
        return (blockH - ph, ph, 0.5);
    }

    private static void FillCells(int[] pixels, int w, int h, int fromColumn, int toColumn, int y, int blockH, int cellW, uint color) =>
        FillRect(pixels, w, h, fromColumn * cellW, y, (toColumn - fromColumn) * cellW, blockH, color);

    private static void FillRect(int[] pixels, int w, int h, int x, int y, int width, int height, uint color)
    {
        var x1 = Math.Min(w, x + width);
        var y1 = Math.Min(h, y + height);
        if (x >= x1 || y >= y1 || x < 0 || y < 0) return;
        var value = unchecked((int)color);
        for (var yy = y; yy < y1; yy++) pixels.AsSpan(yy * w + x, x1 - x).Fill(value);
    }

    /// <summary><paramref name="fg"/> at <paramref name="alpha"/> (times its own) over <paramref name="bg"/>, premultiplied BGRA.</summary>
    internal static uint Blend(Rgba bg, Rgba fg, double alpha)
    {
        var a = alpha * fg.A / 255.0;
        var r = fg.R * a + bg.R * (1 - a);
        var g = fg.G * a + bg.G * (1 - a);
        var b = fg.B * a + bg.B * (1 - a);
        var outA = bg.A / 255.0 + a * (1 - bg.A / 255.0);
        // r/g/b above are straight colours composited as if the background were opaque; premultiply by the result's alpha
        return Pack(r * outA, g * outA, b * outA, outA * 255);
    }

    internal static int Premultiplied(Rgba c) => unchecked((int)Pack(c.R * c.A / 255.0, c.G * c.A / 255.0, c.B * c.A / 255.0, c.A));

    private static uint Pack(double r, double g, double b, double a) =>
        ((uint)Math.Clamp((int)Math.Round(a), 0, 255) << 24) | ((uint)Math.Clamp((int)Math.Round(r), 0, 255) << 16)
        | ((uint)Math.Clamp((int)Math.Round(g), 0, 255) << 8) | (uint)Math.Clamp((int)Math.Round(b), 0, 255);
}
