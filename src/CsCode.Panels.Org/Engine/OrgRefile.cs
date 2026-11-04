using System.Globalization;
using System.Text.RegularExpressions;

namespace CsCode.Panels.Org.Engine;

/// <summary>A place an entry can be refiled to: under a heading of a document, or at the top level of the document (<see cref="Heading"/> null).</summary>
public sealed record OrgRefileTarget(OrgDocument Document, OrgHeading? Heading)
{
    /// <summary>What the picker shows: <c>notes.org</c>, <c>notes.org / Projects / Website</c>.</summary>
    public string Label => Heading is null ? Document.FileName : string.Join(" / ", [Document.FileName, .. Heading.Ancestors(), Heading.Title]);
}

/// <summary>
/// Moving subtrees: org's refile (C-c C-w: the entry and its subheadings become the last child of another heading, in this file or another,
/// their levels adjusted) and archive (C-c C-x C-s: the subtree moves to the archive file, by default <c>notes.org_archive</c> next to it,
/// with <c>ARCHIVE_TIME</c>, <c>ARCHIVE_FILE</c>, <c>ARCHIVE_OLPATH</c>, <c>ARCHIVE_CATEGORY</c> and <c>ARCHIVE_TODO</c> properties).
/// </summary>
public static class OrgRefile
{
    private static readonly Regex HeadingRe = new(@"^(\*+)([ \t].*|)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BlockStartRe = new(@"^[ \t]*#\+begin_", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BlockEndRe = new(@"^[ \t]*#\+end_", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The headings down to <paramref name="maxLevel"/> of each document, after each document's own top level.</summary>
    public static IReadOnlyList<OrgRefileTarget> Targets(IEnumerable<OrgDocument> documents, int maxLevel = 3)
    {
        var targets = new List<OrgRefileTarget>();
        foreach (var doc in documents)
        {
            targets.Add(new OrgRefileTarget(doc, null));
            targets.AddRange(doc.Headings.Where(h => h.Level <= maxLevel).Select(h => new OrgRefileTarget(doc, h)));
        }
        return targets;
    }

    /// <summary>The subtree's lines, without the blank lines that end it.</summary>
    public static List<string> SubtreeLines(OrgDocument doc, OrgHeading h)
    {
        var end = h.SubtreeEnd;
        while (end > h.Line + 1 && string.IsNullOrWhiteSpace(doc.Lines[end - 1])) end--;
        return [.. doc.Lines.Skip(h.Line).Take(end - h.Line)];
    }

    /// <summary>The lines with every heading <paramref name="delta"/> levels deeper (or higher; never above level 1); lines in blocks are left alone.</summary>
    public static List<string> Relevel(IReadOnlyList<string> lines, int delta)
    {
        if (delta == 0) return [.. lines];
        var result = new List<string>(lines.Count);
        var inBlock = false;
        foreach (var line in lines)
        {
            if (inBlock) { if (BlockEndRe.IsMatch(line)) inBlock = false; result.Add(line); continue; }
            if (BlockStartRe.IsMatch(line)) { inBlock = true; result.Add(line); continue; }
            var m = HeadingRe.Match(line);
            result.Add(m.Success ? new string('*', Math.Max(1, m.Groups[1].Length + delta)) + m.Groups[2].Value : line);
        }
        return result;
    }

    /// <summary>Deletes the subtree (its trailing blank lines too).</summary>
    public static OrgEditResult Remove(OrgDocument doc, OrgHeading h) => new([OrgEdits.ReplaceLines(doc, h.Line, h.SubtreeEnd, [])]);

    /// <summary>Where a new last child of <paramref name="parent"/> goes (before its subtree's trailing blank lines), or the end of the document.</summary>
    private static int InsertionLine(OrgDocument doc, OrgHeading? parent)
    {
        if (parent is null) return doc.ContentLineCount;
        var at = parent.SubtreeEnd;
        while (at > parent.Line + 1 && string.IsNullOrWhiteSpace(doc.Lines[at - 1])) at--;
        return at;
    }

    /// <summary>
    /// Inserts a subtree (whose top heading is at <paramref name="level"/>) as the last child of <paramref name="parent"/>, or at the top level at
    /// the end of the document; the caret goes to its heading.
    /// </summary>
    public static OrgEditResult InsertUnder(OrgDocument doc, OrgHeading? parent, IReadOnlyList<string> subtree, int level)
    {
        var lines = Relevel(subtree, (parent?.Level ?? 0) + 1 - level);
        var at = InsertionLine(doc, parent);
        return new OrgEditResult([OrgEdits.ReplaceLines(doc, at, at, lines)], new Core.Text.TextPosition(at, 0));
    }

    /// <summary>Refiles within one document (one edit over the lines between the two places). Null when the target is in the subtree itself.</summary>
    public static OrgEditResult? MoveWithin(OrgDocument doc, OrgHeading h, OrgHeading? parent)
    {
        if (parent is not null && parent.Line >= h.Line && parent.Line < h.SubtreeEnd) return null;
        var block = Relevel(SubtreeLines(doc, h), (parent?.Level ?? 0) + 1 - h.Level);
        var (from, to) = (h.Line, h.SubtreeEnd);
        var at = InsertionLine(doc, parent);
        List<string> lines;
        int start, end, caret;
        if (at <= from)
        {
            (start, end) = (at, to);
            lines = [.. block, .. doc.Lines.Skip(at).Take(from - at)];
            caret = at;
        }
        else
        {
            (start, end) = (from, at);
            lines = [.. doc.Lines.Skip(to).Take(at - to), .. block];
            caret = from + (at - to);
        }
        return new OrgEditResult([OrgEdits.ReplaceLines(doc, start, end, lines)], new Core.Text.TextPosition(caret, 0));
    }

    // ------------------------------------------------------------------------------------------------ archive

    /// <summary>
    /// Reads org's archive location (<c>org-archive-location</c>): <c>file::heading</c>, where <c>%s</c> in the file is the source file's path
    /// and the file is relative to the source's folder; an empty file part is the source file itself; an empty heading part means the top level.
    /// </summary>
    public static (string File, string? Heading) ArchiveLocation(string location, string sourcePath)
    {
        var sep = location.IndexOf("::", StringComparison.Ordinal);
        var filePart = sep < 0 ? location : location[..sep];
        var heading = sep < 0 ? null : location[(sep + 2)..].Trim();
        var file = filePart.Trim().Length == 0 ? sourcePath : filePart.Trim().Replace("%s", sourcePath, StringComparison.Ordinal);
        if (!Path.IsPathRooted(file)) file = Path.Combine(Path.GetDirectoryName(sourcePath) ?? "", file);
        return (Path.GetFullPath(file), string.IsNullOrEmpty(heading) ? null : heading);
    }

    /// <summary>The text a new archive file starts with (org's).</summary>
    public static string ArchiveFileHeader(string sourcePath) => $"#    -*- mode: org -*-\n\n\nArchived entries from file {sourcePath}\n\n\n";

    /// <summary>The subtree as it goes into the archive: its top heading with the <c>ARCHIVE_*</c> properties org records.</summary>
    public static List<string> ArchivedSubtree(OrgDocument doc, OrgHeading h, DateTime now, string sourcePath)
    {
        var lines = SubtreeLines(doc, h);
        var mini = OrgDocument.Parse(string.Join('\n', lines) + "\n", doc.Keywords);
        var props = new List<KeyValuePair<string, string>>
        {
            new("ARCHIVE_TIME", now.ToString("yyyy-MM-dd ddd HH:mm", CultureInfo.InvariantCulture)),
            new("ARCHIVE_FILE", sourcePath),
        };
        if (h.Ancestors() is { Count: > 0 } path) props.Add(new("ARCHIVE_OLPATH", string.Join('/', path)));
        props.Add(new("ARCHIVE_CATEGORY", h.Category));
        if (h.Keyword is { } kw) props.Add(new("ARCHIVE_TODO", kw));
        var text = OrgText.Apply(string.Join('\n', lines) + "\n", OrgEdits.SetProperties(mini, mini.Headings[0], props).Edits);
        var result = OrgDocument.SplitLines(text).ToList();
        if (result.Count > 0 && result[^1].Length == 0) result.RemoveAt(result.Count - 1);
        return result;
    }

    /// <summary>
    /// Puts an archived subtree (top heading at <paramref name="level"/>) into the archive document: under the heading the location names
    /// (created at the end when missing), else at the top level.
    /// </summary>
    public static OrgEditResult InsertIntoArchive(OrgDocument archive, string? headingLine, IReadOnlyList<string> subtree, int level)
    {
        if (headingLine is null) return InsertUnder(archive, null, subtree, level);
        var m = HeadingRe.Match(headingLine);
        var stars = m.Success ? m.Groups[1].Length : 1;
        var title = m.Success ? m.Groups[2].Value.Trim() : headingLine.Trim();
        if (archive.Headings.FirstOrDefault(h => h.Level == stars && h.Title == title) is { } parent) return InsertUnder(archive, parent, subtree, level);
        var lines = new List<string> { new string('*', stars) + " " + title };
        lines.AddRange(Relevel(subtree, stars + 1 - level));
        var at = archive.ContentLineCount;
        return new OrgEditResult([OrgEdits.ReplaceLines(archive, at, at, lines)], new Core.Text.TextPosition(at + 1, 0));
    }
}
