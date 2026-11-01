using System.Text;

namespace CsCode.Testing.Providers.JavaScript;

/// <summary>
/// Finds the tests a Jest or Vitest file declares from its text: <c>describe</c>/<c>suite</c> blocks and <c>it</c>/<c>test</c> calls (also
/// <c>.only</c>, <c>.skip</c>, <c>.concurrent</c>, <c>.todo</c>, <c>fit</c>, <c>xit</c>, <c>xdescribe</c>…) whose name is a string literal, nested by the
/// braces of their callbacks. A test's name is its describe names and its own joined with <c> &gt; </c>, as the runners' <c>ancestorTitles</c> and
/// <c>title</c> are. Template literals with substitutions and <c>.each</c> tables are left to the run (their names are only known then).
/// </summary>
public static class JsTestScanner
{
    public sealed record Found(string Name, IReadOnlyList<string> Describes, string Title, int Line, bool Skipped);

    private static readonly HashSet<string> Suites = ["describe", "suite", "fdescribe", "xdescribe", "context"];
    private static readonly HashSet<string> Tests = ["it", "test", "fit", "xit", "xtest", "specify"];

    public static IReadOnlyList<Found> Scan(string text)
    {
        var code = BlankComments(text);
        var starts = DotNet.CSharpTestScanner.LineStarts(text);
        var result = new List<Found>();
        var stack = new List<(string Name, int Depth)>();
        var depth = 0;
        string? pendingSuite = null;
        var n = code.Length;
        var i = 0;
        while (i < n)
        {
            var c = code[i];
            if (c is '"' or '\'' or '`') { i = SkipString(code, i); continue; }
            if (c == '{') { depth++; if (pendingSuite is not null) { stack.Add((pendingSuite, depth)); pendingSuite = null; } i++; continue; }
            if (c == '}') { if (stack.Count > 0 && stack[^1].Depth == depth) stack.RemoveAt(stack.Count - 1); depth--; i++; continue; }
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                var s = i;
                while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] is '_' or '$')) i++;
                if (s > 0 && code[s - 1] == '.') continue; // a member (foo.it(...)), not the global
                var word = code[s..i];
                var isSuite = Suites.Contains(word);
                if (!isSuite && !Tests.Contains(word)) continue;
                var skipped = word.StartsWith('x');
                // modifiers: .only .skip .concurrent .todo .failing (.each is skipped: its names come from the table)
                var j = i;
                var each = false;
                while (j < n && code[j] == '.')
                {
                    var ms = ++j;
                    while (j < n && char.IsLetter(code[j])) j++;
                    var mod = code[ms..j];
                    if (mod is "each" or "for") each = true;
                    if (mod is "skip" or "todo") skipped = true;
                }
                j = SkipWs(code, j);
                if (each || j >= n || code[j] != '(') { i = j; continue; }
                j = SkipWs(code, j + 1);
                if (j >= n || code[j] is not ('"' or '\'' or '`')) { i = j; continue; }
                var end = SkipString(code, j);
                var literal = text[(j + 1)..Math.Max(j + 1, end - 1)];
                if (code[j] == '`' && literal.Contains("${", StringComparison.Ordinal)) { i = end; continue; }
                var title = Unescape(literal);
                if (isSuite) pendingSuite = title;
                else
                {
                    var describes = stack.Select(x => x.Name).ToList();
                    result.Add(new Found(string.Join(" > ", [.. describes, title]), describes, title, DotNet.CSharpTestScanner.LineOf(starts, s), skipped));
                }
                i = end;
                continue;
            }
            i++;
        }
        return result;
    }

    private static int SkipWs(string code, int i)
    {
        while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
        return i;
    }

    private static int SkipString(string code, int i)
    {
        var q = code[i++];
        while (i < code.Length && code[i] != q)
        {
            if (code[i] == '\\') i++;
            else if (q != '`' && code[i] == '\n') break;
            i++;
        }
        return Math.Min(code.Length, i + 1);
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Line and block comments blanked (strings, which hold the names, are kept; a // inside a string is not a comment).</summary>
    internal static string BlankComments(string text)
    {
        var sb = new StringBuilder(text);
        var n = text.Length;
        var i = 0;
        while (i < n)
        {
            var c = text[i];
            if (c is '"' or '\'' or '`') { i = SkipString(text, i); continue; }
            if (c == '/' && i + 1 < n && (text[i + 1] == '/' || text[i + 1] == '*'))
            {
                var block = text[i + 1] == '*';
                var e = block ? text.IndexOf("*/", i + 2, StringComparison.Ordinal) : text.IndexOf('\n', i);
                e = e < 0 ? n : block ? e + 2 : e;
                for (var k = i; k < e; k++) if (sb[k] is not '\n' and not '\r') sb[k] = ' ';
                i = e;
                continue;
            }
            i++;
        }
        return sb.ToString();
    }
}
