using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.Panels.Org.Engine;

/// <summary>
/// A capture template (org's <c>org-capture-templates</c>, as a setting): a key and a description for the picker, the entry's text with org's
/// <c>%</c> escapes, and where it goes: <see cref="File"/> (relative to <c>org.directory</c>; the notes file when null), under the heading
/// <see cref="Headline"/> of it (created when missing), or in a year / month / day <see cref="DateTree"/>; otherwise at the end of the file.
/// </summary>
public sealed record OrgCaptureTemplate(string Key, string Description, string Template, string? File = null, string? Headline = null, bool DateTree = false)
{
    /// <summary>What Org: Capture offers when <c>org.capture.templates</c> is empty.</summary>
    public static IReadOnlyList<OrgCaptureTemplate> Defaults { get; } =
    [
        new("t", "Task", "* TODO %^{Task}\n%U"),
        new("n", "Note", "* %^{Title}\n%U\n%?"),
        new("e", "Event", "* %^{What}\n%^T"),
        new("j", "Journal", "* %<%H:%M> %^{Title}\n%?", File: "journal.org", DateTree: true),
    ];
}

/// <summary>What expanding a template asks for: text (with a default), a date (org's syntax), tags. Null answers cancel the capture.</summary>
public interface IOrgCapturePrompts
{
    Task<string?> TextAsync(string prompt, string? defaultValue, IReadOnlyList<string> options);
    Task<OrgTimestamp?> DateAsync(string prompt, bool withTime);
    Task<IReadOnlyList<string>?> TagsAsync();
}

/// <summary>Where the capture was made from: the time, the agenda's day (if any), the active file and line, the selection.</summary>
public sealed record OrgCaptureContext(DateTime Now, DateOnly? Day = null, string? FilePath = null, int? Line = null, string? Selection = null);

/// <summary>An expanded template: the entry's text (ending with a line break) and where <c>%?</c> put the cursor (an offset in it), if anywhere.</summary>
public sealed record OrgCaptureEntry(string Text, int? Cursor);

/// <summary>
/// Org capture: expands a template's escapes (<c>%t %T</c> today or the agenda's day, <c>%u %U</c> now, <c>%^t %^T %^u %^U</c> a date asked for,
/// <c>%^{Prompt}</c> / <c>%^{Prompt|default|other}</c> a value asked for, <c>%^g</c> tags, <c>%a</c> a link to where the capture was made,
/// <c>%i</c> the selection, <c>%f</c> / <c>%F</c> the file's name / path, <c>%&lt;%Y-%m-%d %H:%M&gt;</c> the time in a format, <c>%?</c> where
/// the cursor goes, <c>%%</c> a percent sign) and puts the entry in its target file.
/// </summary>
public static class OrgCapture
{
    private static readonly Regex HeadingRe = new(@"^(\*+)[ \t]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static async Task<OrgCaptureEntry?> ExpandAsync(string template, OrgCaptureContext context, IOrgCapturePrompts prompts)
    {
        var sb = new StringBuilder();
        int? cursor = null;
        var day = context.Day ?? DateOnly.FromDateTime(context.Now);
        var time = context.Day is null ? new TimeOnly(context.Now.Hour, context.Now.Minute) : (TimeOnly?)null;
        var t = template.Replace("\r\n", "\n", StringComparison.Ordinal);
        for (var i = 0; i < t.Length; i++)
        {
            var c = t[i];
            if (c != '%' || i + 1 >= t.Length) { sb.Append(c); continue; }
            var n = t[i + 1];
            switch (n)
            {
                case '%': sb.Append('%'); i++; break;
                case '?': cursor ??= sb.Length; i++; break;
                case 't': sb.Append(new OrgTimestamp(true, day)); i++; break;
                case 'T': sb.Append(new OrgTimestamp(true, day, time)); i++; break;
                // %u / %U stamp when the entry was made; %t / %T the day it is for (the agenda's day when captured from it)
                case 'u': sb.Append(new OrgTimestamp(false, DateOnly.FromDateTime(context.Now))); i++; break;
                case 'U': sb.Append(OrgTimestamp.InactiveAt(context.Now)); i++; break;
                case 'a':
                    if (context.FilePath is { } path) sb.Append("[[file:").Append(path).Append(context.Line is { } l ? "::" + (l + 1).ToString(CultureInfo.InvariantCulture) : "").Append("]]");
                    i++;
                    break;
                case 'i': sb.Append(context.Selection ?? ""); i++; break;
                case 'f': sb.Append(context.FilePath is null ? "" : Path.GetFileName(context.FilePath)); i++; break;
                case 'F': sb.Append(context.FilePath ?? ""); i++; break;
                case 'n': sb.Append(Environment.UserName); i++; break;
                case '<':
                {
                    var close = t.IndexOf('>', i + 2);
                    if (close < 0) { sb.Append(c); break; }
                    sb.Append(StrFTime(t[(i + 2)..close], context.Now));
                    i = close;
                    break;
                }
                case '^':
                {
                    if (i + 2 >= t.Length) { sb.Append(c); break; }
                    var k = t[i + 2];
                    if (k == '{')
                    {
                        var close = t.IndexOf('}', i + 3);
                        if (close < 0) { sb.Append(c); break; }
                        var spec = t[(i + 3)..close].Split('|');
                        var answer = await prompts.TextAsync(spec[0], spec.Length > 1 ? spec[1] : null, spec.Skip(1).ToArray()).ConfigureAwait(false);
                        if (answer is null) return null;
                        sb.Append(answer);
                        i = close;
                    }
                    else if (k is 't' or 'T' or 'u' or 'U')
                    {
                        var date = await prompts.DateAsync(k is 't' or 'u' ? "Date" : "Date and time", withTime: k is 'T' or 'U').ConfigureAwait(false);
                        if (date is null) return null;
                        sb.Append(date with { Active = k is 't' or 'T', Start = k is 't' or 'u' ? null : date.Start, End = k is 't' or 'u' ? null : date.End });
                        i += 2;
                    }
                    else if (k is 'g' or 'G')
                    {
                        var tags = await prompts.TagsAsync().ConfigureAwait(false);
                        if (tags is null) return null;
                        if (tags.Count > 0) sb.Append(':').Append(string.Join(':', tags)).Append(':');
                        i += 2;
                    }
                    else sb.Append(c);
                    break;
                }
                default: sb.Append(c); break;
            }
        }
        var raw = sb.ToString();
        string text;
        if (cursor is { } at && at >= raw.TrimEnd('\n').Length && raw.EndsWith('\n'))
        {
            // %? on a line of its own at the end: keep that empty line to write on
            text = raw.TrimEnd('\n') + "\n\n";
            cursor = text.Length - 1;
        }
        else text = raw.TrimEnd('\n') + "\n";
        if (cursor > text.Length - 1) cursor = text.Length - 1;
        return new OrgCaptureEntry(text, cursor);
    }

    /// <summary>The C library's time format (what org's <c>%&lt;...&gt;</c> takes): <c>%Y %y %m %d %e %H %M %S %a %A %b %B %j %%</c>.</summary>
    public static string StrFTime(string format, DateTime at)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i + 1 >= format.Length) { sb.Append(format[i]); continue; }
            sb.Append(format[++i] switch
            {
                'Y' => at.ToString("yyyy", inv),
                'y' => at.ToString("yy", inv),
                'm' => at.ToString("MM", inv),
                'd' => at.ToString("dd", inv),
                'e' => at.Day.ToString(inv).PadLeft(2),
                'H' => at.ToString("HH", inv),
                'M' => at.ToString("mm", inv),
                'S' => at.ToString("ss", inv),
                'a' => at.ToString("ddd", inv),
                'A' => at.ToString("dddd", inv),
                'b' => at.ToString("MMM", inv),
                'B' => at.ToString("MMMM", inv),
                'j' => at.DayOfYear.ToString("000", inv),
                '%' => "%",
                var other => "%" + other,
            });
        }
        return sb.ToString();
    }

    /// <summary>
    /// Puts an expanded entry in the target document: as the last child of the template's heading (created at the end of the file when
    /// missing), in the date tree under today's (or the agenda day's) <c>* 2026</c> / <c>** 2026-11 November</c> / <c>*** 2026-11-03 Tuesday</c>
    /// (the missing parts created in date order), or at the end at the top level. The entry's headings take the level they get there. The caret
    /// goes where <c>%?</c> was, else to the end of the entry.
    /// </summary>
    public static OrgEditResult Insert(OrgDocument doc, OrgCaptureTemplate template, OrgCaptureEntry entry, DateOnly day)
    {
        var lines = OrgDocument.SplitLines(entry.Text).ToList();
        if (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var entryLevel = lines.Count > 0 && HeadingRe.Match(lines[0]) is { Success: true } m ? m.Groups[1].Length : 1;

        List<string> prefix = []; // headings to create above the entry
        OrgHeading? parent = null;
        int at;
        if (template.DateTree)
        {
            var inv = CultureInfo.InvariantCulture;
            string[] path = [day.ToString("yyyy", inv), day.ToString("yyyy-MM MMMM", inv), day.ToString("yyyy-MM-dd dddd", inv)];
            (parent, at, prefix) = DateTree(doc, path);
        }
        else if (template.Headline is { Length: > 0 } headline)
        {
            parent = doc.Headings.FirstOrDefault(h => h.Title == headline);
            at = parent is null ? doc.ContentLineCount : InsertionLine(doc, parent);
            if (parent is null) prefix = ["* " + headline];
        }
        else at = doc.ContentLineCount;

        var level = (parent?.Level ?? 0) + prefix.Count + 1;
        var block = new List<string>(prefix);
        var entryStart = block.Count;
        block.AddRange(OrgRefile.Relevel(lines, level - entryLevel));

        // the caret: where %? was (its line and column in the releveled entry), else the end of the entry
        var caretLine = at + block.Count - 1;
        var caretColumn = block[^1].Length;
        if (entry.Cursor is { } offset)
        {
            var before = entry.Text[..Math.Min(offset, entry.Text.Length)];
            var lineInEntry = before.Count(ch => ch == '\n');
            var column = before.Length - (before.LastIndexOf('\n') + 1);
            if (lineInEntry < lines.Count)
            {
                // a heading line moved by the releveling: so does its column
                column += block[entryStart + lineInEntry].Length - lines[lineInEntry].Length;
                caretLine = at + entryStart + lineInEntry;
                caretColumn = Math.Clamp(column, 0, block[entryStart + lineInEntry].Length);
            }
        }
        return new OrgEditResult([OrgEdits.ReplaceLines(doc, at, at, block)], new Core.Text.TextPosition(caretLine, caretColumn));
    }

    private static int InsertionLine(OrgDocument doc, OrgHeading parent)
    {
        var at = parent.SubtreeEnd;
        while (at > parent.Line + 1 && string.IsNullOrWhiteSpace(doc.Lines[at - 1])) at--;
        return at;
    }

    // the deepest existing heading of the path, where the rest goes (in title order among its siblings), and the headings still to create
    private static (OrgHeading? Parent, int At, List<string> Create) DateTree(OrgDocument doc, string[] path)
    {
        OrgHeading? parent = null;
        for (var depth = 0; depth < path.Length; depth++)
        {
            var level = depth + 1;
            var siblings = doc.Headings.Where(h => h.Level == level && ReferenceEquals(h.Parent, parent)).ToList();
            var found = siblings.FirstOrDefault(h => h.Title == path[depth]);
            if (found is not null) { parent = found; continue; }
            // missing from here on: before the first sibling that sorts after it, else as the last child
            var after = siblings.FirstOrDefault(h => string.CompareOrdinal(h.Title, path[depth]) > 0);
            var at = after?.Line ?? (parent is null ? doc.ContentLineCount : InsertionLine(doc, parent));
            var create = path.Skip(depth).Select((title, i) => new string('*', level + i) + " " + title).ToList();
            return (parent, at, create);
        }
        return (parent, InsertionLine(doc, parent!), []);
    }
}
