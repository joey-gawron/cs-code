using CsCode.Core.Languages;

namespace CsCode.Editor.Features;

/// <summary>
/// Subsequence ("fuzzy") matching for the completion list: every pattern character must appear in order; the score rewards a match at the
/// start, consecutive characters, word starts (after <c>_ - . /</c> or a camelCase hump) and exact case, and penalises gaps. Allocation-light
/// and O(text length) per candidate, so filtering 50k items stays well inside a worker slice.
/// </summary>
public static class FuzzyMatcher
{
    public readonly record struct Result(int Score, int[] Positions);

    public static Result? Match(string pattern, string text)
    {
        if (pattern.Length == 0) return new Result(0, []);
        if (pattern.Length > text.Length) return null;

        // forward: does a match exist, and where does the earliest one end?
        var pi = 0;
        var end = -1;
        for (var j = 0; j < text.Length && pi < pattern.Length; j++)
            if (Eq(pattern[pi], text[j]) && ++pi == pattern.Length) end = j;
        if (end < 0) return null;

        // backward from that end: the tightest (shortest-span) match ending there
        var positions = new int[pattern.Length];
        pi = pattern.Length - 1;
        for (var j = end; j >= 0 && pi >= 0; j--)
            if (Eq(pattern[pi], text[j])) positions[pi--] = j;

        return new Result(Score(pattern, text, positions), positions);
    }

    private static bool Eq(char p, char t) => p == t || char.ToLowerInvariant(p) == char.ToLowerInvariant(t);

    private static int Score(string pattern, string text, int[] positions)
    {
        var score = 0;
        for (var i = 0; i < positions.Length; i++)
        {
            var j = positions[i];
            score += 1;
            if (pattern[i] == text[j]) score += 1; // exact case
            if (j == 0) score += 12;
            else if (IsWordStart(text, j)) score += 8;
            if (i > 0 && positions[i - 1] == j - 1) score += 6; // consecutive
            else if (i > 0) score -= Math.Min(4, j - positions[i - 1] - 1); // gap
        }
        if (positions[0] == 0 && positions[^1] == positions.Length - 1) score += 20; // a prefix match
        score -= Math.Min(10, (text.Length - pattern.Length) / 8); // mild preference for shorter labels
        return Math.Max(1, score);
    }

    private static bool IsWordStart(string text, int j)
    {
        var prev = text[j - 1];
        if (prev is '_' or '-' or '.' or '/' or ' ' or ':') return true;
        return char.IsLower(prev) && char.IsUpper(text[j]); // camelCase hump
    }
}

/// <summary>One suggestion with the provider that produced it (needed to resolve it later) and, when filtered, its score and match positions.</summary>
internal sealed record CompletionEntry(CompletionItem Item, ICompletionProvider Provider, int Order);

internal sealed record FilteredCompletion(CompletionEntry Entry, int Score, int[] Positions);

internal static class CompletionFilter
{
    public const int MaxShown = 300;

    /// <summary>
    /// Filters and orders suggestions for the typed prefix: fuzzy score (descending), preselected first on ties, then <c>sortText</c>, then
    /// label — the same ordering VS Code uses. With no prefix everything is shown in <c>sortText</c> order. Capped at <see cref="MaxShown"/>.
    /// </summary>
    /// <param name="lineBefore">The caret line's text left of the caret, with <paramref name="lineNumber"/>: lets an item whose edit starts BEFORE the
    /// word being typed (a snippet prefix like <c>#region</c> typed as <c>#reg</c>) be matched against the text from its own edit start, as VS Code does.</param>
    public static List<FilteredCompletion> Filter(IReadOnlyList<CompletionEntry> entries, string prefix, CancellationToken ct = default, string? lineBefore = null, int lineNumber = -1)
    {
        var matched = new List<FilteredCompletion>();
        for (var i = 0; i < entries.Count; i++)
        {
            if ((i & 2047) == 0) ct.ThrowIfCancellationRequested();
            var e = entries[i];
            var text = e.Item.FilterText ?? e.Item.Label;
            var itemPrefix = prefix;
            if (lineBefore is not null && e.Item.TextEdit is { } te && te.Range.Start.Line == lineNumber && te.Range.Start.Character >= 0 &&
                te.Range.Start.Character < lineBefore.Length - prefix.Length)
                itemPrefix = lineBefore[te.Range.Start.Character..]; // the typed text this item will replace, including leading non-word characters
            if (itemPrefix.Length == 0)
            {
                matched.Add(new FilteredCompletion(e, 0, []));
                continue;
            }
            if (FuzzyMatcher.Match(itemPrefix, text) is { } m) matched.Add(new FilteredCompletion(e, m.Score, m.Positions));
        }
        matched.Sort((a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            c = b.Entry.Item.Preselect.CompareTo(a.Entry.Item.Preselect);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Entry.Item.SortText ?? a.Entry.Item.Label, b.Entry.Item.SortText ?? b.Entry.Item.Label);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Entry.Item.Label, b.Entry.Item.Label);
            return c != 0 ? c : a.Entry.Order.CompareTo(b.Entry.Order);
        });
        if (matched.Count > MaxShown) matched.RemoveRange(MaxShown, matched.Count - MaxShown);
        return matched;
    }
}
