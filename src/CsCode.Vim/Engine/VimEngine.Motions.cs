using CsCode.Core.Text;

namespace CsCode.Vim;

internal enum MType { Char, Line, Block }

/// <summary>Operator arguments (Vim's oparg_T): what is being operated on, filled in by the motion and by do_pending_operator.</summary>
internal sealed class OpArgs
{
    public OpType Op;
    public int Reg;
    public TextPosition Start, End;
    public MType Type;
    public bool Inclusive;
    public bool UseRegOne;
    public bool IsVisual, BlockMode, EndAdjusted, NoAdjEnd, Empty, IsDollar;
    public int LineCount, StartVcol, EndVcol, Force;

    public void ResetForMotion(OpType op, int reg)
    {
        Op = op;
        Reg = reg;
        Type = MType.Char;
        Inclusive = false;
        UseRegOne = false;
        IsVisual = BlockMode = EndAdjusted = NoAdjEnd = Empty = IsDollar = false;
        Force = 0;
    }
}

public sealed partial class VimEngine
{
    private readonly OpArgs _oap = new();

    private int EffCount(Cmd c, Cmd? m) => m is null ? c.Count0 : c.Count0 > 0 && m.Count0 > 0 ? c.Count0 * m.Count0 : c.Count0 + m.Count0;

    // ------------------------------------------------------------------------------------------------------------
    // character stepping (Vim's inc/dec/incl/decl) over the cursor
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>0 moved within the line, 1 moved to the next line, 2 moved onto the end-of-line position, -1 at end of buffer.</summary>
    private int Inc(ref int line, ref int col)
    {
        var s = L(line);
        if (col < s.Length)
        {
            col += VimText.CharLen(s, col);
            return col < s.Length ? 0 : 2;
        }
        if (line < LastLine) { line++; col = 0; return 1; }
        return -1;
    }

    /// <summary>0 moved within the line, 1 moved to the previous line (onto its end-of-line position), -1 at start of buffer.</summary>
    private int Dec(ref int line, ref int col)
    {
        if (col > 0)
        {
            col = VimText.PrevCharStart(L(line), col);
            return 0;
        }
        if (line > 0) { line--; col = L(line).Length; return 1; }
        return -1;
    }

    private int IncCursor() => Inc(ref _line, ref _col);
    private int DecCursor() => Dec(ref _line, ref _col);

    private int Incl(ref int line, ref int col)
    {
        var r = Inc(ref line, ref col);
        if (r >= 1 && col > 0) r = Inc(ref line, ref col);
        return r;
    }

    private int Decl(ref int line, ref int col)
    {
        var r = Dec(ref line, ref col);
        if (r == 1 && col > 0) r = Dec(ref line, ref col);
        return r;
    }

    private int CharAt(int line, int col)
    {
        var s = L(line);
        return col < s.Length ? VimText.CodePointAt(s, col) : 0;
    }

    private int CharAtCursor() => CharAt(_line, _col);

    private bool _clsBigword;
    private int Cls() => VimText.ClassAt(CurLine, _col, _clsBigword, O.Keywords);

    private bool SkipChars(int cls, bool forward)
    {
        while (Cls() == cls)
            if ((forward ? IncCursor() : DecCursor()) == -1) return true;
        return false;
    }

    private bool LineEmpty(int line) => L(line).Length == 0;

    // ------------------------------------------------------------------------------------------------------------
    // word motions
    // ------------------------------------------------------------------------------------------------------------

    private bool FwdWord(int count, bool bigword, bool eol)
    {
        _clsBigword = bigword;
        while (--count >= 0)
        {
            var sclass = Cls();
            var lastLine = _line == LastLine;
            var i = IncCursor();
            if (i == -1 || (i >= 1 && lastLine)) return false;
            if (i >= 1 && eol && count == 0) return true;
            if (sclass != 0)
                while (Cls() == sclass)
                {
                    i = IncCursor();
                    if (i == -1 || (i >= 1 && eol && count == 0)) return true;
                }
            while (Cls() == 0)
            {
                if (_col == 0 && LineEmpty(_line)) break;
                i = IncCursor();
                if (i == -1 || (i >= 1 && lastLine)) return true;
            }
        }
        return true;
    }

    private bool BckWord(int count, bool bigword, bool stop)
    {
        _clsBigword = bigword;
        while (--count >= 0)
        {
            var sclass = Cls();
            if (DecCursor() == -1) return false;
            var finished = false;
            if (!stop || sclass == Cls() || sclass == 0)
            {
                while (Cls() == 0)
                {
                    if (_col == 0 && LineEmpty(_line)) { finished = true; break; }
                    if (DecCursor() == -1) return true;
                }
                if (!finished && SkipChars(Cls(), false)) return true;
            }
            if (!finished) IncCursor();
            stop = false;
        }
        return true;
    }

    private bool EndWord(int count, bool bigword, bool stop, bool empty)
    {
        _clsBigword = bigword;
        while (--count >= 0)
        {
            var sclass = Cls();
            if (IncCursor() == -1) return false;
            var finished = false;
            if (Cls() == sclass && sclass != 0)
            {
                if (SkipChars(sclass, true)) return false;
            }
            else if (!stop || sclass == 0)
            {
                while (Cls() == 0)
                {
                    if (empty && _col == 0 && LineEmpty(_line)) { finished = true; break; }
                    if (IncCursor() == -1) return false;
                }
                if (!finished && SkipChars(Cls(), true)) return false;
            }
            if (!finished) DecCursor();
            stop = false;
        }
        return true;
    }

    private bool BckendWord(int count, bool bigword, bool eol)
    {
        _clsBigword = bigword;
        while (--count >= 0)
        {
            var sclass = Cls();
            int i;
            if ((i = DecCursor()) == -1) return false;
            if (eol && i == 1) return true;
            if (sclass != 0)
                while (Cls() == sclass)
                    if ((i = DecCursor()) == -1 || (eol && i == 1)) return true;
            while (Cls() == 0)
            {
                if (_col == 0 && LineEmpty(_line)) break;
                if ((i = DecCursor()) == -1 || (eol && i == 1)) return true;
            }
        }
        return true;
    }

    /// <summary>Vim's adjust_cursor: don't leave the cursor on the end-of-line position after a motion.</summary>
    private void AdjustCursorOp(OpArgs oap)
    {
        if (_col > 0 && CharAtCursor() == 0 && (!VisualActive || O.SelectionExclusive) && !O.VirtualOneMore)
        {
            _col = VimText.PrevCharStart(CurLine, _col);
            oap.Inclusive = true;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // the motion dispatcher
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Executes a motion (cursor movement) for key(s) in <paramref name="m"/>. Returns false on failure (the caller beeps).
    /// <paramref name="count0"/> is the effective count (0 = none). The motion sets oap.Type / Inclusive.
    /// </summary>
    private bool RunMotion(Cmd m, int count0, OpArgs oap)
    {
        var count1 = count0 > 0 ? count0 : 1;
        var op = oap.Op;
        oap.Type = MType.Char;
        oap.Inclusive = false;
        var keepCurswant = false;
        var ok = true;
        var startPos = new TextPosition(_line, _col);
        switch (m.Key)
        {
            case 'h' or Keys.Left or Keys.BS or Keys.CtrlH:
                ok = MoveLeft(m.Key, count1, oap);
                break;
            case 'l' or Keys.Right or ' ':
                ok = MoveRight(m.Key, count1, oap);
                break;
            case 'j' or 'k' or Keys.Down or Keys.Up or Keys.NL or 14 or 16:
            {
                var down = m.Key is 'j' or Keys.Down or Keys.NL or 14;
                oap.Type = MType.Line;
                keepCurswant = true;
                UpdateCurswant();
                ok = CursorVertical(down ? count1 : -count1, op == OpType.None);
                break;
            }
            case Keys.CR or '+' or '-' or '_':
            {
                oap.Type = MType.Line;
                if (m.Key == '_') ok = CursorVertical(count1 - 1, op == OpType.None);
                else ok = CursorVertical(m.Key == '-' ? -count1 : count1, op == OpType.None);
                if (ok) Beginline(BL.White | BL.Fix);
                break;
            }
            case '0' or Keys.Home when m.Key == '0' || true:
                Beginline(0);
                _setCurswant = true;
                break;
            case '^':
                Beginline(BL.White | BL.Fix);
                break;
            case '$' or Keys.End:
            {
                oap.Inclusive = true;
                _curswant = int.MaxValue;
                _setCurswant = false;
                ok = CursorVertical(count1 - 1, op == OpType.None);
                keepCurswant = true;
                oap.IsDollar = true;
                break;
            }
            case '|':
                Beginline(0);
                if (count0 > 0) { Coladvance(count0 - 1); _curswant = count0 - 1; }
                else _curswant = 0;
                _setCurswant = false;
                keepCurswant = true;
                break;
            case 'w' or 'W':
            {
                var big = m.Key == 'W';
                var wordEnd = false;
                var flag = false;
                if (op == OpType.Change)
                {
                    var n = CharAtCursor();
                    if (n != 0)
                    {
                        if (n is ' ' or '\t')
                        {
                            if (count1 == 1 && O.CpoHas('w')) { oap.Inclusive = true; break; }
                        }
                        else { oap.Inclusive = true; wordEnd = true; flag = true; }
                    }
                }
                _setCurswant = true;
                var before = new TextPosition(_line, _col);
                var r = wordEnd ? EndWord(count1, big, flag, false) : FwdWord(count1, big, op != OpType.None);
                if (before < new TextPosition(_line, _col)) AdjustCursorOp(oap);
                if (!r && op == OpType.None) ok = false;
                break;
            }
            case 'e' or 'E':
            {
                oap.Inclusive = true;
                _setCurswant = true;
                var beforeE = new TextPosition(_line, _col);
                var rE = EndWord(count1, m.Key == 'E', false, false);
                if (beforeE < new TextPosition(_line, _col)) AdjustCursorOp(oap);
                if (!rE && op == OpType.None) ok = false;
                break;
            }
            case 'b' or 'B':
                _setCurswant = true;
                ok = BckWord(count1, m.Key == 'B', false);
                break;
            case 'f' or 'F' or 't' or 'T':
                ok = FindCharMotion(m.Key, m.NChar, count1, oap, repeat: false);
                break;
            case ';' or ',':
                ok = RepeatFindChar(m.Key == ',', count1, oap);
                break;
            case '%':
                oap.UseRegOne = true;
                ok = PercentMotion(count0, oap);
                break;
            case '(' or ')':
                oap.UseRegOne = true;
                oap.Type = MType.Char;
                oap.Inclusive = false;
                _setCurswant = true;
                ok = FindSentence(m.Key == ')', count1);
                break;
            case '{' or '}':
                oap.UseRegOne = true;
                _setCurswant = true;
                ok = FindParagraph(m.Key == '}', count1, 0, both: false, oap);
                break;
            case 'H' or 'M' or 'L':
                oap.Type = MType.Line;
                ok = ScreenLineMotion(m.Key, count1, op == OpType.None);
                break;
            case 'G':
                oap.Type = MType.Line;
                SetPcmark();
                GotoLine(count0 > 0 ? count0 - 1 : LastLine);
                break;
            case '\'' or '`':
                ok = MarkMotion(m.Key, m.NChar, oap);
                break;
            case 'n' or 'N' or '*' or '#' or '/' or '?':
                ok = SearchMotion(m, count1, oap);
                break;
            case 'g':
                ok = GMotion(m, count0, count1, oap, ref keepCurswant);
                break;
            case '[' or ']':
                ok = BracketMotion(m, count1, oap);
                break;
            default:
                ok = false;
                break;
        }
        if (!keepCurswant && ok && m.Key is not ('0' or '^' or 'j' or 'k' or '$')) _setCurswant = _setCurswant || m.Key is not ('G' or 'H' or 'L' or 'M' or '+' or '-' or '_' or Keys.CR);
        if (ok && m.Key is 'G' or 'H' or 'M' or 'L' or '+' or '-' or '_' or Keys.CR) _setCurswant = true;
        _ = startPos;
        return ok;
    }

    private void GotoLine(int line)
    {
        _line = Math.Clamp(line, 0, LastLine);
        Beginline(BL.Sol | BL.Fix);
    }

    // ---- h / l ----

    private bool MoveLeft(int key, int count1, OpArgs oap)
    {
        var op = oap.Op;
        for (var n = count1; n > 0; n--)
        {
            if (_col > 0)
            {
                _col = VimText.PrevCharStart(CurLine, _col);
                continue;
            }
            var wrap = (key is Keys.BS or Keys.CtrlH && O.WhichWrapHas('b')) || (key == 'h' && O.WhichWrapHas('h')) || (key == Keys.Left && O.WhichWrapHas('<'));
            if (wrap && _line > 0)
            {
                _line--;
                _col = LineEmpty(_line) ? 0 : (_mode is VimMode.Insert or VimMode.Replace || VisualActive ? L(_line).Length : VimText.PrevCharStart(CurLine, CurLine.Length));
                _setCurswant = true;
                if (op is OpType.Delete or OpType.Change && !LineEmpty(_line))
                {
                    // when the newline before the first char is to be deleted, sit on the NUL after the previous line
                    _col += VimText.CharLen(CurLine, _col);
                    oap.NoAdjEnd = true;
                }
                continue;
            }
            if (op == OpType.None && n == count1) return false;
            break;
        }
        _setCurswant = true;
        return true;
    }

    private bool MoveRight(int key, int count1, OpArgs oap)
    {
        var op = oap.Op;
        var pastLine = VisualActive && !O.SelectionExclusive == false ? false : VisualActive && O.Selection != "old";
        for (var n = count1; n > 0; n--)
        {
            var s = CurLine;
            bool atEnd;
            if (!pastLine) atEnd = s.Length == 0 || _col >= VimText.PrevCharStart(s, s.Length) + (s.Length > 0 ? VimText.CharLen(s, VimText.PrevCharStart(s, s.Length)) : 0) - VimText.CharLen(s, VimText.PrevCharStart(s, s.Length)) && _col >= VimText.PrevCharStart(s, s.Length);
            else atEnd = _col >= s.Length;
            if (!pastLine && O.VirtualOneMore && _col < s.Length) atEnd = false;
            if (atEnd)
            {
                var wrap = (key == ' ' && O.WhichWrapHas('s')) || (key == 'l' && O.WhichWrapHas('l')) || (key == Keys.Right && O.WhichWrapHas('>'));
                if (wrap && _line < LastLine)
                {
                    if (op != OpType.None && !oap.Inclusive && !LineEmpty(_line)) oap.Inclusive = true;
                    else { _line++; _col = 0; _setCurswant = true; oap.Inclusive = false; }
                    continue;
                }
                if (op == OpType.None) { if (n == count1) return false; }
                else if (!LineEmpty(_line)) oap.Inclusive = true;
                break;
            }
            if (pastLine)
            {
                _setCurswant = true;
                _col += VimText.CharLen(s, _col);
            }
            else _col += VimText.CharLen(s, _col);
        }
        _setCurswant = true;
        return true;
    }

    // ---- j / k ----

    /// <summary>Vim's cursor_up/cursor_down: fails only if already at the first/last line.</summary>
    private bool CursorVertical(int delta, bool noOp)
    {
        _ = noOp;
        if (delta == 0) { Coladvance(_curswant); return true; }
        var target = _line;
        if (delta > 0)
        {
            if (_line >= LastLine) return false;
            target = Math.Min(LastLine, _line + delta);
            // skip closed folds
            if (_host!.TryGetClosedFold(_line, out _, out var fe) && delta >= 1) { target = Math.Min(LastLine, fe + 1 + (delta - 1)); if (fe >= LastLine) return false; }
        }
        else
        {
            if (_line <= 0) return false;
            target = Math.Max(0, _line + delta);
            if (_host!.TryGetClosedFold(_line, out var fs, out _) && delta <= -1) { target = Math.Max(0, fs - 1 + (delta + 1)); if (fs <= 0) return false; }
        }
        _line = target;
        if (_host!.TryGetClosedFold(_line, out var s2, out _) && delta < 0) _line = s2;
        Coladvance(_curswant);
        return true;
    }

    // ---- find char ----

    private bool FindCharMotion(int cmd, int ch, int count1, OpArgs oap, bool repeat)
    {
        if (!repeat)
        {
            _s.Search.FindChar = ch;
            _s.Search.FindForward = cmd is 'f' or 't';
            _s.Search.FindTill = cmd is 't' or 'T';
        }
        var forward = repeat ? cmd is 'f' or 't' : _s.Search.FindForward;
        var till = _s.Search.FindTill;
        return DoFindChar(_s.Search.FindChar, forward, till, count1, oap, stopAfterRepeat: repeat);
    }

    private bool RepeatFindChar(bool reverse, int count1, OpArgs oap)
    {
        if (_s.Search.FindChar == 0) return false;
        var forward = reverse ? !_s.Search.FindForward : _s.Search.FindForward;
        var till = _s.Search.FindTill;
        // ";" after "t" must not get stuck on the character right next to the cursor (cpo-; not included)
        var spec = till && count1 == 1 && !O.CpoHas(';');
        return DoFindChar(_s.Search.FindChar, forward, till, count1, oap, stopAfterRepeat: spec);
    }

    private bool DoFindChar(int ch, bool forward, bool till, int count, OpArgs oap, bool stopAfterRepeat)
    {
        var s = CurLine;
        var col = _col;
        var n = count;
        if (till && stopAfterRepeat)
        {
            // skip the adjacent char when it already is the target
            var adj = forward ? (col < s.Length ? col + VimText.CharLen(s, col) : -1) : (col > 0 ? VimText.PrevCharStart(s, col) : -1);
            if (adj >= 0 && adj < s.Length && VimText.CodePointAt(s, adj) == ch) col = adj;
        }
        while (n-- > 0)
        {
            int found = -1;
            if (forward)
            {
                var i = col < s.Length ? col + VimText.CharLen(s, col) : s.Length;
                while (i < s.Length) { if (VimText.CodePointAt(s, i) == ch) { found = i; break; } i += VimText.CharLen(s, i); }
            }
            else
            {
                var i = col > 0 ? VimText.PrevCharStart(s, col) : -1;
                while (i >= 0) { if (VimText.CodePointAt(s, i) == ch) { found = i; break; } i = i > 0 ? VimText.PrevCharStart(s, i) : -1; }
            }
            if (found < 0) return false;
            col = found;
        }
        if (till) col = forward ? VimText.PrevCharStart(s, col) : col + VimText.CharLen(s, col);
        _col = col;
        _setCurswant = true;
        oap.Inclusive = forward;
        return true;
    }

    // ---- marks ----

    private bool MarkMotion(int cmd, int markChar, OpArgs oap)
    {
        var linewise = cmd == '\'';
        if (!linewise) oap.UseRegOne = true;
        TextPosition? pos;
        pos = GetMark(markChar);
        if (markChar is >= 'A' and <= 'Z' && _s.GlobalMarks.TryGetValue((char)markChar, out var gm) && gm.Uri != _buf.Uri && gm.Uri is not null)
        {
            SetPcmark();
            Host.PerformAction(VimEditorAction.Edit, new VimActionArgs(gm.Uri.IsFile ? gm.Uri.LocalPath : gm.Uri.ToString()));
            return true;
        }
        if (markChar == '<' || markChar == '>') { }
        if (pos is not { } p) { Error("E20: Mark not set"); return false; }
        if (markChar is not ('[' or ']' or '<' or '>' or '.' or '^') || cmd is '\'' or '`') SetPcmark();
        _line = Math.Clamp(p.Line, 0, LastLine);
        if (linewise)
        {
            oap.Type = MType.Line;
            Beginline(BL.White | BL.Fix);
        }
        else
        {
            oap.Type = MType.Char;
            oap.Inclusive = false;
            _col = p.Character;
            if (markChar is '<' or '>' && _bs.VisualMode == VimMode.VisualLine && markChar == '>') _col = int.MaxValue;
            CheckCursor();
            _setCurswant = true;
        }
        return true;
    }

    // ---- screen-relative ----

    private bool ScreenLineMotion(int key, int count1, bool noOp)
    {
        var vp = Host.GetViewport();
        SetPcmark();
        int target;
        if (key == 'L')
        {
            target = vp.LastLine;
            if (count1 - 1 >= target) target = 0; else target -= count1 - 1;
        }
        else if (key == 'M')
        {
            var rows = 0;
            var n = 0;
            var total = 0;
            for (var l = vp.FirstLine; l <= vp.LastLine; l++) total += Host.GetDisplayRows(l);
            var half = (total + 1) / 2;
            for (n = 0; vp.FirstLine + n < LastLine; n++)
            {
                rows += Host.GetDisplayRows(vp.FirstLine + n);
                if (rows >= half) break;
            }
            if (n > 0 && rows > vp.Height) n--;
            target = Math.Min(vp.FirstLine + n, LastLine);
        }
        else target = Math.Min(vp.FirstLine + count1 - 1, LastLine);
        _line = target;
        if (noOp) CursorCorrect(vp);
        Beginline(BL.Sol | BL.Fix);
        return true;
    }

    /// <summary>Honour 'scrolloff' for H/L-style jumps (cursor_correct).</summary>
    private void CursorCorrect(VimViewport vp)
    {
        var so = Math.Min(O.ScrollOff, (vp.Height - 1) / 2);
        if (so <= 0) return;
        var top = vp.FirstLine == 0 ? 0 : vp.FirstLine + so;
        var bot = vp.LastLine >= LastLine ? LastLine : vp.LastLine - so;
        if (_line < top) _line = Math.Min(top, bot);
        else if (_line > bot) _line = Math.Max(bot, top);
    }

    // ---- g-motions ----

    private bool GMotion(Cmd m, int count0, int count1, OpArgs oap, ref bool keepCurswant)
    {
        var op = oap.Op;
        switch (m.Key2)
        {
            case 'g':
                oap.Type = MType.Line;
                SetPcmark();
                GotoLine(count0 > 0 ? count0 - 1 : 0);
                return true;
            case '_':
            {
                oap.Inclusive = true;
                _curswant = int.MaxValue;
                _setCurswant = false;
                if (!CursorVertical(count1 - 1, op == OpType.None)) return false;
                var s = CurLine;
                if (_col > 0 && _col >= s.Length) _col = VimText.PrevCharStart(s, s.Length);
                while (_col > 0 && s[_col] is ' ' or '\t') _col = VimText.PrevCharStart(s, _col);
                _setCurswant = true;
                return true;
            }
            case 'e' or 'E':
                oap.Inclusive = true;
                _setCurswant = true;
                return BckendWord(count1, m.Key2 == 'E', false);
            case 'j' or 'k':
                oap.Inclusive = false;
                return DisplayLineMove(m.Key2 == 'j' ? count1 : -count1, ref keepCurswant);
            case '0' or '^' or '$' or 'm' or 'M':
                return DisplayLineColumn(m.Key2, count0, oap);
            case 'o':
                return GotoByte(count1);
            case '\'' or '`':
                return MarkMotion(m.Key2 == '\'' ? '\'' : '`', m.NChar, oap) && KeepJumpless();
            case 'n' or 'N':
                return GnMotion(m.Key2 == 'n', count1, oap);
            case '*' or '#':
                return StarSearch(m.Key2 == '*', count1, wholeWord: false, oap);
            case 'D' or 'd':
                Host.ExecuteCommand(Core.Platform.KnownCommands.GotoDefinition, null);
                return true;
        }
        return false;
    }

    private bool KeepJumpless() => true;

    private bool GotoByte(int count1)
    {
        var target = count1 - 1;
        var off = 0;
        for (var l = 0; l <= LastLine; l++)
        {
            var len = L(l).Length + 1;
            if (target < off + len || l == LastLine)
            {
                SetPcmark();
                _line = l;
                _col = Math.Min(Math.Max(0, target - off), L(l).Length);
                CheckCursor();
                _setCurswant = true;
                return true;
            }
            off += len;
        }
        return true;
    }

    private bool DisplayLineColumn(int key2, int count0, OpArgs oap)
    {
        var w = Host.WrapWidth;
        var s = CurLine;
        var vcol = VimText.VcolOf(s, _col, O.TabStop);
        var segStart = w > 0 ? vcol / w * w : 0;
        var segEnd = w > 0 ? segStart + w - 1 : int.MaxValue;
        switch (key2)
        {
            case '0': _col = VimText.IndexOfVcol(s, segStart, O.TabStop, true); oap.Inclusive = false; break;
            case '^':
                _col = VimText.IndexOfVcol(s, segStart, O.TabStop, true);
                while (_col < s.Length && s[_col] is ' ' or '\t') _col++;
                oap.Inclusive = false;
                break;
            case '$':
                oap.Inclusive = true;
                _col = segEnd == int.MaxValue ? Math.Max(0, VimText.PrevCharStart(s, s.Length)) : VimText.IndexOfVcol(s, segEnd, O.TabStop, true);
                _curswant = int.MaxValue;
                _setCurswant = false;
                return true;
            case 'm':
                oap.Inclusive = false;
                _col = VimText.IndexOfVcol(s, segStart + Host.WindowWidth / 2, O.TabStop, true);
                break;
            case 'M':
            {
                oap.Inclusive = false;
                var pct = count0 > 0 ? Math.Min(count0, 100) : 50;
                _col = VimText.IndexOfVcol(s, VimText.DisplayWidth(s, O.TabStop) * pct / 100, O.TabStop, true);
                break;
            }
        }
        CheckCursor();
        _setCurswant = true;
        return true;
    }

    private bool DisplayLineMove(int delta, ref bool keepCurswant)
    {
        var w = Host.WrapWidth;
        keepCurswant = true;
        if (w <= 0)
        {
            UpdateCurswant();
            return CursorVertical(delta, true);
        }
        UpdateCurswant();
        var want = _curswant == int.MaxValue ? w - 1 : _curswant % w;
        var s = CurLine;
        var vcol = VimText.VcolOf(s, _col, O.TabStop);
        var seg = vcol / w;
        var line = _line;
        var n = Math.Abs(delta);
        var dir = Math.Sign(delta);
        for (; n > 0; n--)
        {
            var rows = Math.Max(1, (VimText.DisplayWidth(L(line), O.TabStop) + w - 1) / w);
            if (dir > 0)
            {
                if (seg + 1 < rows) seg++;
                else if (line < LastLine) { line++; seg = 0; }
                else return n != Math.Abs(delta);
            }
            else
            {
                if (seg > 0) seg--;
                else if (line > 0) { line--; seg = Math.Max(1, (VimText.DisplayWidth(L(line), O.TabStop) + w - 1) / w) - 1; }
                else return n != Math.Abs(delta);
            }
        }
        _line = line;
        var t = L(line);
        _col = VimText.IndexOfVcol(t, seg * w + want, O.TabStop, true);
        CheckCursor();
        return true;
    }

    // ---- bracket motions ----

    private bool BracketMotion(Cmd m, int count1, OpArgs oap)
    {
        var k = m.Key;
        var k2 = m.Key2;
        var forward = k == ']';
        switch (k2)
        {
            case '[' or ']':
            {
                // [[ ]] [] ][: sections ('{' or '}' in column 0)
                var flag = k2 == k ? '{' : '}';
                oap.Type = MType.Char;
                oap.Inclusive = false;
                oap.UseRegOne = true;
                _setCurswant = true;
                var okp = FindParagraph(forward, count1, flag, both: oap.Op != OpType.None && forward && flag == '{', oap);
                if (okp && oap.Op == OpType.None) Beginline(BL.White | BL.Fix);
                return okp;
            }
            case '(' or '{' or ')' or '}':
            {
                var findc = k2 is '(' or ')' ? (forward ? ')' : '(') : (forward ? '}' : '{');
                var want = k == '[' ? k2 : k2; // [( and [{ look backwards for the unmatched opener; ]) ]} forwards for the closer
                _ = want;
                var pos = new TextPosition(_line, _col);
                TextPosition? last = null;
                for (var n = count1; n > 0; n--)
                {
                    var p = FindMatchLimit(findc, forward, new TextPosition(_line, _col), unmatched: true);
                    if (p is null)
                    {
                        if (last is null) return false;
                        p = last;
                        _line = p.Value.Line; _col = p.Value.Character;
                        break;
                    }
                    _line = p.Value.Line;
                    _col = p.Value.Character;
                    last = p;
                }
                oap.Type = MType.Char;
                oap.Inclusive = false;
                SetPcmarkAt(pos);
                _setCurswant = true;
                return true;
            }
            case '\'' or '`':
            {
                // ]' [' ]` [`: next/previous lowercase mark
                var cur = new TextPosition(_line, _col);
                var cands = _bs.Marks.Where(p => p.HasValue).Select(p => p!.Value).Distinct().OrderBy(p => p).ToList();
                TextPosition? tgt = null;
                for (var n = count1; n > 0; n--)
                {
                    var ref0 = tgt ?? cur;
                    TextPosition? next = forward ? cands.Where(p => (k2 == '\'' ? p.Line > ref0.Line : p > ref0)).Cast<TextPosition?>().FirstOrDefault()
                                                  : cands.Where(p => (k2 == '\'' ? p.Line < ref0.Line : p < ref0)).Cast<TextPosition?>().LastOrDefault();
                    if (next is null) break;
                    tgt = next;
                }
                if (tgt is not { } t) return false;
                SetPcmark();
                _line = t.Line;
                if (k2 == '\'') { oap.Type = MType.Line; Beginline(BL.White | BL.Fix); }
                else { _col = t.Character; oap.Inclusive = false; CheckCursor(); }
                return true;
            }
        }
        return false;
    }

    private void SetPcmarkAt(TextPosition pos)
    {
        var cur = new TextPosition(_line, _col);
        _line = pos.Line; _col = pos.Character;
        SetPcmark();
        _line = cur.Line; _col = cur.Character;
    }

    // ------------------------------------------------------------------------------------------------------------
    // sentences and paragraphs
    // ------------------------------------------------------------------------------------------------------------

    private bool StartPS(int line, int para, bool both)
    {
        var s = L(line);
        if (s.Length == 0) return para == 0;
        var c = s[0];
        if (c == para || c == '\f' || (both && c == '}')) return true;
        return false;
    }

    private bool FindParagraph(bool forward, int count, int what, bool both, OpArgs oap)
    {
        var dir = forward ? 1 : -1;
        var curr = _line;
        while (count-- > 0)
        {
            var didSkip = false;
            for (var first = true; ; first = false)
            {
                if (L(curr).Length != 0) didSkip = true;
                if (!first && didSkip && StartPS(curr, what, both)) break;
                if ((curr += dir) < 0 || curr > LastLine)
                {
                    if (count > 0) return false;
                    curr -= dir;
                    break;
                }
            }
        }
        SetPcmark();
        if (both && L(curr).Length > 0 && L(curr)[0] == '}') curr++;
        _line = Math.Min(curr, LastLine);
        if (curr == LastLine && what != '}' && forward)
        {
            var line = L(curr);
            _col = line.Length;
            if (_col != 0) { _col = VimText.PrevCharStart(line, _col); oap.Inclusive = true; }
        }
        else _col = 0;
        return true;
    }

    private bool FindSentence(bool forward, int count)
    {
        var line = _line;
        var col = _col;
        var noskip = false;
        int Step(ref int l, ref int c) => forward ? Incl(ref l, ref c) : Decl(ref l, ref c);
        while (count-- > 0)
        {
            var skipToFound = false;
            if (CharAt(line, col) == 0)
            {
                do { if (Step(ref line, ref col) == -1) break; } while (CharAt(line, col) == 0);
                if (forward) skipToFound = true;
            }
            else if (forward && col == 0 && StartPS(line, 0, false))
            {
                if (line == LastLine) return false;
                line++;
                skipToFound = true;
            }
            else if (!forward) { var l2 = line; var c2 = col; Decl(ref l2, ref c2); line = l2; col = c2; }

            if (!skipToFound)
            {
                var foundDot = false;
                while (true)
                {
                    var c = CharAt(line, col);
                    if (!(c is ' ' or '\t' || (c != 0 && ".!?)]\"'".IndexOf((char)c) >= 0))) break;
                    var tl = line; var tc = col;
                    if (Decl(ref tl, ref tc) == -1 || (LineEmpty(tl) && forward)) break;
                    if (foundDot) break;
                    if (".!?".IndexOf((char)c) >= 0) foundDot = true;
                    if (")]\"'".IndexOf((char)c) >= 0 && (CharAt(tl, tc) == 0 || ".!?)]\"'".IndexOf((char)CharAt(tl, tc)) < 0)) break;
                    Decl(ref line, ref col);
                }
                var startl = line;
                while (true)
                {
                    var c = CharAt(line, col);
                    if (c == 0 || (col == 0 && StartPS(line, 0, false)))
                    {
                        if (!forward && line != startl) line++;
                        break;
                    }
                    if (c is '.' or '!' or '?')
                    {
                        var tl = line; var tc = col;
                        int cc;
                        do { if ((cc = Inc(ref tl, ref tc)) == -1) break; } while (")]\"'".IndexOf((char)(cc = CharAt(tl, tc))) is >= 0 and var _ && cc != 0);
                        if (cc == -1 || cc is ' ' or '\t' || cc == 0)
                        {
                            line = tl; col = tc;
                            if (CharAt(line, col) == 0) Inc(ref line, ref col);
                            break;
                        }
                    }
                    if (Step(ref line, ref col) == -1)
                    {
                        if (count > 0) return false;
                        noskip = true;
                        break;
                    }
                }
                col = Math.Min(col, L(line).Length);
            }
            // found: skip white space
            while (!noskip && CharAt(line, col) is ' ' or '\t') if (Incl(ref line, ref col) == -1) break;
        }
        SetPcmark();
        _line = line;
        _col = col;
        return true;
    }

    // ------------------------------------------------------------------------------------------------------------
    // % and bracket matching
    // ------------------------------------------------------------------------------------------------------------

    private bool PercentMotion(int count0, OpArgs oap)
    {
        oap.Type = MType.Char;
        oap.Inclusive = true;
        if (count0 > 0)
        {
            if (count0 > 100) return false;
            oap.Type = MType.Line;
            SetPcmark();
            _line = Math.Min(LastLine, Math.Max(0, (int)(((long)count0 * LineCount + 99) / 100) - 1));
            Beginline(BL.Sol | BL.Fix);
            return true;
        }
        var p = FindMatchPair(new TextPosition(_line, _col));
        if (p is null) return false;
        SetPcmark();
        _line = p.Value.Line;
        _col = p.Value.Character;
        _setCurswant = true;
        return true;
    }

    /// <summary>The "%" command: find the first bracket at/after the cursor on the line and jump to its match (also C comments).</summary>
    private TextPosition? FindMatchPair(TextPosition from)
    {
        var s = L(from.Line);
        var pairs = ParseMatchPairs();
        var i = from.Character;
        // C-style comments: cursor on /* or */
        if (i < s.Length)
        {
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*') return FindCommentEnd(from.Line, i + 2, forward: true);
            if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/') return FindCommentEnd(from.Line, i - 1, forward: false);
            if (i > 0 && s[i] == '/' && s[i - 1] == '*') return FindCommentEnd(from.Line, i - 2, forward: false);
        }
        for (; i < s.Length; i++)
        {
            foreach (var (open, close) in pairs)
            {
                if (s[i] == open) return FindMatchLimit(close, true, new TextPosition(from.Line, i), unmatched: false, initc: open);
                if (s[i] == close) return FindMatchLimit(open, false, new TextPosition(from.Line, i), unmatched: false, initc: close);
            }
        }
        return null;
    }

    private TextPosition? FindCommentEnd(int line, int col, bool forward)
    {
        if (forward)
        {
            for (var l = line; l <= LastLine; l++)
            {
                var s = L(l);
                var idx = s.IndexOf("*/", l == line ? Math.Min(col, s.Length) : 0, StringComparison.Ordinal);
                if (idx >= 0) return new TextPosition(l, idx + 1);
            }
        }
        else
        {
            for (var l = line; l >= 0; l--)
            {
                var s = L(l);
                var start = l == line ? Math.Min(col, s.Length - 1) : s.Length - 1;
                if (start < 0) continue;
                var idx = s.LastIndexOf("/*", start + 1 < s.Length ? start + 1 : s.Length - 1, StringComparison.Ordinal);
                if (idx >= 0) return new TextPosition(l, idx);
            }
        }
        return null;
    }

    private List<(char Open, char Close)> ParseMatchPairs()
    {
        var l = new List<(char, char)>();
        foreach (var p in O.MatchPairs.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (p.Length == 3 && p[1] == ':') l.Add((p[0], p[2]));
        return l;
    }

    /// <summary>
    /// Searches from <paramref name="from"/> for the match of a bracket (port of the core of findmatchlimit, quote and backslash aware).
    /// <paramref name="target"/> is the char to find, <paramref name="forward"/> the direction. With <paramref name="unmatched"/> the start
    /// is not a bracket: find the Nth unmatched [(, {, ), }.
    /// </summary>
    private TextPosition? FindMatchLimit(char target, bool forward, TextPosition from, bool unmatched, char initc = '\0')
    {
        // the bracket that nests: for target ')' scanning forward the nesting char is '('
        char nest = target switch { ')' => '(', '(' => ')', '}' => '{', '{' => '}', ']' => '[', '[' => ']', '>' => '<', '<' => '>', _ => '\0' };
        if (initc != '\0') nest = initc;
        if (nest == '\0') return null;
        var line = from.Line;
        var col = from.Character;
        var s = L(line);
        // Quote awareness: if the starting position is inside "..." only look inside quotes of the same line; otherwise skip quoted parens.
        var startInQuote = unmatched ? false : InDoubleQuote(s, col);
        var depth = 0;
        var step = forward ? 1 : -1;
        var l = line;
        var c = col;
        var first = true;
        while (true)
        {
            if (!first || true)
            {
                // advance one char
                if (forward)
                {
                    c++;
                    while (c >= L(l).Length)
                    {
                        if (l >= LastLine) return null;
                        l++; c = 0;
                        if (L(l).Length > 0) break;
                    }
                }
                else
                {
                    c--;
                    while (c < 0)
                    {
                        if (l <= 0) return null;
                        l--; c = L(l).Length - 1;
                        if (c >= 0) break;
                    }
                }
            }
            first = false;
            var t = L(l);
            if (c < 0 || c >= t.Length) continue;
            var ch = t[c];
            if (ch != nest && ch != target) continue;
            // backslash-escaped bracket: ignore
            var bs = 0;
            for (var j = c - 1; j >= 0 && t[j] == '\\'; j--) bs++;
            if ((bs & 1) == 1) continue;
            if (!unmatched && InDoubleQuote(t, c) != (l == line ? startInQuote : false) && (l == line || startInQuote == false)) continue;
            if (ch == target)
            {
                if (depth == 0) return new TextPosition(l, c);
                depth--;
            }
            else depth++;
        }
    }

    private static bool InDoubleQuote(string s, int col)
    {
        var q = false;
        for (var i = 0; i < col && i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '"') q = !q;
        }
        return q;
    }
}
