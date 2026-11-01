using System.Text;

namespace CsCode.Testing.Providers.Rust;

/// <summary>
/// Reads a Rust file's text for what test discovery and the editor's glyphs need: the <c>#[test]</c> functions (also <c>#[tokio::test]</c> and any
/// attribute path ending in <c>::test</c>) with their module path inside the file (<c>tests::nested::deep</c>) and line, whether each is
/// <c>#[ignore]</c>d, the inline modules (<c>mod tests { … }</c>) and the out-of-line ones (<c>mod parser;</c>). Comments, strings, raw strings and
/// char literals are blanked first; lifetimes are told from char literals.
/// </summary>
public static class RustTestScanner
{
    public sealed record TestFn(string Path, string Name, int Line, bool Ignored);

    public sealed record Module(string Path, int Line, bool Inline);

    public sealed record Result(IReadOnlyList<TestFn> Tests, IReadOnlyList<Module> Modules);

    public static Result Scan(string text)
    {
        var code = Blank(text);
        var starts = Providers.DotNet.CSharpTestScanner.LineStarts(text);
        var tests = new List<TestFn>();
        var modules = new List<Module>();
        var stack = new List<(string? Module, int Depth)>(); // inline modules with the brace depth their body opened at
        var depth = 0;
        var pendingTest = false; var pendingIgnore = false;
        string? pendingModule = null; var pendingModuleLine = 0;
        var n = code.Length;
        var i = 0;
        string ModPath() => string.Join("::", stack.Select(s => s.Module));
        while (i < n)
        {
            var c = code[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '#' && i + 1 < n && (code[i + 1] == '[' || (code[i + 1] == '!' && i + 2 < n && code[i + 2] == '[')))
            {
                var open = code.IndexOf('[', i);
                var close = Match(code, open, '[', ']');
                var attr = code[(open + 1)..close].Trim();
                var name = attr;
                var cut = name.IndexOfAny(['(', '=', ' ']);
                if (cut >= 0) name = name[..cut];
                if (name == "test" || name.EndsWith("::test", StringComparison.Ordinal)) pendingTest = true;
                if (name == "ignore") pendingIgnore = true;
                i = close + 1;
                continue;
            }
            if (c == '{')
            {
                depth++;
                if (pendingModule is not null)
                {
                    stack.Add((pendingModule, depth));
                    modules.Add(new Module(ModPath(), pendingModuleLine, true));
                    pendingModule = null;
                }
                pendingTest = pendingIgnore = false;
                i++;
                continue;
            }
            if (c == '}')
            {
                if (stack.Count > 0 && stack[^1].Depth == depth) stack.RemoveAt(stack.Count - 1);
                depth--;
                pendingTest = pendingIgnore = false;
                i++;
                continue;
            }
            if (c == ';')
            {
                if (pendingModule is not null)
                {
                    var path = ModPath();
                    modules.Add(new Module(path.Length > 0 ? path + "::" + pendingModule : pendingModule, pendingModuleLine, false));
                    pendingModule = null;
                }
                pendingTest = pendingIgnore = false;
                i++;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var s = i;
                while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i++;
                var word = code[s..i];
                if (word == "mod")
                {
                    var j = SkipWs(code, i);
                    var ns = j;
                    while (j < n && (char.IsLetterOrDigit(code[j]) || code[j] == '_')) j++;
                    if (j > ns)
                    {
                        pendingModule = code[ns..j];
                        pendingModuleLine = Providers.DotNet.CSharpTestScanner.LineOf(starts, ns);
                    }
                    i = j;
                    continue;
                }
                if (word == "fn")
                {
                    var j = SkipWs(code, i);
                    var ns = j;
                    while (j < n && (char.IsLetterOrDigit(code[j]) || code[j] == '_')) j++;
                    if (pendingTest && j > ns)
                    {
                        var fname = code[ns..j];
                        var mp = ModPath();
                        tests.Add(new TestFn(mp.Length > 0 ? mp + "::" + fname : fname, fname, Providers.DotNet.CSharpTestScanner.LineOf(starts, ns), pendingIgnore));
                    }
                    pendingTest = pendingIgnore = false;
                    i = j;
                    continue;
                }
                continue;
            }
            i++;
        }
        return new Result(tests, modules);
    }

    private static int SkipWs(string code, int i)
    {
        while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
        return i;
    }

    private static int Match(string code, int open, char o, char c)
    {
        var d = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == o) d++;
            else if (code[i] == c && --d == 0) return i;
        }
        return code.Length - 1;
    }

    /// <summary>Comments (nested block comments too), strings, byte strings, raw strings and char literals replaced by spaces; line breaks kept.</summary>
    internal static string Blank(string text)
    {
        var sb = new StringBuilder(text);
        var n = text.Length;
        void Fill(int from, int to) { for (var k = from; k < to && k < n; k++) if (sb[k] is not '\n' and not '\r') sb[k] = ' '; }
        var i = 0;
        while (i < n)
        {
            var c = text[i];
            if (c == '/' && i + 1 < n && text[i + 1] == '/') { var e = text.IndexOf('\n', i); e = e < 0 ? n : e; Fill(i, e); i = e; continue; }
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                var s = i; var d = 0;
                while (i < n)
                {
                    if (i + 1 < n && text[i] == '/' && text[i + 1] == '*') { d++; i += 2; continue; }
                    if (i + 1 < n && text[i] == '*' && text[i + 1] == '/') { d--; i += 2; if (d == 0) break; continue; }
                    i++;
                }
                Fill(s, i);
                continue;
            }
            if ((c == 'r' || (c == 'b' && i + 1 < n && text[i + 1] == 'r')) && (i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')))
            {
                var j = c == 'b' ? i + 2 : i + 1;
                var hashes = 0;
                while (j < n && text[j] == '#') { hashes++; j++; }
                if (j < n && text[j] == '"')
                {
                    var close = "\"" + new string('#', hashes);
                    var e = text.IndexOf(close, j + 1, StringComparison.Ordinal);
                    e = e < 0 ? n : e + close.Length;
                    Fill(i, e); i = e; continue;
                }
            }
            if (c == '"')
            {
                var s = i++;
                while (i < n && text[i] != '"') { if (text[i] == '\\') i++; i++; }
                Fill(s, i + 1); i++; continue;
            }
            if (c == '\'')
            {
                // a char literal ('a', '\n', '\u{1F600}') or a lifetime ('a, 'static): only a literal has a closing quote right after its one character
                if (i + 2 < n && text[i + 1] == '\\')
                {
                    var e = text.IndexOf('\'', i + 2);
                    if (e > 0 && e - i <= 12) { Fill(i, e + 1); i = e + 1; continue; }
                }
                else if (i + 2 < n && text[i + 2] == '\'') { Fill(i, i + 3); i += 3; continue; }
                else if (i + 3 < n && char.IsSurrogate(text[i + 1]) && text[i + 3] == '\'') { Fill(i, i + 4); i += 4; continue; }
            }
            i++;
        }
        return sb.ToString();
    }
}
