using System.Text;

namespace CsCode.Core.Text;

// Additive Core primitive added by the merge-conflict work (docs/SOURCE-CONTROL.md, "Merge editor"): the 3-way merge behind the merge editor,
// built on LineDiff (no second diff algorithm). UI-free; the merge editor view and the git module's tests drive it.

/// <summary>Lines [<see cref="Start"/>, <see cref="Start"/> + <see cref="Length"/>) of one text (0-based).</summary>
public readonly record struct LineSpan(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>What happened to one stretch of the base text: nothing, a change on one side, the same change on both, or different changes (a conflict).</summary>
public enum MergeRegionKind { Unchanged, Current, Incoming, Both, Conflict }

/// <summary>Which sides of a conflict the result takes.</summary>
[Flags]
public enum MergeSides { None = 0, Current = 1, Incoming = 2, Both = Current | Incoming }

/// <summary>
/// One region of a 3-way merge: the lines it covers in the base, current (ours) and incoming (theirs) texts. Regions follow each other and cover
/// all three texts. <see cref="ConflictIndex"/> numbers the conflicts (-1 for other regions).
/// </summary>
public sealed record MergeRegion(MergeRegionKind Kind, LineSpan Base, LineSpan Current, LineSpan Incoming, int ConflictIndex = -1);

/// <summary>
/// A 3-way merge of <c>base</c>, <c>current</c> (ours, git's stage 2) and <c>incoming</c> (theirs, stage 3), as git merges: the changes of
/// each side against the base (<see cref="LineDiff"/>) that overlap or touch form one region; a region changed on one side only, or the same
/// way on both, merges by itself; one changed differently on both sides is a conflict. Lines keep their terminators (a line-ending change is a
/// change, and the result keeps every line's own ending).
/// </summary>
public sealed class ThreeWayMerge
{
    private ThreeWayMerge(string[] @base, string[] current, string[] incoming, IReadOnlyList<MergeRegion> regions, bool hitTimeout)
    {
        BaseLines = @base;
        CurrentLines = current;
        IncomingLines = incoming;
        Regions = regions;
        Conflicts = [.. regions.Where(r => r.Kind == MergeRegionKind.Conflict)];
        HitTimeout = hitTimeout;
    }

    public IReadOnlyList<string> BaseLines { get; }
    public IReadOnlyList<string> CurrentLines { get; }
    public IReadOnlyList<string> IncomingLines { get; }
    public IReadOnlyList<MergeRegion> Regions { get; }
    public IReadOnlyList<MergeRegion> Conflicts { get; }

    /// <summary>A diff hit its time budget: regions are coarser than they could be (still a correct merge).</summary>
    public bool HitTimeout { get; }

    /// <summary>Splits text into lines that keep their terminators (<c>\n</c>, <c>\r\n</c>, <c>\r</c>); concatenating them gives the text back.</summary>
    public static string[] SplitLinesKeepingEnds(string text)
    {
        if (text.Length == 0) return [];
        var lines = new List<string>(Math.Max(4, text.Length / 32));
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\n' && c != '\r') continue;
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            lines.Add(text.Substring(start, i + 1 - start));
            start = i + 1;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return [.. lines];
    }

    public static ThreeWayMerge Compute(string @base, string current, string incoming, CancellationToken ct = default)
    {
        var b = SplitLinesKeepingEnds(@base);
        var c = SplitLinesKeepingEnds(current);
        var i = SplitLinesKeepingEnds(incoming);
        var options = new LineDiffOptions(MaxComputationTime: TimeSpan.FromSeconds(5));
        var toCurrent = LineDiff.Compute(b, c, options, ct);
        var toIncoming = LineDiff.Compute(b, i, options, ct);
        return new ThreeWayMerge(b, c, i, Merge(b, c, i, toCurrent.Changes, toIncoming.Changes), toCurrent.HitTimeout || toIncoming.HitTimeout);
    }

    private static List<MergeRegion> Merge(string[] b, string[] c, string[] inc, IReadOnlyList<DiffChange> d1, IReadOnlyList<DiffChange> d2)
    {
        var regions = new List<MergeRegion>();
        int basePos = 0, curPos = 0, incPos = 0, i = 0, j = 0, conflicts = 0;
        while (i < d1.Count || j < d2.Count)
        {
            // the next cluster: the earliest change, then every change of either side that overlaps or touches what the cluster covers
            var start = Math.Min(i < d1.Count ? d1[i].OriginalStart : int.MaxValue, j < d2.Count ? d2[j].OriginalStart : int.MaxValue);
            var end = start;
            int i0 = i, j0 = j;
            while (true)
            {
                if (i < d1.Count && d1[i].OriginalStart <= end) { end = Math.Max(end, d1[i].OriginalEnd); i++; continue; }
                if (j < d2.Count && d2[j].OriginalStart <= end) { end = Math.Max(end, d2[j].OriginalEnd); j++; continue; }
                break;
            }
            if (start > basePos)
            {
                var n = start - basePos;
                regions.Add(new MergeRegion(MergeRegionKind.Unchanged, new LineSpan(basePos, n), new LineSpan(curPos, n), new LineSpan(incPos, n)));
                curPos += n;
                incPos += n;
                basePos = start;
            }
            var cur = Side(d1, i0, i, start, end, curPos, basePos);
            var theirs = Side(d2, j0, j, start, end, incPos, basePos);
            MergeRegionKind kind;
            if (i0 == i) kind = MergeRegionKind.Incoming;
            else if (j0 == j) kind = MergeRegionKind.Current;
            else kind = c.AsSpan(cur.Start, cur.Length).SequenceEqual(inc.AsSpan(theirs.Start, theirs.Length)) ? MergeRegionKind.Both : MergeRegionKind.Conflict;
            regions.Add(new MergeRegion(kind, new LineSpan(start, end - start), cur, theirs, kind == MergeRegionKind.Conflict ? conflicts++ : -1));
            basePos = end;
            curPos = cur.End;
            incPos = theirs.End;
        }
        var rest = b.Length - basePos; // after the last change all three texts have the same lines left
        if (rest > 0) regions.Add(new MergeRegion(MergeRegionKind.Unchanged, new LineSpan(basePos, rest), new LineSpan(curPos, rest), new LineSpan(incPos, rest)));
        return regions;
    }

    /// <summary>One side's lines for the base lines [start, end): its changes in [from, to) of <paramref name="changes"/>, else the base lines shifted.</summary>
    private static LineSpan Side(IReadOnlyList<DiffChange> changes, int from, int to, int start, int end, int sidePos, int basePos)
    {
        if (from == to) return new LineSpan(sidePos + (start - basePos), end - start);
        var first = changes[from];
        var last = changes[to - 1];
        var s = first.ModifiedStart - (first.OriginalStart - start);
        var e = last.ModifiedEnd + (end - last.OriginalEnd);
        return new LineSpan(s, e - s);
    }

    /// <summary>The lines a region contributes to the result: its merged lines, or for a conflict the sides taken (the first taken first; none: the base).</summary>
    public IEnumerable<string> ResultLines(MergeRegion region, MergeSides taken = MergeSides.None, bool incomingFirst = false)
    {
        IEnumerable<string> Of(IReadOnlyList<string> lines, LineSpan span) => Enumerable.Range(span.Start, span.Length).Select(k => lines[k]);
        switch (region.Kind)
        {
            case MergeRegionKind.Unchanged: return Of(BaseLines, region.Base);
            case MergeRegionKind.Current or MergeRegionKind.Both: return Of(CurrentLines, region.Current);
            case MergeRegionKind.Incoming: return Of(IncomingLines, region.Incoming);
        }
        return taken switch
        {
            MergeSides.Current => Of(CurrentLines, region.Current),
            MergeSides.Incoming => Of(IncomingLines, region.Incoming),
            MergeSides.Both => incomingFirst
                ? Of(IncomingLines, region.Incoming).Concat(Of(CurrentLines, region.Current))
                : Of(CurrentLines, region.Current).Concat(Of(IncomingLines, region.Incoming)),
            _ => Of(BaseLines, region.Base),
        };
    }
}

/// <summary>
/// The choices made in a merge editor: for each conflict, the sides taken and their order (the side taken first comes first, so taking
/// incoming then current gives incoming followed by current). Unresolved conflicts take the base text in the result, as in VS Code.
/// </summary>
public sealed class MergeResolution(ThreeWayMerge merge)
{
    private readonly MergeSides[] _taken = new MergeSides[merge.Conflicts.Count];
    private readonly bool[] _incomingFirst = new bool[merge.Conflicts.Count];
    private readonly bool[] _manual = new bool[merge.Conflicts.Count];

    public ThreeWayMerge Merge { get; } = merge;

    public MergeSides Taken(int conflict) => _taken[conflict];

    public bool IncomingFirst(int conflict) => _incomingFirst[conflict];

    /// <summary>Whether the conflict counts as handled: a side was taken, or its text was edited by hand (<see cref="MarkManual"/>).</summary>
    public bool IsHandled(int conflict) => _taken[conflict] != MergeSides.None || _manual[conflict];

    public int UnhandledCount => Enumerable.Range(0, _taken.Length).Count(k => !IsHandled(k));

    /// <summary>Takes exactly <paramref name="sides"/> (with <paramref name="incomingFirst"/> deciding the order of both).</summary>
    public void Set(int conflict, MergeSides sides, bool incomingFirst = false)
    {
        _taken[conflict] = sides;
        _incomingFirst[conflict] = sides == MergeSides.Both && incomingFirst;
        _manual[conflict] = false;
    }

    /// <summary>A checkbox of an input pane: takes <paramref name="side"/> when it was not taken (after the other side, if that one was), else drops it.</summary>
    public void Toggle(int conflict, MergeSides side)
    {
        var now = _taken[conflict];
        if ((now & side) != 0) Set(conflict, now & ~side);
        else Set(conflict, now | side, incomingFirst: now == MergeSides.Incoming && side == MergeSides.Current);
    }

    /// <summary>The conflict's result text was edited by hand: it counts as handled and no side is checked.</summary>
    public void MarkManual(int conflict)
    {
        _taken[conflict] = MergeSides.None;
        _manual[conflict] = true;
    }

    /// <summary>The text a conflict contributes to the result now.</summary>
    public string TextOf(int conflict) => string.Concat(Merge.ResultLines(Merge.Conflicts[conflict], _taken[conflict], _incomingFirst[conflict]));

    /// <summary>The whole result.</summary>
    public string ResultText()
    {
        var sb = new StringBuilder();
        foreach (var r in Merge.Regions)
            foreach (var line in r.Kind == MergeRegionKind.Conflict ? Merge.ResultLines(r, _taken[r.ConflictIndex], _incomingFirst[r.ConflictIndex]) : Merge.ResultLines(r))
                sb.Append(line);
        return sb.ToString();
    }

    /// <summary>
    /// What the text of conflict <paramref name="conflict"/> amounts to: the sides whose combination it equals (the base: <see cref="MergeSides.None"/>),
    /// or null when it is none of them (edited by hand).
    /// </summary>
    public (MergeSides Sides, bool IncomingFirst)? Classify(int conflict, string text)
    {
        var r = Merge.Conflicts[conflict];
        string T(MergeSides s, bool incomingFirst = false) => string.Concat(Merge.ResultLines(r, s, incomingFirst));
        if (text == T(MergeSides.None)) return (MergeSides.None, false);
        if (text == T(MergeSides.Current)) return (MergeSides.Current, false);
        if (text == T(MergeSides.Incoming)) return (MergeSides.Incoming, false);
        if (text == T(MergeSides.Both)) return (MergeSides.Both, false);
        if (text == T(MergeSides.Both, true)) return (MergeSides.Both, true);
        return null;
    }
}
