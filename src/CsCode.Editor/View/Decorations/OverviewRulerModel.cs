namespace CsCode.Editor.View.Decorations;

/// <summary>
/// What a mark on the overview ruler (and the minimap) stands for. The order is the priority within a lane: when two marks fall on the same pixel
/// row of a lane, the higher one is drawn (an error beats a warning, a find match beats a word highlight).
/// </summary>
internal enum MarkerKind : byte
{
    None = 0,
    // left lane: source control, bookmarks and breakpoints (a breakpoint wins over a bookmark on the same row, a bookmark over a change)
    Added, Modified, Deleted, Bookmark, Breakpoint,
    // centre lane: occurrences and find
    WordHighlight, WordHighlightStrong, FindMatch,
    // right lane: diagnostics
    Info, Warning, Error,
    // all lanes: merge conflict sides (VS Code draws them across the ruler), then the caret lines
    MergeCommon, MergeCurrent, MergeIncoming,
    Cursor,
}

internal enum MarkerLane { Left, Center, Right, Full }

/// <summary>A mark covering document lines [<see cref="Line"/>, <see cref="EndLine"/>] (0-based, inclusive).</summary>
internal readonly record struct LineMarker(int Line, int EndLine, MarkerKind Kind);

/// <summary>
/// The overview ruler's pixel rows: for each lane, the kind drawn on each pixel row (bucketed: a 1M-line file with 20,000 find matches is still
/// at most one entry per pixel row per lane, so drawing costs O(height) whatever the document). Pure; the UI computes each marker's pixel span
/// from AvaloniaEdit's height tree (so folding and word wrap count) and this class merges them.
/// </summary>
internal sealed class OverviewRulerModel
{
    private MarkerKind[] _left = [], _center = [], _right = [], _full = [];

    public int Height { get; private set; }

    public static MarkerLane LaneOf(MarkerKind kind) => kind switch
    {
        MarkerKind.Added or MarkerKind.Modified or MarkerKind.Deleted or MarkerKind.Bookmark or MarkerKind.Breakpoint => MarkerLane.Left,
        MarkerKind.WordHighlight or MarkerKind.WordHighlightStrong or MarkerKind.FindMatch => MarkerLane.Center,
        MarkerKind.Info or MarkerKind.Warning or MarkerKind.Error => MarkerLane.Right,
        _ => MarkerLane.Full,
    };

    /// <summary>Starts a new layout of <paramref name="height"/> pixel rows (arrays are reused when the height did not change).</summary>
    public void Reset(int height)
    {
        height = Math.Max(0, height);
        if (height != Height || _left.Length != height)
        {
            Height = height;
            _left = new MarkerKind[height];
            _center = new MarkerKind[height];
            _right = new MarkerKind[height];
            _full = new MarkerKind[height];
        }
        else
        {
            Array.Clear(_left);
            Array.Clear(_center);
            Array.Clear(_right);
            Array.Clear(_full);
        }
    }

    /// <summary>Marks pixel rows [<paramref name="top"/>, <paramref name="top"/> + <paramref name="height"/>) with <paramref name="kind"/> where nothing higher is.</summary>
    public void Add(double top, double height, MarkerKind kind)
    {
        if (Height == 0 || kind == MarkerKind.None) return;
        var lane = Lane(LaneOf(kind));
        var from = Math.Clamp((int)Math.Floor(top), 0, Height - 1);
        var to = Math.Clamp((int)Math.Ceiling(top + Math.Max(1, height)), from + 1, Height);
        for (var y = from; y < to; y++)
            if (lane[y] < kind) lane[y] = kind;
    }

    /// <summary>The kind on pixel row <paramref name="y"/> of <paramref name="lane"/> (tests, hit testing).</summary>
    public MarkerKind At(MarkerLane lane, int y) => y >= 0 && y < Height ? Lane(lane)[y] : MarkerKind.None;

    /// <summary>
    /// The runs of equal kinds in a lane, top to bottom: what is drawn (one rectangle per run). <paramref name="into"/> is cleared first.
    /// </summary>
    public void Runs(MarkerLane lane, List<(int Top, int Height, MarkerKind Kind)> into)
    {
        into.Clear();
        var a = Lane(lane);
        var y = 0;
        while (y < a.Length)
        {
            var k = a[y];
            var start = y;
            while (y < a.Length && a[y] == k) y++;
            if (k != MarkerKind.None) into.Add((start, y - start, k));
        }
    }

    private MarkerKind[] Lane(MarkerLane lane) => lane switch
    {
        MarkerLane.Left => _left,
        MarkerLane.Center => _center,
        MarkerLane.Right => _right,
        _ => _full,
    };
}
