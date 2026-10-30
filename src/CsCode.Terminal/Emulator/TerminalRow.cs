using System.Text;

namespace CsCode.Terminal.Emulator;

/// <summary>Shell-integration marks on a row (OSC 633 / OSC 133).</summary>
public enum RowMark : byte { None = 0, PromptStart, CommandStart, CommandExecuted, CommandFinished }

/// <summary>
/// An immutable row: what the renderer draws and what the scrollback keeps. Trailing never-written cells are trimmed (an index past
/// <see cref="Length"/> reads as a blank cell), grapheme clusters and OSC 8 links live in small per-row tables, so a row is self-contained
/// and can be handed to the UI thread, kept in the scrollback or dropped without any shared state.
/// </summary>
public sealed class TerminalRow
{
    public static readonly TerminalRow Empty = new([], null, null, false, RowMark.None, null);

    private readonly Cell[] _cells;
    private readonly string[]? _clusters;
    private readonly string[]? _links;

    internal TerminalRow(Cell[] cells, string[]? clusters, string[]? links, bool wrapped, RowMark mark, int? exitCode)
    {
        _cells = cells;
        _clusters = clusters;
        _links = links;
        IsWrapped = wrapped;
        Mark = mark;
        MarkExitCode = exitCode;
    }

    /// <summary>This row continues the previous one (the text wrapped at the right margin; no newline between them).</summary>
    public bool IsWrapped { get; }

    public RowMark Mark { get; }

    /// <summary>On a prompt row: the exit code of the command run from it once it finished (<see cref="int.MinValue"/>: finished, code unknown).</summary>
    public int? MarkExitCode { get; }

    /// <summary>Stored cells (trailing blanks trimmed).</summary>
    public int Length => _cells.Length;

    public ReadOnlySpan<Cell> Cells => _cells;

    public Cell this[int column] => (uint)column < (uint)_cells.Length ? _cells[column] : Cell.Blank;

    /// <summary>Managed bytes this row holds (scrollback accounting).</summary>
    internal long EstimatedBytes => 64 + _cells.Length * 24L + (_clusters?.Sum(c => 24 + c.Length * 2L) ?? 0) + (_links?.Sum(l => 24 + l.Length * 2L) ?? 0);

    /// <summary>The OSC 8 target of a cell's link id (1-based, row-local), or null.</summary>
    public string? Link(int linkId) => _links is not null && linkId > 0 && linkId <= _links.Length ? _links[linkId - 1] : null;

    /// <summary>The text of one cell: its character or grapheme cluster ("" for a wide character's spacer, " " for a blank).</summary>
    public string CellText(int column)
    {
        var cell = this[column];
        if (cell.IsSpacer) return "";
        return RuneText(cell.Rune);
    }

    internal string RuneText(int rune)
    {
        if (rune == 0) return " ";
        if (rune < 0) return _clusters is not null && -rune - 1 < _clusters.Length ? _clusters[-rune - 1] : "�";
        return rune < 0x10000 ? ((char)rune).ToString() : char.ConvertFromUtf32(rune);
    }

    /// <summary>Appends the text of cells [start, end) (blank cells as spaces, spacers skipped).</summary>
    public void AppendText(StringBuilder sb, int start = 0, int end = int.MaxValue)
    {
        end = Math.Min(end, _cells.Length);
        for (var c = Math.Max(0, start); c < end; c++)
        {
            var cell = _cells[c];
            if (cell.IsSpacer) continue;
            var r = cell.Rune;
            if (r == 0) sb.Append(' ');
            else if (r < 0) sb.Append(RuneText(r));
            else if (r < 0x10000) sb.Append((char)r);
            else sb.Append(char.ConvertFromUtf32(r));
        }
    }

    /// <summary>The row's text with trailing blanks removed.</summary>
    public string GetText(int start = 0, int end = int.MaxValue)
    {
        var sb = new StringBuilder();
        AppendText(sb, start, end);
        var len = sb.Length;
        while (len > 0 && sb[len - 1] == ' ') len--;
        return sb.ToString(0, len);
    }

    public override string ToString() => GetText();
}

/// <summary>A mutable screen line (owned by the terminal's worker thread). <see cref="Snapshot"/> hands out an immutable copy, cached until the next change.</summary>
internal sealed class Line
{
    public Cell[] Cells;
    public bool Wrapped;
    public RowMark Mark;
    public int? MarkExitCode;
    private List<string>? _clusters;
    private List<string>? _links;
    private TerminalRow? _snapshot;

    /// <summary>The longest grapheme cluster kept in one cell (more combining marks are dropped: a hostile stream cannot grow a cell).</summary>
    public const int MaxClusterLength = 32;

    public Line(int columns)
    {
        Cells = new Cell[columns];
    }

    public int Columns => Cells.Length;

    /// <summary>
    /// Managed bytes the line holds: its cells and the strings of its cluster and OSC 8 link tables (a line of 2,000-character links is
    /// hundreds of KB, not its 24 bytes per cell). Shared link strings are counted per line: the cap errs on the safe side.
    /// </summary>
    public long EstimatedBytes()
    {
        var bytes = 64 + Cells.Length * 24L;
        if (_clusters is { } clusters) foreach (var c in clusters) bytes += 24 + c.Length * 2L;
        if (_links is { } links) foreach (var l in links) bytes += 24 + l.Length * 2L;
        return bytes;
    }

    /// <summary>What the scrollback counted for this line when it took it (its tables cannot change while it is kept there).</summary>
    public long AccountedBytes;

    /// <summary>Marks the line changed (drops the cached snapshot). Call after every mutation.</summary>
    public void Touch() => _snapshot = null;

    public bool HasSnapshot => _snapshot is not null;

    public TerminalRow Snapshot()
    {
        if (_snapshot is not null) return _snapshot;
        var used = Cells.Length;
        while (used > 0 && Cells[used - 1].IsBlankDefault) used--;
        var cells = used == 0 ? [] : Cells.AsSpan(0, used).ToArray();
        string[]? clusters = null;
        string[]? links = null;
        if (_clusters is { Count: > 0 } || _links is { Count: > 0 })
        {
            // Copy only what the remaining cells reference, renumbered: tables of overwritten cells are not carried along.
            List<string>? newClusters = null, newLinks = null;
            for (var i = 0; i < cells.Length; i++)
            {
                ref var cell = ref cells[i];
                if (cell.Rune < 0 && _clusters is not null && -cell.Rune - 1 < _clusters.Count)
                {
                    (newClusters ??= []).Add(_clusters[-cell.Rune - 1]);
                    cell.Rune = -newClusters.Count;
                }
                if (cell.Attributes.LinkId > 0 && _links is not null && cell.Attributes.LinkId - 1 < _links.Count)
                {
                    var uri = _links[cell.Attributes.LinkId - 1];
                    newLinks ??= [];
                    var idx = newLinks.IndexOf(uri);
                    if (idx < 0) { newLinks.Add(uri); idx = newLinks.Count - 1; }
                    cell.Attributes = cell.Attributes with { LinkId = idx + 1 };
                }
            }
            clusters = newClusters?.ToArray();
            links = newLinks?.ToArray();
        }
        return _snapshot = new TerminalRow(cells, clusters, links, Wrapped, Mark, MarkExitCode);
    }

    public void Clear(in Cell fill)
    {
        Cells.AsSpan().Fill(fill);
        Wrapped = false;
        Mark = RowMark.None;
        MarkExitCode = null;
        _clusters?.Clear();
        _links?.Clear();
        _snapshot = null;
    }

    /// <summary>Copies a row back into this line (a scrollback row pulled onto the screen when the terminal grows taller).</summary>
    public void Load(TerminalRow row)
    {
        Clear(Cell.Blank);
        var n = Math.Min(row.Length, Cells.Length);
        for (var i = 0; i < n; i++)
        {
            var cell = row[i];
            if (cell.Rune < 0) cell.Rune = AddCluster(row.RuneText(cell.Rune));
            if (cell.Attributes.LinkId > 0)
                cell.Attributes = cell.Attributes with { LinkId = row.Link(cell.Attributes.LinkId) is { } uri ? InternLink(uri) : 0 };
            Cells[i] = cell;
        }
        Wrapped = row.IsWrapped;
        Mark = row.Mark;
        MarkExitCode = row.MarkExitCode;
        FixWideAtEnd();
        _snapshot = null;
    }

    public void Resize(int columns)
    {
        if (columns == Cells.Length) return;
        var old = Cells;
        Cells = new Cell[columns];
        old.AsSpan(0, Math.Min(columns, old.Length)).CopyTo(Cells);
        FixWideAtEnd();
        _snapshot = null;
    }

    /// <summary>A wide character cut in half by the right edge is erased (its spacer no longer fits).</summary>
    private void FixWideAtEnd()
    {
        if (Cells.Length > 0 && Cells[^1].IsWide) Cells[^1] = Cell.Blank;
        if (Cells.Length > 0 && Cells[0].IsSpacer) Cells[0] = Cell.Blank;
    }

    public string ClusterText(int rune) =>
        rune < 0 && _clusters is not null && -rune - 1 < _clusters.Count ? _clusters[-rune - 1] : rune > 0 ? char.ConvertFromUtf32(rune) : " ";

    /// <summary>Stores a cluster and returns the (negative) rune referring to it. The table is compacted when it grows past twice the width.</summary>
    public int AddCluster(string text)
    {
        _clusters ??= [];
        if (_clusters.Count >= Math.Max(8, Cells.Length * 2)) CompactClusters();
        _clusters.Add(text);
        return -_clusters.Count;
    }

    private void CompactClusters()
    {
        var kept = new List<string>();
        for (var i = 0; i < Cells.Length; i++)
        {
            ref var cell = ref Cells[i];
            if (cell.Rune >= 0) continue;
            var idx = -cell.Rune - 1;
            kept.Add(idx < _clusters!.Count ? _clusters[idx] : "�");
            cell.Rune = -kept.Count;
        }
        _clusters = kept;
    }

    /// <summary>Row-local id (1-based) of an OSC 8 target. Bounded like the clusters: at most one per cell.</summary>
    public int InternLink(string uri)
    {
        _links ??= [];
        for (var i = _links.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_links[i], uri) || _links[i] == uri) return i + 1;
        if (_links.Count >= Math.Max(8, Cells.Length)) CompactLinks();
        if (_links.Count >= Math.Max(8, Cells.Length)) return 0;
        _links.Add(uri);
        return _links.Count;
    }

    private void CompactLinks()
    {
        var kept = new List<string>();
        for (var i = 0; i < Cells.Length; i++)
        {
            ref var cell = ref Cells[i];
            var id = cell.Attributes.LinkId;
            if (id <= 0) continue;
            var uri = id - 1 < _links!.Count ? _links[id - 1] : null;
            var idx = uri is null ? -1 : kept.IndexOf(uri);
            if (uri is not null && idx < 0) { kept.Add(uri); idx = kept.Count - 1; }
            cell.Attributes = cell.Attributes with { LinkId = idx + 1 };
        }
        _links = kept;
    }
}

/// <summary>
/// The primary screen's scrollback: a ring of the lines that scrolled off the top, bounded by a line count
/// (<c>terminal.integrated.scrollback</c>) and by a hard memory cap, whichever is reached first. <see cref="TotalPushed"/> counts every line ever
/// pushed, which gives rows stable absolute numbers (selections and the viewport stay put while old lines fall off the front).
/// <para>
/// Allocation-free in steady state, which is what keeps a flood (<c>yes</c> scrolls millions of lines a second) from turning into gigabytes of
/// garbage and GC pauses on every thread: the ring keeps the <see cref="Line"/> objects themselves (they are never written again while in it),
/// and <see cref="Push"/> hands back the line evicted from the front, which the screen clears and reuses as its new bottom line. Immutable
/// <see cref="TerminalRow"/> copies are made only for rows somebody reads (a frame's visible rows, a selection), and stay valid after the line
/// they were copied from is recycled.
/// </para>
/// </summary>
internal sealed class Scrollback
{
    private Line[] _ring;
    private int _start;
    private long _bytes;

    public Scrollback(int maxLines, long maxBytes)
    {
        MaxLines = Math.Max(0, maxLines);
        MaxBytes = maxBytes;
        _ring = new Line[Math.Min(Math.Max(MaxLines, 1), 1024)];
    }

    public int MaxLines { get; private set; }
    public long MaxBytes { get; }
    public int Count { get; private set; }
    public long Bytes => _bytes;

    /// <summary>Lines ever pushed (absolute number of the row after the newest).</summary>
    public long TotalPushed { get; private set; }

    /// <summary>Absolute number of the oldest kept row.</summary>
    public long FirstAbsolute => TotalPushed - Count;

    /// <summary>The kept row <paramref name="index"/> (0 = oldest) as an immutable row (cached by its line).</summary>
    public TerminalRow this[int index] => _ring[(_start + index) % _ring.Length].Snapshot();

    /// <summary>The kept line <paramref name="index"/> (0 = oldest), for reads that need no copy (marks).</summary>
    internal Line LineAt(int index) => _ring[(_start + index) % _ring.Length];

    /// <summary>
    /// What a kept line costs against <see cref="MaxBytes"/>: cells and the line's cluster and link strings, so output made of long OSC 8 links
    /// or long grapheme clusters cannot hold hundreds of MB behind a 32 MB cap. Counted once when the line comes in (and subtracted as counted).
    /// </summary>
    private static long Account(Line line) => line.AccountedBytes = line.EstimatedBytes();

    /// <summary>
    /// Keeps <paramref name="line"/> (which left the screen) as the newest row. Returns a line the caller may reuse: one that fell off the
    /// front of the ring, or <paramref name="line"/> itself when nothing is kept (scrollback 0); null when nothing fell off.
    /// </summary>
    public Line? Push(Line line)
    {
        TotalPushed++;
        if (MaxLines == 0) return line;
        Line? evicted = null;
        if (Count == MaxLines) evicted = DropOldest();
        if (Count == _ring.Length) Grow();
        _ring[(_start + Count) % _ring.Length] = line;
        Count++;
        _bytes += Account(line);
        while (_bytes > MaxBytes && Count > 1)
        {
            var dropped = DropOldest();
            evicted ??= dropped;
        }
        return evicted;
    }

    /// <summary>Removes and returns the newest line (pulled back onto a screen that grew taller).</summary>
    public Line? PopNewest()
    {
        if (Count == 0) return null;
        var idx = (_start + Count - 1) % _ring.Length;
        var line = _ring[idx];
        _ring[idx] = null!;
        Count--;
        TotalPushed--;
        _bytes -= line.AccountedBytes;
        return line;
    }

    private Line DropOldest()
    {
        var line = _ring[_start];
        _bytes -= line.AccountedBytes;
        _ring[_start] = null!;
        _start = (_start + 1) % _ring.Length;
        Count--;
        return line;
    }

    private void Grow()
    {
        var bigger = new Line[Math.Min(Math.Max(_ring.Length * 2, 16), Math.Max(MaxLines, 1))];
        for (var i = 0; i < Count; i++) bigger[i] = _ring[(_start + i) % _ring.Length];
        _ring = bigger;
        _start = 0;
    }

    public void Clear()
    {
        Array.Clear(_ring);
        _start = 0;
        Count = 0;
        _bytes = 0;
        if (_ring.Length > 1024) _ring = new Line[1024];
    }

    /// <summary>Replaces the content with copies of <paramref name="rows"/> at <paramref name="columns"/> wide (reflow rebuilt them).</summary>
    public void Replace(IReadOnlyList<TerminalRow> rows, long totalPushed, int columns)
    {
        Clear();
        foreach (var r in rows.Skip(Math.Max(0, rows.Count - Math.Max(MaxLines, 0))))
        {
            var line = new Line(columns);
            line.Load(r);
            Push(line);
        }
        TotalPushed = totalPushed;
    }
}
