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
            var other => FromCodePageTable(other),
        };
    }

    // ---- the encodings Reopen / Save with Encoding offer (VS Code's ids, labels and order) ----

    private static readonly (string Id, string Label, string Short, int CodePage)[] Table =
    [
        ("utf8", "UTF-8", "UTF-8", 0),
        ("utf8bom", "UTF-8 with BOM", "UTF-8 with BOM", 0),
        ("utf16le", "UTF-16 LE", "UTF-16 LE", 0),
        ("utf16be", "UTF-16 BE", "UTF-16 BE", 0),
        ("windows1252", "Western (Windows 1252)", "Windows 1252", 0),
        ("iso88591", "Western (ISO 8859-1)", "ISO 8859-1", 0),
        ("iso88593", "Western (ISO 8859-3)", "ISO 8859-3", 28593),
        ("iso885915", "Western (ISO 8859-15)", "ISO 8859-15", 28605),
        ("macroman", "Western (Mac Roman)", "Mac Roman", 10000),
        ("cp437", "DOS (CP 437)", "CP437", 437),
        ("windows1256", "Arabic (Windows 1256)", "Windows 1256", 1256),
        ("iso88596", "Arabic (ISO 8859-6)", "ISO 8859-6", 28596),
        ("windows1257", "Baltic (Windows 1257)", "Windows 1257", 1257),
        ("iso88594", "Baltic (ISO 8859-4)", "ISO 8859-4", 28594),
        ("windows1250", "Central European (Windows 1250)", "Windows 1250", 1250),
        ("iso88592", "Central European (ISO 8859-2)", "ISO 8859-2", 28592),
        ("cp852", "Central European (CP 852)", "CP 852", 852),
        ("windows1251", "Cyrillic (Windows 1251)", "Windows 1251", 1251),
        ("cp866", "Cyrillic (CP 866)", "CP 866", 866),
        ("iso88595", "Cyrillic (ISO 8859-5)", "ISO 8859-5", 28595),
        ("koi8r", "Cyrillic (KOI8-R)", "KOI8-R", 20866),
        ("koi8u", "Cyrillic (KOI8-U)", "KOI8-U", 21866),
        ("iso885913", "Estonian (ISO 8859-13)", "ISO 8859-13", 28603),
        ("windows1253", "Greek (Windows 1253)", "Windows 1253", 1253),
        ("iso88597", "Greek (ISO 8859-7)", "ISO 8859-7", 28597),
        ("windows1255", "Hebrew (Windows 1255)", "Windows 1255", 1255),
        ("iso88598", "Hebrew (ISO 8859-8)", "ISO 8859-8", 28598),
        ("windows874", "Thai (Windows 874)", "Windows 874", 874),
        ("windows1254", "Turkish (Windows 1254)", "Windows 1254", 1254),
        ("iso88599", "Turkish (ISO 8859-9)", "ISO 8859-9", 28599),
        ("windows1258", "Vietnamese (Windows 1258)", "Windows 1258", 1258),
        ("gbk", "Simplified Chinese (GBK)", "GBK", 936),
        ("gb18030", "Simplified Chinese (GB18030)", "GB18030", 54936),
        ("cp950", "Traditional Chinese (Big5)", "Big5", 950),
        ("shiftjis", "Japanese (Shift JIS)", "Shift JIS", 932),
        ("eucjp", "Japanese (EUC-JP)", "EUC-JP", 51932),
        ("euckr", "Korean (EUC-KR)", "EUC-KR", 51949),
        ("cp865", "Nordic DOS (CP 865)", "CP 865", 865),
        ("cp850", "Western European DOS (CP 850)", "CP 850", 850),
    ];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileEncoding?> CodePageEncodings = new();

    private static FileEncoding? FromCodePageTable(string id) => CodePageEncodings.GetOrAdd(id, static key =>
    {
        var row = Array.FindIndex(Table, r => r.Id == key);
        if (row < 0 || Table[row].CodePage == 0) return null;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // idempotent
            return new FileEncoding(key, Encoding.GetEncoding(Table[row].CodePage), []);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null; // a code page this runtime does not have: not offered
        }
    });

    /// <summary>The encodings Reopen with Encoding / Save with Encoding offer: VS Code's ids and long labels, in its order (those this runtime has).</summary>
    public static IReadOnlyList<(string Id, string Label)> Choices => ChoicesLazy.Value;

    private static readonly Lazy<IReadOnlyList<(string Id, string Label)>> ChoicesLazy = new(() =>
        Table.Where(r => FromSettingName(r.Id) is not null).Select(r => (r.Id, r.Label)).ToArray());

    /// <summary>What the status bar shows for an encoding id (VS Code's short label: "UTF-8", "Windows 1252"); the id itself when unknown.</summary>
    public static string ShortLabel(string id) => Array.Find(Table, r => r.Id == id) is { Id: not null } row ? row.Short : id.ToUpperInvariant();
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
