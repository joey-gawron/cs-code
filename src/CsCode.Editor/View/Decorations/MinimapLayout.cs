namespace CsCode.Editor.View.Decorations;

/// <summary>
/// Which document lines the minimap shows as rows: every line, minus the lines a collapsed fold hides (VS Code's minimap shows view lines too).
/// A wrapped line stays ONE row (its first <c>editor.minimap.maxColumn</c> columns). Immutable; built on the UI thread from the folds, used by
/// the slider math and the renderer. Line and row numbers are 0-based. O(log folds) per lookup, O(folds) to build.
/// </summary>
internal sealed class MinimapRowMap
{
    private readonly int[] _first;   // first hidden line of each range (sorted, disjoint, merged)
    private readonly int[] _last;    // last hidden line of each range
    private readonly int[] _before;  // hidden lines in the ranges before range i

    private MinimapRowMap(int lineCount, int[] first, int[] last, int[] before, int hidden)
    {
        LineCount = lineCount;
        _first = first;
        _last = last;
        _before = before;
        RowCount = Math.Max(1, lineCount - hidden);
    }

    public static readonly MinimapRowMap Empty = Create(1, []);

    public int LineCount { get; }
    public int RowCount { get; }
    public int HiddenRangeCount => _first.Length;

    /// <summary>
    /// <paramref name="hidden"/>: ranges of hidden lines (inclusive, any order, may overlap or nest: nested folds inside a collapsed one).
    /// </summary>
    public static MinimapRowMap Create(int lineCount, IEnumerable<(int First, int Last)> hidden)
    {
        lineCount = Math.Max(1, lineCount);
        var ranges = hidden
            .Select(r => (First: Math.Max(0, r.First), Last: Math.Min(lineCount - 1, r.Last)))
            .Where(r => r.Last >= r.First)
            .OrderBy(r => r.First)
            .ToList();
        var first = new List<int>(ranges.Count);
        var last = new List<int>(ranges.Count);
        foreach (var (f, l) in ranges)
        {
            if (first.Count > 0 && f <= last[^1] + 1) last[^1] = Math.Max(last[^1], l);
            else { first.Add(f); last.Add(l); }
        }
        var before = new int[first.Count];
        var hiddenTotal = 0;
        for (var i = 0; i < first.Count; i++)
        {
            before[i] = hiddenTotal;
            hiddenTotal += last[i] - first[i] + 1;
        }
        return new MinimapRowMap(lineCount, [.. first], [.. last], before, hiddenTotal);
    }

    /// <summary>True when the ranges are the same as <paramref name="other"/>'s (a re-render is not needed for a layout pass that changed nothing).</summary>
    public bool SameAs(MinimapRowMap other) =>
        LineCount == other.LineCount && _first.AsSpan().SequenceEqual(other._first) && _last.AsSpan().SequenceEqual(other._last);

    /// <summary>The row of document line <paramref name="line"/>; a hidden line maps to the row of the fold's header (the line before the range).</summary>
    public int RowOfLine(int line)
    {
        line = Math.Clamp(line, 0, LineCount - 1);
        // the last range starting at or before the line
        var i = UpperBound(_first, line) - 1;
        if (i < 0) return Math.Min(line, RowCount - 1);
        if (line <= _last[i]) return Math.Clamp(_first[i] - 1 - _before[i], 0, RowCount - 1); // hidden: the header's row
        return Math.Clamp(line - _before[i] - (_last[i] - _first[i] + 1), 0, RowCount - 1);
    }

    /// <summary>The (visible) document line shown on row <paramref name="row"/>.</summary>
    public int LineOfRow(int row)
    {
        row = Math.Clamp(row, 0, RowCount - 1);
        // range i starts after (_first[i] - _before[i]) visible rows; find the last range whose start row is <= row
        int lo = 0, hi = _first.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_first[mid] - _before[mid] <= row) lo = mid + 1; else hi = mid;
        }
        var i = lo - 1;
        if (i < 0) return Math.Min(row, LineCount - 1);
        return Math.Min(row + _before[i] + (_last[i] - _first[i] + 1), LineCount - 1);
    }

    private static int UpperBound(int[] a, int value)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (a[mid] <= value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}

/// <summary>Where the minimap's window of rows starts and where its slider is, for one scroll position (VS Code's "proportional" minimap).</summary>
internal readonly record struct MinimapFrame(int WindowTopRow, int WindowRows, double SliderTop, double SliderHeight);

/// <summary>
/// The minimap's scroll geometry. When every row fits, the minimap shows them all from the top and the slider moves over them. When not (a long
/// file), the minimap shows a window of rows that scrolls with the editor in proportion — the window's top is the same fraction of the rows
/// the window cannot show as the editor's top is of the rows it can scroll — so the slider still covers exactly the rows on screen, and
/// reaches the minimap's bottom when the editor reaches the end. Pure; unit tested.
/// </summary>
internal static class MinimapGeometry
{
    /// <param name="rowCount">Rows of the document (<see cref="MinimapRowMap.RowCount"/>).</param>
    /// <param name="topRow">The editor's top row, fractional (part of the first line scrolled away).</param>
    /// <param name="viewportRows">Rows the editor shows at once.</param>
    /// <param name="rowHeight">Height of one minimap row (DIPs).</param>
    /// <param name="height">Height of the minimap (DIPs).</param>
    public static MinimapFrame Compute(int rowCount, double topRow, double viewportRows, double rowHeight, double height)
    {
        if (rowHeight <= 0 || height <= 0) return new MinimapFrame(0, 0, 0, 0);
        var windowRows = Math.Max(1, (int)Math.Floor(height / rowHeight));
        viewportRows = Math.Max(1, viewportRows);
        topRow = Math.Clamp(topRow, 0, Math.Max(0, rowCount - 1));
        if (rowCount <= windowRows)
            return new MinimapFrame(0, windowRows, topRow * rowHeight, viewportRows * rowHeight);
        var windowTop = WindowTop(rowCount, topRow, viewportRows, windowRows);
        return new MinimapFrame(windowTop, windowRows, (topRow - windowTop) * rowHeight, viewportRows * rowHeight);
    }

    private static int WindowTop(int rowCount, double topRow, double viewportRows, int windowRows)
    {
        var scrollable = Math.Max(1e-9, rowCount - viewportRows);
        var ratio = Math.Clamp(topRow / scrollable, 0, 1);
        return Math.Clamp((int)Math.Round(ratio * (rowCount - windowRows)), 0, rowCount - windowRows);
    }

    /// <summary>The editor's top row that puts the slider's top at <paramref name="sliderTop"/> (dragging the slider, or a click centring it).</summary>
    public static double TopRowForSliderTop(double sliderTop, int rowCount, double viewportRows, double rowHeight, double height)
    {
        if (rowHeight <= 0 || height <= 0) return 0;
        var windowRows = Math.Max(1, (int)Math.Floor(height / rowHeight));
        viewportRows = Math.Max(1, viewportRows);
        var maxTop = Math.Max(0, rowCount - 1);
        if (rowCount <= windowRows) return Math.Clamp(sliderTop / rowHeight, 0, maxTop);
        // sliderTop / rowHeight = topRow - ratio * (rowCount - windowRows), ratio = topRow / (rowCount - viewportRows)
        var scrollable = Math.Max(1e-9, rowCount - viewportRows);
        var factor = 1 - (rowCount - windowRows) / scrollable;
        double top;
        if (factor > 1e-3) top = sliderTop / rowHeight / factor;
        else
        {
            // the window is about as tall as the viewport: map the slider's travel onto the scroll range directly
            var travel = Math.Max(1e-9, height - viewportRows * rowHeight);
            top = Math.Clamp(sliderTop / travel, 0, 1) * scrollable;
        }
        return Math.Clamp(top, 0, Math.Min(maxTop, scrollable));
    }
}
