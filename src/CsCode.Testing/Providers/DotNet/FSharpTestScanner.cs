using System.Text.RegularExpressions;

namespace CsCode.Testing.Providers.DotNet;

/// <summary>
/// Finds the test functions an F# file declares, for the editor's glyphs and lenses and for the file and line of a test that discovery got
/// from the runner's listing (which has no locations for xUnit): a test attribute (<c>[&lt;Fact&gt;]</c>, <c>[&lt;Theory&gt;]</c>, any
/// <c>*Fact</c>/<c>*Theory</c>, <c>[&lt;Test&gt;]</c>, <c>[&lt;TestCase(…)&gt;]</c>, <c>[&lt;TestCaseSource(…)&gt;]</c>, <c>[&lt;TestMethod&gt;]</c>,
/// <c>[&lt;DataTestMethod&gt;]</c>), alone or among others in the brackets, then the next <c>let</c> or <c>member</c> on that line or below:
/// its name (double-backtick names keep their spaces, as the compiler names the method) and 0-based line. Line comments and block comments
/// are skipped; strings are not parsed (a test attribute written inside a string would count). Which module or type holds the test is left
/// to the listing: <see cref="DotNetTestProvider"/> matches a listed test to a scanned one by method name within the project's files.
/// </summary>
public static partial class FSharpTestScanner
{
    public sealed record Found(string Method, int Line);

    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod",
    };

    public static IReadOnlyList<Found> Scan(string text)
    {
        var found = new List<Found>();
        var lines = text.Split('\n');
        var pending = false;
        var inBlockComment = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComments(lines[i].TrimEnd('\r'), ref inBlockComment);
            if (line.Length == 0) continue;
            var rest = line;
            foreach (Match attr in AttributeBlock().Matches(line))
            {
                if (attr.Groups["body"].Value.Split(';').Any(IsTestAttribute)) pending = true;
                rest = line[(attr.Index + attr.Length)..];
            }
            if (!pending) continue;
            var decl = Declaration().Match(rest);
            if (decl.Success)
            {
                var name = decl.Groups["name"].Value;
                found.Add(new Found(name.StartsWith("``", StringComparison.Ordinal) ? name[2..^2] : name, i));
                pending = false;
            }
            else if (rest.Trim().Length > 0 && !rest.TrimStart().StartsWith("[<", StringComparison.Ordinal)) pending = false; // something else came between
        }
        return found;
    }

    private static bool IsTestAttribute(string item)
    {
        var name = item.Trim();
        var paren = name.IndexOf('(');
        if (paren >= 0) name = name[..paren].Trim();
        var dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        if (name.EndsWith("Attribute", StringComparison.Ordinal)) name = name[..^"Attribute".Length];
        return TestAttributes.Contains(name) || name.EndsWith("Fact", StringComparison.Ordinal) || name.EndsWith("Theory", StringComparison.Ordinal);
    }

    private static string StripComments(string line, ref bool inBlock)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            if (inBlock)
            {
                if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == ')') { inBlock = false; i++; }
                continue;
            }
            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            // "(*)" is the multiplication operator, not a comment
            if (line[i] == '(' && i + 1 < line.Length && line[i + 1] == '*' && !(i + 2 < line.Length && line[i + 2] == ')')) { inBlock = true; i++; continue; }
            sb.Append(line[i]);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\[<(?<body>.*?)>\]", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeBlock();

    [GeneratedRegex(@"^\s*(?:let|member|static\s+member|override|default)\s+(?:(?:private|public|internal|inline|rec|mutable)\s+)*(?:[A-Za-z_][\w']*\s*\.\s*)?(?<name>``[^`]+``|[A-Za-z_][\w']*)", RegexOptions.CultureInvariant)]
    private static partial Regex Declaration();
}
