using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace CsCode.Editor.Markdown;

/// <summary>Visual settings for rendered Markdown (taken from the editor palette so popups follow the theme).</summary>
public sealed record MarkdownStyle(FontFamily Body, FontFamily Mono, double FontSize, IBrush Foreground, IBrush Link, IBrush CodeBackground, IBrush Muted);

/// <summary>A coloured run inside a code block (offsets into the block's text), prepared on the worker with the TextMate theme.</summary>
public readonly record struct CodeSpan(int Start, int Length, Color? Foreground, bool Bold, bool Italic);

/// <summary>
/// Builds controls from a parsed Markdown model (<see cref="MarkdownParser"/>): paragraphs and headings as wrapped <see cref="TextBlock"/>s with
/// inline runs, code blocks with TextMate colours when spans are supplied, lists, quotes, rules, clickable links. UI thread only; cheap — the
/// expensive parsing and code highlighting already happened on the worker.
/// </summary>
public static class MarkdownView
{
    private const int MaxBlocks = 200;

    public static Control Build(
        IReadOnlyList<MdBlock> blocks,
        MarkdownStyle style,
        IReadOnlyDictionary<MdCodeBlock, IReadOnlyList<CodeSpan>>? codeSpans = null,
        Action<string>? onLink = null)
    {
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical, Spacing = 6 };
        foreach (var block in blocks.Take(MaxBlocks)) panel.Children.Add(BuildBlock(block, style, codeSpans, onLink));
        return panel;
    }

    private static Control BuildBlock(MdBlock block, MarkdownStyle style, IReadOnlyDictionary<MdCodeBlock, IReadOnlyList<CodeSpan>>? spans, Action<string>? onLink)
    {
        switch (block)
        {
            case MdParagraph p:
                return Text(p.Inlines, style, 1.0, FontWeight.Normal, onLink);
            case MdHeading h:
                return Text(h.Inlines, style, h.Level switch { 1 => 1.5, 2 => 1.3, 3 => 1.15, _ => 1.0 }, FontWeight.SemiBold, onLink);
            case MdCodeBlock c:
            {
                var tb = new TextBlock { FontFamily = style.Mono, FontSize = style.FontSize, Foreground = style.Foreground, TextWrapping = TextWrapping.NoWrap };
                if (spans is not null && spans.TryGetValue(c, out var colored) && colored.Count > 0) AddColoredRuns(tb, c.Text, colored);
                else tb.Text = c.Text;
                return new Border
                {
                    Background = style.CodeBackground,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(8, 5),
                    Child = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = tb },
                };
            }
            case MdList l:
            {
                var stack = new StackPanel { Spacing = 2 };
                for (var i = 0; i < l.Items.Count; i++)
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                    row.Children.Add(new TextBlock { Text = l.Ordered ? $"{i + 1}." : "•", Foreground = style.Muted, FontSize = style.FontSize, FontFamily = style.Body, Margin = new Thickness(0, 0, 6, 0) });
                    var t = Text(l.Items[i], style, 1.0, FontWeight.Normal, onLink);
                    Grid.SetColumn(t, 1);
                    row.Children.Add(t);
                    stack.Children.Add(row);
                }
                return stack;
            }
            case MdQuote q:
                return new Border
                {
                    BorderBrush = style.Muted,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(8, 0, 0, 0),
                    Child = Build(q.Blocks, style, spans, onLink),
                };
            case MdRule:
                return new Border { Height = 1, Background = style.Muted, Opacity = 0.5, Margin = new Thickness(0, 4) };
            default:
                return new TextBlock();
        }
    }

    private static TextBlock Text(IReadOnlyList<MdInline> inlines, MarkdownStyle style, double scale, FontWeight weight, Action<string>? onLink)
    {
        var tb = new TextBlock
        {
            FontFamily = style.Body,
            FontSize = style.FontSize * scale,
            FontWeight = weight,
            Foreground = style.Foreground,
            TextWrapping = TextWrapping.Wrap,
        };
        var links = new List<(int Start, int End, string Url)>();
        var offset = 0;
        AddInlines(tb.Inlines!, inlines, style, bold: false, italic: false, links, ref offset);
        if (links.Count > 0 && onLink is not null)
        {
            tb.Cursor = new Cursor(StandardCursorType.Arrow);
            tb.PointerMoved += (_, e) =>
            {
                var hit = LinkAt(tb, e.GetPosition(tb), links);
                tb.Cursor = new Cursor(hit is null ? StandardCursorType.Arrow : StandardCursorType.Hand);
            };
            tb.PointerPressed += (_, e) =>
            {
                if (LinkAt(tb, e.GetPosition(tb), links) is { } url)
                {
                    onLink(url);
                    e.Handled = true;
                }
            };
        }
        return tb;
    }

    private static string? LinkAt(TextBlock tb, Point p, List<(int Start, int End, string Url)> links)
    {
        var index = tb.TextLayout.HitTestPoint(p).TextPosition;
        foreach (var l in links)
            if (index >= l.Start && index < l.End) return l.Url;
        return null;
    }

    private static void AddInlines(InlineCollection target, IReadOnlyList<MdInline> inlines, MarkdownStyle style, bool bold, bool italic, List<(int, int, string)> links, ref int offset)
    {
        foreach (var node in inlines)
        {
            switch (node)
            {
                case MdText t:
                    target.Add(StyledRun(t.Text, style, bold, italic));
                    offset += t.Text.Length;
                    break;
                case MdCodeSpan c:
                    target.Add(new Run(c.Text) { FontFamily = style.Mono, Background = style.CodeBackground });
                    offset += c.Text.Length;
                    break;
                case MdBreak:
                    target.Add(new LineBreak());
                    offset += 1; // a LineBreak occupies one position in the text layout
                    break;
                case MdStrong s:
                    AddInlines(target, s.Children, style, true, italic, links, ref offset);
                    break;
                case MdEmphasis e:
                    AddInlines(target, e.Children, style, bold, true, links, ref offset);
                    break;
                case MdLink l:
                {
                    var start = offset;
                    var span = new Span { Foreground = style.Link, TextDecorations = Avalonia.Media.TextDecorations.Underline };
                    AddInlines(span.Inlines, l.Children, style, bold, italic, links, ref offset);
                    target.Add(span);
                    links.Add((start, offset, l.Url));
                    break;
                }
            }
        }
    }

    private static Run StyledRun(string text, MarkdownStyle style, bool bold, bool italic)
    {
        var run = new Run(text);
        if (bold) run.FontWeight = FontWeight.Bold;
        if (italic) run.FontStyle = FontStyle.Italic;
        return run;
    }

    internal static void AddColoredRuns(TextBlock tb, string text, IReadOnlyList<CodeSpan> spans)
    {
        var pos = 0;
        foreach (var s in spans.OrderBy(x => x.Start))
        {
            if (s.Start < pos || s.Start + s.Length > text.Length || s.Length <= 0) continue;
            if (s.Start > pos) tb.Inlines!.Add(new Run(text[pos..s.Start]));
            var run = new Run(text.Substring(s.Start, s.Length));
            if (s.Foreground is { } c) run.Foreground = new SolidColorBrush(c);
            if (s.Bold) run.FontWeight = FontWeight.Bold;
            if (s.Italic) run.FontStyle = FontStyle.Italic;
            tb.Inlines!.Add(run);
            pos = s.Start + s.Length;
        }
        if (pos < text.Length) tb.Inlines!.Add(new Run(text[pos..]));
    }
}
