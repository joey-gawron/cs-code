using System.Text;
using CsCode.Terminal.Emulator;

namespace CsCode.Panels.Terminal.Input;

public enum SelectionMode { Character, Word, Line, Block }

/// <summary>A cell by absolute row number (scrollback + screen; stable while rows scroll) and column.</summary>
public readonly record struct GridPoint(long Line, int Column) : IComparable<GridPoint>
{
    public int CompareTo(GridPoint other) => Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);
    public static bool operator <(GridPoint a, GridPoint b) => a.CompareTo(b) < 0;
    public static bool operator >(GridPoint a, GridPoint b) => a.CompareTo(b) > 0;
    public static bool operator <=(GridPoint a, GridPoint b) => a.CompareTo(b) <= 0;
    public static bool operator >=(GridPoint a, GridPoint b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// The terminal's selection: anchor and active cell (inclusive) in absolute rows, by character (drag), word (double-click, separators from
/// <c>terminal.integrated.wordSeparators</c>), line (triple-click; a soft-wrapped line is one line) or block (Alt+drag). Immutable; the
/// view replaces it as the mouse moves. Text extraction joins soft-wrapped rows without a line break, as xterm.js copies them.
/// </summary>
public sealed record TerminalSelection(GridPoint Anchor, GridPoint Active, SelectionMode Mode)
{
    public GridPoint Start => Anchor <= Active ? Anchor : Active;
    public GridPoint End => Anchor <= Active ? Active : Anchor;

    /// <summary>Whether the cell is selected (block: inside the rectangle).</summary>
    public bool Contains(long line, int column)
    {
        var s = Start;
        var e = End;
        if (line < s.Line || line > e.Line) return false;
        if (Mode == SelectionMode.Block)
        {
            var (c0, c1) = (Math.Min(Anchor.Column, Active.Column), Math.Max(Anchor.Column, Active.Column));
            return column >= c0 && column <= c1;
        }
        if (line == s.Line && column < s.Column) return false;
        if (line == e.Line && column > e.Column) return false;
        return true;
    }

    /// <summary>The selected columns of <paramref name="line"/> as [start, end] inclusive, or null.</summary>
    public (int Start, int End)? ColumnsOn(long line, int columns)
    {
        var s = Start;
        var e = End;
        if (line < s.Line || line > e.Line) return null;
        if (Mode == SelectionMode.Block) return (Math.Min(Anchor.Column, Active.Column), Math.Min(columns - 1, Math.Max(Anchor.Column, Active.Column)));
        var from = line == s.Line ? s.Column : 0;
        var to = line == e.Line ? e.Column : columns - 1;
        return from <= to ? (from, Math.Min(to, columns - 1)) : null;
    }

    /// <summary>The selected text: rows joined with '\n' except where a row continues the previous one (soft wrap); trailing blanks trimmed.</summary>
    public string GetText(Func<long, TerminalRow?> rowAt, int columns)
    {
        var sb = new StringBuilder();
        var s = Start;
        var e = End;
        for (var line = s.Line; line <= e.Line; line++)
        {
            var row = rowAt(line);
            if (line > s.Line)
            {
                var wrapped = Mode != SelectionMode.Block && row is { IsWrapped: true };
                if (!wrapped) sb.Append('\n');
            }
            if (row is null || ColumnsOn(line, columns) is not { } cols) continue;
            var piece = new StringBuilder();
            row.AppendText(piece, cols.Start, cols.End + 1);
            var nextWrapped = Mode != SelectionMode.Block && line < e.Line && rowAt(line + 1) is { IsWrapped: true };
            var text = piece.ToString();
            sb.Append(nextWrapped ? text : text.TrimEnd(' '));
        }
        return sb.ToString();
    }

    /// <summary>The word around <paramref name="column"/>: a run of characters that are neither blanks nor <paramref name="separators"/>.</summary>
    public static (int Start, int End) WordAt(TerminalRow row, int column, int columns, string separators)
    {
        bool IsWordChar(int c)
        {
            if (c < 0 || c >= columns) return false;
            var cell = row[c];
            if (cell.IsSpacer) return c > 0 && IsWordChar(c - 1);
            var text = row.CellText(c);
            return text != " " && text.Length > 0 && separators.IndexOf(text[0]) < 0;
        }
        if (!IsWordChar(column)) return (column, row[column].IsWide ? Math.Min(columns - 1, column + 1) : column);
        var start = column;
        var end = column;
        while (start > 0 && IsWordChar(start - 1)) start--;
        while (end < columns - 1 && IsWordChar(end + 1)) end++;
        return (start, end);
    }

    /// <summary>A whole logical line around <paramref name="line"/>: back to the row that is not a continuation, forward over continuations.</summary>
    public static (long First, long Last) LogicalLine(Func<long, TerminalRow?> rowAt, long line, long minLine, long maxLine)
    {
        var first = line;
        while (first > minLine && rowAt(first) is { IsWrapped: true }) first--;
        var last = line;
        while (last < maxLine && rowAt(last + 1) is { IsWrapped: true }) last++;
        return (first, last);
    }
}
