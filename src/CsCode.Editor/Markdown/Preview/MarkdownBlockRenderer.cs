using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace CsCode.Editor.Markdown.Preview;

/// <summary>What one render needs besides the block: the style, the code colours of the parse, and the hooks for links and images.</summary>
public sealed class MarkdownRenderContext(MarkdownPreviewStyle style, IReadOnlyDictionary<LeafBlock, IReadOnlyList<CodeSpan>> codeSpans)
{
    public MarkdownPreviewStyle Style { get; } = style;
    public IReadOnlyDictionary<LeafBlock, IReadOnlyList<CodeSpan>> CodeSpans { get; } = codeSpans;
    /// <summary>A link was clicked (its destination as written: "https://...", "docs/x.md#part", "#anchor").</summary>
    public Action<string>? OnLink { get; init; }
    /// <summary>Builds the control for an image (destination as written, alt text); null shows the alt text.</summary>
    public Func<string, string, Control>? CreateImage { get; init; }
}

/// <summary>
/// Renders one Markdig block to Avalonia controls (UI thread): paragraphs and headings as <see cref="SelectableTextBlock"/>s with inline runs
/// (bold, italic, strikethrough, code, links, images, hard breaks), fenced/indented code with the editor's TextMate colours, bullet, ordered and
/// task lists, block quotes, GFM tables, rules and raw HTML (shown as text: there is no HTML engine). Text is selectable and copyable. Nothing
/// here parses or tokenises: the worker did that.
/// </summary>
public static class MarkdownBlockRenderer
{
    /// <summary>Attached to every rendered text block: its links as (start, end, destination) text positions, for hit testing and tests.</summary>
    public static readonly AttachedProperty<IReadOnlyList<(int Start, int End, string Url)>?> LinksProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, IReadOnlyList<(int, int, string)>?>("Links", typeof(MarkdownBlockRenderer));

    public static IReadOnlyList<(int Start, int End, string Url)>? GetLinks(SelectableTextBlock block) => block.GetValue(LinksProperty);

    public static Control Render(Block block, MarkdownRenderContext ctx) => Render(block, ctx, listDepth: 0);

    private static Control Render(Block block, MarkdownRenderContext ctx, int listDepth)
    {
        var s = ctx.Style;
        switch (block)
        {
            case HeadingBlock h:
            {
                var scale = h.Level switch { 1 => 2.0, 2 => 1.5, 3 => 1.25, 4 => 1.0, 5 => 0.875, _ => 0.85 };
                var text = Text(h.Inline, ctx, s.FontSize * scale, FontWeight.SemiBold, 1.25);
                if (h.Level == 6) text.Foreground = s.Muted;
                if (h.Level > 2) return text;
                return new Border { BorderBrush = s.Separator, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, s.FontSize * 0.3), Child = text };
            }
            case ParagraphBlock p:
                return Text(p.Inline, ctx, s.FontSize, FontWeight.Normal, s.LineHeight);
            case CodeBlock code:
                return Code(code, ctx);
            case HtmlBlock html:
            {
                // No HTML engine: the markup is shown as it is written, muted, so nothing in the file is silently dropped.
                var raw = new SelectableTextBlock
                {
                    Text = MarkdownPreviewParser.CodeText(html), FontFamily = s.Mono, FontSize = s.CodeFontSize, Foreground = s.Muted,
                    TextWrapping = TextWrapping.Wrap,
                };
                return raw;
            }
            case ThematicBreakBlock:
                return new Border { Height = 2, Background = s.Separator, Margin = new Thickness(0, s.FontSize * 0.5) };
            case QuoteBlock q:
                return new Border
                {
                    Background = s.QuoteBackground,
                    BorderBrush = s.QuoteBorder,
                    BorderThickness = new Thickness(5, 0, 0, 0),
                    Padding = new Thickness(14, 6, 10, 6),
                    Child = Stack(q, ctx, listDepth),
                };
            case ListBlock list:
                return List(list, ctx, listDepth);
            case Table table:
                return TableControl(table, ctx);
            case ContainerBlock container:
                return Stack(container, ctx, listDepth);
            case LeafBlock leaf:
                return leaf.Inline is not null
                    ? Text(leaf.Inline, ctx, s.FontSize, FontWeight.Normal, s.LineHeight)
                    : new SelectableTextBlock { Text = MarkdownPreviewParser.CodeText(leaf), FontFamily = s.Body, FontSize = s.FontSize, Foreground = s.Foreground, TextWrapping = TextWrapping.Wrap };
            default:
                return new Panel();
        }
    }

    private static StackPanel Stack(ContainerBlock container, MarkdownRenderContext ctx, int listDepth)
    {
        var panel = new StackPanel { Spacing = ctx.Style.BlockSpacing * 0.6 };
        foreach (var child in container)
        {
            if (child is LinkReferenceDefinitionGroup or BlankLineBlock) continue;
            panel.Children.Add(Render(child, ctx, listDepth));
        }
        return panel;
    }

    // ---------------------------------------------------------------- code

    private static Control Code(LeafBlock code, MarkdownRenderContext ctx)
    {
        var s = ctx.Style;
        var text = MarkdownPreviewParser.CodeText(code);
        var tb = new SelectableTextBlock
        {
            FontFamily = s.Mono, FontSize = s.CodeFontSize, Foreground = s.Foreground, TextWrapping = TextWrapping.NoWrap,
            LineHeight = s.CodeFontSize * 1.45,
        };
        if (ctx.CodeSpans.TryGetValue(code, out var spans) && spans.Count > 0) MarkdownView.AddColoredRuns(tb, text, spans);
        else tb.Text = text;
        return new Border
        {
            Background = s.CodeBackground,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(14, 10),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = tb,
            },
        };
    }

    // ---------------------------------------------------------------- lists

    private static Control List(ListBlock list, MarkdownRenderContext ctx, int depth)
    {
        var s = ctx.Style;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var number = list.IsOrdered && int.TryParse(list.OrderedStart, out var first) ? first : 1;
        var row = 0;
        foreach (var child in list)
        {
            if (child is not ListItemBlock item) continue;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Control marker;
            if (TaskOf(item) is { } task)
            {
                marker = new CheckBox
                {
                    IsChecked = task.Checked, IsHitTestVisible = false, Focusable = false, MinWidth = 0, MinHeight = 0,
                    Padding = new Thickness(0), Margin = new Thickness(0, 2, 6, 0), VerticalAlignment = VerticalAlignment.Top,
                };
                Avalonia.Automation.AutomationProperties.SetName(marker, task.Checked ? "Done" : "Not done");
            }
            else
            {
                var glyph = list.IsOrdered ? $"{number}{(list.OrderedDelimiter == ')' ? ')' : '.')}" : (depth % 3) switch { 0 => "•", 1 => "◦", _ => "▪" };
                marker = new TextBlock
                {
                    Text = glyph, FontFamily = s.Body, FontSize = s.FontSize, Foreground = s.Foreground, LineHeight = s.FontSize * s.LineHeight,
                    TextAlignment = TextAlignment.Right, MinWidth = s.FontSize * (list.IsOrdered ? 1.4 : 0.9), Margin = new Thickness(s.FontSize * 0.4, 0, s.FontSize * 0.5, 0),
                };
            }
            number++;
            Grid.SetRow(marker, row);
            grid.Children.Add(marker);
            var body = new StackPanel { Spacing = list.IsLoose ? s.BlockSpacing * 0.6 : 2 };
            foreach (var inner in item)
            {
                if (inner is BlankLineBlock or LinkReferenceDefinitionGroup) continue;
                body.Children.Add(inner is ListBlock nested ? List(nested, ctx, depth + 1) : Render(inner, ctx, depth + 1));
            }
            Grid.SetRow(body, row);
            Grid.SetColumn(body, 1);
            grid.Children.Add(body);
            row++;
        }
        grid.RowSpacing = list.IsLoose ? s.BlockSpacing * 0.6 : 2;
        return grid;
    }

    private static TaskList? TaskOf(ListItemBlock item) =>
        item.Count > 0 && item[0] is ParagraphBlock { Inline.FirstChild: TaskList task } ? task : null;

    // ---------------------------------------------------------------- tables

    private static Control TableControl(Table table, MarkdownRenderContext ctx)
    {
        var s = ctx.Style;
        var columns = Math.Max(1, table.ColumnDefinitions.Count);
        foreach (var r in table.OfType<TableRow>()) columns = Math.Max(columns, r.Count);
        var grid = new Grid();
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var row = 0;
        foreach (var r in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < r.Count; c++)
            {
                if (r[c] is not TableCell cell) continue;
                var column = cell.ColumnIndex >= 0 ? cell.ColumnIndex : c;
                var align = column < table.ColumnDefinitions.Count ? table.ColumnDefinitions[column].Alignment : null;
                var content = new StackPanel();
                foreach (var inner in cell)
                {
                    var rendered = Render(inner, ctx, 0);
                    if (rendered is TextBlock tb)
                    {
                        if (r.IsHeader) tb.FontWeight = FontWeight.SemiBold;
                        tb.TextAlignment = align switch { TableColumnAlign.Center => TextAlignment.Center, TableColumnAlign.Right => TextAlignment.Right, _ => TextAlignment.Left };
                    }
                    content.Children.Add(rendered);
                }
                var border = new Border
                {
                    BorderBrush = s.Separator,
                    BorderThickness = new Thickness(column == 0 ? 1 : 0, row == 0 ? 1 : 0, 1, 1),
                    Padding = new Thickness(10, 4),
                    Background = r.IsHeader ? s.TableHeaderBackground : null,
                    Child = content,
                };
                Grid.SetRow(border, row);
                Grid.SetColumn(border, Math.Min(column, columns - 1));
                grid.Children.Add(border);
            }
            row++;
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }

    // ---------------------------------------------------------------- text and inlines

    private static SelectableTextBlock Text(ContainerInline? inlines, MarkdownRenderContext ctx, double fontSize, FontWeight weight, double lineHeight)
    {
        var s = ctx.Style;
        var tb = new SelectableTextBlock
        {
            FontFamily = s.Body,
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = s.Foreground,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = fontSize * lineHeight,
            SelectionBrush = s.FindMatch,
        };
        if (inlines is null) return tb;
        var links = new List<(int, int, string)>();
        var offset = 0;
        AddInlines(tb.Inlines!, inlines, ctx, links, ref offset);
        if (links.Count > 0)
        {
            tb.SetValue(LinksProperty, links);
            if (ctx.OnLink is { } open) HookLinks(tb, links, open);
        }
        return tb;
    }

    private static void HookLinks(SelectableTextBlock tb, List<(int Start, int End, string Url)> links, Action<string> open)
    {
        tb.PointerMoved += (_, e) => tb.Cursor = new Cursor(LinkAt(tb, e.GetPosition(tb), links) is null ? StandardCursorType.Ibeam : StandardCursorType.Hand);
        // Tunnel: the click on a link opens it before the text block starts a selection there.
        tb.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(tb).Properties.IsLeftButtonPressed || e.ClickCount > 1) return;
            if (LinkAt(tb, e.GetPosition(tb), links) is not { } url) return;
            e.Handled = true;
            open(url);
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>The destination of the link at <paramref name="p"/> (text block coordinates), or null.</summary>
    public static string? LinkAt(TextBlock tb, Point p, IReadOnlyList<(int Start, int End, string Url)> links)
    {
        var hit = tb.TextLayout.HitTestPoint(p);
        if (!hit.IsInside) return null;
        var index = hit.TextPosition;
        foreach (var l in links)
            if (index >= l.Start && index < l.End) return l.Url;
        return null;
    }

    private readonly record struct Format(bool Bold, bool Italic, bool Strike);

    private static void AddInlines(InlineCollection target, ContainerInline container, MarkdownRenderContext ctx, List<(int, int, string)> links, ref int offset) =>
        AddChildren(target, container, ctx, links, default, ref offset);

    private static void AddChildren(InlineCollection target, ContainerInline container, MarkdownRenderContext ctx, List<(int, int, string)> links, Format f, ref int offset)
    {
        for (var node = container.FirstChild; node is not null; node = node.NextSibling) AddInline(target, node, ctx, links, f, ref offset);
    }

    private static void AddInline(InlineCollection target, Markdig.Syntax.Inlines.Inline node, MarkdownRenderContext ctx, List<(int, int, string)> links, Format f, ref int offset)
    {
        var s = ctx.Style;
        switch (node)
        {
            case LiteralInline lit:
                AddRun(target, lit.Content.ToString(), f, ref offset);
                break;
            case HtmlEntityInline entity:
                AddRun(target, entity.Transcoded.ToString(), f, ref offset);
                break;
            case CodeInline code:
            {
                var run = new Run(code.Content) { FontFamily = s.Mono, FontSize = s.CodeFontSize, Background = s.CodeBackground, Foreground = s.InlineCode };
                target.Add(run);
                offset += code.Content.Length;
                break;
            }
            case LineBreakInline br:
                if (br.IsHard)
                {
                    target.Add(new LineBreak());
                    offset += 1;
                }
                else AddRun(target, " ", f, ref offset);
                break;
            case HtmlInline html:
                // <br> is the one piece of inline HTML that changes the layout; other tags are dropped (their text, if any, still shows).
                if (html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase))
                {
                    target.Add(new LineBreak());
                    offset += 1;
                }
                break;
            case TaskList:
                break; // the list item shows a check box instead
            case LinkInline { IsImage: true } image:
            {
                var alt = PlainText(image);
                var url = image.GetDynamicUrl?.Invoke() ?? image.Url ?? "";
                if (ctx.CreateImage is { } create)
                {
                    target.Add(new InlineUIContainer(create(url, alt)) { BaselineAlignment = BaselineAlignment.TextBottom });
                    offset += 1; // an embedded control is one position of the text layout
                }
                else AddRun(target, alt, f, ref offset);
                break;
            }
            case LinkInline link:
            {
                var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? "";
                var start = offset;
                var span = new Span { Foreground = s.Link };
                AddChildren(span.Inlines, link, ctx, links, f, ref offset);
                target.Add(span);
                if (url.Length > 0) links.Add((start, offset, url));
                break;
            }
            case AutolinkInline auto:
            {
                var url = auto.IsEmail && !auto.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? "mailto:" + auto.Url : auto.Url;
                var start = offset;
                var span = new Span { Foreground = s.Link };
                AddRun(span.Inlines, auto.Url, f, ref offset);
                target.Add(span);
                links.Add((start, offset, url));
                break;
            }
            case EmphasisInline em:
            {
                var next = em.DelimiterChar is '~'
                    ? f with { Strike = true }
                    : em.DelimiterCount >= 3 ? f with { Bold = true, Italic = true }
                    : em.DelimiterCount == 2 ? f with { Bold = true } : f with { Italic = true };
                AddChildren(target, em, ctx, links, next, ref offset);
                break;
            }
            case ContainerInline other:
                AddChildren(target, other, ctx, links, f, ref offset);
                break;
            case LeafInline leaf when leaf.ToString() is { Length: > 0 } fallback:
                AddRun(target, fallback, f, ref offset);
                break;
        }
    }

    private static void AddRun(InlineCollection target, string text, Format f, ref int offset)
    {
        if (text.Length == 0) return;
        var run = new Run(text);
        if (f.Bold) run.FontWeight = FontWeight.Bold;
        if (f.Italic) run.FontStyle = FontStyle.Italic;
        if (f.Strike) run.TextDecorations = TextDecorations.Strikethrough;
        target.Add(run);
        offset += text.Length;
    }

    /// <summary>The text of an inline container without formatting (an image's alt text, a heading's title).</summary>
    public static string PlainText(ContainerInline? container)
    {
        if (container is null) return "";
        var sb = new System.Text.StringBuilder();
        void Walk(ContainerInline c)
        {
            for (var n = c.FirstChild; n is not null; n = n.NextSibling)
            {
                switch (n)
                {
                    case LiteralInline l: sb.Append(l.Content.AsSpan()); break;
                    case CodeInline code: sb.Append(code.Content); break;
                    case HtmlEntityInline e: sb.Append(e.Transcoded.AsSpan()); break;
                    case LineBreakInline: sb.Append(' '); break;
                    case AutolinkInline a: sb.Append(a.Url); break;
                    case ContainerInline inner: Walk(inner); break;
                }
            }
        }
        Walk(container);
        return sb.ToString();
    }
}
