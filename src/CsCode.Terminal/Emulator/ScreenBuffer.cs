namespace CsCode.Terminal.Emulator;

/// <summary>The character sets of ISO 2022 designation (G0-G3) that terminals still meet: ASCII, the UK set and DEC Special Graphics.</summary>
public enum Charset : byte { Ascii, Uk, DecSpecialGraphics }

/// <summary>What DECSC saves (xterm: position, rendition, character sets, origin mode, the pending-wrap flag).</summary>
internal sealed record SavedCursor(int X, int Y, bool PendingWrap, CellAttributes Attributes, Charset G0, Charset G1, Charset G2, Charset G3, int Gl, bool OriginMode);

/// <summary>
/// One screen (the primary one with its scrollback, or the alternate one without): its lines, cursor, scroll region, tab stops and saved
/// cursor. Pure data plus the grid primitives (scrolling a region, inserting/deleting lines); the sequences live in <see cref="VtEmulator"/>.
/// </summary>
internal sealed class ScreenBuffer
{
    public Line[] Lines;
    public int Columns;
    public int Rows;
    public int CursorX;
    public int CursorY;
    public bool PendingWrap;
    public int ScrollTop;
    public int ScrollBottom;
    public bool[] TabStops;
    public SavedCursor? Saved;
    public readonly Scrollback? Scrollback;

    public ScreenBuffer(int columns, int rows, Scrollback? scrollback)
    {
        Columns = columns;
        Rows = rows;
        Lines = new Line[rows];
        for (var i = 0; i < rows; i++) Lines[i] = new Line(columns);
        ScrollBottom = rows - 1;
        TabStops = DefaultTabs(columns);
        Scrollback = scrollback;
    }

    public static bool[] DefaultTabs(int columns)
    {
        var tabs = new bool[columns];
        for (var i = 8; i < columns; i += 8) tabs[i] = true;
        return tabs;
    }

    public Line Current => Lines[CursorY];

    /// <summary>
    /// Scrolls rows [top, bottom] up by <paramref name="count"/>: the top rows leave (into the scrollback when the region starts at the top
    /// of the primary screen and <paramref name="toScrollback"/>), blank rows filled with <paramref name="fill"/> enter at the bottom.
    /// The leaving Line objects are reused for the new rows (their immutable snapshots, if any, are what the scrollback keeps).
    /// </summary>
    public void ScrollUp(int top, int bottom, int count, in Cell fill, bool toScrollback)
    {
        var height = bottom - top + 1;
        if (count <= 0 || height <= 0) return;
        count = Math.Min(count, height);
        var keep = Scrollback is not null && toScrollback && top == 0;
        if (count == 1)
        {
            // The hot path of a flood (one line feed at the bottom): no allocation at all. The leaving line goes into the scrollback and
            // the one that fell off the scrollback's front comes back as the new bottom line.
            var line = Lines[top];
            var reuse = keep ? Recycled(Scrollback!.Push(line)) : line;
            Array.Copy(Lines, top + 1, Lines, top, height - 1);
            reuse.Clear(fill);
            Lines[bottom] = reuse;
            return;
        }
        var leaving = new Line[count];
        for (var i = 0; i < count; i++)
        {
            var line = Lines[top + i];
            leaving[i] = keep ? Recycled(Scrollback!.Push(line)) : line;
        }
        Array.Copy(Lines, top + count, Lines, top, height - count);
        for (var i = 0; i < count; i++)
        {
            var line = leaving[i];
            line.Clear(fill);
            Lines[bottom - count + 1 + i] = line;
        }
        // Rows that moved keep their content (and cached snapshot): only their position changed.
    }

    /// <summary>A line the scrollback let go of, if it has this screen's width; else a new one.</summary>
    private Line Recycled(Line? evicted) => evicted is not null && evicted.Columns == Columns ? evicted : new Line(Columns);

    /// <summary>Scrolls rows [top, bottom] down by <paramref name="count"/> (blank rows enter at the top).</summary>
    public void ScrollDown(int top, int bottom, int count, in Cell fill)
    {
        var height = bottom - top + 1;
        if (count <= 0 || height <= 0) return;
        count = Math.Min(count, height);
        var leaving = new Line[count];
        for (var i = 0; i < count; i++) leaving[i] = Lines[bottom - count + 1 + i];
        Array.Copy(Lines, top, Lines, top + count, height - count);
        for (var i = 0; i < count; i++)
        {
            leaving[i].Clear(fill);
            Lines[top + i] = leaving[i];
        }
    }

    public void ClampCursor()
    {
        CursorX = Math.Clamp(CursorX, 0, Columns - 1);
        CursorY = Math.Clamp(CursorY, 0, Rows - 1);
    }
}
