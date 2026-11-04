namespace CsCode.Panels.Org.Engine;

/// <summary>How a day of a habit's consistency graph is coloured (org-habit's faces).</summary>
public enum OrgHabitState
{
    /// <summary>Before the habit was due (blue).</summary>
    Ahead,
    /// <summary>Due, still within its allowed gap (green).</summary>
    OnTime,
    /// <summary>The last day of its allowed gap (yellow).</summary>
    Due,
    /// <summary>Past its allowed gap (red).</summary>
    Overdue,
}

/// <summary>A day of the graph: its colour, whether the habit was done that day, and whether it is today or after.</summary>
public readonly record struct OrgHabitDay(DateOnly Date, OrgHabitState State, bool Done, bool IsToday, bool IsFuture);

/// <summary>
/// Org's habits: an entry with <c>:STYLE: habit</c> and a scheduled timestamp that repeats (<c>.+2d</c>, or <c>.+2d/4d</c> for "every 2 days,
/// at most 4"). Its history is the done states logged in its logbook (marking a repeating entry done logs them). The consistency graph colours
/// each day of a window around today as org-habit does: blue before the habit was due, green while it may be done, yellow on the last day it
/// may be done, red after; a day it was done is marked.
/// </summary>
public sealed class OrgHabit
{
    private OrgHabit(OrgHeading heading, OrgTimestamp scheduled, int minDays, int maxDays, IReadOnlyList<DateOnly> done)
    {
        Heading = heading;
        Scheduled = scheduled;
        MinDays = minDays;
        MaxDays = maxDays;
        Done = done;
    }

    public OrgHeading Heading { get; }
    public OrgTimestamp Scheduled { get; }
    /// <summary>The repeater's interval in days: when the habit is due again after it is done.</summary>
    public int MinDays { get; }
    /// <summary>The longest gap allowed (<c>/4d</c>), else <see cref="MinDays"/>.</summary>
    public int MaxDays { get; }
    /// <summary>The days it was done, oldest first.</summary>
    public IReadOnlyList<DateOnly> Done { get; }

    public static bool IsHabit(OrgHeading h) => string.Equals(h.Property("STYLE"), "habit", StringComparison.OrdinalIgnoreCase);

    /// <summary>The habit an entry is, or null (no <c>:STYLE: habit</c>, or no repeating scheduled timestamp).</summary>
    public static OrgHabit? Of(OrgHeading h, OrgTodoKeywords keywords)
    {
        if (!IsHabit(h) || h.Scheduled is not { Repeater: { } r } scheduled) return null;
        var min = Math.Max(1, r.Interval.ApproximateDays);
        var max = Math.Max(min, r.Max?.ApproximateDays ?? min);
        var done = h.StateChanges.Where(s => keywords.IsDone(s.To)).Select(s => DateOnly.FromDateTime(s.At)).Distinct().Order().ToList();
        return new OrgHabit(h, scheduled, min, max, done);
    }

    /// <summary>The graph from <paramref name="before"/> days before today to <paramref name="after"/> days after.</summary>
    public IReadOnlyList<OrgHabitDay> Graph(DateOnly today, int before = 21, int after = 7)
    {
        var days = new List<OrgHabitDay>(before + after + 1);
        for (var d = today.AddDays(-before); d <= today.AddDays(after); d = d.AddDays(1))
        {
            // due MinDays after the last time it was done before that day; after the last time, when the timestamp says now; before the first
            // time (no history), not due yet
            var previous = Done.Where(x => x < d).Select(x => (DateOnly?)x).LastOrDefault();
            var due = previous is null ? (Done.Count > 0 ? Done[0] : Scheduled.Date)
                : previous == Done[^1] ? Scheduled.Date
                : previous.Value.AddDays(MinDays);
            var deadline = due.AddDays(MaxDays - MinDays);
            var doneThatDay = Done.Contains(d);
            var state = d < due ? OrgHabitState.Ahead
                : d < deadline || (d == deadline && doneThatDay) ? OrgHabitState.OnTime
                : d == deadline ? OrgHabitState.Due
                : OrgHabitState.Overdue;
            days.Add(new OrgHabitDay(d, state, doneThatDay, d == today, d > today));
        }
        return days;
    }
}
