using CsCode.Panels.Org.Engine;
using CsCode.Panels.Org.Language;
using Xunit;

namespace CsCode.Panels.Org.Tests;

public sealed class ClockTests
{
    private static readonly DateTime Nine = new(2026, 11, 3, 9, 0, 0);

    private static string Run(string text, Func<OrgDocument, OrgEditResult?> edit)
    {
        var r = edit(OrgDocument.Parse(text));
        Assert.NotNull(r);
        return OrgText.Apply(text, r.Edits);
    }

    [Fact]
    public void Clocking_in_and_out_writes_org_clock_lines_in_the_logbook()
    {
        var text = "* TODO Write\nSCHEDULED: <2026-11-03 Tue>\nnotes\n";
        var running = Run(text, d => OrgClock.ClockIn(d, d.Headings[0], Nine));
        Assert.Equal("* TODO Write\nSCHEDULED: <2026-11-03 Tue>\n:LOGBOOK:\nCLOCK: [2026-11-03 Tue 09:00]\n:END:\nnotes\n", running);
        var h = OrgDocument.Parse(running).Headings[0];
        Assert.Equal(2, h.LogbookLine);
        Assert.True(h.Clocks.Single().IsRunning);
        Assert.Equal("notes", OrgDocument.Parse(running).BodyText(h)); // the logbook is not part of the notes

        var stopped = Run(running, d => OrgClock.ClockOut(d, d.Headings[0], Nine.AddMinutes(65)));
        Assert.Contains("CLOCK: [2026-11-03 Tue 09:00]--[2026-11-03 Tue 10:05] =>  1:05\n", stopped, StringComparison.Ordinal);
        Assert.Null(OrgClock.ClockOut(OrgDocument.Parse(stopped), OrgDocument.Parse(stopped).Headings[0], Nine));

        var again = Run(stopped, d => OrgClock.ClockIn(d, d.Headings[0], Nine.AddHours(2)));
        Assert.Contains(":LOGBOOK:\nCLOCK: [2026-11-03 Tue 11:00]\nCLOCK: [2026-11-03 Tue 09:00]--", again, StringComparison.Ordinal);
        var doc = OrgDocument.Parse(again);
        Assert.Equal(TimeSpan.FromMinutes(65 + 60), OrgClock.Total(doc, doc.Headings[0], Nine.AddHours(3)));
        Assert.Equal(TimeSpan.FromMinutes(30), OrgClock.Total(doc, doc.Headings[0], Nine.AddHours(3), from: Nine.AddMinutes(35), to: Nine.AddMinutes(65)));
        Assert.Equal(1, OrgClock.FindRunning([doc])!.Value.Clock.Line - 2);

        Assert.Equal(stopped, Run(again, d => OrgClock.Cancel(d, d.Headings[0])));
        Assert.Equal("* A\nx\n", Run("* A\n:LOGBOOK:\nCLOCK: [2026-11-03 Tue 09:00]\n:END:\nx\n", d => OrgClock.Cancel(d, d.Headings[0]))); // the empty drawer goes too
    }

    [Fact]
    public void The_clock_table_sums_each_heading_with_its_subheadings()
    {
        var text = "* Project\n:LOGBOOK:\nCLOCK: [2026-11-03 Tue 09:00]--[2026-11-03 Tue 10:00] =>  1:00\n:END:\n** Sub\n:LOGBOOK:\n"
                 + "CLOCK: [2026-11-03 Tue 10:00]--[2026-11-03 Tue 10:30] =>  0:30\n:END:\n* Other\n";
        var withTable = Run(text, d => OrgClock.Table(d, 9, Nine));
        Assert.EndsWith("""
            #+BEGIN: clocktable :scope file :maxlevel 2
            #+CAPTION: Clock summary at [2026-11-03 Tue 09:00]
            | Headline     | Time   |      |
            |--------------+--------+------|
            | *Total time* | *1:30* |      |
            |--------------+--------+------|
            | Project      | 1:30   |      |
            | \_  Sub      |        | 0:30 |
            #+END:


            """.Replace("\r\n", "\n", StringComparison.Ordinal), withTable, StringComparison.Ordinal);
        // C-c C-c in the table brings it up to date, keeping its #+BEGIN line
        var updated = Run(withTable.Replace(":maxlevel 2", ":maxlevel 1", StringComparison.Ordinal), d => OrgClock.Table(d, 12, Nine.AddHours(1)));
        Assert.Contains("#+CAPTION: Clock summary at [2026-11-03 Tue 10:00]\n| Headline     | Time   |\n", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("Sub ", updated, StringComparison.Ordinal);
    }
}

public sealed class HabitTests
{
    private const string Text = """
        * TODO Exercise
        SCHEDULED: <2026-11-04 Wed .+2d/4d>
        :PROPERTIES:
        :STYLE: habit
        :END:
        :LOGBOOK:
        - State "DONE"       from "TODO"       [2026-11-02 Mon 08:00]
        - State "DONE"       from "TODO"       [2026-10-28 Wed 08:00]
        :END:

        """;

    [Fact]
    public void A_habit_has_its_interval_its_longest_gap_and_its_history()
    {
        var doc = OrgDocument.Parse(Text);
        Assert.Equal("<2026-11-04 Wed .+2d/4d>", doc.Headings[0].Scheduled!.ToString());
        var habit = OrgHabit.Of(doc.Headings[0], doc.Keywords)!;
        Assert.Equal(2, habit.MinDays);
        Assert.Equal(4, habit.MaxDays);
        Assert.Equal([new DateOnly(2026, 10, 28), new DateOnly(2026, 11, 2)], habit.Done);
        Assert.Null(OrgHabit.Of(OrgDocument.Parse("* TODO x\nSCHEDULED: <2026-11-04 Wed .+2d>\n").Headings[0], doc.Keywords));
    }

    [Fact]
    public void The_graph_colours_each_day_as_org_habit_does()
    {
        var doc = OrgDocument.Parse(Text);
        var graph = OrgHabit.Of(doc.Headings[0], doc.Keywords)!.Graph(new DateOnly(2026, 11, 3), before: 7, after: 2);
        Assert.Equal(
        [
            OrgHabitState.Ahead, OrgHabitState.OnTime, OrgHabitState.Ahead, OrgHabitState.OnTime, OrgHabitState.OnTime,
            OrgHabitState.Due, OrgHabitState.Overdue, OrgHabitState.Ahead, OrgHabitState.OnTime, OrgHabitState.OnTime,
        ], graph.Select(d => d.State));
        Assert.Equal([new DateOnly(2026, 10, 28), new DateOnly(2026, 11, 2)], graph.Where(d => d.Done).Select(d => d.Date));
        Assert.True(graph[7].IsToday);
        Assert.True(graph[8].IsFuture);
    }

    [Fact]
    public void Habits_show_on_today_only_and_archived_trees_not_at_all()
    {
        var doc = OrgDocument.Parse("* TODO Read\nSCHEDULED: <2026-11-01 Sun .+1d>\n:PROPERTIES:\n:STYLE: habit\n:END:\n* TODO Old  :ARCHIVE:\nSCHEDULED: <2026-11-03 Tue>\n");
        var week = OrgAgenda.Build([doc], new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 3));
        Assert.Equal(["Read"], week.SelectMany(d => d.Items).Where(i => i.Day > new DateOnly(2026, 11, 2)).Select(i => i.Heading.Title).Distinct());
        Assert.All(week.SelectMany(d => d.Items), i => Assert.True(i.Day <= new DateOnly(2026, 11, 3)));
        Assert.DoesNotContain(OrgAgenda.TodoList([doc], new DateOnly(2026, 11, 3)), i => i.Heading.Title == "Old");
    }
}

public sealed class RefileTests
{
    private const string Text = "* A\n** A1\n* B\nb\n** B1\n* C\n";

    private static string Move(string text, Func<OrgDocument, OrgEditResult?> edit) => OrgText.Apply(text, edit(OrgDocument.Parse(text))!.Edits);

    [Fact]
    public void Refiling_within_a_file_moves_the_subtree_under_its_new_parent()
    {
        Assert.Equal("* A\n** A1\n* C\n** B\nb\n*** B1\n", Move(Text, d => OrgRefile.MoveWithin(d, d.Headings[2], d.Headings[4])));
        Assert.Equal("* A\n** A1\n** C\n* B\nb\n** B1\n", Move(Text, d => OrgRefile.MoveWithin(d, d.Headings[4], d.Headings[0])));
        Assert.Equal("* A\n* B\nb\n** B1\n* C\n* A1\n", Move(Text, d => OrgRefile.MoveWithin(d, d.Headings[1], null)));
        var doc = OrgDocument.Parse(Text);
        Assert.Null(OrgRefile.MoveWithin(doc, doc.Headings[0], doc.Headings[1])); // into itself
        Assert.Equal(3, OrgRefile.MoveWithin(doc, doc.Headings[2], doc.Headings[4])!.Caret!.Value.Line);
    }

    [Fact]
    public void Refiling_to_another_file_inserts_there_and_removes_here()
    {
        var source = OrgDocument.Parse("* TODO Call Bob\nnotes\n** Detail\n\n* Keep\n");
        var lines = OrgRefile.SubtreeLines(source, source.Headings[0]);
        Assert.Equal(["* TODO Call Bob", "notes", "** Detail"], lines);
        var target = "* Inbox\n* Projects\n** Old\n";
        Assert.Equal("* Inbox\n* Projects\n** Old\n** TODO Call Bob\nnotes\n*** Detail\n",
            Move(target, d => OrgRefile.InsertUnder(d, d.Headings[1], lines, 1)));
        Assert.Equal("* Keep\n", OrgText.Apply(string.Join('\n', source.Lines), OrgRefile.Remove(source, source.Headings[0]).Edits));
        var targets = OrgRefile.Targets([source, OrgDocument.Parse(target, fileName: "projects.org")], maxLevel: 1);
        Assert.Equal(["", "", "", "projects.org", "projects.org / Inbox", "projects.org / Projects"], targets.Select(t => t.Document.FileName.Length == 0 ? "" : t.Label));
    }

    [Fact]
    public void Archive_locations_read_as_org_reads_them()
    {
        Assert.Equal(("/x/notes.org_archive", (string?)null), OrgRefile.ArchiveLocation("%s_archive::", "/x/notes.org"));
        Assert.Equal(("/x/archive/old.org", "* Done"), OrgRefile.ArchiveLocation("archive/old.org::* Done", "/x/notes.org"));
        Assert.Equal(("/x/notes.org", "* Archive"), OrgRefile.ArchiveLocation("::* Archive", "/x/notes.org"));
    }

    [Fact]
    public void An_archived_subtree_records_where_it_came_from()
    {
        var doc = OrgDocument.Parse("* Projects\n** DONE Ship  :work:\nCLOSED: [2026-11-01 Sun 10:00]\ntext\n*** Part\n", fileName: "notes.org");
        var archived = OrgRefile.ArchivedSubtree(doc, doc.Headings[1], new DateTime(2026, 11, 3, 14, 5, 0), "/x/notes.org");
        Assert.Equal(["** DONE Ship  :work:", "CLOSED: [2026-11-01 Sun 10:00]", ":PROPERTIES:", ":ARCHIVE_TIME: 2026-11-03 Tue 14:05", ":ARCHIVE_FILE: /x/notes.org",
            ":ARCHIVE_OLPATH: Projects", ":ARCHIVE_CATEGORY: notes", ":ARCHIVE_TODO: DONE", ":END:", "text", "*** Part"], archived);

        var file = OrgRefile.ArchiveFileHeader("/x/notes.org");
        var top = OrgText.Apply(file, OrgRefile.InsertIntoArchive(OrgDocument.Parse(file), null, archived, 2).Edits);
        Assert.EndsWith("\n\n\n* DONE Ship  :work:\nCLOSED: [2026-11-01 Sun 10:00]\n", top[..(top.IndexOf(":PROPERTIES:", StringComparison.Ordinal))], StringComparison.Ordinal);
        Assert.Contains("\n** Part\n", top, StringComparison.Ordinal);
        var under = OrgText.Apply("x\n", OrgRefile.InsertIntoArchive(OrgDocument.Parse("x\n"), "* Archived Tasks", archived, 2).Edits);
        Assert.StartsWith("x\n* Archived Tasks\n** DONE Ship", under, StringComparison.Ordinal);
    }
}

public sealed class CaptureTests
{
    private static readonly OrgCaptureContext Context = new(new DateTime(2026, 11, 3, 14, 5, 0), FilePath: "/x/a.org", Line: 9);

    private sealed class Prompts(params object?[] answers) : IOrgCapturePrompts
    {
        private readonly Queue<object?> _answers = new(answers);
        public List<(string Prompt, string? Default, IReadOnlyList<string> Options)> Asked { get; } = [];
        public Task<string?> TextAsync(string prompt, string? defaultValue, IReadOnlyList<string> options)
        {
            Asked.Add((prompt, defaultValue, options));
            return Task.FromResult(_answers.Dequeue() as string);
        }
        public Task<OrgTimestamp?> DateAsync(string prompt, bool withTime) => Task.FromResult(_answers.Dequeue() as OrgTimestamp);
        public Task<IReadOnlyList<string>?> TagsAsync() => Task.FromResult(_answers.Dequeue() as IReadOnlyList<string>);
    }

    [Fact]
    public async Task Templates_expand_orgs_escapes()
    {
        Assert.Equal(new OrgCaptureEntry("* TODO Buy milk\n[2026-11-03 Tue 14:05]\n", null),
            await OrgCapture.ExpandAsync("* TODO %^{Task}\n%U", Context, new Prompts("Buy milk")));
        var note = await OrgCapture.ExpandAsync("* %^{Title}\n%U\n%?", Context, new Prompts("Ideas"));
        Assert.Equal("* Ideas\n[2026-11-03 Tue 14:05]\n\n", note!.Text);
        Assert.Equal(note.Text.Length - 1, note.Cursor);
        var forDay = await OrgCapture.ExpandAsync("%t %T %u %<%Y-%m-%d %a> %% %a %f", Context with { Day = new DateOnly(2026, 11, 5) }, new Prompts());
        Assert.Equal("<2026-11-05 Thu> <2026-11-05 Thu> [2026-11-03 Tue] 2026-11-03 Tue % [[file:/x/a.org::10]] a.org\n", forDay!.Text);
        var prompts = new Prompts("L", new[] { "a", "b" }, new OrgTimestamp(true, new DateOnly(2026, 11, 9), new TimeOnly(10, 0)));
        var picked = await OrgCapture.ExpandAsync("* Shirt %^{Size|M|S|L} %^g\n%^T", Context, prompts);
        Assert.Equal("* Shirt L :a:b:\n<2026-11-09 Mon 10:00>\n", picked!.Text);
        Assert.Equal(("Size", "M"), (prompts.Asked[0].Prompt, prompts.Asked[0].Default));
        Assert.Equal(["M", "S", "L"], prompts.Asked[0].Options);
        Assert.Null(await OrgCapture.ExpandAsync("* %^{Title}", Context, new Prompts([null])));
        Assert.Equal("2026-11-03 14:05:00 Tuesday November 307  3 %", OrgCapture.StrFTime("%Y-%m-%d %H:%M:%S %A %B %j %e %%", Context.Now));
    }

    [Fact]
    public void Entries_go_under_their_heading_or_into_the_date_tree()
    {
        var day = new DateOnly(2026, 11, 3);
        var journal = new OrgCaptureTemplate("j", "Journal", "", DateTree: true);
        var text = "#+TITLE: Journal\n";
        text = OrgText.Apply(text, OrgCapture.Insert(OrgDocument.Parse(text), journal, new OrgCaptureEntry("* First\n", null), day).Edits);
        Assert.Equal("#+TITLE: Journal\n* 2026\n** 2026-11 November\n*** 2026-11-03 Tuesday\n**** First\n", text);
        text = OrgText.Apply(text, OrgCapture.Insert(OrgDocument.Parse(text), journal, new OrgCaptureEntry("* Second\n** detail\n", null), day).Edits);
        text = OrgText.Apply(text, OrgCapture.Insert(OrgDocument.Parse(text), journal, new OrgCaptureEntry("* Earlier\n", null), new DateOnly(2026, 11, 1)).Edits);
        text = OrgText.Apply(text, OrgCapture.Insert(OrgDocument.Parse(text), journal, new OrgCaptureEntry("* Later\n", null), new DateOnly(2026, 12, 24)).Edits);
        Assert.Equal("#+TITLE: Journal\n* 2026\n** 2026-11 November\n*** 2026-11-01 Sunday\n**** Earlier\n*** 2026-11-03 Tuesday\n**** First\n**** Second\n***** detail\n"
            + "** 2026-12 December\n*** 2026-12-24 Thursday\n**** Later\n", text);

        var inbox = new OrgCaptureTemplate("i", "Inbox", "", Headline: "Inbox");
        var r = OrgCapture.Insert(OrgDocument.Parse("* Notes\n"), inbox, new OrgCaptureEntry("* %?x\n".Replace("%?", "", StringComparison.Ordinal), 2), day);
        Assert.Equal("* Notes\n* Inbox\n** x\n", OrgText.Apply("* Notes\n", r.Edits));
        Assert.Equal(new Core.Text.TextPosition(2, 3), r.Caret); // %? after "* ", now "** "
        Assert.Equal("* Inbox\n** old\n** x\n* Other\n",
            OrgText.Apply("* Inbox\n** old\n* Other\n", OrgCapture.Insert(OrgDocument.Parse("* Inbox\n** old\n* Other\n"), inbox, new OrgCaptureEntry("* x\n", null), day).Edits));
    }
}

public sealed class ExportTests
{
    private const string Text = """
        #+TITLE: Plan
        #+OPTIONS: toc:nil
        * TODO [#A] Ship *it*   :work:
        SCHEDULED: <2026-11-03 Tue>
        :PROPERTIES:
        :ID: 1
        :END:
        Some /text/ with =code= and a [[https://example.com][link]].
        - [X] done item
        - [ ] open item
          continued
        1. first
        #+BEGIN_SRC python
        print("hi")
        #+END_SRC
        #+RESULTS:
        : hi
        | a | b |
        |---+---|
        | 1 | 2 |
        # a comment
        * Secret   :noexport:
        hidden
        * COMMENT Draft
        also hidden

        """;

    [Fact]
    public void Markdown_keeps_the_text_and_leaves_out_what_org_leaves_out()
    {
        Assert.Equal("# Plan\n\n## TODO [#A] Ship **it**\n\nSome *text* with `code` and a [link](https://example.com).\n\n"
            + "- [x] done item\n- [ ] open item continued\n1. first\n\n```python\nprint(\"hi\")\n```\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n",
            OrgExport.ToMarkdown(OrgDocument.Parse(Text, fileName: "plan.org")));
    }

    [Fact]
    public void Html_is_a_standalone_page()
    {
        var html = OrgExport.ToHtml(OrgDocument.Parse(Text, fileName: "plan.org"));
        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("<title>Plan</title>", html, StringComparison.Ordinal);
        Assert.Contains("<h2><span class=\"todo\">TODO</span> <span class=\"priority\">[#A]</span> Ship <b>it</b> <span class=\"tags\"><span class=\"tag\">work</span></span></h2>", html, StringComparison.Ordinal);
        Assert.Contains("<p>Some <i>text</i> with <code>code</code> and a <a href=\"https://example.com\">link</a>.</p>", html, StringComparison.Ordinal);
        Assert.Contains("<li><code>[X]</code> done item</li>", html, StringComparison.Ordinal);
        Assert.Contains("<pre class=\"src src-python\"><code class=\"language-python\">print(&quot;hi&quot;)</code></pre>", html, StringComparison.Ordinal);
        Assert.Contains("<thead><tr><th>a</th><th>b</th></tr></thead>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", html, StringComparison.Ordinal);
        Assert.DoesNotContain("SCHEDULED", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">hi<", html, StringComparison.Ordinal);
    }
}

public sealed class BabelTests
{
    private const string Text = "* Code\n#+NAME: hello\n#+HEADER: :var n=3\n#+BEGIN_SRC sh :results output :dir /tmp\n  echo $n\n#+END_SRC\ntext\n";

    [Fact]
    public void Blocks_are_found_with_their_header_arguments()
    {
        var doc = OrgDocument.Parse(Text);
        var block = OrgBabel.All(doc).Single();
        Assert.Equal((3, 5, "sh", "echo $n"), (block.Begin, block.End, block.Language, block.Code));
        Assert.Equal("/tmp", block.Header(":dir"));
        Assert.Equal([("n", "3")], block.Variables());
        Assert.Same(block, OrgBabel.At(doc, 1) is { } b && b.Begin == block.Begin ? block : null);
        Assert.NotNull(OrgBabel.At(doc, 4));
        Assert.Null(OrgBabel.At(doc, 6));
        Assert.Equal("n='3'\necho $n", OrgBabel.WithVariables(block));
        var python = OrgBabel.All(OrgDocument.Parse("#+BEGIN_SRC python :var x=5, y=hello\nprint(x)\n#+END_SRC\n")).Single();
        Assert.Equal("x = 5\ny = \"hello\"\nprint(x)", OrgBabel.WithVariables(python));
    }

    [Fact]
    public void Results_are_written_after_the_block_and_replaced_the_next_time()
    {
        var doc = OrgDocument.Parse(Text);
        var once = OrgText.Apply(Text, OrgBabel.Results(doc, OrgBabel.All(doc)[0], "3\n").Edits);
        Assert.EndsWith("#+END_SRC\n\n#+RESULTS:\n: 3\ntext\n", once, StringComparison.Ordinal);
        var again = OrgDocument.Parse(once);
        var twice = OrgText.Apply(once, OrgBabel.Results(again, OrgBabel.All(again)[0], "4\n\n5").Edits);
        Assert.EndsWith("#+END_SRC\n\n#+RESULTS:\n: 4\n:\n: 5\ntext\n", twice, StringComparison.Ordinal);
        var many = string.Join('\n', Enumerable.Range(1, 12)) + "\n";
        var third = OrgDocument.Parse(twice);
        var block = OrgText.Apply(twice, OrgBabel.Results(third, OrgBabel.All(third)[0], many).Edits);
        Assert.EndsWith("#+RESULTS:\n#+begin_example\n1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12\n#+end_example\ntext\n", block, StringComparison.Ordinal);
        Assert.Equal(1, OrgDocument.Parse(block).Lines.Count(l => l == "#+RESULTS:"));
    }

    [Fact]
    public async Task A_block_runs_as_a_program_with_its_output_errors_and_exit_code()
    {
        if (OperatingSystem.IsWindows()) return; // sh
        using var dir = new TestDoubles.TempDirectory("cscode-babel-");
        var run = await OrgBabelRunner.RunAsync("sh", ".sh", "echo hi; pwd; echo oops >&2; exit 3", dir.Path, TimeSpan.FromSeconds(20));
        Assert.Equal(3, run.ExitCode);
        Assert.StartsWith("hi\n", run.Output, StringComparison.Ordinal);
        Assert.EndsWith(Path.GetFileName(dir.Path) + "\n", run.Output, StringComparison.Ordinal);
        Assert.Equal("oops\n", run.Error);
        var slow = await OrgBabelRunner.RunAsync("sh", ".sh", "sleep 20", dir.Path, TimeSpan.FromMilliseconds(300));
        Assert.True(slow.TimedOut);
        Assert.Null(slow.ExitCode);
        var missing = await OrgBabelRunner.RunAsync("no-such-program-for-org", ".x", "", dir.Path, TimeSpan.FromSeconds(5));
        Assert.Contains("Could not start", missing.Error, StringComparison.Ordinal);
    }
}

public sealed class KeywordColourTests
{
    [Fact]
    public void A_files_own_states_are_semantic_tokens()
    {
        var doc = OrgDocument.Parse("#+TODO: OPEN | CLOSED\n* OPEN a\n* CLOSED b\n* TODO c\n");
        Assert.Equal([1u, 2, 4, 0, 0, 1, 2, 6, 1, 0], OrgLanguage.KeywordTokens(doc).Data);
        Assert.Equal(["keyword", "comment"], new OrgLanguage(() => OrgTodoKeywords.Default).Legend.TokenTypes);
    }
}
