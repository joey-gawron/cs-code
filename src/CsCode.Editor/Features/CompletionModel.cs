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
        var positions = new int[pattern.Length];
        return TryMatch(pattern, text, positions, out var score) ? new Result(score, positions) : null;
    }

    /// <summary>Scores without allocating: <paramref name="positions"/> (length &gt;= pattern length) receives the match positions. False when the pattern is not a subsequence.</summary>
    public static bool TryMatch(string pattern, string text, Span<int> positions, out int score)
    {
        score = 0;
        if (pattern.Length == 0) return true;
        if (pattern.Length > text.Length) return false;

        // forward: does a match exist, and where does the earliest one end?
        var pi = 0;
        var end = -1;
        for (var j = 0; j < text.Length && pi < pattern.Length; j++)
            if (Eq(pattern[pi], text[j]) && ++pi == pattern.Length) end = j;
        if (end < 0) return false;

        // backward from that end: the tightest (shortest-span) match ending there
        pi = pattern.Length - 1;
        for (var j = end; j >= 0 && pi >= 0; j--)
            if (Eq(pattern[pi], text[j])) positions[pi--] = j;

        score = Score(pattern, text, positions[..pattern.Length]);
        return true;
    }

    private static bool Eq(char p, char t) => p == t || char.ToLowerInvariant(p) == char.ToLowerInvariant(t);

    private static int Score(string pattern, string text, ReadOnlySpan<int> positions)
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

/// <summary>One provider's answer, kept exactly as the provider returned it (no per-item copy: 50k items cost one reference).</summary>
internal sealed record CompletionSource(IReadOnlyList<CompletionItem> Items, ICompletionProvider Provider, int Order);

internal static class CompletionFilter
{
    public const int MaxShown = 300;
    private const int MaxItemsPerSource = 200_000;

    private readonly record struct Candidate(CompletionItem Item, ICompletionProvider Provider, int Order, int Score);

    /// <summary>The VS Code ordering: score (descending), preselected first on ties, then <c>sortText</c>, then label, then provider order.</summary>
    private static int Compare(in Candidate a, in Candidate b)
    {
        var c = b.Score.CompareTo(a.Score);
        if (c != 0) return c;
        c = b.Item.Preselect.CompareTo(a.Item.Preselect);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Item.SortText ?? a.Item.Label, b.Item.SortText ?? b.Item.Label);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Item.Label, b.Item.Label);
        return c != 0 ? c : a.Order.CompareTo(b.Order);
    }

    /// <summary>
    /// Filters and orders suggestions for the typed prefix and keeps the best <see cref="MaxShown"/> — a bounded selection (a heap of at most
    /// MaxShown candidates), so 50k matches cost no list, no sort and no per-item allocation: only items that make the cut are materialised.
    /// With no prefix everything matches and <c>sortText</c> decides.
    /// </summary>
    /// <param name="lineBefore">The caret line's text left of the caret, with <paramref name="lineNumber"/>: lets an item whose edit starts BEFORE the
    /// word being typed (a snippet prefix like <c>#region</c> typed as <c>#reg</c>) be matched against the text from its own edit start, as VS Code does.</param>
    public static List<FilteredCompletion> Filter(IReadOnlyList<CompletionSource> sources, string prefix, CancellationToken ct = default, string? lineBefore = null, int lineNumber = -1)
    {
        var selector = new Selector(prefix, lineBefore, lineNumber);
        var seen = 0;
        foreach (var source in sources)
        {
            var items = source.Items;
            var n = Math.Min(items.Count, MaxItemsPerSource);
            for (var i = 0; i < n; i++)
            {
                if ((++seen & 2047) == 0) ct.ThrowIfCancellationRequested();
                selector.Consider(items[i], source.Provider, source.Order);
            }
        }
        ct.ThrowIfCancellationRequested();
        return selector.Finish();
    }

    /// <summary>Convenience for already-wrapped entries (tests, single provider): each entry keeps its own provider and order.</summary>
    public static List<FilteredCompletion> Filter(IReadOnlyList<CompletionEntry> entries, string prefix, CancellationToken ct = default, string? lineBefore = null, int lineNumber = -1)
    {
        var selector = new Selector(prefix, lineBefore, lineNumber);
        for (var i = 0; i < entries.Count; i++)
        {
            if ((i & 2047) == 0) ct.ThrowIfCancellationRequested();
            selector.Consider(entries[i].Item, entries[i].Provider, entries[i].Order);
        }
        return selector.Finish();
    }

    private sealed class Selector(string prefix, string? lineBefore, int lineNumber)
    {
        // root = the WORST kept candidate, so a better newcomer replaces it
        private readonly PriorityQueue<Candidate, Candidate> _heap = new(Comparer<Candidate>.Create((a, b) => Compare(b, a)));
        private readonly int[] _scratch = new int[Math.Max(16, prefix.Length + (lineBefore?.Length ?? 0))];

        public void Consider(CompletionItem item, ICompletionProvider provider, int order)
        {
            var text = item.FilterText ?? item.Label;
            var itemPrefix = prefix;
            if (lineBefore is not null && item.TextEdit is { } te && te.Range.Start.Line == lineNumber && te.Range.Start.Character >= 0 &&
                te.Range.Start.Character < lineBefore.Length - prefix.Length)
                itemPrefix = lineBefore[te.Range.Start.Character..]; // the typed text this item will replace, including leading non-word characters
            var score = 0;
            if (itemPrefix.Length > 0 && !FuzzyMatcher.TryMatch(itemPrefix, text, _scratch.Length >= itemPrefix.Length ? _scratch : new int[itemPrefix.Length], out score)) return;
            var candidate = new Candidate(item, provider, order, score);
            if (_heap.Count < MaxShown) _heap.Enqueue(candidate, candidate);
            else if (Compare(candidate, _heap.Peek()) < 0) _heap.EnqueueDequeue(candidate, candidate);
        }

        public List<FilteredCompletion> Finish()
        {
            var kept = new Candidate[_heap.Count];
            for (var i = kept.Length - 1; i >= 0; i--) kept[i] = _heap.Dequeue(); // worst first out → fill from the back
            var result = new List<FilteredCompletion>(kept.Length);
            foreach (var c in kept)
            {
                var text = c.Item.FilterText ?? c.Item.Label;
                var itemPrefix = prefix;
                if (lineBefore is not null && c.Item.TextEdit is { } te && te.Range.Start.Line == lineNumber && te.Range.Start.Character >= 0 &&
                    te.Range.Start.Character < lineBefore.Length - prefix.Length)
                    itemPrefix = lineBefore[te.Range.Start.Character..];
                var positions = itemPrefix.Length == 0 ? [] : FuzzyMatcher.Match(itemPrefix, text)?.Positions ?? [];
                result.Add(new FilteredCompletion(new CompletionEntry(c.Item, c.Provider, c.Order), c.Score, positions));
            }
            return result;
        }
    }
}
