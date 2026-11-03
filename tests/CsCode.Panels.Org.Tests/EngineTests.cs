using CsCode.Panels.Org.Engine;
using Xunit;

namespace CsCode.Panels.Org.Tests;

public sealed class TimestampTests
{
    private static readonly DateOnly Nov3 = new(2026, 11, 3); // a Tuesday

    [Theory]
    [InlineData("<2026-11-05 Thu>")]
    [InlineData("<2026-11-05 Thu 10:00>")]
    [InlineData("<2026-11-05 Thu 10:00-11:30>")]
    [InlineData("<2026-11-05 Thu +1w>")]
    [InlineData("<2026-11-05 Thu 09:00 .+2d -3d>")]
    [InlineData("<2026-11-05 Thu ++1m>")]
    [InlineData("[2026-11-05 Thu 14:05]")]
    [InlineData("<2026-11-05 Thu>--<2026-11-07 Sat>")]
    public void Org_spelling_reads_and_writes_back_unchanged(string text)
    {
        var ts = OrgTimestamp.Parse(text);
        Assert.NotNull(ts);
        Assert.Equal(text, ts.ToString());
    }

    [Fact]
    public void The_parts_of_a_timestamp_are_read()
    {
        var ts = OrgTimestamp.Parse("<2026-11-05 Thu 10:00-11:30 .+1w -2d>")!;
        Assert.True(ts.Active);
        Assert.Equal(new DateOnly(2026, 11, 5), ts.Date);
        Assert.Equal(new TimeOnly(10, 0), ts.Start);
        Assert.Equal(new TimeOnly(11, 30), ts.End);
        Assert.Equal(new OrgRepeater(OrgRepeatKind.Restart, new OrgInterval(1, OrgTimeUnit.Week)), ts.Repeater);
        Assert.Equal(new OrgInterval(2, OrgTimeUnit.Day), ts.Warning);
        Assert.Equal("10:00-11:30", ts.TimeText);
    }

    [Theory]
    [InlineData("<2026-13-05 Thu>")]
    [InlineData("<2026-02-30>")]
    [InlineData("<2026-11-05 Thu]")]
    [InlineData("2026-11-05")]
    public void Not_a_timestamp(string text) => Assert.Null(OrgTimestamp.Parse(text));

    [Fact]
    public void Timestamps_are_found_in_text_with_their_columns()
    {
        var found = OrgTimestamp.FindAll("Call <2026-11-05 Thu 10:00> and [2026-11-01 Sun] then <2026-11-09 Mon>--<2026-11-10 Tue>.").ToList();
        Assert.Equal(3, found.Count);
        Assert.Equal(5, found[0].Column);
        Assert.False(found[1].Timestamp.Active);
        Assert.Equal(new DateOnly(2026, 11, 10), found[2].Timestamp.EndDate);
        Assert.Equal("<2026-11-09 Mon>--<2026-11-10 Tue>".Length, found[2].Length);
    }

    [Fact]
    public void A_repeater_occurs_on_each_repetition_from_its_date_on()
    {
        var weekly = OrgTimestamp.Parse("<2026-10-20 Tue +1w>")!;
        Assert.Equal([new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 10)], weekly.OccurrencesIn(Nov3, Nov3.AddDays(13)));
        var monthly = OrgTimestamp.Parse("<2026-01-31 Sat +1m>")!;
        Assert.Equal([new DateOnly(2026, 2, 28)], monthly.OccurrencesIn(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));
        Assert.Empty(OrgTimestamp.Parse("<2026-12-01 Tue +1d>")!.OccurrencesIn(Nov3, Nov3.AddDays(6)));
    }

    [Fact]
    public void A_date_range_occurs_on_each_of_its_days()
    {
        var range = OrgTimestamp.FindAll("<2026-11-02 Mon>--<2026-11-04 Wed>").Single().Timestamp;
        Assert.Equal([Nov3, Nov3.AddDays(1)], range.OccurrencesIn(Nov3, Nov3.AddDays(6)));
    }

    [Fact]
    public void Repeating_moves_the_date_as_each_kind_of_repeater_says()
    {
        Assert.Equal(new DateOnly(2026, 10, 27), OrgTimestamp.Parse("<2026-10-20 Tue +1w>")!.Repeat(Nov3).Date);   // one interval, still past
        Assert.Equal(new DateOnly(2026, 11, 10), OrgTimestamp.Parse("<2026-10-20 Tue ++1w>")!.Repeat(Nov3).Date);  // into the future, same weekday
        Assert.Equal(new DateOnly(2026, 11, 5), OrgTimestamp.Parse("<2026-10-20 Tue .+2d>")!.Repeat(Nov3).Date);   // from today
        var plain = OrgTimestamp.Parse("<2026-10-20 Tue>")!;
        Assert.Same(plain, plain.Repeat(Nov3));
    }
}

public sealed class DocumentTests
{
    private const string Sample = """
        #+TITLE: My Notes
        #+FILETAGS: :home:
        Preamble text.
        * TODO [#A] Write the report   :work:urgent:
          DEADLINE: <2026-11-05 Thu -3d> SCHEDULED: <2026-11-02 Mon>
          :PROPERTIES:
          :CATEGORY: job
          :ID:       abc
          :END:
          Some notes about it.
          Meeting <2026-11-04 Wed 14:00>.
        ** DONE Draft
           CLOSED: [2026-11-01 Sun 10:00]
        ** Sub without keyword
        :LOGBOOK:
        - Note <2026-11-20 Fri>
        :END:
        #+BEGIN_SRC python
        print("<2026-11-21 Sat>")
        #+END_SRC
        * TODOLIST is not a keyword
        *bold* is not a heading
        * :onlytags:

        """;

    [Fact]
    public void Headings_are_read_with_keyword_priority_title_and_tags()
    {
        var doc = OrgDocument.Parse(Sample, fileName: "notes.org");
        Assert.Equal("My Notes", doc.Title);
        Assert.Equal(["TODO [#A] Write the report", "DONE Draft", "Sub without keyword", "TODOLIST is not a keyword", ":onlytags:"],
            doc.Headings.Select(h => (h.Keyword is null ? "" : h.Keyword + " ") + (h.Priority is { } p ? $"[#{p}] " : "") + (h.Title.Length > 0 ? h.Title : ":" + string.Join(':', h.Tags) + ":")));
        var report = doc.Headings[0];
        Assert.Equal(1, report.Level);
        Assert.Equal(["work", "urgent"], report.Tags);
        Assert.Equal(["work", "urgent", "home"], report.AllTags);
        Assert.True(report.IsTodo);
        Assert.Equal(2, report.KeywordColumn);
        Assert.Equal("Write the report", doc.Lines[report.Line][report.TitleColumn..report.TitleEnd]);
        Assert.True(doc.Headings[1].IsDone);
        Assert.Same(report, doc.Headings[1].Parent);
        Assert.Null(doc.Headings[3].Keyword);
    }

    [Fact]
    public void Planning_properties_category_and_ranges_are_read()
    {
        var doc = OrgDocument.Parse(Sample, fileName: "notes.org");
        var report = doc.Headings[0];
        Assert.Equal(4, report.PlanningLine);
        Assert.Equal(new DateOnly(2026, 11, 5), report.Deadline!.Date);
        Assert.Equal(new OrgInterval(3, OrgTimeUnit.Day), report.Deadline.Warning);
        Assert.Equal(new DateOnly(2026, 11, 2), report.Scheduled!.Date);
        Assert.Equal([OrgPlanningKind.Deadline, OrgPlanningKind.Scheduled], report.Planning.Select(p => p.Kind));
        Assert.Equal("abc", report.Property("id"));
        Assert.Equal("job", report.Category);
        Assert.Equal("job", doc.Headings[1].Category); // inherited
        Assert.Equal("notes", doc.Headings[3].Category); // the file's name
        Assert.Equal(9, report.ContentStart);
        Assert.Equal(11, report.End);
        Assert.Equal(20, report.SubtreeEnd);
        Assert.Equal(new DateOnly(2026, 11, 1), doc.Headings[1].Closed!.Date);
        Assert.Same(report, doc.HeadingAt(10));
        Assert.Null(doc.HeadingAt(2));
    }

    [Fact]
    public void Body_timestamps_skip_drawers_and_blocks()
    {
        var doc = OrgDocument.Parse(Sample);
        Assert.Equal([new DateOnly(2026, 11, 4)], doc.Headings[0].Timestamps.Select(t => t.Timestamp.Date));
        Assert.Equal(new TimeOnly(14, 0), doc.Headings[0].Timestamps[0].Timestamp.Start);
        Assert.Empty(doc.Headings[2].Timestamps); // in a drawer and a source block only
    }

    [Fact]
    public void The_body_text_is_the_notes_without_planning_or_drawer()
    {
        var doc = OrgDocument.Parse(Sample);
        Assert.Equal("Some notes about it.\nMeeting <2026-11-04 Wed 14:00>.", doc.BodyText(doc.Headings[0]));
        Assert.Equal("", doc.BodyText(doc.Headings[1]));
    }

    [Fact]
    public void A_files_TODO_line_sets_its_keywords()
    {
        var doc = OrgDocument.Parse("#+TODO: OPEN REVIEW | MERGED\n#+TODO: BUG | FIXED\n* REVIEW one\n* TODO two\n* FIXED three\n");
        Assert.Equal(["OPEN", "REVIEW", "BUG"], doc.Keywords.NotDone);
        Assert.Equal(["MERGED", "FIXED"], doc.Keywords.Done);
        Assert.Equal("REVIEW", doc.Headings[0].Keyword);
        Assert.Null(doc.Headings[1].Keyword);
        Assert.Equal("TODO two", doc.Headings[1].Title);
        Assert.True(doc.Headings[2].IsDone);
    }

    [Fact]
    public void Keywords_cycle_through_the_states_and_back_to_none()
    {
        var k = OrgTodoKeywords.Parse(["TODO(t)", "WAIT(w@/!)", "|", "DONE(d)"]);
        Assert.Equal(["TODO", "WAIT"], k.NotDone);
        Assert.Equal("TODO", k.Next(null));
        Assert.Equal("WAIT", k.Next("TODO"));
        Assert.Equal("DONE", k.Next("WAIT"));
        Assert.Null(k.Next("DONE"));
        Assert.Equal("DONE", k.Previous(null));
        var noBar = OrgTodoKeywords.Parse(["TODO", "DOING", "FINISHED"]);
        Assert.Equal(["FINISHED"], noBar.Done);
    }

    [Fact]
    public void Lines_are_numbered_as_the_editor_numbers_them()
    {
        Assert.Equal(["a", "b", ""], OrgDocument.SplitLines("a\r\nb\n"));
        Assert.Equal(["a", "b"], OrgDocument.SplitLines("a\rb"));
        var doc = OrgDocument.Parse("* A\ntext\n");
        Assert.Equal(3, doc.LineCount);
        Assert.Equal(2, doc.ContentLineCount);
        Assert.Equal(2, doc.Headings[0].SubtreeEnd);
    }
}

public sealed class DateInputTests
{
    private static readonly DateOnly Today = new(2026, 11, 3); // Tuesday

    [Theory]
    [InlineData("", "2026-11-03")]
    [InlineData(".", "2026-11-03")]
    [InlineData("today", "2026-11-03")]
    [InlineData("tomorrow", "2026-11-04")]
    [InlineData("tom", "2026-11-04")]
    [InlineData("yesterday", "2026-11-02")]
    [InlineData("+3", "2026-11-06")]
    [InlineData("+3d", "2026-11-06")]
    [InlineData("+2w", "2026-11-17")]
    [InlineData("+1m", "2026-12-03")]
    [InlineData("-1d", "2026-11-02")]
    [InlineData("fri", "2026-11-06")]
    [InlineData("Friday", "2026-11-06")]
    [InlineData("tue", "2026-11-03")]
    [InlineData("mon", "2026-11-09")]
    [InlineData("+2fri", "2026-11-13")]
    [InlineData("2026-12-25", "2026-12-25")]
    [InlineData("12/25", "2026-12-25")]
    [InlineData("12/25/27", "2027-12-25")]
    [InlineData("20", "2026-11-20")]
    [InlineData("1", "2026-12-01")]
    [InlineData("nov 20", "2026-11-20")]
    [InlineData("20 November", "2026-11-20")]
    [InlineData("jan 5", "2027-01-05")]
    [InlineData("feb 3 2028", "2028-02-03")]
    [InlineData("2026-11-20 Fri", "2026-11-20")]
    public void Dates_are_read_as_org_reads_them(string input, string expected)
    {
        var ts = OrgDateInput.Parse(input, Today, out var error);
        Assert.Null(error);
        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ts!.Date);
    }

    [Theory]
    [InlineData("14:00", "14:00", null)]
    [InlineData("9:30", "09:30", null)]
    [InlineData("2pm", "14:00", null)]
    [InlineData("10:30am", "10:30", null)]
    [InlineData("12am", "00:00", null)]
    [InlineData("9 am", "09:00", null)]
    [InlineData("10:00-11:30", "10:00", "11:30")]
    [InlineData("9-11am", "09:00", "11:00")]
    [InlineData("fri 2pm-3pm", "14:00", "15:00")]
    public void Times_are_read(string input, string start, string? end)
    {
        var ts = OrgDateInput.Parse(input, Today, out var error);
        Assert.Null(error);
        Assert.Equal(TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture), ts!.Start);
        Assert.Equal(end is null ? null : TimeOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture), ts.End);
    }

    [Fact]
    public void After_a_date_plus_is_a_repeater_and_minus_a_warning()
    {
        var ts = OrgDateInput.Parse("fri 10:00 +1w -2d", Today, out _)!;
        Assert.Equal("<2026-11-06 Fri 10:00 +1w -2d>", ts.ToString());
        Assert.Equal("<2026-11-03 Tue .+3d>", OrgDateInput.Parse("today .+3d", Today, out _)!.ToString());
        // what a prompt prefills (a timestamp without its brackets) reads back as itself
        Assert.Equal("<2026-11-20 Fri 09:00 ++1m>", OrgDateInput.Parse("2026-11-20 Fri 09:00 ++1m", Today, out _)!.ToString());
    }

    [Theory]
    [InlineData("someday")]
    [InlineData("2026-02-30")]
    [InlineData("25:00")]
    public void Nonsense_is_an_error(string input)
    {
        Assert.Null(OrgDateInput.Parse(input, Today, out var error));
        Assert.NotNull(error);
    }
}
