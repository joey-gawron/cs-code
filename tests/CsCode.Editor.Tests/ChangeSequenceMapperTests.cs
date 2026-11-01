using AvaloniaEdit.Document;
using CsCode.Editor.Editing;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// <see cref="ChangeSequenceMapper"/> maps every offset exactly as mapping it through each change in turn does: for batches applied back to front,
/// their undo replayed front to back (both by binary search) and changes in any other order (change by change), including adjacent edits,
/// insertions, deletions and offsets at every edge of a change.
/// </summary>
public class ChangeSequenceMapperTests
{
    /// <summary>The change events of one document update that applies <paramref name="edits"/> in that order.</summary>
    private static (List<DocumentChangeEventArgs> Changes, int Length) Record(string text, IEnumerable<(int Offset, int Remove, string Insert)> edits)
    {
        var doc = new TextDocument(text);
        var changes = new List<DocumentChangeEventArgs>();
        doc.Changed += (_, e) => changes.Add(e);
        doc.BeginUpdate();
        foreach (var (offset, remove, insert) in edits) doc.Replace(offset, remove, insert);
        doc.EndUpdate();
        return (changes, text.Length);
    }

    private static void AssertSameAsSequential(List<DocumentChangeEventArgs> changes, int length, bool ordered)
    {
        var mapper = new ChangeSequenceMapper(changes);
        Assert.Equal(ordered, mapper.IsOrdered);
        for (var x = 0; x <= length; x++)
        {
            var expected = x;
            foreach (var c in changes) expected = c.GetNewOffset(expected, AnchorMovementType.Default);
            Assert.Equal(expected, mapper.Map(x));
        }
    }

    /// <summary>Random non-overlapping (possibly adjacent or empty) edits, sorted by offset, in the original text's offsets.</summary>
    private static List<(int Offset, int Remove, string Insert)> RandomEdits(Random rng, int length)
    {
        var edits = new List<(int, int, string)>();
        var at = 0;
        while (true)
        {
            at += rng.Next(0, 4);
            if (at > length) break;
            var remove = Math.Min(rng.Next(0, 3), length - at);
            var insert = new string('+', rng.Next(0, 3));
            if (remove == 0 && insert.Length == 0) insert = "+";
            edits.Add((at, remove, insert));
            at += remove + rng.Next(1, 3); // at least one character between edits: two edits never start at the same offset
        }
        return edits;
    }

    [Fact]
    public void A_batch_applied_back_to_front_maps_by_binary_search_exactly_as_change_by_change()
    {
        var rng = new Random(7);
        for (var round = 0; round < 200; round++)
        {
            var text = new string('a', rng.Next(1, 60));
            var edits = RandomEdits(rng, text.Length);
            edits.Reverse();
            var (changes, length) = Record(text, edits);
            AssertSameAsSequential(changes, length, ordered: true);
        }
    }

    [Fact]
    public void The_undo_of_such_a_batch_replayed_front_to_back_maps_by_binary_search_exactly_as_change_by_change()
    {
        var rng = new Random(11);
        var orderedRounds = 0;
        for (var round = 0; round < 200; round++)
        {
            var text = new string('a', rng.Next(1, 60));
            var doc = new TextDocument(text);
            doc.BeginUpdate();
            foreach (var (offset, remove, insert) in Enumerable.Reverse(RandomEdits(rng, text.Length))) doc.Replace(offset, remove, insert);
            doc.EndUpdate();
            var changes = new List<DocumentChangeEventArgs>();
            doc.Changed += (_, e) => changes.Add(e);
            var length = doc.TextLength;
            doc.UndoStack.Undo();
            var mapper = new ChangeSequenceMapper(changes);
            if (mapper.IsOrdered) orderedRounds++;
            AssertSameAsSequential(changes, length, mapper.IsOrdered);
        }
        Assert.True(orderedRounds > 150, $"only {orderedRounds} undos were mapped by binary search");
    }

    [Fact]
    public void One_caret_per_line_typed_then_undone_is_mapped_by_binary_search()
    {
        // what typing at a caret on every line makes: "item" replaced by "s" on each line, back to front; undo puts them back front to back
        var text = string.Concat(Enumerable.Repeat("item\n", 50));
        var doc = new TextDocument(text);
        doc.BeginUpdate();
        for (var line = 49; line >= 0; line--) doc.Replace(line * 5, 4, "s");
        doc.EndUpdate();
        var changes = new List<DocumentChangeEventArgs>();
        doc.Changed += (_, e) => changes.Add(e);
        var length = doc.TextLength;
        doc.UndoStack.Undo();
        Assert.Equal(50, changes.Count);
        AssertSameAsSequential(changes, length, ordered: true);
        changes.Clear();
        length = doc.TextLength;
        doc.UndoStack.Redo();
        AssertSameAsSequential(changes, length, ordered: true);
    }

    [Fact]
    public void Changes_in_any_other_order_map_change_by_change()
    {
        var rng = new Random(3);
        for (var round = 0; round < 200; round++)
        {
            var text = new string('a', rng.Next(5, 40));
            var edits = new List<(int, int, string)>();
            var length = text.Length;
            for (var i = 0; i < rng.Next(2, 6); i++)
            {
                var offset = rng.Next(0, length + 1);
                var remove = Math.Min(rng.Next(0, 4), length - offset);
                var insert = new string('+', rng.Next(0, 4));
                edits.Add((offset, remove, insert));
                length += insert.Length - remove;
            }
            var (changes, original) = Record(text, edits);
            var mapper = new ChangeSequenceMapper(changes);
            AssertSameAsSequential(changes, original, mapper.IsOrdered);
        }
        // overlapping changes are never taken for an ordered batch
        var (overlap, len) = Record("abcdef", [(1, 2, "XYZ"), (2, 3, "")]);
        AssertSameAsSequential(overlap, len, ordered: false);
    }

    [Fact]
    public void No_changes_leave_offsets_alone()
    {
        var mapper = new ChangeSequenceMapper([]);
        Assert.Equal(5, mapper.Map(5));
    }
}
