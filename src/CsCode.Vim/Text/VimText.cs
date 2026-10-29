using System.Globalization;
using System.Text;

namespace CsCode.Vim;

/// <summary>
/// The set of "word characters" ('iskeyword'). Characters below 256 come from the option string; above that, like Vim,
/// every letter-ish character is a word character (class &gt;= 2).
/// </summary>
public sealed class KeywordSet
{
    public const string Default = "@,48-57,_,192-255";
    private readonly bool[] _low = new bool[256];

    public KeywordSet(string spec = Default) => Parse(spec);

    public string Spec { get; private set; } = Default;

    public void Parse(string spec)
    {
        Spec = spec;
        Array.Clear(_low);
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var item = raw;
            var exclude = false;
            if (item.Length > 1 && item[0] == '^') { exclude = true; item = item[1..]; }
            if (item == "@") { for (var c = 0; c < 256; c++) if (c < 128 ? char.IsAsciiLetter((char)c) : char.IsLetter((char)c)) _low[c] = !exclude; continue; }
            if (item == "@-@") { _low['@'] = !exclude; continue; }
            var dash = item.IndexOf('-', 1);
            int lo, hi;
            if (dash > 0 && TryNum(item[..dash], out lo) && TryNum(item[(dash + 1)..], out hi)) { }
            else if (TryNum(item, out lo)) hi = lo;
            else continue;
            for (var c = Math.Max(lo, 0); c <= Math.Min(hi, 255); c++) _low[c] = !exclude;
        }
    }

    private static bool TryNum(string s, out int v)
    {
        if (s.Length == 1 && !char.IsDigit(s[0])) { v = s[0]; return true; }
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);
    }

    public bool IsKeyword(int cp) => cp < 256 ? _low[cp] : VimText.UtfClass(cp) >= 2;
}

/// <summary>Character-level text helpers: surrogate/combining-aware stepping, display widths, word classes, indent arithmetic.</summary>
public static class VimText
{
    public static readonly KeywordSet DefaultKeywords = new();

    // ---- character stepping (a "character" = scalar + following combining marks) ----

    public static bool IsCombining(char c)
    {
        if (c < 0x300) return false;
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark || c == '‍';
    }

    /// <summary>UTF-16 length of the character starting at <paramref name="i"/> (0 at end of string).</summary>
    public static int CharLen(string s, int i)
    {
        if ((uint)i >= (uint)s.Length) return 0;
        var n = 1;
        if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) n = 2;
        while (i + n < s.Length && IsCombining(s[i + n])) n++;
        return n;
    }

    /// <summary>Index of the start of the character that ends at or before <paramref name="i"/>-1 (clamped to 0).</summary>
    public static int PrevCharStart(string s, int i)
    {
        if (i <= 0) return 0;
        if (i > s.Length) i = s.Length;
        i--;
        while (i > 0 && IsCombining(s[i])) i--;
        if (i > 0 && char.IsLowSurrogate(s[i]) && char.IsHighSurrogate(s[i - 1])) i--;
        // a combining mark may have followed a surrogate pair
        while (i > 0 && IsCombining(s[i])) i--;
        return i;
    }

    /// <summary>Start of the character containing index <paramref name="i"/> (fixes an index pointing into the middle of a character).</summary>
    public static int CharStartAt(string s, int i)
    {
        if (i <= 0) return 0;
        if (i >= s.Length) return s.Length;
        var j = i;
        while (j > 0 && (IsCombining(s[j]) || (char.IsLowSurrogate(s[j]) && char.IsHighSurrogate(s[j - 1])))) j--;
        return j;
    }

    public static int CodePointAt(string s, int i)
    {
        if ((uint)i >= (uint)s.Length) return 0;
        var c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) return char.ConvertToUtf32(c, s[i + 1]);
        return c;
    }

    // ---- display width ----

    public static int CharWidth(int cp)
    {
        if (cp < 0x20 || cp == 0x7f) return 2; // shown as ^X
        if (cp < 0x7f) return 1;
        if (cp < 0xa0) return 4;               // shown as <xx>
        if (cp < 0x300) return 1;
        if (cp <= 0xFFFF && IsCombining((char)cp)) return 0;
        return IsWide(cp) ? 2 : 1;
    }

    public static bool IsWide(int c) =>
        c >= 0x1100 && (c <= 0x115f || c == 0x2329 || c == 0x232a ||
        (c >= 0x2e80 && c <= 0xa4cf && c != 0x303f) || (c >= 0xac00 && c <= 0xd7a3) || (c >= 0xf900 && c <= 0xfaff) ||
        (c >= 0xfe30 && c <= 0xfe6f) || (c >= 0xff00 && c <= 0xff60) || (c >= 0xffe0 && c <= 0xffe6) ||
        (c >= 0x1f300 && c <= 0x1f64f) || (c >= 0x1f900 && c <= 0x1f9ff) || (c >= 0x20000 && c <= 0x3fffd));

    /// <summary>Display width of the character at <paramref name="i"/> when it starts at virtual column <paramref name="vcol"/>.</summary>
    public static int WidthAt(string s, int i, int vcol, int tabstop)
    {
        var c = s[i];
        if (c == '\t') return tabstop - vcol % tabstop;
        if (c < 0x7f && c >= 0x20) return 1;
        return CharWidth(CodePointAt(s, i));
    }

    /// <summary>Virtual (display) column where the character at index <paramref name="index"/> starts.</summary>
    public static int VcolOf(string s, int index, int tabstop)
    {
        var v = 0;
        var i = 0;
        var end = Math.Min(index, s.Length);
        while (i < end)
        {
            v += WidthAt(s, i, v, tabstop);
            i += CharLen(s, i);
        }
        return v;
    }

    /// <summary>Virtual column of the LAST cell of the character at <paramref name="index"/>.</summary>
    public static int VcolEndOf(string s, int index, int tabstop)
    {
        var start = VcolOf(s, index, tabstop);
        if (index >= s.Length) return start;
        return start + Math.Max(1, WidthAt(s, index, start, tabstop)) - 1;
    }

    /// <summary>Total display width of the string.</summary>
    public static int DisplayWidth(string s, int tabstop) => VcolOf(s, s.Length, tabstop);

    /// <summary>
    /// Index of the character covering virtual column <paramref name="vcol"/>; if the line is shorter, returns the index of the
    /// last character (<paramref name="lastIfBeyond"/>) or s.Length (otherwise).
    /// </summary>
    public static int IndexOfVcol(string s, int vcol, int tabstop, bool lastIfBeyond)
    {
        var v = 0;
        var i = 0;
        var last = 0;
        while (i < s.Length)
        {
            var w = WidthAt(s, i, v, tabstop);
            if (vcol < v + Math.Max(w, 1)) return i;
            last = i;
            v += w;
            i += CharLen(s, i);
        }
        return lastIfBeyond ? last : s.Length;
    }

    // ---- word classes ----

    /// <summary>Vim's utf_class: 0 blank, 1 punctuation, 2 word, higher = script-specific word class.</summary>
    public static int UtfClass(int c)
    {
        if (c < 0x100)
        {
            if (c is ' ' or '\t' or 0 or 0xa0) return 0;
            return DefaultKeywords.IsKeyword(c) ? 2 : 1;
        }
        foreach (var (lo, hi, cls) in Classes)
            if (c >= lo && c <= hi) return cls;
        if (c >= 0x1f000 && c <= 0x1faff) return 3; // emoji
        return 2;
    }

    private static readonly (int Lo, int Hi, int Cls)[] Classes =
    [
        (0x037e, 0x037e, 1), (0x0387, 0x0387, 1), (0x055a, 0x055f, 1), (0x0589, 0x0589, 1), (0x05be, 0x05be, 1), (0x05c0, 0x05c0, 1),
        (0x05c3, 0x05c3, 1), (0x05f3, 0x05f4, 1), (0x060c, 0x060c, 1), (0x061b, 0x061b, 1), (0x061f, 0x061f, 1), (0x066a, 0x066d, 1),
        (0x06d4, 0x06d4, 1), (0x0700, 0x070d, 1), (0x0964, 0x0965, 1), (0x0970, 0x0970, 1), (0x0df4, 0x0df4, 1), (0x0e4f, 0x0e4f, 1),
        (0x0e5a, 0x0e5b, 1), (0x0f04, 0x0f12, 1), (0x0f3a, 0x0f3d, 1), (0x0f85, 0x0f85, 1), (0x104a, 0x104f, 1), (0x10fb, 0x10fb, 1),
        (0x1361, 0x1368, 1), (0x166d, 0x166e, 1), (0x1680, 0x1680, 0), (0x169b, 0x169c, 1), (0x16eb, 0x16ed, 1), (0x1735, 0x1736, 1),
        (0x17d4, 0x17dc, 1), (0x1800, 0x180a, 1), (0x2000, 0x200b, 0), (0x200c, 0x2027, 1), (0x2028, 0x2029, 0), (0x202a, 0x202e, 1),
        (0x202f, 0x202f, 0), (0x2030, 0x205e, 1), (0x205f, 0x205f, 0), (0x2060, 0x206f, 1), (0x2070, 0x207f, 0x2070), (0x2080, 0x2094, 0x2080),
        (0x20a0, 0x27ff, 1), (0x2800, 0x28ff, 0x2800), (0x2900, 0x2998, 1), (0x29d8, 0x29db, 1), (0x29fc, 0x29fd, 1), (0x2e00, 0x2e7f, 1),
        (0x3000, 0x3000, 0), (0x3001, 0x3020, 1), (0x3030, 0x3030, 1), (0x303d, 0x303d, 1), (0x3040, 0x309f, 0x3040), (0x30a0, 0x30ff, 0x30a0),
        (0x3300, 0x9fff, 0x4e00), (0xac00, 0xd7a3, 0xac00), (0xf900, 0xfaff, 0x4e00), (0xfd3e, 0xfd3f, 1), (0xfe30, 0xfe6b, 1),
        (0xff00, 0xff0f, 1), (0xff1a, 0xff20, 1), (0xff3b, 0xff3e, 1), (0xff40, 0xff40, 1), (0xff5b, 0xff65, 1),
        (0x1d000, 0x1d24f, 1), (0x1d400, 0x1d7ff, 1), (0x20000, 0x2a6df, 0x4e00), (0x2a700, 0x2b73f, 0x4e00), (0x2b740, 0x2b81f, 0x4e00),
        (0x2f800, 0x2fa1f, 0x4e00),
    ];

    /// <summary>Word class of the character at <paramref name="i"/> for w/b/e style motions, using the buffer's 'iskeyword'.</summary>
    public static int ClassAt(string s, int i, bool bigword, KeywordSet kw)
    {
        if ((uint)i >= (uint)s.Length) return 0;
        var c = CodePointAt(s, i);
        int cls;
        if (c < 0x100) cls = c is ' ' or '\t' or 0 or 0xa0 ? 0 : kw.IsKeyword(c) ? 2 : 1;
        else cls = UtfClass(c);
        return bigword && cls != 0 ? 1 : cls;
    }

    // ---- indent ----

    public static int IndentWidth(string line, int tabstop)
    {
        var v = 0;
        foreach (var ch in line)
        {
            if (ch == ' ') v++;
            else if (ch == '\t') v += tabstop - v % tabstop;
            else break;
        }
        return v;
    }

    public static int FirstNonBlank(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        return i;
    }

    public static string MakeIndent(int width, int tabstop, bool expandTab)
    {
        if (width <= 0) return "";
        if (expandTab) return new string(' ', width);
        var tabs = width / tabstop;
        var spaces = width % tabstop;
        return tabs == 0 ? new string(' ', spaces) : new string('\t', tabs) + new string(' ', spaces);
    }

    public static bool IsBlank(string line) => FirstNonBlank(line) == line.Length;

    /// <summary>Number of characters (not UTF-16 units) in <paramref name="s"/> between two indices.</summary>
    public static int CountChars(string s, int from, int to)
    {
        var n = 0;
        for (var i = from; i < to && i < s.Length; i += CharLen(s, i)) n++;
        return n;
    }

    public static string Repeat(string s, int n)
    {
        if (n <= 0) return "";
        if (n == 1) return s;
        var sb = new StringBuilder(s.Length * n);
        for (var i = 0; i < n; i++) sb.Append(s);
        return sb.ToString();
    }
}
