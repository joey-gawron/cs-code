using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.Editor.TreeSitter.Queries;

/// <summary>
/// Translates the Lua patterns used by Neovim's <c>#lua-match?</c> predicate into .NET regular expressions. Lua patterns are a small
/// language: character classes (<c>%a %d %l %s %u %w %x %p %c</c> and their negated upper-case forms), sets <c>[...]</c>, the quantifiers
/// <c>* + ? -</c> (<c>-</c> is a lazy <c>*</c>), anchors <c>^</c>/<c>$</c> only at the ends, and <c>%</c> escaping. The two exotic items
/// (<c>%b()</c> balanced pairs and <c>%f[set]</c> frontiers) are not translated: <see cref="TryTranslate"/> returns false and the
/// predicate is reported unsupported instead of silently matching something else.
/// </summary>
public static class LuaPattern
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>A compiled equivalent of <paramref name="lua"/>, or null when it uses an untranslatable item.</summary>
    public static Regex? Compile(string lua) =>
        TryTranslate(lua, out var pattern) ? new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Singleline, MatchTimeout) : null;

    public static bool TryTranslate(string lua, out string regex)
    {
        var sb = new StringBuilder(lua.Length + 8);
        regex = string.Empty;
        var i = 0;
        if (lua.StartsWith('^'))
        {
            sb.Append('^');
            i = 1;
        }
        while (i < lua.Length)
        {
            var c = lua[i];
            int itemStart = sb.Length;
            if (c == '$' && i == lua.Length - 1)
            {
                sb.Append(@"\z");
                i++;
                continue;
            }
            if (c == '%')
            {
                if (i + 1 >= lua.Length) return false;
                var n = lua[i + 1];
                if (n is 'b' or 'f') return false; // balanced match / frontier: not supported
                if (char.IsAsciiDigit(n)) return false; // back-reference to a capture
                sb.Append(ClassOf(n, inSet: false) ?? Regex.Escape(n.ToString()));
                i += 2;
            }
            else if (c == '[')
            {
                if (!TryTranslateSet(lua, ref i, sb)) return false;
            }
            else if (c == '.')
            {
                sb.Append('.');
                i++;
            }
            else if (c == '(' || c == ')')
            {
                // Lua captures group like regex groups; an empty capture "()" (a position capture) has no regex equivalent that matters for matching
                sb.Append(c == '(' ? "(?:" : ")");
                i++;
                continue;
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
            // a quantifier applies to the single item just emitted
            if (i < lua.Length && sb.Length > itemStart)
            {
                switch (lua[i])
                {
                    case '*': sb.Append('*'); i++; break;
                    case '+': sb.Append('+'); i++; break;
                    case '?': sb.Append('?'); i++; break;
                    case '-': sb.Append("*?"); i++; break;
                }
            }
        }
        regex = sb.ToString();
        return true;
    }

    private static bool TryTranslateSet(string lua, ref int i, StringBuilder sb)
    {
        // [set] / [^set]; ']' right after '[' or '[^' is a literal
        var j = i + 1;
        var negate = j < lua.Length && lua[j] == '^';
        if (negate) j++;
        var body = new StringBuilder();
        var first = true;
        while (j < lua.Length && (lua[j] != ']' || first))
        {
            var c = lua[j];
            first = false;
            if (c == '%')
            {
                if (j + 1 >= lua.Length) return false;
                var n = lua[j + 1];
                if (char.IsAsciiLetterUpper(n) && n is not ('D' or 'S') && ClassOf(n, inSet: false) is not null) return false; // [%U] etc.: no faithful bracket form
                var cls = ClassOf(n, inSet: true);
                body.Append(cls ?? EscapeInSet(n));
                j += 2;
                continue;
            }
            if (c == '-' && body.Length > 0 && j + 1 < lua.Length && lua[j + 1] != ']')
            {
                body.Append('-'); // a range a-z
                j++;
                continue;
            }
            body.Append(EscapeInSet(c));
            j++;
        }
        if (j >= lua.Length) return false; // unterminated
        sb.Append('[');
        if (negate) sb.Append('^');
        sb.Append(body);
        sb.Append(']');
        i = j + 1;
        return true;
    }

    private static string EscapeInSet(char c) => c is '\\' or ']' or '[' or '^' or '-' ? "\\" + c : c.ToString();

    /// <summary>A Lua class letter as a regex class (bracketed outside a set, bare inside one), or null when it is not a class letter.</summary>
    private static string? ClassOf(char letter, bool inSet)
    {
        var inner = char.ToLowerInvariant(letter) switch
        {
            'a' => "A-Za-z",
            'd' => "0-9",
            'l' => "a-z",
            'u' => "A-Z",
            's' => @" \t\n\r\f\v",
            'w' => "A-Za-z0-9",
            'x' => "0-9A-Fa-f",
            'p' => @"!-/:-@\[-`{-~",
            'c' => @"\x00-\x1f\x7f",
            'g' => @"!-~",
            _ => null,
        };
        if (inner is null) return null;
        var negated = char.IsUpper(letter);
        if (inSet)
        {
            // a negated class inside a set has no bracket form; only the two whose .NET shorthand means the same thing are allowed (see TryTranslateSet)
            if (!negated) return inner;
            return letter == 'D' ? @"\D" : @"\S";
        }
        return negated ? "[^" + inner + "]" : "[" + inner + "]";
    }
}
