using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.Vim;

/// <summary>Thrown for a pattern Vim would reject (message is Vim's) or one this translator does not support (message says so).</summary>
public sealed class VimRegexException(string message) : Exception(message);

/// <summary>A compiled Vim pattern: the translated .NET regex plus what the search code needs to know about it.</summary>
public sealed class VimPattern
{
    public required Regex Regex { get; init; }
    public required string DotNetSource { get; init; }
    public required string Source { get; init; }
    /// <summary>Contains \n, \_x ...: must be matched against joined text rather than single lines.</summary>
    public bool Multiline { get; init; }
    public bool IgnoreCase { get; init; }
    public bool HasZs { get; init; }
    public bool HasZe { get; init; }
}

/// <summary>
/// Translates Vim patterns ('magic' default, \v \m \M \V, \&lt; \&gt;, \{n,m}, \+ \? \= \|, \( \) \%( \), \zs \ze, \_s \_., [:alpha:], \d \w \s \a \l \u,
/// \c \C, ~, \1-\9, \@= \@! \@&lt;= \@&lt;! \@&gt;, \%^ \%$ \%[] \%d123 ...) to .NET regular expressions.
/// <para>Unsupported (reported as errors): \%V \%# \%'m \%23l \%23c \%23v (position atoms), \&amp; (concat), \z(, backreference-in-collection forms, \%C, \%d with huge values.</para>
/// <para>Approximations: keyword/word-boundary classes use .NET word characters (Unicode letters and digits plus '_').</para>
/// </summary>
public static class VimRegex
{
    private enum T { Lit, Op, Cmd, UCmd, Coll, Brace, Pct }

    private readonly record struct Tok(T Kind, int C, string Raw = "", bool Nl = false);

    public static VimPattern Compile(string pattern, bool ignoreCase, bool smartCase, bool magic = true, string lastSubstitute = "", bool forceMatchCaseDefault = false)
    {
        var (toks, caseFlag) = Tokenize(pattern, magic);
        var hasUpper = HasUppercase(pattern);
        var ic = caseFlag ?? (ignoreCase && !(smartCase && hasUpper));
        var p = new Parser(toks, lastSubstitute);
        var rx = p.ParseAll();
        var options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (ic) options |= RegexOptions.IgnoreCase;
        try
        {
            return new VimPattern
            {
                Regex = new Regex(rx, options),
                DotNetSource = rx,
                Source = pattern,
                Multiline = p.Multiline,
                IgnoreCase = ic,
                HasZs = p.HasZs,
                HasZe = p.HasZe,
            };
        }
        catch (ArgumentException ex)
        {
            throw new VimRegexException("E54: Unmatched \\(" + (ex.Message.Length > 0 ? "" : ""));
        }
    }

    /// <summary>Vim's pat_has_uppercase: an upper-case letter that is not part of a backslash sequence.</summary>
    public static bool HasUppercase(string pattern)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                if (pattern[i + 1] == '_' && i + 2 < pattern.Length) i += 2;
                else if (pattern[i + 1] == '%' && i + 2 < pattern.Length) i += 2;
                else i++;
                continue;
            }
            if (char.IsUpper(c)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------------------------------------------------
    // tokenizer: resolve the magic level so the parser only sees canonical operators
    // ------------------------------------------------------------------------------------------------------------

    private static (List<Tok>, bool?) Tokenize(string p, bool magicDefault)
    {
        var toks = new List<Tok>();
        bool? caseFlag = null;
        var mode = magicDefault ? 'm' : 'M';
        for (var i = 0; i < p.Length;)
        {
            var c = p[i];
            if (c == '\\')
            {
                if (i + 1 >= p.Length) { toks.Add(new Tok(T.Lit, '\\')); i++; continue; }
                var e = p[i + 1];
                i += 2;
                switch (e)
                {
                    case 'v': mode = 'v'; continue;
                    case 'm': mode = 'm'; continue;
                    case 'M': mode = 'M'; continue;
                    case 'V': mode = 'V'; continue;
                    case 'c': caseFlag = true; continue;
                    case 'C': caseFlag = false; continue;
                    case 'Z': continue;
                }
                if (e == '_')
                {
                    if (i >= p.Length) throw new VimRegexException("E63: Invalid use of \\_");
                    var x = p[i++];
                    if (x == '[')
                    {
                        var close = FindCollectionEnd(p, i);
                        if (close < 0) throw new VimRegexException("E769: Missing ] after \\_[");
                        toks.Add(new Tok(T.Coll, 0, p.Substring(i, close - i), Nl: true));
                        i = close + 1;
                    }
                    else toks.Add(new Tok(T.UCmd, x));
                    continue;
                }
                if (e == '%')
                {
                    if (i >= p.Length) throw new VimRegexException("E71: Invalid character after \\%");
                    var x = p[i];
                    if (x == '(') { toks.Add(new Tok(T.Op, '%')); toks.Add(new Tok(T.Op, '(')); i++; continue; }
                    if (x is 'd' or 'x' or 'u' or 'U' or 'o')
                    {
                        i++;
                        var radix = x switch { 'd' => 10, 'o' => 8, _ => 16 };
                        var max = x switch { 'x' => 2, 'u' => 4, 'U' => 8, 'o' => 4, _ => 10 };
                        var s = i;
                        while (i < p.Length && i - s < max && Uri.IsHexDigit(p[i]) && (radix == 16 || (p[i] >= '0' && p[i] < '0' + radix))) i++;
                        if (i == s) throw new VimRegexException("E678: Invalid character after \\%[dxouU]");
                        toks.Add(new Tok(T.Lit, Convert.ToInt32(p.Substring(s, i - s), radix)));
                        continue;
                    }
                    if (x == '[')
                    {
                        var close = p.IndexOf(']', i + 1);
                        if (close < 0) throw new VimRegexException("E69: Missing ] after \\%[");
                        toks.Add(new Tok(T.Pct, '[', p.Substring(i + 1, close - i - 1)));
                        i = close + 1;
                        continue;
                    }
                    // \%^ \%$ \%V \%# \%'m \%23l ...
                    var st = i;
                    if (x is '^' or '$' or 'V' or '#' or 'C') i++;
                    else if (x == '\'') i += 2;
                    else { if (x is '<' or '>') i++; while (i < p.Length && char.IsDigit(p[i])) i++; if (i < p.Length && p[i] is 'l' or 'c' or 'v') i++; }
                    toks.Add(new Tok(T.Pct, 'x', p.Substring(st, Math.Min(i, p.Length) - st)));
                    continue;
                }
                if (IsEscMeta(e, mode)) { AddOp(toks, e, p, ref i, mode); continue; }
                if (char.IsLetterOrDigit(e) || e == '_') { toks.Add(new Tok(T.Cmd, e)); continue; }
                toks.Add(new Tok(T.Lit, e));
                continue;
            }
            // unescaped
            if (IsRawMeta(c, mode))
            {
                if (c == '[')
                {
                    var close = FindCollectionEnd(p, i + 1);
                    if (close < 0) { toks.Add(new Tok(T.Lit, '[')); i++; continue; }
                    toks.Add(new Tok(T.Coll, 0, p.Substring(i + 1, close - i - 1)));
                    i = close + 1;
                    continue;
                }
                i++;
                AddOp(toks, c, p, ref i, mode);
                continue;
            }
            if (char.IsHighSurrogate(c) && i + 1 < p.Length) { toks.Add(new Tok(T.Lit, char.ConvertToUtf32(c, p[i + 1]))); i += 2; continue; }
            toks.Add(new Tok(T.Lit, c));
            i++;
        }
        return (toks, caseFlag);
    }

    private static bool IsRawMeta(char c, char mode) => mode switch
    {
        'v' => "()|+?={@%<>&~.*[^$".IndexOf(c) >= 0,
        'm' => "^$.*[~".IndexOf(c) >= 0,
        'M' => "^$".IndexOf(c) >= 0,
        _ => "^$".IndexOf(c) >= 0, // \V: ^ and $ keep their meaning at the edges (handled by the parser's position rules)
    };

    private static bool IsEscMeta(char e, char mode) => mode switch
    {
        'v' => false,
        'm' => "()|+?={@<>&%".IndexOf(e) >= 0,
        _ => "()|+?={@<>&%.*[~".IndexOf(e) >= 0,
    };

    private static void AddOp(List<Tok> toks, char op, string p, ref int i, char mode)
    {
        if (op == '=') op = '?';
        if (op == '{')
        {
            var end = p.IndexOf('}', i);
            if (end < 0) throw new VimRegexException("E554: Syntax error in \\{...}");
            var raw = p.Substring(i, end - i);
            if (raw.EndsWith('\\')) raw = raw[..^1];
            i = end + 1;
            toks.Add(new Tok(T.Brace, '{', raw));
            return;
        }
        if (op == '@')
        {
            // \@= \@! \@> \@<= \@<!  (optionally with a number before the <)
            var s = i;
            while (i < p.Length && char.IsDigit(p[i])) i++;
            if (i >= p.Length) throw new VimRegexException("E59: Invalid character after \\@");
            var kind = p[i++];
            var raw = kind.ToString();
            if (kind == '<')
            {
                if (i >= p.Length || p[i] is not ('=' or '!')) throw new VimRegexException("E59: Invalid character after \\@");
                raw = "<" + p[i++];
            }
            else if (kind is not ('=' or '!' or '>')) throw new VimRegexException("E59: Invalid character after \\@");
            toks.Add(new Tok(T.Brace, '@', raw));
            _ = s;
            return;
        }
        toks.Add(new Tok(T.Op, op));
    }

    private static int FindCollectionEnd(string p, int from)
    {
        // a valid collection needs a closing ']' ; a leading ']' or '^]' is literal
        var i = from;
        if (i < p.Length && p[i] == '^') i++;
        if (i < p.Length && p[i] == ']') i++;
        for (; i < p.Length; i++)
        {
            if (p[i] == '\\' && i + 1 < p.Length) { i++; continue; }
            if (p[i] == '[' && i + 1 < p.Length && p[i + 1] is ':' or '.' or '=')
            {
                var close = p.IndexOf(p[i + 1] + "]", i + 2, StringComparison.Ordinal);
                if (close > 0) { i = close + 1; continue; }
            }
            if (p[i] == ']') return i;
        }
        return -1;
    }

    // ------------------------------------------------------------------------------------------------------------
    // parser: tokens -> .NET regex source
    // ------------------------------------------------------------------------------------------------------------

    private sealed class Parser(List<Tok> toks, string lastSubstitute)
    {
        private int _p;
        public bool Multiline, HasZs, HasZe;
        private int _groups;
        public bool HasMore => _p < toks.Count;

        public string ParseAll()
        {
            var s = ParseAlt(0);
            if (_p < toks.Count) throw new VimRegexException("E55: Unmatched \\)");
            return s;
        }

        private string ParseAlt(int depth)
        {
            var sb = new StringBuilder();
            sb.Append(ParseConcat(depth));
            while (_p < toks.Count && toks[_p] is { Kind: T.Op, C: '|' })
            {
                _p++;
                sb.Append('|').Append(ParseConcat(depth));
            }
            return sb.ToString();
        }

        private string ParseConcat(int depth)
        {
            var sb = new StringBuilder();
            var atStart = true;
            while (_p < toks.Count)
            {
                var t = toks[_p];
                if (t.Kind == T.Op && t.C is '|') break;
                if (t.Kind == T.Op && t.C == ')') { if (depth == 0) throw new VimRegexException("E55: Unmatched \\)"); break; }
                var atom = ParseAtom(atStart, depth, out var startAnchor);
                if (atom is null) continue;
                // postfix multis
                atom = ParseMulti(atom);
                sb.Append(atom);
                atStart = startAnchor && false;
                if (t.Kind == T.UCmd && t.C == 'n') atStart = true;
                if (t.Kind == T.Cmd && t.C == 'n') atStart = true;
            }
            return sb.ToString();
        }

        private string ParseMulti(string atom)
        {
            while (_p < toks.Count)
            {
                var t = toks[_p];
                if (t.Kind == T.Op && t.C is '*' or '+' or '?')
                {
                    _p++;
                    atom = Wrap(atom) + t.C switch { '*' => "*", '+' => "+", _ => "?" };
                }
                else if (t.Kind == T.Brace && t.C == '{')
                {
                    _p++;
                    atom = Wrap(atom) + Quant(t.Raw);
                }
                else if (t.Kind == T.Brace && t.C == '@')
                {
                    _p++;
                    atom = t.Raw switch
                    {
                        "=" => $"(?={atom})",
                        "!" => $"(?!{atom})",
                        ">" => $"(?>{atom})",
                        "<=" => $"(?<={atom})",
                        "<!" => $"(?<!{atom})",
                        _ => atom,
                    };
                }
                else break;
            }
            return atom;
        }

        private static string Wrap(string atom) => atom.Length > 0 && IsSingleAtom(atom) ? atom : "(?:" + atom + ")";

        private static bool IsSingleAtom(string a)
        {
            if (a.Length == 1) return true;
            if (a.Length == 2 && a[0] == '\\') return true;
            if (a[0] == '[' && a.IndexOf(']', 1) == a.Length - 1) return true;
            return a[0] == '(' && Balanced(a);
        }

        private static bool Balanced(string a)
        {
            var depth = 0;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] == '\\') { i++; continue; }
                if (a[i] == '[') { var e = a.IndexOf(']', i + 1); if (e > 0) i = e; continue; }
                if (a[i] == '(') depth++;
                else if (a[i] == ')') { depth--; if (depth == 0 && i != a.Length - 1) return false; }
            }
            return depth == 0;
        }

        private static string Quant(string raw)
        {
            var lazy = raw.StartsWith('-');
            if (lazy) raw = raw[1..];
            string q;
            if (raw.Length == 0) q = "*";
            else if (raw == ",") q = "*";
            else if (raw.Contains(',', StringComparison.Ordinal))
            {
                var parts = raw.Split(',');
                var lo = parts[0].Length == 0 ? "0" : parts[0];
                q = parts[1].Length == 0 ? $"{{{lo},}}" : $"{{{lo},{parts[1]}}}";
            }
            else q = $"{{{raw}}}";
            return lazy ? q + "?" : q;
        }

        private string? ParseAtom(bool atStart, int depth, out bool startAnchor)
        {
            startAnchor = false;
            var t = toks[_p++];
            switch (t.Kind)
            {
                case T.Lit: return Lit(t.C);
                case T.Coll: Multiline |= t.Nl; return Collection(t.Raw, t.Nl);
                case T.Brace: throw new VimRegexException(t.C == '@' ? "E64: \\@ follows nothing" : "E64: \\{ follows nothing");
                case T.Pct: return Pct(t);
                case T.UCmd: return UnderscoreCmd(t.C);
                case T.Cmd: return Command(t.C);
                case T.Op:
                    switch (t.C)
                    {
                        case '.': return "[^\\n]";
                        case '^':
                            if (atStart || _p == 1) { startAnchor = true; return "^"; }
                            return "\\^";
                        case '$':
                        {
                            var atEnd = _p >= toks.Count || toks[_p] is { Kind: T.Op, C: '|' or ')' or '&' } || toks[_p] is { Kind: T.Cmd, C: 'n' };
                            return atEnd ? "$" : "\\$";
                        }
                        case '~': return Regex.Escape(lastSubstitute);
                        case '*' or '+' or '?': return t.C == '*' && atStart ? "\\*" : throw new VimRegexException($"E64: {(t.C == '?' ? "\\=" : "\\" + t.C)} follows nothing");
                        case '<': return "\\b(?=\\w)";
                        case '>': return "\\b(?<=\\w)";
                        case '&': throw new VimRegexException("E-vimregex: \\& (concat) is not supported");
                        case '(':
                        {
                            _groups++;
                            var inner = ParseAlt(depth + 1);
                            if (_p >= toks.Count || toks[_p] is not { Kind: T.Op, C: ')' }) throw new VimRegexException("E54: Unmatched \\(");
                            _p++;
                            return "(" + inner + ")";
                        }
                        case '%':
                        {
                            // \%( ... \)
                            if (_p < toks.Count && toks[_p] is { Kind: T.Op, C: '(' })
                            {
                                _p++;
                                var inner = ParseAlt(depth + 1);
                                if (_p >= toks.Count || toks[_p] is not { Kind: T.Op, C: ')' }) throw new VimRegexException("E53: Unmatched \\%(");
                                _p++;
                                return "(?:" + inner + ")";
                            }
                            throw new VimRegexException("E71: Invalid character after \\%");
                        }
                        case ')': throw new VimRegexException("E55: Unmatched \\)");
                    }
                    break;
            }
            return null;
        }

        private static string Lit(int cp)
        {
            if (cp > 0xFFFF) return Regex.Escape(char.ConvertFromUtf32(cp));
            return cp switch
            {
                '\n' => "\\n",
                ' ' => " ",
                _ => Regex.Escape(((char)cp).ToString()),
            };
        }

        private string Command(int c)
        {
            switch (c)
            {
                case 'n': Multiline = true; return "\\n";
                case 't': return "\\t";
                case 'e': return "\\x1b";
                case 'r': return "\\r";
                case 'b': return "\\x08";
                case 's': return "[ \\t]";
                case 'S': return "[^ \\t\\n]";
                case 'd': return "[0-9]";
                case 'D': return "[^0-9\\n]";
                case 'w': return "[0-9A-Za-z_]";
                case 'W': return "[^0-9A-Za-z_\\n]";
                case 'a': return "[A-Za-z]";
                case 'A': return "[^A-Za-z\\n]";
                case 'l': return "[a-z]";
                case 'L': return "[^a-z\\n]";
                case 'u': return "[A-Z]";
                case 'U': return "[^A-Z\\n]";
                case 'x': return "[0-9A-Fa-f]";
                case 'X': return "[^0-9A-Fa-f\\n]";
                case 'o': return "[0-7]";
                case 'O': return "[^0-7\\n]";
                case 'h': return "[A-Za-z_]";
                case 'H': return "[^A-Za-z_\\n]";
                case 'i' or 'k': return "[\\w\\u00c0-\\u00ff]";
                case 'I' or 'K': return "[A-Za-z_\\u00c0-\\u00ff]";
                case 'f': return "[\\w/.\\-+,#$%~=]";
                case 'F': return "[A-Za-z/.\\-+,#$%~=_]";
                case 'p': return "[^\\x00-\\x1f\\x7f]";
                case 'P': return "[^\\x00-\\x1f\\x7f0-9]";
                case 'z':
                {
                    if (_p < toks.Count && toks[_p] is { Kind: T.Lit, C: 's' }) { _p++; HasZs = true; return "(?<zs>)"; }
                    if (_p < toks.Count && toks[_p] is { Kind: T.Lit, C: 'e' }) { _p++; HasZe = true; return "(?<ze>)"; }
                    throw new VimRegexException("E68: Invalid character after \\z");
                }
                case >= '1' and <= '9': return $"(?:\\{(char)c})";
                case '0': return "";
                case 'E' or 'L' or 'l' or 'u' or 'U': return "";
                case '/' or '?' or '\\': return Regex.Escape(((char)c).ToString());
            }
            return Regex.Escape(((char)c).ToString());
        }

        private string UnderscoreCmd(int c)
        {
            Multiline = true;
            switch (c)
            {
                case '^': return "^";
                case '$': return "$";
                case '.': return "[\\s\\S]";
                case 's': return "[ \\t\\n]";
                case 'S': return "[^ \\t]";
                case 'd': return "[0-9\\n]";
                case 'D': return "[^0-9]";
                case 'w': return "[0-9A-Za-z_\\n]";
                case 'W': return "[^0-9A-Za-z_]";
                case 'a': return "[A-Za-z\\n]";
                case 'A': return "[^A-Za-z]";
                case 'l': return "[a-z\\n]";
                case 'L': return "[^a-z]";
                case 'u': return "[A-Z\\n]";
                case 'U': return "[^A-Z]";
                case 'x': return "[0-9A-Fa-f\\n]";
                case 'X': return "[^0-9A-Fa-f]";
                case 'o': return "[0-7\\n]";
                case 'O': return "[^0-7]";
                case 'h': return "[A-Za-z_\\n]";
                case 'H': return "[^A-Za-z_]";
                case 'i' or 'k': return "[\\w\\u00c0-\\u00ff\\n]";
                case 'I' or 'K': return "[A-Za-z_\\u00c0-\\u00ff\\n]";
                case 'f': return "[\\w/.\\-+,#$%~=\\n]";
                case 'F': return "[A-Za-z/.\\-+,#$%~=_\\n]";
                case 'p': return "[^\\x00-\\x1f\\x7f]|\\n";
                case 'P': return "[^\\x00-\\x1f\\x7f0-9]|\\n";
            }
            throw new VimRegexException("E63: Invalid use of \\_");
        }

        private string Pct(Tok t)
        {
            if (t.C == '[')
            {
                // \%[abc]: optional sequence
                var inner = new Parser(Tokenize(t.Raw, true).Item1, lastSubstitute);
                var parts = new List<string>();
                while (inner.HasMore) { var a = inner.ParseAtom(false, 1, out _); if (a is not null) parts.Add(a); }
                var sb = new StringBuilder();
                for (var i = 0; i < parts.Count; i++) sb.Append("(?:").Append(parts[i]);
                for (var i = 0; i < parts.Count; i++) sb.Append(")?");
                return sb.ToString();
            }
            switch (t.Raw)
            {
                case "^": return "\\A";
                case "$": return "\\z";
            }
            throw new VimRegexException($"E-vimregex: \\%{t.Raw} is not supported (position atoms)");
        }

        private static string Collection(string raw, bool withNewline)
        {
            var i = 0;
            var negate = false;
            if (raw.Length > 0 && raw[0] == '^') { negate = true; i = 1; }
            var sb = new StringBuilder();
            var first = true;
            while (i < raw.Length)
            {
                var c = raw[i];
                if (c == ']' && first) { sb.Append("\\]"); i++; first = false; continue; }
                first = false;
                if (c == '[' && i + 1 < raw.Length && raw[i + 1] == ':')
                {
                    var end = raw.IndexOf(":]", i + 2, StringComparison.Ordinal);
                    if (end > 0)
                    {
                        sb.Append(ClassOf(raw.Substring(i + 2, end - i - 2)));
                        i = end + 2;
                        continue;
                    }
                }
                if (c == '\\' && i + 1 < raw.Length)
                {
                    var e = raw[i + 1];
                    i += 2;
                    switch (e)
                    {
                        case 'e': sb.Append("\\x1b"); continue;
                        case 't': sb.Append("\\t"); continue;
                        case 'r': sb.Append("\\r"); continue;
                        case 'b': sb.Append("\\x08"); continue;
                        case 'n': sb.Append("\\n"); continue;
                        case '\\' or ']' or '^' or '-': sb.Append('\\').Append(e); continue;
                        case 'd' or 'o' or 'x' or 'u' or 'U':
                        {
                            var radix = e switch { 'd' => 10, 'o' => 8, _ => 16 };
                            var max = e switch { 'x' => 2, 'u' => 4, 'U' => 8, 'o' => 4, _ => 10 };
                            var s = i;
                            while (i < raw.Length && i - s < max && Uri.IsHexDigit(raw[i]) && (radix == 16 || raw[i] < '0' + radix)) i++;
                            if (i == s) { sb.Append("\\\\").Append(e); continue; }
                            sb.Append(Regex.Escape(char.ConvertFromUtf32(Convert.ToInt32(raw.Substring(s, i - s), radix))));
                            continue;
                        }
                    }
                    sb.Append("\\\\").Append(Regex.Escape(e.ToString()));
                    continue;
                }
                if (c is '[' or '^' or '\\') { sb.Append('\\').Append(c); i++; continue; }
                sb.Append(c);
                i++;
            }
            var body = sb.ToString();
            if (negate) return "[^" + body + (withNewline ? "" : "\\n") + "]";
            return "[" + body + (withNewline ? "\\n" : "") + "]";
        }

        private static string ClassOf(string name) => name switch
        {
            "alpha" => "A-Za-z", "digit" => "0-9", "alnum" => "A-Za-z0-9", "lower" => "a-z", "upper" => "A-Z", "blank" => " \\t",
            "space" => " \\t\\n\\r\\f\\v", "punct" => "!-/:-@\\[-`{-~", "xdigit" => "0-9A-Fa-f", "cntrl" => "\\x00-\\x1f\\x7f",
            "print" => " -~", "graph" => "!-~", "return" => "\\r", "tab" => "\\t", "escape" => "\\x1b", "backspace" => "\\x08",
            "ident" => "\\w", "keyword" => "\\w", "fname" => "\\w/.\\-+,#$%~=",
            _ => "",
        };
    }
}
