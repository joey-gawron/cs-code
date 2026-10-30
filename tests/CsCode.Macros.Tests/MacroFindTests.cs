using Xunit;

namespace CsCode.Macros.Tests;

/// <summary>The search of a recorded Find step: never wraps, steps past the previous match, honours case, whole words and regular expressions.</summary>
public class MacroFindTests
{
    private const string Text = "foo bar Foo foobar foo";

    [Fact]
    public void Forwards_from_the_caret_then_from_the_end_of_each_match_and_never_wraps()
    {
        var args = new MacroFindArgs("foo");
        var hits = new List<int>();
        int start = 0, end = 0;
        while (MacroFind.Search(Text, start, end, args) is { } m)
        {
            hits.Add(m.Start);
            (start, end) = (m.Start + m.Length, m.Start); // the match selected with the caret at its start (as the editor's find does)
        }
        Assert.Equal([0, 8, 12, 19], hits); // case-insensitive by default; stops at the end
    }

    [Fact]
    public void Backwards_from_the_caret_or_the_start_of_the_selection()
    {
        var args = new MacroFindArgs("foo", MatchCase: true, Backwards: true);
        Assert.Equal((12, 3), MacroFind.Search(Text, 19, 22, args));
        Assert.Equal((0, 3), MacroFind.Search(Text, 12, 12, args));
        Assert.Null(MacroFind.Search(Text, 0, 0, args));
    }

    [Fact]
    public void Match_case_whole_word_and_regex()
    {
        Assert.Equal((8, 3), MacroFind.Search(Text, 1, 1, new MacroFindArgs("Foo", MatchCase: true)));
        Assert.Equal((19, 3), MacroFind.Search(Text, 9, 9, new MacroFindArgs("foo", MatchCase: true, WholeWord: true)));
        Assert.Equal((4, 3), MacroFind.Search(Text, 0, 0, new MacroFindArgs("b[a-z]r", Regex: true)));
        Assert.Null(MacroFind.Search("axb", 0, 0, new MacroFindArgs("a.b"))); // not a regex: the dot is literal
        Assert.Equal((0, 3), MacroFind.Search("a.b", 0, 0, new MacroFindArgs("a.b")));
        Assert.Null(MacroFind.Search(Text, 0, 0, new MacroFindArgs("")));
        Assert.ThrowsAny<ArgumentException>(() => MacroFind.Search(Text, 0, 0, new MacroFindArgs("(", Regex: true)));
    }

    [Fact]
    public void An_empty_regex_match_does_not_repeat_at_the_place_of_the_previous_one()
    {
        var args = new MacroFindArgs("^", Regex: true);
        Assert.Equal((0, 0), MacroFind.Search("a\nb", 0, 0, args));
        Assert.Equal((2, 0), MacroFind.Search("a\nb", 1, 0, args)); // the previous (empty) match at 0 selected: the next line's start
    }
}
