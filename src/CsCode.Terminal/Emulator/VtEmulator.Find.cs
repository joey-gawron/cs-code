using System.Text;

namespace CsCode.Terminal.Emulator;

/// <summary>A match of <see cref="VtEmulator.Find"/>: its absolute row and first and last column (inclusive; a wide character's two cells).</summary>
public readonly record struct TerminalMatch(long Line, int StartColumn, int EndColumn);

public sealed partial class VtEmulator
{
    /// <summary>
    /// Find in terminal (VS Code's Ctrl+F): the next match strictly after (<paramref name="backward"/>: before) the cell
    /// (<paramref name="fromLine"/>, <paramref name="fromColumn"/>), row by row through the scrollback and the screen (the alternate screen:
    /// its rows only), wrapping around once. A start outside the rows starts at the end the search moves away from (a backward search from
    /// <see cref="long.MaxValue"/> begins at the bottom). Matches do not span rows. Reads the lines in place (no row copies). Worker only.
    /// </summary>
    public TerminalMatch? Find(string query, long fromLine, int fromColumn, bool backward, bool matchCase)
    {
        if (string.IsNullOrEmpty(query)) return null;
        var (first, last) = IsAlternateScreen ? (0L, (long)Rows - 1) : (ScrollbackTop, ScreenTop + Rows - 1);
        if (last < first) return null;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var inRange = fromLine >= first && fromLine <= last;
        var line = inRange ? fromLine : backward ? last : first;
        var text = new StringBuilder();
        var columns = new List<int>();
        var total = last - first + 1;
        for (long i = 0; i <= total; i++)
        {
            var source = LineAtAbsolute(line);
            var row = RowText(source, text, columns);
            // the starting row is searched after/before the start; the last pass (back at the start) searches it whole
            var restrict = inRange && i == 0;
            int index;
            if (!backward)
            {
                var from = restrict ? FirstIndexAfter(columns, fromColumn) : 0;
                index = from >= row.Length ? -1 : row.IndexOf(query, from, comparison);
            }
            else
            {
                var end = restrict ? FirstIndexAtOrAfter(columns, fromColumn) : row.Length; // matches must start before this index
                var window = Math.Min(row.Length, end + query.Length - 1);
                index = end <= 0 || window < query.Length ? -1 : row.AsSpan(0, window).LastIndexOf(query.AsSpan(), comparison);
            }
            if (index >= 0)
            {
                var start = columns[index];
                var stop = columns[index + query.Length - 1];
                if (source.Cells[stop].IsWide && stop + 1 < source.Columns) stop++;
                return new TerminalMatch(line, start, stop);
            }
            line += backward ? -1 : 1;
            if (line > last) line = first;
            if (line < first) line = last;
        }
        return null;
    }

    private Line LineAtAbsolute(long absolute) =>
        IsAlternateScreen ? _screen.Lines[absolute]
        : absolute >= ScreenTop ? _screen.Lines[absolute - ScreenTop] : Scrollback.LineAt((int)(absolute - ScrollbackTop));

    /// <summary>A line's text (blanks as spaces, spacers skipped) and, per UTF-16 unit, the column it came from.</summary>
    private static string RowText(Line line, StringBuilder text, List<int> columns)
    {
        text.Clear();
        columns.Clear();
        var cells = line.Cells;
        for (var c = 0; c < cells.Length; c++)
        {
            var cell = cells[c];
            if (cell.IsSpacer) continue;
            var rune = cell.Rune;
            if (rune == 0) { text.Append(' '); columns.Add(c); }
            else if (rune > 0 && rune < 0x10000) { text.Append((char)rune); columns.Add(c); }
            else
            {
                var s = line.ClusterText(rune);
                text.Append(s);
                for (var k = 0; k < s.Length; k++) columns.Add(c);
            }
        }
        return text.ToString();
    }

    private static int FirstIndexAfter(List<int> columns, int column)
    {
        for (var i = 0; i < columns.Count; i++)
            if (columns[i] > column) return i;
        return columns.Count;
    }

    private static int FirstIndexAtOrAfter(List<int> columns, int column)
    {
        for (var i = 0; i < columns.Count; i++)
            if (columns[i] >= column) return i;
        return columns.Count;
    }
}
