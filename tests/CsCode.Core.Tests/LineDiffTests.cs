using CsCode.Core.Text;
using Xunit;

#pragma warning disable xUnit1051 // pure, synchronous computations: the test token adds nothing (other test projects suppress this rule project-wide)

namespace CsCode.Core.Tests;

public sealed class LineDiffTests
{
    private static string[] L(params string[] lines) => lines;

    /// <summary>Applies the changes to <paramref name="a"/> using <paramref name="b"/>'s lines: must give <paramref name="b"/> back.</summary>
    private static List<string> Apply(IReadOnlyList<string> a, IReadOnlyList<string> b, IReadOnlyList<DiffChange> changes)
    {
        var result = new List<string>();
        var at = 0;
        foreach (var c in changes)
        {
            Assert.True(c.OriginalStart >= at, "changes are ordered and do not overlap");
            for (; at < c.OriginalStart; at++) result.Add(a[at]);
            for (var j = c.ModifiedStart; j < c.ModifiedEnd; j++) result.Add(b[j]);
            at = c.OriginalEnd;
        }
        for (; at < a.Count; at++) result.Add(a[at]);
        return result;
    }

    /// <summary>The equal lines between changes really are equal (the changes are a valid alignment).</summary>
    private static void AssertAlignment(IReadOnlyList<string> a, IReadOnlyList<string> b, IReadOnlyList<DiffChange> changes)
    {
        int i = 0, j = 0;
        foreach (var c in changes)
        {
            Assert.Equal(c.OriginalStart - i, c.ModifiedStart - j);
            for (; i < c.OriginalStart; i++, j++) Assert.Equal(a[i], b[j]);
            i = c.OriginalEnd;
            j = c.ModifiedEnd;
        }
        Assert.Equal(a.Count - i, b.Count - j);
        for (; i < a.Count; i++, j++) Assert.Equal(a[i], b[j]);
    }

    [Fact]
    public void Identical_texts_have_no_changes()
    {
        var r = LineDiff.Compute(L("a", "b", "c"), L("a", "b", "c"));
        Assert.Empty(r.Changes);
        Assert.False(r.HitTimeout);
    }

    [Fact]
    public void A_modified_line_is_one_change_on_both_sides()
    {
        var r = LineDiff.Compute(L("a", "b", "c"), L("a", "B", "c"));
        Assert.Equal([new DiffChange(1, 1, 1, 1)], r.Changes);
    }

    [Fact]
    public void Insertions_and_deletions_are_reported_with_zero_length_on_the_other_side()
    {
        Assert.Equal([new DiffChange(1, 0, 1, 2)], LineDiff.Compute(L("a", "d"), L("a", "b", "c", "d")).Changes);
        Assert.Equal([new DiffChange(1, 2, 1, 0)], LineDiff.Compute(L("a", "b", "c", "d"), L("a", "d")).Changes);
        Assert.Equal([new DiffChange(0, 0, 0, 1)], LineDiff.Compute(L("x"), L("new", "x")).Changes);
        Assert.Equal([new DiffChange(1, 0, 1, 1)], LineDiff.Compute(L("x"), L("x", "new")).Changes);
    }

    [Fact]
    public void Empty_sides_are_one_whole_change()
    {
        Assert.Equal([new DiffChange(0, 0, 0, 2)], LineDiff.Compute(Array.Empty<string>(), L("a", "b")).Changes);
        Assert.Equal([new DiffChange(0, 2, 0, 0)], LineDiff.Compute(L("a", "b"), Array.Empty<string>()).Changes);
    }

    [Fact]
    public void Finds_a_minimal_script_on_the_classic_example()
    {
        // Myers' paper: ABCABBA -> CBABAC has an edit distance of 5
        var a = "ABCABBA".Select(c => c.ToString()).ToArray();
        var b = "CBABAC".Select(c => c.ToString()).ToArray();
        var r = LineDiff.Compute(a, b);
        Assert.Equal(5, r.Changes.Sum(c => c.OriginalLength + c.ModifiedLength));
        AssertAlignment(a, b, r.Changes);
    }

    [Fact]
    public void A_function_added_between_two_others_is_reported_whole_not_as_a_shifted_brace()
    {
        var a = L("void A()", "{", "}", "", "void C()", "{", "}");
        var b = L("void A()", "{", "}", "", "void B()", "{", "}", "", "void C()", "{", "}");
        var r = LineDiff.Compute(a, b);
        var c = Assert.Single(r.Changes);
        Assert.Equal(0, c.OriginalLength);
        Assert.Equal(["void B()", "{", "}", ""], b.Skip(c.ModifiedStart).Take(c.ModifiedLength));
    }

    [Fact]
    public void Ignore_trim_whitespace_treats_indentation_changes_as_equal()
    {
        Assert.Single(LineDiff.Compute(L("  a", "b"), L("a", "b")).Changes);
        Assert.Empty(LineDiff.Compute(L("  a", "b  "), L("a", "b"), new LineDiffOptions(IgnoreTrimWhitespace: true)).Changes);
    }

    [Fact]
    public void Random_edits_round_trip()
    {
        var rng = new Random(1234);
        for (var round = 0; round < 300; round++)
        {
            var a = Enumerable.Range(0, rng.Next(0, 40)).Select(_ => ((char)('a' + rng.Next(0, 5))).ToString()).ToList();
            var b = new List<string>(a);
            for (var e = rng.Next(0, 8); e > 0; e--)
            {
                var op = rng.Next(3);
                if (op == 0 || b.Count == 0) b.Insert(rng.Next(0, b.Count + 1), ((char)('a' + rng.Next(0, 6))).ToString());
                else if (op == 1) b.RemoveAt(rng.Next(0, b.Count));
                else b[rng.Next(0, b.Count)] = ((char)('a' + rng.Next(0, 6))).ToString();
            }
            var r = LineDiff.Compute(a, b, new LineDiffOptions());
            Assert.Equal(b, Apply(a, b, r.Changes));
            AssertAlignment(a, b, r.Changes);
            for (var i = 1; i < r.Changes.Count; i++)
                Assert.True(r.Changes[i].OriginalStart > r.Changes[i - 1].OriginalEnd || r.Changes[i].ModifiedStart > r.Changes[i - 1].ModifiedEnd, "adjacent changes are merged");
        }
    }

    [Fact]
    public void Large_inputs_stay_fast_and_a_tiny_budget_still_gives_a_correct_coarse_diff()
    {
        var a = Enumerable.Range(0, 50_000).Select(i => "line " + i).ToArray();
        var b = a.Select((l, i) => i % 1000 == 0 ? l + " changed" : l).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = LineDiff.Compute(a, b);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(50, r.Changes.Count);

        var x = Enumerable.Range(0, 4000).Select(i => "x" + (i * 7919 % 4000)).ToArray();
        var y = Enumerable.Range(0, 4000).Select(i => "x" + (i * 104729 % 4000)).ToArray();
        var coarse = LineDiff.Compute(x, y, new LineDiffOptions(MaxComputationTime: TimeSpan.FromTicks(1)));
        Assert.True(coarse.HitTimeout);
        Assert.Equal(y, Apply(x, y, coarse.Changes));
    }

    [Fact]
    public void Cancellation_stops_the_search()
    {
        var x = Enumerable.Range(0, 20000).Select(i => "x" + (i * 7919 % 20000)).ToArray();
        var y = Enumerable.Range(0, 20000).Select(i => "x" + (i * 104729 % 20000)).ToArray();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => LineDiff.Compute(x, y, new LineDiffOptions(), cts.Token));
    }

    [Fact]
    public void Split_lines_handles_every_line_ending()
    {
        Assert.Equal([""], LineDiff.SplitLines(""));
        Assert.Equal(["a", "b", "c", ""], LineDiff.SplitLines("a\r\nb\nc\r"));
        Assert.Equal(["a", "", "b"], LineDiff.SplitLines("a\n\nb"));
    }

    [Fact]
    public void Character_changes_mark_the_changed_word_and_merge_tiny_gaps()
    {
        var c = Assert.Single(LineDiff.ComputeChars("var count = 1;", "var total = 1;"));
        Assert.Equal("count", "var count = 1;".Substring(c.OriginalStart, c.OriginalLength));
        Assert.Equal("total", "var total = 1;".Substring(c.ModifiedStart, c.ModifiedLength));
        Assert.Empty(LineDiff.ComputeChars("same", "same"));
        Assert.Equal([new DiffChange(3, 0, 3, 3)], LineDiff.ComputeChars("abc", "abcdef"));
    }
}
