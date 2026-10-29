using System.Text.RegularExpressions;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Theming;

namespace CsCode.Editor.Editing;

/// <summary>What Enter should produce: the new line's indentation plus optional extra text, and (between brackets) a second line for the closer.</summary>
/// <param name="NewLineIndent">Whitespace at the start of the new line.</param>
/// <param name="AppendText">Text placed after the indent (e.g. " * " continuing a doc comment).</param>
/// <param name="ClosingLineIndent">Non-null for "indentOutdent": an extra line with this indent is inserted after the caret line, so <c>{|}</c> becomes three lines.</param>
public sealed record EnterResult(string NewLineIndent, string AppendText = "", string? ClosingLineIndent = null);

/// <summary>Auto-indent from a language configuration: <c>onEnterRules</c>, bracket pairs, <c>indentationRules</c>, else "keep the current indentation".</summary>
public static class AutoIndent
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(20);

    /// <param name="before">The line's text left of the caret.</param>
    /// <param name="after">The line's text right of the caret.</param>
    /// <param name="previousLine">The line above (for <c>previousLineText</c>-guarded rules); null when there is none.</param>
    public static EnterResult ForEnter(string before, string after, EditingContext ctx, string? previousLine = null)
    {
        var cfg = ctx.Language;
        var baseIndent = before[..LeadingWhitespace(before)];
        var unit = ctx.IndentUnit;

        var rules = ctx.EnterRules ?? cfg.OnEnterRules.Select(r => new Languages.EnterRuleEx(r, null)).ToArray();
        foreach (var guarded in rules)
        {
            var rule = guarded.Rule;
            if (!Matches(rule.BeforeText, before)) continue;
            if (rule.AfterText is not null && !Matches(rule.AfterText, after)) continue;
            if (guarded.PreviousLineText is not null && (previousLine is null || !Matches(guarded.PreviousLineText, previousLine))) continue;
            var indent = rule.Action switch
            {
                "indent" or "indentOutdent" => baseIndent + unit,
                "outdent" => Outdent(baseIndent, ctx),
                _ => baseIndent,
            };
            if (rule.RemoveText > 0) indent = indent[..Math.Max(0, indent.Length - rule.RemoveText)];
            return new EnterResult(indent, rule.AppendText ?? "", rule.Action == "indentOutdent" ? baseIndent : null);
        }

        var trimmedBefore = before.TrimEnd();
        var trimmedAfter = after.TrimStart();
        // between a bracket pair: {|}  →  three lines
        if (trimmedBefore.Length > 0 && trimmedAfter.Length > 0 &&
            cfg.Brackets.Any(b => trimmedBefore.EndsWith(b.Open, StringComparison.Ordinal) && trimmedAfter.StartsWith(b.Close, StringComparison.Ordinal)))
            return new EnterResult(baseIndent + unit, "", baseIndent);

        if (Matches(cfg.IncreaseIndentPattern, before) || Matches(cfg.IndentNextLinePattern, before)) return new EnterResult(baseIndent + unit);
        if (trimmedBefore.Length > 0 && cfg.Brackets.Any(b => trimmedBefore.EndsWith(b.Open, StringComparison.Ordinal))) return new EnterResult(baseIndent + unit);
        return new EnterResult(baseIndent);
    }

    /// <summary>
    /// After a character was typed: when the line now matches <c>decreaseIndentPattern</c>, or consists only of a closing bracket, it is
    /// re-indented to its opener's level. Returns the edit to apply to the line's leading whitespace, or null.
    /// </summary>
    public static EditPlan? AfterTyping(ITextDocument doc, TextPosition caret, string typed, EditingContext ctx)
    {
        var cfg = ctx.Language;
        var line = doc.GetLineText(caret.Line);
        var current = line[..LeadingWhitespace(line)];
        string? target = null;

        var trimmed = line.Trim();
        if (typed.Length == 1 && trimmed == typed && cfg.Brackets.Any(b => b.Close == typed))
            target = IndentOfOpener(doc, caret, typed, cfg);
        else if (cfg.DecreaseIndentPattern is not null && Matches(cfg.DecreaseIndentPattern, line) && caret.Line > 0)
        {
            var previous = PreviousNonBlank(doc, caret.Line);
            if (previous is not null)
            {
                var prevIndent = previous[..LeadingWhitespace(previous)];
                // only pull back lines that are not already shallower than "previous - one unit"
                var wanted = Matches(cfg.IncreaseIndentPattern, previous) ? prevIndent : Outdent(prevIndent, ctx);
                if (wanted.Length < current.Length) target = wanted;
            }
        }
        if (target is null || target == current) return null;
        var edit = new TextEdit(TextRange.FromLines(caret.Line, 0, caret.Line, current.Length), target);
        var col = caret.Character - current.Length + target.Length;
        var p = new TextPosition(caret.Line, Math.Max(0, col));
        return new EditPlan([edit], new TextRange(p, p));
    }

    private static string? IndentOfOpener(ITextDocument doc, TextPosition caret, string closer, LanguageConfiguration cfg)
    {
        var open = cfg.Brackets.First(b => b.Close == closer).Open;
        var depth = 0;
        for (var l = caret.Line - 1; l >= 0 && caret.Line - l < 2000; l--)
        {
            var text = doc.GetLineText(l);
            for (var i = text.Length - 1; i >= 0; i--)
            {
                if (text[i].ToString() == closer) depth++;
                else if (text[i].ToString() == open)
                {
                    if (depth == 0) return text[..LeadingWhitespace(text)];
                    depth--;
                }
            }
        }
        return null;
    }

    private static string? PreviousNonBlank(ITextDocument doc, int line)
    {
        for (var l = line - 1; l >= 0 && line - l < 200; l--)
        {
            var t = doc.GetLineText(l);
            if (t.Trim().Length > 0) return t;
        }
        return null;
    }

    private static string Outdent(string indent, EditingContext ctx) =>
        indent.EndsWith('\t') ? indent[..^1] : indent[..Math.Max(0, indent.Length - Math.Min(ctx.TabSize, TrailingSpaces(indent)))];

    private static int TrailingSpaces(string s)
    {
        var n = 0;
        while (n < s.Length && s[^(n + 1)] == ' ') n++;
        return n;
    }

    private static int LeadingWhitespace(string s)
    {
        var i = 0;
        while (i < s.Length && s[i] is ' ' or '\t') i++;
        return i;
    }

    private static bool Matches(string? pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
        {
            return false; // a bad/slow pattern in a language configuration must never break typing
        }
    }
}

public enum AutoCloseKind { None, InsertPair, OverType, Surround }

public sealed record AutoCloseDecision(AutoCloseKind Kind, string Open = "", string Close = "")
{
    public static readonly AutoCloseDecision None = new(AutoCloseKind.None);
}

/// <summary>Auto-close / over-type / surround decisions for one typed character. Pure; the view supplies the context (text around the caret, token kind).</summary>
public static class AutoClosing
{
    public static AutoCloseDecision Decide(
        string typed,
        string lineBefore,
        string lineAfter,
        string selectedText,
        LanguageConfiguration cfg,
        string autoCloseBefore,
        TokenKind kind,
        bool nextIsAutoInserted,
        bool brackets = true,
        bool quotes = true,
        bool surround = true)
    {
        if (typed.Length == 0) return AutoCloseDecision.None;

        if (selectedText.Length > 0)
        {
            if (surround && cfg.SurroundingPairs.FirstOrDefault(p => p.Open == typed) is { Open: not null } sp && sp.Open == typed)
                return new AutoCloseDecision(AutoCloseKind.Surround, sp.Open, sp.Close);
            return AutoCloseDecision.None; // typing replaces the selection
        }

        // typing the closer that sits right after the caret and was inserted by us: step over it
        if (nextIsAutoInserted && lineAfter.StartsWith(typed, StringComparison.Ordinal) && cfg.AutoClosingPairs.Any(p => p.Close == typed))
            return new AutoCloseDecision(AutoCloseKind.OverType, "", typed);

        var typedContext = lineBefore + typed;
        foreach (var pair in cfg.AutoClosingPairs.OrderByDescending(p => p.Open.Length))
        {
            if (!typedContext.EndsWith(pair.Open, StringComparison.Ordinal)) continue;
            var isQuote = pair.Open.Length > 0 && pair.Open[^1] is '"' or '\'' or '`' && pair.Open[^1].ToString() == pair.Close;
            if (isQuote ? !quotes : !brackets) continue;
            if (pair.NotIn is { } notIn && ((kind == TokenKind.String && notIn.Contains("string")) || (kind == TokenKind.Comment && notIn.Contains("comment")) || (kind == TokenKind.Regex && notIn.Contains("regex")))) continue;
            // only close when what follows is space/end of line/one of the "auto close before" characters
            if (lineAfter.Length > 0 && !char.IsWhiteSpace(lineAfter[0]) && !autoCloseBefore.Contains(lineAfter[0])) continue;
            // a lone quote right after a word character is an apostrophe / the end of a string, not an opener (don't → don't'')
            if (isQuote && pair.Open.Length == 1 && lineBefore.Length > 0 && (char.IsLetterOrDigit(lineBefore[^1]) || lineBefore[^1] == '_')) continue;
            // for multi-char openers (/** → */) only the missing suffix of the opener is typed now
            return new AutoCloseDecision(AutoCloseKind.InsertPair, pair.Open, pair.Close);
        }
        return AutoCloseDecision.None;
    }
}

/// <summary>Expand Selection without a language server: word → trimmed line → line → enclosing bracket contents → brackets included → … → document.</summary>
public static class SelectionExpander
{
    public static TextRange? Expand(ITextDocument doc, TextRange current, LanguageConfiguration cfg)
    {
        var text = doc.GetText();
        var s = doc.OffsetAt(current.Start);
        var e = doc.OffsetAt(current.End);

        var candidates = new List<(int Start, int End)>();
        if (s == e && WordAt(text, s) is { } w) candidates.Add(w);
        var line = doc.GetLineText(current.Start.Line);
        var lineStart = doc.OffsetAt(new TextPosition(current.Start.Line, 0));
        var content = line.Trim();
        if (content.Length > 0)
        {
            var from = lineStart + line.IndexOf(content[0]);
            candidates.Add((from, from + content.Length));
        }
        candidates.Add((lineStart, lineStart + line.Length));
        candidates.AddRange(EnclosingBrackets(text, s, e, cfg));
        candidates.Add((0, text.Length));

        (int Start, int End)? best = null;
        foreach (var c in candidates)
        {
            if (c.Start > s || c.End < e) continue;           // must contain the current range
            if (c.Start == s && c.End == e) continue;          // and be strictly larger
            if (best is null || (c.End - c.Start) < (best.Value.End - best.Value.Start)) best = c;
        }
        return best is { } b ? new TextRange(doc.PositionAt(b.Start), doc.PositionAt(b.End)) : null;
    }

    private static (int, int)? WordAt(string text, int offset)
    {
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
        var a = offset;
        var b = offset;
        while (a > 0 && IsWord(text[a - 1])) a--;
        while (b < text.Length && IsWord(text[b])) b++;
        return b > a ? (a, b) : null;
    }

    /// <summary>For each enclosing bracket pair of [s,e]: the contents (exclusive) and the pair including its brackets.</summary>
    private static IEnumerable<(int, int)> EnclosingBrackets(string text, int s, int e, LanguageConfiguration cfg)
    {
        foreach (var (open, close) in cfg.Brackets.Where(b => b.Open.Length == 1 && b.Close.Length == 1))
        {
            var depth = 0;
            for (var i = s - 1; i >= 0 && s - i < 200_000; i--)
            {
                if (text[i] == close[0]) depth++;
                else if (text[i] == open[0])
                {
                    if (depth > 0)
                    {
                        depth--;
                        continue;
                    }
                    // found an opener enclosing the start: find its closer after e
                    var d = 0;
                    for (var j = i + 1; j < text.Length && j - i < 200_000; j++)
                    {
                        if (text[j] == open[0]) d++;
                        else if (text[j] == close[0])
                        {
                            if (d == 0)
                            {
                                if (j >= e)
                                {
                                    yield return (i + 1, j);
                                    yield return (i, j + 1);
                                }
                                break;
                            }
                            d--;
                        }
                    }
                    depth = 0;
                    // keep climbing: outer pairs of the same kind
                }
            }
        }
    }
}
