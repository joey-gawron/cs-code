using CsCode.Terminal.Emulator;
using Xunit;

namespace CsCode.Terminal.Tests;

/// <summary>
/// Find in the emulator: regular expressions, whole words, case, matches across soft-wrapped rows, every match in a range (highlighting),
/// counting with the cap, and expressions that are invalid, empty-matching or on the alternate screen.
/// </summary>
public sealed class FindTests
{
    private static VtEmulator Lines(int columns, params string[] lines)
    {
        var e = new VtEmulator(columns, 10);
        e.Feed(string.Join("\r\n", lines));
        return e;
    }

    private static TerminalFindOptions Q(string query, bool matchCase = false, bool wholeWord = false, bool regex = false) => new(query, matchCase, wholeWord, regex);

    [Fact]
    public void Regular_expressions_find_the_whole_match()
    {
        var e = Lines(40, "error 42 here", "warning 7", "error 1000 there");
        Assert.Equal(new TerminalMatch(2, 0, 9), e.Find(Q(@"error \d+", regex: true), long.MaxValue, 0, backward: true));
        Assert.Equal(new TerminalMatch(0, 0, 7), e.Find(Q(@"error \d+", regex: true), 2, 0, backward: true));
        Assert.Equal(new TerminalMatch(1, 0, 8), e.Find(Q(@"^w\w+ \d$", regex: true), long.MinValue, 0, backward: false));
        // case: off by default, on when asked
        Assert.NotNull(e.Find(Q("ERROR", regex: true), long.MaxValue, 0, backward: true));
        Assert.Null(e.Find(Q("ERROR", matchCase: true, regex: true), long.MaxValue, 0, backward: true));
    }

    [Fact]
    public void Whole_word_skips_matches_inside_words()
    {
        var e = Lines(40, "cat concat cats cat_ (cat)");
        var all = e.FindAll(Q("cat", wholeWord: true), 0, 0);
        Assert.Equal([new TerminalMatch(0, 0, 2), new TerminalMatch(0, 22, 24)], all);
        Assert.Equal(5, e.FindAll(Q("cat"), 0, 0).Count);
        Assert.Equal(2, e.FindAll(Q(@"c\w+", wholeWord: true, regex: true), 0, 0).Count(m => m.StartColumn is 0 or 4)); // "cat", "concat"
    }

    [Fact]
    public void A_match_can_continue_on_the_next_row_of_a_wrapped_line()
    {
        // 10 columns: "0123456789" / "abcdeneedl" / "e done": "needle" starts on row 1 and ends on row 2
        var e = Lines(10, "0123456789abcdeneedle done");
        Assert.True(e.GetScreenRow(2).IsWrapped);
        var m = e.Find(Q("needle"), long.MinValue, 0, backward: false);
        Assert.Equal(new TerminalMatch(1, 5, 2, 0), m);
        Assert.Equal((5, 9), m!.Value.ColumnsOn(1, 10));
        Assert.Equal((0, 0), m.Value.ColumnsOn(2, 10));
        Assert.Null(m.Value.ColumnsOn(0, 10));
        Assert.Equal(m, e.FindAll(Q("needle"), 2, 2).Single()); // a range touching only its last row finds it too
        // the blank a line wrapped on is part of it
        var spaced = Lines(10, "aaaaaaaaa next row");
        Assert.Equal(new TerminalMatch(0, 9, 1, 3), spaced.Find(Q(" next"), long.MinValue, 0, backward: false));
        // a hard line break is not
        Assert.Null(Lines(10, "need", "le").Find(Q("needle"), long.MinValue, 0, backward: false));
    }

    [Fact]
    public void Every_match_of_the_rows_in_view_and_the_count_with_the_current_ones_position()
    {
        var e = new VtEmulator(20, 5);
        e.Feed(string.Concat(Enumerable.Range(0, 40).Select(i => $"x{i} needle needle\r\n")));
        var top = e.ScreenTop;
        var inView = e.FindAll(Q("needle"), top, top + 4);
        Assert.Equal(8, inView.Count); // 4 rows written on screen, 2 matches each (the last row is the empty one after the final newline)
        Assert.All(inView, m => Assert.InRange(m.Line, top, top + 4));
        var current = e.Find(Q("needle"), long.MaxValue, 0, backward: true);
        Assert.Equal(new TerminalFindCount(80, 80, false), e.CountMatches(Q("needle"), current));
        Assert.Equal(new TerminalFindCount(80, 1, false), e.CountMatches(Q("needle"), e.Find(Q("needle"), long.MinValue, 0, backward: false)));

        var many = new VtEmulator(80, 5, new EmulatorOptions { ScrollbackLines = 2000 });
        many.Feed(string.Concat(Enumerable.Range(0, 1200).Select(_ => "hit\r\n")));
        var capped = many.CountMatches(Q("hit"), null);
        Assert.True(capped.Capped);
        Assert.Equal(VtEmulator.MaxFindCount, capped.Total);
    }

    [Fact]
    public void Wrapping_around_and_a_single_match_finding_itself_again()
    {
        var e = Lines(40, "one", "two", "one");
        var last = e.Find(Q("one"), long.MaxValue, 0, backward: true);
        Assert.Equal(new TerminalMatch(2, 0, 2), last);
        Assert.Equal(new TerminalMatch(0, 0, 2), e.Find(Q("one"), 2, 0, backward: true));
        Assert.Equal(new TerminalMatch(2, 0, 2), e.Find(Q("one"), 0, 0, backward: true)); // past the top: the bottom
        Assert.Equal(new TerminalMatch(0, 0, 2), e.Find(Q("one"), 2, 0, backward: false)); // past the bottom: the top
        var single = e.Find(Q("two"), long.MaxValue, 0, backward: true);
        Assert.Equal(single, e.Find(Q("two"), single!.Value.Line, single.Value.StartColumn, backward: true));
        Assert.Equal(single, e.Find(Q("two"), single.Value.Line, single.Value.StartColumn, backward: false));
    }

    [Fact]
    public void Invalid_or_empty_matching_expressions_find_nothing_and_never_throw()
    {
        var e = Lines(40, "abc", "def");
        var invalid = Q("(unclosed", regex: true);
        Assert.True(invalid.IsInvalid);
        Assert.Null(e.Find(invalid, long.MaxValue, 0, backward: true));
        Assert.Empty(e.FindAll(invalid, 0, 5));
        Assert.Equal(default, e.CountMatches(invalid, null));
        // matches of length zero are skipped, the others found
        Assert.Equal([new TerminalMatch(1, 1, 1)], e.FindAll(Q("e*", regex: true), 0, 5));
        Assert.False(Q("plain (text").IsInvalid); // not a regular expression: literal
        Assert.Null(e.Find(Q("plain (text"), long.MaxValue, 0, backward: true));
    }

    [Fact]
    public void On_the_alternate_screen_only_its_rows_are_searched()
    {
        var e = new VtEmulator(40, 5);
        e.Feed("needle in the scrollback\r\n");
        e.Feed("\e[?1049h\e[Hfull screen needle");
        var all = e.FindAll(Q("needle"), 0, 4);
        Assert.Equal([new TerminalMatch(0, 12, 17)], all);
    }
}
