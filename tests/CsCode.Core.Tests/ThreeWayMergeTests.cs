using CsCode.Core.Text;
using Xunit;

#pragma warning disable xUnit1051 // pure, synchronous computations: the test token adds nothing

namespace CsCode.Core.Tests;

public sealed class ThreeWayMergeTests
{
    private static string Kinds(ThreeWayMerge m) => string.Join(",", m.Regions.Select(r => r.Kind));

    [Fact]
    public void Lines_keep_their_terminators_and_concatenate_back_to_the_text()
    {
        Assert.Equal(["a\n", "b\r\n", "c\r", "d"], ThreeWayMerge.SplitLinesKeepingEnds("a\nb\r\nc\rd"));
        Assert.Equal(["a\n", "\n"], ThreeWayMerge.SplitLinesKeepingEnds("a\n\n"));
        Assert.Empty(ThreeWayMerge.SplitLinesKeepingEnds(""));
    }

    [Fact]
    public void Changes_on_different_lines_merge_by_themselves()
    {
        var m = ThreeWayMerge.Compute("a\nb\nc\nd\ne\n", "a\nB\nc\nd\ne\n", "a\nb\nc\nD\ne\n");
        Assert.Equal("Unchanged,Current,Unchanged,Incoming,Unchanged", Kinds(m));
        Assert.Empty(m.Conflicts);
        Assert.Equal("a\nB\nc\nD\ne\n", new MergeResolution(m).ResultText());
    }

    [Fact]
    public void The_same_change_on_both_sides_is_not_a_conflict()
    {
        var m = ThreeWayMerge.Compute("a\nb\nc\n", "a\nX\nc\n", "a\nX\nc\n");
        Assert.Equal("Unchanged,Both,Unchanged", Kinds(m));
        Assert.Equal("a\nX\nc\n", new MergeResolution(m).ResultText());
    }

    [Fact]
    public void Different_changes_of_the_same_lines_conflict_and_each_choice_composes_the_result()
    {
        var m = ThreeWayMerge.Compute("a\nb\nc\n", "a\nours\nc\n", "a\ntheirs 1\ntheirs 2\nc\n");
        var conflict = Assert.Single(m.Conflicts);
        Assert.Equal((new LineSpan(1, 1), new LineSpan(1, 1), new LineSpan(1, 2), 0), (conflict.Base, conflict.Current, conflict.Incoming, conflict.ConflictIndex));
        var r = new MergeResolution(m);
        Assert.Equal(1, r.UnhandledCount);
        Assert.Equal("a\nb\nc\n", r.ResultText()); // unresolved: the base, as VS Code shows it
        r.Set(0, MergeSides.Current);
        Assert.Equal("a\nours\nc\n", r.ResultText());
        r.Set(0, MergeSides.Incoming);
        Assert.Equal("a\ntheirs 1\ntheirs 2\nc\n", r.ResultText());
        Assert.Equal(0, r.UnhandledCount);
        // checking current after incoming: both, incoming first
        r.Toggle(0, MergeSides.Current);
        Assert.Equal((MergeSides.Both, true), (r.Taken(0), r.IncomingFirst(0)));
        Assert.Equal("a\ntheirs 1\ntheirs 2\nours\nc\n", r.ResultText());
        Assert.Equal("theirs 1\ntheirs 2\nours\n", r.TextOf(0));
        // unchecking incoming leaves current; unchecking it too leaves nothing taken
        r.Toggle(0, MergeSides.Incoming);
        Assert.Equal("a\nours\nc\n", r.ResultText());
        r.Toggle(0, MergeSides.Current);
        Assert.Equal(MergeSides.None, r.Taken(0));
        r.Toggle(0, MergeSides.Current);
        r.Toggle(0, MergeSides.Incoming);
        Assert.Equal("a\nours\ntheirs 1\ntheirs 2\nc\n", r.ResultText()); // current first
        Assert.Equal((MergeSides.Both, false), r.Classify(0, "ours\ntheirs 1\ntheirs 2\n"));
        Assert.Equal((MergeSides.Both, true), r.Classify(0, "theirs 1\ntheirs 2\nours\n"));
        Assert.Equal((MergeSides.None, false), r.Classify(0, "b\n"));
        Assert.Null(r.Classify(0, "hand made\n"));
        r.MarkManual(0);
        Assert.Equal(0, r.UnhandledCount);
    }

    [Fact]
    public void Changes_on_adjacent_lines_conflict_as_in_git()
    {
        var m = ThreeWayMerge.Compute("a\nb\nc\nd\n", "a\nB\nc\nd\n", "a\nb\nC\nd\n");
        var conflict = Assert.Single(m.Conflicts);
        Assert.Equal(new LineSpan(1, 2), conflict.Base);
        Assert.Equal("a\nB\nc\nd\n", Resolve(m, MergeSides.Current));
        Assert.Equal("a\nb\nC\nd\n", Resolve(m, MergeSides.Incoming));
    }

    [Fact]
    public void Insertions_at_the_same_place_conflict_unless_they_are_the_same()
    {
        var m = ThreeWayMerge.Compute("a\nz\n", "a\nfrom ours\nz\n", "a\nfrom theirs\nz\n");
        var conflict = Assert.Single(m.Conflicts);
        Assert.Equal(0, conflict.Base.Length);
        Assert.Equal("a\nz\n", Resolve(m, MergeSides.None));
        Assert.Equal("a\nfrom ours\nfrom theirs\nz\n", Resolve(m, MergeSides.Both));
        Assert.Empty(ThreeWayMerge.Compute("a\nz\n", "a\nsame\nz\n", "a\nsame\nz\n").Conflicts);
    }

    [Fact]
    public void Line_endings_and_a_missing_final_newline_are_kept_line_by_line()
    {
        var m = ThreeWayMerge.Compute("a\r\nb\r\nc\r\nd", "A\r\nb\r\nc\r\nd", "a\r\nb\r\nc\r\nd\r\n");
        Assert.Empty(m.Conflicts);
        Assert.Equal("A\r\nb\r\nc\r\nd\r\n", new MergeResolution(m).ResultText());
        // a line-ending change is a change: next to another change it conflicts (git's rule for adjacent lines)
        Assert.Single(ThreeWayMerge.Compute("a\r\nb\r\nc", "a\r\nB\r\nc", "a\r\nb\r\nc\r\n").Conflicts);
        // one side deleting everything, the other changing a line: a conflict whose current side is empty
        var d = ThreeWayMerge.Compute("x\ny\n", "", "x\nY\n");
        var c = Assert.Single(d.Conflicts);
        Assert.Equal(0, c.Current.Length);
        Assert.Equal("", Resolve(d, MergeSides.Current));
    }

    [Fact]
    public void Random_edits_merge_into_one_side_when_the_other_kept_the_base_and_regions_cover_every_text()
    {
        var rng = new Random(1234);
        for (var round = 0; round < 200; round++)
        {
            var b = Lines(rng, rng.Next(0, 40));
            var c = Edit(rng, b);
            var i = Edit(rng, b);
            var m = ThreeWayMerge.Compute(string.Concat(b), string.Concat(c), string.Concat(i));
            AssertCovers(m);
            // taking a side for every conflict and the other side's merged regions gives a text containing each side's unconflicted changes
            Assert.Equal(string.Concat(c), new MergeResolution(ThreeWayMerge.Compute(string.Concat(b), string.Concat(c), string.Concat(b))).ResultText());
            Assert.Equal(string.Concat(i), new MergeResolution(ThreeWayMerge.Compute(string.Concat(b), string.Concat(b), string.Concat(i))).ResultText());
            Assert.Equal(string.Concat(c), new MergeResolution(ThreeWayMerge.Compute(string.Concat(b), string.Concat(c), string.Concat(c))).ResultText());
            if (m.Conflicts.Count == 0) continue;
            var all = new MergeResolution(m);
            for (var k = 0; k < m.Conflicts.Count; k++) all.Set(k, MergeSides.Current);
            if (m.Regions.All(r => r.Kind is not MergeRegionKind.Incoming)) Assert.Equal(string.Concat(c), all.ResultText());
        }
    }

    private static void AssertCovers(ThreeWayMerge m)
    {
        int b = 0, c = 0, i = 0;
        foreach (var r in m.Regions)
        {
            Assert.Equal((b, c, i), (r.Base.Start, r.Current.Start, r.Incoming.Start));
            if (r.Kind == MergeRegionKind.Unchanged)
                for (var k = 0; k < r.Base.Length; k++)
                {
                    Assert.Equal(m.BaseLines[r.Base.Start + k], m.CurrentLines[r.Current.Start + k]);
                    Assert.Equal(m.BaseLines[r.Base.Start + k], m.IncomingLines[r.Incoming.Start + k]);
                }
            b = r.Base.End;
            c = r.Current.End;
            i = r.Incoming.End;
        }
        Assert.Equal((m.BaseLines.Count, m.CurrentLines.Count, m.IncomingLines.Count), (b, c, i));
    }

    private static string Resolve(ThreeWayMerge m, MergeSides sides)
    {
        var r = new MergeResolution(m);
        for (var k = 0; k < m.Conflicts.Count; k++) r.Set(k, sides);
        return r.ResultText();
    }

    private static List<string> Lines(Random rng, int n) => [.. Enumerable.Range(0, n).Select(_ => $"line {rng.Next(0, 12)}\n")];

    private static List<string> Edit(Random rng, List<string> lines)
    {
        var copy = new List<string>(lines);
        for (var k = rng.Next(0, 4); k > 0; k--)
        {
            var at = rng.Next(0, copy.Count + 1);
            switch (rng.Next(3))
            {
                case 0: copy.Insert(at, $"new {rng.Next(0, 5)}\n"); break;
                case 1 when at < copy.Count: copy.RemoveAt(at); break;
                default: if (at < copy.Count) copy[at] = $"changed {rng.Next(0, 5)}\n"; break;
            }
        }
        return copy;
    }
}
