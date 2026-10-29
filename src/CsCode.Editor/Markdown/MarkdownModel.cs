using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.Editor.Markdown;

public abstract record MdBlock;
public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdCodeBlock(string? Language, string Text) : MdBlock;
public sealed record MdList(bool Ordered, IReadOnlyList<IReadOnlyList<MdInline>> Items) : MdBlock;
public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;
public sealed record MdRule : MdBlock;

public abstract record MdInline;
public sealed record MdText(string Text) : MdInline;
public sealed record MdCodeSpan(string Text) : MdInline;
public sealed record MdStrong(IReadOnlyList<MdInline> Children) : MdInline;
public sealed record MdEmphasis(IReadOnlyList<MdInline> Children) : MdInline;
public sealed record MdLink(IReadOnlyList<MdInline> Children, string Url) : MdInline;
public sealed record MdBreak : MdInline;

/// <summary>
/// A small, forgiving Markdown parser for what language servers send in hovers, completion documentation and signature help: paragraphs,
/// headings, fenced and indented code, bullet/numbered lists, block quotes, rules, and inline code / bold / italic / links / autolinks.
/// It never throws and never loops: unknown syntax is text. Pure, so it runs on the editor worker and the result is handed to the UI.
/// </summary>
public static class MarkdownParser
{
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Rule = new(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Bullet = new(@"^\s{0,3}[-*+]\s+(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Numbered = new(@"^\s{0,3}\d{1,9}[.)]\s+(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Fence = new(@"^\s{0,3}(`{3,}|~{3,})\s*([\w+#.-]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Url = new(@"https?://[^\s<>)\]]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlTag = new(@"</?[a-zA-Z][^>]{0,200}>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const int MaxLength = 200_000; // a server returning megabytes of "documentation" must not stall the worker or the popup

    public static IReadOnlyList<MdBlock> Parse(string markdown)
    {
        if (markdown.Length > MaxLength) markdown = markdown[..MaxLength];
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return ParseBlocks(lines);
    }

    private static List<MdBlock> ParseBlocks(string[] lines)
    {
        var blocks = new List<MdBlock>();
        var para = new List<string>();
        var i = 0;

        void FlushParagraph()
        {
            if (para.Count == 0) return;
            blocks.Add(new MdParagraph(ParseInlines(JoinParagraph(para))));
            para.Clear();
        }

        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                i++;
                continue;
            }
            if (Fence.Match(line) is { Success: true } fence)
            {
                FlushParagraph();
                var marker = fence.Groups[1].Value;
                var language = fence.Groups[2].Value.Length > 0 ? fence.Groups[2].Value : null;
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !(lines[i].TrimStart().StartsWith(marker, StringComparison.Ordinal) && lines[i].Trim().Trim(marker[0]).Length == 0))
                {
                    code.Append(lines[i]).Append('\n');
                    i++;
                }
                i++; // closing fence (or end of input)
                blocks.Add(new MdCodeBlock(language, code.ToString().TrimEnd('\n')));
                continue;
            }
            if (Heading.Match(line) is { Success: true } h)
            {
                FlushParagraph();
                blocks.Add(new MdHeading(h.Groups[1].Length, ParseInlines(h.Groups[2].Value)));
                i++;
                continue;
            }
            if (Rule.IsMatch(line) && para.Count == 0)
            {
                blocks.Add(new MdRule());
                i++;
                continue;
            }
            if (line.TrimStart().StartsWith('>'))
            {
                FlushParagraph();
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    var t = lines[i].TrimStart()[1..];
                    quote.Add(t.StartsWith(' ') ? t[1..] : t);
                    i++;
                }
                blocks.Add(new MdQuote(ParseBlocks(quote.ToArray())));
                continue;
            }
            if (Bullet.IsMatch(line) || Numbered.IsMatch(line))
            {
                FlushParagraph();
                var ordered = Numbered.IsMatch(line);
                var items = new List<IReadOnlyList<MdInline>>();
                while (i < lines.Length && (ordered ? Numbered : Bullet).Match(lines[i]) is { Success: true } m)
                {
                    var text = new StringBuilder(m.Groups[1].Value);
                    i++;
                    while (i < lines.Length && lines[i].StartsWith("  ", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(lines[i]) && !Bullet.IsMatch(lines[i]) && !Numbered.IsMatch(lines[i]))
                    {
                        text.Append(' ').Append(lines[i].Trim());
                        i++;
                    }
                    items.Add(ParseInlines(text.ToString()));
                }
                blocks.Add(new MdList(ordered, items));
                continue;
            }
            if (para.Count == 0 && (line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t')))
            {
                var code = new StringBuilder();
                while (i < lines.Length && (lines[i].StartsWith("    ", StringComparison.Ordinal) || lines[i].StartsWith('\t') || string.IsNullOrWhiteSpace(lines[i])))
                {
                    code.Append(lines[i].StartsWith('\t') ? lines[i][1..] : lines[i].Length >= 4 ? lines[i][4..] : "").Append('\n');
                    i++;
                }
                blocks.Add(new MdCodeBlock(null, code.ToString().TrimEnd('\n')));
                continue;
            }
            para.Add(line);
            i++;
        }
        FlushParagraph();
        return blocks;
    }

    /// <summary>Lines of a paragraph joined with a space; a trailing double space or backslash is a hard break (kept as '\n').</summary>
    private static string JoinParagraph(List<string> lines)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var hard = l.EndsWith("  ", StringComparison.Ordinal) || l.EndsWith('\\');
            sb.Append(l.TrimEnd(' ', '\\').TrimStart());
            if (i < lines.Count - 1) sb.Append(hard ? '\n' : ' ');
        }
        return sb.ToString();
    }

    public static IReadOnlyList<MdInline> ParseInlines(string text)
    {
        text = HtmlTag.Replace(text, m => m.Value.StartsWith("<br", StringComparison.OrdinalIgnoreCase) ? "\n" : "");
        return Inlines(text, 0);
    }

    private static List<MdInline> Inlines(string s, int depth)
    {
        var result = new List<MdInline>();
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length == 0) return;
            result.Add(new MdText(text.ToString()));
            text.Clear();
        }
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length && "\\`*_{}[]()#+-.!<>|".Contains(s[i + 1]))
            {
                text.Append(s[i + 1]);
                i += 2;
                continue;
            }
            if (c == '\n')
            {
                Flush();
                result.Add(new MdBreak());
                i++;
                continue;
            }
            if (c == '`')
            {
                var ticks = 1;
                while (i + ticks < s.Length && s[i + ticks] == '`') ticks++;
                var close = s.IndexOf(new string('`', ticks), i + ticks, StringComparison.Ordinal);
                if (close > 0)
                {
                    Flush();
                    result.Add(new MdCodeSpan(s[(i + ticks)..close].Trim(' ')));
                    i = close + ticks;
                    continue;
                }
                text.Append(s, i, ticks);
                i += ticks;
                continue;
            }
            if (depth < 4 && (c == '*' || c == '_') && TryEmphasis(s, i, c, out var node, out var next))
            {
                Flush();
                result.Add(node!);
                i = next;
                continue;
            }
            if (depth < 4 && c == '[' && TryLink(s, i, depth, out var link, out next))
            {
                Flush();
                result.Add(link!);
                i = next;
                continue;
            }
            if (c == '<' && i + 1 < s.Length && (s.AsSpan(i + 1).StartsWith("http://") || s.AsSpan(i + 1).StartsWith("https://")))
            {
                var end = s.IndexOf('>', i);
                if (end > i)
                {
                    var url = s[(i + 1)..end];
                    Flush();
                    result.Add(new MdLink([new MdText(url)], url));
                    i = end + 1;
                    continue;
                }
            }
            if (c == 'h' && (s.AsSpan(i).StartsWith("http://") || s.AsSpan(i).StartsWith("https://")) && (i == 0 || !char.IsLetterOrDigit(s[i - 1])))
            {
                var m = Url.Match(s, i);
                if (m.Success && m.Index == i)
                {
                    var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
                    Flush();
                    result.Add(new MdLink([new MdText(url)], url));
                    i += url.Length;
                    continue;
                }
            }
            text.Append(c);
            i++;
        }
        Flush();
        return result;
    }

    private static bool TryEmphasis(string s, int i, char marker, out MdInline? node, out int next)
    {
        node = null;
        next = i;
        var strong = i + 1 < s.Length && s[i + 1] == marker;
        var len = strong ? 2 : 1;
        var start = i + len;
        if (start >= s.Length || char.IsWhiteSpace(s[start])) return false;
        // snake_case_names are not emphasis: an underscore opener must not follow a word character
        if (marker == '_' && i > 0 && char.IsLetterOrDigit(s[i - 1])) return false;
        var delimiter = new string(marker, len);
        var close = start;
        while (true)
        {
            close = s.IndexOf(delimiter, close, StringComparison.Ordinal);
            if (close < 0) return false;
            if (close > start && !char.IsWhiteSpace(s[close - 1]) && (marker != '_' || close + len >= s.Length || !char.IsLetterOrDigit(s[close + len]))) break;
            close += len;
        }
        var inner = Inlines(s[start..close], 1);
        node = strong ? new MdStrong(inner) : new MdEmphasis(inner);
        next = close + len;
        return true;
    }

    private static bool TryLink(string s, int i, int depth, out MdInline? link, out int next)
    {
        link = null;
        next = i;
        var closeText = FindMatching(s, i, '[', ']');
        if (closeText < 0 || closeText + 1 >= s.Length || s[closeText + 1] != '(') return false;
        var closeUrl = s.IndexOf(')', closeText + 2);
        if (closeUrl < 0) return false;
        var target = s[(closeText + 2)..closeUrl].Trim();
        var space = target.IndexOf(' ');
        var url = (space > 0 ? target[..space] : target).Trim('<', '>');
        link = new MdLink(Inlines(s[(i + 1)..closeText], depth + 1), url);
        next = closeUrl + 1;
        return true;
    }

    private static int FindMatching(string s, int open, char o, char c)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '\\') i++;
            else if (s[i] == o) depth++;
            else if (s[i] == c && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>The plain text of inlines (for tooltips, accessibility names and tests).</summary>
    public static string PlainText(IEnumerable<MdInline> inlines)
    {
        var sb = new StringBuilder();
        foreach (var n in inlines)
            sb.Append(n switch
            {
                MdText t => t.Text,
                MdCodeSpan c => c.Text,
                MdStrong s => PlainText(s.Children),
                MdEmphasis e => PlainText(e.Children),
                MdLink l => PlainText(l.Children),
                MdBreak => "\n",
                _ => "",
            });
        return sb.ToString();
    }
}
