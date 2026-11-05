using AvaloniaEdit.Document;

namespace CsCode.Editor.Editing;

/// <summary>A replacement of [<see cref="Start"/>, <see cref="End"/>) by <see cref="Text"/>, in offsets of the text before any edit of its batch.</summary>
public readonly record struct OffsetEdit(int Start, int End, string Text)
{
    public int Delta => Text.Length - (End - Start);
    public bool IsNoOp => Start == End && Text.Length == 0;
}

/// <summary>
/// The edits of one multi-caret operation (sorted, non-overlapping, offsets against the text before the batch) and the carets after it. The view
/// applies <see cref="Edits"/> back to front inside one document update, so the whole batch is ONE change event and ONE undo step.
/// </summary>
public sealed record CaretEditBatch(OffsetEdit[] Edits, CaretRange[] Carets, int Primary);

/// <summary>
/// Multi-caret editing and caret movement as pure functions over AvaloniaEdit's <see cref="TextDocument"/> (read only here) and a
/// <see cref="CaretSet"/>. Per keystroke the cost is O(carets × log(document)) plus each caret's own line: nothing scans the document per caret.
/// Movement follows the editor's single-caret rules (AvaloniaEdit's word stops, VS Code's smart Home, Up/Down keeping a visual column).
/// </summary>
public static class MultiCaretOperations
{
    // ------------------------------------------------------------------ edits

    /// <summary>One edit per caret, from <paramref name="make"/>: (start, end, text, where the caret goes inside the text).</summary>
    public static CaretEditBatch PerCaret(CaretSet set, Func<CaretRange, (int Start, int End, string Text, int CaretInText)> make)
    {
        var n = set.Count;
        var raw = new (int Start, int End, string Text, int CaretInText)[n];
        for (var i = 0; i < n; i++) raw[i] = make(set[i]);
        // Carets are sorted and apart, but the ranges they edit may still overlap (two carets deleting back to one word start): such edits merge
        // into one, and every caret of a merged edit lands where the merged text puts the first of them.
        var edits = new List<OffsetEdit>(n);
        var editOf = new int[n];
        var caretIn = new int[n];
        for (var i = 0; i < n; i++)
        {
            var (start, end, text, at) = raw[i];
            at = Math.Clamp(at, 0, text.Length);
            // a range reaching back over earlier edits (a word deleted across several carets) swallows them; their carets keep their place
            // in the merged text, which starts with theirs (deletions: all empty)
            while (edits.Count > 0 && start < edits[^1].End)
            {
                var last = edits[^1];
                edits.RemoveAt(edits.Count - 1);
                at += last.Text.Length;
                text = last.Text + text;
                start = Math.Min(start, last.Start);
                end = Math.Max(end, last.End);
            }
            edits.Add(new OffsetEdit(start, end, text));
            editOf[i] = edits.Count - 1;
            caretIn[i] = at;
            for (var j = i - 1; j >= 0 && editOf[j] > edits.Count - 1; j--) editOf[j] = edits.Count - 1; // carets of swallowed edits
        }
        var editArray = edits.ToArray();
        var newStarts = NewStarts(editArray);
        var carets = new CaretRange[n];
        for (var i = 0; i < n; i++) carets[i] = CaretRange.At(newStarts[editOf[i]] + caretIn[i]);
        return new CaretEditBatch(editArray, carets, set.PrimaryIndex);
    }

    /// <summary>
    /// Edits that are not one per caret (whole lines pasted above each caret, lines indented): the carets keep their places, moved by the edits
    /// before them; a caret inside a replaced range, at its end or at an insertion point goes after the new text (selections are kept).
    /// </summary>
    public static CaretEditBatch Mapped(CaretSet set, IReadOnlyList<OffsetEdit> edits, bool keepSelections = true)
    {
        var sorted = Normalize(edits);
        var deltas = PrefixDeltas(sorted);
        var carets = new CaretRange[set.Count];
        for (var i = 0; i < set.Count; i++)
        {
            var c = set[i];
            var active = Map(sorted, deltas, c.Active);
            carets[i] = keepSelections ? new CaretRange(Map(sorted, deltas, c.Anchor), active) : CaretRange.At(active);
        }
        return new CaretEditBatch(sorted, carets, set.PrimaryIndex);
    }

    /// <summary>Sorted by start, no-ops dropped; overlapping edits are refused (an exception: the callers build disjoint ones).</summary>
    private static OffsetEdit[] Normalize(IReadOnlyList<OffsetEdit> edits)
    {
        var list = edits.Where(e => !e.IsNoOp).OrderBy(e => e.Start).ThenBy(e => e.End).ToArray();
        for (var i = 1; i < list.Length; i++)
            if (list[i].Start < list[i - 1].End) throw new ArgumentException("Overlapping edits.", nameof(edits));
        return list;
    }

    /// <summary>deltas[i] = total length change of edits[0..i) (so edit i starts at edits[i].Start + deltas[i] afterwards).</summary>
    private static int[] PrefixDeltas(OffsetEdit[] edits)
    {
        var deltas = new int[edits.Length + 1];
        for (var i = 0; i < edits.Length; i++) deltas[i + 1] = deltas[i] + edits[i].Delta;
        return deltas;
    }

    private static int[] NewStarts(OffsetEdit[] edits)
    {
        var starts = new int[edits.Length];
        var delta = 0;
        for (var i = 0; i < edits.Length; i++)
        {
            starts[i] = edits[i].Start + delta;
            delta += edits[i].Delta;
        }
        return starts;
    }

    /// <summary>Where <paramref name="offset"/> is after the edits (binary search; see <see cref="Mapped"/> for the rules).</summary>
    public static int Map(OffsetEdit[] edits, int[] deltas, int offset)
    {
        // the last edit that starts at or before the offset
        int lo = 0, hi = edits.Length - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (edits[mid].Start <= offset) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return offset;
        var e = edits[found];
        if (offset == e.Start && e.Start < e.End) return e.Start + deltas[found]; // a deletion starting here: stays at its start
        if (offset <= e.End) return e.Start + deltas[found] + e.Text.Length;      // inside, at the end, or at an insertion point: after the new text
        return offset + deltas[found + 1];
    }

    /// <summary>Applies a batch's edits to the buffer, back to front (offsets stay valid), inside one update: ONE change event, ONE undo step.</summary>
    public static void Apply(TextDocument doc, IReadOnlyList<OffsetEdit> edits)
    {
        if (edits.Count == 0) return;
        doc.BeginUpdate();
        try
        {
            for (var i = edits.Count - 1; i >= 0; i--)
            {
                var e = edits[i];
                if (e.IsNoOp) continue;
                doc.Replace(e.Start, e.End - e.Start, e.Text);
            }
        }
        finally
        {
            doc.EndUpdate();
        }
    }

    /// <summary>Typed text replaces every caret's selection; each caret ends after its text.</summary>
    public static CaretEditBatch Type(CaretSet set, string text) =>
        PerCaret(set, c => (c.Start, c.End, text, text.Length));

    /// <summary>One text per caret (a paste spread over the carets): <paramref name="texts"/>[i] replaces caret i's selection.</summary>
    public static CaretEditBatch Insert(CaretSet set, IReadOnlyList<string> texts)
    {
        var i = 0;
        return PerCaret(set, c =>
        {
            var t = texts[i++];
            return (c.Start, c.End, t, t.Length);
        });
    }

    /// <summary>
    /// Enter at every caret: the document's line ending plus the indentation the language's Enter rules give (as the single-caret Enter), the
    /// whitespace after the caret removed; between brackets the closer moves to its own line and the caret stays on the indented one.
    /// </summary>
    public static CaretEditBatch Enter(TextDocument doc, CaretSet set, EditingContext ctx) =>
        PerCaret(set, c =>
        {
            var line = doc.GetLineByOffset(c.Start);
            var before = doc.GetText(line.Offset, c.Start - line.Offset);
            var endLine = doc.GetLineByOffset(c.End);
            var after = doc.GetText(c.End, endLine.EndOffset - c.End);
            var previous = line.PreviousLine is { } p ? doc.GetText(p.Offset, p.Length) : null;
            var result = AutoIndent.ForEnter(before, after, ctx, previous);
            var skip = 0;
            while (skip < after.Length && after[skip] is ' ' or '\t') skip++;
            var head = ctx.Eol + result.NewLineIndent + result.AppendText;
            var text = result.ClosingLineIndent is { } closing ? head + ctx.Eol + closing : head;
            return (c.Start, c.End + skip, text, head.Length);
        });

    /// <summary>Backspace: each selection is deleted, each caret deletes the character (a surrogate pair, a CRLF) before it, or the word with <paramref name="word"/>.</summary>
    public static CaretEditBatch Backspace(TextDocument doc, CaretSet set, bool word) =>
        PerCaret(set, c =>
        {
            if (!c.IsEmpty) return (c.Start, c.End, "", 0);
            var from = word ? WordLeft(doc, c.Start) : PreviousCharOffset(doc, c.Start);
            return (from, c.Start, "", 0);
        });

    /// <summary>Delete: each selection is deleted, each caret deletes the character after it, or up to the next word with <paramref name="word"/>.</summary>
    public static CaretEditBatch Delete(TextDocument doc, CaretSet set, bool word) =>
        PerCaret(set, c =>
        {
            if (!c.IsEmpty) return (c.Start, c.End, "", 0);
            var to = word ? WordRight(doc, c.Start) : NextCharOffset(doc, c.Start);
            return (c.Start, to, "", 0);
        });

    /// <summary>
    /// Tab: a caret or a selection within one line is replaced by indentation to the next tab stop (spaces, or a tab); a selection over several
    /// lines indents those lines by one unit instead (selections kept).
    /// </summary>
    public static CaretEditBatch Tab(TextDocument doc, CaretSet set, EditingContext ctx)
    {
        if (set.Carets.Any(c => IsMultiLine(doc, c))) return IndentLines(doc, set, ctx, outdent: false);
        return PerCaret(set, c =>
        {
            string text;
            if (ctx.InsertSpaces)
            {
                var line = doc.GetLineByOffset(c.Start);
                var column = VisualColumn(doc, line, c.Start, ctx.TabSize);
                text = new string(' ', ctx.TabSize - column % ctx.TabSize);
            }
            else text = "\t";
            return (c.Start, c.End, text, text.Length);
        });
    }

    /// <summary>Indents (or, Shift+Tab, outdents) every line any caret touches by one unit; each line once, the carets keeping their places.</summary>
    public static CaretEditBatch IndentLines(TextDocument doc, CaretSet set, EditingContext ctx, bool outdent)
    {
        var edits = new List<OffsetEdit>();
        var lastLine = 0;
        foreach (var c in set.Carets)
        {
            var first = doc.GetLineByOffset(c.Start).LineNumber;
            var endLine = doc.GetLineByOffset(c.End);
            var last = endLine.LineNumber;
            if (last > first && c.End == endLine.Offset) last--; // a selection ending at a line's start does not take that line
            for (var n = Math.Max(first, lastLine + 1); n <= last; n++)
            {
                var line = doc.GetLineByNumber(n);
                if (outdent)
                {
                    var remove = OutdentLength(doc, line, ctx.TabSize);
                    if (remove > 0) edits.Add(new OffsetEdit(line.Offset, line.Offset + remove, ""));
                }
                else if (line.Length > 0 || first == last) edits.Add(new OffsetEdit(line.Offset, line.Offset, ctx.IndentUnit));
            }
            lastLine = Math.Max(lastLine, last);
        }
        var batch = Mapped(set, edits);
        if (outdent || edits.Count == 0) return batch;
        // an insertion at a selection's start pushes the start along with it: keep the start where the line now begins
        var carets = batch.Carets;
        for (var i = 0; i < carets.Length; i++)
        {
            var before = set[i];
            if (before.IsEmpty) continue;
            var startLine = doc.GetLineByOffset(before.Start);
            if (before.Start != startLine.Offset) continue;
            var mappedStart = Math.Min(carets[i].Anchor, carets[i].Active);
            var lineStart = mappedStart - ctx.IndentUnit.Length;
            if (lineStart < 0 || !edits.Any(e => e.Start == before.Start)) continue;
            carets[i] = before.IsReversed ? new CaretRange(Math.Max(carets[i].Anchor, carets[i].Active), lineStart) : new CaretRange(lineStart, Math.Max(carets[i].Anchor, carets[i].Active));
        }
        return batch;
    }

    private static int OutdentLength(TextDocument doc, DocumentLine line, int tabSize)
    {
        var i = 0;
        var width = 0;
        while (i < line.Length && width < tabSize)
        {
            var ch = doc.GetCharAt(line.Offset + i);
            if (ch == '\t') { i++; break; }
            if (ch != ' ') break;
            width++;
            i++;
        }
        return i;
    }

    private static bool IsMultiLine(TextDocument doc, CaretRange c) => !c.IsEmpty && doc.GetLineByOffset(c.Start).LineNumber != doc.GetLineByOffset(c.End).LineNumber;

    // ------------------------------------------------------------------ typing with auto-closing, Backspace to tab stops (multi-cursor round 2)

    /// <summary>
    /// A typed character at every caret, each caret doing what <paramref name="decisions"/>[i] says (decided from that caret's own context, as
    /// a single caret there would): <see cref="AutoCloseKind.InsertPair"/> types the character and its closer with the caret between them,
    /// <see cref="AutoCloseKind.OverType"/> steps over the closer after the caret (no edit), <see cref="AutoCloseKind.Surround"/> wraps the
    /// selection in the pair and keeps the inner text selected, <see cref="AutoCloseKind.None"/> replaces the selection with the character.
    /// </summary>
    public static CaretEditBatch TypeWithDecisions(CaretSet set, string text, IReadOnlyList<AutoCloseDecision> decisions)
    {
        var edits = new List<OffsetEdit>(set.Count + 2);
        var carets = new CaretRange[set.Count];
        var delta = 0;
        for (var i = 0; i < set.Count; i++)
        {
            var c = set[i];
            var d = decisions[i];
            switch (d.Kind)
            {
                case AutoCloseKind.InsertPair when c.IsEmpty:
                    edits.Add(new OffsetEdit(c.Start, c.Start, text + d.Close));
                    carets[i] = CaretRange.At(c.Start + delta + text.Length);
                    delta += text.Length + d.Close.Length;
                    break;
                case AutoCloseKind.OverType when c.IsEmpty:
                    carets[i] = CaretRange.At(c.Start + delta + text.Length);
                    break;
                case AutoCloseKind.Surround when !c.IsEmpty:
                {
                    edits.Add(new OffsetEdit(c.Start, c.Start, d.Open));
                    edits.Add(new OffsetEdit(c.End, c.End, d.Close));
                    var start = c.Start + delta + d.Open.Length;
                    var end = c.End + delta + d.Open.Length;
                    carets[i] = c.IsReversed ? new CaretRange(end, start) : new CaretRange(start, end);
                    delta += d.Open.Length + d.Close.Length;
                    break;
                }
                default:
                    edits.Add(new OffsetEdit(c.Start, c.End, text));
                    carets[i] = CaretRange.At(c.Start + delta + text.Length);
                    delta += text.Length - c.Length;
                    break;
            }
        }
        return new CaretEditBatch(edits.ToArray(), carets, set.PrimaryIndex);
    }

    /// <summary>
    /// Backspace at every caret with the editor's rules: a selection is deleted; a caret between an auto-inserted pair
    /// (<paramref name="autoClosedPairAt"/>: the closer at that offset was inserted by auto-closing and the character before is its opener)
    /// deletes both; with <paramref name="useTabStops"/> (VS Code's <c>editor.useTabStops</c>) a caret in a line's leading whitespace deletes
    /// back to the previous indentation stop; otherwise one character (a CRLF or a surrogate pair is one), or a word with <paramref name="word"/>.
    /// </summary>
    public static CaretEditBatch Backspace(TextDocument doc, CaretSet set, bool word, int tabSize, bool useTabStops, Func<int, bool>? autoClosedPairAt = null) =>
        PerCaret(set, c =>
        {
            if (!c.IsEmpty) return (c.Start, c.End, "", 0);
            if (word) return (WordLeft(doc, c.Start), c.Start, "", 0);
            if (autoClosedPairAt is not null && c.Start > 0 && c.Start < doc.TextLength && autoClosedPairAt(c.Start)) return (c.Start - 1, c.Start + 1, "", 0);
            if (useTabStops && IndentStopBefore(doc, c.Start, tabSize) is { } stop) return (stop, c.Start, "", 0);
            return (PreviousCharOffset(doc, c.Start), c.Start, "", 0);
        });

    /// <summary>
    /// Where Backspace at <paramref name="offset"/> deletes back to when the caret is in its line's leading whitespace (everything before it on
    /// the line is spaces or tabs): the previous indentation stop, a multiple of <paramref name="tabSize"/> visual columns (VS Code's
    /// <c>prevIndentTabStop</c>), so "        |" loses four spaces and "      |" two. Null when the caret is at the line start or after
    /// non-blank text (Backspace then deletes one character).
    /// </summary>
    public static int? IndentStopBefore(TextDocument doc, int offset, int tabSize)
    {
        var line = doc.GetLineByOffset(offset);
        if (offset <= line.Offset) return null;
        for (var i = line.Offset; i < offset; i++)
            if (doc.GetCharAt(i) is not (' ' or '\t')) return null;
        tabSize = Math.Max(1, tabSize);
        var column = VisualColumn(doc, line, offset, tabSize);
        var target = Math.Max(0, column - 1 - (column - 1) % tabSize);
        var to = OffsetAtVisualColumn(doc, line, target, tabSize);
        if (to >= offset) to = PreviousCharOffset(doc, offset); // a tab spanning the stop: never delete nothing
        return to;
    }

    /// <summary>
    /// Up / Down with word wrap: by one visual row, aiming at each caret's desired column within the row (tabs expanded from the line's start,
    /// counted from the row's start). <paramref name="rowStarts"/> gives the document offsets where each row of a line starts (the first is the
    /// line's own start; one entry for a line that does not wrap). A caret at a wrap point belongs to the row that starts there. The first row's
    /// Up goes to the document's start, the last row's Down to its end, as in <see cref="MoveVertical(TextDocument, CaretSet, bool, bool, int)"/>.
    /// </summary>
    public static CaretSet MoveVertical(TextDocument doc, CaretSet set, bool down, bool extend, int tabSize, Func<DocumentLine, IReadOnlyList<int>> rowStarts) =>
        Move(set, c =>
        {
            var line = doc.GetLineByOffset(c.Active);
            var rows = rowStarts(line);
            var row = RowIndexOf(rows, c.Active);
            var rowStart = rows[row];
            var column = c.DesiredColumn >= 0 ? c.DesiredColumn : VisualColumn(doc, line, c.Active, tabSize) - VisualColumn(doc, line, rowStart, tabSize);
            DocumentLine? targetLine = line;
            var targetRow = row + (down ? 1 : -1);
            IReadOnlyList<int> targetRows = rows;
            if (targetRow < 0 || targetRow >= rows.Count)
            {
                targetLine = down ? line.NextLine : line.PreviousLine;
                if (targetLine is not null)
                {
                    targetRows = rowStarts(targetLine);
                    targetRow = down ? 0 : targetRows.Count - 1;
                }
            }
            int to, desired;
            if (targetLine is null)
            {
                to = down ? doc.TextLength : 0;
                desired = -1;
            }
            else
            {
                var start = targetRows[targetRow];
                var lastRow = targetRow == targetRows.Count - 1;
                var end = lastRow ? targetLine.EndOffset : targetRows[targetRow + 1];
                var baseColumn = VisualColumn(doc, targetLine, start, tabSize);
                to = OffsetAtVisualColumn(doc, targetLine, baseColumn + column, tabSize);
                // the end of a row that wraps is the start of the next one, where the caret would belong to that row: stop before its last character
                to = Math.Clamp(to, start, lastRow || end <= start ? end : Math.Max(start, end - 1));
                desired = column;
            }
            return extend ? new CaretRange(c.Anchor, to, desired) : new CaretRange(to, to, desired);
        });

    /// <summary>The row of <paramref name="offset"/>: the last one starting at or before it.</summary>
    private static int RowIndexOf(IReadOnlyList<int> rowStarts, int offset)
    {
        var row = 0;
        for (var i = 1; i < rowStarts.Count && rowStarts[i] <= offset; i++) row = i;
        return row;
    }

    // ------------------------------------------------------------------ movement

    /// <summary>Left / Right (by a character, or a word); with <paramref name="extend"/> the selection grows, else a selection collapses to its side.</summary>
    public static CaretSet MoveHorizontal(TextDocument doc, CaretSet set, bool forward, bool word, bool extend) =>
        Move(set, c =>
        {
            if (!extend && !word && !c.IsEmpty) return CaretRange.At(forward ? c.End : c.Start);
            var to = word
                ? forward ? WordRight(doc, c.Active) : WordLeft(doc, c.Active)
                : forward ? NextCharOffset(doc, c.Active) : PreviousCharOffset(doc, c.Active);
            return extend ? new CaretRange(c.Anchor, to) : CaretRange.At(to);
        });

    /// <summary>Up / Down by one line, aiming at each caret's desired visual column; the first line's Up goes to its start, the last line's Down to its end.</summary>
    public static CaretSet MoveVertical(TextDocument doc, CaretSet set, bool down, bool extend, int tabSize) =>
        Move(set, c =>
        {
            var line = doc.GetLineByOffset(c.Active);
            var column = c.DesiredColumn >= 0 ? c.DesiredColumn : VisualColumn(doc, line, c.Active, tabSize);
            var target = down ? line.NextLine : line.PreviousLine;
            var to = target is null ? (down ? line.EndOffset : line.Offset) : OffsetAtVisualColumn(doc, target, column, tabSize);
            var desired = target is null ? -1 : column;
            return extend ? new CaretRange(c.Anchor, to, desired) : new CaretRange(to, to, desired);
        });

    /// <summary>Home: to the first non-blank character, or to column 0 when already there (VS Code's and Visual Studio's smart Home).</summary>
    public static CaretSet MoveHome(TextDocument doc, CaretSet set, bool extend) =>
        Move(set, c =>
        {
            var line = doc.GetLineByOffset(c.Active);
            var indent = line.Offset;
            while (indent < line.EndOffset && doc.GetCharAt(indent) is ' ' or '\t') indent++;
            var to = c.Active == indent ? line.Offset : indent;
            return extend ? new CaretRange(c.Anchor, to) : CaretRange.At(to);
        });

    public static CaretSet MoveEnd(TextDocument doc, CaretSet set, bool extend) =>
        Move(set, c =>
        {
            var to = doc.GetLineByOffset(c.Active).EndOffset;
            return extend ? new CaretRange(c.Anchor, to) : CaretRange.At(to);
        });

    private static CaretSet Move(CaretSet set, Func<CaretRange, CaretRange> move)
    {
        var moved = new CaretRange[set.Count];
        for (var i = 0; i < moved.Length; i++) moved[i] = move(set[i]);
        return CaretSet.Create(moved, set.PrimaryIndex);
    }

    // ------------------------------------------------------------------ inserting carets above / below

    /// <summary>
    /// A new caret on the line above (or below) each caret, at the same visual column (VS Code's Ctrl+Alt+Up/Down). The new caret nearest the
    /// direction of travel becomes the primary, so repeating the command keeps extending the column.
    /// </summary>
    public static CaretSet InsertCaretsVertically(TextDocument doc, CaretSet set, bool below, int tabSize, int limit)
    {
        var list = new List<CaretRange>(set.Carets);
        var added = -1;
        var addedOffset = below ? int.MinValue : int.MaxValue;
        foreach (var c in set.Carets)
        {
            var line = doc.GetLineByOffset(c.Active);
            var target = below ? line.NextLine : line.PreviousLine;
            if (target is null) continue;
            var column = c.DesiredColumn >= 0 ? c.DesiredColumn : VisualColumn(doc, line, c.Active, tabSize);
            var at = OffsetAtVisualColumn(doc, target, column, tabSize);
            list.Add(new CaretRange(at, at, column));
            if (below ? at > addedOffset : at < addedOffset)
            {
                addedOffset = at;
                added = list.Count - 1;
            }
        }
        return added < 0 ? set : CaretSet.Create(list, added, limit);
    }

    // ------------------------------------------------------------------ text units

    /// <summary>The offset one character before <paramref name="offset"/>: a CRLF and a surrogate pair are one character.</summary>
    public static int PreviousCharOffset(TextDocument doc, int offset)
    {
        if (offset <= 0) return 0;
        var ch = doc.GetCharAt(offset - 1);
        if (offset >= 2)
        {
            var before = doc.GetCharAt(offset - 2);
            if (ch == '\n' && before == '\r') return offset - 2;
            if (char.IsLowSurrogate(ch) && char.IsHighSurrogate(before)) return offset - 2;
        }
        return offset - 1;
    }

    public static int NextCharOffset(TextDocument doc, int offset)
    {
        var length = doc.TextLength;
        if (offset >= length) return length;
        var ch = doc.GetCharAt(offset);
        if (offset + 1 < length)
        {
            var after = doc.GetCharAt(offset + 1);
            if (ch == '\r' && after == '\n') return offset + 2;
            if (char.IsHighSurrogate(ch) && char.IsLowSurrogate(after)) return offset + 2;
        }
        return offset + 1;
    }

    /// <summary>The word stop before <paramref name="offset"/>, as AvaloniaEdit's own Ctrl+Left finds it (start of the document when none).</summary>
    public static int WordLeft(TextDocument doc, int offset)
    {
        var to = TextUtilities.GetNextCaretPosition(doc, offset, LogicalDirection.Backward, CaretPositioningMode.WordStart);
        return to < 0 ? 0 : to;
    }

    /// <summary>The word stop after <paramref name="offset"/>, as AvaloniaEdit's own Ctrl+Right finds it (end of the document when none).</summary>
    public static int WordRight(TextDocument doc, int offset)
    {
        var to = TextUtilities.GetNextCaretPosition(doc, offset, LogicalDirection.Forward, CaretPositioningMode.WordStart);
        return to < 0 ? doc.TextLength : to;
    }

    /// <summary>The visual column of <paramref name="offset"/> on <paramref name="line"/>, tabs expanded to <paramref name="tabSize"/>.</summary>
    public static int VisualColumn(TextDocument doc, DocumentLine line, int offset, int tabSize)
    {
        var column = 0;
        var end = Math.Min(offset, line.EndOffset);
        for (var i = line.Offset; i < end; i++) column = doc.GetCharAt(i) == '\t' ? column + tabSize - column % tabSize : column + 1;
        return column;
    }

    /// <summary>The offset on <paramref name="line"/> nearest visual column <paramref name="column"/> (its end when the line is shorter).</summary>
    public static int OffsetAtVisualColumn(TextDocument doc, DocumentLine line, int column, int tabSize)
    {
        var current = 0;
        for (var i = line.Offset; i < line.EndOffset; i++)
        {
            if (current >= column) return i;
            var next = doc.GetCharAt(i) == '\t' ? current + tabSize - current % tabSize : current + 1;
            if (next > column) return column - current <= next - column ? i : i + 1; // inside a tab: the nearer side
            current = next;
        }
        return line.EndOffset;
    }
}
