using System.Globalization;
using System.Text.RegularExpressions;

namespace CsCode.Panels.Org.Engine;

/// <summary>
/// Reads a date the way org's date prompt (<c>org-read-date</c>) does, relative to today:
/// <list type="bullet">
/// <item><c>.</c> or <c>today</c>, <c>tomorrow</c> (<c>tom</c>), <c>yesterday</c>; <c>+3d</c>, <c>+2w</c>, <c>+1m</c>, <c>+1y</c>, <c>-1d</c>, <c>+4</c> (days)</item>
/// <item>a weekday: <c>fri</c>, <c>friday</c> (the next one, today included), <c>+2fri</c> (the second one from today)</item>
/// <item><c>2026-11-20</c>, <c>11/20</c>, <c>11/20/2026</c> (month first, as org), <c>20</c> (that day of this month, else next month),
/// <c>nov 20</c>, <c>20 nov</c>, <c>20 november 2027</c> (the next such day when the year is left out)</item>
/// <item>a time anywhere: <c>14:00</c>, <c>9:30</c>, <c>2pm</c>, <c>10:30am</c>, <c>10:00-11:30</c>, <c>9am-10am</c></item>
/// <item>after a date, a repeater (<c>+1w</c>, <c>++1m</c>, <c>.+2d</c>) and a deadline warning (<c>-3d</c>): <c>fri 10:00 +1w</c>, <c>today -2d</c></item>
/// </list>
/// Empty input means today. The result is an active timestamp (the caller makes it inactive when it needs one).
/// </summary>
public static class OrgDateInput
{
    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    private static readonly string[] Days = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    private static readonly Regex TimeRange = new(@"^(\d{1,2})(?::(\d{2}))?(am|pm)?(?:-(\d{1,2})(?::(\d{2}))?(am|pm)?)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Relative = new(@"^([+-])(\d+)([hdwmy]|[a-z]{3,})?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Cookie = new(@"^(\.\+|\+\+|\+|-)(\d+)([hdwmy])$", RegexOptions.CultureInvariant);
    private static readonly Regex Iso = new(@"^(\d{4})-(\d{1,2})-(\d{1,2})$", RegexOptions.CultureInvariant);
    private static readonly Regex Slashed = new(@"^(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?$", RegexOptions.CultureInvariant);

    /// <summary>The timestamp <paramref name="input"/> means, or null with the reason in <paramref name="error"/>.</summary>
    public static OrgTimestamp? Parse(string? input, DateOnly today, out string? error)
    {
        error = null;
        var tokens = Tokenize(input ?? "");
        DateOnly? date = null;
        TimeOnly? start = null, end = null;
        OrgRepeater? repeater = null;
        OrgInterval? warning = null;
        int? day = null, month = null, year = null;
        var absolute = false; // a full date was given: a day name after it ("2026-11-20 Fri", as org writes them) only names its weekday

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var lower = t.ToLowerInvariant();
            Match m;
            if (lower is "." or "today" or "now") { date = today; continue; }
            if (lower is "tomorrow" or "tom") { date = today.AddDays(1); continue; }
            if (lower is "yesterday") { date = today.AddDays(-1); continue; }
            if ((date is not null || day is not null) && (m = Cookie.Match(lower)).Success && OrgInterval.TryParseUnit(m.Groups[3].Value[0], out var cu))
            {
                // after a date: a repeater, or a warning period
                var interval = new OrgInterval(int.Parse(m.Groups[2].ValueSpan, CultureInfo.InvariantCulture), cu);
                switch (m.Groups[1].Value)
                {
                    case "-": warning = interval; break;
                    case "++": repeater = new OrgRepeater(OrgRepeatKind.CatchUp, interval); break;
                    case ".+": repeater = new OrgRepeater(OrgRepeatKind.Restart, interval); break;
                    default: repeater = new OrgRepeater(OrgRepeatKind.Cumulate, interval); break;
                }
                continue;
            }
            if ((m = Relative.Match(lower)).Success)
            {
                var n = int.Parse(m.Groups[2].ValueSpan, CultureInfo.InvariantCulture) * (m.Groups[1].Value == "-" ? -1 : 1);
                var unit = m.Groups[3].Success ? m.Groups[3].Value : "d";
                if (unit.Length > 1)
                {
                    // +2fri: the second Friday from today
                    if (WeekdayOf(unit) is not { } wd) { error = $"\"{t}\": not a weekday"; return null; }
                    if (n >= 0) date = NextWeekday(today, wd, includeToday: false).AddDays(7 * Math.Max(0, n - 1));
                    else date = NextWeekday(today, wd, includeToday: false).AddDays(-7 * -n); // -1fri: the last Friday before today
                    continue;
                }
                date = unit switch
                {
                    "w" => today.AddDays(7 * n),
                    "m" => today.AddMonths(n),
                    "y" => today.AddYears(n),
                    "h" => today.AddDays(n / 24),
                    _ => today.AddDays(n),
                };
                continue;
            }
            if ((m = Iso.Match(lower)).Success)
            {
                if (!TryDate(int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].ValueSpan, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups[3].ValueSpan, CultureInfo.InvariantCulture), out var d)) { error = $"\"{t}\" is not a date"; return null; }
                date = d;
                absolute = true;
                continue;
            }
            if ((m = Slashed.Match(lower)).Success)
            {
                absolute = true;
                month = int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
                day = int.Parse(m.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
                if (m.Groups[3].Success) year = FullYear(int.Parse(m.Groups[3].ValueSpan, CultureInfo.InvariantCulture));
                continue;
            }
            if (TryTime(lower, out var s, out var e))
            {
                // a bare number of one or two digits is a day of the month, not an hour (org reads "20" as the 20th)
                if (Regex.IsMatch(lower, @"^\d{1,2}$")) { day = int.Parse(lower, CultureInfo.InvariantCulture); continue; }
                start = s;
                end = e;
                continue;
            }
            if (MonthOf(lower) is { } mo) { month = mo; continue; }
            if (WeekdayOf(lower) is { } weekday)
            {
                if (!absolute) date = NextWeekday(today, weekday, includeToday: true);
                continue;
            }
            if (Regex.IsMatch(lower, @"^\d{4}$")) { year = int.Parse(lower, CultureInfo.InvariantCulture); continue; }
            error = $"\"{t}\" is not understood (try: today, tomorrow, +3d, fri, nov 20, 2026-11-20, 14:00)";
            return null;
        }

        if (day is not null || month is not null || year is not null)
        {
            var y = year ?? today.Year;
            var mo = month ?? today.Month;
            var d = day ?? 1;
            if (!TryDate(y, mo, d, out var resolved)) { error = $"{y}-{mo:00}-{d:00} is not a date"; return null; }
            // a day (and month) without a year: the next one, as org (no earlier than today)
            if (year is null && resolved < today)
            {
                if (month is null) { var next = today.AddMonths(1); TryDate(next.Year, next.Month, d, out resolved); }
                else TryDate(y + 1, mo, d, out resolved);
            }
            date = resolved;
        }

        if (end is { } en && start is { } st && en < st) (start, end) = (en, st);
        return new OrgTimestamp(true, date ?? today, start, end, repeater, warning);
    }

    private static List<string> Tokenize(string input)
    {
        // "nov 20, 2026" and "fri,10am": commas are blanks; "9 am" is one time
        var raw = input.Replace(',', ' ').Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
        for (var i = raw.Count - 1; i > 0; i--)
            if (raw[i].Equals("am", StringComparison.OrdinalIgnoreCase) || raw[i].Equals("pm", StringComparison.OrdinalIgnoreCase))
            {
                raw[i - 1] += raw[i];
                raw.RemoveAt(i);
            }
        return raw;
    }

    private static bool TryTime(string text, out TimeOnly? start, out TimeOnly? end)
    {
        start = end = null;
        var m = TimeRange.Match(text);
        if (!m.Success) return false;
        start = Clock(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        if (start is null) return false;
        if (m.Groups[4].Success)
        {
            // "9-11am": the first half takes the second's am/pm
            var ampm = m.Groups[3].Success && m.Groups[3].Length > 0 ? m.Groups[3].Value : m.Groups[6].Value;
            if (!m.Groups[3].Success || m.Groups[3].Length == 0) start = Clock(m.Groups[1].Value, m.Groups[2].Value, ampm);
            end = Clock(m.Groups[4].Value, m.Groups[5].Value, m.Groups[6].Value);
            if (end is null) return false;
        }
        // "14" alone is a day of the month (the caller checks); a time needs minutes or am/pm
        return m.Groups[2].Success || m.Groups[3].Length > 0 || m.Groups[4].Success || Regex.IsMatch(text, @"^\d{1,2}$");
    }

    private static TimeOnly? Clock(string h, string m, string ampm)
    {
        var hour = int.Parse(h, CultureInfo.InvariantCulture);
        var minute = m.Length == 0 ? 0 : int.Parse(m, CultureInfo.InvariantCulture);
        if (ampm.Length > 0)
        {
            if (hour is < 1 or > 12) return null;
            if (ampm.Equals("pm", StringComparison.OrdinalIgnoreCase) && hour != 12) hour += 12;
            if (ampm.Equals("am", StringComparison.OrdinalIgnoreCase) && hour == 12) hour = 0;
        }
        if (hour == 24 && minute == 0) return new TimeOnly(23, 59);
        return hour is >= 0 and < 24 && minute is >= 0 and < 60 ? new TimeOnly(hour, minute) : null;
    }

    private static int? MonthOf(string word)
    {
        if (word.Length < 3) return null;
        for (var i = 0; i < Months.Length; i++)
            if (word.StartsWith(Months[i], StringComparison.Ordinal)
                && CultureInfo.InvariantCulture.DateTimeFormat.MonthNames[i].StartsWith(word, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        return null;
    }

    private static DayOfWeek? WeekdayOf(string word)
    {
        if (word.Length < 2) return null;
        for (var i = 0; i < Days.Length; i++)
        {
            var full = CultureInfo.InvariantCulture.DateTimeFormat.DayNames[i];
            if (word.Length >= 3 && word.StartsWith(Days[i], StringComparison.Ordinal) && full.StartsWith(word, StringComparison.OrdinalIgnoreCase)) return (DayOfWeek)i;
        }
        return null;
    }

    private static DateOnly NextWeekday(DateOnly from, DayOfWeek day, bool includeToday)
    {
        var diff = ((int)day - (int)from.DayOfWeek + 7) % 7;
        if (diff == 0 && !includeToday) diff = 7;
        return from.AddDays(diff);
    }

    private static int FullYear(int y) => y < 100 ? 2000 + y : y;

    private static bool TryDate(int y, int m, int d, out DateOnly date)
    {
        date = default;
        if (y is < 1 or > 9999 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        date = new DateOnly(y, m, d);
        return true;
    }

    /// <summary>How a parsed date reads back to the user: <c>Fri 20 Nov 2026 10:00</c> and its repeater, for prompts and status messages.</summary>
    public static string Describe(OrgTimestamp ts)
    {
        var text = ts.Date.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
        if (ts.TimeText.Length > 0) text += " " + ts.TimeText;
        if (ts.Repeater is { } r) text += ", repeats " + r;
        if (ts.Warning is { } w) text += ", warns " + w + " before";
        return text;
    }
}
