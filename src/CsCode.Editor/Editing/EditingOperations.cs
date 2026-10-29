using System.Text;
using CsCode.Core.Languages;
using CsCode.Core.Text;

namespace CsCode.Editor.Editing;

/// <summary>What an editing command wants done: edits (against the CURRENT text, non-overlapping) and where the selection ends up (AFTER the edits).</summary>
public sealed record EditPlan(IReadOnlyList<TextEdit> Edits, TextRange Selection);

/// <summary>Language/indent facts the pure editing functions need.</summary>
public sealed record EditingContext(LanguageConfiguration Language, int TabSize = 4, bool InsertSpaces = true, string Eol = "\n", IReadOnlyList<Languages.EnterRuleEx>? EnterRules = null)
{
    public string IndentUnit => InsertSpaces ? new string(' ', TabSize) : "\t";
}

/// <summary>
/// The editing commands as pure functions: <c>(document, selection, context) → plan</c>. No UI types, no threads — the view applies a plan as
/// ONE undo unit. Each returns null when the command does not apply (nothing to do). AvaloniaEdit has a single selection, so these operate on one.
/// </summary>
public static class EditingOperations
{
    // ---------- line selection helpers ----------

    /// <summary>The lines a command acts on: the selection's lines, except that a selection ending at column 0 of a later line does not include that line.</summary>
    public static (int First, int Last) LineSpan(TextRange sel)
    {
        var first = sel.Start.Line;
        var last = sel.End.Line;
        if (last > first && sel.End.Character == 0) last--;
        return (first, last);
    }

    private static int LeadingWhitespace(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] is ' ' or '\t') i++;
        return i;
    }

    private static TextRange LineRange(ITextDocument doc, int first, int last) =>
        TextRange.FromLines(first, 0, last, doc.GetLineText(last).Length);

    // ---------- comments ----------

    public static EditPlan? ToggleLineComment(ITextDocument doc, TextRange sel, EditingContext ctx)
    {
        var (first, last) = LineSpan(sel);
        var token = ctx.Language.LineComment;
        if (string.IsNullOrEmpty(token))
        {
            // No line comment in this language (CSS, HTML…): comment each line with the block comment instead, like VS Code.
            return ctx.Language.BlockComment is { } bc ? ToggleBlockPerLine(doc, sel, first, last, bc) : null;
        }

        var lines = Enumerable.Range(first, last - first + 1).Select(doc.GetLineText).ToArray();
        var nonBlank = lines.Select((t, i) => (Text: t, Index: i)).Where(x => x.Text.Trim().Length > 0).ToArray();
        var targets = nonBlank.Length > 0 ? nonBlank : lines.Select((t, i) => (Text: t, Index: i)).ToArray();
        var allCommented = nonBlank.Length > 0 && nonBlank.All(x => x.Text.AsSpan(LeadingWhitespace(x.Text)).StartsWith(token, StringComparison.Ordinal));

        var edits = new List<TextEdit>();
        if (allCommented)
        {
            foreach (var (text, i) in nonBlank)
            {
                var col = LeadingWhitespace(text);
                var len = token.Length + (col + token.Length < text.Length && text[col + token.Length] == ' ' ? 1 : 0);
                edits.Add(TextEdit.Delete(TextRange.FromLines(first + i, col, first + i, col + len)));
            }
        }
        else
        {
            var indent = targets.Min(x => LeadingWhitespace(x.Text)); // VS Code puts the markers in one column: the shallowest indent
            foreach (var (_, i) in targets) edits.Add(TextEdit.Insert(new TextPosition(first + i, Math.Min(indent, lines[i].Length)), token + " "));
        }
        return new EditPlan(edits, MapRange(sel, edits));
    }

    private static EditPlan? ToggleBlockPerLine(ITextDocument doc, TextRange sel, int first, int last, (string Open, string Close) bc)
    {
        var edits = new List<TextEdit>();
        var lines = Enumerable.Range(first, last - first + 1).Select(doc.GetLineText).ToArray();
        var nonBlank = lines.Select((t, i) => (Text: t, Index: i)).Where(x => x.Text.Trim().Length > 0).ToArray();
        if (nonBlank.Length == 0) return null;
        var all = nonBlank.All(x => x.Text.TrimStart().StartsWith(bc.Open, StringComparison.Ordinal) && x.Text.TrimEnd().EndsWith(bc.Close, StringComparison.Ordinal));
        foreach (var (text, i) in nonBlank)
        {
            var line = first + i;
            var start = LeadingWhitespace(text);
            var end = text.TrimEnd().Length;
            if (all)
            {
                var openLen = bc.Open.Length + (text.Length > start + bc.Open.Length && text[start + bc.Open.Length] == ' ' ? 1 : 0);
                var closeLen = bc.Close.Length + (end - bc.Close.Length - 1 >= start && text[end - bc.Close.Length - 1] == ' ' ? 1 : 0);
                edits.Add(TextEdit.Delete(TextRange.FromLines(line, end - closeLen, line, end)));
                edits.Add(TextEdit.Delete(TextRange.FromLines(line, start, line, start + openLen)));
            }
            else
            {
                edits.Add(TextEdit.Insert(new TextPosition(line, end), " " + bc.Close));
                edits.Add(TextEdit.Insert(new TextPosition(line, start), bc.Open + " "));
            }
        }
        return new EditPlan(edits, MapRange(sel, edits));
    }

    /// <summary>Wraps the selection in the block comment, or unwraps it when it is already wrapped (inside or around the selection).</summary>
    public static EditPlan? ToggleBlockComment(ITextDocument doc, TextRange sel, EditingContext ctx)
    {
        if (ctx.Language.BlockComment is not { } bc) return null;
        var range = sel;
        if (sel.IsEmpty)
        {
            // no selection: comment the line's content (VS Code behaviour)
            var line = doc.GetLineText(sel.Start.Line);
            var start = LeadingWhitespace(line);
            if (line.Trim().Length == 0) return null;
            range = TextRange.FromLines(sel.Start.Line, start, sel.Start.Line, line.TrimEnd().Length);
        }
        var text = doc.GetText(range);
        // already wrapped by the selection itself
        if (text.StartsWith(bc.Open, StringComparison.Ordinal) && text.EndsWith(bc.Close, StringComparison.Ordinal) && text.Length >= bc.Open.Length + bc.Close.Length)
        {
            var inner = text[bc.Open.Length..^bc.Close.Length];
            var edit = new TextEdit(range, inner.StartsWith(' ') && inner.EndsWith(' ') && inner.Length >= 2 ? inner[1..^1] : inner);
            return new EditPlan([edit], TextRange.At(range.Start.Line, range.Start.Character) with { End = EndAfter(range.Start, edit.NewText) });
        }
        // wrapped just outside the selection: "/* " immediately before it and " */" immediately after (single spaces allowed)
        var before = doc.GetText(new TextRange(OffsetBy(doc, range.Start, -(bc.Open.Length + 1)), range.Start));
        var after = doc.GetText(new TextRange(range.End, OffsetBy(doc, range.End, bc.Close.Length + 1)));
        var openAt = before.TrimEnd(' ').EndsWith(bc.Open, StringComparison.Ordinal) ? before.TrimEnd(' ').Length - bc.Open.Length : -1;
        var closeAt = after.TrimStart(' ').StartsWith(bc.Close, StringComparison.Ordinal) ? after.Length - after.TrimStart(' ').Length : -1;
        if (openAt >= 0 && closeAt >= 0)
        {
            var openStart = OffsetBy(doc, range.Start, -(before.Length - openAt));
            var closeEnd = OffsetBy(doc, range.End, closeAt + bc.Close.Length);
            var edits = new List<TextEdit> { TextEdit.Delete(new TextRange(range.End, closeEnd)), TextEdit.Delete(new TextRange(openStart, range.Start)) };
            return new EditPlan(edits, new TextRange(openStart, EndAfter(openStart, text)));
        }
        var wrapped = bc.Open + " " + text + " " + bc.Close;
        var wrapEdit = new TextEdit(range, wrapped);
        return new EditPlan([wrapEdit], new TextRange(range.Start, EndAfter(range.Start, wrapped)));
    }

    // ---------- moving / copying / deleting lines ----------

    public static EditPlan? MoveLines(ITextDocument doc, TextRange sel, EditingContext ctx, bool down)
    {
        var (first, last) = LineSpan(sel);
        if (down ? last >= doc.LineCount - 1 : first == 0) return null;
        var from = down ? first : first - 1;
        var to = down ? last + 1 : last;
        var lines = Enumerable.Range(from, to - from + 1).Select(doc.GetLineText).ToList();
        if (down)
        {
            var moved = lines[^1];
            lines.RemoveAt(lines.Count - 1);
            lines.Insert(0, moved);
        }
        else
        {
            var moved = lines[0];
            lines.RemoveAt(0);
            lines.Add(moved);
        }
        var edit = new TextEdit(LineRange(doc, from, to), string.Join(ctx.Eol, lines));
        var shift = down ? 1 : -1;
        return new EditPlan([edit], new TextRange(new(sel.Start.Line + shift, sel.Start.Character), new(sel.End.Line + shift, sel.End.Character)));
    }

    public static EditPlan? CopyLines(ITextDocument doc, TextRange sel, EditingContext ctx, bool down)
    {
        var (first, last) = LineSpan(sel);
        var block = string.Join(ctx.Eol, Enumerable.Range(first, last - first + 1).Select(doc.GetLineText));
        var count = last - first + 1;
        if (down)
        {
            var edit = TextEdit.Insert(new TextPosition(last, doc.GetLineText(last).Length), ctx.Eol + block);
            return new EditPlan([edit], new TextRange(new(sel.Start.Line + count, sel.Start.Character), new(sel.End.Line + count, sel.End.Character)));
        }
        var up = TextEdit.Insert(new TextPosition(first, 0), block + ctx.Eol);
        return new EditPlan([up], sel); // the selection now sits on the upper copy
    }

    public static EditPlan? DeleteLines(ITextDocument doc, TextRange sel, EditingContext ctx)
    {
        var (first, last) = LineSpan(sel);
        TextRange range;
        TextPosition caret;
        if (last < doc.LineCount - 1)
        {
            range = TextRange.FromLines(first, 0, last + 1, 0);
            caret = new TextPosition(first, Math.Min(sel.Start.Character, doc.GetLineText(last + 1).Length));
        }
        else if (first > 0)
        {
            // deleting the final line(s): take the terminator BEFORE them so no empty line is left behind
            range = new TextRange(new TextPosition(first - 1, doc.GetLineText(first - 1).Length), new TextPosition(last, doc.GetLineText(last).Length));
            caret = new TextPosition(first - 1, Math.Min(sel.Start.Character, doc.GetLineText(first - 1).Length));
        }
        else
        {
            range = LineRange(doc, first, last);
            caret = TextPosition.Zero;
        }
        return new EditPlan([TextEdit.Delete(range)], new TextRange(caret, caret));
    }

    // ---------- indentation ----------

    public static EditPlan? IndentLines(ITextDocument doc, TextRange sel, EditingContext ctx)
    {
        var (first, last) = LineSpan(sel);
        var edits = new List<TextEdit>();
        for (var l = first; l <= last; l++)
            if (doc.GetLineText(l).Length > 0) edits.Add(TextEdit.Insert(new TextPosition(l, 0), ctx.IndentUnit));
        return edits.Count == 0 ? null : new EditPlan(edits, MapRange(sel, edits));
    }

    public static EditPlan? OutdentLines(ITextDocument doc, TextRange sel, EditingContext ctx)
    {
        var (first, last) = LineSpan(sel);
        var edits = new List<TextEdit>();
        for (var l = first; l <= last; l++)
        {
            var text = doc.GetLineText(l);
            if (text.Length == 0) continue;
            var remove = 0;
            if (text[0] == '\t') remove = 1;
            else
            {
                while (remove < ctx.TabSize && remove < text.Length && text[remove] == ' ') remove++;
            }
            if (remove > 0) edits.Add(TextEdit.Delete(TextRange.FromLines(l, 0, l, remove)));
        }
        return edits.Count == 0 ? null : new EditPlan(edits, MapRange(sel, edits));
    }

    // ---------- join / transpose / trim / sort ----------

    public static EditPlan? JoinLines(ITextDocument doc, TextRange sel, EditingContext ctx)
    {
        var (first, last) = LineSpan(sel);
        if (last == first) last = first + 1;
        if (last >= doc.LineCount) return null;
        // One replacement of the whole block keeps the edit list simple and the result exact.
        var sb = new StringBuilder(doc.GetLineText(first));
        var caretCol = sb.Length;
        for (var l = first + 1; l <= last; l++)
        {
            var next = doc.GetLineText(l);
            var trimmed = next.TrimStart(' ', '\t');
            var needsSpace = trimmed.Length > 0 && sb.Length > 0 && sb[^1] is not (' ' or '\t') && trimmed[0] != ')';
            if (l == first + 1) caretCol = sb.Length;
            if (needsSpace) sb.Append(' ');
            sb.Append(trimmed);
        }
        var edit = new TextEdit(LineRange(doc, first, last), sb.ToString());
        var caret = new TextPosition(first, caretCol);
        return new EditPlan([edit], new TextRange(caret, caret));
    }

    public static EditPlan? TransposeCharacters(ITextDocument doc, TextRange sel, EditingContext ctx)
    {
        if (!sel.IsEmpty) return null;
        var line = doc.GetLineText(sel.Start.Line);
        var col = sel.Start.Character;
        if (line.Length < 2 || col == 0) return null;
        var atEnd = col >= line.Length;
        var a = atEnd ? line.Length - 2 : col - 1;
        var swapped = new string([line[a + 1], line[a]]);
        var caret = new TextPosition(sel.Start.Line, atEnd ? line.Length : col + 1);
        return new EditPlan([new TextEdit(TextRange.FromLines(sel.Start.Line, a, sel.Start.Line, a + 2), swapped)], new TextRange(caret, caret));
    }

    public static EditPlan? TrimTrailingWhitespace(ITextDocument doc, TextRange sel)
    {
        var edits = new List<TextEdit>();
        for (var l = 0; l < doc.LineCount; l++)
        {
            var text = doc.GetLineText(l);
            var end = text.Length;
            while (end > 0 && text[end - 1] is ' ' or '\t') end--;
            if (end < text.Length) edits.Add(TextEdit.Delete(TextRange.FromLines(l, end, l, text.Length)));
        }
        return edits.Count == 0 ? null : new EditPlan(edits, MapRange(sel, edits));
    }

    public static EditPlan? SortLines(ITextDocument doc, TextRange sel, EditingContext ctx, bool descending)
    {
        var (first, last) = LineSpan(sel);
        if (last <= first) return null; // VS Code: sorting needs a multi-line selection
        var lines = Enumerable.Range(first, last - first + 1).Select(doc.GetLineText).ToList();
        var sorted = descending ? lines.OrderByDescending(l => l, StringComparer.Ordinal).ToList() : lines.OrderBy(l => l, StringComparer.Ordinal).ToList();
        if (sorted.SequenceEqual(lines)) return null;
        return new EditPlan([new TextEdit(LineRange(doc, first, last), string.Join(ctx.Eol, sorted))], sel);
    }

    // ---------- position mapping for plans made of single-line, non-overlapping edits ----------

    /// <summary>Where a position ends up after single-line edits (inserts/deletes within their line).</summary>
    public static TextPosition Map(TextPosition p, IReadOnlyList<TextEdit> edits)
    {
        var col = p.Character;
        foreach (var e in edits)
        {
            if (e.Range.Start.Line != p.Line || e.Range.End.Line != p.Line) continue;
            var s = e.Range.Start.Character;
            var en = e.Range.End.Character;
            if (col >= en) col += e.NewText.Length - (en - s); // at or after the edit: shifts (a caret AT an insertion point ends up after the inserted text)
            else if (col > s) col = s + e.NewText.Length; // inside a replaced range: collapse to its end
        }
        return new TextPosition(p.Line, Math.Max(0, col));
    }

    public static TextRange MapRange(TextRange r, IReadOnlyList<TextEdit> edits) => new(Map(r.Start, edits), Map(r.End, edits));

    private static TextPosition EndAfter(TextPosition start, string inserted)
    {
        var lines = inserted.Split('\n');
        return lines.Length == 1 ? new TextPosition(start.Line, start.Character + inserted.Length) : new TextPosition(start.Line + lines.Length - 1, lines[^1].Length);
    }

    private static TextPosition OffsetBy(ITextDocument doc, TextPosition p, int delta) => doc.PositionAt(Math.Clamp(doc.OffsetAt(p) + delta, 0, doc.Length));
}
