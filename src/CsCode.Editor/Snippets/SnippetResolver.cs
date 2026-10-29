using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CsCode.Editor.Snippets;

/// <summary>Everything a snippet's variables can ask about the document and environment. Injectable so resolution is deterministic in tests.</summary>
public sealed class SnippetVariableContext
{
    public string? FilePath { get; init; }
    public string? WorkspaceName { get; init; }
    public string? WorkspaceFolder { get; init; }
    public string SelectedText { get; init; } = string.Empty;
    public string CurrentLine { get; init; } = string.Empty;
    public int LineIndex { get; init; }
    public string CurrentWord { get; init; } = string.Empty;
    public Func<string?>? Clipboard { get; init; }
    public string? LineComment { get; init; }
    public string? BlockCommentStart { get; init; }
    public string? BlockCommentEnd { get; init; }
    public int CursorIndex { get; init; }
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
    public Random Random { get; init; } = Random.Shared;
    public Func<Guid> NewGuid { get; init; } = Guid.NewGuid;

    private static readonly HashSet<string> Known =
    [
        "TM_SELECTED_TEXT", "TM_CURRENT_LINE", "TM_CURRENT_WORD", "TM_LINE_INDEX", "TM_LINE_NUMBER", "TM_FILENAME", "TM_FILENAME_BASE",
        "TM_DIRECTORY", "TM_FILEPATH", "RELATIVE_FILEPATH", "CLIPBOARD", "WORKSPACE_NAME", "WORKSPACE_FOLDER", "CURSOR_INDEX", "CURSOR_NUMBER",
        "CURRENT_YEAR", "CURRENT_YEAR_SHORT", "CURRENT_MONTH", "CURRENT_MONTH_NAME", "CURRENT_MONTH_NAME_SHORT", "CURRENT_DATE",
        "CURRENT_DAY_NAME", "CURRENT_DAY_NAME_SHORT", "CURRENT_HOUR", "CURRENT_MINUTE", "CURRENT_SECOND", "CURRENT_SECONDS_UNIX",
        "CURRENT_TIMEZONE_OFFSET", "RANDOM", "RANDOM_HEX", "UUID", "BLOCK_COMMENT_START", "BLOCK_COMMENT_END", "LINE_COMMENT",
    ];

    /// <summary>True for the built-in variable names. An unknown name is turned into a placeholder showing the name; a known one with no value is empty.</summary>
    public static bool IsKnown(string name) => Known.Contains(name);

    /// <summary>The value of a built-in variable, or null if it has none right now (unknown name, or e.g. no file path). An empty string is a valid value.</summary>
    public string? Resolve(string name)
    {
        var culture = CultureInfo.InvariantCulture;
        switch (name)
        {
            case "TM_SELECTED_TEXT": return SelectedText;
            case "TM_CURRENT_LINE": return CurrentLine;
            case "TM_CURRENT_WORD": return CurrentWord;
            case "TM_LINE_INDEX": return LineIndex.ToString(culture);
            case "TM_LINE_NUMBER": return (LineIndex + 1).ToString(culture);
            case "TM_FILENAME": return FilePath is null ? null : Path.GetFileName(FilePath);
            case "TM_FILENAME_BASE": return FilePath is null ? null : Path.GetFileNameWithoutExtension(FilePath);
            case "TM_DIRECTORY": return FilePath is null ? null : Path.GetDirectoryName(FilePath) is { Length: > 0 } d ? d : ".";
            case "TM_FILEPATH": return FilePath;
            case "RELATIVE_FILEPATH":
                return FilePath is null ? null : WorkspaceFolder is null ? FilePath : Path.GetRelativePath(WorkspaceFolder, FilePath).Replace('\\', '/');
            case "CLIPBOARD": return Clipboard?.Invoke() ?? string.Empty;
            case "WORKSPACE_NAME": return WorkspaceName;
            case "WORKSPACE_FOLDER": return WorkspaceFolder;
            case "CURSOR_INDEX": return CursorIndex.ToString(culture);
            case "CURSOR_NUMBER": return (CursorIndex + 1).ToString(culture);
            case "CURRENT_YEAR": return Now.Year.ToString("D4", culture);
            case "CURRENT_YEAR_SHORT": return (Now.Year % 100).ToString("D2", culture);
            case "CURRENT_MONTH": return Now.Month.ToString("D2", culture);
            case "CURRENT_MONTH_NAME": return Now.ToString("MMMM", culture);
            case "CURRENT_MONTH_NAME_SHORT": return Now.ToString("MMM", culture);
            case "CURRENT_DATE": return Now.Day.ToString("D2", culture);
            case "CURRENT_DAY_NAME": return Now.ToString("dddd", culture);
            case "CURRENT_DAY_NAME_SHORT": return Now.ToString("ddd", culture);
            case "CURRENT_HOUR": return Now.Hour.ToString("D2", culture);
            case "CURRENT_MINUTE": return Now.Minute.ToString("D2", culture);
            case "CURRENT_SECOND": return Now.Second.ToString("D2", culture);
            case "CURRENT_SECONDS_UNIX": return Now.ToUnixTimeSeconds().ToString(culture);
            case "CURRENT_TIMEZONE_OFFSET":
            {
                var o = Now.Offset;
                return $"{(o < TimeSpan.Zero ? '-' : '+')}{Math.Abs(o.Hours):D2}:{Math.Abs(o.Minutes):D2}";
            }
            case "RANDOM": return Random.Next(0, 1_000_000).ToString("D6", culture);
            case "RANDOM_HEX": return Random.Next(0, 0x1000000).ToString("x6", culture);
            case "UUID": return NewGuid().ToString("D", culture);
            case "BLOCK_COMMENT_START": return BlockCommentStart ?? string.Empty;
            case "BLOCK_COMMENT_END": return BlockCommentEnd ?? string.Empty;
            case "LINE_COMMENT": return LineComment ?? string.Empty;
            default: return null;
        }
    }
}

/// <summary>How inserted text is re-indented: lines after the first get <see cref="BaseIndent"/> plus the snippet's own indentation converted to the editor's style.</summary>
public sealed record SnippetIndentation(string BaseIndent = "", int TabSize = 4, bool InsertSpaces = true, string Eol = "\n");

/// <summary>One occurrence of a tab stop inside the resolved text. <see cref="IsPrimary"/> is the one the caret visits; the rest mirror it.</summary>
public sealed record SnippetRange(int Start, int Length, bool IsPrimary, SnippetTransform? Transform);

public sealed record SnippetGroup(int Index, IReadOnlyList<SnippetRange> Ranges, IReadOnlyList<string>? Choices);

/// <summary>The text to insert and where its tab stops are (offsets relative to the start of the inserted text). Groups are in visiting order: 1, 2, 3 … then 0.</summary>
public sealed record ResolvedSnippet(string Text, IReadOnlyList<SnippetGroup> Groups)
{
    public bool HasTabstops => Groups.Count > 0;
}

public static class SnippetResolver
{
    public static ResolvedSnippet Resolve(string template, SnippetVariableContext? variables = null, SnippetIndentation? indentation = null) =>
        Resolve(SnippetParser.Parse(template), variables ?? new SnippetVariableContext(), indentation ?? new SnippetIndentation());

    public static ResolvedSnippet Resolve(ParsedSnippet snippet, SnippetVariableContext variables, SnippetIndentation indentation)
    {
        var nodes = AssignIndicesToUnknownVariables(snippet.Nodes, variables);
        var defaults = new Dictionary<int, string>();
        CollectDefaults(nodes, defaults, variables, indentation);

        var emitter = new Emitter(variables, indentation, defaults);
        emitter.Emit(nodes);

        var groups = new List<SnippetGroup>();
        foreach (var (index, ranges) in emitter.Groups.OrderBy(g => g.Key == 0 ? int.MaxValue : g.Key))
        {
            // The primary is the first occurrence that carries a default (placeholder/choice); otherwise simply the first one.
            var primary = ranges.FindIndex(r => r.Defining);
            if (primary < 0) primary = 0;
            groups.Add(new SnippetGroup(
                index,
                ranges.Select((r, i) => new SnippetRange(r.Start, r.Length, i == primary, r.Transform)).ToList(),
                emitter.Choices.GetValueOrDefault(index)));
        }

        // A snippet with tab stops but no $0 gets an implicit final stop at the end of the inserted text.
        if (groups.Count > 0 && groups.All(g => g.Index != 0))
            groups.Add(new SnippetGroup(0, [new SnippetRange(emitter.Text.Length, 0, true, null)], null));
        return new ResolvedSnippet(emitter.Text.ToString(), groups);
    }

    // ---- unknown variables become placeholders (VS Code): ${foo} -> ${N:foo} with a fresh index ----

    private static IReadOnlyList<SnippetNode> AssignIndicesToUnknownVariables(IReadOnlyList<SnippetNode> nodes, SnippetVariableContext ctx)
    {
        var max = MaxIndex(nodes);
        return Rewrite(nodes, ref max);

        List<SnippetNode> Rewrite(IReadOnlyList<SnippetNode> list, ref int next)
        {
            var result = new List<SnippetNode>(list.Count);
            foreach (var n in list)
            {
                switch (n)
                {
                    case PlaceholderNode p:
                        result.Add(p with { Children = Rewrite(p.Children, ref next) });
                        break;
                    case VariableNode v when v.Default is not null:
                        result.Add(v with { Default = Rewrite(v.Default, ref next) });
                        break;
                    case VariableNode v when !SnippetVariableContext.IsKnown(v.Name) && v.Transform is null:
                        result.Add(new PlaceholderNode(++next, [new TextNode(v.Name)]));
                        break;
                    default:
                        result.Add(n);
                        break;
                }
            }
            return result;
        }
    }

    private static int MaxIndex(IReadOnlyList<SnippetNode> nodes)
    {
        var max = 0;
        foreach (var n in nodes)
            max = Math.Max(max, n switch
            {
                TabstopNode t => t.Index,
                ChoiceNode c => c.Index,
                PlaceholderNode p => Math.Max(p.Index, MaxIndex(p.Children)),
                VariableNode { Default: not null } v => MaxIndex(v.Default),
                _ => 0,
            });
        return max;
    }

    /// <summary>The text a tab stop shows when it is a mirror (or visited before its defining placeholder is emitted): the first placeholder/choice with that index.</summary>
    private static void CollectDefaults(IReadOnlyList<SnippetNode> nodes, Dictionary<int, string> defaults, SnippetVariableContext ctx, SnippetIndentation ind)
    {
        foreach (var n in nodes)
        {
            switch (n)
            {
                case PlaceholderNode p:
                    if (!defaults.ContainsKey(p.Index))
                    {
                        var sub = new Emitter(ctx, ind, defaults, measuring: true);
                        sub.Emit(p.Children);
                        defaults[p.Index] = sub.Text.ToString();
                    }
                    CollectDefaults(p.Children, defaults, ctx, ind);
                    break;
                case ChoiceNode c:
                    defaults.TryAdd(c.Index, c.Options.Count > 0 ? c.Options[0] : string.Empty);
                    break;
                case VariableNode { Default: not null } v:
                    CollectDefaults(v.Default, defaults, ctx, ind);
                    break;
            }
        }
    }

    private sealed class Emitter(SnippetVariableContext ctx, SnippetIndentation ind, Dictionary<int, string> defaults, bool measuring = false)
    {
        public StringBuilder Text { get; } = new();
        public Dictionary<int, List<(int Start, int Length, bool Defining, SnippetTransform? Transform)>> Groups { get; } = new();
        public Dictionary<int, IReadOnlyList<string>> Choices { get; } = new();

        public void Emit(IReadOnlyList<SnippetNode> nodes)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case TextNode t:
                        Append(t.Text);
                        break;
                    case TabstopNode ts:
                    {
                        var shown = defaults.GetValueOrDefault(ts.Index, string.Empty);
                        if (ts.Transform is not null) shown = SnippetTransforms.Apply(shown, ts.Transform);
                        var start = Text.Length;
                        Append(shown);
                        Record(ts.Index, start, Text.Length - start, defining: false, ts.Transform);
                        break;
                    }
                    case PlaceholderNode p:
                    {
                        var start = Text.Length;
                        Emit(p.Children);
                        Record(p.Index, start, Text.Length - start, defining: true, null);
                        break;
                    }
                    case ChoiceNode c:
                    {
                        var start = Text.Length;
                        Append(c.Options.Count > 0 ? c.Options[0] : string.Empty);
                        Record(c.Index, start, Text.Length - start, defining: true, null);
                        if (!measuring) Choices.TryAdd(c.Index, c.Options);
                        break;
                    }
                    case VariableNode v:
                        EmitVariable(v);
                        break;
                }
            }
        }

        private void EmitVariable(VariableNode v)
        {
            var value = ctx.Resolve(v.Name);
            if (v.Transform is not null) value = SnippetTransforms.Apply(value ?? string.Empty, v.Transform);
            if (!string.IsNullOrEmpty(value))
            {
                Append(value);
                return;
            }
            if (v.Default is not null) Emit(v.Default); // known-but-empty (or transform result empty) uses the default
        }

        private void Record(int index, int start, int length, bool defining, SnippetTransform? transform)
        {
            if (measuring) return;
            if (!Groups.TryGetValue(index, out var list)) Groups[index] = list = [];
            list.Add((start, length, defining, transform));
        }

        /// <summary>Appends text, re-indenting every line after the first and normalising line endings to the document's.</summary>
        private void Append(string text)
        {
            if (text.Length == 0) return;
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c is '\r' or '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    i++;
                    Text.Append(ind.Eol);
                    // the snippet's own leading whitespace on the next line, converted to the editor's indent style, after the base indent
                    var wsStart = i;
                    while (i < text.Length && text[i] is ' ' or '\t') i++;
                    Text.Append(ind.BaseIndent).Append(ConvertIndent(text.AsSpan(wsStart, i - wsStart)));
                    continue;
                }
                Text.Append(c);
                i++;
            }
        }

        private string ConvertIndent(ReadOnlySpan<char> whitespace)
        {
            if (whitespace.IsEmpty) return string.Empty;
            var tabSize = Math.Max(1, ind.TabSize);
            var width = 0;
            foreach (var ch in whitespace) width += ch == '\t' ? tabSize - (width % tabSize) : 1;
            return ind.InsertSpaces ? new string(' ', width) : new string('\t', width / tabSize) + new string(' ', width % tabSize);
        }
    }
}

/// <summary>Applies <c>${1/regex/format/flags}</c> transforms (JS regex flavour mapped onto .NET).</summary>
public static class SnippetTransforms
{
    public static string Apply(string input, SnippetTransform t)
    {
        Regex rx;
        try
        {
            var options = RegexOptions.CultureInvariant;
            if (t.Flags.Contains('i')) options |= RegexOptions.IgnoreCase;
            if (t.Flags.Contains('m')) options |= RegexOptions.Multiline;
            if (t.Flags.Contains('s')) options |= RegexOptions.Singleline;
            rx = new Regex(t.Pattern, options, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return input; // an invalid pattern leaves the text as it is
        }
        try
        {
            var global = t.Flags.Contains('g');
            return global ? rx.Replace(input, m => Format(m, t.Format)) : rx.Replace(input, m => Format(m, t.Format), 1);
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }

    private static string Format(Match m, IReadOnlyList<FormatNode> format)
    {
        var sb = new StringBuilder();
        foreach (var node in format)
        {
            if (node is FormatText t)
            {
                sb.Append(t.Text);
                continue;
            }
            var g = (FormatGroup)node;
            var value = g.Group < m.Groups.Count && m.Groups[g.Group].Success ? m.Groups[g.Group].Value : string.Empty;
            sb.Append(g.Kind switch
            {
                FormatKind.Plain => value,
                FormatKind.Upcase => value.ToUpperInvariant(),
                FormatKind.Downcase => value.ToLowerInvariant(),
                FormatKind.Capitalize => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..],
                FormatKind.CamelCase => Words(value, firstUpper: false),
                FormatKind.PascalCase => Words(value, firstUpper: true),
                FormatKind.If => value.Length > 0 ? g.IfText : string.Empty,
                FormatKind.Else => value.Length > 0 ? value : g.ElseText,
                FormatKind.IfElse => value.Length > 0 ? g.IfText : g.ElseText,
                _ => value,
            });
        }
        return sb.ToString();
    }

    /// <summary>VS Code's rule: split on runs of non letters/digits, upper-case each word's first letter (the first word's only for PascalCase).</summary>
    private static string Words(string value, bool firstUpper)
    {
        var words = Regex.Matches(value, "[a-zA-Z0-9]+").Select(w => w.Value).ToList();
        if (words.Count == 0) return value;
        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            sb.Append(i == 0 && !firstUpper ? char.ToLowerInvariant(w[0]) : char.ToUpperInvariant(w[0])).Append(w, 1, w.Length - 1);
        }
        return sb.ToString();
    }
}
