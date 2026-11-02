using System.Text;
using System.Text.RegularExpressions;
using CsCode.Testing.Engine;

namespace CsCode.Testing.Providers.Cpp;

/// <summary>
/// Reads a C++ file's text for the tests it declares, for the editor's glyphs and lenses (discovery itself asks the test executables): GoogleTest's
/// <c>TEST</c>, <c>TEST_F</c>, <c>TEST_P</c>, <c>TYPED_TEST</c>, <c>TYPED_TEST_P</c> (named <c>Suite.Name</c>, as the runner names a plain test),
/// and the <c>TEST_CASE</c> family of Catch2 and doctest (named by their string literal; <c>SCENARIO</c> as <c>Scenario: name</c>; the
/// <c>CATCH_</c> / <c>DOCTEST_</c> prefixed forms too). Comments and preprocessor lines are blanked first (strings are kept: they carry the
/// names); lines are 0-based. <see cref="ScanGroups"/> finds the fixtures and suites that group them.
/// </summary>
public static partial class CppTestScanner
{
    public static IReadOnlyList<SourceTest> Scan(string text) => [.. ScanAll(text, out _, out _).Select(t => t.Test)];

    /// <summary>A scanned test with where its macro starts and the fixture class it runs in (null: none).</summary>
    private sealed record Scanned(SourceTest Test, int Index, string? Fixture);

    private static List<Scanned> ScanAll(string text, out string masked, out int[] starts)
    {
        var code = Blank(text, blankStrings: false);
        masked = Blank(text, blankStrings: true); // where the macros are looked for: not inside strings
        starts = DotNet.CSharpTestScanner.LineStarts(text);
        var result = new List<Scanned>();
        foreach (Match m in GoogleTestMacro().Matches(masked))
        {
            var name = m.Groups["suite"].Value + "." + m.Groups["name"].Value;
            var fixture = m.Groups["macro"].Value is "TEST" or "GTEST_TEST" ? null : m.Groups["suite"].Value; // TEST_F, TEST_P, TYPED_TEST…: the suite is the class
            result.Add(new Scanned(new SourceTest(name, LineOf(starts, m.Index), name), m.Index, fixture));
        }
        foreach (Match m in CaseMacro().Matches(masked))
        {
            var macro = m.Groups["macro"].Value;
            var i = m.Index + m.Length;
            string? fixture = null;
            if (macro.EndsWith("_METHOD", StringComparison.Ordinal) || macro.EndsWith("_FIXTURE", StringComparison.Ordinal))
            {
                // TEST_CASE_METHOD(Fixture, "name", …): the fixture comes first
                var comma = masked.IndexOf(',', i);
                if (comma < 0) continue;
                fixture = masked[i..comma].Trim();
                i = comma + 1;
            }
            if (ReadStringLiteral(code, ref i) is not { Length: > 0 } name) continue; // TEST_CASE() is anonymous: no name to match
            if (macro.EndsWith("SCENARIO", StringComparison.Ordinal)) name = "Scenario: " + name;
            result.Add(new Scanned(new SourceTest(name, LineOf(starts, m.Index), name), m.Index, fixture is { Length: > 0 } ? fixture : null));
        }
        result.Sort((a, b) => a.Index.CompareTo(b.Index));
        return result;
    }

    /// <summary>
    /// The groups a file declares, each with its tests in the file: a fixture class (GoogleTest's, deriving from <c>testing::Test</c>, for its
    /// <c>TEST_F</c> / <c>TEST_P</c> / <c>TYPED_TEST</c> tests; Catch2's <c>TEST_CASE_METHOD</c> and doctest's <c>TEST_CASE_FIXTURE</c> class), at
    /// the class's line; a doctest <c>TEST_SUITE("name") { … }</c> block, or <c>TEST_SUITE_BEGIN("name")</c> up to <c>TEST_SUITE_END</c>, at its
    /// line. A group without tests in the file is left out (a plain <c>TEST(Suite, …)</c> suite declares no class: no group).
    /// </summary>
    public static IReadOnlyList<SourceTestGroup> ScanGroups(string text)
    {
        var tests = ScanAll(text, out var masked, out var starts);
        if (tests.Count == 0) return [];
        var code = Blank(text, blankStrings: false);
        var result = new List<SourceTestGroup>();
        var classes = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in ClassDeclaration().Matches(masked))
        {
            var name = m.Groups["name"].Value;
            if (!classes.Add(name)) continue;
            var members = tests.Where(t => t.Fixture == name).Select(t => t.Test).ToList();
            if (members.Count > 0) result.Add(new SourceTestGroup(name, LineOf(starts, m.Index), members));
        }
        foreach (Match m in SuiteMacro().Matches(masked))
        {
            var i = m.Index + m.Length;
            if (ReadStringLiteral(code, ref i) is not { Length: > 0 } name) continue;
            int end;
            if (m.Groups["begin"].Success)
                end = masked.IndexOf("TEST_SUITE_END", i, StringComparison.Ordinal) is var e and >= 0 ? e : masked.Length;
            else
            {
                var open = masked.IndexOf('{', i);
                if (open < 0) continue;
                end = ClosingBrace(masked, open);
            }
            var members = tests.Where(t => t.Index > i && t.Index < end).Select(t => t.Test).ToList();
            if (members.Count > 0) result.Add(new SourceTestGroup(name, LineOf(starts, m.Index), members));
        }
        result.Sort((a, b) => a.Line.CompareTo(b.Line));
        return result;
    }

    /// <summary>The index of the brace that closes the one at <paramref name="open"/> (comments and strings are blanked), else the text's end.</summary>
    private static int ClosingBrace(string masked, int open)
    {
        var depth = 0;
        for (var k = open; k < masked.Length; k++)
        {
            if (masked[k] == '{') depth++;
            else if (masked[k] == '}' && --depth == 0) return k;
        }
        return masked.Length;
    }

    /// <summary>
    /// Whether a test the runner named <paramref name="runnerName"/> is the one <paramref name="sourceName"/> declares: the same name; a
    /// GoogleTest instance of it (<c>Inst/Suite.Name/2</c>, <c>Suite/0.Name</c> of <c>Suite.Name</c>); a Catch2 or doctest template instance
    /// (<c>name - int</c>, <c>name&lt;int&gt;</c>); doctest's scenario (<c>  Scenario: x</c>); or a test in a doctest suite (<c>suite::name</c>).
    /// </summary>
    public static bool NameMatches(string runnerName, string sourceName)
    {
        if (runnerName == sourceName || TestIds.EndsWithSegment(runnerName, sourceName)) return true;
        var trimmed = runnerName.TrimStart();
        if (trimmed == sourceName) return true;
        var suite = runnerName.IndexOf("::", StringComparison.Ordinal) is var sep and >= 0 ? runnerName[(sep + 2)..].TrimStart() : null;
        foreach (var candidate in suite is null ? [trimmed] : new[] { trimmed, suite })
            if (candidate.StartsWith(sourceName, StringComparison.Ordinal) && candidate.Length > sourceName.Length
                && (candidate.AsSpan(sourceName.Length).StartsWith(" - ") || candidate[sourceName.Length] == '<'))
                return true;
        return GoogleTestNames.Declared(runnerName) == sourceName;
    }

    private static int LineOf(int[] starts, int index)
    {
        var k = Array.BinarySearch(starts, index);
        return k >= 0 ? k : ~k - 1;
    }

    /// <summary>
    /// A string literal (or adjacent literals, concatenated) at <paramref name="i"/> after whitespace, with its escapes decoded; null when there is
    /// none. Advances <paramref name="i"/> past it.
    /// </summary>
    internal static string? ReadStringLiteral(string code, ref int i)
    {
        StringBuilder? sb = null;
        while (true)
        {
            var j = i;
            while (j < code.Length && char.IsWhiteSpace(code[j])) j++;
            // an encoding prefix (u8"…", L"…") is allowed; a raw string is left alone (rare in test names)
            while (j < code.Length && code[j] is 'u' or 'U' or 'L' or '8') j++;
            if (j >= code.Length || code[j] != '"') return sb?.ToString();
            sb ??= new StringBuilder();
            j++;
            while (j < code.Length && code[j] != '"' && code[j] != '\n')
            {
                if (code[j] == '\\' && j + 1 < code.Length)
                {
                    j++;
                    sb.Append(code[j] switch { 'n' => '\n', 't' => '\t', '0' => '\0', var c => c });
                }
                else sb.Append(code[j]);
                j++;
            }
            if (j >= code.Length || code[j] != '"') return sb.ToString();
            i = j + 1;
        }
    }

    /// <summary>
    /// Comments and preprocessor lines replaced by spaces (newlines kept, so offsets and lines stay); the contents of strings and chars too when
    /// <paramref name="blankStrings"/> (their quotes stay).
    /// </summary>
    internal static string Blank(string text, bool blankStrings)
    {
        var sb = new StringBuilder(text);
        var n = text.Length;
        var lineStart = true;
        for (var i = 0; i < n; i++)
        {
            var c = text[i];
            if (c == '\n') { lineStart = true; continue; }
            if (lineStart && c == '#')
            {
                // a preprocessor line (with its backslash continuations): #define TEST(…) must not look like a test
                while (i < n && text[i] != '\n')
                {
                    if (text[i] == '\\' && i + 1 < n && text[i + 1] == '\n') { sb[i] = ' '; i += 2; continue; }
                    if (text[i] == '/' && i + 1 < n && text[i + 1] == '*') break;
                    sb[i] = ' ';
                    i++;
                }
                i--;
                continue;
            }
            if (!char.IsWhiteSpace(c)) lineStart = false;
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                while (i < n && text[i] != '\n') { sb[i] = ' '; i++; }
                i--;
            }
            else if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? n : end + 2;
                for (var k = i; k < end; k++) if (text[k] != '\n') sb[k] = ' ';
                i = end - 1;
            }
            else if (c is '"' or '\'')
            {
                var end = c == '"' && i > 0 && text[i - 1] == 'R' && RawStringEnd(text, i) is var rawEnd and > 0 ? rawEnd : -1;
                if (end < 0)
                {
                    var k = i + 1;
                    while (k < n && text[k] != c && text[k] != '\n') k += text[k] == '\\' ? 2 : 1;
                    end = Math.Min(k, n - 1);
                }
                if (blankStrings) for (var k = i + 1; k < end; k++) if (text[k] != '\n') sb[k] = ' ';
                i = end;
            }
        }
        return sb.ToString();
    }

    /// <summary>The index of the closing quote of a raw string <c>R"delim(…)delim"</c> whose opening quote is at <paramref name="quote"/>, else -1.</summary>
    private static int RawStringEnd(string text, int quote)
    {
        var open = text.IndexOf('(', quote);
        if (open < 0 || open - quote > 17) return -1;
        var delimiter = ")" + text[(quote + 1)..open] + "\"";
        var end = text.IndexOf(delimiter, open, StringComparison.Ordinal);
        return end < 0 ? text.Length - 1 : end + delimiter.Length - 1;
    }

    [GeneratedRegex(@"(?<![\w])(?<macro>GTEST_TEST|TEST|TEST_F|TEST_P|TYPED_TEST|TYPED_TEST_P)\s*\(\s*(?<suite>[A-Za-z_]\w*)\s*,\s*(?<name>[A-Za-z_]\w*)\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleTestMacro();

    /// <summary>A class or struct definition (a base list or a body follows its name; a forward declaration has neither).</summary>
    [GeneratedRegex(@"(?<![\w])(?:class|struct)\s+(?<name>[A-Za-z_]\w*)\s*(?:final\s*)?[:{]", RegexOptions.CultureInvariant)]
    private static partial Regex ClassDeclaration();

    [GeneratedRegex(@"(?<![\w])(?:DOCTEST_)?TEST_SUITE(?<begin>_BEGIN)?\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex SuiteMacro();

    [GeneratedRegex(@"(?<![\w])(?<macro>(?:CATCH_|DOCTEST_)?(?:TEST_CASE|SCENARIO|TEST_CASE_METHOD|TEST_CASE_FIXTURE|TEMPLATE_TEST_CASE|TEMPLATE_TEST_CASE_SIG|TEMPLATE_PRODUCT_TEST_CASE|TEMPLATE_LIST_TEST_CASE|TEMPLATE_TEST_CASE_METHOD|TEST_CASE_TEMPLATE))\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CaseMacro();
}

/// <summary>GoogleTest's instance names, back to the declaration they come from.</summary>
public static class GoogleTestNames
{
    /// <summary>
    /// The <c>Suite.Name</c> a test's macro declares: <c>Numbers/Param.IsPositive/0</c> (a <c>TEST_P</c> instantiated as <c>Numbers</c>) and
    /// <c>Typed/1.DefaultIsZero</c> (a <c>TYPED_TEST</c>'s second type) are <c>Param.IsPositive</c> and <c>Typed.DefaultIsZero</c>.
    /// </summary>
    public static string Declared(string fullName)
    {
        var dot = fullName.IndexOf('.');
        if (dot <= 0) return fullName;
        var suiteParts = fullName[..dot].Split('/');
        var suite = suiteParts.LastOrDefault(p => p.Length > 0 && !p.All(char.IsAsciiDigit)) ?? suiteParts[0];
        var name = fullName[(dot + 1)..];
        var slash = name.IndexOf('/');
        return suite + "." + (slash > 0 ? name[..slash] : name);
    }
}
