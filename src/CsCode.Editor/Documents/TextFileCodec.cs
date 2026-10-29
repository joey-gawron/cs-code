using System.Text;
using System.Text.Unicode;

namespace CsCode.Editor.Documents;

/// <summary>An encoding as the editor/status bar names it (VS Code ids) plus how to read/write it.</summary>
public sealed record FileEncoding(string Name, Encoding Encoding, byte[] Bom)
{
    public static readonly FileEncoding Utf8 = new("utf8", new UTF8Encoding(false), []);
    public static readonly FileEncoding Utf8Bom = new("utf8bom", new UTF8Encoding(false), [0xEF, 0xBB, 0xBF]);
    public static readonly FileEncoding Utf16Le = new("utf16le", new UnicodeEncoding(false, false), [0xFF, 0xFE]);
    public static readonly FileEncoding Utf16Be = new("utf16be", new UnicodeEncoding(true, false), [0xFE, 0xFF]);
    // BOM-less UTF-16 (detected heuristically); kept distinct so we do not invent a BOM on save.
    public static readonly FileEncoding Utf16LeNoBom = new("utf16le", new UnicodeEncoding(false, false), []);
    public static readonly FileEncoding Utf16BeNoBom = new("utf16be", new UnicodeEncoding(true, false), []);
    public static readonly FileEncoding Latin1 = new("iso88591", Encoding.Latin1, []);

    private static readonly Lazy<FileEncoding> Win1252Lazy = new(() =>
    {
        // System.Text.Encoding.CodePages ships in the shared framework; registration is idempotent.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return new FileEncoding("windows1252", Encoding.GetEncoding(1252), []);
    });

    public static FileEncoding Windows1252 => Win1252Lazy.Value;

    public bool HasBom => Bom.Length > 0;

    /// <summary>Maps a <c>files.encoding</c> value (VS Code ids, case-insensitive, tolerant of '-'/'_') to an encoding.</summary>
    public static FileEncoding? FromSettingName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.Replace("-", "").Replace("_", "").ToLowerInvariant() switch
        {
            "utf8" => Utf8,
            "utf8bom" => Utf8Bom,
            "utf16le" => Utf16Le,
            "utf16be" => Utf16Be,
            "windows1252" or "cp1252" => Windows1252,
            "iso88591" or "latin1" => Latin1,
            _ => null,
        };
    }
}

public sealed record EolInfo(string Eol, int Lf, int CrLf, int Cr)
{
    public bool IsMixed => (Lf > 0 ? 1 : 0) + (CrLf > 0 ? 1 : 0) + (Cr > 0 ? 1 : 0) > 1;
}

/// <summary>Encoding and end-of-line detection / conversion. Pure functions, safe on any thread.</summary>
public static class TextFileCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>
    /// Detection order: BOM; an explicit non-default <paramref name="configured"/> encoding; strict UTF-8; BOM-less UTF-16
    /// heuristic (NUL bytes in a regular pattern); fallback Windows-1252 (a superset of Latin-1 for printable text).
    /// </summary>
    public static FileEncoding Detect(ReadOnlySpan<byte> bytes, FileEncoding? configured = null)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return FileEncoding.Utf8Bom;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return FileEncoding.Utf16Le;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return FileEncoding.Utf16Be;
        if (configured is not null && configured.Name != "utf8") return configured;
        // BOM-less UTF-16 is ALSO valid UTF-8 (NUL is a legal byte), so the NUL-pattern check must come first.
        if (GuessBomlessUtf16(bytes) is { } utf16) return utf16;
        return Utf8.IsValid(bytes) ? FileEncoding.Utf8 : FileEncoding.Windows1252;
    }

    private static FileEncoding? GuessBomlessUtf16(ReadOnlySpan<byte> bytes)
    {
        var sample = bytes[..Math.Min(bytes.Length, 4096)];
        if (sample.Length < 4) return null;
        int evenZero = 0, oddZero = 0;
        for (var i = 0; i < sample.Length; i++)
            if (sample[i] == 0)
            {
                if ((i & 1) == 0) evenZero++;
                else oddZero++;
            }
        var half = sample.Length / 2;
        // ASCII-heavy UTF-16: one byte of each pair is NUL.
        if (oddZero > half * 0.3 && evenZero == 0) return FileEncoding.Utf16LeNoBom;
        if (evenZero > half * 0.3 && oddZero == 0) return FileEncoding.Utf16BeNoBom;
        return null;
    }

    /// <summary>Decodes <paramref name="bytes"/> (BOM stripped). Invalid sequences become U+FFFD.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, FileEncoding encoding)
    {
        var skip = encoding.HasBom && bytes.Length >= encoding.Bom.Length && bytes[..encoding.Bom.Length].SequenceEqual(encoding.Bom)
            ? encoding.Bom.Length
            : 0;
        return encoding.Encoding.GetString(bytes[skip..]);
    }

    /// <summary>Strict check: does <paramref name="bytes"/> decode losslessly as UTF-8? (Used to decide the Windows-1252 fallback.)</summary>
    public static bool IsStrictUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Encodes <paramref name="text"/> with the encoding's BOM (when it has one).</summary>
    public static byte[] Encode(string text, FileEncoding encoding)
    {
        var body = encoding.Encoding.GetBytes(text);
        if (!encoding.HasBom) return body;
        var all = new byte[encoding.Bom.Length + body.Length];
        encoding.Bom.CopyTo(all, 0);
        body.CopyTo(all, encoding.Bom.Length);
        return all;
    }

    /// <summary>Counts line terminators; the dominant one wins (ties: CRLF, then LF, then CR). No terminators: <paramref name="fallback"/>.</summary>
    public static EolInfo DetectEol(ReadOnlySpan<char> text, string fallback = "\n")
    {
        int lf = 0, crlf = 0, cr = 0;
        var i = 0;
        ReadOnlySpan<char> breaks = ['\r', '\n'];
        while (i < text.Length)
        {
            var rel = text[i..].IndexOfAny(breaks);
            if (rel < 0) break;
            i += rel;
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i += 2;
                }
                else
                {
                    cr++;
                    i++;
                }
            }
            else
            {
                lf++;
                i++;
            }
        }
        if (lf + crlf + cr == 0) return new EolInfo(fallback, 0, 0, 0);
        var eol = crlf >= lf && crlf >= cr ? "\r\n" : lf >= cr ? "\n" : "\r";
        return new EolInfo(eol, lf, crlf, cr);
    }

    /// <summary>Rewrites every \r\n, \r and \n to <paramref name="eol"/>. Returns the same instance when nothing changes.</summary>
    public static string NormalizeEol(string text, string eol)
    {
        var info = DetectEol(text, eol);
        var uniform = !info.IsMixed && (info.Lf + info.CrLf + info.Cr == 0 || info.Eol == eol);
        if (uniform) return text;
        var sb = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                sb.Append(eol);
            }
            else if (c == '\n') sb.Append(eol);
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
