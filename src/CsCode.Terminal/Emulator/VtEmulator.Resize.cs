namespace CsCode.Terminal.Emulator;

public sealed partial class VtEmulator
{
    /// <summary>
    /// Resizes both screens. Rows: shrinking first drops blank rows below the cursor, then pushes rows from the top into the scrollback;
    /// growing pulls rows back from the scrollback (as xterm.js does). Columns: with <see cref="EmulatorOptions.Reflow"/> the primary screen
    /// and its scrollback are re-wrapped (soft-wrapped lines are joined and split at the new width, the cursor follows its character);
    /// otherwise, and always on the alternate screen, rows are truncated or padded as xterm does.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, 4096);
        rows = Math.Clamp(rows, 1, 4096);
        if (columns == Columns && rows == Rows) return;
        if (columns != _primary.Columns && Options.Reflow) ReflowPrimary(columns, rows);
        else ResizeBuffer(_primary, columns, rows);
        ResizeBuffer(_alternate, columns, rows);
        if (!FollowOutput) ViewportTop = Math.Clamp(ViewportTop, ScrollbackTop, ScreenTop);
        SyncViewport();
        ChangeCount++;
    }

    private void ResizeBuffer(ScreenBuffer b, int columns, int rows)
    {
        if (columns != b.Columns)
        {
            foreach (var line in b.Lines) line.Resize(columns);
            b.TabStops = ResizeTabs(b.TabStops, columns);
            b.Columns = columns;
        }
        ResizeRows(b, rows);
        FinishResize(b);
    }

    private static bool[] ResizeTabs(bool[] old, int columns)
    {
        var tabs = ScreenBuffer.DefaultTabs(columns);
        Array.Copy(old, tabs, Math.Min(old.Length, columns));
        return tabs;
    }

    private static bool IsBlank(Line line)
    {
        foreach (var c in line.Cells) if (!c.IsBlankDefault) return false;
        return true;
    }

    private void ResizeRows(ScreenBuffer b, int rows)
    {
        var old = b.Rows;
        if (rows == old) return;
        if (rows < old)
        {
            var remove = old - rows;
            var lines = b.Lines.ToList();
            for (var r = lines.Count - 1; remove > 0 && r > b.CursorY && IsBlank(lines[r]); r--)
            {
                lines.RemoveAt(r);
                remove--;
            }
            for (var i = 0; i < remove; i++)
            {
                if (b.Scrollback is not null) b.Scrollback.Push(lines[0].Snapshot());
                lines.RemoveAt(0);
                b.CursorY--;
            }
            b.Lines = lines.ToArray();
        }
        else
        {
            var add = rows - old;
            var lines = new List<Line>(rows);
            var pulled = new List<Line>();
            while (b.Scrollback is { Count: > 0 } sb && pulled.Count < add)
            {
                var row = sb.PopNewest()!;
                var line = new Line(b.Columns);
                line.Load(row);
                pulled.Insert(0, line);
            }
            lines.AddRange(pulled);
            lines.AddRange(b.Lines);
            while (lines.Count < rows) lines.Add(new Line(b.Columns));
            b.CursorY += pulled.Count;
            b.Lines = lines.ToArray();
        }
        b.Rows = rows;
    }

    private static void FinishResize(ScreenBuffer b)
    {
        b.ScrollTop = 0;
        b.ScrollBottom = b.Rows - 1;
        if (b.CursorX >= b.Columns) { b.CursorX = b.Columns - 1; }
        b.ClampCursor();
        if (b.Saved is { } saved) b.Saved = saved with { X = Math.Min(saved.X, b.Columns - 1), Y = Math.Clamp(saved.Y, 0, b.Rows - 1) };
        foreach (var line in b.Lines) line.Touch();
    }

    // ------------------------------------------------------------------ reflow

    private void ReflowPrimary(int columns, int rows)
    {
        var b = _primary;
        var sb = b.Scrollback!;
        var oldColumns = b.Columns;
        var firstAbsolute = sb.FirstAbsolute;
        var all = new List<TerminalRow>(sb.Count + b.Rows);
        for (var i = 0; i < sb.Count; i++) all.Add(sb[i]);
        foreach (var line in b.Lines) all.Add(line.Snapshot());

        var cursorIndex = sb.Count + b.CursorY;
        var cursorX = b.PendingWrap ? oldColumns : b.CursorX;
        var last = all.Count - 1;
        while (last > cursorIndex && all[last].Length == 0 && !all[last].IsWrapped) last--;

        var result = new List<TerminalRow>(all.Count);
        int newCursorRow = 0, newCursorX = 0;
        var start = 0;
        while (start <= last)
        {
            var end = start;
            while (end + 1 <= last && all[end + 1].IsWrapped) end++;
            // the logical line's cells: wrapped rows count with their full old width, the last one with its own length
            var cells = new List<(Cell Cell, TerminalRow Row)>();
            var cursorOffset = -1;
            for (var r = start; r <= end; r++)
            {
                var row = all[r];
                var width = r < end ? oldColumns : row.Length;
                if (r == cursorIndex) cursorOffset = cells.Count + cursorX;
                for (var c = 0; c < width; c++) cells.Add((row[c], row));
            }
            if (cursorIndex > end || cursorIndex < start) cursorOffset = -1;

            // split at the new width without cutting a wide character
            var line = new Line(columns);
            var x = 0;
            var first = true;
            for (var i = 0; i <= cells.Count; i++)
            {
                if (cursorOffset == i) { newCursorRow = result.Count; newCursorX = x; }
                if (i == cells.Count) break;
                var (cell, src) = cells[i];
                if (cell.IsSpacer) continue;
                var w = cell.IsWide ? 2 : 1;
                if (x + w > columns)
                {
                    result.Add(Finish(line, first));
                    first = false;
                    line = new Line(columns);
                    x = 0;
                    if (cursorOffset == i) { newCursorRow = result.Count; newCursorX = 0; }
                }
                var copy = cell;
                if (copy.Rune < 0) copy.Rune = line.AddCluster(src.RuneText(copy.Rune));
                if (copy.Attributes.LinkId > 0)
                    copy.Attributes = copy.Attributes with { LinkId = src.Link(copy.Attributes.LinkId) is { } uri ? line.InternLink(uri) : 0 };
                line.Cells[x] = copy;
                if (w == 2 && x + 1 < columns) line.Cells[x + 1] = new Cell(0, copy.Attributes with { Flags = (copy.Attributes.Flags & ~CellFlags.Wide) | CellFlags.WideSpacer });
                x += w;
            }
            result.Add(Finish(line, first));
            if (cursorOffset > cells.Count)
            {
                // the cursor stood past the row's content (blank cells are trimmed): keep its distance from the end of the text
                newCursorRow = result.Count - 1;
                newCursorX = Math.Min(columns - 1, x + (cursorOffset - cells.Count));
            }
            else if (cursorOffset >= 0 && newCursorX >= columns) { newCursorRow = Math.Min(newCursorRow + 1, result.Count); newCursorX = 0; }
            start = end + 1;

            TerminalRow Finish(Line l, bool isFirst)
            {
                l.Wrapped = !isFirst;
                if (isFirst) { l.Mark = all[start].Mark; l.MarkExitCode = all[start].MarkExitCode; }
                return l.Snapshot();
            }
        }
        if (cursorIndex > last) { newCursorRow = result.Count + (cursorIndex - last - 1); newCursorX = Math.Min(b.CursorX, columns - 1); }

        // The screen shows the last rows, but never starts below the cursor's row.
        var screenStart = Math.Max(0, Math.Max(result.Count, newCursorRow + 1) - rows);
        screenStart = Math.Min(screenStart, newCursorRow);
        var screenLines = new Line[rows];
        for (var r = 0; r < rows; r++)
        {
            var line = new Line(columns);
            var idx = screenStart + r;
            if (idx < result.Count) line.Load(result[idx]);
            screenLines[r] = line;
        }
        sb.Replace(result.Take(screenStart).ToList(), firstAbsolute + screenStart);
        b.Lines = screenLines;
        b.Columns = columns;
        b.Rows = rows;
        b.TabStops = ResizeTabs(b.TabStops, columns);
        b.CursorY = Math.Clamp(newCursorRow - screenStart, 0, rows - 1);
        b.CursorX = Math.Clamp(newCursorX, 0, columns - 1);
        b.PendingWrap = false;
        FinishResize(b);
    }
}
