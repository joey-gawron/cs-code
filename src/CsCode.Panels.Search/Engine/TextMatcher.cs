using System.Text.RegularExpressions;

namespace CsCode.Panels.Search.Engine;

/// <summary>The pattern cannot be used (bad regular expression).</summary>
public sealed class SearchPatternException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Finds matches in a span of text: either one line (without its terminator) or, for multi-line queries, the whole file with every
/// line break normalised to '\n'. Stateless and thread-safe (a <see cref="Regex"/> is), shared by all workers of a session.
/// </summary>
public abstract class TextMatcher
{
    /// <summary>The next non-empty match at or after <paramref name="from"/>: its index, or -1.</summary>
    public abstract int Find(ReadOnlySpan<char> text, int from, out int length);

    /// <summary>True when matches may span line breaks, so the scanner must hand over the whole (EOL-normalised) text.</summary>
    public bool RequiresWholeText { get; protected init; }

    /// <summary>"literal", "regex-nonbacktracking" or "regex-backtracking" (diagnostics and tests).</summary>
    public abstract string Kind { get; }

    /// <summary>The regex behind a regex matcher (replace uses it to expand capture groups); null for literals.</summary>
    public virtual Regex? Regex => null;

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    public static TextMatcher Create(SearchQuery query, SearchLimits limits)
    {
        var pattern = query.Pattern;
        if (pattern.Length == 0) throw new SearchPatternException("The search pattern is empty.");
        var hasBreak = pattern.AsSpan().IndexOfAny('\n', '\r') >= 0;

        if (!query.IsRegex)
        {
            if (!hasBreak) return new LiteralMatcher(pattern, query.MatchCase, query.WholeWord, wholeText: false);
            // A literal with a line break can only match in the normalised whole text.
            return new LiteralMatcher(NormaliseBreaks(pattern), query.MatchCase, query.WholeWord, wholeText: true);
        }

        var wholeText = query.MultiLine || hasBreak || HasLineBreakEscape(pattern);
        return RegexMatcher.Create(pattern, query.MatchCase, query.WholeWord, wholeText, limits.RegexTimeout);
    }

    /// <summary>Replaces "\r\n" and lone '\r' by '\n'.</summary>
    internal static string NormaliseBreaks(string s) => s.Contains('\r') ? s.Replace("\r\n", "\n").Replace('\r', '\n') : s;

    // "\n", "\r" (and "\\n" is a literal backslash + n, which must not count): look for an odd run of backslashes before n/r.
    private static bool HasLineBreakEscape(string pattern)
    {
        for (var i = 0; i + 1 < pattern.Length; i++)
        {
            if (pattern[i] != '\\') continue;
            if (pattern[i + 1] is 'n' or 'r') return true;
            i++; // skip the escaped character
        }
        return false;
    }
}

internal sealed class LiteralMatcher : TextMatcher
{
    private readonly string _pattern;
    private readonly StringComparison _comparison;
    private readonly bool _wholeWord, _checkLeft, _checkRight;

    public LiteralMatcher(string pattern, bool matchCase, bool wholeWord, bool wholeText)
    {
        _pattern = pattern;
        _comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _wholeWord = wholeWord;
        // Like VS Code: a word boundary is only demanded on an edge that is itself a word character (so ".foo" can be searched as a word).
        _checkLeft = wholeWord && IsWordChar(pattern[0]);
        _checkRight = wholeWord && IsWordChar(pattern[^1]);
        RequiresWholeText = wholeText;
    }

    public override string Kind => "literal";

    public override int Find(ReadOnlySpan<char> text, int from, out int length)
    {
        length = _pattern.Length;
        while (from <= text.Length - _pattern.Length)
        {
            var idx = text[from..].IndexOf(_pattern, _comparison);
            if (idx < 0) return -1;
            idx += from;
            if (!_wholeWord || (!(_checkLeft && idx > 0 && IsWordChar(text[idx - 1]))
                                && !(_checkRight && idx + _pattern.Length < text.Length && IsWordChar(text[idx + _pattern.Length]))))
                return idx;
            from = idx + 1;
        }
        return -1;
    }
}

internal sealed class RegexMatcher : TextMatcher
{
    private readonly Regex _regex;
    private readonly bool _nonBacktracking;

    private RegexMatcher(Regex regex, bool nonBacktracking, bool wholeText)
    {
        _regex = regex;
        _nonBacktracking = nonBacktracking;
        RequiresWholeText = wholeText;
    }

    public override string Kind => _nonBacktracking ? "regex-nonbacktracking" : "regex-backtracking";
    public override Regex? Regex => _regex;

    public static RegexMatcher Create(string pattern, bool matchCase, bool wholeWord, bool wholeText, TimeSpan timeout)
    {
        var source = wholeWord ? @"\b(?:" + pattern + @")\b" : pattern;
        var options = RegexOptions.CultureInvariant;
        if (!matchCase) options |= RegexOptions.IgnoreCase;
        if (wholeText) options |= RegexOptions.Multiline;
        try
        {
            // Linear-time engine: a pathological pattern such as (a+)+$ cannot hang a worker.
            return new RegexMatcher(new Regex(source, options | RegexOptions.NonBacktracking, timeout), true, wholeText);
        }
        catch (NotSupportedException)
        {
            // Lookarounds, backreferences, atomic groups... need the backtracking engine; the match timeout is the safety net.
            try { return new RegexMatcher(new Regex(source, options, timeout), false, wholeText); }
            catch (ArgumentException ex) { throw new SearchPatternException(ex.Message, ex); }
        }
        catch (ArgumentException ex)
        {
            throw new SearchPatternException(ex.Message, ex);
        }
    }

    public override int Find(ReadOnlySpan<char> text, int from, out int length)
    {
        length = 0;
        if (from > text.Length) return -1;
        // EnumerateMatches is allocation free; empty matches (a*, ^, $) are not results.
        foreach (var m in _regex.EnumerateMatches(text, from))
        {
            if (m.Length == 0) continue;
            length = m.Length;
            return m.Index;
        }
        return -1;
    }
}
