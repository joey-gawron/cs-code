using AvaloniaEdit.Document;
using CsCode.Editor.Editing;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// The second round of multi-caret editing without a view: a typed character with a decision per caret (pair, over-type, surround, plain),
/// Backspace to the previous indentation stop (<c>editor.useTabStops</c>) and between an auto-inserted pair, and Up/Down by wrapped rows.
/// </summary>
public class MultiCaretEditingModelTests
{
    private static CaretSet Carets(params int[] offsets) => CaretSet.Create(offsets.Select(CaretRange.At).ToArray(), 0);

    private static string Apply(TextDocument doc, CaretEditBatch batch)
    {
        MultiCaretOperations.Apply(doc, batch.Edits);
        return doc.Text;
    }

    private static readonly AutoCloseDecision Plain = AutoCloseDecision.None;
    private static readonly AutoCloseDecision Pair = new(AutoCloseKind.InsertPair, "(", ")");
    private static readonly AutoCloseDecision Over = new(AutoCloseKind.OverType, "", ")");
    private static readonly AutoCloseDecision Wrap = new(AutoCloseKind.Surround, "(", ")");

    // ------------------------------------------------------------------ typing with a decision per caret

    [Fact]
    public void Each_caret_gets_its_own_decision_pair_plain_or_over_type()
    {
        var doc = new TextDocument("foo\nfoobar\nf()");
        var set = Carets(3, 7, 13); // "foo|", "foo|bar", "f(|)"
        var batch = MultiCaretOperations.TypeWithDecisions(set, "(", [Pair, Plain, Over]);
        Assert.Equal("foo()\nfoo(bar\nf()", Apply(doc, batch));
        Assert.Equal([4, 10, 17], batch.Carets.Select(c => c.Active)); // between the pair; after the plain "("; over the closer
    }

    [Fact]
    public void Surround_wraps_every_selection_and_keeps_the_inner_text_selected_in_its_direction()
    {
        var doc = new TextDocument("one two");
        var set = CaretSet.Create([CaretRange.Select(0, 3), new CaretRange(7, 4)], 0);
        var batch = MultiCaretOperations.TypeWithDecisions(set, "(", [Wrap, Wrap]);
        Assert.Equal("(one) (two)", Apply(doc, batch));
        Assert.Equal(new CaretRange(1, 4), batch.Carets[0]);
        Assert.Equal(new CaretRange(10, 7), batch.Carets[1]); // reversed stays reversed
    }

    [Fact]
    public void Surrounding_two_touching_selections_puts_the_closer_before_the_next_opener()
    {
        var doc = new TextDocument("abcd");
        var set = CaretSet.Create([CaretRange.Select(0, 2), CaretRange.Select(2, 4)], 0);
        var batch = MultiCaretOperations.TypeWithDecisions(set, "(", [Wrap, Wrap]);
        Assert.Equal("(ab)(cd)", Apply(doc, batch));
        Assert.Equal([(1, 3), (5, 7)], batch.Carets.Select(c => (c.Start, c.End)));
    }

    [Fact]
    public void Over_typing_at_every_caret_makes_no_edit()
    {
        var set = Carets(1, 4);
        var batch = MultiCaretOperations.TypeWithDecisions(set, ")", [Over, Over]);
        Assert.Empty(batch.Edits);
        Assert.Equal([2, 5], batch.Carets.Select(c => c.Active));
    }

    // ------------------------------------------------------------------ Backspace

    [Theory]
    [InlineData("        x", 8, 4)]   // two stops in: back to the previous one
    [InlineData("      x", 6, 4)]     // between stops: back to the stop before
    [InlineData("    x", 4, 0)]
    [InlineData("  x", 2, 0)]
    [InlineData("\t    x", 5, 1)]     // a tab, then four spaces (tab size 4): column 8 back to 4, after the tab
    public void Backspace_in_leading_whitespace_goes_back_to_the_previous_indentation_stop(string line, int caret, int expectedStop)
    {
        var doc = new TextDocument(line);
        Assert.Equal(expectedStop, MultiCaretOperations.IndentStopBefore(doc, caret, 4));
    }

    [Fact]
    public void Backspace_after_text_or_at_the_line_start_has_no_indentation_stop()
    {
        var doc = new TextDocument("ab    \nx");
        Assert.Null(MultiCaretOperations.IndentStopBefore(doc, 6, 4)); // after non-blank text
        Assert.Null(MultiCaretOperations.IndentStopBefore(doc, 7, 4)); // the line's start
    }

    [Fact]
    public void Backspace_at_several_carets_uses_tab_stops_pairs_and_single_characters_each_by_its_own_context()
    {
        var doc = new TextDocument("        a\nb()\ncd");
        // a caret in leading whitespace (column 8), one between an auto-inserted pair, one after text
        var set = Carets(8, 12, 16);
        var batch = MultiCaretOperations.Backspace(doc, set, word: false, tabSize: 4, useTabStops: true, autoClosedPairAt: o => o == 12);
        Assert.Equal("    a\nb\nc", Apply(doc, batch));
        Assert.Equal([4, 7, 9], batch.Carets.Select(c => c.Active));
    }

    [Fact]
    public void Backspace_without_tab_stops_deletes_one_space()
    {
        var doc = new TextDocument("        a\n        b");
        var batch = MultiCaretOperations.Backspace(doc, Carets(8, 18), word: false, tabSize: 4, useTabStops: false);
        Assert.Equal("       a\n       b", Apply(doc, batch));
    }

    // ------------------------------------------------------------------ Up / Down by wrapped rows

    /// <summary>Rows of 10 characters (a fake layout): a 25-character line is three rows starting at 0, 10 and 20.</summary>
    private static Func<DocumentLine, IReadOnlyList<int>> TenColumnRows => line =>
    {
        var starts = new List<int> { line.Offset };
        for (var at = line.Offset + 10; at < line.EndOffset; at += 10) starts.Add(at);
        return starts;
    };

    [Fact]
    public void Down_moves_to_the_next_wrapped_row_of_the_same_line_keeping_the_column_in_the_row()
    {
        var line = new string('a', 25);
        var doc = new TextDocument(line + "\n" + line);
        var set = Carets(3, 29); // column 3 of each line's first row
        var down = MultiCaretOperations.MoveVertical(doc, set, down: true, extend: false, tabSize: 4, TenColumnRows);
        Assert.Equal([13, 39], down.Carets.Select(c => c.Active)); // row 2, column 3 of each line
        down = MultiCaretOperations.MoveVertical(doc, down, down: true, extend: false, tabSize: 4, TenColumnRows);
        Assert.Equal([23, 49], down.Carets.Select(c => c.Active)); // row 3
        down = MultiCaretOperations.MoveVertical(doc, down, down: true, extend: false, tabSize: 4, TenColumnRows);
        Assert.Equal([29, 51], down.Carets.Select(c => c.Active)); // the next line's first row; the last line's last row goes to the end
    }

    [Fact]
    public void Up_from_a_first_row_goes_to_the_previous_lines_last_row_and_a_long_column_stops_before_the_wrap()
    {
        var doc = new TextDocument(new string('a', 25) + "\n" + new string('b', 25));
        var set = CaretSet.Create([new CaretRange(35, 35, 9)], 0); // line 2, row 1, column 9
        var up = MultiCaretOperations.MoveVertical(doc, set, down: false, extend: false, tabSize: 4, TenColumnRows);
        Assert.Equal(25, up.Primary.Active); // the last row of line 1 has 5 characters: its end
        up = MultiCaretOperations.MoveVertical(doc, up, down: false, extend: false, tabSize: 4, TenColumnRows);
        Assert.Equal(19, up.Primary.Active); // row 2 of line 1, column 9: before its last character, so it stays on that row
        Assert.Equal(9, up.Primary.DesiredColumn);
    }

    [Fact]
    public void Shift_Down_by_rows_extends_each_selection()
    {
        var doc = new TextDocument(new string('a', 25));
        var set = Carets(2);
        var down = MultiCaretOperations.MoveVertical(doc, set, down: true, extend: true, tabSize: 4, TenColumnRows);
        Assert.Equal(new CaretRange(2, 12, 2), down.Primary);
    }
}
