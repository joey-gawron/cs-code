using System.Text;
using CsCode.Core.Platform;

namespace CsCode.Vim;

/// <summary>
/// The engine's key representation: a plain <c>int</c> so the typing path never allocates.
/// <list type="bullet">
/// <item>0 .. 0x10FFFF — a Unicode scalar; control characters are Ctrl-keys exactly as in Vim (Esc = 27, CR = 13, Ctrl-A = 1 ...).</item>
/// <item><see cref="Special"/> + n — a non-character key (arrows, F-keys, Backspace key, Delete ...).</item>
/// <item>Modifier flags (<see cref="ModAlt"/>, <see cref="ModCtrl"/>, <see cref="ModShift"/>, <see cref="ModMeta"/>) are only set where the
/// modifier cannot be folded into the character (Alt-x, Ctrl-Left, Shift-F5 ...).</item>
/// </list>
/// Macro registers hold keys as TEXT, encoded like Vim does (K_SPECIAL 0x80 + two bytes for special keys), so register text
/// round-trips exactly and macros can be edited as text.
/// </summary>
public static class Keys
{
    public const int Special = 0x200000;
    public const int ModShift = 0x400000;
    public const int ModCtrl = 0x800000;
    public const int ModAlt = 0x1000000;
    public const int ModMeta = 0x2000000;
    public const int ModMask = ModShift | ModCtrl | ModAlt | ModMeta;
    public const int BaseMask = 0x1FFFFF;

    public const int Esc = 27, CR = 13, NL = 10, Tab = 9, CtrlH = 8, Space = 32, Nul = 0;

    public const int Up = Special + 1, Down = Special + 2, Left = Special + 3, Right = Special + 4,
        Home = Special + 5, End = Special + 6, PageUp = Special + 7, PageDown = Special + 8,
        Insert = Special + 9, Delete = Special + 10, BS = Special + 11, STab = Special + 12,
        Plug = Special + 13, Nop = Special + 14, Ignore = Special + 15,
        F1 = Special + 20; // F1..F12 = F1 + 0..11

    /// <summary>K_SPECIAL: first char of a special-key encoding in register text (Vim's 0x80 byte).</summary>
    public const char KSpecial = '\u0080';
    private const char KsExtra = 'ý', KsModifier = 'ü', KsSpecial = 'þ';

    // Vim notation name, code, termcap pair (KS KE) used in register text.
    private static readonly (string Name, int Code, string Pair)[] SpecialTable =
    [
        ("Up", Up, "ku"), ("Down", Down, "kd"), ("Left", Left, "kl"), ("Right", Right, "kr"),
        ("Home", Home, "kh"), ("End", End, "@7"), ("PageUp", PageUp, "kP"), ("PageDown", PageDown, "kN"),
        ("Insert", Insert, "kI"), ("Del", Delete, "kD"), ("Delete", Delete, "kD"), ("BS", BS, "kb"), ("S-Tab", STab, "kB"),
        ("Plug", Plug, "ýD"), ("Nop", Nop, "ýN"), ("Ignore", Ignore, "ýI"),
        ("F1", F1, "k1"), ("F2", F1 + 1, "k2"), ("F3", F1 + 2, "k3"), ("F4", F1 + 3, "k4"), ("F5", F1 + 4, "k5"),
        ("F6", F1 + 5, "k6"), ("F7", F1 + 6, "k7"), ("F8", F1 + 7, "k8"), ("F9", F1 + 8, "k9"), ("F10", F1 + 9, "k;"),
        ("F11", F1 + 10, "F1"), ("F12", F1 + 11, "F2"),
    ];

    private static readonly Dictionary<string, int> NameToCode = BuildNames();
    private static readonly Dictionary<int, string> CodeToName = SpecialTable.GroupBy(t => t.Code).ToDictionary(g => g.Key, g => g.First().Name);
    private static readonly Dictionary<int, string> CodeToPair = SpecialTable.GroupBy(t => t.Code).ToDictionary(g => g.Key, g => g.First().Pair);
    private static readonly Dictionary<string, int> PairToCode = SpecialTable.GroupBy(t => t.Pair).ToDictionary(g => g.Key, g => g.First().Code);

    private static Dictionary<string, int> BuildNames()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, code, _) in SpecialTable) d[name] = code;
        d["Esc"] = Esc; d["CR"] = CR; d["Return"] = CR; d["Enter"] = CR; d["NL"] = NL; d["NewLine"] = NL; d["LineFeed"] = NL;
        d["Tab"] = Tab; d["Space"] = Space; d["lt"] = '<'; d["Bar"] = '|'; d["Bslash"] = '\\'; d["Nul"] = 0; d["Del"] = Delete;
        d["kEnter"] = CR; d["kPlus"] = '+'; d["kMinus"] = '-'; d["kMultiply"] = '*'; d["kDivide"] = '/'; d["kPoint"] = '.';
        d["Backspace"] = BS; d["PgUp"] = PageUp; d["PgDn"] = PageDown; d["Ins"] = Insert;
        for (var i = 0; i < 10; i++) d["k" + i] = '0' + i;
        return d;
    }

    public static bool IsSpecial(int key) => (key & Special) != 0;
    public static int BaseOf(int key) => key & BaseMask;
    public static bool HasMods(int key) => (key & ModMask) != 0;

    /// <summary>True for a plain Unicode scalar with no flags (the common typed-character case).</summary>
    public static bool IsChar(int key) => (uint)key <= 0x10FFFF;

    public static bool IsPrintableChar(int key) => key >= 32 && key != 127 && IsChar(key) && !(key is >= 0x80 and < 0xA0);

    public static int Ctrl(char letter) => char.ToUpperInvariant(letter) & 0x1f;

    // ------------------------------------------------------------------------------------------------------------
    // Shell KeyStroke -> engine key
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Maps the shell's toolkit-independent <see cref="KeyStroke"/> to an engine key; -1 if it has no Vim meaning.</summary>
    public static int FromKeyStroke(in KeyStroke ks)
    {
        var name = ks.Key;
        if (string.IsNullOrEmpty(name)) return -1;
        var mods = (ks.Alt ? ModAlt : 0) | (ks.Meta ? ModMeta : 0);
        int code;
        if (name.Length == 1 || (name.Length == 2 && char.IsHighSurrogate(name[0])))
        {
            var c = name.Length == 1 ? name[0] : char.ConvertToUtf32(name, 0);
            if (ks.Shift && c is >= 'a' and <= 'z') c = char.ToUpperInvariant((char)c);
            if (ks.Ctrl)
            {
                var cc = c switch
                {
                    >= 'a' and <= 'z' => c - 'a' + 1,
                    >= 'A' and <= 'Z' => c - 'A' + 1,
                    '[' => 27, '\\' => 28, ']' => 29, '^' or '6' => 30, '_' or '-' => 31, '@' or ' ' or '2' => 0, '/' => 31, '?' => 127,
                    _ => -1,
                };
                if (cc >= 0) return mods | cc;
                return mods | ModCtrl | c;
            }
            return mods | c;
        }
        var lower = name.ToLowerInvariant();
        switch (lower)
        {
            case "escape" or "esc": return mods | Esc;
            case "enter" or "return" or "numpad_enter": return ks.Ctrl ? mods | ModCtrl | CR : mods | CR;
            case "tab": return ks.Shift ? mods | STab : mods | (ks.Ctrl ? ModCtrl | Tab : Tab);
            case "space": return ks.Ctrl ? mods | Nul : mods | Space;
            case "backspace": return ks.Ctrl ? mods | ModCtrl | BS : mods | BS;
            case "delete": code = Delete; break;
            case "insert": code = Insert; break;
            case "up": code = Up; break;
            case "down": code = Down; break;
            case "left": code = Left; break;
            case "right": code = Right; break;
            case "home": code = Home; break;
            case "end": code = End; break;
            case "pageup": code = PageUp; break;
            case "pagedown": code = PageDown; break;
            case "numpad_add": return mods | '+';
            case "numpad_subtract": return mods | '-';
            case "numpad_multiply": return mods | '*';
            case "numpad_divide": return mods | '/';
            case "numpad_decimal": return mods | '.';
            default:
                if (lower.Length > 6 && lower.StartsWith("numpad") && lower[6] is >= '0' and <= '9') return mods | lower[6];
                if (lower[0] == 'f' && int.TryParse(lower.AsSpan(1), out var fn) && fn is >= 1 and <= 12) { code = F1 + fn - 1; break; }
                return -1;
        }
        return code | mods | (ks.Ctrl ? ModCtrl : 0) | (ks.Shift ? ModShift : 0);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Vim notation: "<C-w>x<Esc>" <-> keys
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Parses Vim key notation (the syntax of <c>:map</c> and <c>nvim_feedkeys</c>). Unknown <c>&lt;..&gt;</c> is literal text.</summary>
    public static List<int> ParseNotation(string text, string leader = "\\", string localLeader = "\\")
    {
        var keys = new List<int>(text.Length);
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '<')
            {
                var end = text.IndexOf('>', i + 1);
                if (end > i + 1 && TryParseBracket(text.AsSpan(i + 1, end - i - 1), leader, localLeader, keys))
                {
                    i = end + 1;
                    continue;
                }
            }
            var cp = char.ConvertToUtf32(text, i);
            if (cp == 0xFFFD && char.IsHighSurrogate(text[i])) cp = text[i];
            keys.Add(cp);
            i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
        }
        return keys;
    }

    public static bool TryParseKey(string notation, out int key)
    {
        var l = ParseNotation(notation);
        key = l.Count == 1 ? l[0] : -1;
        return l.Count == 1;
    }

    private static bool TryParseBracket(ReadOnlySpan<char> body, string leader, string localLeader, List<int> output)
    {
        var s = body.ToString();
        if (s.Equals("leader", StringComparison.OrdinalIgnoreCase)) { output.AddRange(ParseNotation(leader, "\\", "\\")); return true; }
        if (s.Equals("localleader", StringComparison.OrdinalIgnoreCase)) { output.AddRange(ParseNotation(localLeader, "\\", "\\")); return true; }
        var mods = 0;
        // Modifier prefixes: C- S- A- M- D- (case-insensitive), possibly several.
        while (s.Length > 2 && s[1] == '-' && "CSAMDcsamd".Contains(s[0]))
        {
            mods |= char.ToUpperInvariant(s[0]) switch { 'C' => ModCtrl, 'S' => ModShift, 'A' or 'M' => ModAlt, _ => ModMeta };
            s = s[2..];
        }
        int code;
        if (NameToCode.TryGetValue(s, out code)) { }
        else if (mods != 0 && (s.Length == 1 || (s.Length == 2 && char.IsHighSurrogate(s[0])))) code = char.ConvertToUtf32(s, 0);
        else return false;

        if (mods == 0) { output.Add(code); return true; }
        output.Add(ApplyModifiers(code, mods));
        return true;
    }

    private static int ApplyModifiers(int code, int mods)
    {
        if (IsChar(code) && (mods & ModCtrl) != 0 && (mods & ModAlt) == 0 && (mods & ModMeta) == 0)
        {
            var folded = code switch
            {
                >= 'a' and <= 'z' => code - 'a' + 1,
                >= 'A' and <= 'Z' => code - 'A' + 1,
                '@' or ' ' => 0, '[' => 27, '\\' => 28, ']' => 29, '^' => 30, '_' => 31, '?' => 127,
                _ => -1,
            };
            if (folded >= 0) return folded;
        }
        if (IsChar(code) && (mods & ModShift) != 0 && code is >= 'a' and <= 'z') { code = char.ToUpperInvariant((char)code); mods &= ~ModShift; }
        else if (IsChar(code) && (mods & ModShift) != 0 && code > 32) mods &= ~ModShift; // <S-1> etc: layout dependent, treat as the char
        if (code == Tab && (mods & ModShift) != 0) { code = STab; mods &= ~ModShift; }
        return code | mods;
    }

    /// <summary>Readable form for showcmd / messages: <c>^[</c> style is NOT used here; this is the &lt;Esc&gt; notation.</summary>
    public static string ToNotation(int key)
    {
        var sb = new StringBuilder();
        AppendNotation(sb, key);
        return sb.ToString();
    }

    public static string ToNotation(IEnumerable<int> keys)
    {
        var sb = new StringBuilder();
        foreach (var k in keys) AppendNotation(sb, k);
        return sb.ToString();
    }

    public static void AppendNotation(StringBuilder sb, int key)
    {
        var mods = key & ModMask;
        var b = key & BaseMask;
        if (!IsSpecial(key) && mods == 0)
        {
            switch (b)
            {
                case Esc: sb.Append("<Esc>"); return;
                case CR: sb.Append("<CR>"); return;
                case NL: sb.Append("<NL>"); return;
                case Tab: sb.Append("<Tab>"); return;
                case ' ': sb.Append(' '); return;
                case '<': sb.Append("<lt>"); return;
                case 0: sb.Append("<Nul>"); return;
                case 127: sb.Append("<C-?>"); return;
                case < 32: sb.Append("<C-").Append((char)(b + 64)).Append('>'); return;
                default: sb.Append(char.ConvertFromUtf32(b)); return;
            }
        }
        sb.Append('<');
        if ((mods & ModCtrl) != 0) sb.Append("C-");
        if ((mods & ModShift) != 0) sb.Append("S-");
        if ((mods & ModAlt) != 0) sb.Append("M-");
        if ((mods & ModMeta) != 0) sb.Append("D-");
        if (IsSpecial(key)) sb.Append(CodeToName.TryGetValue(b | Special, out var n) ? n : "?");
        else sb.Append(b switch { Esc => "Esc", CR => "CR", Tab => "Tab", ' ' => "Space", < 32 => "C-" + (char)(b + 64), _ => char.ConvertFromUtf32(b) });
        sb.Append('>');
    }

    // ------------------------------------------------------------------------------------------------------------
    // Register-text encoding (Vim's K_SPECIAL scheme)
    // ------------------------------------------------------------------------------------------------------------

    public static void Encode(StringBuilder sb, int key)
    {
        var mods = key & ModMask;
        var b = key & BaseMask;
        if (mods != 0)
        {
            var m = ((mods & ModShift) != 0 ? 0x02 : 0) | ((mods & ModCtrl) != 0 ? 0x04 : 0) | ((mods & ModAlt) != 0 ? 0x08 : 0) | ((mods & ModMeta) != 0 ? 0x80 : 0);
            sb.Append(KSpecial).Append(KsModifier).Append((char)m);
        }
        if (IsSpecial(key))
        {
            sb.Append(KSpecial);
            sb.Append(CodeToPair.TryGetValue(b | Special, out var pair) ? pair : "ýI");
        }
        else if (b == KSpecial) sb.Append(KSpecial).Append(KsSpecial).Append('X');
        else if (b > 0xFFFF) sb.Append(char.ConvertFromUtf32(b));
        else sb.Append((char)b);
    }

    public static string Encode(int key)
    {
        if (IsChar(key) && key is > 0 and < 0x80) return CharStrings.Get((char)key);
        var sb = new StringBuilder(4);
        Encode(sb, key);
        return sb.ToString();
    }

    public static string Encode(IEnumerable<int> keys)
    {
        var sb = new StringBuilder();
        foreach (var k in keys) Encode(sb, k);
        return sb.ToString();
    }

    /// <summary>Inverse of <see cref="Encode(int)"/>: register text → keys (tolerates malformed K_SPECIAL by treating it as a plain char).</summary>
    public static void Decode(string text, List<int> output)
    {
        var mods = 0;
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (c == KSpecial && i + 2 < text.Length)
            {
                var a = text[i + 1];
                var bch = text[i + 2];
                if (a == KsModifier)
                {
                    mods |= ((bch & 0x02) != 0 ? ModShift : 0) | ((bch & 0x04) != 0 ? ModCtrl : 0) | ((bch & 0x08) != 0 ? ModAlt : 0) | ((bch & 0x80) != 0 ? ModMeta : 0);
                    i += 3;
                    continue;
                }
                if (a == KsSpecial && bch == 'X') { output.Add(KSpecial | mods); mods = 0; i += 3; continue; }
                if (PairToCode.TryGetValue(new string([a, bch]), out var code)) { output.Add(code | mods); mods = 0; i += 3; continue; }
            }
            int cp;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { cp = char.ConvertToUtf32(c, text[i + 1]); i += 2; }
            else { cp = c; i++; }
            output.Add(cp | mods);
            mods = 0;
        }
    }

    public static List<int> Decode(string text)
    {
        var l = new List<int>(text.Length);
        Decode(text, l);
        return l;
    }
}

/// <summary>Cache of one-character strings so the typing path does not allocate a string per keystroke.</summary>
public static class CharStrings
{
    private static readonly string[] Table = Build();

    private static string[] Build()
    {
        var t = new string[512];
        for (var i = 0; i < t.Length; i++) t[i] = ((char)i).ToString();
        return t;
    }

    public static string Get(char c) => c < Table.Length ? Table[c] : c.ToString();
    public static string Get(int codePoint) => codePoint < Table.Length ? Table[codePoint] : char.ConvertFromUtf32(codePoint);
}
