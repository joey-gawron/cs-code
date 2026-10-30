using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsCode.Core.Platform;
using CsCode.Core.Text;

namespace CsCode.Platform.EditorConfig;

/// <summary>One parsed <c>.editorconfig</c>: its <c>root</c> flag and its sections in file order.</summary>
internal sealed class EditorConfigFile
{
    public sealed record Section(string Glob, GlobRegex Pattern, IReadOnlyList<KeyValuePair<string, string>> Properties);

    public bool IsRoot { get; init; }
    public IReadOnlyList<Section> Sections { get; init; } = [];

    /// <summary>Applies this file's matching sections, top to bottom (later wins), onto <paramref name="into"/>. <paramref name="relPath"/> is relative to this file's directory.</summary>
    public void Apply(string relPath, Dictionary<string, string> into)
    {
        foreach (var section in Sections)
        {
            if (!section.Pattern.IsMatch(relPath)) continue;
            foreach (var (key, value) in section.Properties)
            {
                if (value == "unset") into.Remove(key);
                else into[key] = value;
            }
        }
    }
}

internal static class EditorConfigParser
{
    private const int MaxLines = 20_000;
    private const int MaxRangeAlternatives = 1000;

    public static EditorConfigFile Parse(string text)
    {
        var root = false;
        var sections = new List<EditorConfigFile.Section>();
        string? glob = null;
        GlobRegex? pattern = null;
        List<KeyValuePair<string, string>>? props = null;

        void Flush()
        {
            if (glob is not null && pattern is not null && props is not null) sections.Add(new EditorConfigFile.Section(glob, pattern, props));
        }

        var lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            if (++lineNo > MaxLines) break;
            var line = raw.Trim(); // also strips '\r' and a UTF-8 BOM's U+FEFF is handled below
            if (lineNo == 1 && line.Length > 0 && line[0] == '﻿') line = line[1..].TrimStart();
            if (line.Length == 0 || line[0] is '#' or ';') continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                Flush();
                glob = line[1..^1];
                pattern = TryCompile(glob);
                props = new List<KeyValuePair<string, string>>();
                continue;
            }

            var eq = line.IndexOfAny(['=', ':']);
            if (eq <= 0) continue; // not a property line
            var key = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim();
            if (key.Length == 0) continue;

            if (glob is null)
            {
                if (key == "root") root = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                continue; // properties before the first section are only meaningful for "root"
            }
            // values are case-insensitive by spec; normalise (the unset keyword too). Charset keeps its lower-case form.
            props!.Add(new(key, value.ToLowerInvariant()));
        }
        Flush();
        return new EditorConfigFile { IsRoot = root, Sections = sections };
    }

    // ---- glob -> regex -----------------------------------------------------------------------------------------------------------

    private static GlobRegex? TryCompile(string glob)
    {
        try
        {
            var anchored = HasSlashOutsideBrackets(glob);
            var g = glob;
            if (g.StartsWith('/')) g = g[1..];
            var sb = new StringBuilder("^");
            if (!anchored) sb.Append("(?:.*/)?"); // a glob without a slash may match at any depth below the file's directory
            Convert(g, sb);
            sb.Append('$');
            return GlobRegex.Compile(sb.ToString()); // T38: linear, and no ~150 KB NonBacktracking cache per section (see Core Glob)
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or RegexParseException)
        {
            return null;
        }
    }

    private static bool HasSlashOutsideBrackets(string glob)
    {
        var inBracket = false;
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '\\') { i++; continue; }
            if (c == '[') inBracket = true;
            else if (c == ']') inBracket = false;
            else if (c == '/' && !inBracket) return true;
        }
        return false;
    }

    private static void Convert(string glob, StringBuilder sb)
    {
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '\\':
                    if (i + 1 < glob.Length) sb.Append(Regex.Escape(glob[++i].ToString())); else sb.Append(@"\\");
                    break;
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '[':
                {
                    var close = FindClassEnd(glob, i);
                    if (close < 0) { sb.Append(@"\["); break; }
                    var body = glob[(i + 1)..close];
                    var negate = body.StartsWith('!');
                    if (negate) body = body[1..];
                    body = body.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("[", @"\[", StringComparison.Ordinal).Replace("^", @"\^", StringComparison.Ordinal);
                    sb.Append(negate ? "[^/" : "[").Append(body).Append(']');
                    i = close;
                    break;
                }
                case '{':
                {
                    var close = FindBraceEnd(glob, i);
                    if (close < 0) { sb.Append(@"\{"); break; }
                    var inner = glob[(i + 1)..close];
                    AppendBraces(inner, sb);
                    i = close;
                    break;
                }
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
    }

    private static int FindClassEnd(string glob, int open)
    {
        for (var j = open + 1; j < glob.Length; j++)
        {
            if (glob[j] == '\\') { j++; continue; }
            if (glob[j] == ']' && j > open + 1) return j;
        }
        return -1;
    }

    private static int FindBraceEnd(string glob, int open)
    {
        var depth = 0;
        for (var j = open; j < glob.Length; j++)
        {
            if (glob[j] == '\\') { j++; continue; }
            if (glob[j] == '{') depth++;
            else if (glob[j] == '}' && --depth == 0) return j;
        }
        return -1;
    }

    private static void AppendBraces(string inner, StringBuilder sb)
    {
        var parts = SplitTopLevel(inner);
        if (parts.Count == 1)
        {
            // {num1..num2}: a numeric range
            var range = Regex.Match(inner, @"^([+-]?\d+)\.\.([+-]?\d+)$");
            if (range.Success
                && int.TryParse(range.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var from)
                && int.TryParse(range.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var to))
            {
                if (from > to) (from, to) = (to, from);
                if ((long)to - from < MaxRangeAlternatives)
                {
                    sb.Append("(?:").Append(string.Join('|', Enumerable.Range(from, to - from + 1).Select(n => n.ToString(CultureInfo.InvariantCulture)))).Append(')');
                    return;
                }
            }
            // a single alternative without a comma: the braces are literal characters
            sb.Append(@"\{");
            Convert(inner, sb);
            sb.Append(@"\}");
            return;
        }

        sb.Append("(?:");
        for (var k = 0; k < parts.Count; k++)
        {
            if (k > 0) sb.Append('|');
            Convert(parts[k], sb);
        }
        sb.Append(')');
    }

    private static List<string> SplitTopLevel(string inner)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '\\') { i++; continue; }
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == ',' && depth == 0) { parts.Add(inner[start..i]); start = i + 1; }
        }
        parts.Add(inner[start..]);
        return parts;
    }

    // ---- properties -> settings --------------------------------------------------------------------------------------------------

    /// <summary>Turns the merged property bag into typed settings, applying the spec's indent_size / tab_width interplay. Invalid values are ignored.</summary>
    public static EditorConfigSettings ToSettings(Dictionary<string, string> p)
    {
        IndentStyle? style = p.GetValueOrDefault("indent_style") switch { "tab" => IndentStyle.Tab, "space" => IndentStyle.Space, _ => null };
        EndOfLineStyle? eol = p.GetValueOrDefault("end_of_line") switch { "lf" => EndOfLineStyle.Lf, "crlf" => EndOfLineStyle.CrLf, "cr" => EndOfLineStyle.Cr, _ => null };
        var charset = p.GetValueOrDefault("charset") is "latin1" or "utf-8" or "utf-8-bom" or "utf-16be" or "utf-16le" ? p["charset"] : null;

        int? tabWidth = PositiveInt(p.GetValueOrDefault("tab_width"));
        var indentRaw = p.GetValueOrDefault("indent_size");
        int? indentSize = null;
        var indentIsTab = indentRaw == "tab";
        if (!indentIsTab) indentSize = PositiveInt(indentRaw);

        if (indentIsTab || (indentRaw is null && style == IndentStyle.Tab)) indentSize = tabWidth; // "tab": as wide as a tab
        if (tabWidth is null && indentSize is not null) tabWidth = indentSize;                      // tab_width defaults to indent_size

        return new EditorConfigSettings(style, indentSize, tabWidth, eol, charset, Bool(p.GetValueOrDefault("trim_trailing_whitespace")), Bool(p.GetValueOrDefault("insert_final_newline")));
    }

    private static int? PositiveInt(string? s) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 && n <= 1000 ? n : null;

    private static bool? Bool(string? s) => s switch { "true" => true, "false" => false, _ => null };
}
