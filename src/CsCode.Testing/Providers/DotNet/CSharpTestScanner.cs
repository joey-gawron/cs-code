using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.Testing.Providers.DotNet;

/// <summary>
/// Finds the test methods a C# file declares from its text alone (no compiler): methods carrying an xUnit (<c>[Fact]</c>, <c>[Theory]</c> and
/// every <c>*Fact</c>/<c>*Theory</c> such as Avalonia's <c>[AvaloniaFact]</c>), NUnit (<c>[Test]</c>, <c>[TestCase]</c>, <c>[TestCaseSource]</c>)
/// or MSTest (<c>[TestMethod]</c>, <c>[DataTestMethod]</c>) attribute, with their namespace (block or file-scoped) and class (nested classes
/// joined with <c>+</c>, as the runners name them). Comments, strings and character literals are blanked first, so a commented-out test is not
/// one. What it cannot see: tests inherited from a base class in another file, and generated ones; a run adds those as it reports them.
/// </summary>
public static partial class CSharpTestScanner
{
    public sealed record Found(string FullName, string Namespace, string ClassName, string Method, int Line, int ClassLine, string? SkipReason);

    private enum Scope { Namespace, Type, Other }

    private sealed record Frame(Scope Kind, string Name, int Line);

    public static IReadOnlyList<Found> Scan(string text)
    {
        var code = Blank(text);
        var result = new List<Found>();
        var stack = new List<Frame>();
        string fileNamespace = "";
        string? pendingTypeName = null; int pendingTypeLine = 0;
        string? pendingNamespace = null;
        var pendingTest = false; string? pendingSkip = null;
        var lineStarts = LineStarts(text);
        int i = 0, n = code.Length;
        // the identifier last seen at the current depth and where, for "Name(" member detection
        while (i < n)
        {
            var c = code[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '[')
            {
                // an attribute section (or an indexer / array: only attributes matter, and only inside a type's body)
                var end = MatchBracket(code, i, '[', ']');
                if (InType(stack) && !InMember(stack))
                {
                    var section = code[(i + 1)..end];
                    var original = text[(i + 1)..Math.Min(end, text.Length)];
                    foreach (var name in AttributeNames(section))
                    {
                        if (IsTestAttribute(name)) pendingTest = true;
                        if (name is "Ignore" or "IgnoreAttribute") pendingSkip ??= "Ignored";
                    }
                    if (SkipArgument().Match(original) is { Success: true } m) pendingSkip = m.Groups[1].Value;
                }
                i = end + 1;
                continue;
            }
            if (c == '{')
            {
                if (pendingNamespace is not null) { stack.Add(new Frame(Scope.Namespace, pendingNamespace, 0)); pendingNamespace = null; }
                else if (pendingTypeName is not null) { stack.Add(new Frame(Scope.Type, pendingTypeName, pendingTypeLine)); pendingTypeName = null; }
                else stack.Add(new Frame(Scope.Other, "", 0));
                pendingTest = false; pendingSkip = null;
                i++;
                continue;
            }
            if (c == '}')
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                pendingTest = false; pendingSkip = null; pendingTypeName = null;
                i++;
                continue;
            }
            if (c == ';')
            {
                if (pendingNamespace is not null) { fileNamespace = pendingNamespace; pendingNamespace = null; }
                pendingTypeName = null; // a record without a body
                i++;
                continue;
            }
            if (IsIdentStart(c))
            {
                var start = i;
                while (i < n && IsIdentPart(code[i])) i++;
                var word = code[start..i];
                if (InMember(stack)) continue;
                if (word == "namespace")
                {
                    var j = SkipWs(code, i);
                    var nsStart = j;
                    while (j < n && (IsIdentPart(code[j]) || code[j] == '.')) j++;
                    pendingNamespace = code[nsStart..j];
                    i = j;
                    continue;
                }
                if (word is "class" or "struct" or "record" or "interface")
                {
                    var j = SkipWs(code, i);
                    if (word == "record" && code.AsSpan(j).StartsWith("class")) j = SkipWs(code, j + 5);
                    else if (word == "record" && code.AsSpan(j).StartsWith("struct")) j = SkipWs(code, j + 6);
                    var nameStart = j;
                    while (j < n && IsIdentPart(code[j])) j++;
                    if (j > nameStart)
                    {
                        pendingTypeName = code[nameStart..j];
                        pendingTypeLine = LineOf(lineStarts, nameStart);
                    }
                    pendingTest = false; pendingSkip = null;
                    i = j;
                    continue;
                }
                if (pendingTest && InType(stack))
                {
                    // the method's name is the identifier right before its parameter list (or its generic parameter list)
                    var j = SkipWs(code, i);
                    var afterGeneric = j < n && code[j] == '<' ? SkipWs(code, MatchBracket(code, j, '<', '>') + 1) : j;
                    if (afterGeneric < n && code[afterGeneric] == '(' && !IsKeyword(word))
                    {
                        var ns = NamespaceOf(stack, fileNamespace);
                        var cls = string.Join('+', stack.Where(f => f.Kind == Scope.Type).Select(f => f.Name));
                        var classLine = stack.Last(f => f.Kind == Scope.Type).Line;
                        var full = (ns.Length > 0 ? ns + "." : "") + cls + "." + word;
                        result.Add(new Found(full, ns, cls, word, LineOf(lineStarts, start), classLine, pendingSkip));
                        pendingTest = false; pendingSkip = null;
                        // skip the parameter list and the body or expression body
                        i = SkipMember(code, j);
                    }
                }
                continue;
            }
            if (c == '(' ) { i = MatchBracket(code, i, '(', ')') + 1; continue; }
            i++;
        }
        return result;
    }

    private static bool InType(List<Frame> stack) => stack.Count > 0 && stack[^1].Kind == Scope.Type;

    private static bool InMember(List<Frame> stack) => stack.Any(f => f.Kind == Scope.Other);

    private static string NamespaceOf(List<Frame> stack, string fileNamespace)
    {
        var parts = new List<string>();
        if (fileNamespace.Length > 0) parts.Add(fileNamespace);
        parts.AddRange(stack.Where(f => f.Kind == Scope.Namespace).Select(f => f.Name));
        return string.Join('.', parts);
    }

    /// <summary>xUnit, NUnit and MSTest test attributes (with or without the Attribute suffix, with or without a namespace).</summary>
    internal static bool IsTestAttribute(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        if (name.EndsWith("Attribute", StringComparison.Ordinal)) name = name[..^"Attribute".Length];
        return name is "Test" or "TestCase" or "TestCaseSource" or "TestMethod" or "DataTestMethod"
            || name.EndsWith("Fact", StringComparison.Ordinal) || name.EndsWith("Theory", StringComparison.Ordinal);
    }

    private static IEnumerable<string> AttributeNames(string section)
    {
        var depth = 0;
        var sb = new StringBuilder();
        var expectName = true;
        foreach (var ch in section)
        {
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            else if (depth == 0 && ch == ',') { if (sb.Length > 0) yield return sb.ToString(); sb.Clear(); expectName = true; continue; }
            if (depth == 0 && expectName)
            {
                if (IsIdentPart(ch) || ch == '.') sb.Append(ch);
                else if (ch == ':') sb.Clear(); // a target: [method: Fact]
                else if (sb.Length > 0 && !char.IsWhiteSpace(ch)) expectName = false;
                else if (sb.Length > 0 && char.IsWhiteSpace(ch)) expectName = false;
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static int SkipMember(string code, int i)
    {
        var n = code.Length;
        if (i < n && code[i] == '<') i = MatchBracket(code, i, '<', '>') + 1;
        i = SkipWs(code, i);
        if (i < n && code[i] == '(') i = MatchBracket(code, i, '(', ')') + 1;
        // up to the body ({...}), an expression body (=> ...;) or a declaration (;)
        while (i < n)
        {
            if (code[i] == '{') return MatchBracket(code, i, '{', '}') + 1;
            if (code[i] == ';') return i + 1;
            if (code[i] == '}') return i; // malformed: let the caller pop
            i++;
        }
        return n;
    }

    private static int MatchBracket(string code, int open, char o, char c)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == o) depth++;
            else if (code[i] == c && --depth == 0) return i;
        }
        return code.Length - 1;
    }

    private static int SkipWs(string code, int i)
    {
        while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
        return i;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '@';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';

    private static bool IsKeyword(string w) => w is "if" or "for" or "foreach" or "while" or "switch" or "using" or "lock" or "return" or "new" or "typeof" or "nameof" or "sizeof" or "default" or "await";

    internal static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        return [.. starts];
    }

    internal static int LineOf(int[] starts, int offset)
    {
        var idx = Array.BinarySearch(starts, offset);
        return idx >= 0 ? idx : ~idx - 1;
    }

    /// <summary>The text with comments, strings (regular, verbatim, raw, interpolated) and char literals replaced by spaces; line breaks kept.</summary>
    internal static string Blank(string text)
    {
        var sb = new StringBuilder(text);
        var n = text.Length;
        var i = 0;
        void Fill(int from, int to) { for (var k = from; k < to && k < n; k++) if (sb[k] is not '\n' and not '\r') sb[k] = ' '; }
        while (i < n)
        {
            var c = text[i];
            if (c == '/' && i + 1 < n && text[i + 1] == '/') { var e = text.IndexOf('\n', i); e = e < 0 ? n : e; Fill(i, e); i = e; continue; }
            if (c == '/' && i + 1 < n && text[i + 1] == '*') { var e = text.IndexOf("*/", i + 2, StringComparison.Ordinal); e = e < 0 ? n : e + 2; Fill(i, e); i = e; continue; }
            if (c == '\'') { var e = i + 1; while (e < n && text[e] != '\'' && text[e] != '\n') { if (text[e] == '\\') e++; e++; } Fill(i, e + 1); i = e + 1; continue; }
            if (c == '"' || ((c == '@' || c == '$') && i + 1 < n && (text[i + 1] == '"' || ((text[i + 1] == '@' || text[i + 1] == '$') && i + 2 < n && text[i + 2] == '"'))))
            {
                var s = i;
                var verbatim = false;
                while (i < n && text[i] != '"') { if (text[i] == '@') verbatim = true; i++; }
                // raw string literal: three or more quotes
                var q = 0;
                while (i + q < n && text[i + q] == '"') q++;
                if (q >= 3)
                {
                    var close = new string('"', q);
                    var e = text.IndexOf(close, i + q, StringComparison.Ordinal);
                    e = e < 0 ? n : e + q;
                    Fill(s, e); i = e; continue;
                }
                i++; // opening quote
                while (i < n)
                {
                    if (verbatim && text[i] == '"' && i + 1 < n && text[i + 1] == '"') { i += 2; continue; }
                    if (!verbatim && text[i] == '\\') { i += 2; continue; }
                    if (text[i] == '"') { i++; break; }
                    if (!verbatim && text[i] == '\n') break;
                    i++;
                }
                Fill(s, i);
                continue;
            }
            i++;
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\bSkip\s*=\s*""((?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant)]
    private static partial Regex SkipArgument();
}
