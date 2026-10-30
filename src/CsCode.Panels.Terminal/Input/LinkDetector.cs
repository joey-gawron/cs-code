using System.Text;
using System.Text.RegularExpressions;
using CsCode.Terminal.Emulator;

namespace CsCode.Panels.Terminal.Input;

public enum LinkKind { Url, Hyperlink, File }

/// <summary>A link found in a row: cells [StartColumn, EndColumn] (inclusive), what it points at, and for files the 1-based line/column.</summary>
public sealed record TerminalLink(LinkKind Kind, int StartColumn, int EndColumn, string Target, int? Line = null, int? Column = null)
{
    public string Text { get; init; } = Target;
}

/// <summary>
/// Finds links in a row the way VS Code's terminal does: OSC 8 hyperlinks (the program's own), URLs (http, https, ftp, file, mailto) and file
/// paths with an optional position (<c>path:line</c>, <c>path:line:col</c>, MSBuild's <c>path(line,col)</c>) — absolute, <c>~/</c>,
/// <c>./</c>/<c>../</c>, relative with a directory, or a bare file name with an extension followed by a position. Whether a path exists is
/// decided later, off the UI thread, against the terminal's cwd (OSC 7) and the workspace folders.
/// </summary>
public static partial class LinkDetector
{
    [GeneratedRegex(@"\b(?:https?|ftp|file|mailto):(?://)?[^\s""'<>\[\]{}`|\\^]+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    // path, then optional :line[:col] or (line[,col])
    [GeneratedRegex(@"(?<path>(?:~|\.{1,2})?/[^\s:""'<>()\[\]{}`|,;]+|[A-Za-z0-9_.\-+@]+(?:/[^\s:""'<>()\[\]{}`|,;]+)+|[A-Za-z0-9_\-+@]+\.[A-Za-z0-9]+(?=[:(]\d))(?:(?::(?<line>\d+)(?::(?<col>\d+))?)|(?:\((?<line>\d+)(?:,\s*(?<col>\d+))?\)))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    /// <summary>The link covering <paramref name="column"/> in <paramref name="row"/>, or null.</summary>
    public static TerminalLink? At(TerminalRow row, int column, int columns)
    {
        // OSC 8 first: the program said exactly what the cells point at.
        var cell = row[column];
        if (cell.Attributes.LinkId > 0 && row.Link(cell.Attributes.LinkId) is { } uri)
        {
            var id = cell.Attributes.LinkId;
            var start = column;
            var end = column;
            while (start > 0 && row[start - 1].Attributes.LinkId == id) start--;
            while (end < columns - 1 && row[end + 1].Attributes.LinkId == id) end++;
            return new TerminalLink(LinkKind.Hyperlink, start, end, uri);
        }
        return All(row, columns).FirstOrDefault(l => column >= l.StartColumn && column <= l.EndColumn);
    }

    /// <summary>Every URL and path candidate in the row (OSC 8 links excluded).</summary>
    public static IReadOnlyList<TerminalLink> All(TerminalRow row, int columns)
    {
        var (text, map) = TextWithColumns(row, columns);
        var links = new List<TerminalLink>();
        foreach (Match m in UrlPattern().Matches(text))
        {
            var value = TrimUrl(m.Value);
            if (value.Length < 4) continue;
            links.Add(new TerminalLink(LinkKind.Url, map[m.Index], map[m.Index + value.Length - 1], value));
        }
        foreach (Match m in PathPattern().Matches(text))
        {
            var path = m.Groups["path"].Value;
            if (path.Length < 2 || links.Any(l => map[m.Index] >= l.StartColumn && map[m.Index] <= l.EndColumn)) continue;
            if (path.EndsWith('.')) path = path.TrimEnd('.');
            int? line = m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : null;
            int? col = m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : null;
            var length = line is null ? path.Length : m.Length;
            links.Add(new TerminalLink(LinkKind.File, map[m.Index], map[m.Index + length - 1], path, line, col) { Text = m.Value[..length] });
        }
        return links;
    }

    private static string TrimUrl(string url)
    {
        // trailing punctuation belongs to the sentence, a closing parenthesis only when it has no opening one in the URL
        var end = url.Length;
        while (end > 0 && ".,;:!?'\")".Contains(url[end - 1]))
        {
            if (url[end - 1] == ')' && url[..end].Count(c => c == '(') >= url[..end].Count(c => c == ')')) break;
            end--;
        }
        return url[..end];
    }

    /// <summary>The row's text and, for each character, the column it came from (wide characters and clusters map to their cell).</summary>
    public static (string Text, int[] Columns) TextWithColumns(TerminalRow row, int columns)
    {
        var sb = new StringBuilder(columns);
        var map = new List<int>(columns);
        for (var c = 0; c < columns; c++)
        {
            var cell = row[c];
            if (cell.IsSpacer) continue;
            var t = row.CellText(c);
            sb.Append(t);
            for (var i = 0; i < t.Length; i++) map.Add(c);
        }
        return (sb.ToString(), map.ToArray());
    }
}
