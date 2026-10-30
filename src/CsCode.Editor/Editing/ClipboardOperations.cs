using System.Text;
using CsCode.Core.Platform;
using CsCode.Core.Text;

namespace CsCode.Editor.Editing;

/// <summary>
/// The selections a clipboard command acts on: one stream selection (or caret), or the rows of a box (column) selection. A box whose rows are
/// all empty is a column of carets (Visual Studio's multi-caret: Alt+drag down), which is how this editor expresses several carets.
/// </summary>
public sealed record SelectionSet(IReadOnlyList<TextRange> Ranges, bool IsBlock = false)
{
    public static SelectionSet Single(TextRange range) => new([range]);
    public bool IsMultiCaret => Ranges.Count > 1;
}

/// <summary>A paste plan plus the range the pasted text occupies afterwards (null for multi-caret and block pastes): what the clipboard ring replaces.</summary>
public sealed record PasteResult(EditPlan Plan, TextRange? Inserted);

/// <summary>
/// Visual Studio's clipboard behaviours as pure functions over a document, a selection set and the clipboard text (no UI, no clipboard access):
/// <list type="bullet">
/// <item>Copy / Cut with an empty selection take the whole line (<c>editor.emptySelectionClipboard</c>), and pasting such a line inserts it above the caret's line;</item>
/// <item>several carets (a box's rows) copy one line per caret and paste one line per caret when the counts match, otherwise the whole text at each;</item>
/// <item>a box selection copies as a block and a block pastes as a block (rows below the caret, at its visual column, padding short lines);</item>
/// <item>line endings are converted to the document's EOL on paste;</item>
/// <item>a multi-line paste keeps its lines' relative indentation and is re-indented to where it lands (auto-indent).</item>
/// </list>
/// Every plan is applied by the view as ONE undo unit.
/// </summary>
public static class ClipboardOperations
{
    // ------------------------------------------------------------------ copy

    /// <summary>What Copy (and Cut) put on the clipboard; null when there is nothing to copy (empty selection with the line mode off).</summary>
    public static ClipboardText? Copy(ITextDocument doc, SelectionSet selection, EditingContext ctx, bool emptySelectionClipboard = true)
    {
        var ranges = selection.Ranges;
        if (ranges.Count == 0) return null;
        var eol = ctx.Eol;
        var sourceIndent = Measure(doc.GetLineText(ranges[0].Start.Line), ctx.TabSize);
        if (ranges.Count > 1)
        {
            if (ranges.All(r => r.IsEmpty))
            {
                if (!emptySelectionClipboard) return null;
                var lines = CaretLines(ranges).Select(l => doc.GetLineText(l) + eol).ToArray();
                return new ClipboardText(string.Concat(lines), ClipboardTextShape.FullLines, lines) { SourceIndent = sourceIndent };
            }
            var pieces = ranges.Select(doc.GetText).ToArray();
            return new ClipboardText(string.Join(eol, pieces), ClipboardTextShape.Block, pieces) { SourceIndent = sourceIndent };
        }
        var range = ranges[0];
        if (!range.IsEmpty)
        {
            var text = doc.GetText(range);
            return selection.IsBlock
                ? new ClipboardText(text, ClipboardTextShape.Block, [text]) { SourceIndent = sourceIndent }
                : new ClipboardText(text) { SourceIndent = sourceIndent };
        }
        if (!emptySelectionClipboard) return null;
        return new ClipboardText(doc.GetLineText(range.Start.Line) + eol, ClipboardTextShape.FullLines) { SourceIndent = sourceIndent };
    }

    /// <summary>The edits of Cut: the selections, or (empty selection, line mode on) the carets' whole lines. Null when there is nothing to cut.</summary>
    public static EditPlan? Cut(ITextDocument doc, SelectionSet selection, bool emptySelectionClipboard = true)
    {
        var ranges = selection.Ranges;
        if (ranges.Count == 0) return null;
        if (ranges.Any(r => !r.IsEmpty))
        {
            var edits = ranges.Where(r => !r.IsEmpty).Select(TextEdit.Delete).ToArray();
            var start = ranges.MinBy(r => r.Start)!.Start;
            return new EditPlan(edits, new TextRange(start, start));
        }
        if (!emptySelectionClipboard) return null;
        var lines = CaretLines(ranges);
        var deletes = new List<TextEdit>();
        foreach (var line in lines)
        {
            if (line < doc.LineCount - 1) deletes.Add(TextEdit.Delete(TextRange.FromLines(line, 0, line + 1, 0)));
            else if (line > 0 && !lines.Contains(line - 1)) deletes.Add(TextEdit.Delete(TextRange.FromLines(line - 1, doc.GetLineText(line - 1).Length, line, doc.GetLineText(line).Length)));
            else deletes.Add(TextEdit.Delete(TextRange.FromLines(line, 0, line, doc.GetLineText(line).Length)));
        }
        // The caret keeps its column on the line that moves up into the (first) cut line's place, or on the new last line.
        var first = lines[0];
        var column = ranges.MinBy(r => r.Start)!.Start.Character;
        int caretLine;
        string landing;
        if (first + 1 < doc.LineCount && !lines.Contains(first + 1)) { caretLine = first; landing = doc.GetLineText(first + 1); }
        else if (lines.Count == 1 && first > 0 && first == doc.LineCount - 1) { caretLine = first - 1; landing = doc.GetLineText(first - 1); }
        else { caretLine = first; landing = ""; }
        return new EditPlan(deletes, TextRange.At(caretLine, Math.Min(column, landing.Length)));
    }

    private static List<int> CaretLines(IReadOnlyList<TextRange> ranges) => ranges.Select(r => r.Start.Line).Distinct().Order().ToList();

    // ------------------------------------------------------------------ paste

    /// <summary>The edits of pasting <paramref name="clip"/> over <paramref name="selection"/>. Null for an empty clipboard text.</summary>
    public static PasteResult? Paste(ITextDocument doc, SelectionSet selection, ClipboardText clip, EditingContext ctx, bool autoIndent = true)
    {
        var ranges = selection.Ranges;
        if (ranges.Count == 0 || clip.Text.Length == 0) return null;
        var eol = ctx.Eol;
        var text = ClipboardTextExtensions.NormalizeLineEndings(clip.Text, eol);
        var pieces = clip.Pieces?.Select(p => ClipboardTextExtensions.NormalizeLineEndings(p, eol)).ToArray();

        if (ranges.Count > 1) return PasteMulti(doc, ranges, clip.Shape, text, pieces, eol);

        var range = ranges[0];
        if (clip.Shape == ClipboardTextShape.FullLines && range.IsEmpty) return PasteLinesAbove(doc, range.Start, text, clip.SourceIndent, ctx, autoIndent);
        if (clip.Shape == ClipboardTextShape.Block && (range.IsEmpty || range.Start.Line == range.End.Line))
            return PasteBlock(doc, range, pieces is { Length: > 0 } ? pieces : ClipboardTextExtensions.SplitLines(text), ctx);

        var insert = autoIndent ? ReindentInline(doc, range.Start, text, clip.SourceIndent, ctx) : text;
        var end = EndOf(range.Start, insert, eol);
        return new PasteResult(new EditPlan([new TextEdit(range, insert)], new TextRange(end, end)), new TextRange(range.Start, end));
    }

    /// <summary>Several carets (a box's rows): one line (or piece) per caret when the counts match, a single line at every caret, else the whole text at each.</summary>
    private static PasteResult PasteMulti(ITextDocument doc, IReadOnlyList<TextRange> ranges, ClipboardTextShape shape, string text, string[]? pieces, string eol)
    {
        var count = ranges.Count;
        if (shape == ClipboardTextShape.FullLines && ranges.All(r => r.IsEmpty))
        {
            // Whole lines go above each caret's line: its own line when there is one per caret, else all of them.
            var lines = CaretLines(ranges);
            var perLine = pieces is { } p && p.Length == lines.Count ? p : null;
            var inserts = lines.Select((l, i) => TextEdit.Insert(new TextPosition(l, 0), perLine?[i] ?? text)).ToArray();
            var added = inserts.Sum(e => ClipboardTextExtensions.SplitLines(e.NewText).Length - 1);
            var last = ranges.MaxBy(r => r.Start)!.Start;
            return new PasteResult(new EditPlan(inserts, TextRange.At(last.Line + added, last.Character)), null);
        }
        string[]? rows = null;
        if (pieces is { } pc && pc.Length == count) rows = pc;
        else
        {
            var lines = ClipboardTextExtensions.SplitLines(text);
            if (lines.Length == count) rows = lines;
            else if (lines.Length == count + 1 && lines[^1].Length == 0) rows = lines[..^1];
            else if (lines.Length == 1) rows = Enumerable.Repeat(text, count).ToArray();
        }
        var edits = new TextEdit[count];
        for (var i = 0; i < count; i++) edits[i] = new TextEdit(ranges[i], rows?[i] ?? text);
        // The caret ends after the last row's paste (a column of carets is not kept: this editor has a single caret).
        var lastIndex = Enumerable.Range(0, count).MaxBy(i => ranges[i].Start);
        var lastStart = ranges[lastIndex].Start;
        var shift = 0;
        for (var i = 0; i < count; i++)
            if (ranges[i].Start < lastStart) shift += ClipboardTextExtensions.SplitLines(edits[i].NewText).Length - 1 - (ranges[i].End.Line - ranges[i].Start.Line);
        var endPos = EndOf(new TextPosition(lastStart.Line + shift, lastStart.Character), edits[lastIndex].NewText, eol);
        return new PasteResult(new EditPlan(edits, new TextRange(endPos, endPos)), null);
    }

    /// <summary>Whole lines above the caret's line; the caret stays on its own line (now below them), at its column.</summary>
    private static PasteResult PasteLinesAbove(ITextDocument doc, TextPosition caret, string text, int? sourceIndent, EditingContext ctx, bool autoIndent)
    {
        var eol = ctx.Eol;
        if (!text.EndsWith(eol, StringComparison.Ordinal)) text += eol;
        if (autoIndent && TargetIndentForLineAbove(doc, caret.Line, ctx) is { } target)
        {
            var lines = ClipboardTextExtensions.SplitLines(text);
            var body = lines[..^1]; // the final empty string after the last line ending
            var basis = sourceIndent ?? MinIndent(body, ctx.TabSize, 0);
            if (basis is { } b) text = string.Join(eol, Reindent(body, 0, b, target, ctx)) + eol;
        }
        var added = ClipboardTextExtensions.SplitLines(text).Length - 1;
        var at = new TextPosition(caret.Line, 0);
        return new PasteResult(new EditPlan([TextEdit.Insert(at, text)], TextRange.At(caret.Line + added, caret.Character)),
            new TextRange(at, new TextPosition(caret.Line + added, 0)));
    }

    /// <summary>
    /// Where a line inserted above <paramref name="line"/> belongs: the indentation Enter would give after the line above (language rules), or the
    /// line's own indentation when the line above is blank; null (no re-indent) when both are blank.
    /// </summary>
    private static int? TargetIndentForLineAbove(ITextDocument doc, int line, EditingContext ctx)
    {
        if (line > 0)
        {
            var previous = doc.GetLineText(line - 1);
            if (previous.Trim().Length > 0) return Measure(AutoIndent.ForEnter(previous, "", ctx, line > 1 ? doc.GetLineText(line - 2) : null).NewLineIndent, ctx.TabSize);
        }
        var own = line < doc.LineCount ? doc.GetLineText(line) : "";
        return own.Trim().Length > 0 ? Measure(own, ctx.TabSize) : null;
    }

    /// <summary>A block: row i goes to line caret+i at the caret's visual column (short lines are padded with spaces, missing lines appended).</summary>
    private static PasteResult PasteBlock(ITextDocument doc, TextRange range, string[] rows, EditingContext ctx)
    {
        var eol = ctx.Eol;
        var tab = ctx.TabSize;
        var startLine = range.Start.Line;
        var column = VisualColumn(doc.GetLineText(startLine), range.Start.Character, tab);
        var edits = new List<TextEdit>();
        var appended = new StringBuilder();
        TextPosition end = range.Start;
        for (var i = 0; i < rows.Length; i++)
        {
            var line = startLine + i;
            if (line < doc.LineCount)
            {
                var text = doc.GetLineText(line);
                var (index, pad) = IndexAtVisualColumn(text, column, tab);
                var insert = new string(' ', pad) + rows[i];
                if (i == 0 && !range.IsEmpty) edits.Add(new TextEdit(range, rows[i]));
                else edits.Add(TextEdit.Insert(new TextPosition(line, index), insert));
                end = new TextPosition(line, (i == 0 && !range.IsEmpty ? range.Start.Character : index) + (i == 0 && !range.IsEmpty ? rows[i].Length : insert.Length));
            }
            else
            {
                appended.Append(eol).Append(' ', column).Append(rows[i]);
                end = new TextPosition(line, column + rows[i].Length);
            }
        }
        if (appended.Length > 0)
        {
            var last = doc.LineCount - 1;
            edits.Add(TextEdit.Insert(new TextPosition(last, doc.GetLineText(last).Length), appended.ToString()));
        }
        return new PasteResult(new EditPlan(edits, new TextRange(end, end)), null);
    }

    // ------------------------------------------------------------------ auto-indent on paste

    /// <summary>
    /// Re-indents a multi-line text pasted at <paramref name="at"/>: the lines after the first keep their indentation relative to the source line
    /// (<paramref name="sourceIndent"/>, else inferred), re-based on where the paste lands — the caret's column when only whitespace precedes it
    /// (the first line's own leading whitespace is then dropped: the caret already stands at the indentation), else the line's indentation.
    /// </summary>
    internal static string ReindentInline(ITextDocument doc, TextPosition at, string text, int? sourceIndent, EditingContext ctx)
    {
        var lines = ClipboardTextExtensions.SplitLines(text);
        if (lines.Length < 2) return text;
        var tab = ctx.TabSize;
        var lineText = doc.GetLineText(at.Line);
        var prefix = lineText[..Math.Min(at.Character, lineText.Length)];
        var prefixBlank = prefix.All(c => c is ' ' or '\t');
        var basis = sourceIndent
            ?? (LeadingCount(lines[0]) > 0 ? Measure(lines[0], tab) : (int?)null)
            ?? MinIndent(lines, tab, 1);
        if (basis is not { } b) return text;
        var target = prefixBlank ? VisualColumn(lineText, prefix.Length, tab) : Measure(lineText, tab);
        var result = Reindent(lines, 1, b, target, ctx);
        if (prefixBlank) result[0] = lines[0].TrimStart(' ', '\t');
        return string.Join(ctx.Eol, result);
    }

    /// <summary>Lines from <paramref name="from"/> on: <c>target + (indent − basis)</c>, clamped at 0; blank lines become empty.</summary>
    private static string[] Reindent(string[] lines, int from, int basis, int target, EditingContext ctx)
    {
        var result = (string[])lines.Clone();
        for (var i = from; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                result[i] = i == lines.Length - 1 ? line.TrimStart(' ', '\t') : "";
                continue;
            }
            var width = Math.Max(0, target + Measure(line, ctx.TabSize) - basis);
            result[i] = IndentString(width, ctx) + line[LeadingCount(line)..];
        }
        return result;
    }

    private static int? MinIndent(string[] lines, int tab, int from)
    {
        int? min = null;
        for (var i = from; i < lines.Length; i++)
            if (lines[i].Trim().Length > 0) min = Math.Min(min ?? int.MaxValue, Measure(lines[i], tab));
        return min;
    }

    internal static string IndentString(int width, EditingContext ctx) =>
        ctx.InsertSpaces ? new string(' ', width) : new string('\t', width / ctx.TabSize) + new string(' ', width % ctx.TabSize);

    /// <summary>Visual width of a line's leading whitespace (tabs to the next tab stop).</summary>
    internal static int Measure(string line, int tab) => VisualColumn(line, LeadingCount(line), tab);

    private static int LeadingCount(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] is ' ' or '\t') i++;
        return i;
    }

    internal static int VisualColumn(string line, int index, int tab)
    {
        var width = 0;
        for (var i = 0; i < Math.Min(index, line.Length); i++) width = line[i] == '\t' ? width + tab - width % tab : width + 1;
        return width + Math.Max(0, index - line.Length);
    }

    private static (int Index, int Pad) IndexAtVisualColumn(string line, int column, int tab)
    {
        var width = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (width >= column) return (i, 0);
            width = line[i] == '\t' ? width + tab - width % tab : width + 1;
        }
        return (line.Length, Math.Max(0, column - width));
    }

    private static TextPosition EndOf(TextPosition start, string inserted, string eol)
    {
        var lines = ClipboardTextExtensions.SplitLines(inserted);
        return lines.Length == 1 ? new TextPosition(start.Line, start.Character + inserted.Length) : new TextPosition(start.Line + lines.Length - 1, lines[^1].Length);
    }
}
