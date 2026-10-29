using System.Buffers;

namespace CsCode.Platform.Search;

/// <summary>A successful fuzzy match: the score (higher is better) and the matched text indices, ascending, one per pattern character.</summary>
public readonly record struct FuzzyMatch(int Score, int[] Indices);

public readonly record struct FuzzyResult<T>(T Item, int Score, int SourceIndex);

/// <summary>
/// Quick-open style fuzzy matching: the pattern's characters must appear in order (case-insensitively); the score rewards what makes a
/// match look intentional — characters that follow each other (consecutive run), start a word (after space, <c>_ - .</c>), start a
/// camelCase hump or a digit run, follow a path separator, begin the text, and match the pattern's exact case — and penalises gaps.
/// The scoring is a dynamic program (best alignment, not first-found), the same idea as fzf's V2.
/// <para>
/// <see cref="Score(string, string)"/> is the ranking path: allocation-free (stack/pooled buffers), with a cheap subsequence pre-check and
/// the DP restricted to the window where a match is possible, so ranking 100k file paths costs a few tens of milliseconds.
/// <see cref="Match(string, string)"/> additionally recovers the matched indices (for highlighting) and should be called only for the rows shown.
/// </para>
/// </summary>
public static class FuzzyMatcher
{
    public const int NoMatch = -1;

    // Scoring constants (fzf's, so the behaviour is familiar): a match is worth 16; a gap costs 3 to open and 1 per extra character.
    private const int ScoreMatch = 16;
    private const int ScoreGapStart = -3;
    private const int ScoreGapExtension = -1;
    private const int BonusBoundary = ScoreMatch / 2;            // 8: after a non-word character
    private const int BonusBoundaryWhite = BonusBoundary + 2;    // 10: after whitespace / at the start of the text
    private const int BonusBoundaryPath = BonusBoundary + 1;     // 9: right after '/' or '\'
    private const int BonusCamel = BonusBoundary + ScoreGapExtension; // 7: lower→Upper, letter→digit
    private const int BonusConsecutive = 4;
    private const int FirstCharMultiplier = 2;
    private const int BonusExactCase = 1;
    private const int NameBonus = 24;                             // extra for a pattern that matches inside the file NAME (quick open)
    private const int Impossible = int.MinValue / 4;
    private const int MaxText = 1024;                             // longer texts are matched on their last MaxText characters
    private const int MaxPattern = 128;

    private enum CharClass : byte { White, NonWord, Delimiter, Lower, Upper, Number, Letter }

    private static readonly CharClass[] AsciiClasses = BuildAsciiClasses();

    private static CharClass[] BuildAsciiClasses()
    {
        var table = new CharClass[128];
        for (var c = 0; c < 128; c++) table[c] = ClassOfSlow((char)c);
        return table;
    }

    private static CharClass ClassOf(char c) => c < 128 ? AsciiClasses[c] : ClassOfSlow(c);

    private static CharClass ClassOfSlow(char c) =>
        char.IsLower(c) ? CharClass.Lower
        : char.IsUpper(c) ? CharClass.Upper
        : char.IsDigit(c) ? CharClass.Number
        : char.IsLetter(c) ? CharClass.Letter
        : char.IsWhiteSpace(c) ? CharClass.White
        : c is '/' or '\\' ? CharClass.Delimiter
        : CharClass.NonWord;

    private static readonly byte[] BonusTable = BuildBonusTable();

    private static byte[] BuildBonusTable()
    {
        var table = new byte[7 * 7];
        for (var prev = 0; prev < 7; prev++)
            for (var cur = 0; cur < 7; cur++)
                table[prev * 7 + cur] = (byte)BonusForSlow((CharClass)prev, (CharClass)cur);
        return table;
    }

    private static int BonusFor(CharClass prev, CharClass cur) => BonusTable[(int)prev * 7 + (int)cur];

    private static int BonusForSlow(CharClass prev, CharClass cur)
    {
        if (cur >= CharClass.Lower)
        {
            switch (prev)
            {
                case CharClass.White: return BonusBoundaryWhite;
                case CharClass.Delimiter: return BonusBoundaryPath;
                case CharClass.NonWord: return BonusBoundary;
            }
            if ((prev == CharClass.Lower && cur == CharClass.Upper) || (prev != CharClass.Number && cur == CharClass.Number)) return BonusCamel;
            return 0;
        }
        return cur switch { CharClass.White => BonusBoundaryWhite, CharClass.Delimiter => BonusBoundaryPath, _ => BonusBoundary };
    }

    // ASCII fast path: this runs on every character of every candidate when ranking a large list.
    private static char Fold(char c) => c < 128 ? ((uint)(c - 'A') <= 'Z' - 'A' ? (char)(c | 0x20) : c) : char.ToLowerInvariant(c);

    // ---- scoring (allocation-free) -----------------------------------------------------------------------------------------------

    /// <summary>Score of the best alignment, or <see cref="NoMatch"/>. An empty pattern matches everything with score 0.</summary>
    public static int Score(string pattern, string text)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(text);
        return ScoreCore(pattern.AsSpan(), text.AsSpan());
    }

    public static int Score(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text) => ScoreCore(pattern, text);

    private static int ScoreCore(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text) => ScoreCore(pattern, text, applyFileNameBonus: false);

    /// <param name="applyFileNameBonus">Path mode: also score the part after the last separator and add <c>NameBonus</c>.</param>
    private static int ScoreCore(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text, bool applyFileNameBonus)
    {
        if (pattern.Length == 0) return 0;
        if (pattern.Length > MaxPattern) pattern = pattern[..MaxPattern];
        if (text.Length > MaxText) text = text[^MaxText..];
        var p = pattern.Length;
        var t = text.Length;
        if (p > t) return NoMatch;

        // Case-insensitive search without folding the text: each pattern char is looked up as (lower, upper) with a vectorised IndexOfAny.
        Span<char> lo = stackalloc char[p];
        Span<char> up = stackalloc char[p];
        for (var i = 0; i < p; i++) { lo[i] = char.ToLowerInvariant(pattern[i]); up[i] = char.ToUpperInvariant(pattern[i]); }

        Span<int> first = stackalloc int[p];
        Span<int> last = stackalloc int[p];
        // cheap rejection first: most candidates die here, before any DP work
        if (!TryWindows(lo, up, text, first, last)) return NoMatch;

        // scratch rows for the DP: on the stack for ordinary file names/paths, pooled for the long tail
        var rented = t <= 160 ? null : ArrayPool<int>.Shared.Rent(6 * t);
        Span<int> buf = rented is null ? stackalloc int[6 * t] : rented.AsSpan(0, 6 * t);
        try
        {
            var whole = SparseDp(pattern, text, lo, up, first, last, buf);
            if (!applyFileNameBonus) return whole;

            var slash = text.LastIndexOfAny('/', '\\');
            if (slash < 0 || slash == t - 1 || t - slash - 1 < p) return whole;
            var name = text[(slash + 1)..];
            Span<int> nFirst = stackalloc int[p];
            Span<int> nLast = stackalloc int[p];
            if (!TryWindows(lo, up, name, nFirst, nLast)) return whole;
            var inName = SparseDp(pattern, name, lo, up, nFirst, nLast, buf);
            return inName == NoMatch ? whole : Math.Max(whole, inName + NameBonus);
        }
        finally
        {
            if (rented is not null) ArrayPool<int>.Shared.Return(rented);
        }
    }

    private static int IndexOfFold(ReadOnlySpan<char> text, char lo, char up) => lo == up ? text.IndexOf(lo) : text.IndexOfAny(lo, up);
    private static int LastIndexOfFold(ReadOnlySpan<char> text, char lo, char up) => lo == up ? text.LastIndexOf(lo) : text.LastIndexOfAny(lo, up);

    /// <summary>Earliest/latest feasible position of every pattern character; false when the pattern is not a subsequence of the text.</summary>
    private static bool TryWindows(ReadOnlySpan<char> lo, ReadOnlySpan<char> up, ReadOnlySpan<char> text, Span<int> first, Span<int> last)
    {
        var p = lo.Length;
        var pos = 0;
        for (var i = 0; i < p; i++)
        {
            var at = IndexOfFold(text[pos..], lo[i], up[i]);
            if (at < 0) return false;
            first[i] = pos + at;
            pos += at + 1;
        }
        var end = text.Length;
        for (var i = p - 1; i >= 0; i--)
        {
            last[i] = LastIndexOfFold(text[..end], lo[i], up[i]);
            end = last[i];
        }
        return true;
    }

    private static int BonusAt(ReadOnlySpan<char> text, int j) =>
        BonusFor(j == 0 ? CharClass.White : ClassOf(text[j - 1]), ClassOf(text[j]));

    /// <summary>
    /// The DP over MATCH POSITIONS only: row i holds the positions where pattern[i] can match, with the best score and the start bonus of the
    /// run each is part of. Work is proportional to the number of character matches, not to the text length, which is what makes ranking
    /// 100k paths cheap. Gap penalties are carried forward incrementally between consecutive positions instead of being re-derived.
    /// </summary>
    private static int SparseDp(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text, ReadOnlySpan<char> lo, ReadOnlySpan<char> up,
        ReadOnlySpan<int> first, ReadOnlySpan<int> last, Span<int> buf)
    {
        var p = pattern.Length;
        var t = text.Length;
        // two rows of (position, score, chunk-bonus); each row has at most t entries
        var posA = buf.Slice(0, t); var scoreA = buf.Slice(t, t); var chunkA = buf.Slice(2 * t, t);
        var posB = buf.Slice(3 * t, t); var scoreB = buf.Slice(4 * t, t); var chunkB = buf.Slice(5 * t, t);
        var nA = 0;

        // row 0: the first character earns its position bonus twice over
        for (var j = first[0]; j <= last[0];)
        {
            var at = IndexOfFold(text[j..(last[0] + 1)], lo[0], up[0]);
            if (at < 0) break;
            j += at;
            var b = BonusAt(text, j);
            posA[nA] = j; scoreA[nA] = ScoreMatch + (text[j] == pattern[0] ? BonusExactCase : 0) + b * FirstCharMultiplier; chunkA[nA] = b;
            nA++;
            j++;
        }

        for (var i = 1; i < p && nA > 0; i++)
        {
            var nB = 0;
            var ptr = 0;               // first entry of the previous row not yet folded into `gap`
            var gap = Impossible;      // best (previous score + gap penalty) over previous entries at position <= j-2
            var gapAt = 0;             // position `gap` is expressed for

            for (var j = Math.Max(first[i], posA[0] + 1); j <= last[i];)
            {
                var at = IndexOfFold(text[j..(last[i] + 1)], lo[i], up[i]);
                if (at < 0) break;
                j += at;

                if (gap > Impossible) gap += (j - gapAt) * ScoreGapExtension; // every earlier candidate has one more skipped character per step
                gapAt = j;
                while (ptr < nA && posA[ptr] <= j - 2)
                {
                    var cand = scoreA[ptr] + ScoreGapStart + (j - posA[ptr] - 2) * ScoreGapExtension;
                    if (cand > gap) gap = cand;
                    ptr++;
                }

                var matchScore = ScoreMatch + (text[j] == pattern[i] ? BonusExactCase : 0);
                var bonus = BonusAt(text, j);
                var best = Impossible;
                var chunk = bonus;

                if (ptr < nA && posA[ptr] == j - 1) // the previous character matched right before this one: a consecutive run continues
                {
                    best = scoreA[ptr] + matchScore + Math.Max(chunkA[ptr], BonusConsecutive);
                    chunk = chunkA[ptr];
                }
                if (gap > Impossible && gap + matchScore + bonus > best) { best = gap + matchScore + bonus; chunk = bonus; }

                if (best > Impossible) { posB[nB] = j; scoreB[nB] = best; chunkB[nB] = chunk; nB++; }
                j++;
            }

            // roll the rows
            var tp = posA; posA = posB; posB = tp;
            var ts = scoreA; scoreA = scoreB; scoreB = ts;
            var tc = chunkA; chunkA = chunkB; chunkB = tc;
            nA = nB;
        }

        if (nA == 0) return NoMatch;
        var result = Impossible;
        for (var k = 0; k < nA; k++) if (scoreA[k] > result) result = scoreA[k];
        return Math.Max(result, 0);
    }

    // ---- matching with indices ---------------------------------------------------------------------------------------------------

    /// <summary>The best alignment with the matched indices, or null. Allocates (matrices + result): use it for the visible rows, not for every candidate.</summary>
    public static FuzzyMatch? Match(string pattern, string text)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(text);
        if (pattern.Length == 0) return new FuzzyMatch(0, []);
        if (pattern.Length > MaxPattern) pattern = pattern[..MaxPattern];
        var offset = 0;
        if (text.Length > MaxText) { offset = text.Length - MaxText; text = text[offset..]; }
        var p = pattern.Length;
        var t = text.Length;
        if (p > t || ScoreCore(pattern, text) == NoMatch) return null;

        var bonus = new int[t];
        var prev = CharClass.White;
        for (var j = 0; j < t; j++) { var cls = ClassOf(text[j]); bonus[j] = BonusFor(prev, cls); prev = cls; }

        var m = new int[p * t];
        var chunk = new int[p * t];
        Array.Fill(m, Impossible);

        for (var j = 0; j < t; j++)
        {
            if (Fold(text[j]) != Fold(pattern[0])) continue;
            m[j] = ScoreMatch + (text[j] == pattern[0] ? BonusExactCase : 0) + bonus[j] * FirstCharMultiplier;
            chunk[j] = bonus[j];
        }
        for (var i = 1; i < p; i++)
        {
            var gap = Impossible;
            for (var j = 1; j < t; j++)
            {
                if (j >= 2)
                {
                    var k = j - 2;
                    var older = gap > Impossible ? gap + ScoreGapExtension : Impossible;
                    var fresh = m[(i - 1) * t + k] > Impossible ? m[(i - 1) * t + k] + ScoreGapStart : Impossible;
                    gap = Math.Max(older, fresh);
                }
                if (Fold(text[j]) != Fold(pattern[i])) continue;

                var matchScore = ScoreMatch + (text[j] == pattern[i] ? BonusExactCase : 0);
                var best = Impossible;
                var c = bonus[j];
                var diag = m[(i - 1) * t + j - 1];
                if (diag > Impossible)
                {
                    best = diag + matchScore + Math.Max(chunk[(i - 1) * t + j - 1], BonusConsecutive);
                    c = chunk[(i - 1) * t + j - 1];
                }
                if (gap > Impossible && gap + matchScore + bonus[j] > best) { best = gap + matchScore + bonus[j]; c = bonus[j]; }
                if (best > Impossible) { m[i * t + j] = best; chunk[i * t + j] = c; }
            }
        }

        // best end position of the last pattern character, then walk back through the transition that produced each cell
        var endJ = -1;
        var endScore = Impossible;
        for (var j = 0; j < t; j++) if (m[(p - 1) * t + j] > endScore) { endScore = m[(p - 1) * t + j]; endJ = j; }
        if (endJ < 0) return null;

        var indices = new int[p];
        var cj = endJ;
        for (var i = p - 1; i >= 0; i--)
        {
            indices[i] = cj + offset;
            if (i == 0) break;
            var cell = m[i * t + cj];
            var matchScore = ScoreMatch + (text[cj] == pattern[i] ? BonusExactCase : 0);
            var diag = cj - 1 >= 0 ? m[(i - 1) * t + cj - 1] : Impossible;
            if (diag > Impossible && diag + matchScore + Math.Max(chunk[(i - 1) * t + cj - 1], BonusConsecutive) == cell)
            {
                cj -= 1;
                continue;
            }
            var found = -1;
            for (var k = cj - 2; k >= 0; k--)
            {
                var prevScore = m[(i - 1) * t + k];
                if (prevScore <= Impossible) continue;
                var g = cj - k - 1; // skipped characters between the two matches
                if (prevScore + ScoreGapStart + (g - 1) * ScoreGapExtension + matchScore + bonus[cj] == cell) { found = k; break; }
            }
            if (found < 0) return null; // cannot happen: the DP and the backtrack share one recurrence
            cj = found;
        }
        return new FuzzyMatch(Math.Max(endScore, 0), indices);
    }

    // ---- paths -------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Score for a file path: the better of matching the whole path and matching the file NAME (which earns <c>+NameBonus</c>), because
    /// typing "mw" for <c>src/App/MainWindow.axaml</c> means the file, not the directories.
    /// </summary>
    public static int ScorePath(string pattern, string path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);
        return ScoreCore(pattern.AsSpan(), path.AsSpan(), applyFileNameBonus: true);
    }

    public static FuzzyMatch? MatchPath(string pattern, string path)
    {
        var slash = path.LastIndexOfAny(['/', '\\']);
        if (slash >= 0 && slash < path.Length - 1)
        {
            var name = path[(slash + 1)..];
            if (ScoreCore(pattern.AsSpan(), name.AsSpan()) != NoMatch)
            {
                var inName = Match(pattern, name);
                var whole = Match(pattern, path);
                if (inName is { } n && (whole is null || n.Score + NameBonus >= whole.Value.Score))
                    return new FuzzyMatch(n.Score + NameBonus, n.Indices.Select(i => i + slash + 1).ToArray());
                return whole;
            }
        }
        return Match(pattern, path);
    }

    // ---- ranking -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The best <paramref name="maxResults"/> items for the pattern, best first (ties: shorter text first, then original order).
    /// Single-threaded and allocation-light: intended for a background lane; checks <paramref name="ct"/> every 256 items.
    /// </summary>
    public static List<FuzzyResult<T>> Rank<T>(string pattern, IReadOnlyList<T> items, Func<T, string> textOf, int maxResults, bool isPath = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(textOf);
        if (maxResults <= 0) return [];

        // Min-heap of the current best N: the worst of them is at the top and is evicted by anything better.
        var heap = new PriorityQueue<(T Item, int Index, int Length), (int Score, int NegLength, int NegIndex)>(Math.Min(maxResults, items.Count) + 1);
        for (var i = 0; i < items.Count; i++)
        {
            if ((i & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            var text = textOf(items[i]);
            var score = isPath ? ScorePath(pattern, text) : Score(pattern, text);
            if (score == NoMatch) continue;
            var key = (score, -text.Length, -i);
            if (heap.Count < maxResults) heap.Enqueue((items[i], i, text.Length), key);
            else if (heap.TryPeek(out _, out var worst) && key.CompareTo(worst) > 0) heap.EnqueueDequeue((items[i], i, text.Length), key);
        }

        var result = new List<FuzzyResult<T>>(heap.Count);
        while (heap.TryDequeue(out var e, out var k)) result.Add(new FuzzyResult<T>(e.Item, k.Score, e.Index));
        result.Reverse();
        return result;
    }
}
