using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsCode.Core.Text;

namespace CsCode.Panels.Org.Engine;

/// <summary>The text edits of one org command (non-overlapping, against the document the command read), where the caret goes, and what to tell the user.</summary>
public sealed record OrgEditResult(IReadOnlyList<TextEdit> Edits, TextPosition? Caret = null, string? Message = null);

/// <summary>
/// The org commands as pure text transformations of a parsed <see cref="OrgDocument"/>: they return the edits to apply (to the open editor
/// as one undo step, or to the file on disk), never touch the document themselves, and keep every line they do not mean to change as it is.
/// Null means "nothing to do here" (no heading at the line, no sibling to move past).
/// </summary>
public static class OrgEdits
{
    /// <summary>Org's <c>org-tags-column</c> (-77): tags end at column 77 when the title leaves room.</summary>
    public const int TagsColumn = 77;

    private static readonly Regex CheckboxRe = new(@"^([ \t]*(?:[-+*]|\d+[.)])[ \t]+)\[([ xX\-])\]", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ListItemRe = new(@"^([ \t]*)([-+*]|\d+[.)])([ \t]+)(\[[ xX\-]\][ \t]+)?", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CookieRe = new(@"\[(\d*%|\d*/\d*)\]", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PropertyKeyRe = new(@"^[ \t]*:([^\s:]+?)\+?:", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ------------------------------------------------------------------------------------------------ TODO state

    /// <summary>
    /// Sets the heading's TODO keyword (null removes it). Becoming done adds <c>CLOSED: [now]</c> when <paramref name="logDone"/> (leaving a done
    /// state removes it) — unless the entry repeats: a scheduled or deadline timestamp with a repeater moves to its next repetition, the entry
    /// keeps its to-do state and gets <c>:LAST_REPEAT:</c> and, when <paramref name="logRepeat"/>, a <c>- State "DONE" from "TODO" [now]</c>
    /// line in its <c>:LOGBOOK:</c> (what a habit's history is made of), as org does.
    /// </summary>
    public static OrgEditResult SetKeyword(OrgDocument doc, OrgHeading h, string? keyword, DateTime now, bool logDone = true, bool logRepeat = true)
    {
        var keywords = doc.Keywords;
        var toDone = keywords.IsDone(keyword);
        var planning = h.Planning.ToList();
        var props = new List<KeyValuePair<string, string>>();
        List<string>? log = null;
        string? message = null;

        if (toDone && !h.IsDone && planning.Exists(p => p.Kind != OrgPlanningKind.Closed && p.Timestamp.Repeater is not null))
        {
            var today = DateOnly.FromDateTime(now);
            for (var i = 0; i < planning.Count; i++)
                if (planning[i].Kind != OrgPlanningKind.Closed && planning[i].Timestamp.Repeater is not null)
                    planning[i] = (planning[i].Kind, planning[i].Timestamp.Repeat(today));
            planning.RemoveAll(p => p.Kind == OrgPlanningKind.Closed);
            props.Add(new("LAST_REPEAT", Inactive(now).ToString()));
            if (logRepeat) log = [StateLine(keyword is null ? "" : keyword, h.Keyword, now)];
            keyword = keywords.IsTodo(h.Keyword) ? h.Keyword : keywords.NotDone.FirstOrDefault();
            var next = planning.FirstOrDefault(p => p.Kind != OrgPlanningKind.Closed && p.Timestamp.Repeater is not null);
            message = $"Entry repeats: {Name(next.Kind)} {next.Timestamp}";
        }
        else if (toDone && !h.IsDone && logDone) Put(planning, OrgPlanningKind.Closed, Inactive(now));
        else if (!toDone && h.IsDone) planning.RemoveAll(p => p.Kind == OrgPlanningKind.Closed);

        var edits = new List<TextEdit>();
        if (keyword != h.Keyword) edits.Add(KeywordEdit(doc, h, keyword));
        if (MetaEdit(doc, h, planning, props, log) is { } meta) edits.Add(meta);
        if (log is not null && LogbookEdit(doc, h, log) is { } logbook) edits.Add(logbook);
        return new OrgEditResult(edits, Message: message);
    }

    /// <summary>Org's state log line: <c>- State "DONE"       from "TODO"       [2026-11-03 Tue 14:05]</c>.</summary>
    internal static string StateLine(string to, string? from, DateTime now) =>
        string.Format(CultureInfo.InvariantCulture, "- State {0,-12} from {1,-12} {2}", "\"" + to + "\"", "\"" + from + "\"", Inactive(now));

    /// <summary>Sets properties of the heading (in its drawer, created when it has none); other lines of the drawer stay as they are.</summary>
    public static OrgEditResult SetProperties(OrgDocument doc, OrgHeading h, IEnumerable<KeyValuePair<string, string>> properties)
    {
        var edit = MetaEdit(doc, h, h.Planning.ToList(), properties.ToList());
        return new OrgEditResult(edit is null ? [] : [edit]);
    }

    /// <summary>
    /// The edit that puts <paramref name="lines"/> at the top of the entry's existing <c>:LOGBOOK:</c> (newest first, as org writes it); null
    /// when the entry has none (<see cref="MetaEdit"/> creates the drawer then).
    /// </summary>
    internal static TextEdit? LogbookEdit(OrgDocument doc, OrgHeading h, IReadOnlyList<string> lines)
    {
        if (h.LogbookLine is not { } at) return null;
        var indent = LeadingBlanks(doc.Lines[at]);
        return ReplaceLines(doc, at + 1, at + 1, [.. lines.Select(l => indent + l)]);
    }

    /// <summary>
    /// Adds lines to the entry's logbook: at the top of its <c>:LOGBOOK:</c>, or in a new drawer right after its planning line and properties.
    /// </summary>
    public static OrgEditResult AddToLogbook(OrgDocument doc, OrgHeading h, IReadOnlyList<string> lines)
    {
        var edit = LogbookEdit(doc, h, lines) ?? MetaEdit(doc, h, h.Planning.ToList(), [], [.. lines]);
        return new OrgEditResult(edit is null ? [] : [edit]);
    }

    /// <summary>The next (or previous) TODO state of the heading in its file's keyword sequence.</summary>
    public static OrgEditResult CycleKeyword(OrgDocument doc, OrgHeading h, DateTime now, bool logDone = true, bool backwards = false) =>
        SetKeyword(doc, h, backwards ? doc.Keywords.Previous(h.Keyword) : doc.Keywords.Next(h.Keyword), now, logDone);

    private static TextEdit KeywordEdit(OrgDocument doc, OrgHeading h, string? keyword)
    {
        var line = doc.Lines[h.Line];
        if (h.Keyword is null)
            return TextEdit.Insert(new TextPosition(h.Line, h.KeywordColumn), keyword + (h.KeywordColumn < line.Length ? " " : ""));
        var end = h.KeywordColumn + h.Keyword.Length;
        if (keyword is not null) return new TextEdit(Span(h.Line, h.KeywordColumn, end), keyword);
        // removing it removes its blank too
        var next = h.PriorityColumn >= 0 ? h.PriorityColumn : Math.Max(end, h.TitleColumn);
        return new TextEdit(Span(h.Line, h.KeywordColumn, Math.Min(next, line.Length)), "");
    }

    // ------------------------------------------------------------------------------------------------ planning

    /// <summary>
    /// Sets (or with null removes) the heading's <c>SCHEDULED</c>, <c>DEADLINE</c> or <c>CLOSED</c> timestamp. A new date for a timestamp that
    /// repeats keeps its repeater and warning unless <paramref name="value"/> has its own (org keeps them too).
    /// </summary>
    public static OrgEditResult SetPlanning(OrgDocument doc, OrgHeading h, OrgPlanningKind kind, OrgTimestamp? value, bool keepCookies = true)
    {
        var planning = h.Planning.ToList();
        if (value is null) planning.RemoveAll(p => p.Kind == kind);
        else
        {
            if (keepCookies && h.PlanningOf(kind) is { } old)
                value = value with { Repeater = value.Repeater ?? old.Repeater, Warning = value.Warning ?? old.Warning };
            Put(planning, kind, value);
        }
        var edit = MetaEdit(doc, h, planning, []);
        return new OrgEditResult(edit is null ? [] : [edit],
            Message: value is null ? $"Removed {Name(kind)}" : $"{Name(kind)} {OrgDateInput.Describe(value)}");
    }

    private static string Name(OrgPlanningKind kind) => kind switch
    {
        OrgPlanningKind.Scheduled => "Scheduled",
        OrgPlanningKind.Deadline => "Deadline",
        _ => "Closed",
    };

    private static string Keyword(OrgPlanningKind kind) => kind switch
    {
        OrgPlanningKind.Scheduled => "SCHEDULED",
        OrgPlanningKind.Deadline => "DEADLINE",
        _ => "CLOSED",
    };

    // a kind already there keeps its place; a new one goes where org puts it: CLOSED, DEADLINE, SCHEDULED
    private static void Put(List<(OrgPlanningKind Kind, OrgTimestamp Timestamp)> planning, OrgPlanningKind kind, OrgTimestamp value)
    {
        var at = planning.FindIndex(p => p.Kind == kind);
        if (at >= 0) { planning[at] = (kind, value); return; }
        static int Rank(OrgPlanningKind k) => k switch { OrgPlanningKind.Closed => 0, OrgPlanningKind.Deadline => 1, _ => 2 };
        var insert = planning.FindIndex(p => Rank(p.Kind) > Rank(kind));
        planning.Insert(insert < 0 ? planning.Count : insert, (kind, value));
    }

    public static OrgTimestamp Inactive(DateTime now) =>
        new(false, DateOnly.FromDateTime(now), new TimeOnly(now.Hour, now.Minute));

    /// <summary>
    /// Rewrites the lines between the heading and its content (the planning line and the property drawer) with <paramref name="planning"/> and
    /// the properties set in <paramref name="setProperties"/>; every other line of the drawer stays as it is. Null when nothing changes.
    /// </summary>
    private static TextEdit? MetaEdit(OrgDocument doc, OrgHeading h, List<(OrgPlanningKind Kind, OrgTimestamp Timestamp)> planning, List<KeyValuePair<string, string>> setProperties,
        List<string>? newLogbook = null)
    {
        var indent = MetaIndent(doc, h);
        var lines = new List<string>();
        if (planning.Count > 0)
            lines.Add(indent + string.Join(' ', planning.Select(p => Keyword(p.Kind) + ": " + p.Timestamp)));

        if (h.PropertiesLine is { } from && h.PropertiesEndLine is { } to)
        {
            var drawer = new List<string>();
            for (var i = from; i <= to; i++) drawer.Add(doc.Lines[i]);
            foreach (var (key, value) in setProperties)
            {
                var at = drawer.FindIndex(1, drawer.Count - 2, l => PropertyKeyRe.Match(l) is { Success: true } m && string.Equals(m.Groups[1].Value, key, StringComparison.OrdinalIgnoreCase));
                var propIndent = LeadingBlanks(drawer[0]);
                if (at >= 0) drawer[at] = propIndent + ":" + key + ": " + value;
                else drawer.Insert(drawer.Count - 1, propIndent + ":" + key + ": " + value);
            }
            lines.AddRange(drawer);
        }
        else if (setProperties.Count > 0)
        {
            lines.Add(indent + ":PROPERTIES:");
            foreach (var (key, value) in setProperties) lines.Add(indent + ":" + key + ": " + value);
            lines.Add(indent + ":END:");
        }

        // an entry without a logbook gets one right after its properties (where org's log goes)
        if (newLogbook is { Count: > 0 } && h.LogbookLine is null)
        {
            lines.Add(indent + ":LOGBOOK:");
            lines.AddRange(newLogbook.Select(l => indent + l));
            lines.Add(indent + ":END:");
        }

        var start = h.Line + 1;
        var old = new List<string>();
        for (var i = start; i < h.ContentStart; i++) old.Add(doc.Lines[i]);
        if (h.PlanningLine is { } pl && planning.Count > 0 && SamePlanning(doc.Lines[pl], planning)) lines[0] = doc.Lines[pl]; // untouched: keep its spacing
        if (old.SequenceEqual(lines)) return null;
        return ReplaceLines(doc, start, h.ContentStart, lines);
    }

    private static bool SamePlanning(string line, List<(OrgPlanningKind Kind, OrgTimestamp Timestamp)> planning)
    {
        var parsed = OrgDocument.ParsePlanning(line);
        if (parsed.Count != planning.Count) return false;
        for (var i = 0; i < parsed.Count; i++)
            if (parsed[i].Item1 != planning[i].Kind || parsed[i].Item2 != planning[i].Timestamp) return false;
        return true;
    }

    // the planning line's own indentation; else the drawer's; else that of the entry's first line of text when it is indented (a file written
    // with org-adapt-indentation); else none (org's default since 9.5)
    internal static string MetaIndent(OrgDocument doc, OrgHeading h)
    {
        if (h.PlanningLine is { } pl) return LeadingBlanks(doc.Lines[pl]);
        if (h.PropertiesLine is { } pr) return LeadingBlanks(doc.Lines[pr]);
        if (h.ContentStart < h.End && doc.Lines[h.ContentStart] is { Length: > 0 } first && !string.IsNullOrWhiteSpace(first) && first[0] is ' ' or '\t')
            return LeadingBlanks(first);
        return "";
    }

    internal static string LeadingBlanks(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] is ' ' or '\t') n++;
        return line[..n];
    }

    // ------------------------------------------------------------------------------------------------ priority, tags

    /// <summary>Sets the heading's priority cookie (<c>[#A]</c>), or removes it with null.</summary>
    public static OrgEditResult SetPriority(OrgDocument doc, OrgHeading h, char? priority)
    {
        if (h.Priority == priority) return new OrgEditResult([]);
        TextEdit edit;
        if (h.PriorityColumn >= 0)
        {
            var end = h.PriorityColumn + 4;
            edit = priority is { } p
                ? new TextEdit(Span(h.Line, h.PriorityColumn, end), $"[#{p}]")
                : new TextEdit(Span(h.Line, h.PriorityColumn, Math.Max(end, Math.Min(h.TitleColumn, doc.Lines[h.Line].Length))), "");
        }
        else
        {
            var line = doc.Lines[h.Line];
            var at = h.TitleColumn;
            var text = $"[#{priority}]" + (at < line.Length ? " " : "");
            if (h.Keyword is not null && at == h.KeywordColumn + h.Keyword.Length) text = " " + text; // "* TODO" with nothing after it
            edit = TextEdit.Insert(new TextPosition(h.Line, Math.Min(at, line.Length)), text);
        }
        return new OrgEditResult([edit], Message: priority is null ? "Priority removed" : $"Priority {priority}");
    }

    /// <summary>
    /// The priority one step up (towards A) or down (towards C) from <paramref name="current"/>, as the agenda's <c>+</c> / <c>-</c>; no cookie
    /// counts as B, org's default priority.
    /// </summary>
    public static char ShiftPriority(char? current, bool up)
    {
        var c = current ?? 'B';
        if (c is < 'A' or > 'Z') return c;
        if (up) return c > 'A' ? (char)(c - 1) : c;
        return c < 'C' ? (char)(c + 1) : c;
    }

    /// <summary>Sets the heading's tags (an empty list removes them), right-aligned to <see cref="TagsColumn"/> as org aligns them.</summary>
    public static OrgEditResult SetTags(OrgDocument doc, OrgHeading h, IReadOnlyList<string> tags)
    {
        var line = doc.Lines[h.Line];
        var clean = tags.Select(t => t.Trim().Trim(':')).Where(t => t.Length > 0).Distinct().ToList();
        string text;
        if (clean.Count == 0) text = "";
        else
        {
            var cookie = ":" + string.Join(':', clean) + ":";
            var pad = Math.Max(1, TagsColumn - h.TitleEnd - cookie.Length);
            text = new string(' ', pad) + cookie;
        }
        return new OrgEditResult([new TextEdit(Span(h.Line, h.TitleEnd, line.Length), text)]);
    }

    // ------------------------------------------------------------------------------------------------ structure

    /// <summary>
    /// Promotes (<paramref name="delta"/> -1) or demotes (+1) the heading, with its subheadings when <paramref name="subtree"/> (org's M-left /
    /// M-S-left). Null when a heading would end above level 1.
    /// </summary>
    public static OrgEditResult? ChangeLevel(OrgDocument doc, OrgHeading h, int delta, bool subtree)
    {
        var affected = subtree ? doc.Headings.Where(x => x.Line >= h.Line && x.Line < h.SubtreeEnd).ToList() : [h];
        if (affected.Exists(x => x.Level + delta < 1)) return null;
        var edits = affected.Select(x => new TextEdit(Span(x.Line, 0, x.Level), new string('*', x.Level + delta))).ToList();
        return new OrgEditResult(edits);
    }

    /// <summary>
    /// Swaps the heading's subtree with the previous (or next) sibling's (org's M-up / M-down); the caret goes to the moved heading. Null when it
    /// has no sibling on that side.
    /// </summary>
    public static OrgEditResult? MoveSubtree(OrgDocument doc, OrgHeading h, bool up, int caretColumn = 0)
    {
        OrgHeading? other = up
            ? doc.Headings.LastOrDefault(x => x.Line < h.Line && ReferenceEquals(x.Parent, h.Parent) && x.SubtreeEnd == h.Line)
            : doc.Headings.FirstOrDefault(x => x.Line == h.SubtreeEnd && ReferenceEquals(x.Parent, h.Parent));
        if (other is null) return null;
        var (first, second) = up ? (other, h) : (h, other);
        var lines = new List<string>();
        for (var i = second.Line; i < second.SubtreeEnd; i++) lines.Add(doc.Lines[i]);
        for (var i = first.Line; i < first.SubtreeEnd; i++) lines.Add(doc.Lines[i]);
        var newLine = up ? first.Line : h.Line + (second.SubtreeEnd - second.Line);
        return new OrgEditResult([ReplaceLines(doc, first.Line, second.SubtreeEnd, lines)], new TextPosition(newLine, caretColumn));
    }

    /// <summary>
    /// Org's M-RET: on a list item, a new item of the same kind below the line; elsewhere a new heading of the current entry's level after its
    /// subtree (before the subtree's trailing blank lines), with <paramref name="keyword"/> when given. The caret goes to the end of the new line.
    /// </summary>
    public static OrgEditResult InsertHeading(OrgDocument doc, int line, string? keyword)
    {
        line = Math.Clamp(line, 0, Math.Max(0, doc.LineCount - 1));
        var lineText = doc.Lines.Count > 0 ? doc.Lines[line] : "";
        var h = doc.HeadingAt(line);
        if (keyword is null && (h is null || line != h.Line) && ListItemRe.Match(lineText) is { Success: true } item)
        {
            var bullet = item.Groups[2].Value;
            if (char.IsDigit(bullet[0]) && int.TryParse(bullet[..^1], out var n)) bullet = (n + 1) + bullet[^1..];
            var text = item.Groups[1].Value + bullet + item.Groups[3].Value + (item.Groups[4].Success ? "[ ] " : "");
            return new OrgEditResult([ReplaceLines(doc, line + 1, line + 1, [text])], new TextPosition(line + 1, text.Length));
        }
        var level = h?.Level ?? 1;
        var at = h is null ? line + 1 : h.SubtreeEnd;
        while (h is not null && at > h.Line + 1 && string.IsNullOrWhiteSpace(doc.Lines[at - 1])) at--;
        var heading = new string('*', level) + " " + (keyword is null ? "" : keyword + " ");
        return new OrgEditResult([ReplaceLines(doc, at, at, [heading])], new TextPosition(at, heading.Length));
    }

    /// <summary>
    /// Org's C-c C-c on a check box (<c>- [ ]</c> ↔ <c>- [X]</c>), updating the entry's statistics cookie (<c>[1/3]</c>, <c>[33%]</c>) in its
    /// heading; on a heading with a cookie, just the cookie. Null elsewhere.
    /// </summary>
    public static OrgEditResult? ToggleCheckbox(OrgDocument doc, int line)
    {
        if (line < 0 || line >= doc.LineCount) return null;
        var h = doc.HeadingAt(line);
        var edits = new List<TextEdit>();
        bool? toggledTo = null;
        if ((h is null || line != h.Line) && CheckboxRe.Match(doc.Lines[line]) is { Success: true } m)
        {
            var on = m.Groups[2].Value is " " or "-";
            toggledTo = on;
            var col = m.Groups[2].Index;
            edits.Add(new TextEdit(Span(line, col, col + 1), on ? "X" : " "));
        }
        if (h is not null && CookieEdit(doc, h, line, toggledTo) is { } cookie) edits.Add(cookie);
        return edits.Count == 0 ? null : new OrgEditResult(edits);
    }

    private static TextEdit? CookieEdit(OrgDocument doc, OrgHeading h, int toggledLine, bool? toggledTo)
    {
        var heading = doc.Lines[h.Line];
        var cm = CookieRe.Match(heading, h.TitleColumn);
        if (!cm.Success) return null;
        int done = 0, total = 0;
        for (var i = h.ContentStart; i < h.End; i++)
        {
            if (CheckboxRe.Match(doc.Lines[i]) is not { Success: true } m) continue;
            total++;
            var isOn = i == toggledLine && toggledTo is { } t ? t : m.Groups[2].Value is "x" or "X";
            if (isOn) done++;
        }
        var text = cm.Groups[1].Value.EndsWith('%')
            ? $"[{(total == 0 ? 0 : done * 100 / total)}%]"
            : $"[{done}/{total}]";
        return text == cm.Value ? null : new TextEdit(Span(h.Line, cm.Index, cm.Index + cm.Length), text);
    }

    // ------------------------------------------------------------------------------------------------ capture

    /// <summary>A new entry's text (ends with a line break): heading, planning line, a creation stamp, then the note's text.</summary>
    public static string Entry(int level, string? keyword, string title, OrgTimestamp? scheduled = null, OrgTimestamp? deadline = null,
        OrgTimestamp? created = null, string? body = null, IReadOnlyList<string>? tags = null)
    {
        var sb = new StringBuilder();
        var head = new string('*', Math.Max(1, level)) + " " + (keyword is null ? "" : keyword + " ") + title.Trim();
        if (tags is { Count: > 0 })
        {
            var cookie = ":" + string.Join(':', tags) + ":";
            head += new string(' ', Math.Max(1, TagsColumn - head.Length - cookie.Length)) + cookie;
        }
        sb.Append(head).Append('\n');
        var planning = new List<string>();
        if (deadline is not null) planning.Add("DEADLINE: " + deadline);
        if (scheduled is not null) planning.Add("SCHEDULED: " + scheduled);
        if (planning.Count > 0) sb.Append(string.Join(' ', planning)).Append('\n');
        if (created is not null) sb.Append(created).Append('\n');
        if (!string.IsNullOrWhiteSpace(body)) sb.Append(body.TrimEnd()).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Appends <paramref name="entry"/> (text ending with a line break) at the end of the document, on lines of its own; the caret goes to the end
    /// of the entry's last line.
    /// </summary>
    public static OrgEditResult Append(OrgDocument doc, string entry)
    {
        var lastLine = doc.LineCount - 1;
        var last = doc.Lines[lastLine];
        var onOwnLine = last.Length == 0;
        var text = onOwnLine ? entry : "\n" + entry;
        var headingLine = onOwnLine ? lastLine : lastLine + 1;
        var entryLines = OrgDocument.SplitLines(entry);
        var lastText = entryLines.Count > 1 && entryLines[^1].Length == 0 ? entryLines.Count - 2 : entryLines.Count - 1;
        return new OrgEditResult([TextEdit.Insert(new TextPosition(lastLine, last.Length), text)],
            new TextPosition(headingLine + lastText, entryLines[lastText].Length));
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static TextRange Span(int line, int from, int to) => new(new TextPosition(line, from), new TextPosition(line, to));

    /// <summary>
    /// The edit that replaces lines [<paramref name="from"/>, <paramref name="to"/>) with <paramref name="lines"/> (an empty range inserts before
    /// <paramref name="from"/>), keeping the document's last line break as it is.
    /// </summary>
    internal static TextEdit ReplaceLines(OrgDocument doc, int from, int to, IReadOnlyList<string> lines)
    {
        var count = doc.LineCount;
        if (to < count)
            return new TextEdit(new TextRange(new TextPosition(from, 0), new TextPosition(to, 0)), string.Concat(lines.Select(l => l + "\n")));
        // up to the end of a document whose last line has no line break
        var lastLen = doc.Lines[count - 1].Length;
        if (from >= count) // after the last line
            return TextEdit.Insert(new TextPosition(count - 1, lastLen), lines.Count == 0 ? "" : "\n" + string.Join('\n', lines));
        if (lines.Count == 0 && from > 0) // delete through the end, with the line break before
            return new TextEdit(new TextRange(new TextPosition(from - 1, doc.Lines[from - 1].Length), new TextPosition(count - 1, lastLen)), "");
        return new TextEdit(new TextRange(new TextPosition(from, 0), new TextPosition(count - 1, lastLen)), string.Join('\n', lines));
    }
}
