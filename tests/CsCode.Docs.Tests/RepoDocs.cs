using System.Text.RegularExpressions;

namespace CsCode.Docs.Tests;

/// <summary>The repository's files (found by walking up to CsCode.slnx from the test assembly) and the Markdown tables of its docs.</summary>
internal static partial class RepoDocs
{
    public static string Root { get; } = FindRoot();

    public static string PathOf(string relative) => Path.Combine([Root, .. relative.Split('/')]);

    public static string Read(string relative) => File.ReadAllText(PathOf(relative));

    /// <summary>
    /// The body rows of the first table in <paramref name="markdown"/> whose header row starts with <paramref name="headerStart"/> (e.g.
    /// <c>"| Action | Command |"</c>), each as its cells: trimmed, a cell that is one code span without its backticks, Markdown's backslash
    /// escapes (<c>\`</c>, <c>\\</c>) removed elsewhere.
    /// </summary>
    public static IReadOnlyList<string[]> Table(string markdown, string headerStart)
    {
        var lines = markdown.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var header = Array.FindIndex(lines, l => l.StartsWith(headerStart, StringComparison.Ordinal));
        if (header < 0) throw new InvalidOperationException($"No table whose header starts with '{headerStart}'.");
        var rows = new List<string[]>();
        for (var i = header + 2; i < lines.Length && lines[i].StartsWith('|'); i++)
            rows.Add([.. SplitRow(lines[i]).Select(Cell)]);
        return rows;
    }

    /// <summary>The text of the first fenced code block (```lang) after the line containing <paramref name="marker"/>.</summary>
    public static string CodeBlockAfter(string markdown, string marker, string language)
    {
        var at = markdown.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException($"'{marker}' not found.");
        var open = markdown.IndexOf("```" + language + "\n", at, StringComparison.Ordinal);
        if (open < 0) throw new InvalidOperationException($"No ```{language} block after '{marker}'.");
        var start = open + 3 + language.Length + 1;
        return markdown[start..markdown.IndexOf("\n```", start, StringComparison.Ordinal)];
    }

    /// <summary>A key cell's alternatives ("F5", "Ctrl+Q or F1"); "—" is none.</summary>
    public static IReadOnlySet<string> Keys(string cell) =>
        cell is "—" ? new HashSet<string>() : cell.Split(" or ", StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> SplitRow(string row)
    {
        // Split on '|' outside code spans and not escaped.
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var inCode = false;
        for (var i = 1; i < row.Length; i++)
        {
            var c = row[i];
            if (c == '\\' && !inCode && i + 1 < row.Length) { current.Append(c).Append(row[++i]); continue; }
            if (c == '`') inCode = !inCode;
            if (c == '|' && !inCode) { cells.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        return cells;
    }

    private static string Cell(string raw)
    {
        var s = raw.Trim();
        if (CodeSpan().Match(s) is { Success: true } m) return m.Groups[1].Value;
        return Escape().Replace(s, "$1");
    }

    [GeneratedRegex(@"^`([^`]+)`$")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"\\(.)")]
    private static partial Regex Escape();

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CsCode.slnx"))) return d.FullName;
        throw new InvalidOperationException("CsCode.slnx not found above " + AppContext.BaseDirectory);
    }
}
