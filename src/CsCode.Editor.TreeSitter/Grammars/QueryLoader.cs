using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.Editor.TreeSitter.Grammars;

/// <summary>
/// Reads a query kind (<c>highlights</c>, <c>injections</c>) for a language and resolves Neovim's <c>; inherits: a,b,(c)</c> modeline:
/// inherited queries come FIRST (their patterns have lower precedence, later patterns win), recursively, except a parenthesised name,
/// whose own <c>inherits</c> are not followed. The result is one source text compiled against the inheriting language (e.g. TypeScript's
/// highlights = ecma's + typescript's, compiled with the TypeScript grammar).
/// </summary>
public static partial class QueryLoader
{
    [GeneratedRegex(@"^\s*;+\s*inherits\s*:?\s*(?<list>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex InheritsLine();

    /// <summary>The composed query text, or null when <paramref name="name"/> has no such query. <paramref name="files"/> lists the files used, in order.</summary>
    public static string? Load(GrammarLocator locator, string name, string kind, out IReadOnlyList<string> files)
    {
        var used = new List<string>();
        var sb = new StringBuilder();
        var found = Append(locator, name, kind, followInherits: true, sb, used, new HashSet<string>(StringComparer.Ordinal), depth: 0);
        files = used;
        return found ? sb.ToString() : null;
    }

    private static bool Append(GrammarLocator locator, string name, string kind, bool followInherits, StringBuilder sb, List<string> used, HashSet<string> visited, int depth)
    {
        if (depth > 8 || !visited.Add(name)) return false;
        string? path = null;
        foreach (var dir in locator.QueryDirectories(name))
        {
            var candidate = Path.Combine(dir, kind + ".scm");
            if (File.Exists(candidate))
            {
                path = candidate;
                break;
            }
        }
        if (path is null) return false;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (followInherits)
        {
            foreach (var line in text.Split('\n').Take(8)) // the modeline sits in the first lines
            {
                var m = InheritsLine().Match(line);
                if (!m.Success) continue;
                foreach (var raw in m.Groups["list"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var optional = raw.StartsWith('(') && raw.EndsWith(')');
                    var inherited = optional ? raw[1..^1] : raw;
                    Append(locator, inherited, kind, followInherits: !optional, sb, used, visited, depth + 1);
                }
            }
        }
        used.Add(path);
        sb.Append("; ---- ").Append(name).Append('/').Append(kind).Append(".scm\n");
        sb.Append(text);
        if (!text.EndsWith('\n')) sb.Append('\n');
        return true;
    }
}
