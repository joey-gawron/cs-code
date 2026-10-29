using System.Text;

namespace CsCode.Editor.Snippets;

// VS Code snippet syntax: https://code.visualstudio.com/docs/editor/userdefinedsnippets#_snippet-syntax
//
//   any         ::= tabstop | placeholder | choice | variable | text
//   tabstop     ::= '$' int | '${' int '}' | '${' int transform '}'
//   placeholder ::= '${' int ':' any '}'
//   choice      ::= '${' int '|' text (',' text)* '|}'
//   variable    ::= '$' var | '${' var '}' | '${' var ':' any '}' | '${' var transform '}'
//   transform   ::= '/' regex '/' (format | text)+ '/' options
//   format      ::= '$' int | '${' int '}' | '${' int ':' ('/upcase'|'/downcase'|'/capitalize'|'/camelcase'|'/pascalcase') '}'
//                 | '${' int ':+' if '}' | '${' int ':?' if ':' else '}' | '${' int ':-' else '}' | '${' int ':' else '}'

public abstract record SnippetNode;

/// <summary>Literal text (escapes already resolved).</summary>
public sealed record TextNode(string Text) : SnippetNode;

/// <summary><c>$1</c>, <c>${1}</c> or <c>${1/regex/format/flags}</c>.</summary>
public sealed record TabstopNode(int Index, SnippetTransform? Transform = null) : SnippetNode;

/// <summary><c>${1:default text and nested ${2:placeholders}}</c>.</summary>
public sealed record PlaceholderNode(int Index, IReadOnlyList<SnippetNode> Children) : SnippetNode;

/// <summary><c>${1|one,two,three|}</c>: the first option is inserted, the others are offered.</summary>
public sealed record ChoiceNode(int Index, IReadOnlyList<string> Options) : SnippetNode;

/// <summary><c>$NAME</c>, <c>${NAME}</c>, <c>${NAME:default}</c>, <c>${NAME/regex/format/flags}</c>.</summary>
public sealed record VariableNode(string Name, IReadOnlyList<SnippetNode>? Default, SnippetTransform? Transform = null) : SnippetNode;

public sealed record SnippetTransform(string Pattern, IReadOnlyList<FormatNode> Format, string Flags);

public abstract record FormatNode;
public sealed record FormatText(string Text) : FormatNode;
public sealed record FormatGroup(int Group, FormatKind Kind = FormatKind.Plain, string? IfText = null, string? ElseText = null) : FormatNode;

public enum FormatKind { Plain, Upcase, Downcase, Capitalize, CamelCase, PascalCase, If, Else, IfElse }

public sealed record ParsedSnippet(IReadOnlyList<SnippetNode> Nodes);

/// <summary>
/// Parses VS Code snippet bodies. Never throws: anything that is not valid snippet syntax (an unterminated <c>${</c>, a stray
/// <c>$</c>) is kept as literal text, exactly like VS Code, so a user's half-written snippet degrades to plain text instead of failing.
/// </summary>
public static class SnippetParser
{
    public static ParsedSnippet Parse(string template) => new(new Parser(template).ParseTop());

    private sealed class Parser(string s)
    {
        private int _i;

        public IReadOnlyList<SnippetNode> ParseTop() => Sequence(inBraces: false);

        private static bool IsVarStart(char c) => c == '_' || char.IsAsciiLetter(c);
        private static bool IsVarPart(char c) => c == '_' || char.IsAsciiLetterOrDigit(c);

        private List<SnippetNode> Sequence(bool inBraces)
        {
            var nodes = new List<SnippetNode>();
            var text = new StringBuilder();
            void Flush()
            {
                if (text.Length == 0) return;
                nodes.Add(new TextNode(text.ToString()));
                text.Clear();
            }

            while (_i < s.Length)
            {
                var c = s[_i];
                if (c == '\\' && _i + 1 < s.Length && s[_i + 1] is '$' or '}' or '\\')
                {
                    text.Append(s[_i + 1]);
                    _i += 2;
                    continue;
                }
                if (c == '}' && inBraces) break;
                if (c == '$')
                {
                    var start = _i;
                    var node = Dollar();
                    if (node is not null)
                    {
                        Flush();
                        nodes.Add(node);
                        continue;
                    }
                    _i = start + 1; // not valid syntax: a literal '$'
                    text.Append('$');
                    continue;
                }
                text.Append(c);
                _i++;
            }
            Flush();
            return nodes;
        }

        private SnippetNode? Dollar()
        {
            _i++; // '$'
            if (_i >= s.Length) return null;
            if (char.IsAsciiDigit(s[_i])) return new TabstopNode(ReadInt());
            if (IsVarStart(s[_i])) return new VariableNode(ReadVar(), null);
            if (s[_i] != '{') return null;
            _i++;
            if (_i >= s.Length) return null;

            if (char.IsAsciiDigit(s[_i]))
            {
                var index = ReadInt();
                if (_i >= s.Length) return null;
                switch (s[_i])
                {
                    case '}':
                        _i++;
                        return new TabstopNode(index);
                    case ':':
                    {
                        _i++;
                        var children = Sequence(inBraces: true);
                        return Eat('}') ? new PlaceholderNode(index, children) : null;
                    }
                    case '|':
                    {
                        _i++;
                        var options = ChoiceOptions();
                        return options is null ? null : new ChoiceNode(index, options);
                    }
                    case '/':
                    {
                        _i++;
                        var t = Transform();
                        return t is not null && Eat('}') ? new TabstopNode(index, t) : null;
                    }
                    default:
                        return null;
                }
            }

            if (!IsVarStart(s[_i])) return null;
            var name = ReadVar();
            if (_i >= s.Length) return null;
            switch (s[_i])
            {
                case '}':
                    _i++;
                    return new VariableNode(name, null);
                case ':':
                {
                    _i++;
                    var def = Sequence(inBraces: true);
                    return Eat('}') ? new VariableNode(name, def) : null;
                }
                case '/':
                {
                    _i++;
                    var t = Transform();
                    return t is not null && Eat('}') ? new VariableNode(name, null, t) : null;
                }
                default:
                    return null;
            }
        }

        private bool Eat(char c)
        {
            if (_i < s.Length && s[_i] == c)
            {
                _i++;
                return true;
            }
            return false;
        }

        private int ReadInt()
        {
            long n = 0;
            while (_i < s.Length && char.IsAsciiDigit(s[_i]))
            {
                n = Math.Min(n * 10 + (s[_i] - '0'), int.MaxValue);
                _i++;
            }
            return (int)n;
        }

        private string ReadVar()
        {
            var start = _i;
            while (_i < s.Length && IsVarPart(s[_i])) _i++;
            return s[start.._i];
        }

        /// <summary>Options up to the closing <c>|}</c>; <c>\,</c> <c>\|</c> <c>\\</c> escape. Null when unterminated.</summary>
        private List<string>? ChoiceOptions()
        {
            var options = new List<string>();
            var sb = new StringBuilder();
            while (_i < s.Length)
            {
                var c = s[_i];
                if (c == '\\' && _i + 1 < s.Length && s[_i + 1] is ',' or '|' or '\\' or '$' or '}')
                {
                    sb.Append(s[_i + 1]);
                    _i += 2;
                }
                else if (c == ',')
                {
                    options.Add(sb.ToString());
                    sb.Clear();
                    _i++;
                }
                else if (c == '|' && _i + 1 < s.Length && s[_i + 1] == '}')
                {
                    options.Add(sb.ToString());
                    _i += 2;
                    return options;
                }
                else
                {
                    sb.Append(c);
                    _i++;
                }
            }
            return null;
        }

        /// <summary>After the opening '/': <c>regex/format/flags</c>; leaves the closing '}' for the caller.</summary>
        private SnippetTransform? Transform()
        {
            var regex = new StringBuilder();
            while (_i < s.Length && s[_i] != '/')
            {
                if (s[_i] == '\\' && _i + 1 < s.Length)
                {
                    if (s[_i + 1] == '/') regex.Append('/'); // \/ is a literal slash in the regex
                    else regex.Append(s[_i]).Append(s[_i + 1]);
                    _i += 2;
                }
                else regex.Append(s[_i++]);
            }
            if (!Eat('/')) return null;
            var format = FormatSequence();
            if (!Eat('/')) return null;
            var flagsStart = _i;
            while (_i < s.Length && char.IsAsciiLetter(s[_i])) _i++;
            return new SnippetTransform(regex.ToString(), format, s[flagsStart.._i]);
        }

        private List<FormatNode> FormatSequence()
        {
            var nodes = new List<FormatNode>();
            var text = new StringBuilder();
            void Flush()
            {
                if (text.Length == 0) return;
                nodes.Add(new FormatText(text.ToString()));
                text.Clear();
            }

            while (_i < s.Length && s[_i] != '/')
            {
                var c = s[_i];
                if (c == '\\' && _i + 1 < s.Length && s[_i + 1] is '$' or '/' or '\\')
                {
                    text.Append(s[_i + 1]);
                    _i += 2;
                    continue;
                }
                if (c == '$')
                {
                    var start = _i;
                    var node = FormatDollar();
                    if (node is not null)
                    {
                        Flush();
                        nodes.Add(node);
                        continue;
                    }
                    _i = start + 1;
                    text.Append('$');
                    continue;
                }
                text.Append(c);
                _i++;
            }
            Flush();
            return nodes;
        }

        private FormatGroup? FormatDollar()
        {
            _i++; // '$'
            if (_i >= s.Length) return null;
            if (char.IsAsciiDigit(s[_i])) return new FormatGroup(ReadInt());
            if (s[_i] != '{') return null;
            _i++;
            if (_i >= s.Length || !char.IsAsciiDigit(s[_i])) return null;
            var group = ReadInt();
            if (Eat('}')) return new FormatGroup(group);
            if (!Eat(':')) return null;
            if (_i >= s.Length) return null;

            if (s[_i] == '/')
            {
                _i++;
                var start = _i;
                while (_i < s.Length && s[_i] != '}') _i++;
                var kind = s[start.._i] switch
                {
                    "upcase" => FormatKind.Upcase,
                    "downcase" => FormatKind.Downcase,
                    "capitalize" => FormatKind.Capitalize,
                    "camelcase" => FormatKind.CamelCase,
                    "pascalcase" => FormatKind.PascalCase,
                    _ => (FormatKind?)null,
                };
                return kind is not null && Eat('}') ? new FormatGroup(group, kind.Value) : null;
            }
            if (s[_i] == '+')
            {
                _i++;
                var ifText = FormatBraceText(stopAtColon: false);
                return ifText is not null ? new FormatGroup(group, FormatKind.If, IfText: ifText) : null;
            }
            if (s[_i] == '?')
            {
                _i++;
                var ifText = FormatBraceText(stopAtColon: true);
                if (ifText is null || !Eat(':')) return null;
                var elseText = FormatBraceText(stopAtColon: false);
                return elseText is not null ? new FormatGroup(group, FormatKind.IfElse, ifText, elseText) : null;
            }
            if (s[_i] == '-') _i++; // '-' is optional: ${1:else} == ${1:-else}
            var other = FormatBraceText(stopAtColon: false);
            return other is not null ? new FormatGroup(group, FormatKind.Else, ElseText: other) : null;
        }

        /// <summary>Text up to the closing '}' (consumed), or up to an unescaped ':' (NOT consumed) when <paramref name="stopAtColon"/>.</summary>
        private string? FormatBraceText(bool stopAtColon)
        {
            var sb = new StringBuilder();
            while (_i < s.Length)
            {
                var c = s[_i];
                if (c == '\\' && _i + 1 < s.Length && s[_i + 1] is '$' or '}' or '\\' or ':' or '/')
                {
                    sb.Append(s[_i + 1]);
                    _i += 2;
                    continue;
                }
                if (c == '}' && !stopAtColon)
                {
                    _i++;
                    return sb.ToString();
                }
                if (c == ':' && stopAtColon) return sb.ToString();
                sb.Append(c);
                _i++;
            }
            return null;
        }
    }
}
