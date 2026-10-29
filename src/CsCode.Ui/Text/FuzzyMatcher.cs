namespace CsCode.Ui.Text;

/// <summary>
/// Subsequence fuzzy matcher (fzf-style two-pass: tightest forward/backward window, then bonus scoring) used by the palette,
/// quick picks and filter boxes. Case-insensitive. <see cref="Score"/> allocates nothing, so ranking 200k paths off the UI thread
/// stays cheap; <see cref="Positions"/> is only called for the few rows that are actually displayed (highlight spans).
/// </summary>
public static class FuzzyMatcher
{
    public const int NoMatch = -1;

    /// <summary>Score &gt;= 0 when <paramref name="pattern"/> is a case-insensitive subsequence of <paramref name="text"/>, else <see cref="NoMatch"/>.</summary>
    public static int Score(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text) => Run(pattern, text, default);

    /// <summary>Indices (ascending) of the matched characters in <paramref name="text"/>; empty if no match.</summary>
    public static int[] Positions(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        if (pattern.IsEmpty) return [];
        var buffer = new int[pattern.Length];
        return Run(pattern, text, buffer) < 0 ? [] : buffer;
    }

    private static int Run(ReadOnlySpan<char> p, ReadOnlySpan<char> t, Span<int> positions)
    {
        if (p.IsEmpty) return 0;
        if (p.Length > t.Length) return NoMatch;

        // Forward pass: earliest end of a subsequence match.
        var pi = 0;
        var end = -1;
        for (var ti = 0; ti < t.Length; ti++)
        {
            if (Eq(t[ti], p[pi]) && ++pi == p.Length) { end = ti; break; }
        }
        if (end < 0) return NoMatch;

        // Backward pass: tighten the window so the match is as compact as possible.
        pi = p.Length - 1;
        var start = end;
        for (var ti = end; ti >= 0; ti--)
        {
            if (Eq(t[ti], p[pi]))
            {
                if (pi == 0) { start = ti; break; }
                pi--;
            }
        }

        // Score the window, re-walking forward to record positions and bonuses.
        var score = 0;
        var prevMatched = -2;
        pi = 0;
        var lastSep = LastSeparator(t);
        var inName = 0;
        for (var ti = start; ti <= end && pi < p.Length; ti++)
        {
            if (!Eq(t[ti], p[pi])) continue;
            score += 16;
            if (ti == prevMatched + 1) score += 6; // consecutive run
            else if (prevMatched >= 0) score -= Math.Min(6, 3 + (ti - prevMatched - 2)); // gap penalty (capped)
            if (ti == 0) score += 10;
            else if (IsBoundary(t[ti - 1])) score += 9;
            else if (char.IsUpper(t[ti]) && char.IsLower(t[ti - 1])) score += 7; // camelCase hump
            if (ti > lastSep) inName++;
            if (p[pi] == t[ti]) score += 1; // exact-case bonus
            if (positions.Length > pi) positions[pi] = ti;
            prevMatched = ti;
            pi++;
        }
        score += inName * 2;                  // matches inside the file name outweigh directory matches
        score -= Math.Min(20, start / 2);     // late starts rank lower
        score -= Math.Min(20, t.Length / 8);  // shorter candidates rank higher
        return Math.Max(0, score);
    }

    private static bool Eq(char a, char b) => a == b || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

    private static bool IsBoundary(char c) => c is '/' or '\\' or '.' or '_' or '-' or ' ' or ':' or '(' or '<';

    private static int LastSeparator(ReadOnlySpan<char> t)
    {
        for (var i = t.Length - 1; i >= 0; i--)
            if (t[i] is '/' or '\\') return i;
        return -1;
    }
}
