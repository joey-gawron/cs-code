using CsCode.Editor.Markdown;
using CsCode.Editor.Markdown.Preview;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>The preview's parse (worker side, docs/MARKDOWN.md): CommonMark + GFM through Markdig, source lines, anchors, reuse keys, code colours.</summary>
public class MarkdownPreviewParserTests
{
    private const string Sample = """
        # Title

        Some *text* here
        over two lines.

        - [ ] open
        - [x] done

        | a | b |
        |:--|--:|
        | 1 | 2 |

        ```csharp
        var x = 1;
        ```

        [ref]: https://example.com
        """;

    [Fact]
    public void Top_level_blocks_carry_the_source_lines_they_span()
    {
        var doc = MarkdownPreviewParser.Parse(Sample, 3);
        Assert.Equal(3, doc.Version);
        Assert.Collection(doc.Blocks,
            b => { Assert.IsType<HeadingBlock>(b.Block); Assert.Equal((0, 0), (b.StartLine, b.EndLine)); },
            b => { Assert.IsType<ParagraphBlock>(b.Block); Assert.Equal((2, 3), (b.StartLine, b.EndLine)); },
            b => { Assert.IsType<ListBlock>(b.Block); Assert.Equal((5, 6), (b.StartLine, b.EndLine)); },
            b => { Assert.IsType<Table>(b.Block); Assert.Equal((8, 10), (b.StartLine, b.EndLine)); },
            b => { Assert.IsType<FencedCodeBlock>(b.Block); Assert.Equal((12, 14), (b.StartLine, b.EndLine)); });
        // the link reference definition renders nothing and is not a block
        Assert.Equal(17, doc.LineCount);
    }

    [Fact]
    public void BlockIndexAtLine_finds_the_block_showing_a_line_including_blank_lines_between_blocks()
    {
        var doc = MarkdownPreviewParser.Parse(Sample, 1);
        Assert.Equal(0, doc.BlockIndexAtLine(0));
        Assert.Equal(0, doc.BlockIndexAtLine(1)); // the blank line after the heading
        Assert.Equal(1, doc.BlockIndexAtLine(3.5));
        Assert.Equal(4, doc.BlockIndexAtLine(100));
        Assert.Equal(-1, PreviewDocument.Empty.BlockIndexAtLine(3));
    }

    [Fact]
    public void Headings_get_GitHub_anchor_ids()
    {
        var doc = MarkdownPreviewParser.Parse("# Getting Started!\n\ntext\n\n## API & Usage\n\n> ### Inside a quote\n", 1);
        Assert.Equal(0, doc.Anchors["getting-started"]);
        Assert.Equal(2, doc.Anchors["api--usage"]);
        Assert.Equal(3, doc.Anchors["inside-a-quote"]);
    }

    [Fact]
    public void GFM_constructs_parse_tables_task_lists_strikethrough_and_autolinks()
    {
        var doc = MarkdownPreviewParser.Parse("~~gone~~ www.example.com\n\n- [x] a\n\n|h|\n|-|\n|c|\n", 1);
        var para = (ParagraphBlock)doc.Blocks[0].Block;
        var strike = Assert.IsType<EmphasisInline>(para.Inline!.FirstChild);
        Assert.Equal('~', strike.DelimiterChar);
        var link = para.Inline.Descendants<LinkInline>().Single();
        Assert.Equal("http://www.example.com", link.GetDynamicUrl?.Invoke() ?? link.Url);
        Assert.Contains(doc.Blocks[1].Block.Descendants<Markdig.Extensions.TaskLists.TaskList>(), t => t.Checked);
        Assert.IsType<Table>(doc.Blocks[2].Block);
    }

    [Fact]
    public void Linkify_and_breaks_follow_the_options()
    {
        var off = MarkdownPreviewParser.Parse("see www.example.com\nnext", 1, new MarkdownPreviewOptions(Breaks: false, Linkify: false));
        var para = (ParagraphBlock)off.Blocks[0].Block;
        Assert.Empty(para.Inline!.Descendants<LinkInline>());
        Assert.DoesNotContain(para.Inline!.Descendants<LineBreakInline>(), b => b.IsHard);

        var on = MarkdownPreviewParser.Parse("see www.example.com\nnext", 1, new MarkdownPreviewOptions(Breaks: true, Linkify: true));
        var para2 = (ParagraphBlock)on.Blocks[0].Block;
        Assert.Single(para2.Inline!.Descendants<LinkInline>());
        Assert.Contains(para2.Inline!.Descendants<LineBreakInline>(), b => b.IsHard);
    }

    [Fact]
    public void An_edit_changes_the_key_of_the_edited_block_only()
    {
        var before = MarkdownPreviewParser.Parse("# A\n\none\n\ntwo\n\nthree\n", 1);
        var after = MarkdownPreviewParser.Parse("# A\n\none\n\ntwo!\n\nthree\n", 2);
        Assert.Equal(before.Blocks.Count, after.Blocks.Count);
        Assert.Equal(before.Blocks[0].Key, after.Blocks[0].Key);
        Assert.Equal(before.Blocks[1].Key, after.Blocks[1].Key);
        Assert.NotEqual(before.Blocks[2].Key, after.Blocks[2].Key);
        Assert.Equal(before.Blocks[3].Key, after.Blocks[3].Key);
        // a moved block keeps its key (lines differ, text does not)
        var shifted = MarkdownPreviewParser.Parse("intro\n\n# A\n\none\n", 3);
        Assert.Equal(before.Blocks[0].Key, shifted.Blocks[1].Key);
        Assert.Equal(2, shifted.Blocks[1].StartLine);
    }

    [Fact]
    public void Changing_adding_or_removing_a_link_definition_changes_the_key_of_blocks_that_may_use_it_only()
    {
        var a = MarkdownPreviewParser.Parse("see [foo]\n\nplain\n\n[foo]: https://a.example/\n", 1);
        var b = MarkdownPreviewParser.Parse("see [foo]\n\nplain\n\n[foo]: https://b.example/\n", 2);
        var none = MarkdownPreviewParser.Parse("see [foo]\n\nplain\n", 3);
        Assert.NotEqual(a.Blocks[0].Key, b.Blocks[0].Key);
        Assert.NotEqual(a.Blocks[0].Key, none.Blocks[0].Key);
        Assert.NotEqual(b.Blocks[0].Key, none.Blocks[0].Key);
        Assert.Equal(a.Blocks[1].Key, b.Blocks[1].Key); // no bracket: cannot use a definition
        Assert.Equal(a.Blocks[1].Key, none.Blocks[1].Key);
        // the same definitions give the same keys
        Assert.Equal(a.Blocks[0].Key, MarkdownPreviewParser.Parse("see [foo]\n\nplain\n\n[foo]: https://a.example/\n", 4).Blocks[0].Key);
    }

    [Fact]
    public void Code_blocks_are_coloured_with_their_fence_language_and_unchanged_blocks_reuse_the_previous_spans()
    {
        var calls = new List<(string? Info, string Code)>();
        IReadOnlyList<CodeSpan>? Colorize(string? info, string code, CancellationToken _)
        {
            calls.Add((info, code));
            return [new CodeSpan(0, 3, Avalonia.Media.Colors.Red, false, false)];
        }
        const string text = "```cs {.class}\nvar x = 1;\nvar y = 2;\n```\n\n    indented\n\npara\n";
        var first = MarkdownPreviewParser.Parse(text, 1, colorize: Colorize);
        Assert.Equal([("cs", "var x = 1;\nvar y = 2;"), (null, "indented")], calls);
        var fenced = (LeafBlock)first.Blocks[0].Block;
        Assert.Single(first.CodeSpans[fenced]);

        calls.Clear();
        var second = MarkdownPreviewParser.Parse(text.Replace("para", "para!"), 2, colorize: Colorize, previous: first);
        Assert.Empty(calls); // neither code block changed: their spans are carried over
        Assert.Same(first.CodeSpans[fenced], second.CodeSpans[(LeafBlock)second.Blocks[0].Block]);

        var third = MarkdownPreviewParser.Parse(text.Replace("y = 2", "y = 3"), 3, colorize: Colorize, previous: second);
        Assert.Equal([("cs", "var x = 1;\nvar y = 3;")], calls);
        Assert.NotNull(third);
    }

    [Fact]
    public void A_source_longer_than_the_limit_is_cut_and_marked_truncated()
    {
        var line = new string('x', 99) + "\n\n";
        var text = string.Concat(Enumerable.Repeat(line, MarkdownPreviewParser.MaxLength / line.Length + 10));
        var doc = MarkdownPreviewParser.Parse(text, 1);
        Assert.True(doc.Truncated);
        Assert.True(doc.Blocks[^1].EndLine < text.Count(c => c == '\n'));
        Assert.False(MarkdownPreviewParser.Parse("short", 1).Truncated);
    }

    [Fact]
    public void Odd_input_never_throws()
    {
        foreach (var text in new[] { "", "\n\n\n", "```", "|", "- ", "> > > >", "[x]: <", "# ", "\r\n\r\n# a\r\n\r\nb\r", "<div>", "![]()" })
        {
            var doc = MarkdownPreviewParser.Parse(text, 1);
            foreach (var b in doc.Blocks) Assert.True(b.EndLine >= b.StartLine);
        }
    }

    [Fact]
    public void CRLF_sources_count_lines_like_LF_ones()
    {
        var lf = MarkdownPreviewParser.Parse("# a\n\nb\nc\n\n- d\n", 1);
        var crlf = MarkdownPreviewParser.Parse("# a\r\n\r\nb\r\nc\r\n\r\n- d\r\n", 1);
        Assert.Equal(lf.Blocks.Select(b => (b.StartLine, b.EndLine)), crlf.Blocks.Select(b => (b.StartLine, b.EndLine)));
    }
}
