using System.Globalization;
using System.Text;
using CsCode.Core.Text;

namespace CsCode.Vim;

public sealed partial class VimEngine
{
    private readonly record struct BlockDef(int StartSpaces, int EndSpaces, int TextLen, int TextCol, int StartVcol, int EndVcol, int PreWhitesp, int PreWhitespC, int EndCharVcols, int StartCharVcols, bool IsShort, bool IsOneChar)
    {
        public string YankText(string line)
        {
            var text = TextLen > 0 && TextCol + TextLen <= line.Length ? line.Substring(TextCol, TextLen) : TextLen > 0 && TextCol < line.Length ? line.Substring(TextCol) : "";
            if (StartSpaces == 0 && EndSpaces == 0) return text;
            return new string(' ', StartSpaces) + text + new string(' ', EndSpaces);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Visual mode lifecycle
    // ------------------------------------------------------------------------------------------------------------

    private void StartVisualMode(VimMode m, int count0)
    {
        if (VisualActive)
        {
            if (_mode == m) EndVisual();
            else SetMode(m);
            return;
        }
        _visualStart = new TextPosition(_line, _col);
        SetMode(m);
        if (_restartEdit) { }
    }

    private void EndVisual()
    {
        if (!VisualActive) return;
        _bs.VisualStart = _visualStart;
        _bs.VisualEnd = new TextPosition(_line, _col);
        _bs.VisualMode = _mode;
        _bs.VisualCurswant = _curswant;
        _mode = VimMode.Normal;
        CheckCursor();
        if (_restartEdit && _insert is not null) ResumeInsert();
    }

    private void ReselectVisual()
    {
        if (_bs.VisualStart is not { } vs || _bs.VisualEnd is not { } ve) { Beep(); return; }
        var prevMode = _bs.VisualMode;
        var prevCurswant = _bs.VisualCurswant;
        if (VisualActive)
        {
            // gv in Visual mode exchanges the current and the previous area
            var curStart = _visualStart;
            var curEnd = new TextPosition(_line, _col);
            var curMode = _mode;
            var curSw = _curswant;
            _visualStart = vs;
            _line = Math.Min(ve.Line, LastLine); _col = ve.Character;
            _mode = prevMode;
            _curswant = prevCurswant;
            _bs.VisualStart = curStart; _bs.VisualEnd = curEnd; _bs.VisualMode = curMode; _bs.VisualCurswant = curSw;
            CheckCursor();
            return;
        }
        _visualStart = new TextPosition(Math.Min(vs.Line, LastLine), vs.Character);
        _line = Math.Min(ve.Line, LastLine);
        _col = ve.Character;
        SetMode(prevMode);
        _curswant = prevCurswant;
        _setCurswant = false;
        CheckCursor();
    }

    private VimSelection CurrentSelection()
    {
        if (!VisualActive) return VimSelection.None;
        var kind = _mode switch { VimMode.VisualLine => VimSelectionKind.Line, VimMode.VisualBlock => VimSelectionKind.Block, _ => VimSelectionKind.Character };
        var a = _visualStart;
        var b = new TextPosition(_line, _col);
        if (kind != VimSelectionKind.Block) return new VimSelection(kind, a, b);
        var ts = O.TabStop;
        var av = VimText.VcolOf(L(a.Line), a.Character, ts);
        var bv = VimText.VcolOf(L(b.Line), b.Character, ts);
        var ae = VimText.VcolEndOf(L(a.Line), a.Character, ts);
        var be = VimText.VcolEndOf(L(b.Line), b.Character, ts);
        return new VimSelection(kind, a, b, Math.Min(av, bv), Math.Max(ae, be), _curswant == int.MaxValue);
    }

    private string ShowCmdText()
    {
        if (_pend.Count == 0 && _mapBuf.Count == 0) return _mode == VimMode.Normal || VisualActive ? "" : "";
        var sb = new StringBuilder();
        foreach (var k in _pend) sb.Append(k < 32 && k != Keys.Esc ? "^" + (char)(k + 64) : Keys.ToNotation(k));
        foreach (var k in _mapBuf) sb.Append(k < 32 ? "^" + (char)(k + 64) : Keys.ToNotation(k));
        return sb.ToString();
    }

    private void StartVisualRedo()
    {
        var r = _pendingVisualRedo!;
        _pendingVisualRedo = null;
        var start = new TextPosition(_line, _col);
        _visualStart = start;
        var endLine = Math.Min(LastLine, _line + r.VisualLines - 1);
        _line = endLine;
        SetMode(r.VisualMode);
        if (r.VisualMode == VimMode.Visual)
        {
            if (r.VisualLines <= 1) { _curswant = VimText.VcolOf(L(start.Line), start.Character, O.TabStop) + r.VisualVcol - 1; }
            else _curswant = r.VisualVcol;
            Coladvance(_curswant);
        }
        else if (r.VisualMode == VimMode.VisualBlock)
        {
            if (r.VisualDollar) _curswant = int.MaxValue;
            else _curswant = VimText.VcolOf(L(start.Line), start.Character, O.TabStop) + r.VisualVcol - 1;
            Coladvance(_curswant);
        }
        else _col = start.Character;
        _setCurswant = false;
    }

    // ------------------------------------------------------------------------------------------------------------
    // commands that only exist in Visual mode
    // ------------------------------------------------------------------------------------------------------------

    private bool ExecVisualOnly(Cmd c)
    {
        var k = c.Key;
        switch (k)
        {
            case 'o':
            {
                var cur = new TextPosition(_line, _col);
                _line = _visualStart.Line; _col = _visualStart.Character;
                _visualStart = cur;
                _setCurswant = true;
                return true;
            }
            case 'O':
                if (_mode == VimMode.VisualBlock)
                {
                    var ts = O.TabStop;
                    var sv = VimText.VcolOf(L(_visualStart.Line), _visualStart.Character, ts);
                    var cv = VimText.VcolOf(CurLine, _col, ts);
                    var sl = _visualStart.Line;
                    var newStartCol = VimText.IndexOfVcol(L(sl), cv, ts, true);
                    var newCurCol = VimText.IndexOfVcol(CurLine, sv, ts, true);
                    _visualStart = new TextPosition(sl, newStartCol);
                    _col = newCurCol;
                    _setCurswant = true;
                    return true;
                }
                goto case 'o';
            case ':':
                StartExFromVisual();
                return true;
            case 'x' or Keys.Delete: return VisualOp(c, OpType.Delete);
            case 'd' or 'y' or 'c': return false;
            case 'X' or 'D': return VisualOp(c, OpType.Delete, linewise: _mode != VimMode.VisualBlock, dollar: _mode == VimMode.VisualBlock && k == 'D');
            case 's': return VisualOp(c, OpType.Change);
            case 'S' or 'R': return VisualOp(c, OpType.Change, linewise: _mode != VimMode.VisualBlock);
            case 'C': return VisualOp(c, OpType.Change, linewise: _mode != VimMode.VisualBlock, dollar: _mode == VimMode.VisualBlock);
            case 'Y': return VisualOp(c, OpType.Yank, linewise: _mode != VimMode.VisualBlock);
            case 'J': return VisualOp(c, OpType.Join, linewise: true);
            case 'u': return VisualOp(c, OpType.Lower);
            case 'U': return VisualOp(c, OpType.Upper);
            case '~': return VisualOp(c, OpType.Tilde);
            case 'r': return VisualOp(c, OpType.Replace);
            case 'I': return VisualOp(c, OpType.Insert, linewise: _mode != VimMode.VisualBlock);
            case 'A': return VisualOp(c, OpType.Append, linewise: _mode != VimMode.VisualBlock);
            case 'p' or 'P': VisualPut(c); return true;
            case 1 or 24: return VisualOp(c, k == 1 ? OpType.NrAdd : OpType.NrSub);
            case 'g' when c.Key2 is 'J': return VisualOp(c, OpType.JoinNoSpace, linewise: true);
            case 'g' when c.Key2 is 1 or 24: _gIncrement = true; return VisualOp(c, c.Key2 == 1 ? OpType.NrAdd : OpType.NrSub);
            case 'g' when c.Key2 is 'v': ReselectVisual(); return true;
            case 'z' when c.Key2 == 'f': return false;
        }
        if (k == 'g' && c.Key2 == 'n') { GnCommand(c); return true; }
        return false;
    }

    private bool _gIncrement;

    private bool VisualOp(Cmd c, OpType op, bool linewise = false, bool dollar = false)
    {
        var x = new Cmd { Key = c.Key, Key2 = c.Key2, NChar = c.NChar, NChar2 = c.NChar2, Count0 = c.Count0, Reg = c.Reg, Op = op, Visual = true };
        if (linewise && _mode != VimMode.VisualBlock) _forceLinewise = true;
        if (dollar) _curswant = int.MaxValue;
        ApplyVisualOperator(x);
        return true;
    }

    private bool _forceLinewise;

    private static bool OpOnLines(OpType op) => op is OpType.ShiftLeft or OpType.ShiftRight or OpType.Indent or OpType.Format or OpType.FormatKeep or OpType.Filter or OpType.Fold or OpType.Join or OpType.JoinNoSpace or OpType.Comment;

    /// <summary>Applies an operator to the Visual selection (the Visual branch of do_pending_operator).</summary>
    private void ApplyVisualOperator(Cmd c)
    {
        var oap = _oap;
        oap.ResetForMotion(c.Op, c.Reg);
        oap.IsVisual = true;
        var op = c.Op;
        var vmode = _mode;
        if (_forceLinewise) { if (vmode != VimMode.VisualBlock) vmode = VimMode.VisualLine; _forceLinewise = false; }
        var count0 = c.Count0;
        var cursor = new TextPosition(_line, _col);
        var start = _visualStart;
        var oldCol = _curswant;
        var dollar = _curswant == int.MaxValue;

        // remember for gv and '< '>
        _bs.VisualStart = start;
        _bs.VisualEnd = cursor;
        _bs.VisualMode = _mode;
        _bs.VisualCurswant = _curswant;

        if (vmode == VimMode.VisualLine) start = new TextPosition(start.Line, 0);
        if (start < cursor) { oap.Start = start; oap.End = cursor; }
        else { oap.Start = cursor; oap.End = start; }
        _line = oap.Start.Line; _col = oap.Start.Character;
        oap.LineCount = oap.End.Line - oap.Start.Line + 1;

        // block corners
        if (vmode == VimMode.VisualBlock)
        {
            oap.BlockMode = true;
            var ts = O.TabStop;
            var sl = L(oap.Start.Line); var el = L(oap.End.Line);
            var sStart = VimText.VcolOf(sl, oap.Start.Character, ts);
            var sEnd = VimText.VcolEndOf(sl, oap.Start.Character, ts);
            oap.StartVcol = sStart; oap.EndVcol = sEnd;
            var eStart = VimText.VcolOf(el, oap.End.Character, ts);
            var eEnd = VimText.VcolEndOf(el, oap.End.Character, ts);
            if (eStart < oap.StartVcol) oap.StartVcol = eStart;
            if (eEnd > oap.EndVcol) oap.EndVcol = eEnd;
            if (dollar)
            {
                oap.IsDollar = true;
                oap.EndVcol = 0;
                for (var l = oap.Start.Line; l <= oap.End.Line; l++)
                {
                    var s = L(l);
                    var e = s.Length == 0 ? 0 : VimText.VcolEndOf(s, VimText.PrevCharStart(s, s.Length), ts);
                    if (e > oap.EndVcol) oap.EndVcol = e;
                }
            }
            // corners: upper-left and lower-right
            _line = oap.End.Line; _col = 0; Coladvance(oap.EndVcol); oap.End = new TextPosition(_line, _col);
            _line = oap.Start.Line; _col = 0; Coladvance(oap.StartVcol); oap.Start = new TextPosition(_line, _col);
        }

        oap.Inclusive = true;
        oap.Type = vmode == VimMode.VisualLine ? MType.Line : MType.Char;
        if (vmode == VimMode.VisualBlock) oap.Type = MType.Char;
        var endIsNul = CharAt(oap.End.Line, oap.End.Character) == 0;
        if (vmode != VimMode.VisualLine && vmode != VimMode.VisualBlock && endIsNul)
        {
            oap.Inclusive = false;
            if (!O.SelectionExclusive && !OpOnLines(op) && oap.End.Line < LastLine)
            {
                oap.End = new TextPosition(oap.End.Line + 1, 0);
                oap.LineCount++;
            }
        }
        else if (O.SelectionExclusive && vmode == VimMode.Visual) { }

        // exit Visual mode
        _mode = VimMode.Normal;
        if (!OpOnLines(op) || true) { }

        // prepare redo
        if (op is not (OpType.Yank or OpType.Fold) && op != OpType.None)
        {
            var r = new RedoRecord { Reg = c.Reg, Count = count0, IsVisual = true, VisualMode = vmode };
            r.VisualLines = oap.LineCount;
            r.VisualDollar = dollar;
            if (vmode == VimMode.VisualBlock) r.VisualVcol = oap.EndVcol - oap.StartVcol + 1;
            else if (dollar) r.VisualVcol = int.MaxValue;
            else if (oap.LineCount <= 1) r.VisualVcol = VimText.VcolEndOf(L(oap.End.Line), oap.End.Character, O.TabStop) - VimText.VcolOf(L(oap.Start.Line), oap.Start.Character, O.TabStop) + 1;
            else r.VisualVcol = VimText.VcolEndOf(L(oap.End.Line), oap.End.Character, O.TabStop);
            r.Keys.Add(c.Key);
            if (c.Key2 != 0) r.Keys.Add(c.Key2);
            if (c.NChar != 0) r.Keys.Add(c.NChar);
            if (c.NChar2 != 0) r.Keys.Add(c.NChar2);
            _redo = r;
            _redoBuilding = r;
        }
        var oldInc = _gIncrement;
        _gIncrement = false;
        _incrementProgressive = oldInc;
        ApplyOperator(c, oap, count0, oldCol);
        _incrementProgressive = false;
        if (_restartEdit && _insert is not null && _mode == VimMode.Normal) ResumeInsert();
    }

    private bool _incrementProgressive;

    /// <summary>Operator applied after `d^V{motion}` forced a block selection.</summary>
    private void DoBlockOperator(Cmd c, OpArgs oap, TextPosition start, TextPosition cursor, int count0)
    {
        _visualStart = start;
        _mode = VimMode.VisualBlock;
        var vc = new Cmd { Key = c.Key, Key2 = c.Key2, Op = c.Op, Count0 = count0, Reg = c.Reg, Visual = true, NChar = c.NChar };
        ApplyVisualOperator(vc);
    }

    // ------------------------------------------------------------------------------------------------------------
    // block operations
    // ------------------------------------------------------------------------------------------------------------

    private BlockDef BlockPrep(OpArgs oap, int lnum, bool isDel)
    {
        var line = L(lnum);
        var ts = O.TabStop;
        int startSpaces = 0, endSpaces = 0, textLen = 0, startVcol = 0, endVcol = 0, preWhitesp = 0, preWhitespC = 0, endCharVcols = 0, startCharVcols = 0;
        bool isShort = false, isOneChar = false;
        var pstart = 0;
        var prevPstart = 0;
        var incr = 0;
        while (startVcol < oap.StartVcol && pstart < line.Length)
        {
            incr = VimText.WidthAt(line, pstart, startVcol, ts);
            startVcol += incr;
            if (line[pstart] is ' ' or '\t') { preWhitesp += incr; preWhitespC++; } else { preWhitesp = 0; preWhitespC = 0; }
            prevPstart = pstart;
            pstart += VimText.CharLen(line, pstart);
        }
        startCharVcols = incr;
        int pend;
        if (startVcol < oap.StartVcol)
        {
            endVcol = startVcol;
            isShort = true;
            if (!isDel || oap.Op == OpType.Append) endSpaces = oap.EndVcol - oap.StartVcol + 1;
            pend = pstart;
        }
        else
        {
            startSpaces = startVcol - oap.StartVcol;
            if (isDel && startSpaces != 0) startSpaces = startCharVcols - startSpaces;
            pend = pstart;
            endVcol = startVcol;
            if (endVcol > oap.EndVcol)
            {
                isOneChar = true;
                if (oap.Op == OpType.Insert) endSpaces = startCharVcols - startSpaces;
                else if (oap.Op == OpType.Append) { startSpaces += oap.EndVcol - oap.StartVcol + 1; endSpaces = startCharVcols - startSpaces; }
                else
                {
                    startSpaces = oap.EndVcol - oap.StartVcol + 1;
                    if (isDel && oap.Op != OpType.ShiftLeft)
                    {
                        startSpaces = startCharVcols - (startVcol - oap.StartVcol);
                        endSpaces = endVcol - oap.EndVcol - 1;
                    }
                }
            }
            else
            {
                var prevPend = pend;
                while (endVcol <= oap.EndVcol && pend < line.Length)
                {
                    prevPend = pend;
                    incr = VimText.WidthAt(line, pend, endVcol, ts);
                    endVcol += incr;
                    pend += VimText.CharLen(line, pend);
                }
                if (endVcol <= oap.EndVcol && (!isDel || oap.Op == OpType.Append || oap.Op == OpType.Replace))
                {
                    isShort = true;
                    if (oap.Op == OpType.Append) endSpaces = oap.EndVcol - endVcol + 1;
                    else endSpaces = 0;
                }
                else if (endVcol > oap.EndVcol)
                {
                    endSpaces = endVcol - oap.EndVcol - 1;
                    if (!isDel && endSpaces != 0)
                    {
                        endSpaces = incr - endSpaces;
                        if (pend != pstart) pend = prevPend;
                    }
                }
            }
            endCharVcols = incr;
            if (isDel && startSpaces != 0) pstart = prevPstart;
            textLen = pend - pstart;
        }
        return new BlockDef(startSpaces, endSpaces, textLen, pstart, startVcol, endVcol, preWhitesp, preWhitespC, endCharVcols, startCharVcols, isShort, isOneChar);
    }

    private void DeleteBlock(OpArgs oap)
    {
        for (var l = oap.Start.Line; l <= oap.End.Line; l++)
        {
            var bd = BlockPrep(oap, l, true);
            if (bd.TextLen == 0) continue;
            if (l == oap.Start.Line) { _line = l; _col = bd.TextCol + bd.StartSpaces; }
            var old = L(l);
            var n = bd.TextLen - bd.StartSpaces - bd.EndSpaces;
            _ = n;
            var newLine = old.Substring(0, bd.TextCol) + new string(' ', bd.StartSpaces + bd.EndSpaces) + old.Substring(Math.Min(old.Length, bd.TextCol + bd.TextLen));
            SetLine(l, newLine, bd.TextCol);
        }
        _line = oap.Start.Line;
        CheckCursor();
        NoteChange(oap.Start.Line, _col);
        oap.LineCount = 0;
    }

    private void ShiftBlockLine(OpArgs oap, int line, int amount)
    {
        var left = oap.Op == OpType.ShiftLeft;
        var bd = BlockPrep(oap, line, true);
        if (bd.IsShort) return;
        var sw = O.Sw;
        var ts = O.TabStop;
        var total = amount * sw;
        var old = L(line);
        if (!left)
        {
            total += bd.PreWhitesp;
            var wsVcol = bd.StartVcol - bd.PreWhitesp;
            var textStart = bd.TextCol;
            var startSpaces = bd.StartSpaces;
            if (startSpaces != 0)
            {
                if (VimText.CharLen(old, textStart) == 1) textStart++; else { wsVcol = 0; startSpaces = 0; }
            }
            var vcol = bd.StartVcol;
            while (textStart < old.Length && old[textStart] is ' ' or '\t')
            {
                var incr = VimText.WidthAt(old, textStart, vcol, ts);
                total += incr; vcol += incr; textStart++;
            }
            int tabs = 0, spaces;
            if (!O.ExpandTab) tabs = (wsVcol % ts + total) / ts;
            if (tabs > 0) spaces = (wsVcol % ts + total) % ts; else spaces = total;
            var preCol = bd.TextCol - bd.PreWhitespC;
            if (bd.StartSpaces != 0 && startSpaces != 0) preCol = bd.TextCol;
            var newLine = old.Substring(0, Math.Max(0, bd.TextCol - bd.PreWhitespC - (bd.StartSpaces != 0 && startSpaces == 0 ? 0 : 0))) + new string('\t', tabs) + new string(' ', spaces) + old.Substring(textStart);
            _ = preCol;
            SetLine(line, newLine, bd.TextCol);
        }
        else
        {
            // left shift: remove up to `total` columns of white space starting at the block's left edge
            var textStart = bd.TextCol;
            var vcol = bd.StartVcol;
            var wsStart = textStart;
            var i = textStart;
            var removed = 0;
            var wsVcol = vcol;
            var wsEnd = i;
            while (i < old.Length && old[i] is ' ' or '\t')
            {
                var incr = VimText.WidthAt(old, i, wsVcol, ts);
                wsVcol += incr; i++;
                wsEnd = i;
            }
            var wsWidth = wsVcol - vcol;
            removed = Math.Min(total, wsWidth);
            var keepWidth = wsWidth - removed;
            var keep = new string(' ', keepWidth);
            if (!O.ExpandTab && keepWidth > 0)
            {
                var sb = new StringBuilder();
                var v = vcol;
                while ((v / ts + 1) * ts <= vcol + keepWidth) { sb.Append('\t'); v = (v / ts + 1) * ts; }
                sb.Append(' ', vcol + keepWidth - v);
                keep = sb.ToString();
            }
            SetLine(line, old.Substring(0, wsStart) + keep + old.Substring(wsEnd), wsStart);
        }
    }

    private void OpReplaceChars(OpArgs oap, int ch)
    {
        if (!CanModify()) return;
        if (ch is Keys.Esc or 3) { return; }
        var first = oap.Start.Line;
        var last = oap.End.Line;
        var rep = ch == Keys.CR || ch == Keys.NL ? "\r" : char.ConvertFromUtf32(ch);
        if (oap.BlockMode)
        {
            for (var l = first; l <= last; l++)
            {
                var bd = BlockPrep(oap, l, true);
                if (bd.TextLen == 0 && bd.StartSpaces == 0 && bd.EndSpaces == 0) continue;
                var old = L(l);
                var sb = new StringBuilder(old.Substring(0, bd.TextCol));
                // replace every cell of the block with the new char (wide/tab chars replaced by the right number of cells)
                var numChars = oap.EndVcol - oap.StartVcol + 1;
                if (bd.IsShort && bd.TextLen > 0 || bd.IsOneChar) numChars = Math.Max(0, Math.Min(numChars, bd.EndVcol - bd.StartVcol));
                if (bd.IsOneChar) numChars = oap.EndVcol - oap.StartVcol + 1;
                var textCells = bd.TextLen > 0 ? VimText.VcolOf(old, bd.TextCol + bd.TextLen, O.TabStop) - VimText.VcolOf(old, bd.TextCol, O.TabStop) : 0;
                var cells = Math.Min(numChars, bd.StartSpaces + textCells + bd.EndSpaces);
                if (bd.IsShort) cells = Math.Min(numChars, bd.StartSpaces + textCells);
                var perChar = VimText.CharWidth(ch) == 2 ? 2 : 1;
                sb.Append(rep == "\r" ? " " : string.Concat(Enumerable.Repeat(rep, Math.Max(0, cells / perChar))));
                if (bd.IsShort && false) { }
                sb.Append(new string(' ', bd.StartSpaces == 0 ? 0 : 0));
                sb.Append(old.Substring(Math.Min(old.Length, bd.TextCol + bd.TextLen)));
                if (bd.EndSpaces > 0 && !bd.IsShort) { }
                SetLine(l, sb.ToString(), bd.TextCol);
            }
            _line = first;
            _col = oap.Start.Character;
            CheckCursor();
            _bs.OpStart = oap.Start;
            _bs.OpEnd = oap.End;
            return;
        }
        var start = oap.Start;
        var end = oap.End;
        if (oap.Type == MType.Line) { start = new TextPosition(start.Line, 0); end = new TextPosition(end.Line, Math.Max(0, L(end.Line).Length - 1)); }
        else if (!oap.Inclusive) { var el = end.Line; var ec = end.Character; Dec(ref el, ref ec); end = new TextPosition(el, ec); }
        var newlines = ch is Keys.CR or Keys.NL;
        for (var l = start.Line; l <= end.Line; l++)
        {
            var s = L(l);
            var sc = l == start.Line ? start.Character : 0;
            var ec = l == end.Line ? end.Character + (end.Character < s.Length ? VimText.CharLen(s, end.Character) : 0) : s.Length;
            if (ec <= sc) continue;
            var sb = new StringBuilder(s.Substring(0, sc));
            for (var i = sc; i < ec && i < s.Length;)
            {
                var len = VimText.CharLen(s, i);
                // keep wide chars aligned: a wide replaced by a narrow one stays one cell (Vim does the same)
                sb.Append(newlines ? "" : rep);
                i += len;
            }
            sb.Append(s.AsSpan(Math.Min(ec, s.Length)));
            if (newlines) SetLine(l, sb.ToString(), sc);
            else SetLine(l, sb.ToString(), sc);
        }
        _line = start.Line;
        _col = start.Character;
        CheckCursor();
        _bs.OpStart = oap.Start;
        _bs.OpEnd = oap.End;
        _ = newlines;
    }

    // ---- block insert / append / change ----

    private sealed class BlockInsertState
    {
        public OpArgs Oap = null!;
        public bool Append;
        public int StartLine, EndLine, StartCol, Vcol;
        public bool Dollar;
        public bool IsChange;
        public int OrigLineLen;
    }

    private BlockInsertState? _blockInsert;

    private void OpBlockInsert(OpArgs oap, bool append)
    {
        if (!CanModify()) return;
        if (!oap.BlockMode)
        {
            if (append) { _line = oap.End.Line; _col = Math.Min(oap.End.Character, L(oap.End.Line).Length); if (!LineEmpty(_line) && oap.Type != MType.Line) _col += VimText.CharLen(CurLine, Math.Min(_col, CurLine.Length - 1)); else _col = CurLine.Length; }
            else { _line = oap.Start.Line; _col = oap.Type == MType.Line ? 0 : oap.Start.Character; }
            _col = Math.Min(_col, CurLine.Length);
            StartInsert(append ? 'A' : 'I', 1, false);
            return;
        }
        var first = oap.Start.Line;
        var st = new BlockInsertState { Oap = oap, Append = append, StartLine = first, EndLine = oap.End.Line };
        var ts = O.TabStop;
        if (append)
        {
            st.Dollar = oap.IsDollar;
            var bd = BlockPrep(oap, first, true);
            _line = first;
            if (oap.IsDollar) { _col = L(first).Length; }
            else
            {
                if (bd.IsShort)
                {
                    // pad the first line with spaces up to the block's right edge
                    var pad = oap.EndVcol + 1 - VimText.DisplayWidth(L(first), ts);
                    if (pad > 0) SetLine(first, L(first) + new string(' ', pad));
                    _col = L(first).Length;
                }
                else _col = bd.TextCol + bd.TextLen;
                if (bd.StartSpaces > 0 && bd.TextLen == 0) { }
            }
            st.StartCol = _col;
            st.Vcol = VimText.VcolOf(L(first), _col, ts);
        }
        else
        {
            var bd = BlockPrep(oap, first, true);
            _line = first;
            _col = bd.TextCol;
            if (bd.StartSpaces > 0 && !bd.IsShort)
            {
                // inside a tab: insert at the start of the character
            }
            st.StartCol = _col;
            st.Vcol = oap.StartVcol;
        }
        st.OrigLineLen = L(first).Length;
        _blockInsert = st;
        StartInsert(append ? 'A' : 'I', 1, false);
    }

    private void OpBlockChange(OpArgs oap)
    {
        // `c` in block mode: the block was deleted; insert at its left edge and replicate on Esc
        var first = oap.Start.Line;
        var st = new BlockInsertState { Oap = oap, StartLine = first, EndLine = oap.End.Line, IsChange = true, Vcol = oap.StartVcol };
        var bd = BlockPrep(new OpArgs { Op = OpType.Insert, StartVcol = oap.StartVcol, EndVcol = oap.EndVcol }, first, true);
        _line = first;
        _col = bd.IsShort ? L(first).Length : bd.TextCol;
        if (bd.IsShort)
        {
            var pad = oap.StartVcol - VimText.DisplayWidth(L(first), O.TabStop);
            if (pad > 0) { SetLine(first, L(first) + new string(' ', pad)); _col = L(first).Length; }
        }
        st.StartCol = _col;
        st.OrigLineLen = L(first).Length;
        _blockInsert = st;
        StartInsert('c', 1, false);
    }

    /// <summary>Called when Insert mode ends: copy the text typed on the first line into the other lines of the block.</summary>
    private void FinishBlockInsert(TextPosition endPos, int firstLineLenBefore)
    {
        var st = _blockInsert!;
        _blockInsert = null;
        var first = st.StartLine;
        if (endPos.Line != first || st.EndLine == first) return;
        var line = L(first);
        var added = line.Length - st.OrigLineLen;
        if (added <= 0) return;
        var text = line.Substring(Math.Min(st.StartCol, line.Length), Math.Min(added, line.Length - Math.Min(st.StartCol, line.Length)));
        if (text.Length == 0) return;
        var ts = O.TabStop;
        for (var l = first + 1; l <= st.EndLine; l++)
        {
            var s = L(l);
            var oap = st.Oap;
            if (st.Append)
            {
                if (st.Dollar) { SetLine(l, s + text); continue; }
                var width = VimText.DisplayWidth(s, ts);
                if (width <= oap.EndVcol)
                {
                    // short line: pad up to the block edge then append
                    SetLine(l, s + new string(' ', oap.EndVcol + 1 - width) + text);
                }
                else
                {
                    var bd = BlockPrep(new OpArgs { Op = OpType.Append, StartVcol = oap.StartVcol, EndVcol = oap.EndVcol }, l, true);
                    var at = bd.TextCol + bd.TextLen;
                    SetLine(l, s.Substring(0, at) + text + s.Substring(at));
                }
            }
            else if (st.IsChange)
            {
                var w = VimText.DisplayWidth(s, ts);
                if (w < st.Vcol) continue;
                var idx = VimText.IndexOfVcol(s, st.Vcol, ts, false);
                SetLine(l, s.Substring(0, idx) + text + s.Substring(idx));
            }
            else
            {
                var w = VimText.DisplayWidth(s, ts);
                if (w <= oap.StartVcol && !(w == oap.StartVcol && false)) { if (w < oap.StartVcol || s.Length == 0 || true) { if (w <= oap.StartVcol) continue; } }
                var bd = BlockPrep(new OpArgs { Op = OpType.Insert, StartVcol = oap.StartVcol, EndVcol = oap.EndVcol }, l, true);
                if (bd.IsShort) continue;
                if (bd.StartSpaces > 0 && s.Length > bd.TextCol && s[bd.TextCol] == '\t')
                {
                    // splitting a tab: expand it to spaces around the insert
                    var tabW = bd.StartCharVcols;
                    var before = tabW - bd.StartSpaces;
                    var after = bd.StartSpaces;
                    _ = after;
                    SetLine(l, s.Substring(0, bd.TextCol) + new string(' ', before) + text + new string(' ', tabW - before) + s.Substring(bd.TextCol + 1));
                }
                else SetLine(l, s.Substring(0, bd.TextCol) + text + s.Substring(bd.TextCol));
            }
        }
        _line = first;
        _col = Math.Min(st.StartCol, L(first).Length);
        _bs.OpStart = new TextPosition(first, st.StartCol);
        _bs.OpEnd = new TextPosition(st.EndLine, st.StartCol + text.Length);
        CheckCursor();
    }

    // ---- visual put ----

    private void VisualPut(Cmd c)
    {
        if (!CanModify()) return;
        var keepRegister = c.Key == 'P';
        var regName = c.Reg;
        var src = _s.Registers.ForRead(regName == 0 && _s.UseSystemClipboard ? '*' : regName, () => Host.ReadClipboard());
        if (src is null || src.IsEmpty) { Error($"E353: Nothing in register {(regName == 0 ? "\"" : ((char)regName).ToString())}"); return; }
        // copy the register content NOW: the delete below overwrites the unnamed register
        var saved = new Register();
        saved.CopyFrom(src);
        var vmode = _mode;
        var del = new Cmd { Key = 'd', Op = OpType.Delete, Visual = true, Reg = keepRegister ? '_' : 0 };
        var oldMode = vmode;
        // determine selection bounds before deleting
        var startPos = _visualStart;
        var curPos = new TextPosition(_line, _col);
        var lo = startPos < curPos ? startPos : curPos;
        var hi = startPos < curPos ? curPos : startPos;
        var lastLineBefore = LineCount;
        ApplyVisualOperator(del);
        // put: before the cursor when the deleted text ended before the line end (or was the last line), else after
        var flags = PutFlags.None;
        var forward = false;
        var selWasLine = oldMode == VimMode.VisualLine;
        if (selWasLine)
        {
            // linewise selection: text goes in place of the deleted lines
            forward = _line < LastLine || lastLineBefore - 0 > LineCount && false ? false : false;
            var deletedAtEnd = lo.Line >= LineCount;
            if (deletedAtEnd) { _line = LastLine; forward = true; }
            else { _line = Math.Min(lo.Line, LastLine); forward = false; }
            if (saved.Type != VimRegisterType.Line) flags |= PutFlags.Line;
        }
        else
        {
            var lineLen = CurLine.Length;
            forward = _col < lineLen && _col >= 0 && lo.Character >= lineLen ? true : false;
            // after deleting to the end of the line the cursor sits on the last char: put after it
            if (lo.Character >= lineLen && lineLen > 0) forward = true;
            if (saved.Type == VimRegisterType.Line) flags |= PutFlags.LineSplit;
        }
        if (oldMode == VimMode.VisualBlock) { forward = false; }
        _bs.VisualStart = lo; _bs.VisualEnd = hi;
        DoPut(0, forward, c.Count1, flags, saved);
        if (!keepRegister && false) { }
        _ = oldMode;
    }

    // ------------------------------------------------------------------------------------------------------------
    // block put
    // ------------------------------------------------------------------------------------------------------------

    private void PutBlock(Register reg, bool forward, int count, PutFlags flags)
    {
        var ts = O.TabStop;
        var yarr = reg.Lines;
        var ysize = yarr.Length;
        var yWidth = Math.Max(0, reg.Width - 1);
        var s0 = CurLine;
        var c = CharAtCursor();
        int col;
        if (forward && c != 0)
        {
            col = VimText.VcolEndOf(s0, _col, ts);
            _col += VimText.CharLen(s0, _col);
            col++;
        }
        else col = VimText.VcolOf(s0, Math.Min(_col, s0.Length), ts);
        var lnum = _line;
        var nrLines = 0;
        var totlen = 0;
        var curLine = _line;
        var bdTextCol = 0;
        for (var i = 0; i < ysize; i++)
        {
            if (curLine > LastLine) { InsertLines(LineCount, [""]); nrLines++; }
            var oldp = L(curLine);
            var vcol = 0;
            var ptr = 0;
            var incr = 0;
            while (vcol < col && ptr < oldp.Length)
            {
                incr = VimText.WidthAt(oldp, ptr, vcol, ts);
                vcol += incr;
                ptr += VimText.CharLen(oldp, ptr);
            }
            bdTextCol = ptr;
            var startSpaces = 0;
            var endSpaces = 0;
            var delcount = 0;
            var shortline = vcol < col || (vcol == col && ptr >= oldp.Length);
            if (vcol < col) startSpaces = col - vcol;
            else if (vcol > col)
            {
                endSpaces = vcol - col;
                startSpaces = incr - endSpaces;
                bdTextCol = VimText.PrevCharStart(oldp, bdTextCol);
                delcount = 1;
                if (oldp[bdTextCol] != '\t') { delcount = 0; endSpaces = 0; }
            }
            var yank = yarr[i];
            var spaces = 0;
            if ((flags & (PutFlags)64) == 0)
            {
                spaces = yWidth + 1 - VimText.DisplayWidth(yank, ts);
                if (spaces < 0) spaces = 0;
            }
            var sb = new StringBuilder();
            sb.Append(oldp, 0, bdTextCol);
            sb.Append(' ', startSpaces);
            for (var j = 0; j < count; j++)
            {
                sb.Append(yank);
                if ((j < count - 1 || !shortline) && spaces > 0) sb.Append(' ', spaces);
            }
            sb.Append(' ', endSpaces);
            sb.Append(oldp, bdTextCol + delcount, oldp.Length - bdTextCol - delcount);
            totlen = count * (yank.Length + spaces) + startSpaces + endSpaces;
            if (!((count - 1 >= 0) && !shortline) && spaces > 0) totlen -= spaces;
            SetLine(curLine, sb.ToString(), bdTextCol);
            curLine++;
            if (i == 0) _col += startSpaces;
        }
        _bs.OpStart = new TextPosition(lnum, _col);
        _bs.OpEnd = new TextPosition(curLine - 1, Math.Max(0, bdTextCol + totlen - 1));
        if ((flags & PutFlags.CursEnd) != 0)
        {
            _line = _bs.OpEnd.Value.Line;
            _col = Math.Min(_bs.OpEnd.Value.Character + 1, L(_line).Length);
        }
        else _line = lnum;
        _ = nrLines;
    }

    // ------------------------------------------------------------------------------------------------------------
    // numbers: Ctrl-A / Ctrl-X
    // ------------------------------------------------------------------------------------------------------------

    private bool AddToNumberAtCursor(int delta)
    {
        var s = CurLine;
        var res = ComputeIncrement(s, _col, delta, false, out var startCol, out var endCol, out var newText, out var cursorOffset);
        if (!res) return false;
        ReplaceInLine(_line, startCol, endCol - startCol, newText);
        _col = startCol + newText.Length - 1;
        if (_col < 0) _col = 0;
        _bs.OpStart = new TextPosition(_line, startCol);
        _bs.OpEnd = new TextPosition(_line, _col);
        _setCurswant = true;
        _ = cursorOffset;
        return true;
    }

    private bool ComputeIncrement(string ptr, int from, int delta, bool visual, out int startCol, out int endCol, out string newText, out int cursorOffset)
    {
        startCol = endCol = 0; newText = ""; cursorOffset = 0;
        var dobin = O.NrBin; var dooct = O.NrOctal; var dohex = O.NrHex; var doalp = O.NrAlpha; var unsigned = O.NrUnsigned;
        char At(int i) => i >= 0 && i < ptr.Length ? ptr[i] : '\0';
        static bool IsDig(char c) => c is >= '0' and <= '9';
        static bool IsHex(char c) => IsDig(c) || c is >= 'a' and <= 'f' or >= 'A' and <= 'F';
        static bool IsBin(char c) => c is '0' or '1';
        var col = Math.Min(from, Math.Max(0, ptr.Length - 1));
        if (ptr.Length == 0) return false;
        if (!visual)
        {
            if (dobin) while (col > 0 && IsBin(At(col))) col--;
            if (dohex) while (col > 0 && IsHex(At(col))) col--;
            if (dobin && dohex && !(col > 0 && At(col) is 'x' or 'X' && At(col - 1) == '0' && IsHex(At(col + 1))))
            {
                col = from;
                while (col > 0 && IsDig(At(col))) col--;
            }
            if ((dohex && col > 0 && At(col) is 'x' or 'X' && At(col - 1) == '0' && IsHex(At(col + 1))) || (dobin && col > 0 && At(col) is 'b' or 'B' && At(col - 1) == '0' && IsBin(At(col + 1))))
                col--;
            else
            {
                col = from;
                while (col < ptr.Length && !IsDig(At(col)) && !(doalp && char.IsAsciiLetter(At(col)))) col++;
                while (col > 0 && IsDig(At(col - 1)) && !(doalp && char.IsAsciiLetter(At(col)))) col--;
            }
        }
        else col = from;
        var first = At(col);
        if (!IsDig(first) && !(doalp && char.IsAsciiLetter(first))) return false;
        if (doalp && char.IsAsciiLetter(first))
        {
            var isUpper = char.IsAsciiLetterUpper(first);
            var n = (isUpper ? first - 'A' : first - 'a') + delta;
            n = Math.Clamp(n, 0, 25);
            startCol = col; endCol = col + 1;
            newText = ((char)((isUpper ? 'A' : 'a') + n)).ToString();
            return true;
        }
        var negative = false;
        var startAt = col;
        if (col > 0 && At(col - 1) == '-' && !unsigned && !visual) { negative = true; startAt = col - 1; }
        // parse the number
        char pre = '\0';
        var p = col;
        if (first == '0' && (dohex && At(col + 1) is 'x' or 'X' && IsHex(At(col + 2))))
        { pre = At(col + 1); p = col + 2; }
        else if (first == '0' && (dobin && At(col + 1) is 'b' or 'B' && IsBin(At(col + 2)))) { pre = At(col + 1); p = col + 2; }
        else if (first == '0' && dooct && IsDig(At(col + 1)))
        {
            var q = col + 1; var allOct = true;
            while (IsDig(At(q))) { if (At(q) > '7') allOct = false; q++; }
            if (allOct) { pre = '0'; p = col + 1; }
        }
        var end = p;
        if (pre is 'x' or 'X') while (IsHex(At(end))) end++;
        else if (pre is 'b' or 'B') while (IsBin(At(end))) end++;
        else if (pre == '0') while (At(end) is >= '0' and <= '7') end++;
        else while (IsDig(At(end))) end++;
        var digits = ptr.Substring(p, end - p);
        if (pre != '\0' && negative) { negative = false; startAt = col; }
        ulong value;
        try
        {
            value = pre switch
            {
                'x' or 'X' => ulong.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                'b' or 'B' => Convert.ToUInt64(digits, 2),
                '0' => Convert.ToUInt64(digits, 8),
                _ => ulong.Parse(digits, CultureInfo.InvariantCulture),
            };
        }
        catch (OverflowException) { value = ulong.MaxValue; }
        var subtract = delta < 0;
        if (negative) subtract = !subtract;
        var oldn = value;
        var amount = (ulong)Math.Abs((long)delta);
        if (subtract) value -= amount; else value += amount;
        var wasPositive = !negative;
        if (pre == '\0')
        {
            if (subtract) { if (value > oldn) { value = 1 + ~value; negative = !negative; } }
            else if (value < oldn) { value = ~value; negative = !negative; }
            if (value == 0) negative = false;
        }
        var length = end - col;
        if (pre != '\0') length = end - col;
        string body;
        if (pre is 'b' or 'B') body = Convert.ToString((long)value, 2);
        else if (pre == '0') body = Convert.ToString((long)value, 8);
        else if (pre is 'x' or 'X')
        {
            var upper = false;
            foreach (var ch in digits) if (char.IsAsciiLetter(ch)) upper = char.IsAsciiLetterUpper(ch);
            body = value.ToString(upper ? "X" : "x", CultureInfo.InvariantCulture);
        }
        else body = value.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        if (negative && (!visual || wasPositive)) sb.Append('-');
        var lead = 0;
        if (pre != '\0') { sb.Append('0'); lead++; }
        if (pre is 'b' or 'B' or 'x' or 'X') { sb.Append(pre); lead++; }
        var remaining = length - lead - body.Length;
        if (first == '0' && !(dooct && pre == '\0')) while (remaining-- > 0) sb.Append('0');
        sb.Append(body);
        startCol = startAt;
        endCol = end;
        newText = sb.ToString();
        if (visual && !wasPositive && !negative) { }
        return true;
    }

    private void OpAddSubtract(OpArgs oap, int delta)
    {
        if (!CanModify()) return;
        var progressive = _incrementProgressive;
        var step = 0;
        var amount = delta;
        for (var l = oap.Start.Line; l <= oap.End.Line; l++)
        {
            var s = L(l);
            int from, to;
            if (oap.BlockMode) { var bd = BlockPrep(oap, l, false); from = bd.TextCol; to = bd.TextCol + bd.TextLen; }
            else if (oap.Type == MType.Line) { from = 0; to = s.Length; }
            else
            {
                from = l == oap.Start.Line ? oap.Start.Character : 0;
                to = l == oap.End.Line ? Math.Min(s.Length, oap.End.Character + (oap.Inclusive ? 1 : 0)) : s.Length;
            }
            if (from >= s.Length) continue;
            // find the first number inside [from, to)
            var sub = s.Substring(0, Math.Min(to, s.Length));
            var idx = from;
            while (idx < sub.Length && !(sub[idx] is >= '0' and <= '9')) idx++;
            if (idx >= sub.Length) continue;
            var negPrefix = idx > from && sub[idx - 1] == '-';
            var useDelta = progressive ? delta * (step + 1) : delta;
            if (!ComputeIncrement(sub, idx, useDelta, true, out var sc, out var ec, out var nt, out _)) continue;
            // in Visual mode a leading '-' inside the selection counts as part of the number
            if (negPrefix)
            {
                // re-run with the sign: treat the number as negative
                var signed = ParseSignedDecimal(sub, idx - 1, out var sEnd);
                if (signed is { } sv)
                {
                    var nv = sv + useDelta;
                    SetLine(l, s.Substring(0, idx - 1) + nv.ToString(CultureInfo.InvariantCulture) + s.Substring(sEnd), idx - 1);
                    step++;
                    continue;
                }
            }
            SetLine(l, s.Substring(0, sc) + nt + s.Substring(ec), sc);
            step++;
        }
        _line = oap.Start.Line;
        _col = oap.Start.Character;
        if (oap.Type == MType.Line) _col = 0;
        CheckCursor();
        _bs.OpStart = oap.Start;
        _bs.OpEnd = oap.End;
    }

    private static long? ParseSignedDecimal(string s, int at, out int end)
    {
        end = at + 1;
        while (end < s.Length && s[end] is >= '0' and <= '9') end++;
        return long.TryParse(s.AsSpan(at, end - at), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
