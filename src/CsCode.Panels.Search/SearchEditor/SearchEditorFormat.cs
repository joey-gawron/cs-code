using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsCode.Panels.Search.Engine;

namespace CsCode.Panels.Search.SearchEditor;

/// <summary>
/// What a search editor searches for: its header (VS Code's <c>SearchConfiguration</c>, the lines <c># Query:</c>, <c># Flags:</c>,
/// <c># Including:</c>, <c># Excluding:</c>, <c># ContextLines:</c>).
/// </summary>
public sealed record SearchEditorConfig
{
    public string Query { get; init; } = "";
    public bool IsRegex { get; init; }
    public bool MatchCase { get; init; }
    public bool WholeWord { get; init; }
    public bool OnlyOpenEditors { get; init; }
    /// <summary>VS Code's single toggle for <c>files.exclude</c>/<c>search.exclude</c> and the ignore files (flag <c>IgnoreExcludeSettings</c> when off).</summary>
    public bool UseExcludeSettingsAndIgnoreFiles { get; init; } = true;
    public string Include { get; init; } = "";
    public string Exclude { get; init; } = "";
    /// <summary>Lines of context printed around every matching line (0 to <see cref="SearchEditorFormat.MaxContextLines"/>).</summary>
    public int ContextLines { get; init; }

    /// <summary>The engine query (the Find in Files window's engine and filters).</summary>
    public SearchQuery ToQuery() => new(Query, IsRegex, MatchCase, WholeWord, MultiLine: false,
        Include: Include, Exclude: Exclude, UseExcludeSettings: UseExcludeSettingsAndIgnoreFiles, UseIgnoreFiles: UseExcludeSettingsAndIgnoreFiles,
        OpenEditorsOnly: OnlyOpenEditors);

    /// <summary>The header of a search editor run from <paramref name="query"/> (a Find in Files query: the include/exclude globs and toggles carry over).</summary>
    public static SearchEditorConfig From(SearchQuery query, int contextLines) => new()
    {
        Query = query.Pattern,
        IsRegex = query.IsRegex,
        MatchCase = query.MatchCase,
        WholeWord = query.WholeWord,
        OnlyOpenEditors = query.OpenEditorsOnly,
        UseExcludeSettingsAndIgnoreFiles = query.UseExcludeSettings && query.UseIgnoreFiles,
        Include = query.Include ?? "",
        Exclude = query.Exclude ?? "",
        ContextLines = contextLines,
    };
}

/// <summary>The kind of one line of a search editor's results (VS Code's <c>search-result</c> extension's parse).</summary>
public enum SearchResultLineKind { Other, File, Match, Context }

/// <summary>
/// VS Code's <c>.code-search</c> format, which is also the search editor's text: the header, an empty line, then the results.
/// <code>
/// # Query: foo
/// # Flags: CaseSensitive WordMatch RegExp
/// # Including: src
/// # ContextLines: 1
///
/// 3 results - 2 files
///
/// src/a.cs:
///    9  context line
///   10: line with foo
///   11  context line
///
/// src/b.cs:
///   2: foo again
/// </code>
/// A path line is the file's path relative to the workspace folder (<c>folder • path</c> with several folders, absolute outside them); match lines
/// are <c>  N: text</c>, context lines <c>  N  text</c> (line numbers 1-based, right-aligned within a file). A line longer than
/// <see cref="MaxLineChars"/> is shown around its first match with <c>⟪ N characters skipped ⟫</c> for the rest, as VS Code clips its previews.
/// </summary>
public static class SearchEditorFormat
{
    public const string LanguageId = "search-result";
    public const string FileExtension = ".code-search";
    public const int MaxContextLines = 100;
    /// <summary>A result line longer than this is clipped around its match (VS Code's search editor previews keep 1000 characters).</summary>
    public const int MaxLineChars = 1000;
    /// <summary>Characters kept before a match when a long line is clipped.</summary>
    internal const int ClipLead = 100;
    public const string NoResults = "No Results";
    public const string LimitHitMessage = "The result set only contains a subset of all matches. Be more specific in your search to narrow down the results.";

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);
    /// <summary>VS Code's <c>FILE_LINE_REGEX</c>.</summary>
    public static readonly Regex FileLine = new(@"^(\S.*):$", RegexOptions.CultureInvariant, Timeout);
    /// <summary>VS Code's <c>RESULT_LINE_REGEX</c>: indentation, line number, separator (": " for a match, two spaces for context), text.</summary>
    public static readonly Regex ResultLine = new(@"^(\s+)(\d+)(: |  )(\s*)(.*)$", RegexOptions.CultureInvariant, Timeout);
    /// <summary>VS Code's <c>ELISION_REGEX</c>.</summary>
    public static readonly Regex Elision = new(@"⟪ ([0-9]+) characters skipped ⟫", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex HeaderLine = new(@"^#\s?([^:]+):\s?(.*)$", RegexOptions.CultureInvariant, Timeout);

    // ------------------------------------------------------------------------------------------------ header

    /// <summary>The header lines of <paramref name="config"/>, each ending with "\n" (no empty line after them).</summary>
    public static string SerializeHeader(SearchEditorConfig config)
    {
        var sb = new StringBuilder();
        sb.Append("# Query: ").Append(EscapeQuery(config.Query)).Append('\n');
        var flags = new List<string>(5);
        if (config.MatchCase) flags.Add("CaseSensitive");
        if (config.WholeWord) flags.Add("WordMatch");
        if (config.IsRegex) flags.Add("RegExp");
        if (config.OnlyOpenEditors) flags.Add("OpenEditors");
        if (!config.UseExcludeSettingsAndIgnoreFiles) flags.Add("IgnoreExcludeSettings");
        if (flags.Count > 0) sb.Append("# Flags: ").Append(string.Join(' ', flags)).Append('\n');
        if (config.Include.Length > 0) sb.Append("# Including: ").Append(OneLine(config.Include)).Append('\n');
        if (config.Exclude.Length > 0) sb.Append("# Excluding: ").Append(OneLine(config.Exclude)).Append('\n');
        if (config.ContextLines > 0) sb.Append("# ContextLines: ").Append(config.ContextLines.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Reads a header (VS Code's <c>extractSearchQueryFromLines</c>): <c># Key: value</c> lines; anything else is ignored. Lenient where VS Code
    /// throws: a backslash before anything but <c>n</c> or another backslash stays a backslash, so a query typed as <c>\d+</c> means <c>\d+</c>.
    /// </summary>
    public static SearchEditorConfig ParseHeader(IEnumerable<string> lines)
    {
        var config = new SearchEditorConfig();
        foreach (var line in lines)
        {
            Match m;
            try { m = HeaderLine.Match(line); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!m.Success) continue;
            var value = m.Groups[2].Value;
            switch (m.Groups[1].Value.Trim().ToLowerInvariant())
            {
                case "query":
                    config = config with { Query = UnescapeQuery(value) };
                    break;
                case "including":
                    config = config with { Include = value.Trim() };
                    break;
                case "excluding":
                    config = config with { Exclude = value.Trim() };
                    break;
                case "contextlines":
                    config = config with
                    {
                        ContextLines = int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 0, MaxContextLines) : 0,
                    };
                    break;
                case "flags":
                    config = config with
                    {
                        IsRegex = value.Contains("RegExp", StringComparison.Ordinal),
                        MatchCase = value.Contains("CaseSensitive", StringComparison.Ordinal),
                        WholeWord = value.Contains("WordMatch", StringComparison.Ordinal),
                        OnlyOpenEditors = value.Contains("OpenEditors", StringComparison.Ordinal),
                        UseExcludeSettingsAndIgnoreFiles = !value.Contains("IgnoreExcludeSettings", StringComparison.Ordinal),
                    };
                    break;
            }
        }
        return config;
    }

    /// <summary>Whether <paramref name="line"/> belongs to a header (a search editor's text starts with its header lines, all starting with '#').</summary>
    public static bool IsHeaderLine(string line) => line.StartsWith('#');

    /// <summary>
    /// The number of header lines at the top of a document: the leading lines that start with '#' (at most <paramref name="max"/> are looked at).
    /// <paramref name="lineAt"/> reads one line; <paramref name="lineCount"/> is the document's line count.
    /// </summary>
    public static int CountHeaderLines(Func<int, string> lineAt, int lineCount, int max = 32)
    {
        var n = 0;
        while (n < lineCount && n < max && IsHeaderLine(lineAt(n))) n++;
        return n;
    }

    /// <summary>VS Code's query escaping: a backslash doubles, a line break becomes <c>\n</c>.</summary>
    public static string EscapeQuery(string query) => query.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\n", StringComparison.Ordinal);

    public static string UnescapeQuery(string value)
    {
        if (!value.Contains('\\')) return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                if (next == 'n') { sb.Append('\n'); i++; continue; }
                if (next == '\\') { sb.Append('\\'); i++; continue; }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

    // ------------------------------------------------------------------------------------------- whole file

    /// <summary>
    /// A saved search editor: the header is everything up to the first empty line, the results everything after it (VS Code's
    /// <c>parseSerializedSearchEditor</c>).
    /// </summary>
    public static (SearchEditorConfig Config, string Results) Parse(string text)
    {
        var lines = text.Split('\n');
        var header = new List<string>();
        var i = 0;
        for (; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) { i++; break; }
            header.Add(line);
        }
        var body = i < lines.Length ? string.Join('\n', lines[i..].Select(l => l.TrimEnd('\r'))) : "";
        return (ParseHeader(header), body);
    }

    /// <summary>A whole search editor's text: header, empty line, results (what Save writes and <see cref="Parse"/> reads back).</summary>
    public static string Serialize(SearchEditorConfig config, string results) => SerializeHeader(config) + "\n" + results;

    // ------------------------------------------------------------------------------------------------ results

    /// <summary>The summary line under the header: "3 results - 2 files" (VS Code's wording), "No Results", with the limit message when truncated.</summary>
    public static string Summary(int results, int files, bool limitHit = false)
    {
        if (results == 0) return NoResults;
        var text = (results == 1 ? "1 result" : results.ToString(CultureInfo.InvariantCulture) + " results") + " - " +
                   (files == 1 ? "1 file" : files.ToString(CultureInfo.InvariantCulture) + " files");
        return limitHit ? text + " - " + LimitHitMessage : text;
    }

    /// <summary>A summary line never ends with ':' (it would read as a path line).</summary>
    internal static string SafeSummary(string text)
    {
        text = OneLine(text).TrimEnd();
        return text.EndsWith(':') ? text + "." : text;
    }

    /// <summary>"  12: text" (a match) or "  12  text" (context), the line number right-aligned to <paramref name="width"/> digits.</summary>
    public static string ResultLineText(int oneBasedLine, int width, bool isMatch, string text)
    {
        var number = oneBasedLine.ToString(CultureInfo.InvariantCulture);
        return "  " + new string(' ', Math.Max(0, width - number.Length)) + number + (isMatch ? ": " : "  ") + text;
    }

    /// <summary>
    /// A line's text as the results show it: whole when short, else <see cref="MaxLineChars"/> of it starting <see cref="ClipLead"/> characters
    /// before <paramref name="matchColumn"/> with <c>⟪ N characters skipped ⟫</c> standing for the rest (VS Code's elision).
    /// </summary>
    public static string ClipLine(string text, int? matchColumn)
    {
        if (text.Length <= MaxLineChars) return text;
        var start = Math.Clamp((matchColumn ?? 0) - ClipLead, 0, text.Length);
        var end = Math.Min(text.Length, start + MaxLineChars);
        var sb = new StringBuilder(MaxLineChars + 64);
        if (start > 0) sb.Append("⟪ ").Append(start.ToString(CultureInfo.InvariantCulture)).Append(" characters skipped ⟫");
        sb.Append(text, start, end - start);
        if (end < text.Length) sb.Append("⟪ ").Append((text.Length - end).ToString(CultureInfo.InvariantCulture)).Append(" characters skipped ⟫");
        return sb.ToString();
    }

    /// <summary>The kind of a results line (header lines are <see cref="SearchResultLineKind.Other"/>; callers skip the header themselves).</summary>
    public static SearchResultLineKind Classify(string line)
    {
        if (line.Length == 0) return SearchResultLineKind.Other;
        try
        {
            if (!char.IsWhiteSpace(line[0])) return line.EndsWith(':') && FileLine.IsMatch(line) ? SearchResultLineKind.File : SearchResultLineKind.Other;
            var m = ResultLine.Match(line);
            if (!m.Success) return SearchResultLineKind.Other;
            return m.Groups[3].Value == ": " ? SearchResultLineKind.Match : SearchResultLineKind.Context;
        }
        catch (RegexMatchTimeoutException)
        {
            return SearchResultLineKind.Other;
        }
    }
}
