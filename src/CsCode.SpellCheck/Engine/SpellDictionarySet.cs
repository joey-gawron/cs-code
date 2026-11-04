using WeCantSpell.Hunspell;

namespace CsCode.SpellCheck.Engine;

/// <summary>What the dictionaries say about a word.</summary>
public enum SpellVerdict : byte { Ok = 0, Unknown = 1, Forbidden = 2 }

/// <summary>The word-level checker a document check uses (the loaded dictionaries; a fake in tests).</summary>
public interface IWordChecker
{
    /// <summary>Known to a dictionary or to the software terms (not the user's words: those are in the settings).</summary>
    bool IsKnown(ReadOnlySpan<char> word);

    /// <summary>Up to <paramref name="max"/> replacements, best first, in the dictionaries' casing.</summary>
    IReadOnlyList<string> Suggest(string word, int max);
}

/// <summary>
/// The loaded Hunspell dictionaries <c>cSpell.language</c> chose, plus CsCode's software terms: an immutable set the spell lane checks
/// words against. Hunspell accepts a word's capitalised and upper-case forms itself (<c>Receive</c>, <c>RECEIVE</c>); on top of it a
/// possessive is checked without its <c>'s</c> and a plural acronym without its <c>s</c> (<c>APIs</c>). Verdicts are cached per exact word
/// (bounded; the cache is lane-only, so it takes no lock). Hunspell's own structures may be queried from several threads at once.
/// </summary>
public sealed class SpellDictionarySet : IWordChecker
{
    private const int MaxCachedVerdicts = 50_000;

    private readonly IReadOnlyList<WordList> _lists;
    private readonly HashSet<string> _terms;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _termsLookup;
    private readonly Dictionary<string, bool> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool>.AlternateLookup<ReadOnlySpan<char>> _cacheLookup;

    public SpellDictionarySet(IReadOnlyList<DictionarySource> sources, IReadOnlyList<WordList> lists, HashSet<string> softwareTerms, IReadOnlyList<string> missing)
    {
        Sources = sources;
        _lists = lists;
        _terms = softwareTerms;
        _termsLookup = softwareTerms.GetAlternateLookup<ReadOnlySpan<char>>();
        _cacheLookup = _cache.GetAlternateLookup<ReadOnlySpan<char>>();
        Missing = missing;
        // measured (docs/SPELLCHECK.md): a Hunspell word list holds about 20 times its .dic file in managed memory (en_US: 551 KB → 11.1 MB)
        EstimatedBytes = sources.Sum(s => s.DicBytes * 20) + softwareTerms.Count * 64L;
    }

    public IReadOnlyList<DictionarySource> Sources { get; }

    /// <summary>The <c>cSpell.language</c> entries no dictionary matched.</summary>
    public IReadOnlyList<string> Missing { get; }

    public long EstimatedBytes { get; }

    public int CachedVerdicts => _cache.Count;

    public bool IsKnown(ReadOnlySpan<char> word)
    {
        if (_cacheLookup.TryGetValue(word, out var known)) return known;
        known = Compute(word);
        if (_cache.Count >= MaxCachedVerdicts) _cache.Clear();
        _cacheLookup[word] = known;
        return known;
    }

    private bool Compute(ReadOnlySpan<char> word)
    {
        if (_termsLookup.Contains(word)) return true;
        if (word.Contains('’'))
        {
            Span<char> straight = word.Length <= 128 ? stackalloc char[word.Length] : new char[word.Length];
            word.CopyTo(straight);
            straight.Replace('’', '\'');
            return Compute(straight);
        }
        foreach (var list in _lists)
            if (list.Check(word)) return true;
        // the possessive of a known word: Atkinson's, the parser's
        if (word.Length > 3 && word[^2] == '\'' && word[^1] is 's' or 'S') return Compute(word[..^2]);
        // a plural acronym: APIs, URLs, IDs (Hunspell has some, not all)
        if (word.Length >= 3 && word[^1] == 's' && IsUpper(word[..^1])) return Compute(word[..^1]);
        // a word Hunspell knows only in lower case, written in title or upper case in an identifier (Http, JSON's parts)
        if (word.Length > 1 && char.IsUpper(word[0]))
        {
            Span<char> lower = word.Length <= 128 ? stackalloc char[word.Length] : new char[word.Length];
            word.ToLowerInvariant(lower);
            if (!lower.SequenceEqual(word))
                foreach (var list in _lists)
                    if (list.Check(lower)) return true;
        }
        return false;
    }

    public IReadOnlyList<string> Suggest(string word, int max)
    {
        var result = new List<string>();
        foreach (var list in _lists)
        {
            foreach (var s in list.Suggest(word))
            {
                if (result.Count >= max) break;
                if (!result.Contains(s, StringComparer.Ordinal)) result.Add(s);
            }
        }
        return result;
    }

    /// <summary>Software terms close to <paramref name="word"/> (edit distance 1), for words Hunspell has no good suggestion for.</summary>
    public IEnumerable<string> NearTerms(string word)
    {
        var lower = word.ToLowerInvariant();
        foreach (var t in _terms)
            if (Math.Abs(t.Length - lower.Length) <= 1 && EditDistance.AtMostOne(t, lower)) yield return t;
    }

    public void ClearCache() => _cache.Clear();

    private static bool IsUpper(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
            if (!char.IsUpper(c)) return false;
        return true;
    }
}

/// <summary>Edit distance helpers (suggestions among the user's words and the software terms).</summary>
public static class EditDistance
{
    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> differ by at most one insertion, deletion, substitution or adjacent swap.</summary>
    public static bool AtMostOne(string a, string b)
    {
        if (a == b) return true;
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        if (a.Length == b.Length)
        {
            if (a.AsSpan(i + 1).SequenceEqual(b.AsSpan(i + 1))) return true; // substitution
            return i + 1 < a.Length && a[i] == b[i + 1] && a[i + 1] == b[i] && a.AsSpan(i + 2).SequenceEqual(b.AsSpan(i + 2)); // swap
        }
        return a.Length > b.Length ? a.AsSpan(i + 1).SequenceEqual(b.AsSpan(i)) : a.AsSpan(i).SequenceEqual(b.AsSpan(i + 1));
    }

    /// <summary>Damerau-Levenshtein (optimal string alignment) distance, capped at <paramref name="limit"/> + 1.</summary>
    public static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;
        var prev2 = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var best = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                var v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, prev2[j - 2] + 1);
                cur[j] = v;
                best = Math.Min(best, v);
            }
            if (best > limit) return limit + 1;
            (prev2, prev, cur) = (prev, cur, prev2);
        }
        return prev[b.Length];
    }
}
