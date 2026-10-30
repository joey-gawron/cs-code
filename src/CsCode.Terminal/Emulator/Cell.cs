using System.Runtime.CompilerServices;

namespace CsCode.Terminal.Emulator;

/// <summary>Underline styles (SGR 4, 4:n, 21).</summary>
public enum UnderlineStyle : byte { None = 0, Single = 1, Double = 2, Curly = 3, Dotted = 4, Dashed = 5 }

/// <summary>Rendition flags of a cell (SGR) plus the cell's place in a wide character.</summary>
[Flags]
public enum CellFlags : uint
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    // bits 3-5: UnderlineStyle
    Blink = 1 << 6,
    Inverse = 1 << 7,
    Invisible = 1 << 8,
    Strikethrough = 1 << 9,
    Overline = 1 << 10,
    /// <summary>First cell of a double-width character (the next cell is its <see cref="WideSpacer"/>).</summary>
    Wide = 1 << 11,
    /// <summary>Second cell of a double-width character: draws nothing.</summary>
    WideSpacer = 1 << 12,
    /// <summary>DECSCA protected (not erased by DECSED/DECSEL).</summary>
    Protected = 1 << 13,
}

/// <summary>
/// Colours packed into 32 bits: the top byte is the kind (0 default, 1 palette index in the low byte, 2 RGB in the low 24 bits).
/// </summary>
public static class TermColor
{
    public const uint Default = 0;
    private const uint IndexedTag = 1u << 24;
    private const uint RgbTag = 2u << 24;

    public static uint Indexed(int index) => IndexedTag | (uint)(index & 0xff);
    public static uint Rgb(int r, int g, int b) => RgbTag | ((uint)(r & 0xff) << 16) | ((uint)(g & 0xff) << 8) | (uint)(b & 0xff);

    public static bool IsDefault(uint c) => c >> 24 == 0;
    public static bool IsIndexed(uint c) => c >> 24 == 1;
    public static bool IsRgb(uint c) => c >> 24 == 2;
    public static int Index(uint c) => (int)(c & 0xff);
    public static (byte R, byte G, byte B) RgbOf(uint c) => ((byte)(c >> 16), (byte)(c >> 8), (byte)c);

    public static string Describe(uint c) => IsDefault(c) ? "default" : IsIndexed(c) ? "idx" + Index(c) : $"#{c & 0xffffff:x6}";
}

/// <summary>
/// The rendition a printed character gets: colours, flags, underline colour and an OSC 8 hyperlink id. A value type compared bit for
/// bit, so the renderer can cut rows into runs of equal attributes cheaply.
/// </summary>
public readonly record struct CellAttributes(uint Foreground, uint Background, CellFlags Flags, uint UnderlineColor, int LinkId)
{
    public static readonly CellAttributes Default = new(TermColor.Default, TermColor.Default, CellFlags.None, TermColor.Default, 0);

    public UnderlineStyle Underline => (UnderlineStyle)(((uint)Flags >> 3) & 0x7);

    public CellAttributes WithUnderline(UnderlineStyle style) =>
        this with { Flags = (CellFlags)(((uint)Flags & ~(0x7u << 3)) | ((uint)style << 3)) };

    public CellAttributes With(CellFlags flag, bool on) => this with { Flags = on ? Flags | flag : Flags & ~flag };

    public bool Has(CellFlags flag) => (Flags & flag) != 0;

    /// <summary>Attributes without the per-cell layout bits (wide, spacer): what SGR controls.</summary>
    public CellAttributes Rendition => this with { Flags = Flags & ~(CellFlags.Wide | CellFlags.WideSpacer) };

    public override string ToString()
    {
        var parts = new List<string>();
        if (!TermColor.IsDefault(Foreground)) parts.Add("fg=" + TermColor.Describe(Foreground));
        if (!TermColor.IsDefault(Background)) parts.Add("bg=" + TermColor.Describe(Background));
        foreach (var f in new[] { CellFlags.Bold, CellFlags.Dim, CellFlags.Italic, CellFlags.Blink, CellFlags.Inverse, CellFlags.Invisible, CellFlags.Strikethrough, CellFlags.Overline })
            if (Has(f)) parts.Add(f.ToString().ToLowerInvariant());
        if (Underline != UnderlineStyle.None) parts.Add("ul=" + Underline.ToString().ToLowerInvariant());
        if (!TermColor.IsDefault(UnderlineColor)) parts.Add("ulc=" + TermColor.Describe(UnderlineColor));
        if (LinkId != 0) parts.Add("link=" + LinkId);
        return parts.Count == 0 ? "default" : string.Join(' ', parts);
    }
}

/// <summary>
/// One grid cell. <see cref="Rune"/> is a Unicode scalar value, 0 for an empty (never written) cell, or a negative index into the row's
/// cluster table when the cell holds a grapheme cluster (a base character with combining marks, an emoji sequence).
/// </summary>
public struct Cell(int rune, CellAttributes attributes)
{
    public int Rune = rune;
    public CellAttributes Attributes = attributes;

    public static readonly Cell Blank = new(0, CellAttributes.Default);

    public readonly bool IsWide => (Attributes.Flags & CellFlags.Wide) != 0;
    public readonly bool IsSpacer => (Attributes.Flags & CellFlags.WideSpacer) != 0;

    /// <summary>A blank cell with the given background (erase uses the current background: BCE, as xterm, tmux and libvterm do).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Cell Erased(in CellAttributes current) =>
        TermColor.IsDefault(current.Background) && current.LinkId == 0 && (current.Flags & CellFlags.Protected) == 0
            ? Blank
            : new Cell(0, new CellAttributes(TermColor.Default, current.Background, CellFlags.None, TermColor.Default, 0));

    /// <summary>True for a never-written cell with default attributes (trimmed off the end of scrollback rows).</summary>
    public readonly bool IsBlankDefault => Rune is 0 or ' ' && Attributes == CellAttributes.Default;
}
