using System.Text.RegularExpressions;

namespace CsCode.ProjectTemplates.Generators;

/// <summary>A project template of the installed SDK, as <c>dotnet new list</c> prints it (the first of its short names).</summary>
public sealed record DotnetTemplate(string Name, string ShortName, string Languages, string Tags)
{
    public bool IsTestProject => Tags.Split('/').Any(t => t.Equals("Test", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Parsers of the .NET SDK's CLI output (checked with SDK 10.0.403): <c>dotnet new list</c>'s table and the template options of
/// <c>dotnet new &lt;template&gt; --help</c>. CsCode owns this listing (the New Project page); nothing else in CsCode parses it.
/// </summary>
public static partial class DotnetTemplates
{
    /// <summary>The templates shown first, in this order, when the SDK has them (the rest follow by name).</summary>
    public static readonly IReadOnlyList<string> Curated = ["console", "classlib", "webapi", "xunit"];

    /// <summary>
    /// The rows of <c>dotnet new list</c>: a header line, a line of dash runs (one per column, two spaces apart) that gives the columns' widths,
    /// then one row per template. A column's text may be wider than its dashes only in the last column. Null when no table is found.
    /// </summary>
    public static IReadOnlyList<DotnetTemplate>? ParseList(string output)
    {
        var lines = output.ReplaceLineEndings("\n").Split('\n');
        var dashes = Array.FindIndex(lines, l => l.Trim().Length > 0 && l.Trim().All(c => c is '-' or ' ') && l.Contains("--", StringComparison.Ordinal));
        if (dashes < 1) return null;
        var columns = DashRuns().Matches(lines[dashes]).Select(m => m.Index).ToArray();
        if (columns.Length < 2) return null;
        string Cell(string line, int i)
        {
            var start = columns[i];
            if (start >= line.Length) return "";
            var end = i + 1 < columns.Length ? Math.Min(columns[i + 1], line.Length) : line.Length;
            return line[start..end].Trim();
        }
        var templates = new List<DotnetTemplate>();
        foreach (var line in lines.Skip(dashes + 1))
        {
            if (line.Trim().Length == 0) continue;
            var name = Cell(line, 0);
            var shortName = Cell(line, 1).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (name.Length == 0 || shortName is null) continue;
            templates.Add(new DotnetTemplate(name, shortName, columns.Length > 2 ? Cell(line, 2) : "", columns.Length > 3 ? Cell(line, 3) : ""));
        }
        return templates;
    }

    /// <summary>The curated templates first (in <see cref="Curated"/>'s order), then the others by name; duplicates of a short name dropped.</summary>
    public static IReadOnlyList<DotnetTemplate> Order(IEnumerable<DotnetTemplate> templates)
    {
        var unique = templates.GroupBy(t => t.ShortName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var curated = Curated.Select(c => unique.FirstOrDefault(t => t.ShortName.Equals(c, StringComparison.OrdinalIgnoreCase))).OfType<DotnetTemplate>();
        var rest = unique.Where(t => !Curated.Contains(t.ShortName, StringComparer.OrdinalIgnoreCase)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);
        return [.. curated, .. rest];
    }

    /// <summary>
    /// The values of a choice option in <c>dotnet new &lt;template&gt; --help</c> (<c>-f, --framework &lt;net10.0&gt;</c> followed by
    /// <c>Type: choice</c>, one indented <c>value  description</c> line per choice, then <c>Default: value</c>) and its default; null when the
    /// template has no such option.
    /// </summary>
    public static (IReadOnlyList<string> Values, string? Default)? ParseChoices(string help, string option)
    {
        var lines = help.ReplaceLineEndings("\n").Split('\n');
        var at = Array.FindIndex(lines, l => Regex.IsMatch(l, @"(^|[\s,])" + Regex.Escape(option) + @"(\s|$)"));
        if (at < 0) return null;
        var values = new List<string>();
        string? @default = null;
        var inChoices = false;
        for (var i = at + 1; i < lines.Length; i++)
        {
            var text = lines[i].Trim();
            if (text.StartsWith('-')) break; // the next option
            if (text.StartsWith("Type:", StringComparison.Ordinal)) { inChoices = text.Contains("choice", StringComparison.Ordinal); continue; }
            if (text.StartsWith("Default:", StringComparison.Ordinal)) { @default = text["Default:".Length..].Trim(); break; }
            if (inChoices && ChoiceLine().Match(text) is { Success: true } m) values.Add(m.Groups[1].Value);
        }
        return (values, @default ?? values.FirstOrDefault());
    }

    /// <summary>Whether <c>--help</c> output lists <paramref name="option"/> (a template without <c>--framework</c> must not be given one).</summary>
    public static bool HasOption(string help, string option) => ParseChoices(help, option) is not null;

    [GeneratedRegex("-+")]
    private static partial Regex DashRuns();

    [GeneratedRegex(@"^(\S+)(\s{2,}.*)?$")]
    private static partial Regex ChoiceLine();
}
