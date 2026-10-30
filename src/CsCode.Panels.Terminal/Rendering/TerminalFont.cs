using System.Globalization;
using Avalonia.Media;

namespace CsCode.Panels.Terminal.Rendering;

/// <summary>
/// The terminal's font and cell geometry: the first family of the list that the system has (<c>terminal.integrated.fontFamily</c>, else the
/// editor's), its bold/italic faces, the cell size (the advance of 'M' plus letter spacing, the line height times
/// <c>terminal.integrated.lineHeight</c>), the baseline and decoration positions, and a bounded cache of glyph lookups with system font
/// fallback for characters the font lacks (CJK, emoji, symbols). UI thread only.
/// </summary>
internal sealed class TerminalFont
{
    private const int MaxCachedGlyphs = 8192;

    private readonly GlyphTypeface[] _faces = new GlyphTypeface[4]; // regular, bold, italic, bold italic
    private readonly Dictionary<(int CodePoint, int Face), (GlyphTypeface Face, ushort Glyph)> _glyphs = new();

    private TerminalFont(FontFamily family, GlyphTypeface regular, double size, double lineHeight, double letterSpacing)
    {
        Family = family;
        Size = size;
        _faces[0] = regular;
        _faces[1] = Face(family, FontWeight.Bold, FontStyle.Normal) ?? regular;
        _faces[2] = Face(family, FontWeight.Normal, FontStyle.Italic) ?? regular;
        _faces[3] = Face(family, FontWeight.Bold, FontStyle.Italic) ?? _faces[1];
        var m = regular.Metrics;
        var scale = size / m.DesignEmHeight;
        var ascent = -m.Ascent * scale;
        var descent = m.Descent * scale;
        var gap = Math.Max(0, m.LineGap * scale);
        var advance = Advance(regular, 'M') * scale;
        if (advance <= 0) advance = size * 0.6;
        CellWidth = Math.Max(1, Math.Round(advance + letterSpacing));
        CellHeight = Math.Max(1, Math.Ceiling((ascent + descent + gap) * Math.Clamp(lineHeight, 1, 3)));
        Baseline = Math.Round((CellHeight - (ascent + descent)) / 2 + ascent);
        UnderlineY = Math.Min(CellHeight - 1, Baseline + Math.Max(1, -m.UnderlinePosition * scale));
        LineThickness = Math.Max(1, Math.Round(m.UnderlineThickness * scale));
        StrikeY = Math.Round(Baseline - Math.Max(1, m.StrikethroughPosition * scale));
    }

    public FontFamily Family { get; }
    public double Size { get; }
    public double CellWidth { get; }
    public double CellHeight { get; }
    public double Baseline { get; }
    public double UnderlineY { get; }
    public double StrikeY { get; }
    public double LineThickness { get; }
    public string Key => $"{Family}|{Size}|{CellWidth}|{CellHeight}";

    /// <summary>The first family in <paramref name="families"/> (comma separated) the system can load, else the default font.</summary>
    public static TerminalFont Create(string families, double size, double lineHeight = 1, double letterSpacing = 0)
    {
        size = Math.Clamp(size, 4, 100);
        foreach (var name in families.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var family = new FontFamily(name.Trim('\'', '"'));
            if (Face(family, FontWeight.Normal, FontStyle.Normal) is { } face && face.FamilyName.Length > 0 && Lookup(face, 'M') != 0)
                return new TerminalFont(family, face, size, lineHeight, letterSpacing);
        }
        var fallback = FontFamily.Default;
        var regular = Face(fallback, FontWeight.Normal, FontStyle.Normal) ?? throw new InvalidOperationException("No font is available.");
        return new TerminalFont(fallback, regular, size, lineHeight, letterSpacing);
    }

    private static GlyphTypeface? Face(FontFamily family, FontWeight weight, FontStyle style) =>
        FontManager.Current.TryGetGlyphTypeface(new Typeface(family, style, weight), out var face) ? face : null;

    private static double Advance(GlyphTypeface face, int codePoint)
    {
        var glyph = Lookup(face, codePoint);
        return glyph != 0 && face.TryGetHorizontalGlyphAdvance(glyph, out var advance) ? advance : 0;
    }

    private static ushort Lookup(GlyphTypeface face, int codePoint) =>
        face.CharacterToGlyphMap.TryGetGlyph(codePoint, out var glyph) ? glyph : (ushort)0;

    /// <summary>The face and glyph for a character (0 = draw nothing): the terminal font's, else a system fallback's (cached, bounded).</summary>
    public (GlyphTypeface Face, ushort Glyph) Glyph(int codePoint, bool bold, bool italic)
    {
        var index = (bold ? 1 : 0) + (italic ? 2 : 0);
        if (_glyphs.TryGetValue((codePoint, index), out var hit)) return hit;
        var face = _faces[index];
        var glyph = Lookup(face, codePoint);
        if (glyph == 0 && codePoint > 0x7f
            && FontManager.Current.TryMatchCharacter(codePoint, italic ? FontStyle.Italic : FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal,
                FontStretch.Normal, Family, CultureInfo.CurrentUICulture, out var typeface)
            && FontManager.Current.TryGetGlyphTypeface(typeface, out var fallback))
        {
            var g = Lookup(fallback, codePoint);
            if (g != 0) { face = fallback; glyph = g; }
        }
        if (_glyphs.Count >= MaxCachedGlyphs) _glyphs.Clear();
        return _glyphs[(codePoint, index)] = (face, glyph);
    }

    public GlyphTypeface RegularFace => _faces[0];
}
