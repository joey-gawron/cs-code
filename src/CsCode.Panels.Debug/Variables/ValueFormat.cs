using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.Panels.Debug.Variables;

public enum NumberFormat { Decimal, Hexadecimal }

/// <summary>Client-side presentation of adapter values: single-line truncation, string quoting and integer radix.</summary>
public static partial class ValueFormat
{
    /// <summary>Longest value shown in a row; the full text stays available for the tooltip, copy and editing.</summary>
    public const int MaxDisplayChars = 200;

    [GeneratedRegex(@"^(system\.)?(unsigned\s+|signed\s+)?(u?int(8|16|32|64|128)?(_t)?|u?long( long)?|u?short|s?byte|nuint|nint|size_t|char\d*_t)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntegerType();

    [GeneratedRegex(@"(^|\.)(string|str|std::string|std::wstring|wstring|string_view)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StringType();

    public static bool IsIntegerType(string? type) => type is not null && IntegerType().IsMatch(type.Trim());

    public static bool IsStringType(string? type) => type is not null && StringType().IsMatch(type.Trim());

    /// <summary>Bit width implied by a type name (32 when it cannot be told), used for the two's-complement hex of negative numbers.</summary>
    public static int WidthOf(string? type)
    {
        if (type is null) return 32;
        var t = type.ToLowerInvariant();
        if (t.Contains("128")) return 128;
        if (t.Contains("64") || t.Contains("long") || t.Contains("nint") || t.Contains("size_t")) return 64;
        if (t.Contains("16") || t.Contains("short")) return 16;
        if (t.Contains('8') || t.Contains("byte")) return 8;
        return 32;
    }

    /// <summary>The text a row shows for <paramref name="value"/>: quoted when it is a string, hex/decimal for integers, one line, ≤ 200 chars.</summary>
    public static string Display(string value, string? type, NumberFormat format)
    {
        var text = value;
        if (format == NumberFormat.Hexadecimal && IsIntegerType(type)) text = ToHexadecimal(text, type) ?? text;
        else if (format == NumberFormat.Decimal && IsIntegerType(type)) text = ToDecimal(text) ?? text;
        else if (IsStringType(type) && !LooksQuoted(text) && !LooksLikeNull(text)) text = "\"" + text + "\"";
        return SingleLine(text, MaxDisplayChars);
    }

    private static bool LooksQuoted(string s) => s.Length >= 2 && (s[0] is '"' or '\'' or '@' or '$' or 'L' or 'u' or 'U' or 'R') && s.Contains('"');

    private static bool LooksLikeNull(string s) => s is "null" or "nullptr" or "None" or "<null>" or "0x0000000000000000" or "0x0";

    /// <summary>Collapses control characters so a multi-line value cannot break the row height, then clips to <paramref name="max"/> with an ellipsis.</summary>
    public static string SingleLine(string text, int max)
    {
        var clipped = text.Length > max;
        var length = clipped ? max : text.Length;
        var needsRewrite = false;
        for (var i = 0; i < length; i++)
            if (text[i] < ' ') { needsRewrite = true; break; }
        if (!needsRewrite) return clipped ? text[..max] + "…" : text;
        var sb = new StringBuilder(length + 1);
        for (var i = 0; i < length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\n': sb.Append('⏎'); break;
                case '\r': break;
                case '\t': sb.Append(' '); break;
                default: sb.Append(c < ' ' ? '·' : c); break;
            }
        }
        if (clipped) sb.Append('…');
        return sb.ToString();
    }

    private static bool TryParseInteger(string text, out BigInteger value)
    {
        value = default;
        var s = text.Trim();
        if (s.Length == 0) return false;
        // Adapters decorate numbers ("65 'A'", "0x41 (65)"): the leading token is the number.
        var space = s.IndexOf(' ');
        if (space > 0) s = s[..space];
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return BigInteger.TryParse("0" + s[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        return BigInteger.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    public static string? ToHexadecimal(string text, string? type)
    {
        if (!TryParseInteger(text, out var n)) return null;
        if (text.TrimStart().StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return text.Trim();
        if (n.Sign < 0)
        {
            var width = WidthOf(type);
            n += BigInteger.One << width; // two's complement of the declared width
        }
        var hex = n.ToString("X", CultureInfo.InvariantCulture).TrimStart('0');
        return "0x" + (hex.Length == 0 ? "0" : hex);
    }

    public static string? ToDecimal(string text)
    {
        if (!text.TrimStart().StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return null;
        return TryParseInteger(text, out var n) ? n.ToString(CultureInfo.InvariantCulture) : null;
    }
}
