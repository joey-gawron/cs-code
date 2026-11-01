using AvaloniaEdit.Document;

namespace CsCode.Editor.Editing;

/// <summary>
/// Maps offsets through a sequence of document changes (the change events of one document update, in the order they happened) with the same
/// result as mapping each offset through every change in turn (<see cref="DocumentChangeEventArgs.GetNewOffset"/>, default movement), but in
/// O(log changes) per offset when the changes run in one direction without touching each other: back to front (how a batch of edits and its
/// redo are applied) or front to back (how AvaloniaEdit's undo replays such a batch). Anything else maps change by change.
/// So n carets follow an n-change undo step in O(n log n) instead of O(n²).
/// </summary>
public sealed class ChangeSequenceMapper
{
    private readonly DocumentChangeEventArgs[] _changes; // in the order they happened
    private readonly int[]? _order;                      // indexes into _changes, by offset in the text before the first change
    private readonly int[]? _original;                   // each ordered change's offset in the text before the first change
    private readonly int[]? _before;                     // added to an original offset to reach the ordered change's own text
    private readonly int[]? _after;                      // added to that change's result to reach the text after the last change

    public ChangeSequenceMapper(IReadOnlyList<DocumentChangeEventArgs> changes)
    {
        _changes = changes.ToArray();
        var n = _changes.Length;
        if (n == 0) return;
        if (BackToFront(_changes))
        {
            // every change is in the original text's offsets; an offset is shifted by the changes below the one that holds it
            _order = new int[n];
            _original = new int[n];
            _before = new int[n];
            _after = new int[n];
            var below = 0;
            for (var k = 0; k < n; k++)
            {
                var c = _changes[n - 1 - k];
                _order[k] = n - 1 - k;
                _original[k] = c.Offset;
                _after[k] = below;
                below += c.InsertionLength - c.RemovalLength;
            }
        }
        else if (FrontToBack(_changes))
        {
            // each change is in the offsets left by the ones before it; the ones after it do not reach what it maps to
            _order = new int[n];
            _original = new int[n];
            _before = new int[n];
            _after = new int[n];
            var shift = 0;
            for (var k = 0; k < n; k++)
            {
                var c = _changes[k];
                _order[k] = k;
                _original[k] = c.Offset - shift;
                _before[k] = shift;
                shift += c.InsertionLength - c.RemovalLength;
            }
        }
    }

    /// <summary>Whether offsets are mapped by binary search (false: change by change).</summary>
    public bool IsOrdered => _order is not null || _changes.Length == 0;

    /// <summary>Where <paramref name="offset"/> (in the text before the first change) is in the text after the last.</summary>
    public int Map(int offset)
    {
        if (_order is null)
        {
            foreach (var c in _changes) offset = c.GetNewOffset(offset, AnchorMovementType.Default);
            return offset;
        }
        // the last change starting at or before the offset is the only one that can hold it
        int lo = 0, hi = _original!.Length - 1, j = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_original[mid] <= offset)
            {
                j = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }
        if (j < 0) return offset;
        return _changes[_order[j]].GetNewOffset(offset + _before![j], AnchorMovementType.Default) + _after![j];
    }

    // each next change ends at or before the previous one's start and starts strictly below it: none moves another's offsets
    private static bool BackToFront(DocumentChangeEventArgs[] c)
    {
        for (var k = 1; k < c.Length; k++)
            if (c[k].Offset >= c[k - 1].Offset || c[k].Offset + c[k].RemovalLength > c[k - 1].Offset)
                return false;
        return true;
    }

    // each next change starts strictly after the text the previous one inserted: what one change maps to, the later ones leave alone
    private static bool FrontToBack(DocumentChangeEventArgs[] c)
    {
        for (var k = 1; k < c.Length; k++)
            if (c[k].Offset <= c[k - 1].Offset + c[k - 1].InsertionLength)
                return false;
        return true;
    }
}
