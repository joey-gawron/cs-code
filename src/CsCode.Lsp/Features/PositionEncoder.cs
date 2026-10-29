using System.Text;

namespace CsCode.Lsp.Features;

public enum PositionEncodingMode { Utf16, Utf8, Utf32 }

/// <summary>
/// Converts between CsCode columns (UTF-16 code units) and the position encoding negotiated with a server.
/// UTF-16 — the LSP default and what every document here uses — is the identity and costs nothing (no line text is even fetched).
/// Columns past the end of the line clamp to the line end, as the spec prescribes.
/// </summary>
public sealed class PositionEncoder(PositionEncodingMode kind)
{
    public static readonly PositionEncoder Utf16 = new(PositionEncodingMode.Utf16);
    public static readonly PositionEncoder Utf8 = new(PositionEncodingMode.Utf8);
    public static readonly PositionEncoder Utf32 = new(PositionEncodingMode.Utf32);

    public PositionEncodingMode Kind { get; } = kind;
    public bool IsIdentity => Kind == PositionEncodingMode.Utf16;

    public string Name => Kind switch { PositionEncodingMode.Utf8 => "utf-8", PositionEncodingMode.Utf32 => "utf-32", _ => "utf-16" };

    public static PositionEncoder FromName(string? name) => name switch
    {
        "utf-8" => Utf8,
        "utf-32" => Utf32,
        _ => Utf16,
    };

    /// <summary>UTF-16 column -> negotiated-encoding column on <paramref name="line"/>.</summary>
    public int ToLsp(string line, int utf16Column)
    {
        if (IsIdentity) return utf16Column;
        var end = Math.Clamp(utf16Column, 0, line.Length);
        // Pure-ASCII prefix (by far the common case): every code unit is one byte / one code point, so all encodings coincide. Vectorised check.
        if (System.Text.Ascii.IsValid(line.AsSpan(0, end))) return utf16Column;
        var count = 0;
        for (var i = 0; i < end; i++)
        {
            var c = line[i];
            if (Kind == PositionEncodingMode.Utf8)
            {
                if (c < 0x80) count += 1;
                else if (c < 0x800) count += 2;
                else if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1])) { count += 4; i++; }
                else count += 3;
            }
            else
            {
                if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1])) i++;
                count += 1;
            }
        }
        return count + Math.Max(0, utf16Column - end); // columns past the end stay past the end
    }

    /// <summary>Negotiated-encoding column -> UTF-16 column on <paramref name="line"/> (clamped to the line length).</summary>
    public int FromLsp(string line, int column)
    {
        if (IsIdentity) return column;
        var probe = Math.Clamp(column, 0, line.Length);
        if (System.Text.Ascii.IsValid(line.AsSpan(0, probe))) return probe; // ASCII prefix: encodings coincide (and an overshooting column clamps to the line end)
        var units = 0;
        var i = 0;
        while (i < line.Length && units < column)
        {
            var c = line[i];
            if (Kind == PositionEncodingMode.Utf8)
            {
                if (c < 0x80) units += 1;
                else if (c < 0x800) units += 2;
                else if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1])) { units += 4; i++; }
                else units += 3;
            }
            else
            {
                if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1])) i++;
                units += 1;
            }
            i++;
        }
        return i;
    }

    /// <summary>UTF-8 byte count of a string (helper for tests and size accounting).</summary>
    public static int Utf8Length(string s) => Encoding.UTF8.GetByteCount(s);
}
