using AvaloniaEdit.Document;
using CsCode.Core.Languages;
using CsCode.Editor.Editing;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// The multi-caret model without a view: <see cref="CaretSet"/> (sorting, merging, the limit, the primary), <see cref="MultiCaretOperations"/>
/// (each edit and movement at every caret, the carets after a batch) and <see cref="MatchFinder"/> (occurrences across chunk seams, whole words,
/// case, wrapping).
/// </summary>
public class MultiCaretModelTests
{
    private static CaretSet Set(int primary, params CaretRange[] carets) => CaretSet.Create(carets, primary);

    private static CaretSet Carets(params int[] offsets) => CaretSet.Create(offsets.Select(CaretRange.At).ToArray(), 0);

    /// <summary>Applies a batch to a document (as the view does) and returns its text.</summary>
    private static string Apply(TextDocument doc, CaretEditBatch batch)
    {
        MultiCaretOperations.Apply(doc, batch.Edits);
        return doc.Text;
    }

    private static int[] Actives(CaretEditBatch batch) => batch.Carets.Select(c => c.Active).ToArray();

    private static readonly EditingContext Ctx = new(new LanguageConfiguration(), TabSize: 4, InsertSpaces: true, Eol: "\n");

    // ------------------------------------------------------------------ CaretSet

    [Fact]
    public void Carets_are_kept_sorted_and_the_primary_follows_its_caret()
    {
        var set = Set(0, CaretRange.At(9), CaretRange.At(2), CaretRange.At(5));
        Assert.Equal([2, 5, 9], set.Carets.Select(c => c.Active));
        Assert.Equal(9, set.Primary.Active);
        Assert.True(set.IsMulti);
    }

    [Fact]
    public void Carets_at_one_offset_and_overlapping_selections_merge_but_touching_selections_stay_apart()
    {
        Assert.Equal(1, Set(0, CaretRange.At(4), CaretRange.At(4)).Count);
        var merged = Set(1, CaretRange.Select(0, 5), CaretRange.Select(3, 8));
        Assert.Single(merged.Carets);
        Assert.Equal((0, 8), (merged[0].Start, merged[0].End));
        Assert.Equal(2, Set(0, CaretRange.Select(0, 5), CaretRange.Select(5, 8)).Count);
        // a caret touching a selection joins it
        Assert.Equal(1, Set(0, CaretRange.Select(0, 5), CaretRange.At(5)).Count);
        Assert.Equal(1, Set(0, CaretRange.Select(3, 5), CaretRange.At(3)).Count);
    }

    [Fact]
    public void A_merged_caret_keeps_the_primarys_direction_and_stays_primary()
    {
        var set = Set(0, new CaretRange(6, 2), CaretRange.Select(4, 9), CaretRange.At(20));
        Assert.Equal(2, set.Count);
        Assert.Equal(0, set.PrimaryIndex);
        Assert.Equal(new CaretRange(9, 2), set.Primary);
    }

    [Fact]
    public void The_limit_keeps_carets_around_the_primary_and_says_so()
    {
        var carets = Enumerable.Range(0, 100).Select(i => CaretRange.At(i * 2)).ToArray();
        var set = CaretSet.Create(carets, 50, limit: 10);
        Assert.Equal(10, set.Count);
        Assert.True(set.WasLimited);
        Assert.Equal(100, set.Primary.Active);
        Assert.False(CaretSet.Create(carets, 0, limit: 100).WasLimited);
    }

    [Fact]
    public void Add_makes_the_new_caret_primary_and_Remove_moves_the_primary_to_a_neighbour()
    {
        var set = Carets(1, 5).Add(CaretRange.At(3));
        Assert.Equal(3, set.Primary.Active);
        var removed = set.Remove(set.PrimaryIndex);
        Assert.Equal([1, 5], removed.Carets.Select(c => c.Active));
        Assert.Equal(5, removed.Primary.Active);
    }

    [Fact]
    public void IndexAt_and_FirstEndingAtOrAfter_are_binary_searches_over_the_ranges()
    {
        var set = Set(0, CaretRange.Select(2, 4), CaretRange.At(10), CaretRange.Select(20, 30));
        Assert.Equal(0, set.IndexAt(3));
        Assert.Equal(1, set.IndexAt(10));
        Assert.Equal(-1, set.IndexAt(11));
        Assert.Equal(2, set.IndexAt(30));
        Assert.Equal(1, set.FirstEndingAtOrAfter(5));
        Assert.Equal(2, set.FirstEndingAtOrAfter(11));
        Assert.Equal(3, set.FirstEndingAtOrAfter(31));
    }

    // ------------------------------------------------------------------ edits

    [Fact]
    public void Typing_inserts_at_every_caret_and_replaces_every_selection_with_carets_after_each_insertion()
    {
        var doc = new TextDocument("one two three");
        var set = Set(0, CaretRange.At(0), CaretRange.Select(4, 7), CaretRange.At(13));
        var batch = MultiCaretOperations.Type(set, "X");
        Assert.Equal("Xone X threeX", Apply(doc, batch));
        Assert.Equal([1, 6, 13], Actives(batch));
        Assert.All(batch.Carets, c => Assert.True(c.IsEmpty));
    }

    [Fact]
    public void Backspace_and_Delete_remove_one_character_per_caret_treating_CRLF_and_surrogate_pairs_as_one()
    {
        var doc = new TextDocument("ab\r\ncd😀e");
        // carets after the CRLF and after the emoji
        var set = Carets(4, 8);
        var batch = MultiCaretOperations.Backspace(doc, set, word: false);
        Assert.Equal("abcde", Apply(doc, batch));
        Assert.Equal([2, 4], Actives(batch));

        var doc2 = new TextDocument("ab\r\ncd😀e");
        var del = MultiCaretOperations.Delete(doc2, Carets(2, 6), word: false);
        Assert.Equal("abcde", Apply(doc2, del));
    }

    [Fact]
    public void Word_deletes_reaching_over_other_carets_merge_into_one_edit()
    {
        var doc = new TextDocument("call someLongName(x)");
        // two carets inside one word: both delete back to its start
        var batch = MultiCaretOperations.Backspace(doc, Carets(9, 13), word: true);
        Assert.Single(batch.Edits);
        Assert.Equal("call Name(x)", Apply(doc, batch));
        Assert.All(batch.Carets, c => Assert.Equal(5, c.Active));
        Assert.Equal(1, CaretSet.Create(batch.Carets, 0).Count); // they merge
    }

    [Fact]
    public void Enter_indents_each_new_line_by_the_language_rules_and_splits_brackets()
    {
        var lang = new LanguageConfiguration { Brackets = [("{", "}")] };
        var ctx = new EditingContext(lang, 4, true, "\n");
        var doc = new TextDocument("    a{}\n    b");
        var set = Carets(6, 13);
        var batch = MultiCaretOperations.Enter(doc, set, ctx);
        Assert.Equal("    a{\n        \n    }\n    b\n    ", Apply(doc, batch));
        Assert.Equal([15, doc.TextLength], Actives(batch));
    }

    [Fact]
    public void Tab_inserts_spaces_to_the_next_tab_stop_per_caret_and_indents_lines_of_multi_line_selections()
    {
        var doc = new TextDocument("ab\nabc\n");
        var batch = MultiCaretOperations.Tab(doc, Carets(2, 6), Ctx);
        Assert.Equal("ab  \nabc \n", Apply(doc, batch));

        var doc2 = new TextDocument("x\ny\nz");
        var set = Set(0, CaretRange.Select(0, 3), CaretRange.At(4));
        var indent = MultiCaretOperations.Tab(doc2, set, Ctx);
        Assert.Equal("    x\n    y\n    z", Apply(doc2, indent));
        Assert.Equal((0, 11), (indent.Carets[0].Start, indent.Carets[0].End)); // the selection still covers both lines, from column 0

        var outdent = MultiCaretOperations.IndentLines(doc2, CaretSet.Create(indent.Carets, 0), Ctx, outdent: true);
        Assert.Equal("x\ny\nz", Apply(doc2, outdent));
    }

    [Fact]
    public void Insert_puts_one_text_per_caret_and_Mapped_moves_carets_past_edits_before_them()
    {
        var doc = new TextDocument("a\nb\nc");
        var spread = MultiCaretOperations.Insert(Carets(1, 3, 5), ["1", "22", "333"]);
        Assert.Equal("a1\nb22\nc333", Apply(doc, spread));
        Assert.Equal([2, 6, 11], Actives(spread));

        var doc2 = new TextDocument("a\nb");
        var mapped = MultiCaretOperations.Mapped(Carets(0, 3), [new OffsetEdit(0, 0, "x\n"), new OffsetEdit(2, 2, "y\n")], keepSelections: false);
        Assert.Equal("x\na\ny\nb", Apply(doc2, mapped));
        Assert.Equal([2, 7], Actives(mapped)); // each caret stays on its own line, after the inserted one
    }

    [Fact]
    public void Map_follows_VS_Code_rules_for_offsets_at_and_inside_edits()
    {
        OffsetEdit[] edits = [new(2, 2, "ab"), new(5, 8, "")];
        var deltas = new[] { 0, 2, -1 };
        Assert.Equal(1, MultiCaretOperations.Map(edits, deltas, 1));
        Assert.Equal(4, MultiCaretOperations.Map(edits, deltas, 2)); // at an insertion: after it
        Assert.Equal(7, MultiCaretOperations.Map(edits, deltas, 5)); // a deletion starting here: its start
        Assert.Equal(7, MultiCaretOperations.Map(edits, deltas, 6)); // inside the deletion
        Assert.Equal(8, MultiCaretOperations.Map(edits, deltas, 9));
    }

    // ------------------------------------------------------------------ movement

    [Fact]
    public void Arrows_move_every_caret_collapse_selections_to_their_side_and_shift_extends()
    {
        var doc = new TextDocument("abc\ndef");
        var right = MultiCaretOperations.MoveHorizontal(doc, Carets(0, 4), forward: true, word: false, extend: false);
        Assert.Equal([1, 5], right.Carets.Select(c => c.Active));
        var extended = MultiCaretOperations.MoveHorizontal(doc, right, forward: true, word: false, extend: true);
        Assert.Equal([(1, 2), (5, 6)], extended.Carets.Select(c => (c.Anchor, c.Active)));
        var collapsed = MultiCaretOperations.MoveHorizontal(doc, extended, forward: false, word: false, extend: false);
        Assert.Equal([1, 5], collapsed.Carets.Select(c => c.Active));
    }

    [Fact]
    public void Carets_that_meet_while_moving_merge()
    {
        var doc = new TextDocument("abcdef");
        var moved = MultiCaretOperations.MoveHorizontal(doc, Carets(0, 1), forward: false, word: false, extend: false);
        Assert.Equal(1, moved.Count);
        Assert.Equal(0, moved.Primary.Active);
    }

    [Fact]
    public void Up_and_Down_keep_the_visual_column_over_short_lines_and_tabs()
    {
        var doc = new TextDocument("abcdefgh\nab\n\tcdefgh");
        var set = CaretSet.Single(CaretRange.At(6));
        var down = MultiCaretOperations.MoveVertical(doc, set, down: true, extend: false, tabSize: 4);
        Assert.Equal(11, down.Primary.Active); // end of the short line
        var down2 = MultiCaretOperations.MoveVertical(doc, down, down: true, extend: false, tabSize: 4);
        Assert.Equal(doc.GetLineByNumber(3).Offset + 3, down2.Primary.Active); // column 6 = tab (4) + "cd"
        var up = MultiCaretOperations.MoveVertical(doc, CaretSet.Single(CaretRange.At(3)), down: false, extend: false, tabSize: 4);
        Assert.Equal(0, up.Primary.Active); // first line: Up goes to its start
    }

    [Fact]
    public void Home_is_smart_and_End_goes_to_each_line_end()
    {
        var doc = new TextDocument("    abc\n  de");
        var home = MultiCaretOperations.MoveHome(doc, Carets(6, 12), extend: false);
        Assert.Equal([4, 10], home.Carets.Select(c => c.Active));
        var home2 = MultiCaretOperations.MoveHome(doc, home, extend: false);
        Assert.Equal([0, 8], home2.Carets.Select(c => c.Active));
        var end = MultiCaretOperations.MoveEnd(doc, home2, extend: true);
        Assert.Equal([(0, 7), (8, 12)], end.Carets.Select(c => (c.Anchor, c.Active)));
    }

    [Fact]
    public void Word_moves_use_AvaloniaEdits_word_stops()
    {
        var doc = new TextDocument("foo bar baz");
        var right = MultiCaretOperations.MoveHorizontal(doc, Carets(0, 4), forward: true, word: true, extend: false);
        Assert.Equal([4, 8], right.Carets.Select(c => c.Active));
        Assert.Equal(TextUtilities.GetNextCaretPosition(doc, 0, LogicalDirection.Forward, CaretPositioningMode.WordStart), right[0].Active);
    }

    [Fact]
    public void Insert_carets_below_adds_one_per_caret_at_the_same_column_and_the_lowest_is_primary()
    {
        var doc = new TextDocument("abcd\nab\nabcd");
        var set = CaretSet.Single(CaretRange.At(3));
        var once = MultiCaretOperations.InsertCaretsVertically(doc, set, below: true, tabSize: 4, limit: 100);
        Assert.Equal([3, 7], once.Carets.Select(c => c.Active));
        Assert.Equal(7, once.Primary.Active);
        var twice = MultiCaretOperations.InsertCaretsVertically(doc, once, below: true, tabSize: 4, limit: 100);
        Assert.Equal([3, 7, 11], twice.Carets.Select(c => c.Active)); // the column is remembered across the short line
        var single = CaretSet.Single(CaretRange.At(0));
        Assert.Same(single, MultiCaretOperations.InsertCaretsVertically(new TextDocument("x"), single, below: true, tabSize: 4, limit: 100)); // no line below
    }

    // ------------------------------------------------------------------ MatchFinder

    private static (int, Func<int, int, ReadOnlyMemory<char>>) Source(string text) => (text.Length, (o, l) => text.AsMemory(o, l));

    [Fact]
    public void FindNext_wraps_and_skips_rejected_matches()
    {
        var (len, read) = Source("foo bar foo baz foo");
        var q = new OccurrenceQuery("foo", false, true);
        Assert.Equal(8, MatchFinder.FindNext(len, read, q, 3));
        Assert.Equal(0, MatchFinder.FindNext(len, read, q, 17)); // wraps
        Assert.Equal(16, MatchFinder.FindNext(len, read, q, 3, start => start != 8));
        Assert.Equal(-1, MatchFinder.FindNext(len, read, new OccurrenceQuery("nope", false, true), 0));
    }

    [Fact]
    public void Whole_word_and_case_are_honoured()
    {
        var (len, read) = Source("Foo food foo _foo foo1 FOO");
        var (strict, _) = MatchFinder.FindAll(len, read, new OccurrenceQuery("foo", WholeWord: true, MatchCase: true), 100);
        Assert.Equal([9], strict);
        var (loose, _) = MatchFinder.FindAll(len, read, new OccurrenceQuery("foo", WholeWord: false, MatchCase: false), 100);
        Assert.Equal(6, loose.Count);
    }

    [Fact]
    public void Matches_across_chunk_seams_are_found_and_FindAll_stops_at_the_limit()
    {
        var text = new string('x', (1 << 16) - 2) + "needle" + new string('y', 100) + "needle";
        var (len, read) = Source(text);
        var (all, limited) = MatchFinder.FindAll(len, read, new OccurrenceQuery("needle", false, true), 100);
        Assert.Equal([(1 << 16) - 2, (1 << 16) + 104], all);
        Assert.False(limited);
        var (one, cut) = MatchFinder.FindAll(len, read, new OccurrenceQuery("needle", false, true), 1);
        Assert.Single(one);
        Assert.True(cut);
    }
}
