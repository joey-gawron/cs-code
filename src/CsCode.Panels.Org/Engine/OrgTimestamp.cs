using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.Panels.Org.Engine;

/// <summary>The unit of a repeater or warning period: <c>h</c>, <c>d</c>, <c>w</c>, <c>m</c>, <c>y</c>.</summary>
public enum OrgTimeUnit { Hour, Day, Week, Month, Year }

/// <summary>How a repeater moves its timestamp when the entry is marked done (org's <c>+</c>, <c>++</c>, <c>.+</c>).</summary>
public enum OrgRepeatKind
{
    /// <summary><c>+1w</c>: one interval later, even if that is still in the past.</summary>
    Cumulate,
    /// <summary><c>++1w</c>: as many intervals as it takes to land in the future (keeps the weekday).</summary>
    CatchUp,
    /// <summary><c>.+1w</c>: one interval after the day it was marked done.</summary>
    Restart,
}

/// <summary>An interval such as <c>3d</c> or <c>2w</c>.</summary>
public readonly record struct OrgInterval(int Value, OrgTimeUnit Unit)
{
    public static string UnitLetter(OrgTimeUnit unit) => unit switch
    {
        OrgTimeUnit.Hour => "h",
        OrgTimeUnit.Day => "d",
        OrgTimeUnit.Week => "w",
        OrgTimeUnit.Month => "m",
        _ => "y",
    };

    public static bool TryParseUnit(char c, out OrgTimeUnit unit)
    {
        unit = c switch { 'h' => OrgTimeUnit.Hour, 'd' => OrgTimeUnit.Day, 'w' => OrgTimeUnit.Week, 'm' => OrgTimeUnit.Month, 'y' => OrgTimeUnit.Year, _ => (OrgTimeUnit)(-1) };
        return (int)unit >= 0;
    }

    /// <summary>The date this interval (times <paramref name="count"/>) after <paramref name="from"/>; hours count as whole days only past 24.</summary>
    public DateOnly AddTo(DateOnly from, int count = 1) => Unit switch
    {
        OrgTimeUnit.Hour => from.AddDays(Value * count / 24),
        OrgTimeUnit.Day => from.AddDays(Value * count),
        OrgTimeUnit.Week => from.AddDays(7 * Value * count),
        OrgTimeUnit.Month => from.AddMonths(Value * count),
        _ => from.AddYears(Value * count),
    };

    /// <summary>The interval in days (months as 30, years as 365): what a warning period means.</summary>
    public int ApproximateDays => Unit switch
    {
        OrgTimeUnit.Hour => Value / 24,
        OrgTimeUnit.Day => Value,
        OrgTimeUnit.Week => 7 * Value,
        OrgTimeUnit.Month => 30 * Value,
        _ => 365 * Value,
    };

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture) + UnitLetter(Unit);
}

/// <summary>A repeater cookie: <c>+1w</c>, <c>++2d</c>, <c>.+1m</c>.</summary>
public readonly record struct OrgRepeater(OrgRepeatKind Kind, OrgInterval Interval)
{
    public override string ToString() => (Kind switch { OrgRepeatKind.CatchUp => "++", OrgRepeatKind.Restart => ".+", _ => "+" }) + Interval;
}

/// <summary>
/// An org timestamp: <c>&lt;2026-11-05 Thu&gt;</c> (active: shows in the agenda), <c>[2026-11-05 Thu 10:00]</c> (inactive), with an optional time
/// or time range (<c>10:00-11:30</c>), repeater (<c>+1w</c>) and warning period (<c>-3d</c>), and the end date of a date range
/// (<c>&lt;2026-11-05 Thu&gt;--&lt;2026-11-07 Sat&gt;</c>).
/// </summary>
public sealed record OrgTimestamp(bool Active, DateOnly Date, TimeOnly? Start = null, TimeOnly? End = null, OrgRepeater? Repeater = null, OrgInterval? Warning = null, DateOnly? EndDate = null)
{
    // <2026-11-05 Thu 10:00-11:30 +1w -3d>; the day name is any run of letters (org writes the locale's), the cookies may come in either order
    private static readonly Regex One = new(
        @"(?<open>[<\[])(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})(?:\s+(?<dn>[^\s\d>\]+\-.]+))?(?:\s+(?<h1>\d{1,2}):(?<m1>\d{2})(?:-(?<h2>\d{1,2}):(?<m2>\d{2}))?)?(?<cookies>(?:\s+(?:\.\+|\+\+|\+|--?)\d+[hdwmy])*)\s*(?<close>[>\]])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Cookie = new(@"(?<sign>\.\+|\+\+|\+|--|-)(?<n>\d+)(?<u>[hdwmy])", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Every timestamp in <paramref name="text"/> with its column and length (a range <c>&lt;a&gt;--&lt;b&gt;</c> is one).</summary>
    public static IEnumerable<(OrgTimestamp Timestamp, int Column, int Length)> FindAll(string text)
    {
        if (text.IndexOf('<') < 0 && text.IndexOf('[') < 0) yield break;
        var at = 0;
        while (at < text.Length)
        {
            var m = One.Match(text, at);
            if (!m.Success) yield break;
            var ts = FromMatch(m);
            var length = m.Length;
            if (ts is not null)
            {
                // a date range: <a>--<b> (same kind of bracket)
                var after = m.Index + m.Length;
                if (after + 2 < text.Length && text[after] == '-' && text[after + 1] == '-')
                {
                    var n = One.Match(text, after + 2);
                    if (n.Success && n.Index == after + 2 && FromMatch(n) is { } end && end.Active == ts.Active)
                    {
                        ts = ts with { EndDate = end.Date };
                        length = n.Index + n.Length - m.Index;
                    }
                }
                yield return (ts, m.Index, length);
            }
            at = m.Index + Math.Max(1, length);
        }
    }

    /// <summary>The timestamp <paramref name="text"/> is (surrounding blanks allowed), or null.</summary>
    public static OrgTimestamp? Parse(string text)
    {
        var trimmed = text.Trim();
        foreach (var (ts, column, length) in FindAll(trimmed))
            return column == 0 && length == trimmed.Length ? ts : null;
        return null;
    }

    private static OrgTimestamp? FromMatch(Match m)
    {
        var open = m.Groups["open"].Value[0];
        var close = m.Groups["close"].Value[0];
        if ((open == '<') != (close == '>')) return null;
        if (!int.TryParse(m.Groups["y"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var y)
            || !int.TryParse(m.Groups["mo"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var mo)
            || !int.TryParse(m.Groups["d"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var d)
            || mo is < 1 or > 12 || d < 1 || y < 1 || d > DateTime.DaysInMonth(y, mo))
            return null;
        TimeOnly? start = null, end = null;
        if (m.Groups["h1"].Success)
        {
            start = Time(m.Groups["h1"].Value, m.Groups["m1"].Value);
            if (start is null) return null;
            if (m.Groups["h2"].Success) end = Time(m.Groups["h2"].Value, m.Groups["m2"].Value);
        }
        OrgRepeater? repeater = null;
        OrgInterval? warning = null;
        foreach (Match c in Cookie.Matches(m.Groups["cookies"].Value))
        {
            if (!OrgInterval.TryParseUnit(c.Groups["u"].Value[0], out var unit)) continue;
            var interval = new OrgInterval(int.Parse(c.Groups["n"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture), unit);
            switch (c.Groups["sign"].Value)
            {
                case "+": repeater ??= new OrgRepeater(OrgRepeatKind.Cumulate, interval); break;
                case "++": repeater ??= new OrgRepeater(OrgRepeatKind.CatchUp, interval); break;
                case ".+": repeater ??= new OrgRepeater(OrgRepeatKind.Restart, interval); break;
                default: warning ??= interval; break; // - and -- (the first deadline warning only)
            }
        }
        return new OrgTimestamp(open == '<', new DateOnly(y, mo, d), start, end, repeater, warning);
    }

    private static TimeOnly? Time(string h, string m)
    {
        var hour = int.Parse(h, CultureInfo.InvariantCulture);
        var minute = int.Parse(m, CultureInfo.InvariantCulture);
        // 24:00 is how org spells the end of a day; read it as the last minute
        if (hour == 24 && minute == 0) return new TimeOnly(23, 59);
        return hour is >= 0 and < 24 && minute is >= 0 and < 60 ? new TimeOnly(hour, minute) : null;
    }

    /// <summary>Org's spelling: <c>&lt;2026-11-05 Thu 10:00-11:30 +1w -3d&gt;</c> (English day names, as org writes them in the C locale).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        Append(Date, Start, End, cookies: true);
        if (EndDate is { } endDate && endDate != Date)
        {
            sb.Append("--");
            Append(endDate, null, null, cookies: false);
        }
        return sb.ToString();

        void Append(DateOnly date, TimeOnly? start, TimeOnly? end, bool cookies)
        {
            sb.Append(Active ? '<' : '[').Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(' ').Append(DayName(date));
            if (start is { } t)
            {
                sb.Append(' ').Append(t.ToString("HH:mm", CultureInfo.InvariantCulture));
                if (end is { } e) sb.Append('-').Append(e.ToString("HH:mm", CultureInfo.InvariantCulture));
            }
            if (cookies)
            {
                if (Repeater is { } r) sb.Append(' ').Append(r);
                if (Warning is { } w) sb.Append(" -").Append(w);
            }
            sb.Append(Active ? '>' : ']');
        }
    }

    public static string DayName(DateOnly date) => date.DayOfWeek.ToString()[..3];

    /// <summary>The time part as the agenda shows it: <c>10:00</c>, <c>10:00-11:30</c>, or empty.</summary>
    public string TimeText => Start is { } s
        ? s.ToString("HH:mm", CultureInfo.InvariantCulture) + (End is { } e ? "-" + e.ToString("HH:mm", CultureInfo.InvariantCulture) : "")
        : "";

    /// <summary>
    /// The days in [<paramref name="from"/>, <paramref name="to"/>] this timestamp falls on: its date, each day of a date range, and each
    /// repetition at or after its date (a repeater repeats forward only, as in org's agenda).
    /// </summary>
    public IEnumerable<DateOnly> OccurrencesIn(DateOnly from, DateOnly to)
    {
        if (to < from) yield break;
        if (EndDate is { } end && end > Date)
        {
            for (var d = Max(Date, from); d <= end && d <= to; d = d.AddDays(1)) yield return d;
            yield break;
        }
        if (Repeater is not { } r || r.Interval.Value <= 0)
        {
            if (Date >= from && Date <= to) yield return Date;
            yield break;
        }
        // jump close to the window, then step (bounded: a 1-day repeater over a long window still yields at most the window's days)
        var step = r.Interval;
        var k = 0;
        if (from > Date && step.ApproximateDays > 0) k = Math.Max(0, (from.DayNumber - Date.DayNumber) / step.ApproximateDays - 1);
        for (var guard = 0; guard < 4000; guard++, k++)
        {
            var d = step.AddTo(Date, k);
            if (d > to) yield break;
            if (d >= from) yield return d;
        }
    }

    /// <summary>
    /// The timestamp after the entry is marked done on <paramref name="today"/> (org's repeat): <c>+</c> moves one interval, <c>++</c> as many as it
    /// takes to pass today, <c>.+</c> one interval from today. Without a repeater, the timestamp itself.
    /// </summary>
    public OrgTimestamp Repeat(DateOnly today)
    {
        if (Repeater is not { } r || r.Interval.Value <= 0) return this;
        var next = r.Kind switch
        {
            OrgRepeatKind.Restart => r.Interval.AddTo(today),
            OrgRepeatKind.CatchUp => CatchUp(),
            _ => r.Interval.AddTo(Date),
        };
        var shift = next.DayNumber - Date.DayNumber;
        return this with { Date = next, EndDate = EndDate?.AddDays(shift) };

        DateOnly CatchUp()
        {
            var d = r.Interval.AddTo(Date);
            for (var k = 2; d <= today && k < 100_000; k++) d = r.Interval.AddTo(Date, k);
            return d;
        }
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
