using System.Text;

namespace CsCode.Terminal.Emulator;

/// <summary>Limits of one emulator (scrollback lines from <c>terminal.integrated.scrollback</c>, plus the hard memory cap).</summary>
public sealed record EmulatorOptions
{
    public int ScrollbackLines { get; init; } = 1000;
    /// <summary>Hard cap of the scrollback's managed memory, whatever the line setting (default 32 MB).</summary>
    public long ScrollbackMaxBytes { get; init; } = 32L * 1024 * 1024;
    /// <summary>OSC 52 clipboard writes are honoured only when allowed (<c>terminal.integrated.allowOsc52</c>, off by default).</summary>
    public bool AllowOsc52 { get; init; }
    public ITerminalPalette Palette { get; init; } = DefaultTerminalPalette.Instance;
    /// <summary>Re-flow wrapped lines when the width changes (<c>terminal.integrated.reflow</c>).</summary>
    public bool Reflow { get; init; } = true;
}

/// <summary>
/// The terminal emulator: an <see cref="IVtHandler"/> over two <see cref="ScreenBuffer"/>s (primary with scrollback, alternate without),
/// implementing xterm's sequences as xterm.js supports them (see docs/TERMINAL.md for the list). Single-threaded: the owning session's worker
/// is the only thread that feeds or reads it; the UI only ever sees immutable <see cref="TerminalSnapshot"/>s. Answers (DSR, DA, OSC queries)
/// go to <see cref="Respond"/>, notifications (title, bell, cwd, clipboard) to the events, all on the worker thread.
/// </summary>
public sealed partial class VtEmulator : IVtHandler
{
    private readonly VtParser _parser;
    private ScreenBuffer _primary;
    private ScreenBuffer _alternate;
    private ScreenBuffer _screen;
    private CellAttributes _attr = CellAttributes.Default;
    private string? _currentLink; // OSC 8 target while a link is open
    private readonly Charset[] _g = [Charset.Ascii, Charset.Ascii, Charset.Ascii, Charset.Ascii];
    private int _gl;           // which G set is mapped to GL (SI/SO, LS2/LS3)
    private int _singleShift = -1; // SS2/SS3: the set for the next character only
    private int _lastPrinted = -1; // for REP
    private bool _afterZwj; // the last printed character was U+200D: the next one joins its cluster
    private readonly Stack<string> _titleStack = new();
    private Dictionary<int, uint>? _paletteOverrides;

    public VtEmulator(int columns, int rows, EmulatorOptions? options = null)
    {
        Options = options ?? new EmulatorOptions();
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        _primary = new ScreenBuffer(columns, rows, new Scrollback(Options.ScrollbackLines, Options.ScrollbackMaxBytes));
        _alternate = new ScreenBuffer(columns, rows, null);
        _screen = _primary;
        _parser = new VtParser(this);
        ViewportTop = 0;
    }

    public EmulatorOptions Options { get; private set; }

    // ------------------------------------------------------------------ outward

    /// <summary>Bytes the terminal answers with (DSR/CPR, DA, OSC colour queries...): the session writes them to the PTY.</summary>
    public Action<byte[]>? Respond { get; set; }
    public Action<string>? TitleChanged { get; set; }
    public Action? Bell { get; set; }
    public Action<string>? CurrentDirectoryChanged { get; set; }
    /// <summary>OSC 52 write (only when <see cref="EmulatorOptions.AllowOsc52"/>): the decoded text.</summary>
    public Action<string>? ClipboardWrite { get; set; }
    /// <summary>Shell integration (OSC 633/133): a command finished with this exit code (null: unknown).</summary>
    public Action<int?>? CommandFinished { get; set; }

    // ------------------------------------------------------------------ state

    public int Columns => _screen.Columns;
    public int Rows => _screen.Rows;
    public int CursorX => _screen.CursorX;
    public int CursorY => _screen.CursorY;
    public bool PendingWrap => _screen.PendingWrap;
    public bool IsAlternateScreen => !ReferenceEquals(_screen, _primary);
    public string Title { get; private set; } = "";
    public string? CurrentDirectory { get; private set; }
    public CellAttributes CurrentAttributes => _attr;

    public bool InsertMode { get; private set; }
    public bool AutoWrap { get; private set; } = true;
    public bool OriginMode { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool? CursorBlink { get; private set; }
    public CursorShape CursorShape { get; private set; }
    public bool ReverseVideo { get; private set; }
    public bool SynchronizedOutput { get; private set; }
    public TerminalModes Modes { get; private set; } = TerminalModes.Initial;

    /// <summary>Bumped whenever something visible changed (the session builds a snapshot only when it moved).</summary>
    public long ChangeCount { get; private set; }

    /// <summary>Bumped when <see cref="Modes"/> changed.</summary>
    public long ModesVersion { get; private set; }

    internal ScreenBuffer Screen => _screen;
    internal ScreenBuffer PrimaryBuffer => _primary;
    internal Scrollback Scrollback => _primary.Scrollback!;

    /// <summary>Absolute number of screen row 0 (rows ever pushed into the scrollback).</summary>
    public long ScreenTop => IsAlternateScreen ? 0 : Scrollback.TotalPushed;
    public long ScrollbackTop => IsAlternateScreen ? 0 : Scrollback.FirstAbsolute;
    public int ScrollbackCount => IsAlternateScreen ? 0 : Scrollback.Count;

    /// <summary>The first visible row (absolute); equal to <see cref="ScreenTop"/> while following the output.</summary>
    public long ViewportTop { get; private set; }
    public bool FollowOutput { get; private set; } = true;

    /// <summary>The immutable row at screen row <paramref name="row"/> (tests, selection).</summary>
    public TerminalRow GetScreenRow(int row) => _screen.Lines[row].Snapshot();

    /// <summary>The row with absolute number <paramref name="absolute"/> (scrollback or screen), or null when it is not kept.</summary>
    public TerminalRow? GetRow(long absolute)
    {
        if (IsAlternateScreen) return absolute >= 0 && absolute < Rows ? _screen.Lines[absolute].Snapshot() : null;
        var top = Scrollback.TotalPushed;
        if (absolute >= top) return absolute - top < Rows ? _screen.Lines[absolute - top].Snapshot() : null;
        if (absolute < Scrollback.FirstAbsolute) return null;
        return Scrollback[(int)(absolute - Scrollback.FirstAbsolute)];
    }

    /// <summary>The text of the screen (rows joined by '\n', trailing blanks trimmed): tests and the differential suite.</summary>
    public string ScreenText()
    {
        var sb = new StringBuilder();
        for (var r = 0; r < Rows; r++)
        {
            if (r > 0) sb.Append('\n');
            sb.Append(GetScreenRow(r).GetText());
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ input

    /// <summary>Feeds program output. Never throws on any input.</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        _parser.Feed(data);
        ChangeCount++;
    }

    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    // ------------------------------------------------------------------ viewport (user scrolling)

    /// <summary>Scrolls the view to absolute row <paramref name="top"/> (clamped); at or past the screen it follows the output again.</summary>
    public void ScrollViewportTo(long top)
    {
        var min = ScrollbackTop;
        var max = ScreenTop;
        top = Math.Clamp(top, min, max);
        FollowOutput = top >= max;
        if (ViewportTop != top) { ViewportTop = top; ChangeCount++; }
    }

    public void ScrollViewportBy(long delta) => ScrollViewportTo((FollowOutput ? ScreenTop : ViewportTop) + delta);

    /// <summary>
    /// VS Code's Terminal: Clear (xterm.js <c>clear()</c>): the scrollback and the screen go, the cursor's line (the prompt) becomes the first
    /// line. On the alternate screen only the screen is cleared around it.
    /// </summary>
    public void ClearKeepingCursorLine()
    {
        var s = _screen;
        if (s.CursorY > 0)
        {
            (s.Lines[0], s.Lines[s.CursorY]) = (s.Lines[s.CursorY], s.Lines[0]);
            s.Lines[0].Wrapped = false;
            s.Lines[0].Touch();
            s.CursorY = 0;
        }
        for (var r = 1; r < s.Rows; r++) s.Lines[r].Clear(Cell.Blank);
        if (!IsAlternateScreen)
        {
            var pushed = Scrollback.TotalPushed;
            Scrollback.Replace([], pushed);
        }
        FollowOutput = true;
        ViewportTop = ScreenTop;
        ChangeCount++;
    }

    public void ScrollViewportToBottom() => ScrollViewportTo(ScreenTop);

    private void SyncViewport()
    {
        if (FollowOutput || IsAlternateScreen) ViewportTop = ScreenTop;
        else if (ViewportTop < ScrollbackTop) ViewportTop = ScrollbackTop;
        else if (ViewportTop > ScreenTop) { ViewportTop = ScreenTop; FollowOutput = true; }
    }

    // ------------------------------------------------------------------ snapshot

    /// <summary>Builds the frame the renderer draws (on the worker). Rows that did not change are the same instances as last time.</summary>
    public TerminalSnapshot CreateSnapshot(long sequence)
    {
        SyncViewport();
        var rows = new TerminalRow[Rows];
        var top = ViewportTop;
        var screenTop = ScreenTop;
        for (var i = 0; i < rows.Length; i++)
        {
            var abs = top + i;
            rows[i] = abs >= screenTop ? _screen.Lines[abs - screenTop].Snapshot() : (GetRow(abs) ?? TerminalRow.Empty);
        }
        var cursorRow = (int)(screenTop + _screen.CursorY - top);
        var inView = cursorRow >= 0 && cursorRow < Rows;
        return new TerminalSnapshot
        {
            Columns = Columns,
            RowCount = Rows,
            Rows = rows,
            CursorX = inView ? Math.Min(_screen.CursorX, Columns - 1) : -1,
            CursorY = inView ? cursorRow : -1,
            CursorVisible = CursorVisible,
            CursorShape = CursorShape,
            CursorBlink = CursorBlink,
            ViewportTop = top,
            ScreenTop = screenTop,
            ScrollbackTop = ScrollbackTop,
            ReverseVideo = ReverseVideo,
            Modes = Modes,
            PaletteOverrides = _paletteOverrides is { Count: > 0 } p ? new Dictionary<int, uint>(p) : null,
            Sequence = sequence,
        };
    }

    // ------------------------------------------------------------------ printing

    void IVtHandler.PrintAscii(ReadOnlySpan<byte> text)
    {
        _afterZwj = false;
        // Charset translation, insert mode and wide neighbours need the general path; plain text on a plain line goes fast.
        if (_g[_gl] != Charset.Ascii || _singleShift >= 0 || InsertMode)
        {
            foreach (var b in text) Print(b);
            return;
        }
        var s = _screen;
        var i = 0;
        while (i < text.Length)
        {
            if (s.PendingWrap)
            {
                if (AutoWrap) WrapToNextLine();
                else s.PendingWrap = false;
            }
            var line = s.Current;
            var cells = line.Cells;
            var x = s.CursorX;
            var room = s.Columns - x;
            var n = Math.Min(room, text.Length - i);
            // A wide character whose spacer is overwritten loses its other half.
            if (cells[x].IsSpacer && x > 0) cells[x - 1] = Cell.Erased(_attr) with { Rune = ' ' };
            var attr = _attr;
            if (_currentLink is not null) attr = attr with { LinkId = line.InternLink(_currentLink) };
            for (var k = 0; k < n; k++) cells[x + k] = new Cell(text[i + k], attr);
            var end = x + n;
            if (end < s.Columns && cells[end].IsSpacer) cells[end] = Cell.Erased(_attr) with { Rune = ' ' };
            line.Touch();
            i += n;
            _lastPrinted = text[i - 1];
            if (end >= s.Columns)
            {
                s.CursorX = s.Columns - 1;
                s.PendingWrap = AutoWrap;
                if (!AutoWrap && i < text.Length)
                {
                    // Without auto-wrap the rest of the run overwrites the last column; only its final character stays.
                    cells[s.Columns - 1] = new Cell(text[^1], attr);
                    _lastPrinted = text[^1];
                    return;
                }
            }
            else s.CursorX = end;
        }
    }

    void IVtHandler.Print(int codePoint) => Print(codePoint);

    private void Print(int cp)
    {
        var set = _singleShift >= 0 ? _g[_singleShift] : _g[_gl];
        _singleShift = -1;
        if (set != Charset.Ascii && cp < 0x80) cp = Translate(set, cp);

        var width = CharWidth.Of(cp);
        var s = _screen;
        if (width == 0 || _afterZwj)
        {
            // A zero-width character, or the character after a zero-width joiner (an emoji ZWJ sequence), joins the previous cell.
            _afterZwj = cp == 0x200D;
            Combine(cp);
            return;
        }
        if (s.PendingWrap)
        {
            if (AutoWrap) WrapToNextLine();
            else s.PendingWrap = false;
        }
        if (width == 2 && s.CursorX >= s.Columns - 1)
        {
            if (s.Columns < 2) return;
            if (!AutoWrap) return; // nowhere to put it (xterm drops it)
            // Does not fit: the last cell stays empty and the character starts the next line.
            var line0 = s.Current;
            ClearCellForWrite(line0, s.CursorX);
            line0.Cells[s.CursorX] = Cell.Erased(_attr);
            line0.Touch();
            WrapToNextLine();
        }
        var line = s.Current;
        var x = s.CursorX;
        if (InsertMode) InsertBlanks(line, x, width);
        ClearCellForWrite(line, x);
        if (width == 2) ClearCellForWrite(line, x + 1);
        var attr = _attr;
        if (_currentLink is not null) attr = attr with { LinkId = line.InternLink(_currentLink) };
        if (width == 2)
        {
            line.Cells[x] = new Cell(cp, attr with { Flags = attr.Flags | CellFlags.Wide });
            line.Cells[x + 1] = new Cell(0, attr with { Flags = attr.Flags | CellFlags.WideSpacer });
        }
        else line.Cells[x] = new Cell(cp, attr);
        line.Touch();
        _lastPrinted = cp;
        var next = x + width;
        if (next >= s.Columns)
        {
            s.CursorX = s.Columns - 1;
            s.PendingWrap = AutoWrap;
        }
        else s.CursorX = next;
    }

    /// <summary>A zero-width character joins the previous cell's grapheme cluster (combining marks, ZWJ sequences, variation selectors).</summary>
    private void Combine(int cp)
    {
        var s = _screen;
        int x;
        if (s.PendingWrap) x = s.CursorX;
        else if (s.CursorX > 0) x = s.CursorX - 1;
        else return; // nothing to join
        var line = s.Current;
        if (line.Cells[x].IsSpacer && x > 0) x--;
        ref var cell = ref line.Cells[x];
        if (cell.Rune == 0) return;
        var text = line.ClusterText(cell.Rune);
        var add = char.ConvertFromUtf32(cp);
        if (text.Length + add.Length > Line.MaxClusterLength) return;
        cell.Rune = line.AddCluster(text + add);
        // VS16 (emoji presentation) does not widen a narrow base, as in xterm.js, tmux and libvterm: the cluster just carries the selector.
        line.Touch();
    }

    private static int Translate(Charset set, int cp)
    {
        switch (set)
        {
            case Charset.Uk: return cp == '#' ? 0xA3 : cp;
            case Charset.DecSpecialGraphics:
                if (cp is >= 0x5F and <= 0x7E) return DecGraphics[cp - 0x5F];
                return cp;
            default: return cp;
        }
    }

    // DEC Special Graphics 0x5F-0x7E (VT100 line drawing), as xterm maps them.
    private static readonly int[] DecGraphics =
    [
        0x00A0, 0x25C6, 0x2592, 0x2409, 0x240C, 0x240D, 0x240A, 0x00B0, 0x00B1, 0x2424, 0x240B, 0x2518, 0x2510, 0x250C, 0x2514, 0x253C,
        0x23BA, 0x23BB, 0x2500, 0x23BC, 0x23BD, 0x251C, 0x2524, 0x2534, 0x252C, 0x2502, 0x2264, 0x2265, 0x03C0, 0x2260, 0x00A3, 0x00B7,
    ];

    /// <summary>Overwriting half of a wide character erases the other half.</summary>
    private void ClearCellForWrite(Line line, int x)
    {
        if ((uint)x >= (uint)line.Cells.Length) return;
        var cell = line.Cells[x];
        if (cell.IsSpacer && x > 0 && line.Cells[x - 1].IsWide) line.Cells[x - 1] = Cell.Erased(_attr) with { Rune = ' ' };
        else if (cell.IsWide && x + 1 < line.Cells.Length && line.Cells[x + 1].IsSpacer) line.Cells[x + 1] = Cell.Erased(_attr) with { Rune = ' ' };
    }

    private void WrapToNextLine()
    {
        var s = _screen;
        s.PendingWrap = false;
        s.CursorX = 0;
        Index();
        s.Current.Wrapped = true;
        s.Current.Touch();
    }

    // ------------------------------------------------------------------ C0 / C1

    void IVtHandler.Execute(int c)
    {
        _afterZwj = false;
        var s = _screen;
        switch (c)
        {
            case 0x07: Bell?.Invoke(); break;
            case 0x08: // BS
                if (s.PendingWrap) { s.PendingWrap = false; s.CursorX = Math.Max(0, s.Columns - 2); }
                else if (s.CursorX > 0) s.CursorX--;
                break;
            case 0x09: Tab(1); break;
            case 0x0A or 0x0B or 0x0C:
                Index();
                if (Modes.LineFeedNewLine) s.CursorX = 0;
                break;
            case 0x0D: s.CursorX = 0; s.PendingWrap = false; break;
            case 0x0E: _gl = 1; break; // SO: G1
            case 0x0F: _gl = 0; break; // SI: G0
            case 0x84: Index(); break;  // IND
            case 0x85: s.CursorX = 0; Index(); break; // NEL
            case 0x88: s.TabStops[Math.Min(s.CursorX, s.Columns - 1)] = true; break; // HTS
            case 0x8D: ReverseIndex(); break; // RI
            case 0x8E: _singleShift = 2; break; // SS2
            case 0x8F: _singleShift = 3; break; // SS3
        }
    }

    /// <summary>IND / line feed: down one row, scrolling the region when at its bottom.</summary>
    private void Index()
    {
        var s = _screen;
        s.PendingWrap = false;
        if (s.CursorY == s.ScrollBottom) ScrollRegionUp(1);
        else if (s.CursorY < s.Rows - 1) s.CursorY++;
    }

    private void ReverseIndex()
    {
        var s = _screen;
        s.PendingWrap = false;
        if (s.CursorY == s.ScrollTop) s.ScrollDown(s.ScrollTop, s.ScrollBottom, 1, Cell.Erased(_attr));
        else if (s.CursorY > 0) s.CursorY--;
    }

    private void ScrollRegionUp(int count)
    {
        var s = _screen;
        s.ScrollUp(s.ScrollTop, s.ScrollBottom, count, Cell.Erased(_attr), toScrollback: true);
        if (!FollowOutput && !IsAlternateScreen && s.ScrollTop == 0 && ViewportTop < ScrollbackTop) ViewportTop = ScrollbackTop;
    }

    private void Tab(int count)
    {
        var s = _screen;
        s.PendingWrap = false;
        var x = s.CursorX;
        while (count-- > 0)
        {
            x++;
            while (x < s.Columns - 1 && !s.TabStops[x]) x++;
            if (x >= s.Columns - 1) { x = s.Columns - 1; break; }
        }
        s.CursorX = x;
    }

    private void BackTab(int count)
    {
        var s = _screen;
        s.PendingWrap = false;
        var x = s.CursorX;
        while (count-- > 0 && x > 0)
        {
            x--;
            while (x > 0 && !s.TabStops[x]) x--;
        }
        s.CursorX = x;
    }

    private void InsertBlanks(Line line, int x, int count)
    {
        var cells = line.Cells;
        count = Math.Min(count, cells.Length - x);
        if (count <= 0) return;
        // A wide character split by the shift loses its spacer at the right edge.
        Array.Copy(cells, x, cells, x + count, cells.Length - x - count);
        var fill = Cell.Erased(_attr);
        for (var i = 0; i < count; i++) cells[x + i] = fill;
        if (cells[^1].IsWide) cells[^1] = fill;
        if (x + count < cells.Length && cells[x + count].IsSpacer) cells[x + count] = fill;
        line.Touch();
    }
}
