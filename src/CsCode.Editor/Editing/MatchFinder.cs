namespace CsCode.Editor.Editing;

/// <summary>What Add Next Occurrence / Select All Occurrences look for: the text, and whether it must be a whole word and match case.</summary>
public sealed record OccurrenceQuery(string Text, bool WholeWord, bool MatchCase)
{
    public StringComparison Comparison => MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}

/// <summary>
/// Finds occurrences of a text in a document read through chunks (an immutable snapshot, so any thread), without materialising the document:
/// each chunk is searched with the vectorised span search, chunks overlap by the query's length minus one so no match is missed at a seam.
/// </summary>
public static class MatchFinder
{
    private const int ChunkSize = 1 << 16;

    /// <summary>
    /// The first match starting at or after <paramref name="from"/>, wrapping to the document's start; <paramref name="accept"/> can skip a match
    /// (one already selected). -1 when there is none (or every one is skipped).
    /// </summary>
    public static int FindNext(int length, Func<int, int, ReadOnlyMemory<char>> read, OccurrenceQuery query, int from, Func<int, bool>? accept = null)
    {
        from = Math.Clamp(from, 0, length);
        var found = Find(length, read, query, from, length, accept);
        if (found >= 0) return found;
        return Find(length, read, query, 0, Math.Min(length, from + query.Text.Length - 1), accept);
    }

    /// <summary>Every match in document order, at most <paramref name="limit"/> (whether more existed is reported).</summary>
    public static (List<int> Starts, bool Limited) FindAll(int length, Func<int, int, ReadOnlyMemory<char>> read, OccurrenceQuery query, int limit, CancellationToken ct = default)
    {
        var starts = new List<int>();
        var limited = false;
        var at = 0;
        while (at <= length - query.Text.Length)
        {
            ct.ThrowIfCancellationRequested();
            var found = Find(length, read, query, at, length, null);
            if (found < 0) break;
            if (starts.Count == limit)
            {
                limited = true;
                break;
            }
            starts.Add(found);
            at = found + Math.Max(1, query.Text.Length);
        }
        return (starts, limited);
    }

    /// <summary>The first acceptable match starting in [<paramref name="from"/>, <paramref name="end"/>) (the match itself may run past end).</summary>
    private static int Find(int length, Func<int, int, ReadOnlyMemory<char>> read, OccurrenceQuery query, int from, int end, Func<int, bool>? accept)
    {
        var needle = query.Text.AsSpan();
        var n = needle.Length;
        if (n == 0 || n > length) return -1;
        var pos = from;
        while (pos < end && pos <= length - n)
        {
            var chunkLength = Math.Min(ChunkSize + n - 1, length - pos);
            var memory = read(pos, chunkLength);
            var span = memory.Span;
            var searched = 0;
            while (true)
            {
                var i = span[searched..].IndexOf(needle, query.Comparison);
                if (i < 0) break;
                var start = pos + searched + i;
                if (start >= end) return -1;
                if ((!query.WholeWord || IsWholeWord(length, read, start, n)) && (accept is null || accept(start))) return start;
                searched += i + 1;
                if (searched > span.Length - n) break;
            }
            if (pos + chunkLength >= length) break;
            pos += Math.Max(1, chunkLength - (n - 1));
        }
        return -1;
    }

    private static bool IsWholeWord(int length, Func<int, int, ReadOnlyMemory<char>> read, int start, int n)
    {
        if (start > 0 && IsWordChar(read(start - 1, 1).Span[0])) return false;
        if (start + n < length && IsWordChar(read(start + n, 1).Span[0])) return false;
        return true;
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
