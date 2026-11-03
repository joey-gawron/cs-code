using CsCode.Panels.Org.Engine;
using Xunit;

namespace CsCode.Panels.Org.Tests;

public sealed class AgendaTests
{
    private static readonly DateOnly Mon = new(2026, 11, 2);
    private static readonly DateOnly Today = new(2026, 11, 3); // Tuesday
    private static readonly DateOnly Sun = new(2026, 11, 8);

    private const string Text = """
        * TODO Overdue task
          SCHEDULED: <2026-10-31 Sat>
        * TODO Due Thursday
          DEADLINE: <2026-11-05 Thu>
        * TODO Late payment
          DEADLINE: <2026-11-01 Sun>
        * TODO Far deadline
          DEADLINE: <2026-12-20 Sun>
        * TODO Close deadline with own warning
          DEADLINE: <2026-11-20 Fri -30d>
        * DONE Finished yesterday
          CLOSED: [2026-11-02 Mon 18:00] SCHEDULED: <2026-11-02 Mon>
        * Standup
          <2026-11-02 Mon 09:30 +1d>
        * TODO [#A] Important today
          SCHEDULED: <2026-11-03 Tue>
        * TODO [#C] Minor today
          SCHEDULED: <2026-11-03 Tue>
        * TODO Lunch with Sam
          SCHEDULED: <2026-11-03 Tue 12:30>
        * NEXT Unscheduled
        * Conference
          <2026-11-06 Fri>--<2026-11-07 Sat>
        """;

    private static IReadOnlyList<OrgAgendaDay> Week() =>
        OrgAgenda.Build([OrgDocument.Parse(Text, fileName: "a.org")], Mon, Sun, Today, new OrgAgendaOptions(DeadlineWarningDays: 14));

    private static string Line(OrgAgendaItem i) => string.Join(' ', new[] { i.TimeText, i.Extra.Trim(), i.Heading.Title }.Where(p => p.Length > 0));

    [Fact]
    public void Today_lists_timed_entries_first_then_by_urgency_and_priority()
    {
        var today = Week().Single(d => d.Date == Today).Items.Select(Line).ToList();
        Assert.Equal(
        [
            "09:30 Standup",
            "12:30 Scheduled: Lunch with Sam",
            "2 d. ago: Late payment",
            "Sched. 3x: Overdue task",
            "Scheduled: Important today",
            "Scheduled: Minor today",
            "In  2 d.: Due Thursday",
            "In 17 d.: Close deadline with own warning",
        ], today);
    }

    [Fact]
    public void Other_days_list_their_own_entries_and_repetitions()
    {
        var days = Week().ToDictionary(d => d.Date, d => d.Items.Select(Line).ToList());
        Assert.Equal(7, days.Count);
        Assert.Equal(["09:30 Standup", "Scheduled: Finished yesterday"], days[Mon]); // Late payment's deadline was the Sunday before
        Assert.Equal(["09:30 Standup", "Deadline: Due Thursday"], days[new DateOnly(2026, 11, 5)]);
        Assert.Equal(["09:30 Standup", "(1/2): Conference"], days[new DateOnly(2026, 11, 6)]);
        Assert.Equal(["09:30 Standup", "(2/2): Conference"], days[new DateOnly(2026, 11, 7)]);
        Assert.DoesNotContain(days.Values.SelectMany(d => d), l => l.Contains("Far deadline", StringComparison.Ordinal));
    }

    [Fact]
    public void Done_entries_are_left_out_when_asked()
    {
        var days = OrgAgenda.Build([OrgDocument.Parse(Text)], Mon, Mon, Today, new OrgAgendaOptions(ShowDone: false));
        Assert.DoesNotContain(days[0].Items, i => i.Heading.IsDone);
    }

    [Fact]
    public void A_day_in_the_future_shows_no_overdue_or_warnings()
    {
        var friday = OrgAgenda.Build([OrgDocument.Parse(Text)], new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 6), Today);
        Assert.DoesNotContain(friday[0].Items, i => i.Kind is OrgAgendaKind.ScheduledOverdue or OrgAgendaKind.DeadlineOverdue or OrgAgendaKind.DeadlineUpcoming);
    }

    [Fact]
    public void The_todo_list_has_every_entry_still_to_do()
    {
        var items = OrgAgenda.TodoList([OrgDocument.Parse(Text)], Today);
        Assert.Equal(["Overdue task", "Due Thursday", "Late payment", "Far deadline", "Close deadline with own warning", "Important today", "Minor today", "Lunch with Sam", "Unscheduled"],
            items.Select(i => i.Heading.Title));
        Assert.Equal(["Unscheduled"], OrgAgenda.TodoList([OrgDocument.Parse(Text)], Today, "NEXT").Select(i => i.Heading.Title));
    }
}

public sealed class EditTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 14, 5, 0);

    private static string Run(string text, Func<OrgDocument, OrgEditResult?> edit)
    {
        var doc = OrgDocument.Parse(text);
        var result = edit(doc);
        Assert.NotNull(result);
        return OrgText.Apply(text, result.Edits);
    }

    private static string OnFirst(string text, Func<OrgDocument, OrgHeading, OrgEditResult?> edit) => Run(text, d => edit(d, d.Headings[0]));

    [Fact]
    public void Marking_done_adds_CLOSED_and_undoing_it_removes_it()
    {
        var done = OnFirst("* TODO Task\nNotes\n", (d, h) => OrgEdits.SetKeyword(d, h, "DONE", Now));
        Assert.Equal("* DONE Task\nCLOSED: [2026-11-03 Tue 14:05]\nNotes\n", done);
        var back = OnFirst(done, (d, h) => OrgEdits.SetKeyword(d, h, "TODO", Now));
        Assert.Equal("* TODO Task\nNotes\n", back);
        Assert.Equal("* DONE Task\n", OnFirst("* TODO Task\n", (d, h) => OrgEdits.SetKeyword(d, h, "DONE", Now, logDone: false)));
    }

    [Fact]
    public void Done_keeps_the_planning_lines_indentation_and_order()
    {
        var text = "** TODO Task  :x:\n   SCHEDULED: <2026-11-01 Sun>\n   body\n";
        Assert.Equal("** DONE Task  :x:\n   CLOSED: [2026-11-03 Tue 14:05] SCHEDULED: <2026-11-01 Sun>\n   body\n",
            OnFirst(text, (d, h) => OrgEdits.SetKeyword(d, h, "DONE", Now)));
    }

    [Fact]
    public void A_repeating_entry_moves_to_its_next_date_and_stays_to_do()
    {
        var text = "* TODO Water plants\n  SCHEDULED: <2026-10-27 Tue +1w>\n  :PROPERTIES:\n  :ID: 1\n  :END:\n";
        var result = OrgEdits.SetKeyword(OrgDocument.Parse(text), OrgDocument.Parse(text).Headings[0], "DONE", Now);
        Assert.Equal("* TODO Water plants\n  SCHEDULED: <2026-11-03 Tue +1w>\n  :PROPERTIES:\n  :ID: 1\n  :LAST_REPEAT: [2026-11-03 Tue 14:05]\n  :END:\n",
            OrgText.Apply(text, result.Edits));
        Assert.Contains("Entry repeats", result.Message, StringComparison.Ordinal);
        Assert.Equal("* TODO Gym\nSCHEDULED: <2026-11-05 Thu .+2d>\n:PROPERTIES:\n:LAST_REPEAT: [2026-11-03 Tue 14:05]\n:END:\n",
            OnFirst("* TODO Gym\nSCHEDULED: <2026-10-01 Thu .+2d>\n", (d, h) => OrgEdits.SetKeyword(d, h, "DONE", Now)));
    }

    [Fact]
    public void Cycling_goes_through_every_state_and_back_to_none()
    {
        var text = "* Task";
        foreach (var expected in new[] { "* TODO Task", "* NEXT Task", "* WAITING Task", "* DONE Task", "* CANCELLED Task", "* Task" })
        {
            text = OnFirst(text, (d, h) => OrgEdits.CycleKeyword(d, h, Now, logDone: false));
            Assert.Equal(expected, text);
        }
        Assert.Equal("* TODO [#A] Task", OnFirst("* [#A] Task", (d, h) => OrgEdits.SetKeyword(d, h, "TODO", Now)));
        Assert.Equal("* [#A] Task", OnFirst("* TODO [#A] Task", (d, h) => OrgEdits.SetKeyword(d, h, null, Now)));
    }

    [Fact]
    public void Scheduling_adds_replaces_and_removes_the_planning_line()
    {
        var ts = new OrgTimestamp(true, new DateOnly(2026, 11, 6), new TimeOnly(10, 0));
        var added = OnFirst("* TODO Task\nbody\n", (d, h) => OrgEdits.SetPlanning(d, h, OrgPlanningKind.Scheduled, ts));
        Assert.Equal("* TODO Task\nSCHEDULED: <2026-11-06 Fri 10:00>\nbody\n", added);
        var deadline = OnFirst(added, (d, h) => OrgEdits.SetPlanning(d, h, OrgPlanningKind.Deadline, new OrgTimestamp(true, new DateOnly(2026, 11, 9))));
        Assert.Equal("* TODO Task\nDEADLINE: <2026-11-09 Mon> SCHEDULED: <2026-11-06 Fri 10:00>\nbody\n", deadline); // org's order
        var removed = OnFirst(deadline, (d, h) => OrgEdits.SetPlanning(d, h, OrgPlanningKind.Scheduled, null));
        Assert.Equal("* TODO Task\nDEADLINE: <2026-11-09 Mon>\nbody\n", removed);
        Assert.Equal("* TODO Task\nbody\n", OnFirst(removed, (d, h) => OrgEdits.SetPlanning(d, h, OrgPlanningKind.Deadline, null)));
        // a heading at the very end without a line break
        Assert.Equal("* Last\nSCHEDULED: <2026-11-06 Fri 10:00>", OnFirst("* Last", (d, h) => OrgEdits.SetPlanning(d, h, OrgPlanningKind.Scheduled, ts)));
    }

    [Fact]
    public void A_new_date_keeps_the_repeater_and_follows_the_bodys_indentation()
    {
        var text = "* TODO Bills\n  SCHEDULED: <2026-11-01 Sun +1m>\n";
        Assert.Equal("* TODO Bills\n  SCHEDULED: <2026-11-10 Tue +1m>\n",
            OnFirst(text, (d, h) => OrgEdits.SetPlanning(d, h, OrgPlanningKind.Scheduled, new OrgTimestamp(true, new DateOnly(2026, 11, 10)))));
        Assert.Equal("** Item\n   DEADLINE: <2026-11-10 Tue>\n   indented notes\n",
            OnFirst("** Item\n   indented notes\n", (d, h) => OrgEdits.SetPlanning(d, h, OrgPlanningKind.Deadline, new OrgTimestamp(true, new DateOnly(2026, 11, 10)))));
    }

    [Fact]
    public void Priority_and_tags_are_set_and_removed()
    {
        Assert.Equal("* TODO [#A] Task", OnFirst("* TODO Task", (d, h) => OrgEdits.SetPriority(d, h, 'A')));
        Assert.Equal("* TODO [#C] Task", OnFirst("* TODO [#A] Task", (d, h) => OrgEdits.SetPriority(d, h, 'C')));
        Assert.Equal("* TODO Task", OnFirst("* TODO [#A] Task", (d, h) => OrgEdits.SetPriority(d, h, null)));
        Assert.Equal("* [#B] Task", OnFirst("* Task", (d, h) => OrgEdits.SetPriority(d, h, 'B')));
        Assert.Equal('A', OrgEdits.ShiftPriority(null, up: true));
        Assert.Equal('C', OrgEdits.ShiftPriority(null, up: false));
        Assert.Equal('A', OrgEdits.ShiftPriority('A', up: true));

        var tagged = OnFirst("* TODO Task\n", (d, h) => OrgEdits.SetTags(d, h, ["work", ":home:"]));
        Assert.Equal("* TODO Task" + new string(' ', 77 - "* TODO Task".Length - ":work:home:".Length) + ":work:home:\n", tagged);
        Assert.Equal(77, tagged.IndexOf('\n'));
        Assert.Equal("* TODO Task\n", OnFirst(tagged, (d, h) => OrgEdits.SetTags(d, h, [])));
    }

    [Fact]
    public void Promote_demote_and_move_subtrees()
    {
        var text = "* A\n** A1\n* B\nb text\n** B1\n* C";
        Assert.Equal("* A\n*** A1\n* B\nb text\n** B1\n* C", Run(text, d => OrgEdits.ChangeLevel(d, d.Headings[1], +1, subtree: false)));
        Assert.Equal("* A\n** A1\n** B\nb text\n*** B1\n* C", Run(text, d => OrgEdits.ChangeLevel(d, d.Headings[2], +1, subtree: true)));
        Assert.Null(OrgEdits.ChangeLevel(OrgDocument.Parse(text), OrgDocument.Parse(text).Headings[0], -1, subtree: false));

        var up = OrgEdits.MoveSubtree(OrgDocument.Parse(text), OrgDocument.Parse(text).Headings[2], up: true)!;
        Assert.Equal("* B\nb text\n** B1\n* A\n** A1\n* C", OrgText.Apply(text, up.Edits));
        Assert.Equal(0, up.Caret!.Value.Line);
        var down = OrgEdits.MoveSubtree(OrgDocument.Parse(text), OrgDocument.Parse(text).Headings[2], up: false)!;
        Assert.Equal("* A\n** A1\n* C\n* B\nb text\n** B1", OrgText.Apply(text, down.Edits));
        Assert.Equal(3, down.Caret!.Value.Line);
        Assert.Null(OrgEdits.MoveSubtree(OrgDocument.Parse(text), OrgDocument.Parse(text).Headings[1], up: true)); // A1 has no sibling above
    }

    [Fact]
    public void Insert_heading_goes_after_the_subtree_and_a_list_item_after_its_line()
    {
        var text = "* A\ntext\n** A1\n\n* B\n";
        var r = OrgEdits.InsertHeading(OrgDocument.Parse(text), 1, "TODO");
        Assert.Equal("* A\ntext\n** A1\n* TODO \n\n* B\n", OrgText.Apply(text, r.Edits));
        Assert.Equal(new Core.Text.TextPosition(3, 7), r.Caret);
        var list = "* A\n- [ ] one\n";
        var item = OrgEdits.InsertHeading(OrgDocument.Parse(list), 1, null);
        Assert.Equal("* A\n- [ ] one\n- [ ] \n", OrgText.Apply(list, item.Edits));
        Assert.Equal("1. x\n2. ", OrgText.Apply("1. x", OrgEdits.InsertHeading(OrgDocument.Parse("1. x"), 0, null).Edits));
    }

    [Fact]
    public void Checkboxes_toggle_and_update_the_headings_cookie()
    {
        var text = "* Shopping [0/2]\n- [ ] milk\n- [ ] eggs\n";
        var once = Run(text, d => OrgEdits.ToggleCheckbox(d, 1));
        Assert.Equal("* Shopping [1/2]\n- [X] milk\n- [ ] eggs\n", once);
        Assert.Equal("* Shopping [0/2]\n- [ ] milk\n- [ ] eggs\n", Run(once, d => OrgEdits.ToggleCheckbox(d, 1)));
        Assert.Equal("* Shopping [50%]\n- [X] milk\n- [ ] eggs\n", Run("* Shopping [%]\n- [X] milk\n- [ ] eggs\n", d => OrgEdits.ToggleCheckbox(d, 0)));
        Assert.Null(OrgEdits.ToggleCheckbox(OrgDocument.Parse("* A\nplain\n"), 1));
    }

    [Fact]
    public void Capture_appends_an_entry_on_its_own_lines()
    {
        var entry = OrgEdits.Entry(1, "TODO", "Call Bob", scheduled: new OrgTimestamp(true, new DateOnly(2026, 11, 4)), created: OrgEdits.Inactive(Now));
        Assert.Equal("* TODO Call Bob\nSCHEDULED: <2026-11-04 Wed>\n[2026-11-03 Tue 14:05]\n", entry);
        var r = OrgEdits.Append(OrgDocument.Parse("#+TITLE: Notes"), entry);
        Assert.Equal("#+TITLE: Notes\n" + entry, OrgText.Apply("#+TITLE: Notes", r.Edits));
        Assert.Equal(new Core.Text.TextPosition(3, "[2026-11-03 Tue 14:05]".Length), r.Caret);
        Assert.Equal(entry, OrgText.Apply("", OrgEdits.Append(OrgDocument.Parse(""), entry).Edits));
        Assert.Equal("x\n" + entry, OrgText.Apply("x\n", OrgEdits.Append(OrgDocument.Parse("x\n"), entry).Edits));
    }

    [Fact]
    public void Edits_apply_with_the_files_own_line_endings()
    {
        var text = "* TODO Task\r\nNotes\r\n";
        var doc = OrgDocument.Parse(text);
        var r = OrgEdits.SetKeyword(doc, doc.Headings[0], "DONE", Now);
        Assert.Equal("* DONE Task\r\nCLOSED: [2026-11-03 Tue 14:05]\r\nNotes\r\n", OrgText.Apply(text, OrgText.WithEol(r.Edits, OrgText.DetectEol(text))));
    }
}
