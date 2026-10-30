using System.Globalization;
using System.Text;
using CsCode.Core.Theming;
using CsCode.Editor.Theming;

namespace CsCode.Editor.Editing;

/// <summary>One copied line: its text and the syntax style runs that cover it (columns relative to <see cref="Text"/>; gaps are plain).</summary>
public readonly record struct HtmlClipboardLine(string Text, IReadOnlyList<HtmlClipboardRun> Runs);

/// <summary>A styled stretch of a copied line: <see cref="Start"/> and <see cref="Length"/> in UTF-16 columns of the line's text.</summary>
public readonly record struct HtmlClipboardRun(int Start, int Length, ResolvedTokenStyle Style);

/// <summary>What the whole fragment looks like: the editor's colours and font (the theme's <c>editor.background</c> / <c>editor.foreground</c>).</summary>
public sealed record HtmlClipboardStyle(Rgba Background, Rgba Foreground, string FontFamily, double FontSize, double LineHeight, int TabSize);

/// <summary>
/// Copy with formatting: the copied text as an HTML fragment with the theme's syntax colours, for pasting into documents and mail (the clipboard
/// service offers it as <c>text/html</c> next to the plain text). The same shape VS Code writes: an outer <c>div</c> with the editor's background,
/// foreground and font, one <c>div</c> per line, one <c>span</c> per run of equal style; <c>white-space: pre</c> keeps the indentation and tabs.
/// Pure and thread-agnostic (the editor builds it on a worker).
/// </summary>
public static class HtmlClipboard
{
    /// <summary>
    /// Above this many characters a copy carries no HTML (only the plain text), as in VS Code: a rich paste of megabytes helps nobody, and the
    /// fragment is several times the text.
    /// </summary>
    public const int MaxChars = 65_536;

    public static string Build(IReadOnlyList<HtmlClipboardLine> lines, HtmlClipboardStyle style)
    {
        var sb = new StringBuilder(64 + lines.Sum(l => l.Text.Length) * 3);
        sb.Append("<meta charset=\"utf-8\"><div style=\"");
        sb.Append("color: ").Append(Hex(style.Foreground)).Append(';');
        sb.Append("background-color: ").Append(Hex(style.Background)).Append(';');
        sb.Append("font-family: ").Append(CssFontFamily(style.FontFamily)).Append(';');
        sb.Append("font-weight: normal;");
        sb.Append("font-size: ").Append(Px(style.FontSize)).Append(';');
        sb.Append("line-height: ").Append(Px(style.LineHeight)).Append(';');
        sb.Append("white-space: pre;");
        sb.Append("tab-size: ").Append(style.TabSize.ToString(CultureInfo.InvariantCulture)).Append(';');
        sb.Append("\">");
        foreach (var line in lines)
        {
            sb.Append("<div>");
            if (line.Text.Length == 0) sb.Append("<br>");
            else AppendLine(sb, line, style.Foreground);
            sb.Append("</div>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    private static void AppendLine(StringBuilder sb, HtmlClipboardLine line, Rgba defaultForeground)
    {
        var text = line.Text;
        var column = 0;
        ResolvedTokenStyle? open = null;
        void Open(ResolvedTokenStyle s)
        {
            sb.Append("<span style=\"");
            sb.Append("color: ").Append(Hex(s.Foreground ?? defaultForeground)).Append(';');
            if (s.Background is { } bg) sb.Append("background-color: ").Append(Hex(bg)).Append(';');
            if (s.Bold) sb.Append("font-weight: bold;");
            if (s.Italic) sb.Append("font-style: italic;");
            if (s.Underline || s.Strikethrough) sb.Append("text-decoration: ").Append(s.Underline && s.Strikethrough ? "underline line-through" : s.Underline ? "underline" : "line-through").Append(';');
            sb.Append("\">");
            open = s;
        }
        void Emit(int from, int to, ResolvedTokenStyle s)
        {
            if (to <= from) return;
            if (open is not { } current || current != s)
            {
                if (open is not null) sb.Append("</span>");
                Open(s);
            }
            AppendEscaped(sb, text.AsSpan(from, to - from));
        }
        var plain = new ResolvedTokenStyle(null, null, false, false, false, false);
        foreach (var run in line.Runs.OrderBy(r => r.Start))
        {
            var start = Math.Clamp(run.Start, 0, text.Length);
            var end = Math.Clamp(run.Start + run.Length, start, text.Length);
            if (start < column) start = column; // overlapping runs: the earlier one wins
            Emit(column, start, plain);
            Emit(start, end, run.Style);
            column = Math.Max(column, end);
        }
        Emit(column, text.Length, plain);
        if (open is not null) sb.Append("</span>");
    }

    private static void AppendEscaped(StringBuilder sb, ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\r' or '\n': break; // lines are divs
                default: sb.Append(c); break;
            }
        }
    }

    internal static string Hex(Rgba c) => c.A == 255
        ? string.Create(CultureInfo.InvariantCulture, $"#{c.R:x2}{c.G:x2}{c.B:x2}")
        : string.Create(CultureInfo.InvariantCulture, $"rgba({c.R}, {c.G}, {c.B}, {c.A / 255.0:0.###})");

    private static string Px(double v) => v.ToString("0.##", CultureInfo.InvariantCulture) + "px";

    /// <summary>The editor's font family list ("Cascadia Code, Consolas") as CSS, quoted where needed, with monospace as the last resort.</summary>
    internal static string CssFontFamily(string families)
    {
        var parts = families.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim('\'', '"'))
            .Where(f => f.Length > 0 && f.IndexOfAny(['<', '>', '"', ';', '{', '}']) < 0)
            .Select(f => f is "monospace" or "serif" or "sans-serif" ? f : "'" + f.Replace("'", "", StringComparison.Ordinal) + "'")
            .ToList();
        if (!parts.Contains("monospace")) parts.Add("monospace");
        return string.Join(", ", parts);
    }
}
