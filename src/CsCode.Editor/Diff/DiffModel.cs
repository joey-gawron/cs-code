using CsCode.Core.Text;

namespace CsCode.Editor.Diff;

/// <summary>A character range inside one line (0-based column and length, UTF-16 units) that changed.</summary>
public readonly record struct LineSpan(int Start, int Length);

/// <summary>What one line of a diff side is: unchanged, or part of a change (removed on the original side, inserted on the modified side).</summary>
public enum DiffLineKind : byte { None = 0, Removed = 1, Inserted = 2 }

/// <summary>
/// The computed comparison of two texts, immutable (built on the editor worker, read by the renderers on the UI thread): the line changes, the
/// kind of every line of each side, the changed characters of each changed line, and a line mapping for synchronised scrolling.
/// </summary>
public sealed class DiffModel
{
    /// <summary>Changes larger than this (characters, per side) get no character-level highlighting: it would cost more than it helps.</summary>
    public const int MaxInlineChars = 20_000;

    private DiffModel(string[] original, string[] modified, IReadOnlyList<DiffChange> changes, bool hitTimeout)
    {
        Original = original;
        Modified = modified;
        Changes = changes;
        HitTimeout = hitTimeout;
        OriginalKinds = new DiffLineKind[original.Length];
        ModifiedKinds = new DiffLineKind[modified.Length];
        foreach (var c in changes)
        {
            for (var i = c.OriginalStart; i < c.OriginalEnd && i < original.Length; i++) OriginalKinds[i] = DiffLineKind.Removed;
            for (var i = c.ModifiedStart; i < c.ModifiedEnd && i < modified.Length; i++) ModifiedKinds[i] = DiffLineKind.Inserted;
        }
    }

    public string[] Original { get; }
    public string[] Modified { get; }
    public IReadOnlyList<DiffChange> Changes { get; }
    public bool HitTimeout { get; }
    public DiffLineKind[] OriginalKinds { get; }
    public DiffLineKind[] ModifiedKinds { get; }
    public Dictionary<int, List<LineSpan>> OriginalSpans { get; } = new();
    public Dictionary<int, List<LineSpan>> ModifiedSpans { get; } = new();

    public bool IsIdentical => Changes.Count == 0;

    public static DiffModel Compute(string original, string modified, bool ignoreTrimWhitespace, CancellationToken ct = default)
    {
        var a = LineDiff.SplitLines(original);
        var b = LineDiff.SplitLines(modified);
        var result = LineDiff.Compute(a, b, new LineDiffOptions(ignoreTrimWhitespace, TimeSpan.FromSeconds(2)), ct);
        var model = new DiffModel(a, b, result.Changes, result.HitTimeout);
        foreach (var c in result.Changes)
        {
            ct.ThrowIfCancellationRequested();
            if (c.OriginalLength == 0 || c.ModifiedLength == 0) continue;
            var left = string.Join('\n', a, c.OriginalStart, c.OriginalLength);
            var right = string.Join('\n', b, c.ModifiedStart, c.ModifiedLength);
            if (left.Length > MaxInlineChars || right.Length > MaxInlineChars) continue;
            var chars = LineDiff.ComputeChars(left, right, ct: ct);
            foreach (var ch in chars)
            {
                AddSpans(model.OriginalSpans, a, c.OriginalStart, ch.OriginalStart, ch.OriginalLength);
                AddSpans(model.ModifiedSpans, b, c.ModifiedStart, ch.ModifiedStart, ch.ModifiedLength);
            }
        }
        return model;
    }

    // Maps an offset range of the '\n'-joined block back to per-line spans (a range crossing line breaks becomes one span per line).
    private static void AddSpans(Dictionary<int, List<LineSpan>> into, string[] lines, int firstLine, int offset, int length)
    {
        if (length <= 0) return;
        var line = firstLine;
        var lineStart = 0;
        while (line < lines.Length && offset > lineStart + lines[line].Length) { lineStart += lines[line].Length + 1; line++; }
        var end = offset + length;
        while (line < lines.Length && lineStart <= end)
        {
            var lineEnd = lineStart + lines[line].Length;
            var s = Math.Max(offset, lineStart);
            var e = Math.Min(end, lineEnd);
            if (e > s)
            {
                if (!into.TryGetValue(line, out var list)) into[line] = list = new List<LineSpan>(2);
                list.Add(new LineSpan(s - lineStart, e - s));
            }
            lineStart = lineEnd + 1;
            line++;
        }
    }

    /// <summary>
    /// The line of the other side that corresponds to <paramref name="line"/> (fractional: inside a change the position is interpolated, so two
    /// panes scrolled together stay aligned through blocks of different heights).
    /// </summary>
    public double MapLine(double line, bool fromOriginal)
    {
        double delta = 0;
        foreach (var c in Changes)
        {
            var (s, len, os, olen) = fromOriginal ? (c.OriginalStart, c.OriginalLength, c.ModifiedStart, c.ModifiedLength) : (c.ModifiedStart, c.ModifiedLength, c.OriginalStart, c.OriginalLength);
            if (line < s) return line + delta;
            if (line < s + len) return os + (line - s) * (olen / (double)len);
            delta = os + olen - (s + len);
        }
        return line + delta;
    }

    /// <summary>The index of the first change at or after <paramref name="line"/> of the modified side (wrapping), or of the last change before it (<paramref name="forward"/> false).</summary>
    public int NextChange(int line, bool forward, bool onModified = true)
    {
        if (Changes.Count == 0) return -1;
        int Start(DiffChange c) => onModified ? c.ModifiedStart : c.OriginalStart;
        if (forward)
        {
            for (var i = 0; i < Changes.Count; i++) if (Start(Changes[i]) > line) return i;
            return 0;
        }
        for (var i = Changes.Count - 1; i >= 0; i--) if (Start(Changes[i]) < line) return i;
        return Changes.Count - 1;
    }
}

/// <summary>
/// The inline (unified) presentation of a <see cref="DiffModel"/>: one text with the removed lines placed above the lines that replaced them,
/// each line tagged with its kind and its line numbers on both sides (null where the line does not exist on that side).
/// </summary>
public sealed class UnifiedDiff
{
    public required string Text { get; init; }
    public required DiffLineKind[] Kinds { get; init; }
    public required int?[] OriginalNumbers { get; init; }
    public required int?[] ModifiedNumbers { get; init; }
    public required Dictionary<int, List<LineSpan>> Spans { get; init; }
    /// <summary>The unified line where each change starts.</summary>
    public required int[] ChangeStarts { get; init; }

    public static UnifiedDiff From(DiffModel m)
    {
        var lines = new List<string>(m.Modified.Length + 16);
        var kinds = new List<DiffLineKind>(lines.Capacity);
        var orig = new List<int?>(lines.Capacity);
        var mod = new List<int?>(lines.Capacity);
        var spans = new Dictionary<int, List<LineSpan>>();
        var starts = new List<int>(m.Changes.Count);
        int a = 0, b = 0;
        void Equal(int upToModified)
        {
            while (b < upToModified)
            {
                lines.Add(m.Modified[b]); kinds.Add(DiffLineKind.None); orig.Add(a + 1); mod.Add(b + 1);
                a++; b++;
            }
        }
        foreach (var c in m.Changes)
        {
            Equal(c.ModifiedStart);
            a = c.OriginalStart;
            starts.Add(lines.Count);
            for (var i = c.OriginalStart; i < c.OriginalEnd; i++)
            {
                if (m.OriginalSpans.TryGetValue(i, out var s)) spans[lines.Count] = s;
                lines.Add(m.Original[i]); kinds.Add(DiffLineKind.Removed); orig.Add(i + 1); mod.Add(null);
            }
            for (var i = c.ModifiedStart; i < c.ModifiedEnd; i++)
            {
                if (m.ModifiedSpans.TryGetValue(i, out var s)) spans[lines.Count] = s;
                lines.Add(m.Modified[i]); kinds.Add(DiffLineKind.Inserted); orig.Add(null); mod.Add(i + 1);
            }
            a = c.OriginalEnd;
            b = c.ModifiedEnd;
        }
        Equal(m.Modified.Length);
        return new UnifiedDiff
        {
            Text = string.Join('\n', lines), Kinds = [.. kinds], OriginalNumbers = [.. orig], ModifiedNumbers = [.. mod], Spans = spans, ChangeStarts = [.. starts],
        };
    }
}
