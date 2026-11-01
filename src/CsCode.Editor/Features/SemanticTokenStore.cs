using CsCode.Core.Text;

namespace CsCode.Editor.Features;

/// <summary>
/// The semantic tokens of one view's document, by line: each line's tokens as <c>[start, length, type, modifiers]</c> quadruples sorted by start
/// (UTF-16 columns, the legend's indices). Kept in step with the text between answers — VS Code's "keep the old tokens, adjusted by the edits":
/// an edit shifts the tokens after it on its line and moves the lines below it, an edit inside a token grows or shrinks it (so does typing an
/// identifier character at its end), and only tokens the edit cuts are dropped. UI thread only; bounded by <see cref="MaxTokens"/>.
/// </summary>
internal sealed class SemanticTokenStore
{
    public const int MaxTokens = 200_000;
    private const int Stride = 4;

    private readonly List<int[]?> _lines = [];
    private readonly List<int> _scratch = [];

    /// <summary>Tokens of a 0-based line (null when it has none).</summary>
    public int[]? Get(int line) => line >= 0 && line < _lines.Count ? _lines[line] : null;

    public int TokenCount { get; private set; }

    public bool IsEmpty => TokenCount == 0;

    public void Clear()
    {
        _lines.Clear();
        TokenCount = 0;
    }

    /// <summary>Replaces everything with a full answer (LSP relative encoding) for a document of <paramref name="lineCount"/> lines.</summary>
    public void SetFull(IReadOnlyList<uint> data, int lineCount)
    {
        _lines.Clear();
        _lines.Capacity = Math.Max(_lines.Capacity, lineCount);
        for (var i = 0; i < lineCount; i++) _lines.Add(null);
        TokenCount = 0;
        Decode(data, 0, lineCount - 1);
    }

    /// <summary>
    /// Replaces the lines <paramref name="first"/>..<paramref name="last"/> with a range answer; lines further than <paramref name="keep"/> from
    /// the range are forgotten (range mode is for big files: memory follows the viewport).
    /// </summary>
    public void SetRange(IReadOnlyList<uint> data, int first, int last, int lineCount, int keep)
    {
        while (_lines.Count < lineCount) _lines.Add(null);
        if (_lines.Count > lineCount) _lines.RemoveRange(lineCount, _lines.Count - lineCount);
        for (var i = 0; i < _lines.Count; i++)
            if ((i >= first && i <= last) || i < first - keep || i > last + keep) _lines[i] = null;
        TokenCount = 0;
        foreach (var row in _lines) if (row is not null) TokenCount += row.Length / Stride;
        Decode(data, first, last);
    }

    private void Decode(IReadOnlyList<uint> data, int first, int last)
    {
        var line = 0;
        var start = 0;
        var current = -1;
        _scratch.Clear();
        for (var i = 0; i + 5 <= data.Count; i += 5)
        {
            var deltaLine = (int)data[i];
            var deltaStart = (int)data[i + 1];
            line += deltaLine;
            start = deltaLine == 0 ? start + deltaStart : deltaStart;
            if (line != current)
            {
                Flush(current, first, last);
                current = line;
            }
            if (TokenCount + _scratch.Count / Stride >= MaxTokens) break;
            var length = (int)Math.Min(data[i + 2], int.MaxValue);
            if (length <= 0) continue;
            _scratch.Add(start);
            _scratch.Add(length);
            _scratch.Add((int)Math.Min(data[i + 3], int.MaxValue));
            _scratch.Add((int)data[i + 4]);
        }
        Flush(current, first, last);
    }

    private void Flush(int line, int first, int last)
    {
        if (_scratch.Count > 0 && line >= first && line <= last && line < _lines.Count)
        {
            var row = _scratch.ToArray();
            if (!Sorted(row)) row = SortRow(row);
            _lines[line] = row;
            TokenCount += row.Length / Stride;
        }
        _scratch.Clear();
    }

    private static bool Sorted(int[] row)
    {
        for (var i = Stride; i < row.Length; i += Stride)
            if (row[i] < row[i - Stride]) return false;
        return true;
    }

    private static int[] SortRow(int[] row)
    {
        var quads = Enumerable.Range(0, row.Length / Stride).Select(i => row.AsSpan(i * Stride, Stride).ToArray()).OrderBy(q => q[0]);
        return quads.SelectMany(q => q).ToArray();
    }

    /// <summary>
    /// Follows one change (its range against the text BEFORE it, as <c>EditorDocument.BeforeChange</c> reports it): O(tokens of the lines the
    /// change touches) plus moving the line list when lines come or go.
    /// </summary>
    public void Apply(TextContentChange change)
    {
        if (_lines.Count == 0) return;
        var (sl, sc) = (change.Range.Start.Line, change.Range.Start.Character);
        var (el, ec) = (change.Range.End.Line, change.Range.End.Character);
        if (sl >= _lines.Count) return;
        el = Math.Min(el, _lines.Count - 1);
        var text = change.Text;
        var inserted = CountLines(text, out var lastSegment);

        // a change inside one line that adds no line: shift, grow or shrink in place
        if (sl == el && inserted == 0)
        {
            if (_lines[sl] is { } row) _lines[sl] = EditRow(row, sc, ec, text);
            return;
        }

        var head = _lines[sl] is { } h ? Keep(h, t => t.Start + t.Length <= sc, 0) : null;
        var tailShift = (inserted == 0 ? sc + text.Length : lastSegment) - ec;
        var tail = _lines[el] is { } tr ? Keep(tr, t => t.Start >= ec, tailShift) : null;
        for (var i = sl; i <= el; i++) TokenCount -= (_lines[i]?.Length ?? 0) / Stride;
        if (el > sl) _lines.RemoveRange(sl + 1, el - sl);
        for (var i = 0; i < inserted; i++) _lines.Insert(sl + 1, null);
        if (inserted == 0) _lines[sl] = Concat(head, tail);
        else
        {
            _lines[sl] = head;
            _lines[sl + inserted] = tail;
        }
        TokenCount += ((head?.Length ?? 0) + (tail?.Length ?? 0)) / Stride;
    }

    private int[]? EditRow(int[] row, int sc, int ec, string text)
    {
        var delta = text.Length - (ec - sc);
        var grows = text.Length > 0 && text.All(c => char.IsLetterOrDigit(c) || c == '_');
        _scratch.Clear();
        for (var i = 0; i < row.Length; i += Stride)
        {
            int start = row[i], length = row[i + 1], end = start + length;
            if (end <= sc && !(end == sc && sc == ec && grows && length > 0)) // before the change (typing an identifier character at a token's end grows it)
                _scratch.AddRange(row.AsSpan(i, Stride));
            else if (start >= ec) // after it
                AddShifted(row, i, delta);
            else if (start <= sc && ec <= end) // the change is inside the token: it grows or shrinks
            {
                var newLength = length + delta;
                if (newLength > 0)
                {
                    _scratch.Add(start);
                    _scratch.Add(newLength);
                    _scratch.Add(row[i + 2]);
                    _scratch.Add(row[i + 3]);
                }
            }
            // else: the change cuts the token: dropped until the next answer
        }
        TokenCount += _scratch.Count / Stride - row.Length / Stride;
        return _scratch.Count == 0 ? null : _scratch.ToArray();
    }

    private void AddShifted(int[] row, int i, int delta)
    {
        _scratch.Add(row[i] + delta);
        _scratch.Add(row[i + 1]);
        _scratch.Add(row[i + 2]);
        _scratch.Add(row[i + 3]);
    }

    private int[]? Keep(int[] row, Func<(int Start, int Length), bool> keep, int shift)
    {
        _scratch.Clear();
        for (var i = 0; i < row.Length; i += Stride)
            if (keep((row[i], row[i + 1]))) AddShifted(row, i, shift);
        return _scratch.Count == 0 ? null : _scratch.ToArray();
    }

    private static int[]? Concat(int[]? a, int[]? b) => a is null ? b : b is null ? a : [.. a, .. b];

    /// <summary>Line breaks in <paramref name="text"/> (\r\n counts once) and the length of the text after the last one.</summary>
    internal static int CountLines(string text, out int lastSegment)
    {
        var count = 0;
        var lastBreakEnd = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                count++;
                lastBreakEnd = i + 1;
            }
            else if (c == '\n')
            {
                count++;
                lastBreakEnd = i + 1;
            }
        }
        lastSegment = text.Length - lastBreakEnd;
        return count;
    }
}
