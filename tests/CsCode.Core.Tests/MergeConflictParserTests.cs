using CsCode.Core.Text;
using Xunit;

#pragma warning disable xUnit1051 // pure, synchronous computations: the test token adds nothing

namespace CsCode.Core.Tests;

public sealed class MergeConflictParserTests
{
    private const string TwoWay = """
        before
        <<<<<<< HEAD
        ours 1
        ours 2
        =======
        theirs
        >>>>>>> feature
        after

        """;

    [Fact]
    public void A_two_way_block_has_its_lines_offsets_and_names()
    {
        var c = Assert.Single(MergeConflictParser.Parse(TwoWay));
        Assert.Equal((1, 6), (c.StartLine, c.EndLine));
        Assert.Equal(4, c.SplitterLine);
        Assert.Empty(c.CommonAncestors);
        Assert.Equal("HEAD", c.Current.Name);
        Assert.Equal("feature", c.Incoming.Name);
        Assert.Equal((1, 2, 4), (c.Current.HeaderLine, c.Current.ContentStartLine, c.Current.ContentEndLine));
        Assert.Equal((6, 5, 6), (c.Incoming.HeaderLine, c.Incoming.ContentStartLine, c.Incoming.ContentEndLine));
        Assert.Equal("ours 1\nours 2\n", TwoWay[c.Current.ContentStart..c.Current.ContentEnd]);
        Assert.Equal("theirs\n", TwoWay[c.Incoming.ContentStart..c.Incoming.ContentEnd]);
        Assert.Equal(TwoWay.IndexOf("<<<<<<<", StringComparison.Ordinal), c.Start);
        Assert.Equal(TwoWay.IndexOf("after", StringComparison.Ordinal), c.End);
        Assert.Equal(new TextRange(new TextPosition(1, 0), new TextPosition(7, 0)), c.Range);
    }

    [Fact]
    public void Resolving_keeps_one_side_or_both_in_order()
    {
        var c = MergeConflictParser.Parse(TwoWay)[0];
        Assert.Equal("ours 1\nours 2\n", c.Resolve(TwoWay, MergeConflictChoice.Current));
        Assert.Equal("theirs\n", c.Resolve(TwoWay, MergeConflictChoice.Incoming));
        Assert.Equal("ours 1\nours 2\ntheirs\n", c.Resolve(TwoWay, MergeConflictChoice.Both));
        var all = MergeConflictParser.ResolveAll(TwoWay, [c], MergeConflictChoice.Incoming);
        Assert.Equal("before\ntheirs\nafter\n", all);
    }

    [Fact]
    public void Diff3_and_zdiff3_common_ancestor_sections_are_parsed_and_dropped_on_resolve()
    {
        const string text = "<<<<<<< HEAD\nours\n||||||| merged common ancestors\nbase\n||||||| another base\nbase 2\n=======\ntheirs\n>>>>>>> topic\nend";
        var c = Assert.Single(MergeConflictParser.Parse(text));
        Assert.Equal(2, c.CommonAncestors.Count);
        Assert.Equal("merged common ancestors", c.CommonAncestors[0].Name);
        Assert.Equal("base\n", text[c.CommonAncestors[0].ContentStart..c.CommonAncestors[0].ContentEnd]);
        Assert.Equal("base 2\n", text[c.CommonAncestors[1].ContentStart..c.CommonAncestors[1].ContentEnd]);
        Assert.Equal((2, 3), (c.Current.ContentEndLine, c.CommonAncestors[0].ContentStartLine));
        Assert.Equal("ours\n", c.Resolve(text, MergeConflictChoice.Current));
        Assert.Equal("ours\ntheirs\n", c.Resolve(text, MergeConflictChoice.Both));
        Assert.Equal(MergeConflictPart.CurrentHeader, c.PartAt(0));
        Assert.Equal(MergeConflictPart.Current, c.PartAt(1));
        Assert.Equal(MergeConflictPart.CommonAncestors, c.PartAt(2));
        Assert.Equal(MergeConflictPart.CommonAncestors, c.PartAt(5));
        Assert.Equal(MergeConflictPart.Splitter, c.PartAt(6));
        Assert.Equal(MergeConflictPart.Incoming, c.PartAt(7));
        Assert.Equal(MergeConflictPart.IncomingFooter, c.PartAt(8));
        Assert.Equal(MergeConflictPart.None, c.PartAt(9));
    }

    [Fact]
    public void Crlf_and_a_footer_on_the_last_line_without_a_terminator()
    {
        const string text = "x\r\n<<<<<<< a\r\none\r\n=======\r\ntwo\r\n>>>>>>> b";
        var c = Assert.Single(MergeConflictParser.Parse(text));
        Assert.Equal("one\r\n", c.Resolve(text, MergeConflictChoice.Current));
        Assert.Equal("two\r\n", c.Resolve(text, MergeConflictChoice.Incoming));
        Assert.Equal(text.Length, c.End);
        Assert.Equal(new TextPosition(5, 9), c.EndPosition);
        Assert.Equal("x\r\none\r\n", MergeConflictParser.ResolveAll(text, [c], MergeConflictChoice.Current));
    }

    [Fact]
    public void Empty_sides_and_several_blocks()
    {
        const string text = "<<<<<<< HEAD\n=======\nadded\n>>>>>>> b\nmiddle\n<<<<<<< HEAD\nkept\n=======\n>>>>>>> b\n";
        var list = MergeConflictParser.Parse(text);
        Assert.Equal(2, list.Count);
        Assert.Equal(list[0].Current.ContentStartLine, list[0].Current.ContentEndLine);
        Assert.Equal("", list[0].Resolve(text, MergeConflictChoice.Current));
        Assert.Equal("", list[1].Resolve(text, MergeConflictChoice.Incoming));
        Assert.Equal("middle\nkept\n", MergeConflictParser.ResolveAll(text, list, MergeConflictChoice.Current));
        Assert.Equal("added\nmiddle\n", MergeConflictParser.ResolveAll(text, list, MergeConflictChoice.Incoming));
        Assert.Equal("added\nmiddle\nkept\n", MergeConflictParser.ResolveAll(text, list, MergeConflictChoice.Both));
    }

    [Fact]
    public void Broken_blocks_follow_vs_codes_rules()
    {
        // no splitter: not a conflict; the next block is
        Assert.Single(MergeConflictParser.Parse("<<<<<<< a\nx\n>>>>>>> b\n<<<<<<< a\ny\n=======\nz\n>>>>>>> b\n"));
        // the splitter must be exactly seven '=': "======= x" is content
        Assert.Empty(MergeConflictParser.Parse("<<<<<<< a\nx\n======= x\ny\n>>>>>>> b\n"));
        // a start marker inside an open block stops the scan; what came before is kept
        var text = "<<<<<<< a\n1\n=======\n2\n>>>>>>> b\n<<<<<<< a\n<<<<<<< a\n3\n=======\n4\n>>>>>>> b\n";
        Assert.Single(MergeConflictParser.Parse(text));
        // an unterminated block is not reported
        Assert.Empty(MergeConflictParser.Parse("<<<<<<< a\n1\n=======\n2\n"));
        Assert.Empty(MergeConflictParser.Parse("no markers at all"));
        Assert.Empty(MergeConflictParser.Parse(""));
    }

    [Fact]
    public void The_staging_check_matches_vs_codes_marker_lines()
    {
        Assert.True(MergeConflictParser.HasConflictMarkers("a\n<<<<<<< HEAD\nb"));
        Assert.True(MergeConflictParser.HasConflictMarkers("a\n=======\nb"));
        Assert.True(MergeConflictParser.HasConflictMarkers("a\r\n>>>>>>> topic\r\n"));
        Assert.False(MergeConflictParser.HasConflictMarkers("a\n<<<<<<<\nb")); // no whitespace after the marker
        Assert.False(MergeConflictParser.HasConflictMarkers("x = \"=======\";\n"));
        Assert.False(MergeConflictParser.HasConflictMarkers("resolved\ntext\n"));
    }

    [Fact]
    public void A_large_text_without_markers_is_rejected_by_the_fast_path_and_a_large_one_with_markers_parses_quickly()
    {
        var plain = string.Concat(Enumerable.Repeat("some ordinary line of code;\n", 200_000));
        Assert.False(MergeConflictParser.MayContainConflicts(plain));
        Assert.Empty(MergeConflictParser.Parse(plain));
        var block = "<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> b\n";
        var big = string.Concat(Enumerable.Repeat("line\n" + block, 20_000));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var list = MergeConflictParser.Parse(big);
        Assert.Equal(20_000, list.Count);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"parsing took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(string.Concat(Enumerable.Repeat("line\nours\n", 20_000)), MergeConflictParser.ResolveAll(big, list, MergeConflictChoice.Current));
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        var big = string.Concat(Enumerable.Repeat("<<<<<<< a\nx\n=======\ny\n>>>>>>> b\n", 5000));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => MergeConflictParser.Parse(big, cts.Token));
    }
}
