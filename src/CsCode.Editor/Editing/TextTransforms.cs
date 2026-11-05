using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsCode.Core.Text;

namespace CsCode.Editor.Editing;

/// <summary>
/// VS Code's Transform to Uppercase / Lowercase / Title Case / Snake Case / Camel Case / Kebab Case / Pascal Case and Duplicate Selection, as pure
/// functions over a document and its selections (the primary first, then multi-cursor's others; docs/EDITOR.md §7). Each returns the edits
/// (against the current text, non-overlapping: ONE <see cref="ITextDocument.ApplyEdits"/>, one undo step) and the selections afterwards as
/// offsets in the text the edits produce. The case rules are ports of VS Code's regular expressions (linesOperations.ts), so the results match
/// it, quirks included. Safe on any thread (a document read off the UI thread reads its snapshot).
/// </summary>
public static class TextTransforms
{
    /// <summary>VS Code's <c>editor.wordSeparators</c> default: an empty caret transforms the word it touches, a run of other non-blank characters.</summary>
    public const string DefaultWordSeparators = "`~!@#$%^&*()-=+[{]}\\|;:'\",.<>/?";

    public enum Case { Upper, Lower, Title, Snake, Camel, Kebab, Pascal }

    /// <summary>The edits and the selections after them (offsets into the new text; the caret at <c>End</c>).</summary>
    public sealed record Result(IReadOnlyList<TextEdit> Edits, IReadOnlyList<(int Start, int End)> Selections);

    private const RegexOptions Options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
    private static readonly Regex TitleBoundary = new(@"(^|[^\p{L}\p{N}']|((^|\P{L})'))\p{L}", Options);
    private static readonly Regex CaseBoundary = new(@"(\p{Ll})(\p{Lu})", Options);
    private static readonly Regex SnakeSingleLetters = new(@"(\p{Lu}|\p{N})(\p{Lu})(\p{Ll})", Options);
    private static readonly Regex KebabSingleLetters = new(@"(\p{Lu}|\p{N})(\p{Lu}\p{Ll})", Options);
    private static readonly Regex UnderscoreBoundary = new(@"(\S)(_)(\S)", Options);
    private static readonly Regex WordBoundary = new(@"[_\s-]", Options);
    private static readonly Regex KeptBoundary = new(@"(?<=\.)", Options);

    /// <summary>The text in <paramref name="kind"/> (VS Code's rules; upper/lower case follow the current culture, as its locale case does).</summary>
    public static string Apply(Case kind, string text)
    {
        var culture = CultureInfo.CurrentCulture;
        switch (kind)
        {
            case Case.Upper: return text.ToUpper(culture);
            case Case.Lower: return text.ToLower(culture);
            case Case.Title: return TitleBoundary.Replace(text.ToLower(culture), m => m.Value.ToUpper(culture));
            case Case.Snake: return SnakeSingleLetters.Replace(CaseBoundary.Replace(text, "$1_$2"), "$1_$2$3").ToLower(culture);
            case Case.Kebab: return KebabSingleLetters.Replace(CaseBoundary.Replace(UnderscoreBoundary.Replace(text, "$1-$3"), "$1-$2"), "$1-$2").ToLower(culture);
            case Case.Camel:
            {
                var words = WordBoundary.Split(text);
                var sb = new StringBuilder(words[0]); // the first word is kept as it is (VS Code)
                for (var i = 1; i < words.Length; i++) sb.Append(Capitalize(words[i], culture));
                return sb.ToString();
            }
            case Case.Pascal:
            {
                var sb = new StringBuilder(text.Length);
                foreach (var piece in KeptBoundary.Split(text))
                    foreach (var word in WordBoundary.Split(piece)) sb.Append(Capitalize(word, culture));
                return sb.ToString();
            }
            default: return text;
        }
    }

    private static string Capitalize(string word, CultureInfo culture) =>
        word.Length == 0 ? word : word[..1].ToUpper(culture) + word[1..];

    /// <summary>
    /// Transforms every selection, and at an empty caret the word it touches (<paramref name="wordSeparators"/>: VS Code's
    /// <c>editor.wordSeparators</c>; a caret on no word is left alone). Carets in the same word transform it once; a caret's word that overlaps a
    /// selection is left to the selection. A selection keeps covering its text; a caret keeps its place in its word (at the word's end: the end).
    /// Null when nothing changes.
    /// </summary>
    public static Result? TransformCase(ITextDocument doc, IReadOnlyList<TextRange> selections, Case kind, string? wordSeparators = null)
    {
        var separators = wordSeparators ?? DefaultWordSeparators;
        var n = selections.Count;
        var sel = new (int Start, int End)[n];
        for (var i = 0; i < n; i++) sel[i] = (doc.OffsetAt(selections[i].Start), doc.OffsetAt(selections[i].End));

        var targets = new List<(int Start, int End)>();
        var targetOf = new int[n];
        Array.Fill(targetOf, -1);
        bool Overlaps(int s, int e) => targets.Any(t => s < t.End && t.Start < e);
        for (var i = 0; i < n; i++)
        {
            if (sel[i].Start == sel[i].End || Overlaps(sel[i].Start, sel[i].End)) continue;
            targetOf[i] = targets.Count;
            targets.Add(sel[i]);
        }
        for (var i = 0; i < n; i++)
        {
            if (sel[i].Start != sel[i].End) continue;
            if (WordAt(doc, selections[i].Start, separators) is not { } word) continue;
            var same = targets.FindIndex(t => t == word);
            if (same >= 0) { targetOf[i] = same; continue; }
            if (Overlaps(word.Start, word.End)) continue;
            targetOf[i] = targets.Count;
            targets.Add(word);
        }
        if (targets.Count == 0) return null;

        var replacements = new string[targets.Count];
        var changed = false;
        for (var k = 0; k < targets.Count; k++)
        {
            var old = doc.GetText(new TextRange(doc.PositionAt(targets[k].Start), doc.PositionAt(targets[k].End)));
            replacements[k] = Apply(kind, old);
            changed |= !string.Equals(old, replacements[k], StringComparison.Ordinal);
        }
        if (!changed) return null;

        // where each target starts in the new text: its old start plus what the targets before it grew or shrank by
        var order = Enumerable.Range(0, targets.Count).OrderBy(k => targets[k].Start).ToArray();
        var newStart = new int[targets.Count];
        var shift = 0;
        foreach (var k in order)
        {
            newStart[k] = targets[k].Start + shift;
            shift += replacements[k].Length - (targets[k].End - targets[k].Start);
        }
        int Map(int offset)
        {
            var d = 0;
            foreach (var k in order)
                if (targets[k].End <= offset) d += replacements[k].Length - (targets[k].End - targets[k].Start);
            return offset + d;
        }

        var after = new (int Start, int End)[n];
        for (var i = 0; i < n; i++)
        {
            var k = targetOf[i];
            if (k < 0) { after[i] = (Map(sel[i].Start), Map(sel[i].End)); continue; }
            var length = replacements[k].Length;
            if (sel[i].Start != sel[i].End) { after[i] = (newStart[k], newStart[k] + length); continue; }
            var within = sel[i].Start == targets[k].End ? length : Math.Min(sel[i].Start - targets[k].Start, length);
            after[i] = (newStart[k] + within, newStart[k] + within);
        }
        var edits = new TextEdit[targets.Count];
        for (var k = 0; k < targets.Count; k++)
            edits[k] = new TextEdit(new TextRange(doc.PositionAt(targets[k].Start), doc.PositionAt(targets[k].End)), replacements[k]);
        return new Result(edits, after);
    }

    /// <summary>
    /// VS Code's Duplicate Selection: a selection's text goes in again right after it and the copy becomes the selection; an empty caret duplicates
    /// its line below and moves to the copy, at the same column (several carets on one line duplicate it once). <paramref name="eol"/>: the
    /// document's line ending. Null when there is nothing to do.
    /// </summary>
    public static Result? Duplicate(ITextDocument doc, IReadOnlyList<TextRange> selections, string eol)
    {
        var n = selections.Count;
        if (n == 0) return null;
        // (offset, text, order at one offset: a selection's copy before a line's, so the copy stays next to its selection)
        var inserts = new List<(int Offset, string Text, int Rank)>();
        var insertOf = new int[n];
        var lineInsert = new Dictionary<int, int>();
        for (var i = 0; i < n; i++)
        {
            var r = selections[i];
            if (r.Start != r.End)
            {
                insertOf[i] = inserts.Count;
                inserts.Add((doc.OffsetAt(r.End), doc.GetText(r), 0));
                continue;
            }
            var line = r.Start.Line;
            if (lineInsert.TryGetValue(line, out var existing)) { insertOf[i] = existing; continue; }
            var text = doc.GetLineText(line);
            lineInsert[line] = insertOf[i] = inserts.Count;
            inserts.Add((doc.OffsetAt(new TextPosition(line, text.Length)), eol + text, 1));
        }

        var order = Enumerable.Range(0, inserts.Count).OrderBy(k => inserts[k].Offset).ThenBy(k => inserts[k].Rank).ThenBy(k => k).ToArray();
        var at = new int[inserts.Count];
        var shift = 0;
        foreach (var k in order)
        {
            at[k] = inserts[k].Offset + shift;
            shift += inserts[k].Text.Length;
        }

        var after = new (int Start, int End)[n];
        for (var i = 0; i < n; i++)
        {
            var k = insertOf[i];
            if (selections[i].Start != selections[i].End) after[i] = (at[k], at[k] + inserts[k].Text.Length);
            else
            {
                var caret = at[k] + eol.Length + selections[i].Start.Character;
                after[i] = (caret, caret);
            }
        }
        var edits = order.Select(k => TextEdit.Insert(doc.PositionAt(inserts[k].Offset), inserts[k].Text)).ToArray();
        return new Result(edits, after);
    }

    /// <summary>The word an empty caret at <paramref name="at"/> touches (on either side), as offsets, or null (VS Code's getWordAtPosition).</summary>
    internal static (int Start, int End)? WordAt(ITextDocument doc, TextPosition at, string separators)
    {
        var text = doc.GetLineText(at.Line);
        var col = Math.Clamp(at.Character, 0, text.Length);
        bool IsWord(char c) => !char.IsWhiteSpace(c) && separators.IndexOf(c) < 0;
        var a = col;
        while (a > 0 && IsWord(text[a - 1])) a--;
        var b = col;
        while (b < text.Length && IsWord(text[b])) b++;
        if (a == b) return null;
        var lineStart = doc.OffsetAt(new TextPosition(at.Line, 0));
        return (lineStart + a, lineStart + b);
    }
}
