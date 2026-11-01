using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.LogicalTree;
using CsCode.Core.Theming;
using CsCode.Editor.Markdown;
using CsCode.Editor.Markdown.Preview;
using Markdig.Syntax;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>One test per construct of the preview's renderer (docs/MARKDOWN.md): what each Markdown block becomes as Avalonia controls.</summary>
public class MarkdownBlockRendererTests
{
    private static readonly MarkdownPreviewStyle Style = new(
        new FontFamily("Inter"), new FontFamily("fonts:CsCode#Fira Code"), 14, 1.6,
        Brush(0xFF1E1E1E), Brush(0xFFD4D4D4), Brush(0xFF3794FF), Brush(0x660A0A0A), Brush(0xFFD7BA7D), Brush(0x1A7F7F7F), Brush(0x80007ACC),
        Brush(0x2EFFFFFF), Brush(0xB3D4D4D4), Brush(0x1AD4D4D4), Brush(0x55EA5C00), ThemeKind.Dark);

    private static IBrush Brush(uint argb) => new ImmutableSolidColorBrush(Color.FromUInt32(argb));

    private static (Control Control, PreviewDocument Doc) RenderFirst(string markdown, Action<string>? onLink = null, Func<string, string, Control>? image = null,
        IReadOnlyDictionary<LeafBlock, IReadOnlyList<CodeSpan>>? spans = null, CodeColorizer? colorize = null)
    {
        var doc = MarkdownPreviewParser.Parse(markdown, 1, colorize: colorize);
        var ctx = new MarkdownRenderContext(Style, spans ?? doc.CodeSpans) { OnLink = onLink, CreateImage = image };
        return (MarkdownBlockRenderer.Render(doc.Blocks[0].Block, ctx), doc);
    }

    private static List<Run> Runs(InlineCollection inlines)
    {
        var list = new List<Run>();
        foreach (var i in inlines)
        {
            if (i is Run r) list.Add(r);
            else if (i is Span s) list.AddRange(Runs(s.Inlines));
        }
        return list;
    }

    private static string Text(SelectableTextBlock tb) => string.Concat(Runs(tb.Inlines!).Select(r => r.Text));

    private static Window Show(Control content, double width = 600)
    {
        var w = new Window { Width = width, Height = 400, Content = content };
        w.Show();
        ViewHostPump();
        return w;
    }

    private static void ViewHostPump() => Support.ViewHost.Pump();

    [AvaloniaFact]
    public void A_paragraph_renders_bold_italic_strikethrough_and_inline_code_runs_in_a_selectable_text_block()
    {
        var (control, _) = RenderFirst("plain **bold** *it* ***both*** ~~gone~~ `code()`");
        var tb = Assert.IsType<SelectableTextBlock>(control);
        var runs = Runs(tb.Inlines!);
        Assert.Equal("plain bold it both gone code()", Text(tb));
        Assert.Equal(FontWeight.Bold, runs.Single(r => r.Text == "bold").FontWeight);
        Assert.Equal(FontStyle.Italic, runs.Single(r => r.Text == "it").FontStyle);
        var both = runs.Single(r => r.Text == "both");
        Assert.Equal((FontWeight.Bold, FontStyle.Italic), (both.FontWeight, both.FontStyle));
        Assert.Equal(TextDecorations.Strikethrough, runs.Single(r => r.Text == "gone").TextDecorations);
        var code = runs.Single(r => r.Text == "code()");
        Assert.Same(Style.Mono, code.FontFamily);
        Assert.Same(Style.CodeBackground, code.Background);
        Assert.Same(Style.InlineCode, code.Foreground);
        Assert.Equal(TextWrapping.Wrap, tb.TextWrapping);
    }

    [AvaloniaFact]
    public void Headings_scale_with_their_level_and_the_first_two_have_a_rule_under_them()
    {
        var (h1, _) = RenderFirst("# One");
        var border = Assert.IsType<Border>(h1);
        Assert.Equal(new Thickness(0, 0, 0, 1), border.BorderThickness);
        var t1 = Assert.IsType<SelectableTextBlock>(border.Child);
        Assert.Equal(28, t1.FontSize);
        Assert.Equal(FontWeight.SemiBold, t1.FontWeight);
        var (h3, _) = RenderFirst("### Three");
        Assert.Equal(17.5, Assert.IsType<SelectableTextBlock>(h3).FontSize);
        var (h6, _) = RenderFirst("###### Six");
        Assert.Same(Style.Muted, Assert.IsType<SelectableTextBlock>(h6).Foreground);
    }

    [AvaloniaFact]
    public void Links_are_coloured_recorded_by_text_position_and_open_on_click()
    {
        var opened = new List<string>();
        var (control, _) = RenderFirst("Go [to docs](docs/a.md#part) or <https://x.org> or www.example.com now", opened.Add);
        var tb = Assert.IsType<SelectableTextBlock>(control);
        var links = MarkdownBlockRenderer.GetLinks(tb)!;
        Assert.Equal(["docs/a.md#part", "https://x.org", "http://www.example.com"], links.Select(l => l.Url));
        var text = Text(tb);
        Assert.Equal("to docs", text[links[0].Start..links[0].End]);
        Assert.Equal("https://x.org", text[links[1].Start..links[1].End]);
        Assert.Same(Style.Link, tb.Inlines!.OfType<Span>().First().Foreground);

        var window = Show(tb);
        // click in the middle of "to docs"
        var hit = tb.TextLayout.HitTestTextPosition(links[0].Start + 2);
        var point = tb.TranslatePoint(hit.Center, window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Equal(["docs/a.md#part"], opened);
        window.Close();
    }

    [AvaloniaFact]
    public void A_fenced_code_block_renders_the_colours_of_its_spans_in_the_code_font_and_scrolls_sideways()
    {
        IReadOnlyList<CodeSpan> Colorize(string? info, string code, CancellationToken _) =>
            info == "cs" ? [new CodeSpan(0, 3, Colors.Blue, true, false), new CodeSpan(8, 1, Colors.Green, false, true)] : [];
        var (control, _) = RenderFirst("```cs\nvar x = 1;\n```", colorize: Colorize);
        var border = Assert.IsType<Border>(control);
        Assert.Same(Style.CodeBackground, border.Background);
        var scroller = Assert.IsType<ScrollViewer>(border.Child);
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, scroller.HorizontalScrollBarVisibility);
        var tb = Assert.IsType<SelectableTextBlock>(scroller.Content);
        Assert.Same(Style.Mono, tb.FontFamily);
        Assert.Equal(TextWrapping.NoWrap, tb.TextWrapping);
        var runs = Runs(tb.Inlines!);
        Assert.Equal("var x = 1;", string.Concat(runs.Select(r => r.Text)));
        var keyword = runs.Single(r => r.Text == "var");
        Assert.Equal(Colors.Blue, ((ISolidColorBrush)keyword.Foreground!).Color);
        Assert.Equal(FontWeight.Bold, keyword.FontWeight);
        Assert.Equal(FontStyle.Italic, runs.Single(r => r.Text == "1").FontStyle);
    }

    [AvaloniaFact]
    public void A_code_block_without_spans_is_plain_text()
    {
        var (control, _) = RenderFirst("```unknownlang\na < b\n```");
        var tb = (SelectableTextBlock)((ScrollViewer)((Border)control).Child!).Content!;
        Assert.Equal("a < b", tb.Text);
    }

    [AvaloniaFact]
    public void Bullet_and_ordered_lists_show_markers_and_nest()
    {
        var (bullets, _) = RenderFirst("- one\n- two\n  - inner\n");
        var grid = Assert.IsType<Grid>(bullets);
        var markers = grid.Children.OfType<TextBlock>().Where(t => Grid.GetColumn(t) == 0).Select(t => t.Text).ToList();
        Assert.Equal(["•", "•"], markers);
        var nested = grid.GetLogicalDescendants().OfType<Grid>().Single();
        Assert.Equal("◦", nested.Children.OfType<TextBlock>().First(t => Grid.GetColumn(t) == 0).Text);

        var (ordered, _) = RenderFirst("3. c\n4. d\n");
        var numbers = ((Grid)ordered).Children.OfType<TextBlock>().Where(t => Grid.GetColumn(t) == 0).Select(t => t.Text);
        Assert.Equal(["3.", "4."], numbers);
    }

    [AvaloniaFact]
    public void Task_list_items_show_a_check_box_with_their_state_instead_of_a_bullet()
    {
        var (control, _) = RenderFirst("- [ ] open\n- [x] done\n");
        var grid = Assert.IsType<Grid>(control);
        var boxes = grid.Children.OfType<CheckBox>().ToList();
        Assert.Equal([false, true], boxes.Select(b => b.IsChecked == true));
        Assert.All(boxes, b => Assert.False(b.IsHitTestVisible));
        var texts = grid.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(Text).ToList();
        Assert.Equal([" open", " done"], texts);
    }

    [AvaloniaFact]
    public void A_table_is_a_grid_with_a_bold_header_row_and_column_alignment()
    {
        var (control, _) = RenderFirst("| Name | Size |\n|:-----|-----:|\n| a | 10 |\n| bb | 200 |\n");
        var scroller = Assert.IsType<ScrollViewer>(control);
        var grid = Assert.IsType<Grid>(scroller.Content);
        Assert.Equal(2, grid.ColumnDefinitions.Count);
        Assert.Equal(3, grid.RowDefinitions.Count);
        var cells = grid.Children.OfType<Border>().ToList();
        Assert.Equal(6, cells.Count);
        TextBlock Cell(int row, int col) => (TextBlock)((StackPanel)cells.Single(c => Grid.GetRow(c) == row && Grid.GetColumn(c) == col).Child!).Children[0];
        Assert.Equal(FontWeight.SemiBold, Cell(0, 0).FontWeight);
        Assert.Equal(FontWeight.Normal, Cell(1, 0).FontWeight);
        Assert.Equal(TextAlignment.Right, Cell(2, 1).TextAlignment);
        Assert.Equal(TextAlignment.Left, Cell(2, 0).TextAlignment);
        Assert.Equal("200", Text((SelectableTextBlock)Cell(2, 1)));
        Assert.NotNull(cells.First(c => Grid.GetRow(c) == 0).Background);
    }

    [AvaloniaFact]
    public void A_block_quote_has_the_theme_border_and_background_and_holds_its_blocks()
    {
        var (control, _) = RenderFirst("> quoted *text*\n>\n> - item\n");
        var border = Assert.IsType<Border>(control);
        Assert.Same(Style.QuoteBorder, border.BorderBrush);
        Assert.Same(Style.QuoteBackground, border.Background);
        Assert.Equal(5, border.BorderThickness.Left);
        var stack = Assert.IsType<StackPanel>(border.Child);
        Assert.Equal(2, stack.Children.Count);
        Assert.IsType<Grid>(stack.Children[1]);
    }

    [AvaloniaFact]
    public void A_thematic_break_is_a_rule_in_the_separator_colour()
    {
        var (control, _) = RenderFirst("---\n");
        var border = Assert.IsType<Border>(control);
        Assert.Same(Style.Separator, border.Background);
        Assert.Equal(2, border.Height);
    }

    [AvaloniaFact]
    public void Images_are_built_by_the_image_hook_with_their_address_and_alt_text_inside_the_text()
    {
        var asked = new List<(string Url, string Alt)>();
        var (control, _) = RenderFirst("before ![the *logo*](img/logo.png \"t\") after", image: (url, alt) =>
        {
            asked.Add((url, alt));
            return new Border { Width = 10, Height = 10 };
        });
        var tb = Assert.IsType<SelectableTextBlock>(control);
        Assert.Equal([("img/logo.png", "the logo")], asked);
        Assert.Single(tb.Inlines!.OfType<InlineUIContainer>());
        // without the hook, the alt text stands in
        var (plain, _) = RenderFirst("![alt text](x.png)");
        Assert.Equal("alt text", Text((SelectableTextBlock)plain));
    }

    [AvaloniaFact]
    public void Raw_HTML_blocks_show_their_markup_and_inline_br_breaks_the_line()
    {
        var (block, _) = RenderFirst("<div align=\"center\">\n  hi\n</div>\n");
        var html = Assert.IsType<SelectableTextBlock>(block);
        Assert.Contains("<div align=\"center\">", html.Text);
        Assert.Same(Style.Muted, html.Foreground);

        var (para, _) = RenderFirst("one<br>two <span>three</span>");
        var tb = Assert.IsType<SelectableTextBlock>(para);
        Assert.Single(tb.Inlines!.OfType<LineBreak>());
        Assert.Equal("onetwo three", Text(tb));
    }

    [AvaloniaFact]
    public void Hard_line_breaks_break_and_soft_ones_are_spaces()
    {
        var (control, _) = RenderFirst("a  \nb\nc");
        var tb = Assert.IsType<SelectableTextBlock>(control);
        Assert.Single(tb.Inlines!.OfType<LineBreak>());
        Assert.Equal("ab c", Text(tb));
    }

    [AvaloniaFact]
    public void Entities_are_decoded()
    {
        var (control, _) = RenderFirst("&copy; &amp; &#x41;");
        Assert.Equal("© & A", Text((SelectableTextBlock)control));
    }

    [AvaloniaFact]
    public void Rendered_text_lays_out_and_can_be_selected_and_copied()
    {
        var (control, _) = RenderFirst("Select **me** please");
        var tb = (SelectableTextBlock)control;
        var window = Show(tb);
        tb.SelectAll();
        Assert.Equal("Select me please", tb.SelectedText);
        Assert.True(tb.Bounds.Height > 0);
        window.Close();
    }
}
