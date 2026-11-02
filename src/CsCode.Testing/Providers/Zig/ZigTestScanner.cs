using System.Globalization;
using System.Text;

namespace CsCode.Testing.Providers.Zig;

/// <summary>
/// The tests a Zig source declares, from its text: <c>test "name" { … }</c> and decltests <c>test ident { … }</c>, at the top level of the file
/// or inside named containers (<c>const Inner = struct { test "x" { … } };</c>). A lexical scan, exact for this: <c>test</c> is a reserved word
/// (an identifier spelled so is <c>@"test"</c>), Zig has no block comments, and strings, character literals and <c>\\</c> multiline strings are
/// skipped. Unnamed <c>test { … }</c> blocks (reference aggregators) and tests inside anonymous containers (a generic function's
/// <c>return struct { … }</c>) are left out: neither can be asked for by name.
/// </summary>
public static class ZigTestScanner
{
    /// <summary>
    /// A declared test. <see cref="Qualified"/> is its name as the test runner prints it after the file's own prefix (<c>test.adds</c>,
    /// <c>decltest.add</c>, <c>Inner.test.nested</c>); <see cref="Name"/> is the string or the identifier; <see cref="Line"/> is 0-based.
    /// </summary>
    public sealed record Test(string Qualified, string Name, int Line, IReadOnlyList<string> Containers, bool IsDecltest);

    private enum Kind { Identifier, String, Punct, End }

    private readonly record struct Token(Kind Kind, string Text, int Line);

    public static IReadOnlyList<Test> Scan(string text)
    {
        var tests = new List<Test>();
        // one entry per open brace: the container's name, or null for any other brace (a block, an anonymous container, an initializer)
        var braces = new Stack<string?>();
        var recent = new List<Token>(8); // the tokens since the last ';', '{' or '}' (what a container declaration's opening brace follows)
        var lexer = new Lexer(text);
        while (true)
        {
            var t = lexer.Next();
            if (t.Kind == Kind.End) break;
            if (t.Kind == Kind.Identifier && t.Text == "test" && !lexer.LastWasQuotedIdentifier)
            {
                var name = lexer.Next();
                if (name.Kind is Kind.String or Kind.Identifier && lexer.Peek() == '{' && braces.All(b => b is not null))
                {
                    var containers = braces.Reverse().Select(b => b!).ToList();
                    var decl = name.Kind == Kind.Identifier;
                    var qualified = string.Concat(containers.Select(c => c + ".")) + (decl ? "decltest." : "test.") + name.Text;
                    tests.Add(new Test(qualified, name.Text, t.Line, containers, decl));
                }
                // the test's body opens with '{', handled as any brace below
                recent.Clear();
                if (name.Kind == Kind.Punct) Handle(name);
                continue;
            }
            Handle(t);
        }
        return tests;

        void Handle(Token t)
        {
            if (t.Kind != Kind.Punct) { if (recent.Count < 32) recent.Add(t); return; }
            switch (t.Text)
            {
                case "{":
                    braces.Push(ContainerName(recent));
                    recent.Clear();
                    break;
                case "}":
                    if (braces.Count > 0) braces.Pop();
                    recent.Clear();
                    break;
                case ";":
                    recent.Clear();
                    break;
                default:
                    if (recent.Count < 32) recent.Add(t);
                    break;
            }
        }
    }

    /// <summary>
    /// The name a brace opens a container for: <c>[pub] const|var Name = [extern|packed] struct|union|enum|opaque [(…)] {</c>; null for any other.
    /// </summary>
    private static string? ContainerName(List<Token> tokens)
    {
        var i = tokens.Count - 1;
        if (i >= 0 && tokens[i].Text == ")")
        {
            // union(enum) / enum(u8) / union(Tag): back over the balanced parentheses
            var depth = 0;
            for (; i >= 0; i--)
            {
                if (tokens[i].Text == ")") depth++;
                else if (tokens[i].Text == "(" && --depth == 0) { i--; break; }
            }
        }
        if (i < 0 || tokens[i].Kind != Kind.Identifier || tokens[i].Text is not ("struct" or "union" or "enum" or "opaque")) return null;
        i--;
        if (i >= 0 && tokens[i].Text is "extern" or "packed") i--;
        if (i < 2 || tokens[i].Text != "=" || tokens[i - 1].Kind != Kind.Identifier || tokens[i - 2].Text is not ("const" or "var")) return null;
        return tokens[i - 1].Text;
    }

    private sealed class Lexer(string text)
    {
        private int _pos;
        private int _line;

        public bool LastWasQuotedIdentifier { get; private set; }

        /// <summary>The next character that is not whitespace or inside a comment, without consuming anything.</summary>
        public char Peek()
        {
            var save = (_pos, _line);
            SkipTrivia();
            var c = _pos < text.Length ? text[_pos] : '\0';
            (_pos, _line) = save;
            return c;
        }

        public Token Next()
        {
            LastWasQuotedIdentifier = false;
            while (true)
            {
                SkipTrivia();
                if (_pos >= text.Length) return new Token(Kind.End, "", _line);
                var c = text[_pos];
                var line = _line;
                if (c == '"') return new Token(Kind.String, ReadString(), line);
                if (c == '\'') { ReadString(); continue; }
                if (c == '\\' && _pos + 1 < text.Length && text[_pos + 1] == '\\') { SkipToLineEnd(); continue; } // a multiline string line
                if (c == '@' && _pos + 1 < text.Length && text[_pos + 1] == '"')
                {
                    _pos++;
                    var quoted = ReadString();
                    LastWasQuotedIdentifier = true;
                    return new Token(Kind.Identifier, quoted, line);
                }
                if (char.IsLetter(c) || c == '_')
                {
                    var start = _pos;
                    while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
                    return new Token(Kind.Identifier, text[start.._pos], line);
                }
                if (char.IsDigit(c))
                {
                    while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '_' or '.')) _pos++;
                    continue;
                }
                _pos++;
                return new Token(Kind.Punct, c.ToString(), line);
            }
        }

        private void SkipTrivia()
        {
            while (_pos < text.Length)
            {
                var c = text[_pos];
                if (c == '\n') { _line++; _pos++; }
                else if (char.IsWhiteSpace(c)) _pos++;
                else if (c == '/' && _pos + 1 < text.Length && text[_pos + 1] == '/') SkipToLineEnd();
                else break;
            }
        }

        private void SkipToLineEnd()
        {
            var nl = text.IndexOf('\n', _pos);
            _pos = nl < 0 ? text.Length : nl;
        }

        /// <summary>A string or character literal from its opening quote: its decoded text (Zig's escapes); a literal never spans lines.</summary>
        private string ReadString()
        {
            var quote = text[_pos++];
            var sb = new StringBuilder();
            while (_pos < text.Length && text[_pos] != quote && text[_pos] != '\n')
            {
                var c = text[_pos++];
                if (c != '\\' || _pos >= text.Length) { sb.Append(c); continue; }
                var e = text[_pos++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'x' when _pos + 2 <= text.Length && int.TryParse(text.AsSpan(_pos, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b):
                        sb.Append((char)b);
                        _pos += 2;
                        break;
                    case 'u' when _pos < text.Length && text[_pos] == '{':
                        var close = text.IndexOf('}', _pos);
                        if (close > _pos && int.TryParse(text.AsSpan(_pos + 1, close - _pos - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp)
                            && cp is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
                        {
                            sb.Append(char.ConvertFromUtf32(cp));
                            _pos = close + 1;
                        }
                        break;
                    default: sb.Append(e); break; // \\ \" \'
                }
            }
            if (_pos < text.Length && text[_pos] == quote) _pos++;
            return sb.ToString();
        }
    }
}
