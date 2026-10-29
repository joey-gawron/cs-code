using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CsCode.Platform.Context;

/// <summary>Read-only view of the context the clause is evaluated against (implemented by the context key service).</summary>
internal interface IContextReader
{
    CtxValue Get(string key);
    CtxValue GetConfig(string settingKey);
}

/// <summary>Public face of the when-clause grammar: validation and dependency discovery (menus re-evaluate when these keys change).</summary>
public static class WhenClause
{
    /// <summary>Returns a description of the first syntax error, or null when <paramref name="clause"/> is valid (empty counts as valid: always true).</summary>
    public static string? Validate(string? clause) =>
        string.IsNullOrWhiteSpace(clause) ? null : (WhenParser.Parse(clause) as ErrorNode)?.Message;

    /// <summary>The context keys the clause reads (config keys appear as <c>config.x.y</c>). Empty for an invalid clause.</summary>
    public static IReadOnlyCollection<string> GetKeys(string? clause)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(clause)) WhenParser.Parse(clause).CollectKeys(set);
        return set;
    }
}

internal abstract class WhenExpr
{
    public abstract bool Evaluate(IContextReader ctx);
    public virtual void CollectKeys(HashSet<string> keys) { }

    protected static string Display(string key, bool config) => config ? "config." + key : key;
}

internal sealed class ErrorNode(string message) : WhenExpr
{
    public string Message { get; } = message;
    public override bool Evaluate(IContextReader ctx) => false; // a malformed clause is never satisfied; it must not throw at evaluation time
}

internal sealed class ConstNode(bool value) : WhenExpr
{
    public override bool Evaluate(IContextReader ctx) => value;
}

internal sealed class KeyNode(string key, bool config) : WhenExpr
{
    public override bool Evaluate(IContextReader ctx) => (config ? ctx.GetConfig(key) : ctx.Get(key)).IsTruthy;
    public override void CollectKeys(HashSet<string> keys) => keys.Add(Display(key, config));
}

internal sealed class NotNode(WhenExpr inner) : WhenExpr
{
    public override bool Evaluate(IContextReader ctx) => !inner.Evaluate(ctx);
    public override void CollectKeys(HashSet<string> keys) => inner.CollectKeys(keys);
}

internal sealed class AndNode(WhenExpr[] items) : WhenExpr
{
    public override bool Evaluate(IContextReader ctx)
    {
        for (var i = 0; i < items.Length; i++)
            if (!items[i].Evaluate(ctx)) return false; // short-circuit
        return true;
    }
    public override void CollectKeys(HashSet<string> keys) { foreach (var i in items) i.CollectKeys(keys); }
}

internal sealed class OrNode(WhenExpr[] items) : WhenExpr
{
    public override bool Evaluate(IContextReader ctx)
    {
        for (var i = 0; i < items.Length; i++)
            if (items[i].Evaluate(ctx)) return true; // short-circuit
        return false;
    }
    public override void CollectKeys(HashSet<string> keys) { foreach (var i in items) i.CollectKeys(keys); }
}

/// <summary>A right-hand-side literal. Quoted text is always a string; an unquoted number is a number; true/false are booleans; other bare words are strings.</summary>
internal readonly struct Literal(string text, bool quoted)
{
    public string Text { get; } = text;
    public bool Quoted { get; } = quoted;
    public bool IsBool => !Quoted && (Text == "true" || Text == "false");
    public bool BoolValue => Text == "true";
    public bool IsNumber => !Quoted && double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    public double NumberValue => double.Parse(Text, NumberStyles.Float, CultureInfo.InvariantCulture);
}

internal sealed class EqNode(string key, bool config, Literal lit, bool negate, bool strict) : WhenExpr
{
    // Parsed once: Evaluate must not parse or allocate.
    private readonly bool _isBool = lit.IsBool;
    private readonly bool _boolValue = lit.IsBool && lit.BoolValue;
    private readonly bool _strictNumber = lit.IsNumber;                       // unquoted numeric literal
    private readonly bool _looseNumber = double.TryParse(lit.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    private readonly double _number = double.TryParse(lit.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    public override bool Evaluate(IContextReader ctx)
    {
        var v = config ? ctx.GetConfig(key) : ctx.Get(key);
        return (strict ? StrictEquals(v) : LooseEquals(v)) != negate;
    }

    // `==`: `key == true` is plain truthiness; a numeric value compares numerically with a numeric literal (quoted or not); strings
    // compare ordinally (so "10" == '10' but "10" != '10.0'); an unset key equals nothing, hence `unset != x` is true.
    private bool LooseEquals(in CtxValue v)
    {
        if (_isBool) return v.IsTruthy == _boolValue;
        switch (v.Kind)
        {
            case CtxKind.Number:
                return _looseNumber && v.Number == _number;
            case CtxKind.String:
                return string.Equals(v.Text, lit.Text, StringComparison.Ordinal);
            case CtxKind.Bool:
                return string.Equals(lit.Text, v.Bool ? "true" : "false", StringComparison.OrdinalIgnoreCase);
            case CtxKind.Json:
                return v.Json.ValueKind switch
                {
                    JsonValueKind.String => v.Json.ValueEquals(lit.Text),
                    JsonValueKind.Number => _looseNumber && v.Json.TryGetDouble(out var jd) && jd == _number,
                    JsonValueKind.True => string.Equals(lit.Text, "true", StringComparison.OrdinalIgnoreCase),
                    JsonValueKind.False => string.Equals(lit.Text, "false", StringComparison.OrdinalIgnoreCase),
                    _ => false,
                };
            default:
                return false;
        }
    }

    // `===`: the value's type must match the literal's type as well (number literal needs a number, text literal needs a string).
    private bool StrictEquals(in CtxValue v)
    {
        if (_isBool) return v.IsBoolLike && v.AsBool == _boolValue;
        if (_strictNumber)
        {
            return v.Kind == CtxKind.Number ? v.Number == _number
                : v.Kind == CtxKind.Json && v.Json.ValueKind == JsonValueKind.Number && v.Json.TryGetDouble(out var jd) && jd == _number;
        }
        return v.StringEquals(lit.Text);
    }

    public override void CollectKeys(HashSet<string> keys) => keys.Add(Display(key, config));
}

internal enum CmpOp { Lt, Le, Gt, Ge }

internal sealed class CmpNode(string key, bool config, CmpOp op, double rhs) : WhenExpr
{
    public override bool Evaluate(IContextReader ctx)
    {
        var l = (config ? ctx.GetConfig(key) : ctx.Get(key)).AsNumber();
        if (double.IsNaN(l)) return false; // non-numeric on either side: all four comparisons are false (JS semantics)
        return op switch { CmpOp.Lt => l < rhs, CmpOp.Le => l <= rhs, CmpOp.Gt => l > rhs, _ => l >= rhs };
    }
    public override void CollectKeys(HashSet<string> keys) => keys.Add(Display(key, config));
}

internal sealed class RegexNode(string key, bool config, Regex regex) : WhenExpr
{
    public override bool Evaluate(IContextReader ctx)
    {
        var v = config ? ctx.GetConfig(key) : ctx.Get(key);
        try
        {
            return v.TryGetString(out var s) ? regex.IsMatch(s.AsSpan()) : regex.IsMatch(v.AsText().AsSpan());
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
    public override void CollectKeys(HashSet<string> keys) => keys.Add(Display(key, config));
}

/// <summary><c>left in rightKey</c>: the right key holds a collection (array/list/set/dictionary/JSON array or object) that must contain the left value.</summary>
internal sealed class InNode(string? leftKey, bool leftConfig, string? leftLiteral, string rightKey, bool rightConfig, bool negate) : WhenExpr
{
    public override bool Evaluate(IContextReader ctx)
    {
        var left = leftKey is null ? CtxValue.FromString(leftLiteral!) : (leftConfig ? ctx.GetConfig(leftKey) : ctx.Get(leftKey));
        var right = rightConfig ? ctx.GetConfig(rightKey) : ctx.Get(rightKey);
        return Contains(right, left) != negate;
    }

    private static bool Contains(in CtxValue collection, in CtxValue item)
    {
        switch (collection.Kind)
        {
            case CtxKind.Json:
                switch (collection.Json.ValueKind)
                {
                    case JsonValueKind.Array:
                        foreach (var e in collection.Json.EnumerateArray())
                            if (item.LooseEquals(CtxValue.FromJson(e))) return true;
                        return false;
                    case JsonValueKind.Object:
                        return item.TryGetString(out var name) && collection.Json.TryGetProperty(name, out _);
                    default: return false;
                }
            case CtxKind.String:
                return item.TryGetString(out var needle) && collection.Text!.Contains(needle, StringComparison.Ordinal);
            case CtxKind.Other:
                return OtherContains(collection.Other!, item);
            default:
                return false;
        }
    }

    private static bool OtherContains(object collection, in CtxValue item)
    {
        // Typed fast paths first: iterating through IEnumerable would box an enumerator (an allocation per keystroke).
        switch (collection)
        {
            case string[] sa:
                if (!item.TryGetString(out var s1)) return false;
                foreach (var x in sa) if (string.Equals(x, s1, StringComparison.Ordinal)) return true;
                return false;
            case object?[] oa:
                foreach (var x in oa) if (item.LooseEquals(CtxValue.From(x))) return true;
                return false;
            case ISet<string> set:
                return item.TryGetString(out var s2) && set.Contains(s2);
            case IReadOnlySet<string> rset:
                return item.TryGetString(out var s3) && rset.Contains(s3);
            case IDictionary dict:
                return item.TryGetString(out var k) ? dict.Contains(k) : false;
            case IList list:
                for (var i = 0; i < list.Count; i++)
                    if (item.LooseEquals(CtxValue.From(list[i]))) return true;
                return false;
            case IEnumerable seq:
                foreach (var x in seq) if (item.LooseEquals(CtxValue.From(x))) return true;
                return false;
            default:
                return false;
        }
    }

    public override void CollectKeys(HashSet<string> keys)
    {
        if (leftKey is not null) keys.Add(Display(leftKey, leftConfig));
        keys.Add(Display(rightKey, rightConfig));
    }
}

/// <summary>
/// Parser for VS Code when-clauses: <c>!</c>, <c>&amp;&amp;</c>, <c>||</c> (precedence ! &gt; &amp;&amp; &gt; ||), parentheses,
/// <c>== === != !==</c>, <c>&lt; &lt;= &gt; &gt;=</c>, <c>=~ /re/flags</c>, <c>in</c>, <c>not in</c>, <c>true</c>/<c>false</c>, quoted strings and numbers.
/// Never throws: syntax errors yield an <see cref="ErrorNode"/> that evaluates to false.
/// </summary>
internal sealed class WhenParser
{
    private const int MaxDepth = 64;
    private const string ConfigPrefix = "config.";

    private enum T { LParen, RParen, Not, And, Or, Eq, EqEq, Neq, NeqEq, Lt, Le, Gt, Ge, Match, Word, Quoted, Regex, End }

    private readonly record struct Token(T Type, string Text, string? Flags, int Pos);

    private sealed class SyntaxError(string message) : Exception(message);

    private readonly List<Token> _tokens = new();
    private int _i;
    private int _depth;

    public static WhenExpr Parse(string clause)
    {
        try
        {
            var p = new WhenParser();
            p.Lex(clause);
            var expr = p.ParseOr();
            if (p.Peek.Type != T.End) throw new SyntaxError($"Unexpected '{p.Peek.Text}' at position {p.Peek.Pos}");
            return expr;
        }
        catch (SyntaxError ex)
        {
            return new ErrorNode(ex.Message);
        }
    }

    // ---- lexer -------------------------------------------------------------------------------------------------------------------

    private static bool IsWordTerminator(char c) => char.IsWhiteSpace(c) || c is '(' or ')' or '!' or '&' or '|' or '=' or '<' or '>';

    private void Lex(string s)
    {
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            var start = i;
            switch (c)
            {
                case '(': Add(T.LParen, "(", start); i++; break;
                case ')': Add(T.RParen, ")", start); i++; break;
                case '!':
                    if (Next(s, i, '=')) { if (Next(s, i + 1, '=')) { Add(T.NeqEq, "!==", start); i += 3; } else { Add(T.Neq, "!=", start); i += 2; } }
                    else { Add(T.Not, "!", start); i++; }
                    break;
                case '=':
                    if (Next(s, i, '=')) { if (Next(s, i + 1, '=')) { Add(T.EqEq, "===", start); i += 3; } else { Add(T.Eq, "==", start); i += 2; } }
                    else if (Next(s, i, '~')) { Add(T.Match, "=~", start); i += 2; }
                    else throw new SyntaxError($"Unexpected '=' at position {i} (did you mean '==' or '=~'?)");
                    break;
                case '<': if (Next(s, i, '=')) { Add(T.Le, "<=", start); i += 2; } else { Add(T.Lt, "<", start); i++; } break;
                case '>': if (Next(s, i, '=')) { Add(T.Ge, ">=", start); i += 2; } else { Add(T.Gt, ">", start); i++; } break;
                case '&':
                    if (!Next(s, i, '&')) throw new SyntaxError($"Unexpected '&' at position {i} (did you mean '&&'?)");
                    Add(T.And, "&&", start); i += 2; break;
                case '|':
                    if (!Next(s, i, '|')) throw new SyntaxError($"Unexpected '|' at position {i} (did you mean '||'?)");
                    Add(T.Or, "||", start); i += 2; break;
                case '\'' or '"':
                    i = LexQuoted(s, i);
                    break;
                case '/' when _tokens.Count > 0 && _tokens[^1].Type == T.Match:
                    i = LexRegex(s, i);
                    break;
                default:
                    while (i < s.Length && !IsWordTerminator(s[i])) i++;
                    Add(T.Word, s[start..i], start);
                    break;
            }
        }
        Add(T.End, "<end>", s.Length);
    }

    private static bool Next(string s, int i, char c) => i + 1 < s.Length && s[i + 1] == c;

    private void Add(T type, string text, int pos, string? flags = null) => _tokens.Add(new Token(type, text, flags, pos));

    private int LexQuoted(string s, int i)
    {
        var quote = s[i];
        var sb = new StringBuilder();
        var j = i + 1;
        while (j < s.Length)
        {
            var c = s[j];
            if (c == '\\' && j + 1 < s.Length) { sb.Append(s[j + 1]); j += 2; continue; }
            if (c == quote) { Add(T.Quoted, sb.ToString(), i); return j + 1; }
            sb.Append(c);
            j++;
        }
        throw new SyntaxError($"Unterminated string starting at position {i}");
    }

    private int LexRegex(string s, int i)
    {
        var sb = new StringBuilder();
        var j = i + 1;
        var inClass = false;
        while (j < s.Length)
        {
            var c = s[j];
            if (c == '\\' && j + 1 < s.Length)
            {
                // "\/" is an escaped delimiter; everything else stays as the regex wrote it
                if (s[j + 1] == '/') sb.Append('/'); else sb.Append(c).Append(s[j + 1]);
                j += 2;
                continue;
            }
            if (c == '[') inClass = true;
            else if (c == ']') inClass = false;
            else if (c == '/' && !inClass)
            {
                var f = j + 1;
                while (f < s.Length && char.IsAsciiLetter(s[f])) f++;
                Add(T.Regex, sb.ToString(), i, s[(j + 1)..f]);
                return f;
            }
            sb.Append(c);
            j++;
        }
        throw new SyntaxError($"Unterminated regular expression starting at position {i}");
    }

    // ---- parser ------------------------------------------------------------------------------------------------------------------

    private Token Peek => _tokens[_i];
    private Token Take() => _tokens[_i++];

    private bool IsWord(Token t, string text) => t.Type == T.Word && t.Text == text;

    private WhenExpr ParseOr()
    {
        var items = new List<WhenExpr> { ParseAnd() };
        while (Peek.Type == T.Or) { Take(); items.Add(ParseAnd()); }
        return items.Count == 1 ? items[0] : new OrNode([.. items]);
    }

    private WhenExpr ParseAnd()
    {
        var items = new List<WhenExpr> { ParseUnary() };
        while (Peek.Type == T.And) { Take(); items.Add(ParseUnary()); }
        return items.Count == 1 ? items[0] : new AndNode([.. items]);
    }

    private WhenExpr ParseUnary()
    {
        if (++_depth > MaxDepth) throw new SyntaxError("Expression is nested too deeply");
        try
        {
            if (Peek.Type == T.Not) { Take(); return new NotNode(ParseUnary()); }
            return ParsePrimary();
        }
        finally { _depth--; }
    }

    private WhenExpr ParsePrimary()
    {
        var t = Take();
        switch (t.Type)
        {
            case T.LParen:
            {
                var inner = ParseOr();
                if (Peek.Type != T.RParen) throw new SyntaxError($"Expected ')' at position {Peek.Pos}");
                Take();
                return inner;
            }
            case T.Word or T.Quoted:
                return ParseOperand(t);
            case T.End:
                throw new SyntaxError("Unexpected end of expression");
            default:
                throw new SyntaxError($"Unexpected '{t.Text}' at position {t.Pos}");
        }
    }

    private static (string Key, bool Config) SplitConfig(string key) =>
        key.StartsWith(ConfigPrefix, StringComparison.Ordinal) && key.Length > ConfigPrefix.Length ? (key[ConfigPrefix.Length..], true) : (key, false);

    private WhenExpr ParseOperand(Token left)
    {
        var op = Peek;

        // `'literal' in key` / `'literal' not in key`: only valid before an in-operator
        if (left.Type == T.Quoted)
        {
            if (IsWord(op, "in")) { Take(); return new InNode(null, false, left.Text, ReadKey(out var rc), rc, false); }
            if (IsWord(op, "not") && IsWord(_tokens[_i + 1], "in")) { Take(); Take(); return new InNode(null, false, left.Text, ReadKey(out var rc2), rc2, true); }
            throw new SyntaxError($"A quoted string at position {left.Pos} must be followed by 'in' or 'not in'");
        }

        var (key, config) = SplitConfig(left.Text);

        switch (op.Type)
        {
            case T.Eq or T.EqEq or T.Neq or T.NeqEq:
            {
                Take();
                var v = Take();
                if (v.Type is not (T.Word or T.Quoted)) throw new SyntaxError($"Expected a value after '{op.Text}' at position {op.Pos}");
                var negate = op.Type is T.Neq or T.NeqEq;
                var strict = op.Type is T.EqEq or T.NeqEq;
                return new EqNode(key, config, new Literal(v.Text, v.Type == T.Quoted), negate, strict);
            }
            case T.Lt or T.Le or T.Gt or T.Ge:
            {
                Take();
                var v = Take();
                if (v.Type is not (T.Word or T.Quoted) || !double.TryParse(v.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    throw new SyntaxError($"Expected a number after '{op.Text}' at position {op.Pos}");
                var cmp = op.Type switch { T.Lt => CmpOp.Lt, T.Le => CmpOp.Le, T.Gt => CmpOp.Gt, _ => CmpOp.Ge };
                return new CmpNode(key, config, cmp, number);
            }
            case T.Match:
            {
                Take();
                var r = Take();
                if (r.Type != T.Regex) throw new SyntaxError($"Expected /regex/ after '=~' at position {op.Pos}");
                return new RegexNode(key, config, BuildRegex(r));
            }
            default:
                break;
        }

        if (IsWord(op, "in")) { Take(); return new InNode(key, config, null, ReadKey(out var rc), rc, false); }
        if (IsWord(op, "not") && IsWord(_tokens[_i + 1], "in")) { Take(); Take(); return new InNode(key, config, null, ReadKey(out var rc2), rc2, true); }

        // bare key or constant
        if (!config && key == "true") return new ConstNode(true);
        if (!config && key == "false") return new ConstNode(false);
        return new KeyNode(key, config);
    }

    private string ReadKey(out bool config)
    {
        var t = Take();
        if (t.Type != T.Word) throw new SyntaxError($"Expected a context key at position {t.Pos}");
        var (key, cfg) = SplitConfig(t.Text);
        config = cfg;
        return key;
    }

    private static Regex BuildRegex(Token t)
    {
        var options = RegexOptions.CultureInvariant;
        foreach (var f in t.Flags ?? "")
        {
            switch (f)
            {
                case 'i': options |= RegexOptions.IgnoreCase; break;
                case 'm': options |= RegexOptions.Multiline; break;
                case 's': options |= RegexOptions.Singleline; break;
                case 'u' or 'g' or 'y' or 'd': break; // no effect for matching a string once
                default: throw new SyntaxError($"Unknown regular-expression flag '{f}' at position {t.Pos}");
            }
        }
        try
        {
            // Linear-time engine first (immune to catastrophic backtracking); fall back for patterns it cannot express.
            return new Regex(t.Text, options | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
            try { return new Regex(t.Text, options, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException ex) { throw new SyntaxError($"Invalid regular expression at position {t.Pos}: {ex.Message}"); }
        }
        catch (ArgumentException ex)
        {
            throw new SyntaxError($"Invalid regular expression at position {t.Pos}: {ex.Message}");
        }
    }
}
