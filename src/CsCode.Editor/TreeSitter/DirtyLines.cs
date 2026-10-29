namespace CsCode.Editor.TreeSitter;

/// <summary>
/// Lines whose tokens may be wrong for the current syntax tree, as sorted, disjoint, inclusive ranges, kept in step with line insertions and
/// deletions the same way <c>LineTokenStore.ApplyChange</c> keeps the token rows. Worker only. Small: edits touch a few lines, and a change
/// that dirties a huge region is collapsed into a handful of ranges.
/// </summary>
internal sealed class DirtyLines
{
    private readonly List<(int First, int Last)> _ranges = new();

    public bool IsEmpty => _ranges.Count == 0;

    public int RangeCount => _ranges.Count;

    public IReadOnlyList<(int First, int Last)> Ranges => _ranges;

    public void Clear() => _ranges.Clear();

    public void Add(int first, int last)
    {
        if (last < first) return;
        // merge with every overlapping or adjacent range
        var i = 0;
        while (i < _ranges.Count && _ranges[i].Last < first - 1) i++;
        var j = i;
        while (j < _ranges.Count && _ranges[j].First <= last + 1)
        {
            first = Math.Min(first, _ranges[j].First);
            last = Math.Max(last, _ranges[j].Last);
            j++;
        }
        _ranges.RemoveRange(i, j - i);
        _ranges.Insert(i, (first, last));
    }

    /// <summary>
    /// A change inside line <paramref name="line"/> removed <paramref name="removedBreaks"/> and inserted <paramref name="insertedBreaks"/> line
    /// breaks: lines after it move, the removed ones disappear. The changed lines themselves are added by the caller.
    /// </summary>
    public void Shift(int line, int removedBreaks, int insertedBreaks)
    {
        var delta = insertedBreaks - removedBreaks;
        if (delta == 0 && removedBreaks == 0) return;
        var removedLast = line + removedBreaks; // lines (line, removedLast] are gone (merged into `line`)
        for (var k = _ranges.Count - 1; k >= 0; k--)
        {
            var (f, l) = _ranges[k];
            f = Map(f, line, removedLast, delta);
            l = Map(l, line, removedLast, delta);
            if (l < f)
            {
                _ranges.RemoveAt(k);
                continue;
            }
            _ranges[k] = (f, l);
        }
        // shifting can make neighbours touch: re-normalise
        if (_ranges.Count > 1)
        {
            var copy = _ranges.ToArray();
            _ranges.Clear();
            foreach (var (f, l) in copy) Add(f, l);
        }
    }

    private static int Map(int x, int line, int removedLast, int delta) =>
        x <= line ? x
        : x <= removedLast ? line // a removed line collapses onto the edited one
        : x + delta;

    /// <summary>Removes and returns the dirty lines inside [<paramref name="first"/>, <paramref name="last"/>].</summary>
    public List<(int First, int Last)> Take(int first, int last)
    {
        var taken = new List<(int, int)>();
        for (var k = 0; k < _ranges.Count; k++)
        {
            var (f, l) = _ranges[k];
            if (l < first || f > last) continue;
            var a = Math.Max(f, first);
            var b = Math.Min(l, last);
            taken.Add((a, b));
            var replacement = new List<(int, int)>(2);
            if (f < a) replacement.Add((f, a - 1));
            if (l > b) replacement.Add((b + 1, l));
            _ranges.RemoveAt(k);
            _ranges.InsertRange(k, replacement);
            k += replacement.Count - 1;
        }
        return taken;
    }

    /// <summary>Removes and returns everything outside [<paramref name="first"/>, <paramref name="last"/>].</summary>
    public List<(int First, int Last)> TakeOutside(int first, int last)
    {
        var taken = new List<(int, int)>();
        if (first > 0) taken.AddRange(Take(0, first - 1));
        taken.AddRange(Take(last + 1, int.MaxValue));
        return taken;
    }

    /// <summary>Clamps every range to the document's last line (after the line count shrank).</summary>
    public void Clamp(int lineCount)
    {
        for (var k = _ranges.Count - 1; k >= 0; k--)
        {
            var (f, l) = _ranges[k];
            if (f >= lineCount) _ranges.RemoveAt(k);
            else if (l >= lineCount) _ranges[k] = (f, lineCount - 1);
        }
    }
}
