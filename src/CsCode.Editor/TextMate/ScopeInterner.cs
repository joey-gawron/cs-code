using CsCode.Editor.Theming;

namespace CsCode.Editor.TextMate;

/// <summary>
/// Interns TextMate scope stacks (<c>["source.cs", "meta.method", "entity.name.function"]</c>) to small integer ids. A tokenised line stores
/// <c>(startColumn, scopeStackId)</c> pairs — 8 bytes per token instead of a list of strings — and the id is theme-independent, so a
/// theme change re-resolves a few thousand distinct stacks instead of re-tokenising anything. Id 0 is "no scopes" (unstyled).
/// Writes happen on the editor worker, reads on any thread (the entry array is only ever replaced by a larger copy, never mutated in place
/// below <see cref="Count"/>).
/// </summary>
public sealed class ScopeInterner
{
    /// <summary>Backstop against pathological grammars: beyond this, new stacks map to id 0 (default style) instead of growing forever.</summary>
    public const int MaxEntries = 200_000;

    private sealed record Entry(string[] Scopes, TokenKind Kind);

    private readonly object _gate = new();
    private readonly Dictionary<int, List<int>> _byHash = new();
    private Entry[] _entries = new Entry[512];
    private int _count = 1;

    public ScopeInterner() => _entries[0] = new Entry([], TokenKind.Other);

    public int Count => Volatile.Read(ref _count);

    public int Intern(IReadOnlyList<string> scopes)
    {
        if (scopes.Count == 0) return 0;
        var hash = new HashCode();
        for (var i = 0; i < scopes.Count; i++) hash.Add(scopes[i], StringComparer.Ordinal);
        var h = hash.ToHashCode();
        lock (_gate)
        {
            if (_byHash.TryGetValue(h, out var bucket))
                foreach (var id in bucket)
                    if (Same(_entries[id].Scopes, scopes)) return id; // hit: no allocation
            if (_count >= MaxEntries) return 0;
            var copy = new string[scopes.Count];
            for (var i = 0; i < copy.Length; i++) copy[i] = scopes[i];
            var entry = new Entry(copy, TokenColorResolver.KindOf(copy));
            if (_count == _entries.Length)
            {
                var bigger = new Entry[_entries.Length * 2];
                Array.Copy(_entries, bigger, _count);
                Volatile.Write(ref _entries, bigger);
            }
            var newId = _count;
            _entries[newId] = entry; // the entry is in place BEFORE the count that publishes it
            if (!_byHash.TryGetValue(h, out bucket)) _byHash[h] = bucket = [];
            bucket.Add(newId);
            Volatile.Write(ref _count, newId + 1);
            return newId;
        }
    }

    public string[] Scopes(int id) => Volatile.Read(ref _entries)[id].Scopes;

    public TokenKind Kind(int id) => Volatile.Read(ref _entries)[id].Kind;

    private static bool Same(string[] stored, IReadOnlyList<string> scopes)
    {
        if (stored.Length != scopes.Count) return false;
        for (var i = 0; i < stored.Length; i++)
            if (!string.Equals(stored[i], scopes[i], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Approximate managed bytes held (for diagnostics / IMemoryTrimmable accounting).</summary>
    public long EstimatedBytes
    {
        get
        {
            var n = Count;
            return n * 120L; // entry + array header + shared string references; strings themselves are mostly shared literals
        }
    }
}
