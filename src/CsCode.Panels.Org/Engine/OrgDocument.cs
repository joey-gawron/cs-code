using System.Text.RegularExpressions;

namespace CsCode.Panels.Org.Engine;

/// <summary>
/// The TODO keywords of a file: the states that are still to do, then the done states (org's <c>org-todo-keywords</c>, or a file's
/// <c>#+TODO: TODO NEXT | DONE</c>). Cycling goes none → each to-do state → each done state → none.
/// </summary>
public sealed class OrgTodoKeywords
{
    public static readonly OrgTodoKeywords Default = Parse(["TODO", "NEXT", "WAITING", "|", "DONE", "CANCELLED"]);

    private readonly HashSet<string> _done;
    private readonly HashSet<string> _all;

    public OrgTodoKeywords(IReadOnlyList<string> notDone, IReadOnlyList<string> done)
    {
        NotDone = notDone;
        Done = done;
        All = [.. notDone, .. done];
        _done = new HashSet<string>(done, StringComparer.Ordinal);
        _all = new HashSet<string>(All, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> NotDone { get; }
    public IReadOnlyList<string> Done { get; }
    public IReadOnlyList<string> All { get; }

    public bool IsKeyword(string word) => _all.Contains(word);
    public bool IsDone(string? keyword) => keyword is not null && _done.Contains(keyword);
    public bool IsTodo(string? keyword) => keyword is not null && _all.Contains(keyword) && !_done.Contains(keyword);

    /// <summary>The state after <paramref name="keyword"/> when cycling (null = no keyword).</summary>
    public string? Next(string? keyword)
    {
        if (All.Count == 0) return null;
        if (keyword is null) return All[0];
        var i = IndexOf(keyword);
        return i < 0 || i + 1 >= All.Count ? null : All[i + 1];
    }

    /// <summary>The state before <paramref name="keyword"/> when cycling backwards.</summary>
    public string? Previous(string? keyword)
    {
        if (All.Count == 0) return null;
        if (keyword is null) return All[^1];
        var i = IndexOf(keyword);
        return i <= 0 ? null : All[i - 1];
    }

    private int IndexOf(string keyword)
    {
        for (var i = 0; i < All.Count; i++) if (All[i] == keyword) return i;
        return -1;
    }

    /// <summary>
    /// Reads org's spelling: words, <c>|</c> between the to-do and the done states (without one, the last word is the done state), and
    /// fast-access suffixes such as <c>TODO(t)</c> or <c>WAIT(w@/!)</c>, which are dropped.
    /// </summary>
    public static OrgTodoKeywords Parse(IEnumerable<string> words)
    {
        var notDone = new List<string>();
        var done = new List<string>();
        var seenBar = false;
        foreach (var raw in words)
        {
            foreach (var part in raw.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == "|") { seenBar = true; continue; }
                var word = part.IndexOf('(') is > 0 and var p ? part[..p] : part;
                if (word.Length == 0 || notDone.Contains(word) || done.Contains(word)) continue;
                (seenBar ? done : notDone).Add(word);
            }
        }
        if (!seenBar && notDone.Count > 1) { done.Add(notDone[^1]); notDone.RemoveAt(notDone.Count - 1); }
        return new OrgTodoKeywords(notDone, done);
    }
}

/// <summary>A line of a heading's planning: <c>SCHEDULED:</c>, <c>DEADLINE:</c> or <c>CLOSED:</c>.</summary>
public enum OrgPlanningKind { Scheduled, Deadline, Closed }

/// <summary>An active timestamp in a heading's title or body (an appointment), with the line it is on.</summary>
public sealed record OrgBodyTimestamp(OrgTimestamp Timestamp, int Line, int Column);

/// <summary>
/// One heading (entry) of an org file, with its line ranges: the heading line, the planning line right below it, the property drawer, the
/// entry's own content (up to the next heading of any level) and its subtree (up to the next heading of its level or above).
/// </summary>
public sealed class OrgHeading
{
    internal OrgHeading() { }

    public int Index { get; internal set; }
    public int Level { get; internal init; }
    public int Line { get; internal init; }
    public string? Keyword { get; internal init; }
    public char? Priority { get; internal init; }
    public string Title { get; internal init; } = "";
    public IReadOnlyList<string> Tags { get; internal init; } = [];
    public OrgHeading? Parent { get; internal init; }

    /// <summary>Column of the TODO keyword, or of the first character after the stars and their blank when there is none.</summary>
    public int KeywordColumn { get; internal init; }
    /// <summary>Column of <c>[#A]</c>, or -1.</summary>
    public int PriorityColumn { get; internal init; } = -1;
    public int TitleColumn { get; internal init; }
    /// <summary>Column after the title's last character (before the blanks and the tags).</summary>
    public int TitleEnd { get; internal init; }
    /// <summary>Column of the tags (<c>:a:b:</c>), or -1.</summary>
    public int TagsColumn { get; internal init; } = -1;

    public int? PlanningLine { get; internal set; }
    /// <summary>The planning items in the order the line has them.</summary>
    public IReadOnlyList<(OrgPlanningKind Kind, OrgTimestamp Timestamp)> Planning { get; internal set; } = [];
    public OrgTimestamp? Scheduled => PlanningOf(OrgPlanningKind.Scheduled);
    public OrgTimestamp? Deadline => PlanningOf(OrgPlanningKind.Deadline);
    public OrgTimestamp? Closed => PlanningOf(OrgPlanningKind.Closed);

    /// <summary>The <c>:PROPERTIES:</c> line, or null.</summary>
    public int? PropertiesLine { get; internal set; }
    /// <summary>The drawer's <c>:END:</c> line, or null.</summary>
    public int? PropertiesEndLine { get; internal set; }
    public IReadOnlyList<KeyValuePair<string, string>> Properties { get; internal set; } = [];

    /// <summary>The first line after the heading, its planning line and its property drawer.</summary>
    public int ContentStart { get; internal set; }
    /// <summary>The line of the next heading (any level), or the document's line count: the end (exclusive) of this entry's own text.</summary>
    public int End { get; internal set; }
    /// <summary>The line of the next heading of this level or above (exclusive), trailing blank lines included.</summary>
    public int SubtreeEnd { get; internal set; }

    public IReadOnlyList<OrgBodyTimestamp> Timestamps { get; internal set; } = [];

    /// <summary>The <c>CATEGORY</c> property of this heading or the nearest ancestor that has one, else the file's category.</summary>
    public string Category { get; internal set; } = "";
    /// <summary>The heading's own tags, then its ancestors' and the file's (<c>#+FILETAGS</c>), without repeats.</summary>
    public IReadOnlyList<string> AllTags { get; internal set; } = [];

    public bool IsDone { get; internal init; }
    public bool IsTodo => Keyword is not null && !IsDone;

    public string? Property(string key)
    {
        foreach (var p in Properties) if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    public OrgTimestamp? PlanningOf(OrgPlanningKind kind)
    {
        foreach (var (k, ts) in Planning) if (k == kind) return ts;
        return null;
    }

    /// <summary>The titles from the top-level ancestor down to this heading's parent.</summary>
    public IReadOnlyList<string> Ancestors()
    {
        var list = new List<string>();
        for (var p = Parent; p is not null; p = p.Parent) list.Insert(0, p.Title);
        return list;
    }

    public override string ToString() => new string('*', Level) + " " + (Keyword is null ? "" : Keyword + " ") + Title;
}

/// <summary>
/// A parsed org file (immutable once parsed): its lines (as the editor numbers them: a final empty line when the text ends with a line break),
/// the in-buffer settings (<c>#+TITLE</c>, <c>#+CATEGORY</c>, <c>#+FILETAGS</c>, <c>#+TODO</c>) and its headings in document order.
/// </summary>
public sealed class OrgDocument
{
    private static readonly Regex HeadingRe = new(@"^(\*+)[ \t]+(.*?)[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TagsRe = new(@"[ \t]+(:(?:[\p{L}\p{N}_@#%]+:)+)[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PriorityRe = new(@"^\[#([A-Z0-9])\](?:[ \t]+|$)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PlanningRe = new(@"\b(SCHEDULED|DEADLINE|CLOSED):[ \t]*", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PlanningLineRe = new(@"^[ \t]*(?:SCHEDULED|DEADLINE|CLOSED):", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DrawerStartRe = new(@"^[ \t]*:([\w\-]+):[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DrawerEndRe = new(@"^[ \t]*:END:[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PropertyRe = new(@"^[ \t]*:([^\s:]+?)(\+)?:(?:[ \t]+(.*?))?[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex KeywordRe = new(@"^[ \t]*#\+(\w+):[ \t]*(.*?)[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BlockStartRe = new(@"^[ \t]*#\+begin_(\w+)", RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BlockEndRe = new(@"^[ \t]*#\+end_(\w+)", RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private OrgDocument(string fileName, IReadOnlyList<string> lines, OrgTodoKeywords keywords)
    {
        FileName = fileName;
        Lines = lines;
        Keywords = keywords;
    }

    /// <summary>The file this was read from (null for text that is not a file).</summary>
    public Uri? Uri { get; private init; }
    public string FileName { get; }
    public IReadOnlyList<string> Lines { get; }
    public int LineCount => Lines.Count;
    /// <summary>The lines that hold text: <see cref="LineCount"/> without the final empty line a closing line break makes.</summary>
    public int ContentLineCount => Lines.Count > 1 && Lines[^1].Length == 0 ? Lines.Count - 1 : Lines.Count;
    public OrgTodoKeywords Keywords { get; }
    public string? Title { get; private set; }
    public string Category { get; private set; } = "";
    public IReadOnlyList<string> FileTags { get; private set; } = [];
    public IReadOnlyList<OrgHeading> Headings { get; private set; } = [];

    /// <summary>Splits text into lines the way the editor numbers them (<c>\r\n</c>, <c>\n</c> and <c>\r</c> all end a line).</summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\n' && c != '\r') continue;
            lines.Add(text[start..i]);
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        lines.Add(text[start..]);
        return lines;
    }

    public static OrgDocument Parse(string text, OrgTodoKeywords? defaultKeywords = null, string? fileName = null, Uri? uri = null) =>
        Parse(SplitLines(text), defaultKeywords, fileName, uri);

    public static OrgDocument Parse(IReadOnlyList<string> lines, OrgTodoKeywords? defaultKeywords = null, string? fileName = null, Uri? uri = null)
    {
        fileName ??= uri is null ? "" : Path.GetFileName(uri.LocalPath);
        // in-buffer settings first: #+TODO lines decide what a heading's first word is
        string? title = null, category = null;
        var fileTags = new List<string>();
        List<string>? todoWords = null;
        foreach (var line in lines)
        {
            if (line.Length < 4 || line.TrimStart()[0] != '#') continue;
            var m = KeywordRe.Match(line);
            if (!m.Success) continue;
            var value = m.Groups[2].Value;
            switch (m.Groups[1].Value.ToUpperInvariant())
            {
                case "TITLE": title ??= value; break;
                case "CATEGORY": category ??= value; break;
                case "FILETAGS": fileTags.AddRange(value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                case "TODO" or "SEQ_TODO" or "TYP_TODO":
                    // each line is a sequence of its own: its last word is done when it has no |
                    var seq = OrgTodoKeywords.Parse([value]);
                    todoWords ??= [];
                    todoWords.AddRange(seq.NotDone);
                    todoWords.Add("|");
                    todoWords.AddRange(seq.Done);
                    todoWords.Add("|"); // back to to-do states for the next line's sequence
                    break;
            }
        }
        var keywords = todoWords is null ? defaultKeywords ?? OrgTodoKeywords.Default : MergeSequences(todoWords);
        var doc = new OrgDocument(fileName, lines, keywords)
        {
            Uri = uri,
            Title = title,
            Category = string.IsNullOrWhiteSpace(category) ? Path.GetFileNameWithoutExtension(fileName) : category!,
            FileTags = fileTags,
        };
        doc.Headings = doc.ParseHeadings();
        return doc;
    }

    // "TODO NEXT | DONE" and "BUG | FIXED" on two #+TODO lines: to-do states of both, then the done states of both
    private static OrgTodoKeywords MergeSequences(List<string> words)
    {
        var notDone = new List<string>();
        var done = new List<string>();
        var inDone = false;
        foreach (var w in words)
        {
            if (w == "|") { inDone = !inDone; continue; }
            var target = inDone ? done : notDone;
            if (!notDone.Contains(w) && !done.Contains(w)) target.Add(w);
        }
        return new OrgTodoKeywords(notDone, done);
    }

    private List<OrgHeading> ParseHeadings()
    {
        var headings = new List<OrgHeading>();
        var stack = new List<OrgHeading>(); // the open ancestors, outermost first
        var contentLines = ContentLineCount;
        for (var i = 0; i < contentLines; i++)
        {
            var line = Lines[i];
            if (line.Length < 2 || line[0] != '*') continue;
            var m = HeadingRe.Match(line);
            if (!m.Success) continue;
            var level = m.Groups[1].Length;
            while (stack.Count > 0 && stack[^1].Level >= level) stack.RemoveAt(stack.Count - 1);
            var h = ParseHeadingLine(line, i, level, m.Groups[2].Index, stack.Count > 0 ? stack[^1] : null);
            h.Index = headings.Count;
            headings.Add(h);
            stack.Add(h);
        }

        for (var k = 0; k < headings.Count; k++)
        {
            var h = headings[k];
            h.End = k + 1 < headings.Count ? headings[k + 1].Line : contentLines;
            var end = contentLines;
            for (var j = k + 1; j < headings.Count; j++)
                if (headings[j].Level <= h.Level) { end = headings[j].Line; break; }
            h.SubtreeEnd = end;
            ParseMeta(h);
            ParseBody(h);
            var category = h.Property("CATEGORY") ?? h.Parent?.Category ?? Category;
            h.Category = string.IsNullOrWhiteSpace(category) ? Category : category;
            var tags = new List<string>(h.Tags);
            for (var p = h.Parent; p is not null; p = p.Parent)
                foreach (var t in p.Tags) if (!tags.Contains(t)) tags.Add(t);
            foreach (var t in FileTags) if (!tags.Contains(t)) tags.Add(t);
            h.AllTags = tags;
        }
        return headings;
    }

    private OrgHeading ParseHeadingLine(string line, int lineNo, int level, int restColumn, OrgHeading? parent)
    {
        var rest = line[restColumn..];
        var restEnd = rest.Length; // after trimming trailing blanks below
        while (restEnd > 0 && (rest[restEnd - 1] == ' ' || rest[restEnd - 1] == '\t')) restEnd--;
        rest = rest[..restEnd];

        string? keyword = null;
        var keywordColumn = restColumn;
        var pos = 0;
        var space = rest.IndexOfAny([' ', '\t']);
        var first = space < 0 ? rest : rest[..space];
        if (first.Length > 0 && Keywords.IsKeyword(first))
        {
            keyword = first;
            pos = first.Length;
            while (pos < rest.Length && (rest[pos] == ' ' || rest[pos] == '\t')) pos++;
        }

        char? priority = null;
        var priorityColumn = -1;
        var pm = PriorityRe.Match(rest[pos..]);
        if (pm.Success)
        {
            priority = pm.Groups[1].Value[0];
            priorityColumn = restColumn + pos;
            pos += pm.Length;
        }

        var titleColumn = restColumn + pos;
        var titleText = rest[pos..];
        IReadOnlyList<string> tags = [];
        var tagsColumn = -1;
        var tm = TagsRe.Match(titleText);
        if (tm.Success)
        {
            tags = tm.Groups[1].Value.Split(':', StringSplitOptions.RemoveEmptyEntries);
            tagsColumn = titleColumn + tm.Groups[1].Index;
            titleText = titleText[..tm.Index];
        }
        else if (titleText.StartsWith(':') && TagsRe.Match(" " + titleText) is { Success: true } only)
        {
            // a heading of tags only: "* :work:"
            tags = only.Groups[1].Value.Split(':', StringSplitOptions.RemoveEmptyEntries);
            tagsColumn = titleColumn;
            titleText = "";
        }
        return new OrgHeading
        {
            Level = level,
            Line = lineNo,
            Keyword = keyword,
            IsDone = Keywords.IsDone(keyword),
            Priority = priority,
            Title = titleText.TrimEnd(),
            Tags = tags,
            Parent = parent,
            KeywordColumn = keywordColumn,
            PriorityColumn = priorityColumn,
            TitleColumn = titleColumn,
            TitleEnd = titleColumn + titleText.TrimEnd().Length,
            TagsColumn = tagsColumn,
        };
    }

    // the planning line (right after the heading, as in org) and the property drawer (right after that)
    private void ParseMeta(OrgHeading h)
    {
        var next = h.Line + 1;
        if (next < h.End && PlanningLineRe.IsMatch(Lines[next]))
        {
            h.PlanningLine = next;
            h.Planning = ParsePlanning(Lines[next]);
            next++;
        }
        if (next < h.End && Lines[next].Trim().Equals(":PROPERTIES:", StringComparison.OrdinalIgnoreCase))
        {
            for (var j = next + 1; j < h.End; j++)
            {
                if (!DrawerEndRe.IsMatch(Lines[j])) continue;
                var props = new List<KeyValuePair<string, string>>();
                for (var p = next + 1; p < j; p++)
                {
                    var m = PropertyRe.Match(Lines[p]);
                    if (!m.Success) continue;
                    var key = m.Groups[1].Value;
                    var value = m.Groups[3].Value;
                    var at = props.FindIndex(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
                    if (m.Groups[2].Success && at >= 0) props[at] = new(props[at].Key, props[at].Value + " " + value); // :KEY+: appends
                    else if (at >= 0) props[at] = new(key, value);
                    else props.Add(new(key, value));
                }
                h.PropertiesLine = next;
                h.PropertiesEndLine = j;
                h.Properties = props;
                next = j + 1;
                break;
            }
        }
        h.ContentStart = next;
    }

    internal static IReadOnlyList<(OrgPlanningKind, OrgTimestamp)> ParsePlanning(string line)
    {
        var items = new List<(OrgPlanningKind, OrgTimestamp)>();
        foreach (Match m in PlanningRe.Matches(line))
        {
            var kind = m.Groups[1].Value switch { "SCHEDULED" => OrgPlanningKind.Scheduled, "DEADLINE" => OrgPlanningKind.Deadline, _ => OrgPlanningKind.Closed };
            var after = m.Index + m.Length;
            foreach (var (ts, column, _) in OrgTimestamp.FindAll(line[after..]))
            {
                if (column == 0 && !items.Exists(e => e.Item1 == kind)) items.Add((kind, ts));
                break;
            }
        }
        return items;
    }

    // active timestamps in the title and the entry's own text: not in drawers (clock lines, logbook) or blocks (#+BEGIN_SRC ... #+END_SRC)
    private void ParseBody(OrgHeading h)
    {
        List<OrgBodyTimestamp>? found = null;
        foreach (var (ts, column, _) in OrgTimestamp.FindAll(Lines[h.Line]))
            if (ts.Active) (found ??= []).Add(new OrgBodyTimestamp(ts, h.Line, column));
        string? block = null;
        var inDrawer = false;
        for (var i = h.ContentStart; i < h.End; i++)
        {
            var line = Lines[i];
            if (block is not null)
            {
                if (BlockEndRe.Match(line) is { Success: true } e && e.Groups[1].Value.Equals(block, StringComparison.OrdinalIgnoreCase)) block = null;
                continue;
            }
            if (inDrawer)
            {
                if (DrawerEndRe.IsMatch(line)) inDrawer = false;
                continue;
            }
            if (BlockStartRe.Match(line) is { Success: true } b) { block = b.Groups[1].Value; continue; }
            if (DrawerStartRe.IsMatch(line) && !DrawerEndRe.IsMatch(line)) { inDrawer = true; continue; }
            if (line.IndexOf('<') < 0) continue;
            foreach (var (ts, column, _) in OrgTimestamp.FindAll(line))
                if (ts.Active) (found ??= []).Add(new OrgBodyTimestamp(ts, i, column));
        }
        h.Timestamps = found ?? (IReadOnlyList<OrgBodyTimestamp>)[];
    }

    /// <summary>The entry whose text contains <paramref name="line"/> (the last heading at or above it), or null before the first heading.</summary>
    public OrgHeading? HeadingAt(int line)
    {
        OrgHeading? found = null;
        var lo = 0;
        var hi = Headings.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Headings[mid].Line <= line) { found = Headings[mid]; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    /// <summary>The entry's own text (no heading, planning or property drawer), with the indentation its lines share removed.</summary>
    public string BodyText(OrgHeading h, int maxLines = 400)
    {
        var start = h.ContentStart;
        var end = Math.Min(h.End, start + maxLines);
        while (end > start && string.IsNullOrWhiteSpace(Lines[end - 1])) end--;
        while (start < end && string.IsNullOrWhiteSpace(Lines[start])) start++;
        if (start >= end) return "";
        var indent = int.MaxValue;
        for (var i = start; i < end; i++)
        {
            var l = Lines[i];
            if (string.IsNullOrWhiteSpace(l)) continue;
            var n = 0;
            while (n < l.Length && (l[n] == ' ' || l[n] == '\t')) n++;
            indent = Math.Min(indent, n);
        }
        var sb = new System.Text.StringBuilder();
        for (var i = start; i < end; i++)
        {
            var l = Lines[i];
            sb.Append(l.Length >= indent ? l[indent..] : l.TrimStart());
            if (i + 1 < end) sb.Append('\n');
        }
        if (end < h.End && end - start >= maxLines) sb.Append("\n…");
        return sb.ToString();
    }
}
