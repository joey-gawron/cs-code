using System.Globalization;
using System.Text.RegularExpressions;

namespace CsCode.Editor.Features;

/// <summary>A colour as the picker edits it: hue in degrees [0, 360), saturation, value and alpha in [0, 1].</summary>
/// <remarks>The picker keeps HSV rather than RGB so a grey or black colour keeps the hue the user chose (RGB has none to give back).</remarks>
internal readonly record struct HsvaColor(double H, double S, double V, double A)
{
    public static HsvaColor FromRgba(double r, double g, double b, double a, double? hueHint = null)
    {
        r = Math.Clamp(r, 0, 1);
        g = Math.Clamp(g, 0, 1);
        b = Math.Clamp(b, 0, 1);
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double h;
        if (delta <= 0) h = hueHint ?? 0;
        else if (max == r) h = 60 * ((g - b) / delta % 6);
        else if (max == g) h = 60 * ((b - r) / delta + 2);
        else h = 60 * ((r - g) / delta + 4);
        if (h < 0) h += 360;
        return new HsvaColor(h % 360, max <= 0 ? 0 : delta / max, max, Math.Clamp(a, 0, 1));
    }

    public (double R, double G, double B) ToRgb()
    {
        var c = V * S;
        var (r, g, b) = ColorFormats.Sector(H, c);
        var m = V - c;
        return (r + m, g + m, b + m);
    }

    public Avalonia.Media.Color ToAvalonia()
    {
        var (r, g, b) = ToRgb();
        return Avalonia.Media.Color.FromArgb(ColorFormats.Byte(A), ColorFormats.Byte(r), ColorFormats.Byte(g), ColorFormats.Byte(b));
    }
}

/// <summary>
/// CSS colour syntax for the built-in colour detection (<c>editor.defaultColorDecorators</c>) and its presentations: <c>#rgb</c>,
/// <c>#rgba</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c>, <c>rgb()</c> / <c>rgba()</c> and <c>hsl()</c> / <c>hsla()</c> with comma or space
/// separated arguments (CSS Color 4's <c>/ alpha</c> too). Pure: unit-tested without a view.
/// </summary>
internal static class ColorFormats
{
    /// <summary>A found colour: offset and length in the scanned text, components in [0, 1].</summary>
    internal readonly record struct Found(int Start, int Length, double R, double G, double B, double A);

    // A hex colour must not continue a word (`a#fff`, `&#123;`) or be followed by one (`#fffg`, `#abc-def`); a function name must not either
    // (`torgb(`). Arguments stay on one line and are short: the regex never reads far.
    private static readonly Regex Pattern = new(
        @"(?<![\w#&-])#(?<hex>[0-9a-f]{8}|[0-9a-f]{6}|[0-9a-f]{3,4})(?![\w-])|(?<![\w-])(?<fn>rgba?|hsla?)\((?<args>[^()\r\n]{1,80})\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Every colour in <paramref name="text"/> (at most <paramref name="max"/>), in order.</summary>
    public static void Find(string text, int max, List<Found> into, int baseOffset = 0)
    {
        if (into.Count >= max) return;
        foreach (Match m in Pattern.Matches(text))
        {
            if (TryParse(m.ValueSpan, out var r, out var g, out var b, out var a))
            {
                into.Add(new Found(baseOffset + m.Index, m.Length, r, g, b, a));
                if (into.Count >= max) return;
            }
        }
    }

    /// <summary>Parses one whole colour (<c>#ff0000</c>, <c>rgb(255, 0, 0)</c>, <c>hsl(0 100% 50% / 0.5)</c>...).</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out double r, out double g, out double b, out double a)
    {
        r = g = b = 0;
        a = 1;
        text = text.Trim();
        if (text.Length > 1 && text[0] == '#') return TryParseHex(text[1..], out r, out g, out b, out a);
        var open = text.IndexOf('(');
        if (open <= 0 || text[^1] != ')') return false;
        var name = text[..open].ToString().ToLowerInvariant();
        var args = SplitArgs(text[(open + 1)..^1]);
        if (args is null || args.Count is < 3 or > 4) return false;
        switch (name)
        {
            case "rgb":
            case "rgba":
                if (!Channel(args[0], out r) || !Channel(args[1], out g) || !Channel(args[2], out b)) return false;
                break;
            case "hsl":
            case "hsla":
                if (!Hue(args[0], out var h) || !Percent(args[1], out var s) || !Percent(args[2], out var l)) return false;
                (r, g, b) = HslToRgb(h, s, l);
                break;
            default:
                return false;
        }
        return args.Count < 4 || Alpha(args[3], out a);
    }

    private static bool TryParseHex(ReadOnlySpan<char> hex, out double r, out double g, out double b, out double a)
    {
        r = g = b = 0;
        a = 1;
        foreach (var c in hex)
            if (!char.IsAsciiHexDigit(c)) return false;
        static int D(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
        switch (hex.Length)
        {
            case 3 or 4:
                r = D(hex[0]) * 17 / 255d;
                g = D(hex[1]) * 17 / 255d;
                b = D(hex[2]) * 17 / 255d;
                if (hex.Length == 4) a = D(hex[3]) * 17 / 255d;
                return true;
            case 6 or 8:
                r = (D(hex[0]) * 16 + D(hex[1])) / 255d;
                g = (D(hex[2]) * 16 + D(hex[3])) / 255d;
                b = (D(hex[4]) * 16 + D(hex[5])) / 255d;
                if (hex.Length == 8) a = (D(hex[6]) * 16 + D(hex[7])) / 255d;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The arguments of a colour function: <c>1, 2, 3</c>, <c>1, 2, 3, 0.5</c>, <c>1 2 3</c> or <c>1 2 3 / 0.5</c>.</summary>
    private static List<string>? SplitArgs(ReadOnlySpan<char> inner)
    {
        var s = inner.ToString();
        if (s.Contains(','))
        {
            if (s.Contains('/')) return null;
            var parts = s.Split(',', StringSplitOptions.TrimEntries);
            return parts.Any(p => p.Length == 0 || p.Contains(' ')) ? null : [.. parts];
        }
        var slash = s.Split('/', StringSplitOptions.TrimEntries);
        if (slash.Length > 2) return null;
        var list = slash[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (list.Count != 3) return null;
        if (slash.Length == 2)
        {
            if (slash[1].Length == 0 || slash[1].Contains(' ')) return null;
            list.Add(slash[1]);
        }
        return list;
    }

    private static bool Number(string s, out double value) =>
        double.TryParse(s, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>A percentage in [0%, 100%] as a unit value.</summary>
    private static bool Percent(string s, out double value)
    {
        value = 0;
        if (!s.EndsWith('%') || !Number(s[..^1], out var p) || p is < 0 or > 100) return false;
        value = p / 100;
        return true;
    }

    /// <summary>An RGB channel: 0-255 or 0%-100%.</summary>
    private static bool Channel(string s, out double value)
    {
        if (s.EndsWith('%')) return Percent(s, out value);
        value = 0;
        if (!Number(s, out var n) || n is < 0 or > 255) return false;
        value = n / 255;
        return true;
    }

    private static bool Hue(string s, out double value)
    {
        if (s.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) s = s[..^3];
        if (!Number(s, out value)) return false;
        value = (value % 360 + 360) % 360;
        return true;
    }

    private static bool Alpha(string s, out double value)
    {
        if (s.EndsWith('%')) return Percent(s, out value);
        return Number(s, out value) && value is >= 0 and <= 1;
    }

    // ---- conversions --------------------------------------------------------------------------------------------

    public static (double R, double G, double B) HslToRgb(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var (r, g, b) = Sector(h, c);
        var m = l - c / 2;
        return (r + m, g + m, b + m);
    }

    /// <summary>The hue's RGB before the lightness offset, for chroma <paramref name="c"/> (shared by HSV and HSL).</summary>
    internal static (double R, double G, double B) Sector(double hue, double c)
    {
        var h = (hue % 360 + 360) % 360 / 60;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        return (int)h switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
    }

    public static (double H, double S, double L) RgbToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var d = max - min;
        if (d <= 0) return (0, 0, l);
        var s = d / (1 - Math.Abs(2 * l - 1));
        var h = HsvaColor.FromRgba(r, g, b, 1).H;
        return (h, Math.Clamp(s, 0, 1), l);
    }

    public static byte Byte(double unit) => (byte)Math.Clamp((int)Math.Round(unit * 255, MidpointRounding.AwayFromZero), 0, 255);

    // ---- presentations (what the built-in provider offers the picker) -------------------------------------------

    /// <summary>VS Code's default presentations of a colour, in its order: <c>rgb()</c>, <c>hsl()</c>, hex (<c>…a</c> forms when not opaque).</summary>
    public static IReadOnlyList<string> Presentations(double r, double g, double b, double a, bool upperHex = false) =>
        [FormatRgb(r, g, b, a), FormatHsl(r, g, b, a), FormatHex(r, g, b, a, upperHex)];

    public static string FormatRgb(double r, double g, double b, double a) => Opaque(a)
        ? $"rgb({Byte(r)}, {Byte(g)}, {Byte(b)})"
        : $"rgba({Byte(r)}, {Byte(g)}, {Byte(b)}, {FormatAlpha(a)})";

    public static string FormatHsl(double r, double g, double b, double a)
    {
        var (h, s, l) = RgbToHsl(Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
        var hh = (int)Math.Round(h, MidpointRounding.AwayFromZero) % 360;
        var ss = (int)Math.Round(s * 100, MidpointRounding.AwayFromZero);
        var ll = (int)Math.Round(l * 100, MidpointRounding.AwayFromZero);
        return Opaque(a) ? $"hsl({hh}, {ss}%, {ll}%)" : $"hsla({hh}, {ss}%, {ll}%, {FormatAlpha(a)})";
    }

    public static string FormatHex(double r, double g, double b, double a, bool upper = false)
    {
        var format = upper ? "X2" : "x2";
        var text = "#" + Byte(r).ToString(format, CultureInfo.InvariantCulture) + Byte(g).ToString(format, CultureInfo.InvariantCulture) + Byte(b).ToString(format, CultureInfo.InvariantCulture);
        return Opaque(a) ? text : text + Byte(a).ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>Alpha as VS Code writes it: at most two decimals, no trailing zeros (<c>0.5</c>, <c>0.25</c>, <c>1</c>).</summary>
    public static string FormatAlpha(double a) => Math.Round(Math.Clamp(a, 0, 1), 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture);

    private static bool Opaque(double a) => a >= 0.995;

    /// <summary>
    /// The presentation that matches how the colour is written now (VS Code's <c>guessColorPresentation</c>): the one whose label is the text,
    /// else the first whose label starts like it (<c>rgba(</c> for an <c>rgba(…)</c> text, <c>#</c>… for a hex), else the first.
    /// </summary>
    public static int Guess(IReadOnlyList<string> labels, string? originalText)
    {
        if (labels.Count == 0 || string.IsNullOrEmpty(originalText)) return 0;
        var original = originalText.Trim();
        for (var i = 0; i < labels.Count; i++)
            if (string.Equals(labels[i], original, StringComparison.OrdinalIgnoreCase)) return i;
        var open = original.IndexOf('(');
        var prefix = (open > 0 ? original[..(open + 1)] : original.StartsWith('#') ? "#" : original).ToLowerInvariant();
        for (var i = 0; i < labels.Count; i++)
            if (labels[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return i;
        // rgb( matches rgba( and hsl( hsla( : the alpha forms differ only in the letter before the parenthesis
        if (prefix.Length > 1 && prefix.EndsWith('('))
        {
            var stem = prefix.TrimEnd('(').TrimEnd('a');
            for (var i = 0; i < labels.Count; i++)
                if (labels[i].StartsWith(stem, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return 0;
    }

    /// <summary>Whether <paramref name="text"/> is a hex colour written in upper case (its presentation keeps the case).</summary>
    public static bool IsUpperHex(string? text) =>
        text is { Length: > 1 } t && t[0] == '#' && t.Skip(1).Any(char.IsAsciiLetterUpper) && !t.Skip(1).Any(char.IsAsciiLetterLower);

    /// <summary>Relative luminance (sRGB, WCAG) of an opaque colour: the picker's header writes black text on light colours.</summary>
    public static double Luminance(double r, double g, double b)
    {
        static double L(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        return 0.2126 * L(r) + 0.7152 * L(g) + 0.0722 * L(b);
    }
}
