using System.Diagnostics;

namespace CsCode.Core.Text;

// Additive Core primitive added by the source-control work (docs/SOURCE-CONTROL.md): the one diff algorithm behind the diff editor, the
// editor's gutter change markers and the hunk staging of the Source Control window. UI-free, allocation-light, cancellable.

/// <summary>
/// One change between two line lists: <see cref="OriginalLength"/> lines of the original starting at <see cref="OriginalStart"/> were replaced by
/// <see cref="ModifiedLength"/> lines of the modified text starting at <see cref="ModifiedStart"/> (0-based). A pure insertion has
/// <see cref="OriginalLength"/> 0 (it goes before original line <see cref="OriginalStart"/>), a pure deletion <see cref="ModifiedLength"/> 0.
/// The same record is used for character changes (offsets instead of lines).
/// </summary>
public readonly record struct DiffChange(int OriginalStart, int OriginalLength, int ModifiedStart, int ModifiedLength)
{
    public int OriginalEnd => OriginalStart + OriginalLength;
    public int ModifiedEnd => ModifiedStart + ModifiedLength;
    public bool IsInsertion => OriginalLength == 0;
    public bool IsDeletion => ModifiedLength == 0;
}

/// <summary>Options of <see cref="LineDiff.Compute(IReadOnlyList{string}, IReadOnlyList{string}, LineDiffOptions?, CancellationToken)"/>.</summary>
/// <param name="IgnoreTrimWhitespace">Lines that differ only in leading/trailing whitespace are equal (VS Code's <c>diffEditor.ignoreTrimWhitespace</c>).</param>
/// <param name="MaxComputationTime">
/// Budget for the search. Past it the remaining unsolved ranges are reported as whole replacements (still a correct diff, just a coarser one) and
/// <see cref="LineDiffResult.HitTimeout"/> says so. <see cref="TimeSpan.Zero"/> = no limit.
/// </param>
public sealed record LineDiffOptions(bool IgnoreTrimWhitespace = false, TimeSpan MaxComputationTime = default)
{
    public static readonly LineDiffOptions Default = new(false, TimeSpan.FromSeconds(5));
}

/// <summary>The changes (ordered, non-overlapping) and whether the time budget cut the search short.</summary>
public sealed record LineDiffResult(IReadOnlyList<DiffChange> Changes, bool HitTimeout)
{
    public static readonly LineDiffResult Identical = new([], false);
}

/// <summary>
/// Myers' O(ND) difference algorithm in linear space (divide and conquer on the middle snake), after stripping the common prefix and suffix.
/// Lines are interned to integers first, so comparisons are integer compares. Pure insertions and deletions are then slid to the position git
/// prefers (a block that can move is moved down, and up again only to end on a blank line), so a function added between two others is reported as
/// the whole function rather than a shifted closing brace. Characters (<see cref="ComputeChars"/>) use the same engine.
/// </summary>
public static class LineDiff
{
    /// <summary>Splits text into lines on <c>\r\n</c>, <c>\n</c> or <c>\r</c>; the terminators are not kept. Empty text is one empty line; a final terminator adds an empty last line.</summary>
    public static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [""];
        var lines = new List<string>(Math.Max(4, text.Length / 32));
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\n' && c != '\r') continue;
            lines.Add(text.Substring(start, i - start));
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        lines.Add(text.Substring(start));
        return [.. lines];
    }

    public static LineDiffResult Compute(string original, string modified, LineDiffOptions? options = null, CancellationToken ct = default) =>
        Compute(SplitLines(original), SplitLines(modified), options, ct);

    /// <summary>The line changes that turn <paramref name="original"/> into <paramref name="modified"/>. Cancellable (throws <see cref="OperationCanceledException"/>).</summary>
    public static LineDiffResult Compute(IReadOnlyList<string> original, IReadOnlyList<string> modified, LineDiffOptions? options = null, CancellationToken ct = default)
    {
        options ??= LineDiffOptions.Default;
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Intern(string line)
        {
            var key = options.IgnoreTrimWhitespace ? line.Trim() : line;
            if (!ids.TryGetValue(key, out var id)) ids[key] = id = ids.Count;
            return id;
        }
        var a = new int[original.Count];
        for (var i = 0; i < a.Length; i++) a[i] = Intern(original[i]);
        var b = new int[modified.Count];
        for (var i = 0; i < b.Length; i++) b[i] = Intern(modified[i]);
        var engine = new Engine(a, b, options.MaxComputationTime, ct);
        var changes = engine.Run();
        if (changes.Count == 0) return engine.HitTimeout ? new LineDiffResult([], true) : LineDiffResult.Identical;
        var blank = new bool[a.Length];
        for (var i = 0; i < a.Length; i++) blank[i] = string.IsNullOrWhiteSpace(original[i]);
        var blankB = new bool[b.Length];
        for (var i = 0; i < b.Length; i++) blankB[i] = string.IsNullOrWhiteSpace(modified[i]);
        return new LineDiffResult(Merge(Slide(changes, a, b, blank, blankB)), engine.HitTimeout);
    }

    /// <summary>Joins changes that touch (no equal line between them) into one.</summary>
    private static List<DiffChange> Merge(List<DiffChange> changes)
    {
        if (changes.Count < 2) return changes;
        var merged = new List<DiffChange>(changes.Count) { changes[0] };
        for (var i = 1; i < changes.Count; i++)
        {
            var p = merged[^1];
            var n = changes[i];
            if (n.OriginalStart == p.OriginalEnd && n.ModifiedStart == p.ModifiedEnd)
                merged[^1] = new DiffChange(p.OriginalStart, p.OriginalLength + n.OriginalLength, p.ModifiedStart, p.ModifiedLength + n.ModifiedLength);
            else merged.Add(n);
        }
        return merged;
    }

    /// <summary>
    /// Character changes between two strings (offsets into each). Small equal runs (up to <paramref name="mergeGap"/> characters, none of them a line
    /// break) between two changes are merged into one change, which reads better than a confetti of one-letter matches.
    /// </summary>
    public static IReadOnlyList<DiffChange> ComputeChars(string original, string modified, int mergeGap = 2, TimeSpan maxComputationTime = default, CancellationToken ct = default)
    {
        var a = new int[original.Length];
        for (var i = 0; i < a.Length; i++) a[i] = original[i];
        var b = new int[modified.Length];
        for (var i = 0; i < b.Length; i++) b[i] = modified[i];
        var changes = new Engine(a, b, maxComputationTime == default ? TimeSpan.FromMilliseconds(200) : maxComputationTime, ct).Run();
        if (mergeGap <= 0 || changes.Count < 2) return changes;
        var merged = new List<DiffChange>(changes.Count) { changes[0] };
        for (var i = 1; i < changes.Count; i++)
        {
            var prev = merged[^1];
            var next = changes[i];
            var gap = next.OriginalStart - prev.OriginalEnd;
            if (gap <= mergeGap && !original.AsSpan(prev.OriginalEnd, gap).ContainsAny('\n', '\r'))
                merged[^1] = new DiffChange(prev.OriginalStart, next.OriginalEnd - prev.OriginalStart, prev.ModifiedStart, next.ModifiedEnd - prev.ModifiedStart);
            else merged.Add(next);
        }
        return merged;
    }

    // Pure insertions/deletions can often be placed at several positions (the repeated closing brace). Move each as far down as it goes (git's
    // default), then back up while that makes it end on a blank line (git's "indent heuristic", reduced to its most visible rule).
    private static List<DiffChange> Slide(List<DiffChange> changes, int[] a, int[] b, bool[] blankA, bool[] blankB)
    {
        for (var i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            if (c.OriginalLength > 0 && c.ModifiedLength > 0) continue;
            var nextOrig = i + 1 < changes.Count ? changes[i + 1].OriginalStart : a.Length;
            var nextMod = i + 1 < changes.Count ? changes[i + 1].ModifiedStart : b.Length;
            var prevOrig = i > 0 ? changes[i - 1].OriginalEnd : 0;
            var prevMod = i > 0 ? changes[i - 1].ModifiedEnd : 0;
            var (seq, start, len, other, otherStart) = c.OriginalLength > 0
                ? (a, c.OriginalStart, c.OriginalLength, 0, c.ModifiedStart)
                : (b, c.ModifiedStart, c.ModifiedLength, 1, c.OriginalStart);
            var limitDown = other == 0 ? nextOrig : nextMod;
            var limitUp = other == 0 ? prevOrig : prevMod;
            var blank = other == 0 ? blankA : blankB;
            // down
            var s = start;
            while (s + len < limitDown && seq[s] == seq[s + len] && (other == 0 ? otherStart + (s - start) + 1 <= nextMod : otherStart + (s - start) + 1 <= nextOrig)) s++;
            // up, to the lowest position that ends on a blank line
            var best = s;
            var t = s;
            while (t > limitUp && seq[t - 1] == seq[t - 1 + len] && (otherStart + (t - start) - 1) >= (other == 0 ? prevMod : prevOrig))
            {
                t--;
                if (blank[t + len - 1] && !blank[best + len - 1]) { best = t; break; }
            }
            var shift = best - start;
            if (shift == 0) continue;
            changes[i] = c.OriginalLength > 0
                ? new DiffChange(c.OriginalStart + shift, c.OriginalLength, c.ModifiedStart + shift, 0)
                : new DiffChange(c.OriginalStart + shift, 0, c.ModifiedStart + shift, c.ModifiedLength);
        }
        return changes;
    }

    private sealed class Engine
    {
        private readonly int[] _a, _b;
        private readonly int[] _vf, _vb;
        private readonly int _offset;
        private readonly long _deadline;
        private readonly CancellationToken _ct;
        private readonly List<DiffChange> _out = new();
        private int _calls;

        public Engine(int[] a, int[] b, TimeSpan budget, CancellationToken ct)
        {
            _a = a;
            _b = b;
            _ct = ct;
            var max = a.Length + b.Length + 2;
            _offset = max;
            _vf = new int[2 * max + 3];
            _vb = new int[2 * max + 3];
            _deadline = budget > TimeSpan.Zero ? Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency) : long.MaxValue;
        }

        public bool HitTimeout { get; private set; }

        public List<DiffChange> Run()
        {
            Diff(0, _a.Length, 0, _b.Length);
            return Merge(_out); // the recursion can split one change in two at a split point
        }

        private bool OverBudget()
        {
            if (HitTimeout) return true;
            if (_deadline == long.MaxValue) return false;
            if (Stopwatch.GetTimestamp() <= _deadline) return false;
            HitTimeout = true;
            return true;
        }

        private void Add(int aStart, int aLen, int bStart, int bLen)
        {
            if (aLen == 0 && bLen == 0) return;
            _out.Add(new DiffChange(aStart, aLen, bStart, bLen));
        }

        private void Diff(int aLo, int aHi, int bLo, int bHi)
        {
            if ((++_calls & 63) == 0) _ct.ThrowIfCancellationRequested();
            while (aLo < aHi && bLo < bHi && _a[aLo] == _b[bLo]) { aLo++; bLo++; }
            while (aLo < aHi && bLo < bHi && _a[aHi - 1] == _b[bHi - 1]) { aHi--; bHi--; }
            if (aLo == aHi) { Add(aLo, 0, bLo, bHi - bLo); return; }
            if (bLo == bHi) { Add(aLo, aHi - aLo, bLo, 0); return; }
            if (OverBudget() || !MiddleSnake(aLo, aHi, bLo, bHi, out var xs, out var ys, out var xe, out var ye))
            {
                Add(aLo, aHi - aLo, bLo, bHi - bLo);
                return;
            }
            Diff(aLo, xs, bLo, ys);
            Diff(xe, aHi, ye, bHi);
        }

        // Finds the middle snake of the shortest edit script of a[aLo..aHi) -> b[bLo..bHi) (both non-empty, first and last elements differ).
        // Returns its start (xs, ys) and end (xe, ye) in absolute indices; false when the time budget ran out mid-search.
        private bool MiddleSnake(int aLo, int aHi, int bLo, int bHi, out int xs, out int ys, out int xe, out int ye)
        {
            int n = aHi - aLo, m = bHi - bLo;
            var delta = n - m;
            var odd = (delta & 1) != 0;
            var max = (n + m + 1) / 2;
            var vf = _vf;
            var vb = _vb;
            var o = _offset;
            vf[o + 1] = 0;
            vb[o + 1] = 0;
            for (var d = 0; d <= max; d++)
            {
                if ((d & 255) == 255)
                {
                    _ct.ThrowIfCancellationRequested();
                    if (OverBudget()) { xs = ys = xe = ye = 0; return false; }
                }
                for (var k = -d; k <= d; k += 2)
                {
                    var x = k == -d || (k != d && vf[o + k - 1] < vf[o + k + 1]) ? vf[o + k + 1] : vf[o + k - 1] + 1;
                    var y = x - k;
                    int sx = x, sy = y;
                    while (x < n && y < m && _a[aLo + x] == _b[bLo + y]) { x++; y++; }
                    vf[o + k] = x;
                    if (odd && k >= delta - (d - 1) && k <= delta + (d - 1) && x + vb[o + delta - k] >= n)
                    {
                        xs = aLo + sx; ys = bLo + sy; xe = aLo + x; ye = bLo + y;
                        return true;
                    }
                }
                for (var k = -d; k <= d; k += 2)
                {
                    var x = k == -d || (k != d && vb[o + k - 1] < vb[o + k + 1]) ? vb[o + k + 1] : vb[o + k - 1] + 1;
                    var y = x - k;
                    int sx = x, sy = y;
                    while (x < n && y < m && _a[aHi - 1 - x] == _b[bHi - 1 - y]) { x++; y++; }
                    vb[o + k] = x;
                    if (!odd && delta - k >= -d && delta - k <= d && x + vf[o + delta - k] >= n)
                    {
                        xs = aHi - x; ys = bHi - y; xe = aHi - sx; ye = bHi - sy;
                        return true;
                    }
                }
            }
            xs = ys = xe = ye = 0;
            return false; // unreachable for valid input
        }
    }
}
