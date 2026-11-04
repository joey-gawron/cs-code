namespace CsCode.Panels.Org.Engine;

/// <summary>Why an entry is on a day of the agenda (the text org puts before it: <c>Scheduled:</c>, <c>Sched. 3x:</c>, <c>In 4 d.:</c>, ...).</summary>
public enum OrgAgendaKind
{
    /// <summary>A deadline that has passed, shown on today: <c>2 d. ago:</c>.</summary>
    DeadlineOverdue,
    /// <summary>The deadline's own day: <c>Deadline:</c>.</summary>
    Deadline,
    /// <summary>A scheduled entry not done since an earlier day, shown on today: <c>Sched. 3x:</c>.</summary>
    ScheduledOverdue,
    /// <summary>The scheduled day: <c>Scheduled:</c>.</summary>
    Scheduled,
    /// <summary>A deadline coming within its warning period, shown on today: <c>In 4 d.:</c>.</summary>
    DeadlineUpcoming,
    /// <summary>An active timestamp in the heading or its text (an appointment, an event).</summary>
    Timestamp,
    /// <summary>An entry of the TODO list (not tied to a day).</summary>
    Todo,
}

/// <summary>One line of the agenda: an entry of a document, why it is there and when.</summary>
public sealed record OrgAgendaItem(OrgAgendaKind Kind, DateOnly Day, OrgDocument Document, OrgHeading Heading, OrgTimestamp? Timestamp, int Days, int Line)
{
    /// <summary>The time the agenda sorts and shows it by, when it has one (<c>10:00-11:00</c>).</summary>
    public TimeOnly? Time => Kind is OrgAgendaKind.DeadlineOverdue or OrgAgendaKind.ScheduledOverdue or OrgAgendaKind.DeadlineUpcoming ? null : Timestamp?.Start;

    public string TimeText => Time is null ? "" : Timestamp!.TimeText;

    /// <summary>Org's prefix: <c>Scheduled:</c>, <c>Sched. 3x:</c>, <c>Deadline:</c>, <c>In 4 d.:</c>, <c>2 d. ago:</c>; a range's day <c>(2/3)</c>.</summary>
    public string Extra => Kind switch
    {
        OrgAgendaKind.Scheduled => "Scheduled:",
        OrgAgendaKind.ScheduledOverdue => $"Sched.{Days,2}x:",
        OrgAgendaKind.Deadline => "Deadline:",
        OrgAgendaKind.DeadlineUpcoming => $"In {Days,2} d.:",
        OrgAgendaKind.DeadlineOverdue => $"{Days,2} d. ago:",
        OrgAgendaKind.Timestamp when Timestamp?.EndDate is { } end && end > Timestamp.Date =>
            $"({Day.DayNumber - Timestamp.Date.DayNumber + 1}/{end.DayNumber - Timestamp.Date.DayNumber + 1}):",
        _ => "",
    };

    /// <summary>Overdue deadlines and scheduled entries, and deadlines due today: what the agenda colours as urgent.</summary>
    public bool IsUrgent => !Heading.IsDone && Kind is OrgAgendaKind.DeadlineOverdue or OrgAgendaKind.Deadline or OrgAgendaKind.ScheduledOverdue;
}

/// <summary>A day of the agenda and its entries, in the order they are shown.</summary>
public sealed record OrgAgendaDay(DateOnly Date, IReadOnlyList<OrgAgendaItem> Items);

/// <param name="DeadlineWarningDays">How many days before a deadline it shows on today (org's <c>org-deadline-warning-days</c>); a deadline's own <c>-3d</c> wins.</param>
/// <param name="ShowDone">Whether entries already done are listed on their day (org lists them).</param>
/// <param name="HabitsOnlyToday">Whether habits (<c>:STYLE: habit</c>) are listed on today only, not on their repetitions after it (org's default).</param>
public sealed record OrgAgendaOptions(int DeadlineWarningDays = 14, bool ShowDone = true, bool HabitsOnlyToday = true);

/// <summary>
/// Builds org's agenda views from parsed documents: <see cref="Build"/> (the day or week agenda: scheduled entries, deadlines with their
/// warnings, overdue entries carried to today, appointments, repeaters) and <see cref="TodoList"/> (every entry that is still to do).
/// Pure functions of their inputs: the documents are immutable snapshots.
/// </summary>
public static class OrgAgenda
{
    public static IReadOnlyList<OrgAgendaDay> Build(IEnumerable<OrgDocument> documents, DateOnly from, DateOnly to, DateOnly today, OrgAgendaOptions? options = null)
    {
        options ??= new OrgAgendaOptions();
        var byDay = new SortedDictionary<DateOnly, List<OrgAgendaItem>>();
        for (var d = from; d <= to; d = d.AddDays(1)) byDay[d] = [];
        var todayShown = today >= from && today <= to;

        foreach (var doc in documents)
            foreach (var h in doc.Headings)
            {
                if (h.IsDone && !options.ShowDone) continue;
                if (IsArchived(h)) continue; // org-agenda-skip-archived-trees
                var habit = options.HabitsOnlyToday && OrgHabit.IsHabit(h);
                var line = h.PlanningLine ?? h.Line;
                if (h.Scheduled is { Active: true } scheduled)
                {
                    foreach (var d in scheduled.OccurrencesIn(from, to))
                    {
                        // on its day, and on each repetition from today on (as org: a past day keeps it, today adds Sched. Nx); a habit on today only
                        if (d < today && d != scheduled.Date) continue;
                        if (habit && d > today) continue;
                        if (d == today && todayShown && !h.IsDone && scheduled.Date < today) continue; // a repetition on today of an overdue entry: Sched. Nx below
                        byDay[d].Add(new OrgAgendaItem(OrgAgendaKind.Scheduled, d, doc, h, scheduled, 0, line));
                    }
                    if (todayShown && !h.IsDone && scheduled.Date < today)
                        byDay[today].Add(new OrgAgendaItem(OrgAgendaKind.ScheduledOverdue, today, doc, h, scheduled, today.DayNumber - scheduled.Date.DayNumber, line));
                }
                if (h.Deadline is { Active: true } deadline)
                {
                    foreach (var d in deadline.OccurrencesIn(from, to))
                    {
                        if (d < today && d != deadline.Date) continue;
                        if (d == today && todayShown && !h.IsDone && deadline.Date < today) continue; // shown as overdue below
                        byDay[d].Add(new OrgAgendaItem(OrgAgendaKind.Deadline, d, doc, h, deadline, 0, line));
                    }
                    if (todayShown && !h.IsDone)
                    {
                        var days = deadline.Date.DayNumber - today.DayNumber;
                        var warning = deadline.Warning?.ApproximateDays ?? options.DeadlineWarningDays;
                        if (days < 0) byDay[today].Add(new OrgAgendaItem(OrgAgendaKind.DeadlineOverdue, today, doc, h, deadline, -days, line));
                        else if (days > 0 && days <= warning) byDay[today].Add(new OrgAgendaItem(OrgAgendaKind.DeadlineUpcoming, today, doc, h, deadline, days, line));
                    }
                }
                foreach (var t in h.Timestamps)
                    foreach (var d in t.Timestamp.OccurrencesIn(from, to))
                        byDay[d].Add(new OrgAgendaItem(OrgAgendaKind.Timestamp, d, doc, h, t.Timestamp, 0, t.Line));
            }

        return [.. byDay.Select(kv => new OrgAgendaDay(kv.Key, Sort(kv.Value)))];
    }

    /// <summary>
    /// Org's sort: entries with a time first, by time; then the rest by urgency (overdue and due deadlines, overdue and scheduled entries,
    /// coming deadlines, appointments), priority (<c>[#A]</c> first; none counts as <c>B</c>) and the order of the files.
    /// </summary>
    private static List<OrgAgendaItem> Sort(List<OrgAgendaItem> items)
    {
        // one entry with both a scheduled day and an appointment on the same day is listed once per reason, as in org
        return [.. items
            .Select((item, i) => (item, i))
            .OrderBy(x => x.item.Time is null ? 1 : 0)
            .ThenBy(x => x.item.Time ?? TimeOnly.MinValue)
            .ThenBy(x => (int)x.item.Kind)
            .ThenBy(x => x.item.Kind == OrgAgendaKind.DeadlineUpcoming ? x.item.Days : -x.item.Days)
            .ThenBy(x => PriorityRank(x.item.Heading.Priority))
            .ThenBy(x => x.item.Heading.IsDone ? 1 : 0)
            .ThenBy(x => x.i)
            .Select(x => x.item)];
    }

    /// <summary>In an archived tree: tagged <c>:ARCHIVE:</c> (itself or an ancestor).</summary>
    public static bool IsArchived(OrgHeading h) => h.AllTags.Contains("ARCHIVE");

    internal static int PriorityRank(char? priority) => priority switch
    {
        null => 'B' - 'A',
        >= 'A' and <= 'Z' => priority.Value - 'A',
        _ => priority.Value - '0' + 26,
    };

    /// <summary>Every heading with a to-do keyword (not done), file by file in document order; <paramref name="keyword"/> keeps one state only.</summary>
    public static IReadOnlyList<OrgAgendaItem> TodoList(IEnumerable<OrgDocument> documents, DateOnly today, string? keyword = null)
    {
        var items = new List<OrgAgendaItem>();
        foreach (var doc in documents)
            foreach (var h in doc.Headings)
                if (h.IsTodo && !IsArchived(h) && (keyword is null || h.Keyword == keyword))
                    items.Add(new OrgAgendaItem(OrgAgendaKind.Todo, today, doc, h, h.Deadline ?? h.Scheduled, 0, h.Line));
        return items;
    }
}
