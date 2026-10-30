using System.Text;

namespace CsCode.Terminal.Emulator;

public sealed partial class VtEmulator
{
    // ------------------------------------------------------------------ ESC

    void IVtHandler.EscDispatch(int intermediates, char final)
    {
        _afterZwj = false;
        var s = _screen;
        if (intermediates == 0)
        {
            switch (final)
            {
                case '7': SaveCursor(); break;
                case '8': RestoreCursor(); break;
                case 'D': Index(); break;
                case 'E': s.CursorX = 0; Index(); break;
                case 'H': s.TabStops[Math.Min(s.CursorX, s.Columns - 1)] = true; break;
                case 'M': ReverseIndex(); break;
                case 'N': _singleShift = 2; break;
                case 'O': _singleShift = 3; break;
                case 'n': _gl = 2; break;
                case 'o': _gl = 3; break;
                case 'c': FullReset(); break;
                case '=': SetModes(Modes with { ApplicationKeypad = true }); break;
                case '>': SetModes(Modes with { ApplicationKeypad = false }); break;
            }
            return;
        }
        var i0 = (char)(intermediates & 0xff);
        if ((intermediates >> 8) != 0) return;
        switch (i0)
        {
            case '(' or ')' or '*' or '+':
                _g[i0 - '('] = final switch { '0' => Charset.DecSpecialGraphics, 'A' => Charset.Uk, _ => Charset.Ascii };
                break;
            case '#' when final == '8': // DECALN: fill the screen with E
                for (var r = 0; r < s.Rows; r++)
                {
                    var line = s.Lines[r];
                    line.Clear(Cell.Blank);
                    line.Cells.AsSpan().Fill(new Cell('E', CellAttributes.Default));
                    line.Touch();
                }
                s.ScrollTop = 0;
                s.ScrollBottom = s.Rows - 1;
                s.CursorX = s.CursorY = 0;
                s.PendingWrap = false;
                break;
        }
    }

    // ------------------------------------------------------------------ CSI

    void IVtHandler.CsiDispatch(char prefix, VtParams p, int intermediates, char final)
    {
        _afterZwj = false;
        var s = _screen;
        var i0 = (char)(intermediates & 0xff);
        if (prefix == '?')
        {
            switch (final)
            {
                case 'h' when intermediates == 0: for (var i = 0; i < Math.Max(1, p.Count); i++) DecMode(p.Get(i), true); return;
                case 'l' when intermediates == 0: for (var i = 0; i < Math.Max(1, p.Count); i++) DecMode(p.Get(i), false); return;
                case 'J' when intermediates == 0: EraseInDisplay(p.Get(0)); return; // DECSED (no protected cells here)
                case 'K' when intermediates == 0: EraseInLine(p.Get(0)); return;    // DECSEL
                case 'n' when intermediates == 0:
                    if (p.Get(0) == 6) RespondText($"\e[?{CursorRowForReport()};{s.CursorX + 1}R");
                    return;
                case 'p' when i0 == '$': RespondText($"\e[?{p.Get(0)};{DecModeState(p.Get(0))}$y"); return; // DECRQM
            }
            return;
        }
        if (prefix == '>')
        {
            if (final == 'c' && intermediates == 0 && p.Get(0) == 0) RespondText("\e[>0;276;0c"); // DA2, as xterm.js answers
            else if (final == 'q' && intermediates == 0 && p.Get(0) == 0) RespondText("\eP>|CsCode\e\\"); // XTVERSION
            return;
        }
        if (prefix != '\0') return;

        if (intermediates != 0)
        {
            switch (i0, final)
            {
                case (' ', 'q'): // DECSCUSR
                    var style = p.Get(0);
                    (CursorShape, CursorBlink) = style switch
                    {
                        0 => (CursorShape.Default, (bool?)null),
                        1 => (CursorShape.Block, true),
                        2 => (CursorShape.Block, false),
                        3 => (CursorShape.Underline, true),
                        4 => (CursorShape.Underline, false),
                        5 => (CursorShape.Bar, true),
                        6 => (CursorShape.Bar, false),
                        _ => (CursorShape, CursorBlink),
                    };
                    break;
                case ('!', 'p'): SoftReset(); break; // DECSTR
                case ('$', 'p'): RespondText($"\e[{p.Get(0)};{AnsiModeState(p.Get(0))}$y"); break;
                case ('"', 'q'): break; // DECSCA: protection is not modelled
            }
            return;
        }

        switch (final)
        {
            case '@': InsertBlanks(s.Current, s.CursorX, p.GetNonZero(0)); s.PendingWrap = false; break; // ICH
            case 'A': CursorUp(p.GetNonZero(0)); break;
            case 'B' or 'e': CursorDown(p.GetNonZero(0)); break;
            case 'C' or 'a': CursorForward(p.GetNonZero(0)); break;
            case 'D': CursorBack(p.GetNonZero(0)); break;
            case 'E': CursorDown(p.GetNonZero(0)); s.CursorX = 0; break; // CNL
            case 'F': CursorUp(p.GetNonZero(0)); s.CursorX = 0; break;   // CPL
            case 'G' or '`': s.CursorX = Math.Clamp(p.GetNonZero(0) - 1, 0, s.Columns - 1); s.PendingWrap = false; break; // CHA, HPA
            case 'H' or 'f': CursorPosition(p.GetNonZero(0), p.GetNonZero(1)); break; // CUP, HVP
            case 'I': Tab(p.GetNonZero(0)); break; // CHT
            case 'J': EraseInDisplay(p.Get(0)); break;
            case 'K': EraseInLine(p.Get(0)); break;
            case 'L': InsertLines(p.GetNonZero(0)); break;
            case 'M': DeleteLines(p.GetNonZero(0)); break;
            case 'P': DeleteChars(p.GetNonZero(0)); break;
            case 'S': s.ScrollUp(s.ScrollTop, s.ScrollBottom, p.GetNonZero(0), Cell.Erased(_attr), toScrollback: false); break; // SU
            case 'T' when p.Count <= 1: s.ScrollDown(s.ScrollTop, s.ScrollBottom, p.GetNonZero(0), Cell.Erased(_attr)); break; // SD
            case 'X': EraseChars(p.GetNonZero(0)); break; // ECH
            case 'Z': BackTab(p.GetNonZero(0)); break;    // CBT
            case 'b': Repeat(p.GetNonZero(0)); break;     // REP
            case 'c': if (p.Get(0) == 0) RespondText("\e[?1;2c"); break; // DA1, as xterm.js answers
            case 'd': CursorPosition(p.GetNonZero(0), (s.CursorX + 1)); break; // VPA (column kept)
            case 'g': // TBC
                if (p.Get(0) == 0) s.TabStops[Math.Min(s.CursorX, s.Columns - 1)] = false;
                else if (p.Get(0) == 3) Array.Clear(s.TabStops);
                break;
            case 'h': for (var i = 0; i < Math.Max(1, p.Count); i++) AnsiMode(p.Get(i), true); break;
            case 'l': for (var i = 0; i < Math.Max(1, p.Count); i++) AnsiMode(p.Get(i), false); break;
            case 'm': Sgr(p); break;
            case 'n':
                if (p.Get(0) == 5) RespondText("\e[0n");
                else if (p.Get(0) == 6) RespondText($"\e[{CursorRowForReport()};{s.CursorX + 1}R");
                break;
            case 'r': SetScrollRegion(p.GetNonZero(0), p.GetNonZero(1, s.Rows)); break; // DECSTBM
            case 's': SaveCursor(); break;   // SCOSC (no DECSLRM here)
            case 'u': RestoreCursor(); break; // SCORC
            case 't': WindowOp(p); break;
        }
    }

    private int CursorRowForReport() => OriginMode ? _screen.CursorY - _screen.ScrollTop + 1 : _screen.CursorY + 1;

    private void CursorUp(int n)
    {
        var s = _screen;
        var top = s.CursorY >= s.ScrollTop ? s.ScrollTop : 0;
        s.CursorY = Math.Max(top, s.CursorY - n);
        s.PendingWrap = false;
    }

    private void CursorDown(int n)
    {
        var s = _screen;
        var bottom = s.CursorY <= s.ScrollBottom ? s.ScrollBottom : s.Rows - 1;
        s.CursorY = Math.Min(bottom, s.CursorY + n);
        s.PendingWrap = false;
    }

    private void CursorForward(int n)
    {
        var s = _screen;
        s.CursorX = Math.Min(s.Columns - 1, s.CursorX + n);
        s.PendingWrap = false;
    }

    private void CursorBack(int n)
    {
        var s = _screen;
        if (s.PendingWrap) { s.PendingWrap = false; s.CursorX = Math.Max(0, s.Columns - 1 - n); return; }
        s.CursorX = Math.Max(0, s.CursorX - n);
    }

    /// <summary>CUP: 1-based row and column, relative to the scroll region in origin mode.</summary>
    private void CursorPosition(int row, int column)
    {
        var s = _screen;
        if (OriginMode) s.CursorY = Math.Clamp(s.ScrollTop + row - 1, s.ScrollTop, s.ScrollBottom);
        else s.CursorY = Math.Clamp(row - 1, 0, s.Rows - 1);
        s.CursorX = Math.Clamp(column - 1, 0, s.Columns - 1);
        s.PendingWrap = false;
    }

    private void SetScrollRegion(int top, int bottom)
    {
        var s = _screen;
        top = Math.Max(1, top);
        bottom = Math.Min(s.Rows, bottom);
        if (top >= bottom) return; // invalid: ignored, as xterm does
        s.ScrollTop = top - 1;
        s.ScrollBottom = bottom - 1;
        CursorPosition(1, 1);
    }

    private void EraseInDisplay(int mode)
    {
        var s = _screen;
        var fill = Cell.Erased(_attr);
        s.PendingWrap = false;
        switch (mode)
        {
            case 0:
                EraseCells(s.Current, s.CursorX, s.Columns, fill);
                for (var r = s.CursorY + 1; r < s.Rows; r++) ClearLine(s.Lines[r], fill);
                break;
            case 1:
                for (var r = 0; r < s.CursorY; r++) ClearLine(s.Lines[r], fill);
                EraseCells(s.Current, 0, s.CursorX + 1, fill);
                break;
            case 2:
                for (var r = 0; r < s.Rows; r++) ClearLine(s.Lines[r], fill);
                break;
            case 3:
                if (!IsAlternateScreen)
                {
                    // Like xterm.js: the scrollback goes, the screen stays (absolute row numbers restart at the screen).
                    Scrollback.Replace([], Scrollback.TotalPushed, Columns);
                    ViewportTop = ScreenTop;
                    FollowOutput = true;
                }
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        var s = _screen;
        var fill = Cell.Erased(_attr);
        var line = s.Current;
        s.PendingWrap = false;
        switch (mode)
        {
            case 0: EraseCells(line, s.CursorX, s.Columns, fill); break;
            case 1: EraseCells(line, 0, s.CursorX + 1, fill); break;
            case 2: EraseCells(line, 0, s.Columns, fill); break;
        }
    }

    private static void ClearLine(Line line, in Cell fill) => line.Clear(fill);

    private void EraseCells(Line line, int from, int to, in Cell fill)
    {
        from = Math.Max(0, from);
        to = Math.Min(line.Cells.Length, to);
        if (from >= to) return;
        // half of a wide character at either edge is erased with it
        if (from > 0 && line.Cells[from].IsSpacer) line.Cells[from - 1] = fill;
        if (to < line.Cells.Length && line.Cells[to].IsSpacer) line.Cells[to] = fill;
        line.Cells.AsSpan(from, to - from).Fill(fill);
        if (to == line.Cells.Length && from == 0) line.Wrapped = false;
        line.Touch();
    }

    private void EraseChars(int n)
    {
        var s = _screen;
        s.PendingWrap = false;
        EraseCells(s.Current, s.CursorX, s.CursorX + n, Cell.Erased(_attr));
    }

    private void DeleteChars(int n)
    {
        var s = _screen;
        var line = s.Current;
        var cells = line.Cells;
        var x = s.CursorX;
        s.PendingWrap = false;
        n = Math.Min(n, cells.Length - x);
        if (n <= 0) return;
        var fill = Cell.Erased(_attr);
        if (cells[x].IsSpacer && x > 0) cells[x - 1] = fill;
        Array.Copy(cells, x + n, cells, x, cells.Length - x - n);
        for (var i = cells.Length - n; i < cells.Length; i++) cells[i] = fill;
        if (cells[x].IsSpacer) cells[x] = fill;
        line.Touch();
    }

    private void InsertLines(int n)
    {
        var s = _screen;
        if (s.CursorY < s.ScrollTop || s.CursorY > s.ScrollBottom) return;
        s.ScrollDown(s.CursorY, s.ScrollBottom, n, Cell.Erased(_attr));
        s.CursorX = 0;
        s.PendingWrap = false;
    }

    private void DeleteLines(int n)
    {
        var s = _screen;
        if (s.CursorY < s.ScrollTop || s.CursorY > s.ScrollBottom) return;
        s.ScrollUp(s.CursorY, s.ScrollBottom, n, Cell.Erased(_attr), toScrollback: false);
        s.CursorX = 0;
        s.PendingWrap = false;
    }

    private void Repeat(int n)
    {
        if (_lastPrinted < 0) return;
        n = Math.Min(n, _screen.Columns * _screen.Rows); // a huge count cannot do more than fill the screen
        var cp = _lastPrinted;
        for (var i = 0; i < n; i++) Print(cp);
    }

    private void WindowOp(VtParams p)
    {
        switch (p.Get(0))
        {
            case 18: RespondText($"\e[8;{Rows};{Columns}t"); break;
            case 22: if (_titleStack.Count < 10) _titleStack.Push(Title); break;
            case 23: if (_titleStack.Count > 0) SetTitle(_titleStack.Pop()); break;
        }
    }

    // ------------------------------------------------------------------ SGR

    private void Sgr(VtParams p)
    {
        if (p.Count == 0) { _attr = CellAttributes.Default with { LinkId = _attr.LinkId }; return; }
        var a = _attr;
        for (var i = 0; i < p.Count; i++)
        {
            var v = p.Get(i);
            switch (v)
            {
                case 0: a = CellAttributes.Default with { LinkId = a.LinkId }; break;
                case 1: a = a.With(CellFlags.Bold, true); break;
                case 2: a = a.With(CellFlags.Dim, true); break;
                case 3: a = a.With(CellFlags.Italic, true); break;
                case 4:
                    if (i + 1 < p.Count && p.IsSub(i + 1))
                    {
                        var style = p.Get(++i);
                        while (i + 1 < p.Count && p.IsSub(i + 1)) i++;
                        a = a.WithUnderline(style is >= 0 and <= 5 ? (UnderlineStyle)style : UnderlineStyle.Single);
                    }
                    else a = a.WithUnderline(UnderlineStyle.Single);
                    break;
                case 5 or 6: a = a.With(CellFlags.Blink, true); break;
                case 7: a = a.With(CellFlags.Inverse, true); break;
                case 8: a = a.With(CellFlags.Invisible, true); break;
                case 9: a = a.With(CellFlags.Strikethrough, true); break;
                case 21: a = a.WithUnderline(UnderlineStyle.Double); break;
                case 22: a = a.With(CellFlags.Bold, false).With(CellFlags.Dim, false); break;
                case 23: a = a.With(CellFlags.Italic, false); break;
                case 24: a = a.WithUnderline(UnderlineStyle.None); break;
                case 25: a = a.With(CellFlags.Blink, false); break;
                case 27: a = a.With(CellFlags.Inverse, false); break;
                case 28: a = a.With(CellFlags.Invisible, false); break;
                case 29: a = a.With(CellFlags.Strikethrough, false); break;
                case >= 30 and <= 37: a = a with { Foreground = TermColor.Indexed(v - 30) }; break;
                case 38: if (ExtendedColor(p, ref i) is { } fg) a = a with { Foreground = fg }; break;
                case 39: a = a with { Foreground = TermColor.Default }; break;
                case >= 40 and <= 47: a = a with { Background = TermColor.Indexed(v - 40) }; break;
                case 48: if (ExtendedColor(p, ref i) is { } bg) a = a with { Background = bg }; break;
                case 49: a = a with { Background = TermColor.Default }; break;
                case 53: a = a.With(CellFlags.Overline, true); break;
                case 55: a = a.With(CellFlags.Overline, false); break;
                case 58: if (ExtendedColor(p, ref i) is { } ul) a = a with { UnderlineColor = ul }; break;
                case 59: a = a with { UnderlineColor = TermColor.Default }; break;
                case >= 90 and <= 97: a = a with { Foreground = TermColor.Indexed(v - 90 + 8) }; break;
                case >= 100 and <= 107: a = a with { Background = TermColor.Indexed(v - 100 + 8) }; break;
                default:
                    while (i + 1 < p.Count && p.IsSub(i + 1)) i++; // unknown attribute: skip its sub-parameters
                    break;
            }
        }
        _attr = a;
    }

    /// <summary>
    /// 38/48/58 colour forms: <c>;5;n</c>, <c>;2;r;g;b</c>, and the ':' forms <c>:5:n</c>, <c>:2:r:g:b</c>, <c>:2::r:g:b</c> (with a colour
    /// space id). <paramref name="i"/> is left on the last parameter consumed.
    /// </summary>
    private static uint? ExtendedColor(VtParams p, ref int i)
    {
        if (i + 1 < p.Count && p.IsSub(i + 1))
        {
            // colon form: count the sub-parameters
            var start = i + 1;
            var end = start;
            while (end + 1 < p.Count && p.IsSub(end + 1)) end++;
            i = end;
            var kind = p.Get(start);
            var n = end - start + 1;
            if (kind == 5 && n >= 2) return TermColor.Indexed(Math.Clamp(p.Get(start + 1), 0, 255));
            if (kind == 2 && n >= 4)
            {
                var o = n >= 5 ? start + 2 : start + 1; // skip the colour space id when present
                return TermColor.Rgb(Math.Clamp(p.Get(o), 0, 255), Math.Clamp(p.Get(o + 1), 0, 255), Math.Clamp(p.Get(o + 2), 0, 255));
            }
            return null;
        }
        if (i + 1 >= p.Count) return null;
        var mode = p.Get(i + 1);
        if (mode == 5)
        {
            if (i + 2 >= p.Count) { i = p.Count - 1; return null; }
            i += 2;
            return TermColor.Indexed(Math.Clamp(p.Get(i), 0, 255));
        }
        if (mode == 2)
        {
            if (i + 4 >= p.Count) { i = p.Count - 1; return null; }
            var r = p.Get(i + 2); var g = p.Get(i + 3); var b = p.Get(i + 4);
            i += 4;
            return TermColor.Rgb(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
        }
        i++;
        return null;
    }

    // ------------------------------------------------------------------ modes

    private void AnsiMode(int mode, bool on)
    {
        switch (mode)
        {
            case 4: InsertMode = on; break;
            case 20: SetModes(Modes with { LineFeedNewLine = on }); break;
        }
    }

    private int AnsiModeState(int mode) => mode switch
    {
        4 => InsertMode ? 1 : 2,
        20 => Modes.LineFeedNewLine ? 1 : 2,
        _ => 0,
    };

    private void DecMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: SetModes(Modes with { ApplicationCursorKeys = on }); break;
            case 5: if (ReverseVideo != on) { ReverseVideo = on; } break;
            case 6: OriginMode = on; CursorPosition(1, 1); break;
            case 7: AutoWrap = on; if (!on) _screen.PendingWrap = false; break;
            case 9: SetModes(Modes with { Mouse = on ? MouseTracking.X10 : MouseTracking.None }); break;
            case 12: CursorBlink = on; break;
            case 25: CursorVisible = on; break;
            case 66: SetModes(Modes with { ApplicationKeypad = on }); break;
            case 1000: SetModes(Modes with { Mouse = on ? MouseTracking.Normal : MouseTracking.None }); break;
            case 1002: SetModes(Modes with { Mouse = on ? MouseTracking.ButtonEvent : MouseTracking.None }); break;
            case 1003: SetModes(Modes with { Mouse = on ? MouseTracking.AnyEvent : MouseTracking.None }); break;
            case 1004: SetModes(Modes with { FocusEvents = on }); break;
            case 1005: SetModes(Modes with { MouseEncoding = on ? MouseEncoding.Utf8 : MouseEncoding.Default }); break;
            case 1006: SetModes(Modes with { MouseEncoding = on ? MouseEncoding.Sgr : MouseEncoding.Default }); break;
            case 1015: SetModes(Modes with { MouseEncoding = on ? MouseEncoding.Urxvt : MouseEncoding.Default }); break;
            case 1007: SetModes(Modes with { AlternateScroll = on }); break;
            case 47 or 1047: if (on) EnterAlternate(saveCursor: false); else LeaveAlternate(restoreCursor: false); break;
            case 1048: if (on) SaveCursor(); else RestoreCursor(); break;
            case 1049: if (on) EnterAlternate(saveCursor: true); else LeaveAlternate(restoreCursor: true); break;
            case 2004: SetModes(Modes with { BracketedPaste = on }); break;
            case 2026: SynchronizedOutput = on; break;
        }
    }

    private int DecModeState(int mode) => mode switch
    {
        1 => Modes.ApplicationCursorKeys ? 1 : 2,
        5 => ReverseVideo ? 1 : 2,
        6 => OriginMode ? 1 : 2,
        7 => AutoWrap ? 1 : 2,
        12 => CursorBlink == true ? 1 : 2,
        25 => CursorVisible ? 1 : 2,
        66 => Modes.ApplicationKeypad ? 1 : 2,
        1000 => Modes.Mouse == MouseTracking.Normal ? 1 : 2,
        1002 => Modes.Mouse == MouseTracking.ButtonEvent ? 1 : 2,
        1003 => Modes.Mouse == MouseTracking.AnyEvent ? 1 : 2,
        1004 => Modes.FocusEvents ? 1 : 2,
        1006 => Modes.MouseEncoding == MouseEncoding.Sgr ? 1 : 2,
        47 or 1047 or 1049 => IsAlternateScreen ? 1 : 2,
        2004 => Modes.BracketedPaste ? 1 : 2,
        2026 => SynchronizedOutput ? 1 : 2,
        _ => 0,
    };

    private void SetModes(TerminalModes modes)
    {
        if (modes == Modes) return;
        Modes = modes;
        ModesVersion++;
    }

    private void EnterAlternate(bool saveCursor)
    {
        if (saveCursor) SaveCursor();
        if (IsAlternateScreen) return;
        var p = _primary;
        var a = _alternate;
        // As xterm.js: the alternate screen starts blank, with the cursor where it was.
        var fill = Cell.Erased(_attr);
        for (var r = 0; r < a.Rows; r++) a.Lines[r].Clear(fill);
        a.CursorX = p.CursorX;
        a.CursorY = p.CursorY;
        a.PendingWrap = false;
        a.ScrollTop = 0;
        a.ScrollBottom = a.Rows - 1;
        a.TabStops = (bool[])p.TabStops.Clone();
        _screen = a;
        SetModes(Modes with { AlternateScreen = true });
        ViewportTop = 0;
    }

    private void LeaveAlternate(bool restoreCursor)
    {
        if (IsAlternateScreen)
        {
            _screen = _primary;
            SetModes(Modes with { AlternateScreen = false });
        }
        if (restoreCursor) RestoreCursor();
        FollowOutput = true;
        ViewportTop = ScreenTop;
    }

    private void SaveCursor()
    {
        var s = _screen;
        s.Saved = new SavedCursor(s.CursorX, s.CursorY, s.PendingWrap, _attr, _g[0], _g[1], _g[2], _g[3], _gl, OriginMode);
    }

    private void RestoreCursor()
    {
        var s = _screen;
        if (s.Saved is not { } c)
        {
            s.CursorX = s.CursorY = 0;
            s.PendingWrap = false;
            _attr = CellAttributes.Default;
            OriginMode = false;
            return;
        }
        s.CursorX = Math.Min(c.X, s.Columns - 1);
        s.CursorY = Math.Min(c.Y, s.Rows - 1);
        s.PendingWrap = c.PendingWrap && c.X < s.Columns;
        _attr = c.Attributes with { LinkId = _attr.LinkId };
        (_g[0], _g[1], _g[2], _g[3], _gl) = (c.G0, c.G1, c.G2, c.G3, c.Gl);
        OriginMode = c.OriginMode;
    }

    /// <summary>DECSTR: modes back to their defaults; the screen and the cursor position stay.</summary>
    private void SoftReset()
    {
        CursorVisible = true;
        InsertMode = false;
        OriginMode = false;
        AutoWrap = true;
        SetModes(Modes with { ApplicationCursorKeys = false, ApplicationKeypad = false });
        _screen.ScrollTop = 0;
        _screen.ScrollBottom = _screen.Rows - 1;
        _attr = CellAttributes.Default;
        _currentLink = null;
        _g[0] = _g[1] = _g[2] = _g[3] = Charset.Ascii;
        _gl = 0;
        _screen.Saved = null;
        _screen.PendingWrap = false;
    }

    /// <summary>RIS: everything back to the initial state (screens, scrollback, modes, palette); the title stays, as in xterm.js.</summary>
    private void FullReset()
    {
        var cols = Columns;
        var rows = Rows;
        _primary = new ScreenBuffer(cols, rows, new Scrollback(Options.ScrollbackLines, Options.ScrollbackMaxBytes));
        _alternate = new ScreenBuffer(cols, rows, null);
        _screen = _primary;
        _attr = CellAttributes.Default;
        _currentLink = null;
        _g[0] = _g[1] = _g[2] = _g[3] = Charset.Ascii;
        _gl = 0;
        _singleShift = -1;
        _lastPrinted = -1;
        InsertMode = false;
        AutoWrap = true;
        OriginMode = false;
        CursorVisible = true;
        CursorBlink = null;
        CursorShape = CursorShape.Default;
        ReverseVideo = false;
        SynchronizedOutput = false;
        _paletteOverrides = null;
        SetModes(TerminalModes.Initial);
        FollowOutput = true;
        ViewportTop = 0;
    }

    // ------------------------------------------------------------------ OSC

    void IVtHandler.OscDispatch(ReadOnlySpan<byte> payload)
    {
        var semi = payload.IndexOf((byte)';');
        var cmdSpan = semi < 0 ? payload : payload[..semi];
        if (!int.TryParse(Encoding.ASCII.GetString(cmdSpan), System.Globalization.NumberStyles.None, null, out var command)) return;
        var data = semi < 0 ? "" : Encoding.UTF8.GetString(payload[(semi + 1)..]);
        switch (command)
        {
            case 0 or 2: SetTitle(data); break;
            case 1: break; // icon name
            case 4: OscPalette(data); break;
            case 7: SetCurrentDirectory(data); break;
            case 8: OscHyperlink(data); break;
            case 10 or 11 or 12: OscDynamicColor(command, data); break;
            case 52: OscClipboard(data); break;
            case 104:
                if (data.Length == 0) _paletteOverrides = null;
                else foreach (var part in data.Split(';')) if (int.TryParse(part, out var idx)) _paletteOverrides?.Remove(idx);
                break;
            case 133 or 633: OscShellIntegration(command, data); break;
        }
    }

    private const int MaxTitleLength = 1024;

    private void SetTitle(string title)
    {
        if (title.Length > MaxTitleLength) title = title[..MaxTitleLength];
        // control characters never reach a tab title
        if (title.AsSpan().IndexOfAnyInRange('\0', '\x1f') >= 0) title = new string(title.Where(c => c >= ' ').ToArray());
        if (title == Title) return;
        Title = title;
        TitleChanged?.Invoke(title);
    }

    private void SetCurrentDirectory(string uri)
    {
        // file://host/path (percent-encoded); a bare path is accepted too
        string? path = null;
        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = uri[7..];
            var slash = rest.IndexOf('/');
            if (slash >= 0) path = Uri.UnescapeDataString(rest[slash..]);
        }
        else if (uri.StartsWith('/')) path = uri;
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || path == CurrentDirectory) return;
        CurrentDirectory = path;
        CurrentDirectoryChanged?.Invoke(path);
    }

    private void OscHyperlink(string data)
    {
        // "params;uri": an empty uri closes the link
        var semi = data.IndexOf(';');
        if (semi < 0) return;
        var uri = data[(semi + 1)..];
        if (uri.Length == 0 || uri.Length > 2048) { _currentLink = null; _attr = _attr with { LinkId = 0 }; return; }
        _currentLink = string.IsInterned(uri) ?? uri;
        _attr = _attr with { LinkId = 1 }; // the row-local id is assigned when a cell is written
    }

    private void OscPalette(string data)
    {
        var parts = data.Split(';');
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            if (!int.TryParse(parts[i], out var index) || index is < 0 or > 255) continue;
            var spec = parts[i + 1];
            if (spec == "?")
            {
                var rgb = _paletteOverrides is not null && _paletteOverrides.TryGetValue(index, out var o)
                    ? (int)(o & 0xffffff)
                    : DefaultTerminalPalette.Color256(Options.Palette, index);
                RespondText($"\e]4;{index};{XParseColor(rgb)}\e\\");
            }
            else if (ParseColorSpec(spec) is { } color)
            {
                (_paletteOverrides ??= [])[index] = (uint)color;
            }
        }
    }

    private void OscDynamicColor(int command, string data)
    {
        foreach (var spec in data.Split(';'))
        {
            if (spec != "?") continue; // setting the default colours is not supported (the theme owns them)
            var pal = Options.Palette;
            var rgb = command switch { 10 => pal.Foreground, 11 => pal.Background, _ => pal.Cursor };
            RespondText($"\e]{command};{XParseColor(rgb)}\e\\");
            command++;
        }
    }

    private void OscClipboard(string data)
    {
        if (!Options.AllowOsc52 || ClipboardWrite is null) return;
        var semi = data.IndexOf(';');
        if (semi < 0) return;
        var payload = data[(semi + 1)..];
        if (payload == "?") return; // reads are never answered (a program must not see the user's clipboard)
        try
        {
            var bytes = Convert.FromBase64String(payload);
            ClipboardWrite(Encoding.UTF8.GetString(bytes));
        }
        catch (FormatException) { }
    }

    private void OscShellIntegration(int command, string data)
    {
        var line = _screen.Current;
        var kind = data.Length > 0 ? data[0] : '\0';
        if (kind is 'A' or 'B' or 'C' or 'D' or 'E' or 'P') HasShellIntegration = true;
        switch (kind)
        {
            case 'A':
                line.Mark = RowMark.PromptStart;
                line.Touch();
                OnPromptStart();
                break;
            // the command line usually shares the prompt's row: a prompt mark is never downgraded
            case 'B':
                if (line.Mark == RowMark.None) { line.Mark = RowMark.CommandStart; line.Touch(); }
                OnCommandStart();
                break;
            case 'C':
                if (line.Mark == RowMark.None) { line.Mark = RowMark.CommandExecuted; line.Touch(); }
                OnCommandExecuted();
                break;
            case 'D':
            {
                // The exit code belongs to the command whose prompt is above (VS Code decorates that prompt): mark the nearest prompt row.
                int? code = data.Length > 2 && int.TryParse(data.AsSpan(2).TrimEnd(';'), out var c) ? c : null;
                if (LastPromptLine() is { } prompt)
                {
                    prompt.MarkExitCode = code ?? int.MinValue; // MinValue: finished, code unknown
                    prompt.Touch();
                }
                OnCommandFinished(code);
                CommandFinished?.Invoke(code);
                break;
            }
            case 'E' when command == 633:
                OnCommandLine(data);
                break;
            case 'P' when command == 633 && data.StartsWith("P;Cwd=", StringComparison.Ordinal):
                SetCurrentDirectory(UnescapeShellIntegrationValue(data[6..]));
                break;
        }
    }

    /// <summary>
    /// The line a finished command's exit code belongs to: the nearest prompt at or above the cursor (screen, then scrollback, within 2,000
    /// lines) when it has none yet; without any prompt mark (a shell that reports only B/C), the nearest unfinished command mark.
    /// </summary>
    private Line? LastPromptLine()
    {
        Line? fallback = null;
        Line? Check(Line l, out bool stop)
        {
            stop = false;
            if (l.Mark == RowMark.PromptStart) { stop = true; return l.MarkExitCode is null ? l : null; }
            if (l.Mark is RowMark.CommandStart or RowMark.CommandExecuted && l.MarkExitCode is null) fallback ??= l;
            return null;
        }
        var s = _screen;
        for (var r = s.CursorY; r >= 0; r--)
        {
            var hit = Check(s.Lines[r], out var stop);
            if (stop) return hit;
        }
        if (!IsAlternateScreen)
            for (int i = Scrollback.Count - 1, n = 0; i >= 0 && n < 2000; i--, n++)
            {
                var hit = Check(Scrollback.LineAt(i), out var stop);
                if (stop) return hit;
            }
        return fallback;
    }

    /// <summary>
    /// Shell integration (OSC 633/133): the absolute row of the nearest prompt before (<paramref name="direction"/> -1) or after (+1)
    /// <paramref name="from"/>, or null. Reads the marks on the lines, so it allocates nothing however long the scrollback.
    /// </summary>
    public long? FindPrompt(long from, int direction)
    {
        if (IsAlternateScreen) return null;
        var first = ScrollbackTop;
        var last = ScreenTop + Rows - 1;
        for (var a = from + direction; a >= first && a <= last; a += direction)
        {
            var mark = a >= ScreenTop ? _screen.Lines[a - ScreenTop].Mark : Scrollback.LineAt((int)(a - first)).Mark;
            if (mark == RowMark.PromptStart) return a;
        }
        return null;
    }

    // the prompt the last Ctrl+Up/Down went to, while the view is still where that left it (a prompt on the last screen cannot reach the top)
    private long? _commandNavRow;
    private long _commandNavViewport;

    /// <summary>Ctrl+Up/Down: brings the previous/next prompt to the top of the view (past the last one: the bottom).</summary>
    public void ScrollToCommand(int direction)
    {
        var from = _commandNavRow is { } row && ViewportTop == _commandNavViewport ? row
            : FollowOutput ? ScreenTop + _screen.CursorY : ViewportTop;
        if (FindPrompt(from, direction) is { } target)
        {
            ScrollViewportTo(target);
            (_commandNavRow, _commandNavViewport) = (target, ViewportTop);
        }
        else if (direction > 0)
        {
            ScrollViewportToBottom();
            _commandNavRow = null;
        }
    }

    internal static string XParseColor(int rgb)
    {
        var r = (rgb >> 16) & 0xff;
        var g = (rgb >> 8) & 0xff;
        var b = rgb & 0xff;
        return $"rgb:{r:x2}{r:x2}/{g:x2}{g:x2}/{b:x2}{b:x2}";
    }

    /// <summary>"#rrggbb", "#rgb", "rgb:r/g/b" (1-4 hex digits each) as 0xRRGGBB.</summary>
    internal static int? ParseColorSpec(string spec)
    {
        if (spec.StartsWith('#'))
        {
            var hex = spec[1..];
            if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return v;
            if (hex.Length == 3 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var s))
                return ((s >> 8 & 0xf) * 0x11 << 16) | ((s >> 4 & 0xf) * 0x11 << 8) | (s & 0xf) * 0x11;
            return null;
        }
        if (!spec.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = spec[4..].Split('/');
        if (parts.Length != 3) return null;
        var result = 0;
        foreach (var part in parts)
        {
            if (part.Length is < 1 or > 4 || !int.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out var c)) return null;
            var max = (1 << (4 * part.Length)) - 1;
            result = (result << 8) | (c * 255 / max);
        }
        return result;
    }

    private void RespondText(string text) => Respond?.Invoke(Encoding.UTF8.GetBytes(text));

    void IVtHandler.DcsDispatch(char prefix, VtParams parameters, int intermediates, char final, ReadOnlySpan<byte> data)
    {
        // Device control strings (DECRQSS, sixel, XTGETTCAP...) are consumed and ignored.
    }
}
