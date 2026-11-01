namespace CsCode.Editor.Editing;

/// <summary>
/// One caret of a multi-caret editor: a selection from <see cref="Anchor"/> (where it started) to <see cref="Active"/> (where the caret blinks),
/// both document offsets. <see cref="DesiredColumn"/> is the visual column (tabs expanded) Up/Down aim for, kept while moving vertically over
/// shorter lines (-1: the caret's own column).
/// </summary>
public readonly record struct CaretRange(int Anchor, int Active, int DesiredColumn = -1)
{
    public int Start => Math.Min(Anchor, Active);
    public int End => Math.Max(Anchor, Active);
    public bool IsEmpty => Anchor == Active;
    public int Length => End - Start;

    /// <summary>The selection runs backwards (the caret at its start).</summary>
    public bool IsReversed => Active < Anchor;

    public static CaretRange At(int offset) => new(offset, offset);
    public static CaretRange Select(int start, int end) => new(start, end);
}

/// <summary>
/// The carets of one view: sorted by start offset, never overlapping (touching selections stay apart; a caret touching a selection merges into
/// it), at most a bounded number, one of them the primary (the one AvaloniaEdit draws and every single-caret feature uses). Immutable: every
/// operation returns a new set. Normalising is O(n) for an already sorted input, O(n log n) otherwise; nothing here looks at the document.
/// </summary>
public sealed class CaretSet
{
    private readonly CaretRange[] _carets;

    private CaretSet(CaretRange[] sortedMerged, int primary)
    {
        _carets = sortedMerged;
        PrimaryIndex = primary;
    }

    /// <summary>The carets in document order.</summary>
    public IReadOnlyList<CaretRange> Carets => _carets;

    public int Count => _carets.Length;
    public int PrimaryIndex { get; }
    public CaretRange Primary => _carets[PrimaryIndex];
    public bool IsMulti => _carets.Length > 1;
    public CaretRange this[int index] => _carets[index];

    /// <summary>True when the last <see cref="Create"/> had to drop carets above the limit.</summary>
    public bool WasLimited { get; private init; }

    public static CaretSet Single(CaretRange caret) => new([caret], 0);

    /// <summary>
    /// Sorts and merges <paramref name="carets"/> (the caret at <paramref name="primary"/> stays primary, also when it merges with others) and keeps
    /// at most <paramref name="limit"/> of them: those nearest the primary in document order, the primary always among them.
    /// </summary>
    public static CaretSet Create(IReadOnlyList<CaretRange> carets, int primary, int limit = int.MaxValue)
    {
        if (carets.Count == 0) throw new ArgumentException("A caret set needs at least one caret.", nameof(carets));
        primary = Math.Clamp(primary, 0, carets.Count - 1);
        var items = new (CaretRange Caret, bool Primary)[carets.Count];
        var sorted = true;
        for (var i = 0; i < carets.Count; i++)
        {
            items[i] = (carets[i], i == primary);
            if (i > 0 && Compare(carets[i - 1], carets[i]) > 0) sorted = false;
        }
        if (!sorted) Array.Sort(items, (a, b) => Compare(a.Caret, b.Caret)); // Array.Sort is unstable: ties are equal ranges, which merge anyway

        var merged = new List<CaretRange>(items.Length);
        var mergedPrimary = 0;
        var current = items[0].Caret;
        var currentPrimary = items[0].Primary;
        for (var i = 1; i < items.Length; i++)
        {
            var (next, nextPrimary) = items[i];
            if (Overlaps(current, next))
            {
                current = Merge(current, currentPrimary, next, nextPrimary);
                currentPrimary |= nextPrimary;
                continue;
            }
            if (currentPrimary) mergedPrimary = merged.Count;
            merged.Add(current);
            current = next;
            currentPrimary = nextPrimary;
        }
        if (currentPrimary) mergedPrimary = merged.Count;
        merged.Add(current);

        limit = Math.Max(1, limit);
        if (merged.Count <= limit) return new CaretSet(merged.ToArray(), mergedPrimary);
        // Too many: a window of `limit` carets around the primary (VS Code keeps the first ones; around the primary keeps what the user sees).
        var from = Math.Clamp(mergedPrimary - limit / 2, 0, merged.Count - limit);
        return new CaretSet(merged.GetRange(from, limit).ToArray(), mergedPrimary - from) { WasLimited = true };
    }

    /// <summary>The same carets with another primary (an index into <see cref="Carets"/>).</summary>
    public CaretSet WithPrimary(int index) => new(_carets, Math.Clamp(index, 0, _carets.Length - 1));

    /// <summary>The same carets with the primary replaced by <paramref name="primary"/> (merged with any caret it now overlaps).</summary>
    public CaretSet WithPrimaryRange(CaretRange primary)
    {
        var current = _carets[PrimaryIndex];
        if (current.Anchor == primary.Anchor && current.Active == primary.Active) return this;
        var list = (CaretRange[])_carets.Clone();
        list[PrimaryIndex] = primary with { DesiredColumn = current.DesiredColumn };
        return Create(list, PrimaryIndex);
    }

    /// <summary>This set plus <paramref name="caret"/>, which becomes the primary (merged with any caret it overlaps).</summary>
    public CaretSet Add(CaretRange caret, int limit = int.MaxValue)
    {
        var list = new List<CaretRange>(_carets.Length + 1);
        list.AddRange(_carets);
        list.Add(caret);
        return Create(list, list.Count - 1, limit);
    }

    /// <summary>This set without the caret at <paramref name="index"/> (never the last one); the primary moves to the nearest remaining caret.</summary>
    public CaretSet Remove(int index)
    {
        if (_carets.Length == 1) return this;
        var list = new List<CaretRange>(_carets);
        list.RemoveAt(index);
        var primary = PrimaryIndex == index ? Math.Min(index, list.Count - 1) : PrimaryIndex > index ? PrimaryIndex - 1 : PrimaryIndex;
        return new CaretSet(list.ToArray(), primary);
    }

    /// <summary>The index of the caret whose range contains <paramref name="offset"/> (ends included), or -1. O(log n).</summary>
    public int IndexAt(int offset)
    {
        int lo = 0, hi = _carets.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var c = _carets[mid];
            if (offset < c.Start) hi = mid - 1;
            else if (offset > c.End) lo = mid + 1;
            else return mid;
        }
        return -1;
    }

    /// <summary>The index of the first caret ending at or after <paramref name="offset"/> (for drawing only the visible ones). O(log n).</summary>
    public int FirstEndingAtOrAfter(int offset)
    {
        int lo = 0, hi = _carets.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_carets[mid].End < offset) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int Compare(CaretRange a, CaretRange b)
    {
        var c = a.Start.CompareTo(b.Start);
        return c != 0 ? c : a.End.CompareTo(b.End);
    }

    /// <summary>Overlapping ranges merge; so do a caret and a selection it touches, and carets at one offset. Touching selections stay apart.</summary>
    private static bool Overlaps(CaretRange a, CaretRange b) =>
        b.Start < a.End || b.Start == a.Start || (b.Start == a.End && (a.IsEmpty || b.IsEmpty));

    private static CaretRange Merge(CaretRange a, bool aPrimary, CaretRange b, bool bPrimary)
    {
        var start = Math.Min(a.Start, b.Start);
        var end = Math.Max(a.End, b.End);
        if (start == end) return new CaretRange(start, start, aPrimary ? a.DesiredColumn : b.DesiredColumn);
        // the direction of the primary when it is involved, else of the later one
        var pick = aPrimary && !bPrimary ? a : b.IsEmpty && !a.IsEmpty ? a : b;
        return pick.IsReversed ? new CaretRange(end, start) : new CaretRange(start, end);
    }
}
