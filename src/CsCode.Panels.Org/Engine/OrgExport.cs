using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.Panels.Org.Engine;

public enum OrgExportFormat { Html, Markdown }

/// <summary>
/// Org's export (C-c C-e) to a standalone HTML page or to Markdown (GitHub's): the title (<c>#+TITLE</c>), headings with their TODO state and
/// priority (HTML: tags too), paragraphs, <c>*bold*</c> <c>/italic/</c> <c>_underline_</c> <c>+strike+</c> <c>=verbatim=</c> <c>~code~</c>,
/// links and URLs, timestamps, plain and ordered lists (nested, with check boxes, <c>term :: description</c>), tables, source blocks with
/// their language, example, quote and verse blocks, fixed-width lines and rules. Left out, as org leaves them out by default: planning lines,
/// drawers, <c>#+KEYWORD</c> lines, comments, results of source blocks, <c>COMMENT</c> headings and subtrees tagged <c>:noexport:</c> or
/// <c>:ARCHIVE:</c>.
/// </summary>
public static class OrgExport
{
    private static readonly Regex HeadingRe = new(@"^\*+[ \t]", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex KeywordRe = new(@"^[ \t]*#\+\w+:", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CommentRe = new(@"^[ \t]*#([ \t]|$)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BlockStartRe = new(@"^[ \t]*#\+begin_(\w+)[ \t]*(.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DrawerStartRe = new(@"^[ \t]*:[\w\-]+:[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DrawerEndRe = new(@"^[ \t]*:END:[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PlanningRe = new(@"^[ \t]*(?:SCHEDULED|DEADLINE|CLOSED):", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ListRe = new(@"^([ \t]*)([-+]|[ \t]+\*|\d+[.)])[ \t]+(?:\[([ xX\-])\][ \t]+)?(.*)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex RuleRe = new(@"^[ \t]*-{5,}[ \t]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FixedRe = new(@"^[ \t]*:( |$)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Inline = new(
        @"(?<link>\[\[(?<target>[^\]\[]+)\](?:\[(?<desc>[^\]\[]+)\])?\])" +
        @"|(?<ts>[<\[]\d{4}-\d{2}-\d{2}[^>\]\n]*[>\]](?:--[<\[]\d{4}-\d{2}-\d{2}[^>\]\n]*[>\]])?)" +
        @"|(?<url>\b(?:https?|ftp|mailto):[^\s<>\[\]""']+[^\s<>\[\]""'.,;:!?)])" +
        @"|(?:(?<=^|[\s('""{\-])(?<m>[*/_+=~])(?<body>[^\s]|[^\s].*?[^\s])\k<m>(?=$|[\s\-.,:!?;'"")}\]]))",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Export(OrgDocument doc, OrgExportFormat format) => format == OrgExportFormat.Html ? ToHtml(doc) : ToMarkdown(doc);

    // ------------------------------------------------------------------------------------------------ the document as blocks

    private abstract record Block;
    private sealed record HeadingBlock(int Level, OrgHeading Heading) : Block;
    private sealed record Paragraph(List<string> Lines) : Block;
    private sealed record ListItem(int Indent, bool Ordered, char? Check, string Text, string? Term);
    private sealed record ListBlock(List<ListItem> Items) : Block;
    private sealed record CodeBlock(string Kind, string Language, List<string> Lines) : Block;
    private sealed record QuoteBlock(string Kind, List<string> Lines) : Block;
    private sealed record TableBlock(List<string[]> Rows, bool HasHeader) : Block;
    private sealed record RuleBlock : Block;

    private static List<Block> Blocks(OrgDocument doc)
    {
        var blocks = new List<Block>();
        var skipUntil = -1;
        var headings = doc.Headings.ToDictionary(h => h.Line);
        var lines = doc.Lines;
        for (var i = 0; i < doc.ContentLineCount; i++)
        {
            if (i < skipUntil) continue;
            var line = lines[i];
            if (headings.TryGetValue(i, out var h))
            {
                if (h.Title.StartsWith("COMMENT", StringComparison.Ordinal) || h.Tags.Any(t => t is "noexport" or "ARCHIVE")) { skipUntil = h.SubtreeEnd; continue; }
                blocks.Add(new HeadingBlock(h.Level, h));
                continue;
            }
            if (string.IsNullOrWhiteSpace(line) || PlanningRe.IsMatch(line) || CommentRe.IsMatch(line)) continue;
            if (BlockStartRe.Match(line) is { Success: true } b)
            {
                var kind = b.Groups[1].Value.ToLowerInvariant();
                var body = new List<string>();
                var end = i + 1;
                while (end < doc.ContentLineCount && !Regex.IsMatch(lines[end], @"^[ \t]*#\+end_" + Regex.Escape(kind) + @"\b", RegexOptions.IgnoreCase)) body.Add(lines[end++]);
                i = end;
                var language = kind == "src" ? b.Groups[2].Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "" : "";
                if (kind is "src" or "example" or "export") blocks.Add(new CodeBlock(kind, language, Dedent(body)));
                else if (kind is "comment") { }
                else blocks.Add(new QuoteBlock(kind, body));
                continue;
            }
            if (KeywordRe.IsMatch(line))
            {
                // #+RESULTS: and what follows it are the results of a block (not exported by default)
                if (line.TrimStart().StartsWith("#+RESULTS", StringComparison.OrdinalIgnoreCase) && i + 1 < doc.ContentLineCount)
                {
                    // one element: fixed-width lines, a table or a block
                    var next = lines[i + 1];
                    if (FixedRe.IsMatch(next)) while (i + 1 < doc.ContentLineCount && FixedRe.IsMatch(lines[i + 1])) i++;
                    else if (next.TrimStart().StartsWith('|')) while (i + 1 < doc.ContentLineCount && lines[i + 1].TrimStart().StartsWith('|')) i++;
                    else if (BlockStartRe.IsMatch(next) || DrawerStartRe.IsMatch(next))
                    {
                        i++;
                        while (i + 1 < doc.ContentLineCount && !Regex.IsMatch(lines[i], @"^[ \t]*(#\+end_|:end:)", RegexOptions.IgnoreCase)) i++;
                    }
                }
                continue;
            }
            if (DrawerStartRe.IsMatch(line))
            {
                var end = i + 1;
                while (end < doc.ContentLineCount && !DrawerEndRe.IsMatch(lines[end]) && !headings.ContainsKey(end)) end++;
                if (end < doc.ContentLineCount && DrawerEndRe.IsMatch(lines[end])) { i = end; continue; }
            }
            if (RuleRe.IsMatch(line)) { blocks.Add(new RuleBlock()); continue; }
            if (line.TrimStart().StartsWith('|'))
            {
                var rows = new List<string[]>();
                var header = false;
                while (i < doc.ContentLineCount && lines[i].TrimStart().StartsWith('|'))
                {
                    var row = lines[i].Trim();
                    if (row.StartsWith("|-", StringComparison.Ordinal)) { if (rows.Count == 1) header = true; }
                    else rows.Add([.. row.Trim('|').Split('|').Select(c => c.Trim())]);
                    i++;
                }
                i--;
                if (rows.Count > 0) blocks.Add(new TableBlock(rows, header));
                continue;
            }
            if (FixedRe.IsMatch(line))
            {
                var body = new List<string>();
                while (i < doc.ContentLineCount && FixedRe.IsMatch(lines[i])) { var t = lines[i].TrimStart(); body.Add(t.Length > 1 ? t[2..] : ""); i++; }
                i--;
                blocks.Add(new CodeBlock("example", "", body));
                continue;
            }
            if (ListRe.Match(line) is { Success: true })
            {
                var items = new List<ListItem>();
                while (i < doc.ContentLineCount && !headings.ContainsKey(i))
                {
                    var l = lines[i];
                    if (ListRe.Match(l) is { Success: true } m)
                    {
                        var text = m.Groups[4].Value;
                        string? term = null;
                        var sep = text.IndexOf(" :: ", StringComparison.Ordinal);
                        if (sep > 0) { term = text[..sep]; text = text[(sep + 4)..]; }
                        items.Add(new ListItem(m.Groups[1].Value.Replace("\t", "    ", StringComparison.Ordinal).Length, char.IsDigit(m.Groups[2].Value[0]),
                            m.Groups[3].Success ? m.Groups[3].Value[0] : null, text, term));
                    }
                    else if (!string.IsNullOrWhiteSpace(l) && l.Length > 0 && char.IsWhiteSpace(l[0]) && items.Count > 0)
                        items[^1] = items[^1] with { Text = items[^1].Text + " " + l.Trim() }; // a continuation line of the item
                    else if (string.IsNullOrWhiteSpace(l) && i + 1 < doc.ContentLineCount && ListRe.IsMatch(lines[i + 1])) { }
                    else break;
                    i++;
                }
                i--;
                blocks.Add(new ListBlock(items));
                continue;
            }
            var para = new List<string> { lines[i++].Trim() }; // at least this line, whatever it looks like
            while (i < doc.ContentLineCount && !string.IsNullOrWhiteSpace(lines[i]) && !headings.ContainsKey(i) && !StartsBlock(lines[i])) para.Add(lines[i++].Trim());
            i--;
            if (para.Count > 0) blocks.Add(new Paragraph(para));
        }
        return blocks;
    }

    private static bool StartsBlock(string line) =>
        BlockStartRe.IsMatch(line) || KeywordRe.IsMatch(line) || CommentRe.IsMatch(line) || line.TrimStart().StartsWith('|') || ListRe.IsMatch(line)
        || RuleRe.IsMatch(line) || FixedRe.IsMatch(line) || PlanningRe.IsMatch(line) || DrawerStartRe.IsMatch(line);

    private static List<string> Dedent(List<string> lines)
    {
        var indent = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return [.. lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())];
    }

    // ------------------------------------------------------------------------------------------------ inline markup

    private static string Inlines(string text, bool html)
    {
        var sb = new StringBuilder();
        var at = 0;
        foreach (Match m in Inline.Matches(text))
        {
            if (m.Index < at) continue;
            sb.Append(Plain(text[at..m.Index], html));
            if (m.Groups["link"].Success)
            {
                var target = m.Groups["target"].Value;
                var url = target.StartsWith("file:", StringComparison.Ordinal) ? target[5..] : target;
                if (html && url.EndsWith(".org", StringComparison.OrdinalIgnoreCase)) url = url[..^4] + ".html";
                var desc = m.Groups["desc"].Success ? m.Groups["desc"].Value : target;
                sb.Append(html ? $"<a href=\"{Attr(url)}\">{Inlines(desc, html)}</a>" : $"[{Inlines(desc, html)}]({url.Replace(" ", "%20", StringComparison.Ordinal)})");
            }
            else if (m.Groups["ts"].Success)
                sb.Append(html ? $"<span class=\"timestamp\">{WebUtility.HtmlEncode(m.Value)}</span>" : m.Value.Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal));
            else if (m.Groups["url"].Success)
                sb.Append(html ? $"<a href=\"{Attr(m.Value)}\">{WebUtility.HtmlEncode(m.Value)}</a>" : $"<{m.Value}>");
            else
            {
                var body = m.Groups["body"].Value;
                sb.Append(m.Groups["m"].Value switch
                {
                    "*" => html ? $"<b>{Inlines(body, html)}</b>" : $"**{Inlines(body, html)}**",
                    "/" => html ? $"<i>{Inlines(body, html)}</i>" : $"*{Inlines(body, html)}*",
                    "_" => html ? $"<span class=\"underline\">{Inlines(body, html)}</span>" : $"<u>{Inlines(body, html)}</u>",
                    "+" => html ? $"<del>{Inlines(body, html)}</del>" : $"~~{Inlines(body, html)}~~",
                    _ => html ? $"<code>{WebUtility.HtmlEncode(body)}</code>" : "`" + body + "`",
                });
            }
            at = m.Index + m.Length;
        }
        sb.Append(Plain(text[at..], html));
        return sb.ToString();
    }

    private static string Plain(string text, bool html) => html ? WebUtility.HtmlEncode(text) : text;

    private static string Attr(string value) => WebUtility.HtmlEncode(value);

    // ------------------------------------------------------------------------------------------------ Markdown

    public static string ToMarkdown(OrgDocument doc)
    {
        var sb = new StringBuilder();
        var shift = 0;
        if (doc.Title is { Length: > 0 } title)
        {
            sb.Append("# ").Append(Inlines(title, false)).Append("\n\n");
            shift = 1;
        }
        foreach (var block in Blocks(doc))
        {
            switch (block)
            {
                case HeadingBlock hb:
                    var h = hb.Heading;
                    sb.Append(new string('#', Math.Min(6, hb.Level + shift))).Append(' ');
                    if (h.Keyword is { } kw) sb.Append(kw).Append(' ');
                    if (h.Priority is { } p) sb.Append("[#").Append(p).Append("] ");
                    sb.Append(Inlines(h.Title, false));
                    break;
                case Paragraph para:
                    sb.Append(string.Join('\n', para.Lines.Select(l => Inlines(l, false))));
                    break;
                case ListBlock list:
                    var indents = list.Items.Select(x => x.Indent).Distinct().Order().ToList();
                    sb.Append(string.Join('\n', list.Items.Select(x =>
                        new string(' ', 2 * indents.IndexOf(x.Indent)) + (x.Ordered ? "1. " : "- ")
                        + (x.Check is { } c ? (c is 'x' or 'X' ? "[x] " : "[ ] ") : "")
                        + (x.Term is { } term ? "**" + Inlines(term, false) + "**: " : "")
                        + Inlines(x.Text, false))));
                    break;
                case CodeBlock code:
                    sb.Append("```").Append(code.Language).Append('\n').Append(string.Join('\n', code.Lines)).Append("\n```");
                    break;
                case QuoteBlock quote:
                    sb.Append(string.Join('\n', quote.Lines.Select(l => "> " + Inlines(l.Trim(), false))));
                    break;
                case TableBlock table:
                    var width = table.Rows.Max(r => r.Length);
                    string Row(string[] r) => "| " + string.Join(" | ", Enumerable.Range(0, width).Select(c => c < r.Length ? Inlines(r[c], false).Replace("|", "\\|", StringComparison.Ordinal) : "")) + " |";
                    sb.Append(Row(table.Rows[0])).Append('\n').Append("|" + string.Concat(Enumerable.Repeat(" --- |", width)));
                    foreach (var r in table.Rows.Skip(1)) sb.Append('\n').Append(Row(r));
                    break;
                case RuleBlock:
                    sb.Append("---");
                    break;
            }
            sb.Append("\n\n");
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    // ------------------------------------------------------------------------------------------------ HTML

    public static string ToHtml(OrgDocument doc)
    {
        var title = doc.Title is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(doc.FileName);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n");
        sb.Append("<h1 class=\"title\">").Append(Inlines(title, true)).Append("</h1>\n");
        foreach (var block in Blocks(doc))
        {
            switch (block)
            {
                case HeadingBlock hb:
                    var h = hb.Heading;
                    var tag = "h" + Math.Min(6, hb.Level + 1);
                    sb.Append('<').Append(tag).Append('>');
                    if (h.Keyword is { } kw) sb.Append($"<span class=\"{(h.IsDone ? "done" : "todo")}\">{WebUtility.HtmlEncode(kw)}</span> ");
                    if (h.Priority is { } p) sb.Append($"<span class=\"priority\">[#{p}]</span> ");
                    sb.Append(Inlines(h.Title, true));
                    if (h.Tags.Count > 0) sb.Append(" <span class=\"tags\">").Append(string.Join("", h.Tags.Select(x => $"<span class=\"tag\">{WebUtility.HtmlEncode(x)}</span>"))).Append("</span>");
                    sb.Append("</").Append(tag).Append(">\n");
                    break;
                case Paragraph para:
                    sb.Append("<p>").Append(string.Join("\n", para.Lines.Select(l => Inlines(l, true)))).Append("</p>\n");
                    break;
                case ListBlock list:
                    HtmlList(sb, list.Items);
                    break;
                case CodeBlock code:
                    sb.Append(code.Kind == "src" ? $"<pre class=\"src src-{Attr(code.Language)}\"><code class=\"language-{Attr(code.Language)}\">" : "<pre class=\"example\"><code>")
                      .Append(WebUtility.HtmlEncode(string.Join('\n', code.Lines))).Append("</code></pre>\n");
                    break;
                case QuoteBlock quote:
                    var element = quote.Kind == "quote" ? "blockquote" : quote.Kind == "verse" ? "p class=\"verse\"" : "div class=\"" + Attr(quote.Kind) + "\"";
                    var close = element.Split(' ')[0];
                    sb.Append('<').Append(element).Append('>').Append(string.Join(quote.Kind == "verse" ? "<br>\n" : "\n", quote.Lines.Select(l => Inlines(l.Trim(), true)))).Append("</").Append(close).Append(">\n");
                    break;
                case TableBlock table:
                    sb.Append("<table>\n");
                    var rows = table.Rows.AsEnumerable();
                    if (table.HasHeader)
                    {
                        sb.Append("<thead><tr>").Append(string.Concat(table.Rows[0].Select(c => $"<th>{Inlines(c, true)}</th>"))).Append("</tr></thead>\n");
                        rows = rows.Skip(1);
                    }
                    sb.Append("<tbody>\n");
                    foreach (var r in rows) sb.Append("<tr>").Append(string.Concat(r.Select(c => $"<td>{Inlines(c, true)}</td>"))).Append("</tr>\n");
                    sb.Append("</tbody>\n</table>\n");
                    break;
                case RuleBlock:
                    sb.Append("<hr>\n");
                    break;
            }
        }
        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static void HtmlList(StringBuilder sb, List<ListItem> items)
    {
        var stack = new Stack<(int Indent, string Tag)>();
        foreach (var item in items)
        {
            while (stack.Count > 0 && item.Indent < stack.Peek().Indent) sb.Append("</li>\n</").Append(stack.Pop().Tag).Append(">\n");
            if (stack.Count == 0 || item.Indent > stack.Peek().Indent)
            {
                var tag = item.Ordered ? "ol" : "ul";
                sb.Append('<').Append(tag).Append(">\n");
                stack.Push((item.Indent, tag));
            }
            else sb.Append("</li>\n");
            sb.Append("<li>");
            if (item.Check is { } c) sb.Append(c is 'x' or 'X' ? "<code>[X]</code> " : c == '-' ? "<code>[-]</code> " : "<code>[ ]</code> ");
            if (item.Term is { } term) sb.Append("<b>").Append(Inlines(term, true)).Append("</b>: ");
            sb.Append(Inlines(item.Text, true));
        }
        while (stack.Count > 0) sb.Append("</li>\n</").Append(stack.Pop().Tag).Append(">\n");
    }

    private const string Css = """
        body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif; line-height: 1.55; color: #1f2328; background: #fff; }
        main { max-width: 52rem; margin: 2rem auto; padding: 0 1rem; }
        .title { border-bottom: 1px solid #d0d7de; padding-bottom: .3em; }
        .todo { color: #cf222e; font-weight: 600; }
        .done { color: #1a7f37; font-weight: 600; }
        .priority { color: #9a6700; }
        .tag { font-size: .75em; background: #eaeef2; border-radius: 1em; padding: .1em .6em; margin-left: .3em; font-weight: 400; }
        .timestamp { color: #8250df; }
        .underline { text-decoration: underline; }
        pre { background: #f6f8fa; padding: .8em 1em; border-radius: 6px; overflow: auto; }
        code { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: .9em; }
        table { border-collapse: collapse; margin: 1em 0; }
        th, td { border: 1px solid #d0d7de; padding: .3em .7em; }
        blockquote { border-left: .25em solid #d0d7de; margin: 0; padding: 0 1em; color: #59636e; }
        @media (prefers-color-scheme: dark) {
          body { color: #e6edf3; background: #0d1117; } .title { border-color: #30363d; } pre { background: #161b22; } th, td { border-color: #30363d; }
          .tag { background: #30363d; } .todo { color: #ff7b72; } .done { color: #3fb950; } .timestamp { color: #d2a8ff; }
        }

        """;
}
