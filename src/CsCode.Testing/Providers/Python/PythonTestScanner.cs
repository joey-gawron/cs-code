using System.Text.RegularExpressions;

namespace CsCode.Testing.Providers.Python;

/// <summary>
/// Finds the tests of a Python file from its text, for the gutter glyphs and code lenses before (and between) discoveries, as pytest and unittest
/// would collect them by default: module-level <c>def test*</c> / <c>async def test*</c>, and the <c>test*</c> methods of a class named <c>Test*</c>
/// or deriving from a <c>TestCase</c>. A name is <c>Class::method</c> or <c>function</c> (the provider also matches it against unittest's dotted ids).
/// Indentation decides what belongs to a class; strings and comments are not parsed (a <c>def test</c> inside a docstring would count, as it rarely
/// matters for markers).
/// </summary>
public static partial class PythonTestScanner
{
    public sealed record Found(string Name, int Line, string Label);

    [GeneratedRegex(@"^(?<indent>[ \t]*)class\s+(?<name>[A-Za-z_]\w*)\s*(?:\((?<bases>[^)]*)\))?\s*:")]
    private static partial Regex Class();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?:async\s+)?def\s+(?<name>test\w*)\s*\(")]
    private static partial Regex TestFunction();

    public static IReadOnlyList<Found> Scan(string text)
    {
        var result = new List<Found>();
        var classes = new Stack<(int Indent, string Name, bool IsTestClass)>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            var indent = Indent(line);
            while (classes.Count > 0 && indent <= classes.Peek().Indent) classes.Pop();
            if (Class().Match(line) is { Success: true } c)
            {
                var name = c.Groups["name"].Value;
                var bases = c.Groups["bases"].Value;
                classes.Push((indent, name, name.StartsWith("Test", StringComparison.Ordinal) || bases.Contains("TestCase", StringComparison.Ordinal)));
                continue;
            }
            if (TestFunction().Match(line) is not { Success: true } f) continue;
            var fn = f.Groups["name"].Value;
            if (classes.Count == 0)
            {
                if (indent == 0) result.Add(new Found(fn, i, fn));
            }
            else if (classes.Peek() is { IsTestClass: true } owner && classes.Count == 1)
                result.Add(new Found(owner.Name + "::" + fn, i, fn));
        }
        return result;
    }

    private static int Indent(string line)
    {
        var n = 0;
        foreach (var ch in line)
        {
            if (ch == ' ') n++;
            else if (ch == '\t') n += 8 - n % 8;
            else break;
        }
        return n;
    }
}
