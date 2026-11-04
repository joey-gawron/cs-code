using System.Globalization;
using System.Text.RegularExpressions;
using CsCode.Core.Text;

namespace CsCode.Panels.Org.Engine;

/// <summary>
/// Org's clock: <c>CLOCK: [2026-11-03 Tue 14:05]</c> lines in an entry's <c>:LOGBOOK:</c> (a running clock), closed as
/// <c>CLOCK: [start]--[end] =&gt;  1:05</c>; the time clocked in an entry or a subtree; and org's clock table, a dynamic block
/// (<c>#+BEGIN: clocktable :scope file :maxlevel 2</c> ... <c>#+END:</c>) with the time of each heading.
/// </summary>
public static class OrgClock
{
    private static readonly Regex TableBeginRe = new(@"^[ \t]*#\+BEGIN:[ \t]+clocktable\b(.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TableEndRe = new(@"^[ \t]*#\+END:?[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MaxLevelRe = new(@":maxlevel[ \t]+(\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Org's duration: <c>1:05</c>, <c>12:30</c>.</summary>
    public static string Format(TimeSpan t)
    {
        var minutes = (long)Math.Max(0, Math.Round(t.TotalMinutes));
        return $"{minutes / 60}:{minutes % 60:00}";
    }

    /// <summary>Starts the clock on the entry: <c>CLOCK: [now]</c> at the top of its logbook (created after its planning line and properties).</summary>
    public static OrgEditResult ClockIn(OrgDocument doc, OrgHeading h, DateTime now)
    {
        var r = OrgEdits.AddToLogbook(doc, h, ["CLOCK: " + OrgTimestamp.InactiveAt(now)]);
        return r with { Message = $"Clock started on \"{h.Title}\"" };
    }

    /// <summary>Stops the entry's running clock: <c>CLOCK: [start]--[now] =&gt;  1:05</c>. Null when no clock runs there.</summary>
    public static OrgEditResult? ClockOut(OrgDocument doc, OrgHeading h, DateTime now)
    {
        if (h.Clocks.FirstOrDefault(c => c.IsRunning) is not { } open) return null;
        var line = doc.Lines[open.Line];
        var end = now < open.Start ? open.Start : now;
        var duration = new DateTime(end.Year, end.Month, end.Day, end.Hour, end.Minute, 0) - new DateTime(open.Start.Year, open.Start.Month, open.Start.Day, open.Start.Hour, open.Start.Minute, 0);
        var text = OrgEdits.LeadingBlanks(line) + $"CLOCK: {OrgTimestamp.InactiveAt(open.Start)}--{OrgTimestamp.InactiveAt(end)} => {Format(duration),5}";
        return new OrgEditResult([new TextEdit(new TextRange(new TextPosition(open.Line, 0), new TextPosition(open.Line, line.Length)), text)],
            Message: $"Clock stopped on \"{h.Title}\" after {Format(duration)}");
    }

    /// <summary>Removes the entry's running clock line (and its logbook when nothing else is in it). Null when no clock runs there.</summary>
    public static OrgEditResult? Cancel(OrgDocument doc, OrgHeading h)
    {
        if (h.Clocks.FirstOrDefault(c => c.IsRunning) is not { } open) return null;
        var (from, to) = h.LogbookLine == open.Line - 1 && h.LogbookEndLine == open.Line + 1 ? (open.Line - 1, open.Line + 2) : (open.Line, open.Line + 1);
        return new OrgEditResult([OrgEdits.ReplaceLines(doc, from, to, [])], Message: "Clock canceled");
    }

    /// <summary>The time clocked in the entry (and its subheadings when <paramref name="subtree"/>), within [<paramref name="from"/>, <paramref name="to"/>) when given; a running clock counts up to <paramref name="now"/>.</summary>
    public static TimeSpan Total(OrgDocument doc, OrgHeading h, DateTime now, bool subtree = true, DateTime? from = null, DateTime? to = null)
    {
        var total = TimeSpan.Zero;
        foreach (var x in subtree ? doc.Headings.Where(x => x.Line >= h.Line && x.Line < h.SubtreeEnd) : [h])
            foreach (var c in x.Clocks)
            {
                var start = from is { } f && c.Start < f ? f : c.Start;
                var end = c.End ?? now;
                if (to is { } t && end > t) end = t;
                if (end > start) total += end - start;
            }
        return total;
    }

    /// <summary>The first running clock in the documents (org runs one at a time).</summary>
    public static (OrgDocument Document, OrgHeading Heading, OrgClockLine Clock)? FindRunning(IEnumerable<OrgDocument> documents)
    {
        foreach (var doc in documents)
            foreach (var h in doc.Headings)
                if (h.Clocks.FirstOrDefault(c => c.IsRunning) is { } open) return (doc, h, open);
        return null;
    }

    // ------------------------------------------------------------------------------------------------ clock table

    /// <summary>The clock table block whose lines contain <paramref name="line"/>: its <c>#+BEGIN:</c> and <c>#+END:</c> lines.</summary>
    public static (int Begin, int End)? TableAt(OrgDocument doc, int line)
    {
        for (var b = Math.Min(line, doc.LineCount - 1); b >= 0; b--)
        {
            if (TableBeginRe.IsMatch(doc.Lines[b]))
            {
                for (var e = b + 1; e < doc.LineCount; e++)
                    if (TableEndRe.IsMatch(doc.Lines[e])) return line <= e ? (b, e) : null;
                return null;
            }
            if (b < line && TableEndRe.IsMatch(doc.Lines[b])) return null;
        }
        return null;
    }

    /// <summary>
    /// Org's C-c C-x C-r: inserts a clock table of the file at <paramref name="line"/>, or (on an existing one) brings it up to date, keeping its
    /// <c>#+BEGIN:</c> line. One row per heading down to <c>:maxlevel</c> (2) with time clocked, its subheadings' time included, as org's table.
    /// </summary>
    public static OrgEditResult Table(OrgDocument doc, int line, DateTime now)
    {
        var existing = TableAt(doc, line);
        var begin = existing is { } t ? doc.Lines[t.Begin] : "#+BEGIN: clocktable :scope file :maxlevel 2";
        var maxLevel = MaxLevelRe.Match(begin) is { Success: true } m ? Math.Clamp(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 1, 9) : 2;
        var lines = new List<string> { begin, "#+CAPTION: Clock summary at " + OrgTimestamp.InactiveAt(now) };
        lines.AddRange(TableRows(doc, maxLevel, now));
        lines.Add("#+END:");
        if (existing is { } x) return new OrgEditResult([OrgEdits.ReplaceLines(doc, x.Begin, x.End + 1, lines)], Message: "Clock table updated");
        var at = Math.Clamp(line, 0, doc.ContentLineCount);
        return new OrgEditResult([OrgEdits.ReplaceLines(doc, at, at, [.. lines, ""])], new TextPosition(at, 0), "Clock table inserted");
    }

    private static List<string> TableRows(OrgDocument doc, int maxLevel, DateTime now)
    {
        var rows = new List<string[]>();
        var total = TimeSpan.Zero;
        foreach (var h in doc.Headings.Where(h => h.Level == 1)) total += Total(doc, h, now);
        foreach (var h in doc.Headings.Where(h => h.Level <= maxLevel))
        {
            var t = Total(doc, h, now);
            if (t <= TimeSpan.Zero) continue;
            var cells = new string[maxLevel + 1];
            cells[0] = (h.Level > 1 ? "\\" + new string('_', h.Level - 1) + "  " : "") + h.Title;
            for (var i = 1; i < cells.Length; i++) cells[i] = "";
            cells[h.Level] = Format(t);
            rows.Add(cells);
        }
        var header = new string[maxLevel + 1];
        header[0] = "Headline";
        header[1] = "Time";
        for (var i = 2; i < header.Length; i++) header[i] = "";
        var totalRow = new string[maxLevel + 1];
        totalRow[0] = "*Total time*";
        totalRow[1] = "*" + Format(total) + "*";
        for (var i = 2; i < totalRow.Length; i++) totalRow[i] = "";
        var all = new List<string[]> { header, totalRow };
        all.AddRange(rows);
        var widths = Enumerable.Range(0, header.Length).Select(c => all.Max(r => r[c].Length)).ToArray();
        string Row(string[] r) => "| " + string.Join(" | ", r.Select((cell, c) => cell.PadRight(widths[c]))) + " |";
        string Rule() => "|-" + string.Join("-+-", widths.Select(w => new string('-', w))) + "-|";
        var lines = new List<string> { Row(header), Rule(), Row(totalRow) };
        if (rows.Count > 0) lines.Add(Rule());
        lines.AddRange(rows.Select(Row));
        return lines;
    }
}
