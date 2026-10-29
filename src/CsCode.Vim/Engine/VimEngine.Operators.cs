using System.Text;
using CsCode.Core.Text;

namespace CsCode.Vim;

public sealed partial class VimEngine
{
    // ------------------------------------------------------------------------------------------------------------
    // operator + motion execution
    // ------------------------------------------------------------------------------------------------------------

    private void ExecOperatorMotion(Cmd c)
    {
        var oap = _oap;
        oap.ResetForMotion(c.Op, c.Reg);
        oap.Start = new TextPosition(_line, _col);
        var startCursor = oap.Start;
        oap.CursorStart = startCursor;
        var count0 = EffCount(c, c.M);
        bool ok;
        if (c.Doubled)
        {
            oap.Type = MType.Line;
            var count1 = count0 > 0 ? count0 : 1;
            ok = CursorVertical(count1 - 1, false) || count1 == 1;
            if (ok)
            {
                if ((c.Op == OpType.Delete && c.Force != 'v' && c.Force != 22) || c.Op is OpType.ShiftLeft or OpType.ShiftRight) Beginline(BL.Sol | BL.Fix);
                else if (c.Op != OpType.Yank) Beginline(BL.White | BL.Fix);
            }
        }
        else if (c.M.Key is 'a' or 'i') ok = RunTextObject(c.M, count0, oap);
        else ok = RunMotion(c.M, count0, oap);

        if (_searchPendingOp)
        {
            // the motion started a command line (d/foo<CR>): it will finish when the line is executed
            return;
        }
        if (!ok)
        {
            Beep();
            CheckCursor();
            return;
        }
        oap.Force = c.Force;
        DoPendingOperator(c, oap, count0);
    }

    /// <summary>Port of the core of do_pending_operator for the non-Visual case (the Visual case builds the same oap from the selection).</summary>
    private void DoPendingOperator(Cmd c, OpArgs oap, int count0)
    {
        var oldCol = _curswant;
        var motionForce = oap.Force;
        var cursor = new TextPosition(_line, _col);
        var start = oap.Start;

        if (motionForce != 0 && !oap.IsVisual)
        {
            if (motionForce == 'v')
            {
                if (oap.Type == MType.Line) oap.Inclusive = false;
                else if (oap.Type == MType.Char) oap.Inclusive = !oap.Inclusive;
                oap.Type = MType.Char;
            }
            else if (motionForce == 'V') oap.Type = MType.Line;
            else if (motionForce == 22)
            {
                // turn the motion into a Visual block selection
                oap.Type = MType.Block;
                oap.BlockMode = true;
                _visualStart = start;
                _bs.VisualMode = VimMode.VisualBlock;
                DoBlockOperator(c, oap, start, cursor, count0);
                return;
            }
        }

        if (!oap.IsVisual)
        {
            if (start < cursor) { oap.End = cursor; _line = start.Line; _col = start.Character; oap.Start = start; }
            else { oap.End = start; oap.Start = cursor; _line = cursor.Line; _col = cursor.Character; }
            oap.End = new TextPosition(Math.Min(oap.End.Line, LastLine), oap.End.Character);
            oap.LineCount = oap.End.Line - oap.Start.Line + 1;
            if (oap.Type != MType.Line && oap.End.Character > L(oap.End.Line).Length) oap.End = new TextPosition(oap.End.Line, L(oap.End.Line).Length);
            // if the end is on the NUL (empty line) an inclusive motion becomes exclusive
            if (oap.Force == 0 || oap.Type == MType.Line) { }
        }

        oap.Empty = oap.Type == MType.Char &&
            (!oap.Inclusive || (oap.Op == OpType.Yank && CharAt(oap.End.Line, oap.End.Character) == 0)) &&
            oap.Start == oap.End;

        // exclusive motion ending in column 0 of a later line: end at the previous line's end (":help exclusive-linewise")
        if (oap.Type == MType.Char && !oap.Inclusive && !oap.NoAdjEnd && oap.End.Character == 0 && (!oap.IsVisual || O.SelectionExclusive) && !oap.BlockMode && oap.LineCount > 1)
        {
            oap.EndAdjusted = true;
            oap.LineCount--;
            var endLine = oap.End.Line - 1;
            oap.End = new TextPosition(endLine, 0);
            if (InIndentAt(oap.Start))
            {
                oap.Type = MType.Line;
            }
            else
            {
                var len = L(endLine).Length;
                var ec = len;
                if (len > 0) { ec = VimText.PrevCharStart(L(endLine), len); oap.Inclusive = true; }
                oap.End = new TextPosition(endLine, ec);
            }
        }

        if (c.Op != OpType.None && !oap.IsVisual) RecordRedoForCommand(c, oap, count0);
        ApplyOperator(c, oap, count0, oldCol);
    }

    private bool InIndentAt(TextPosition p)
    {
        var s = L(p.Line);
        var ws = VimText.FirstNonBlank(s);
        return ws >= p.Character;
    }

    private void ApplyOperator(Cmd c, OpArgs oap, int count0, int oldCol)
    {
        var count1 = count0 > 0 ? count0 : 1;
        var op = oap.Op;
        switch (op)
        {
            case OpType.Delete:
                if (oap.Empty) { SaveUndo(_line, 1, 1); CheckCursor(); break; }
                OpDelete(oap);
                CheckCursor();
                if (oap.Type == MType.Char) _setCurswant = true;
                break;
            case OpType.Yank:
                OpYank(oap, deleting: false);
                _line = oap.Start.Line;
                _col = oap.Start.Character;
                CheckCursor();
                _setCurswant = true;
                break;
            case OpType.Change:
                OpChange(oap);
                break;
            case OpType.ShiftLeft or OpType.ShiftRight:
                OpShift(oap, true, oap.IsVisual ? count1 : 1);
                if (!O.StartOfLine && oap.Type == MType.Line && !oap.EndAdjusted) Coladvance(oldCol);
                break;
            case OpType.Tilde or OpType.Upper or OpType.Lower or OpType.Rot13:
                OpTilde(oap);
                CheckCursor();
                _setCurswant = true;
                break;
            case OpType.Indent:
                OpReindent(oap);
                break;
            case OpType.Format or OpType.FormatKeep:
                OpFormat(oap, keepCursor: op == OpType.FormatKeep);
                break;
            case OpType.Fold:
                Host.FoldCommand(VimFoldCommand.Create, oap.Start.Line, oap.End.Line);
                break;
            case OpType.Join or OpType.JoinNoSpace:
                if (oap.LineCount < 2) oap.LineCount = 2;
                if (_line + oap.LineCount - 1 > LastLine) Beep();
                else DoJoin(oap.LineCount, op == OpType.Join, setmark: true);
                break;
            case OpType.Replace:
                OpReplaceChars(oap, c.NChar2 != 0 ? c.NChar2 : c.NChar);
                break;
            case OpType.Insert or OpType.Append:
                OpBlockInsert(oap, op == OpType.Append);
                break;
            case OpType.NrAdd or OpType.NrSub:
                OpAddSubtract(oap, op == OpType.NrAdd ? count1 : -count1);
                break;
            case OpType.Comment:
                CommentOperator(oap, c.Key == 'g' && c.Key2 == 'C');
                break;
            case OpType.SurroundAdd:
                SurroundAddOperator(c, oap);
                break;
            case OpType.Filter or OpType.Function:
                Error("E319: This operator is not supported in this editor");
                break;
        }
        if (oap.IsVisual && op != OpType.None && op is not (OpType.Change or OpType.Insert or OpType.Append)) { }
    }

    // ------------------------------------------------------------------------------------------------------------
    // yank / delete
    // ------------------------------------------------------------------------------------------------------------

    private bool IsClipboardReg(int reg) => reg is '+' or '*' || (reg == 0 && (_s.UseSystemClipboard || O.UnnamedClip || O.UnnamedPlusClip));

    private string[] CollectLines(OpArgs oap, out VimRegisterType type, out int width)
    {
        width = 0;
        type = VimRegisterType.Character;
        var lines = oap.LineCount;
        var startLine = oap.Start.Line;
        if (oap.BlockMode)
        {
            type = VimRegisterType.Block;
            var arr = new string[lines];
            width = oap.EndVcol - oap.StartVcol + (oap.IsDollar ? 0 : 1);
            if (oap.IsDollar) width = 0;
            var maxw = 0;
            for (var i = 0; i < lines; i++)
            {
                var bd = BlockPrep(oap, startLine + i, isDel: false);
                arr[i] = bd.YankText(L(startLine + i));
                maxw = Math.Max(maxw, VimText.DisplayWidth(arr[i], O.TabStop));
            }
            if (oap.IsDollar) width = maxw - 1 >= 0 ? maxw : 0;
            else width = oap.EndVcol - oap.StartVcol + 1;
            width = Math.Max(0, oap.IsDollar ? maxw : width);
            width = oap.IsDollar ? maxw : oap.EndVcol - oap.StartVcol + 1;
            return arr;
        }
        if (oap.Type == MType.Line)
        {
            type = VimRegisterType.Line;
            var arr = new string[lines];
            for (var i = 0; i < lines; i++) arr[i] = L(startLine + i);
            return arr;
        }
        // charwise
        var res = new string[lines];
        for (var i = 0; i < lines; i++)
        {
            var l = startLine + i;
            var s = L(l);
            var sc = l == oap.Start.Line ? oap.Start.Character : 0;
            var ec = l == oap.End.Line ? oap.End.Character : int.MaxValue;
            if (ec == int.MaxValue) ec = s.Length;
            var len = sc > ec ? 0 : ec - sc + (oap.Inclusive && l == oap.End.Line ? (ec < s.Length ? VimText.CharLen(s, ec) : 0) : 0);
            len = Math.Min(len, s.Length - Math.Min(sc, s.Length));
            res[i] = len <= 0 ? "" : len == 1 ? CharStrings.Get(s[Math.Min(sc, s.Length - 1)]) : s.Substring(Math.Min(sc, s.Length), len);
        }
        return res;
    }

    /// <summary>Port of op_yank: stores the operated text in <paramref name="target"/> (or the register chosen by oap.Reg when null).</summary>
    private bool OpYank(OpArgs oap, bool deleting, Register? target = null, bool append = false, int? regForClipboard = null)
    {
        if (oap.Reg == '_' && !deleting) return true;
        if (!deleting)
        {
            var reg = oap.Reg;
            if (reg == 0 && _s.UseSystemClipboard) reg = '*';
            if (reg != 0 && (!RegisterFile.IsValidName(reg) || reg is '.' or ':' or '%' or '#' or '=')) { Beep(); return false; }
            target = _s.Registers.ForWrite(reg, out append);
            if (target is null) return true;
            regForClipboard = reg;
        }
        var lines = CollectLines(oap, out var type, out var width);
        var yankLines = oap.LineCount;
        // charwise yank that starts and ends in column 0 and is exclusive is linewise
        if (!oap.BlockMode && oap.Type == MType.Char && oap.Start.Character == 0 && !oap.Inclusive && (!oap.IsVisual || O.SelectionExclusive) && oap.End.Character == 0 && yankLines > 1)
        {
            type = VimRegisterType.Line;
            lines = lines[..^1];
        }
        var total = 0;
        foreach (var l in lines) total += l.Length + 1;
        if (total > O.MaxRegisterChars)
        {
            Error("E-register: text too large for a register (see maxRegisterSize)");
            return false;
        }
        RegisterFile.Store(target!, lines, type, width, append);
        if (regForClipboard is { } named && named < 128 && char.IsAsciiLetter((char)named)) NotifyNamedRegisterChange();
        var start = oap.Start;
        var end = oap.End;
        if (!deleting)
        {
            _bs.OpStart = start;
            var yEnd = end;
            if (!oap.Inclusive && !oap.BlockMode && type == VimRegisterType.Character) { var yl = end.Line; var yc = end.Character; Dec(ref yl, ref yc); yEnd = new TextPosition(yl, yc); }
            _bs.OpEnd = type == VimRegisterType.Line ? new TextPosition(end.Line, 1 << 30) : yEnd;
            if (type == VimRegisterType.Line) _bs.OpStart = new TextPosition(start.Line, 0);
            if (_s.Plugins.HighlightedYank) Host.OnYankHighlight(start, end, type == VimRegisterType.Line);
        }
        if (regForClipboard is { } rc && IsClipboardReg(rc))
            SyncClipboardFrom(target!);
        var n = oap.LineCount;
        if (!deleting && n > O.Report) Message($"{n} lines yanked");
        return true;
    }

    private void SyncClipboardFrom(Register r) => Host.WriteClipboard(r.ToText(), r.Type);

    private void YankForDelete(OpArgs oap)
    {
        if (oap.Reg == '_') return;
        var reg = oap.Reg == 0 && _s.UseSystemClipboard ? '*' : oap.Reg;
        var appended = false;
        if (reg != 0)
        {
            if (!RegisterFile.IsValidName(reg) || reg is '.' or ':' or '%' or '#' or '=') { Beep(); return; }
            var t = _s.Registers.ForWrite(reg, out var append);
            appended = append;
            if (t is not null) OpYank(oap, deleting: true, t, append, reg);
        }
        if (oap.Type == MType.Line || oap.LineCount > 1 || oap.UseRegOne)
        {
            var one = _s.Registers.ShiftDeleteRegisters(keepPrevious: appended);
            OpYank(oap, deleting: true, one, false, null);
        }
        if ((reg == 0 || (O.UnnamedClip && reg == '*') || (O.UnnamedPlusClip && reg == '+')) && oap.Type != MType.Line && oap.LineCount == 1)
        {
            var small = _s.Registers.SmallDelete();
            OpYank(oap, deleting: true, small, false, null);
        }
        if (IsClipboardReg(reg) || (_s.UseSystemClipboard && reg == '*'))
        {
            var cur = _s.Registers.ForRead(0);
            if (cur is not null) SyncClipboardFrom(cur);
        }
    }

    private bool OpDelete(OpArgs oap)
    {
        // nothing to delete in an empty buffer: registers stay untouched
        if (LineCount == 1 && L(0).Length == 0) return true;
        if (!CanModify()) return false;
        if (oap.Type == MType.Char && !oap.IsVisual && !oap.BlockMode && oap.LineCount > 1 && oap.Force == 0 && oap.Op == OpType.Delete)
        {
            var endLine = L(oap.End.Line);
            var p = oap.End.Character;
            if (p < endLine.Length) p += oap.Inclusive ? VimText.CharLen(endLine, p) : 0;
            while (p < endLine.Length && endLine[p] is ' ' or '\t') p++;
            if (p >= endLine.Length && InIndentAt(oap.Start)) oap.Type = MType.Line;
        }
        if (oap.Type == MType.Char && oap.LineCount == 1 && oap.Op == OpType.Delete && L(oap.Start.Line).Length == 0)
        {
            SaveUndo(oap.Start.Line, 1, 1);
            return true;
        }
        YankForDelete(oap);

        var start = oap.Start;
        if (oap.BlockMode)
        {
            DeleteBlock(oap);
            _bs.OpEnd = new TextPosition(oap.End.Line, oap.Start.Character);
            _bs.OpStart = oap.Start;
            return true;
        }
        if (oap.Type == MType.Line)
        {
            if (oap.Op == OpType.Change)
            {
                if (oap.LineCount > 1) DeleteLines(start.Line + 1, oap.LineCount - 1);
                _line = start.Line;
                SaveUndo(_line, 1, 1);
                if (O.AutoIndent)
                {
                    Beginline(BL.White);
                    _didAutoIndent = true;
                    _aiCol = _col;
                }
                else Beginline(0);
                TruncateLine(false);
                if (oap.LineCount > 1) { _bs.ULineNr = -1; _bs.ULineText = null; }
            }
            else
            {
                _line = start.Line;
                DeleteLines(start.Line, oap.LineCount);
                _line = Math.Min(start.Line, LastLine);
                Beginline(BL.White | BL.Fix);
                _bs.ULineNr = -1;
                _bs.ULineText = null;
            }
        }
        else
        {
            var endExcl = oap.Inclusive ? new TextPosition(oap.End.Line, oap.End.Character + (oap.End.Character < L(oap.End.Line).Length ? VimText.CharLen(L(oap.End.Line), oap.End.Character) : 0)) : oap.End;
            if (oap.LineCount == 1)
            {
                var s = L(start.Line);
                var n = Math.Min(endExcl.Character, s.Length) - start.Character;
                _line = start.Line;
                _col = start.Character;
                if (n > 0) DeleteInLine(start.Line, start.Character, n);
                else SaveUndo(start.Line, 1, 1);
                FixCursorAfterDelBytes();
            }
            else
            {
                ReplaceRange(start, endExcl, "");
                _line = start.Line;
                _col = start.Character;
                CheckCursor();
            }
        }
        _bs.OpEnd = start;
        _bs.OpStart = start;
        return true;
    }

    /// <summary>Vim's del_bytes cursor fix-up: after removing text up to the end of the line the cursor steps back onto the last character.</summary>
    private void FixCursorAfterDelBytes()
    {
        var s = CurLine;
        if (_col >= s.Length && _col > 0 && !_restartEdit && !O.VirtualOneMore) _col = VimText.PrevCharStart(s, s.Length);
    }

    private void TruncateLine(bool fixpos)
    {
        var s = CurLine;
        if (_col < s.Length) DeleteInLine(_line, _col, s.Length - _col);
        if (fixpos && _col > 0) _col = VimText.PrevCharStart(CurLine, Math.Min(_col, CurLine.Length));
    }

    private bool _didAutoIndent;
    private int _aiCol;

    private void OpChange(OpArgs oap)
    {
        var l = oap.Start.Character;
        if (oap.Type == MType.Line) l = 0;
        if (!OpDelete(oap)) return;
        if (l > _col && !LineEmpty(_line)) _col += VimText.CharLen(CurLine, _col);
        if (oap.BlockMode)
        {
            OpBlockChange(oap);
            return;
        }
        StartInsert('c', 1, restart: false);
    }

    // ------------------------------------------------------------------------------------------------------------
    // shift / case / indent / format
    // ------------------------------------------------------------------------------------------------------------

    private void ShiftLine(int line, bool left, bool round, int amount)
    {
        var s = L(line);
        var sw = O.Sw;
        var count = VimText.IndentWidth(s, O.TabStop);
        if (round)
        {
            var i = count / sw;
            var j = count % sw;
            if (j != 0 && left) amount--;
            if (left) { i -= amount; if (i < 0) i = 0; }
            else i += amount;
            count = i * sw;
        }
        else if (left) { count -= sw * amount; if (count < 0) count = 0; }
        else count += sw * amount;
        SetIndent(line, count);
    }

    private void SetIndent(int line, int width)
    {
        var s = L(line);
        var fnb = VimText.FirstNonBlank(s);
        var indent = VimText.MakeIndent(width, O.TabStop, O.ExpandTab);
        if (string.CompareOrdinal(s, 0, indent, 0, Math.Max(fnb, indent.Length)) == 0 && fnb == indent.Length) return;
        SetLine(line, string.Concat(indent, s.AsSpan(fnb)), 0);
    }

    private void OpShift(OpArgs oap, bool cursTop, int amount)
    {
        var first = oap.Start.Line;
        var last = oap.End.Line;
        SaveUndoRange(first, last - first + 1);
        var blockCol = _col;
        for (var l = first; l <= last; l++)
        {
            var s = L(l);
            if (s.Length == 0) continue;
            if (oap.BlockMode) ShiftBlockLine(oap, l, amount);
            else ShiftLine(l, oap.Op == OpType.ShiftLeft, O.ShiftRound, amount);
        }
        if (oap.BlockMode) { _line = first; _col = oap.Start.Character == 0 ? blockCol : blockCol; }
        else if (cursTop) { _line = first; Beginline(BL.Sol | BL.Fix); }
        else _line = last;
        var n = last - first + 1;
        if (n > O.Report) Message($"{n} lines {(oap.Op == OpType.ShiftLeft ? "<" : ">")}ed {amount} time{(amount == 1 ? "" : "s")}");
        NoteChange(first, 0);
        var endLen = L(last).Length;
        _bs.OpStart = new TextPosition(first, oap.Start.Character);
        _bs.OpStart = oap.Start;
        _bs.OpEnd = new TextPosition(last, endLen > 0 ? endLen - 1 : 0);
    }

    /// <summary>Pre-saves a line range so later per-line SetLine calls merge into one undo entry.</summary>
    private void SaveUndoRange(int first, int count)
    {
        // SetLine saves per line; a pre-save only matters for the Vim undo cursor rule, so we simply make sure a header exists.
        EnsureUndoHeader();
        _ = first; _ = count;
    }

    private static string SwapChars(OpType op, string s, int from, int to)
    {
        StringBuilder? sb = null;
        for (var i = from; i < to && i < s.Length;)
        {
            var len = VimText.CharLen(s, i);
            var ch = s[i];
            if (len == 1 || char.IsHighSurrogate(ch) == false)
            {
                var n = op switch
                {
                    OpType.Upper => ch == '\u00df' ? '\u1e9e' : char.ToUpperInvariant(ch),
                    OpType.Lower => ch == '\u1e9e' ? '\u00df' : char.ToLowerInvariant(ch),
                    OpType.Rot13 => Rot13(ch),
                    _ => char.IsUpper(ch) ? char.ToLowerInvariant(ch) : char.IsLower(ch) ? char.ToUpperInvariant(ch) : ch,
                };
                if (n != ch) { sb ??= new StringBuilder(s); sb[i] = n; }
            }
            i += len;
        }
        return sb?.ToString() ?? s;
    }

    private static char Rot13(char c) => c switch
    {
        >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
        >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
        _ => c,
    };

    private void OpTilde(OpArgs oap)
    {
        var first = oap.Start.Line;
        var last = oap.End.Line;
        var anyChange = false;
        if (oap.BlockMode)
        {
            for (var l = first; l <= last; l++)
            {
                var bd = BlockPrep(oap, l, isDel: false);
                var s = L(l);
                var n = SwapChars(oap.Op, s, bd.TextCol, bd.TextCol + bd.TextLen);
                if (!ReferenceEquals(n, s) && n != s) { SetLine(l, n); anyChange = true; }
            }
        }
        else
        {
            var start = oap.Start;
            var end = oap.End;
            if (oap.Type == MType.Line)
            {
                start = new TextPosition(start.Line, 0);
                var len = L(end.Line).Length;
                end = new TextPosition(end.Line, len > 0 ? len - 1 : 0);
            }
            else if (!oap.Inclusive)
            {
                var el = end.Line; var ec = end.Character;
                Dec(ref el, ref ec);
                end = new TextPosition(el, ec);
            }
            for (var l = start.Line; l <= end.Line; l++)
            {
                var s = L(l);
                var sc = l == start.Line ? start.Character : 0;
                var ec = l == end.Line ? end.Character + (end.Character < s.Length ? VimText.CharLen(s, end.Character) : 0) : s.Length;
                var n = SwapChars(oap.Op, s, sc, ec);
                if (n != s) { SetLine(l, n, sc); anyChange = true; }
            }
        }
        if (anyChange) NoteChange(oap.Start.Line, oap.Type == MType.Line ? 0 : oap.Start.Character);
        if (oap.BlockMode) { _bs.OpStart = oap.Start; _bs.OpEnd = oap.End; }
        else if (oap.Type == MType.Line) { _bs.OpStart = new TextPosition(oap.Start.Line, 0); _bs.OpEnd = new TextPosition(oap.End.Line, Math.Max(0, L(oap.End.Line).Length - 1)); }
        else
        {
            var eEnd = oap.End;
            if (!oap.Inclusive) { var el2 = eEnd.Line; var ec2 = eEnd.Character; Dec(ref el2, ref ec2); eEnd = new TextPosition(el2, ec2); }
            _bs.OpStart = oap.Start;
            _bs.OpEnd = eEnd;
        }
        if (oap.LineCount > O.Report) Message($"{oap.LineCount} lines changed");
    }

    private void OpReindent(OpArgs oap)
    {
        var first = oap.Start.Line;
        var last = oap.End.Line;
        if (Host.FormatLines(first, last)) { }
        else
        {
            // No host formatter: copy the indent of the previous non-blank line (autoindent-style), keeping the relative first line.
            var prevIndent = first > 0 ? PrevNonBlankIndent(first) : 0;
            for (var l = first; l <= last; l++)
            {
                var s = L(l);
                if (VimText.IsBlank(s)) { if (s.Length > 0) SetLine(l, ""); continue; }
                if (l > first || first > 0) SetIndent(l, l == first ? prevIndent : PrevNonBlankIndent(l));
            }
        }
        _line = first;
        Beginline(BL.Sol | BL.Fix);
        var n = last - first + 1;
        if (n > O.Report) Message($"{n} lines indented ");
        _bs.OpStart = new TextPosition(first, 0);
        _bs.OpEnd = new TextPosition(last, 0);
    }

    private int PrevNonBlankIndent(int line)
    {
        for (var l = line - 1; l >= 0; l--)
            if (!VimText.IsBlank(L(l))) return VimText.IndentWidth(L(l), O.TabStop);
        return 0;
    }

    private void OpFormat(OpArgs oap, bool keepCursor)
    {
        // gw keeps the cursor where it was before the operator (moved along with the text it sits in)
        var saved = oap.CursorStart;
        var first = oap.Start.Line;
        var last = oap.End.Line;
        var tw = O.TextWidth > 0 ? O.TextWidth : 79;
        var l = first;
        var lastFormatted = first;
        var offset = 0;
        while (l <= last + offset)
        {
            if (VimText.IsBlank(L(l))) { l++; continue; }
            var pStart = l;
            var pEnd = l;
            while (pEnd + 1 <= last + offset && !VimText.IsBlank(L(pEnd + 1))) pEnd++;
            var indentStr = L(pStart).Substring(0, VimText.FirstNonBlank(L(pStart)));
            var contIndent = O.AutoIndent ? indentStr : "";
            var words = new List<string>();
            var savedWord = -1;
            var savedInWord = 0;
            for (var i = pStart; i <= pEnd; i++)
            {
                var text = L(i);
                var p = 0;
                while (p < text.Length)
                {
                    while (p < text.Length && text[p] is ' ' or '\t') p++;
                    if (p >= text.Length) break;
                    var q = p;
                    while (q < text.Length && text[q] is not (' ' or '\t')) q++;
                    if (keepCursor && savedWord < 0 && saved.Line == i && saved.Character < q)
                    {
                        // the cursor is in this word, or in the blanks before it (then it goes to the start of the word)
                        savedWord = words.Count;
                        savedInWord = Math.Max(0, saved.Character - p);
                    }
                    words.Add(text.Substring(p, q - p));
                    p = q;
                }
            }
            var outLines = new List<string>();
            var wordPos = new List<(int Line, int Col)>();
            var cur = new StringBuilder(indentStr);
            var curHasWord = false;
            var curWidth = VimText.DisplayWidth(indentStr, O.TabStop);
            foreach (var w in words)
            {
                var ww = VimText.DisplayWidth(w, O.TabStop);
                if (curHasWord && curWidth + 1 + ww > tw)
                {
                    outLines.Add(cur.ToString());
                    cur.Clear().Append(contIndent);
                    curWidth = VimText.DisplayWidth(contIndent, O.TabStop);
                    curHasWord = false;
                }
                if (curHasWord) { cur.Append(' '); curWidth++; }
                wordPos.Add((outLines.Count, cur.Length));
                cur.Append(w);
                curWidth += ww;
                curHasWord = true;
            }
            outLines.Add(cur.ToString());
            var oldCount = pEnd - pStart + 1;
            var same = oldCount == outLines.Count;
            if (same) for (var i = 0; i < oldCount; i++) if (L(pStart + i) != outLines[i]) { same = false; break; }
            if (!same)
            {
                ReplaceRange(new TextPosition(pStart, 0), new TextPosition(pEnd, L(pEnd).Length), string.Join('\n', outLines));
                offset += outLines.Count - oldCount;
            }
            if (keepCursor)
            {
                if (savedWord >= 0) saved = new TextPosition(pStart + wordPos[savedWord].Line, wordPos[savedWord].Col + savedInWord);
                else if (saved.Line > pEnd) saved = new TextPosition(saved.Line + outLines.Count - oldCount, saved.Character);
            }
            lastFormatted = pStart + outLines.Count - 1;
            l = pStart + outLines.Count;
        }
        if (keepCursor)
        {
            _line = Math.Min(saved.Line, LastLine);
            _col = saved.Character;
            CheckCursor();
            _setCurswant = true;
        }
        else
        {
            _line = Math.Min(oap.EndAdjusted && lastFormatted < LastLine ? lastFormatted + 1 : lastFormatted, LastLine);
            Beginline(BL.White | BL.Fix);
        }
        // '[ keeps the column the operator was typed at when the formatted area starts on that line (nvim: gww)
        _bs.OpStart = !oap.IsVisual && oap.CursorStart.Line == first ? new TextPosition(first, oap.CursorStart.Character) : oap.Start;
        _bs.OpEnd = new TextPosition(Math.Min(lastFormatted, LastLine), 0);
    }

    // ------------------------------------------------------------------------------------------------------------
    // join
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Port of do_join: joins <paramref name="count"/> lines starting at the cursor line.</summary>
    private bool DoJoin(int count, bool insertSpace, bool setmark)
    {
        if (_line + count - 1 > LastLine || count < 2) return false;
        var first = _line;
        var sb = new StringBuilder();
        var spaces = new int[count];
        var sum = 0;
        var endcurr1 = '\0';
        var endcurr2 = '\0';
        var currsize = 0;
        string curr = "";
        var parts = new string[count];
        for (var t = 0; t < count; t++)
        {
            curr = L(first + t);
            if (t == 0 && setmark) _bs.OpStart = new TextPosition(first, curr.Length);
            if (insertSpace && t > 0)
            {
                curr = curr.TrimStart(' ', '\t');
                if (curr.Length > 0 && curr[0] != ')' && sum != 0 && endcurr1 != '\t')
                {
                    if (endcurr1 == ' ') endcurr1 = endcurr2; else spaces[t]++;
                    if (O.JoinSpaces && (endcurr1 == '.' || (!O.CpoHas('j') && endcurr1 is '?' or '!'))) spaces[t]++;
                }
            }
            parts[t] = curr;
            currsize = curr.Length;
            sum += currsize + spaces[t];
            endcurr1 = endcurr2 = '\0';
            if (insertSpace && currsize > 0)
            {
                endcurr1 = curr[^1];
                if (currsize > 1) endcurr2 = curr[^2];
            }
        }
        var col = sum - currsize - spaces[count - 1];
        for (var t = 0; t < count; t++)
        {
            if (spaces[t] > 0) sb.Append(' ', spaces[t]);
            sb.Append(parts[t]);
        }
        var newText = sb.ToString();
        var lastLen = L(first + count - 1).Length;
        {
            var offset = 0;
            for (var t = 0; t < count; t++)
            {
                offset += spaces[t];
                if (t > 0)
                {
                    var removed = L(first + t).Length - L(first + t).TrimStart(' ', '\t').Length;
                    if (!insertSpace) removed = 0;
                    _bs.MarkColAdjust(first + t, 0, -t, offset - removed);
                }
                offset += parts[t].Length;
            }
        }
        ReplaceRange(new TextPosition(first, 0), new TextPosition(first + count - 1, lastLen), newText);
        NoteChange(first + 1, 0);
        _bs.OpEnd = new TextPosition(first, Math.Max(0, sum));
        _line = first;
        _col = O.CpoHas('q') ? currsize : col;
        CheckCursor();
        _setCurswant = true;
        return true;
    }

    // ------------------------------------------------------------------------------------------------------------
    // put
    // ------------------------------------------------------------------------------------------------------------

    [Flags]
    private enum PutFlags { None = 0, CursEnd = 1, Line = 2, LineSplit = 4, LineForward = 8, CursLine = 16, FixIndent = 32 }

    private bool DoPut(int regname, bool forward, int count, PutFlags flags, Register? fake = null)
    {
        // '[ and '] default to the cursor, even when the put fails (Vim sets them before looking at the register)
        _bs.OpStart = new TextPosition(_line, _col);
        _bs.OpEnd = new TextPosition(_line, _col);
        Register? reg = fake;
        if (reg is null)
        {
            var name = regname;
            if (name == 0 && _s.UseSystemClipboard) name = '*';
            if (name == '.')
            {
                // the last inserted text is put as typed
                var text = Keys.Decode(_s.Registers.LastInserted ?? "");
                reg = Register.FromText(new string(text.Where(k => k is >= 32 or '\t' or '\n').Select(k => (char)(k & 0xFFFF)).ToArray()), VimRegisterType.Character);
            }
            else reg = _s.Registers.ForRead(name, () => Host.ReadClipboard());
        }
        if (reg is null || reg.IsEmpty)
        {
            Error(regname switch
            {
                '.' => "E29: No inserted text yet",
                ':' => "E30: No previous command line",
                '/' => "E35: No previous regular expression",
                '%' => "E32: No file name",
                '#' => "E23: No alternate file",
                _ => $"E353: Nothing in register {(regname == 0 ? "\"" : Keys.SafeString(regname))}",
            });
            return false;
        }
        if (!CanModify()) return false;
        if (regname == '.' && fake is null)
        {
            // the inserted text is "put" by typing it: a (or i) + text + Esc
            var typed = Keys.Decode(_s.Registers.LastInserted ?? "");
            var stuffKeys = new List<int> { forward ? 'a' : 'i' };
            for (var i = 0; i < Math.Max(1, count); i++) stuffKeys.AddRange(typed);
            stuffKeys.Add(Keys.Esc);
            if ((flags & PutFlags.CursEnd) != 0) stuffKeys.Add('l');
            _stuff.PushFront(stuffKeys.ToArray());
            return true;
        }
        var type = reg.Type;
        var yarr = reg.Lines;
        var ysize = yarr.Length;
        if ((flags & PutFlags.Line) != 0) type = VimRegisterType.Line;
        // putting an empty one-line charwise register inserts nothing and leaves the cursor alone
        if (type == VimRegisterType.Character && ysize == 1 && yarr[0].Length == 0) return true;
        var dir = forward;
        var nrLines = 0;

        if (type == VimRegisterType.Line)
        {
            if ((flags & PutFlags.LineSplit) != 0)
            {
                // "p" in Visual mode: split the line to put the text in between
                var s = CurLine;
                var p = _col;
                if (dir && p < s.Length) p += VimText.CharLen(s, p);
                var rest = s.Substring(Math.Min(p, s.Length));
                InsertLines(_line + 1, [rest]);
                SetLine(_line, s.Substring(0, Math.Min(p, s.Length)));
                nrLines++;
                dir = true;
            }
            if ((flags & PutFlags.LineForward) != 0)
            {
                _line = _bs.VisualEnd?.Line ?? _line;
                dir = true;
            }
        }

        if (type == VimRegisterType.Block)
        {
            PutBlock(reg, dir, count, flags);
            _setCurswant = true;
            return true;
        }

        if (type == VimRegisterType.Line)
        {
            var after = dir ? _line : _line - 1;   // append after this line index (-1 = above the first line)
            var first = after + 1;
            for (var cnt = 1; cnt <= count; cnt++)
            {
                InsertLines(after + 1 + (cnt - 1) * ysize, yarr);
                nrLines += ysize;
            }
            var lastLine = first + count * ysize - 1;
            if ((flags & PutFlags.FixIndent) != 0)
                for (var cnt = 0; cnt < count; cnt++) FixPutIndent(first + cnt * ysize, ysize);
            var lastText = L(lastLine);
            _bs.OpStart = new TextPosition(first, 0);
            _bs.OpEnd = new TextPosition(lastLine, lastText.Length > 1 ? VimText.PrevCharStart(lastText, lastText.Length) : 0);
            NoteChange(first, 0);
            if ((flags & PutFlags.CursLine) != 0)
            {
                _line = lastLine;
                Beginline(BL.White | BL.Fix);
            }
            else if ((flags & PutFlags.CursEnd) != 0)
            {
                _line = lastLine >= LastLine ? LastLine : lastLine + 1;
                _col = 0;
            }
            else
            {
                _line = first;
                _col = 0;
                Beginline(BL.White | BL.Fix);
            }
            if (nrLines > O.Report) Message($"{nrLines} more lines");
            _setCurswant = true;
            return true;
        }

        // ---- characterwise
        var col = _col;
        var lnum = _line;
        if (dir && CharAtCursor() != 0)
        {
            var bl = VimText.CharLen(CurLine, _col);
            col += bl;
        }
        col = Math.Min(col, CurLine.Length);
        var startPos = new TextPosition(lnum, col);
        if (ysize == 1)
        {
            var yanklen = yarr[0].Length;
            if (count > 0 && yanklen > 0)
            {
                var ins = count == 1 ? yarr[0] : VimText.Repeat(yarr[0], count);
                InsertInLine(lnum, col, ins);
                var lastCharStart = col + ins.Length - VimText.CharLen(ins, VimText.PrevCharStart(ins, ins.Length));
                _bs.OpStart = startPos;
                _bs.OpEnd = new TextPosition(lnum, lastCharStart);
                _line = lnum;
                _col = (flags & PutFlags.CursEnd) != 0 || _restartEdit ? col + ins.Length : lastCharStart;
            }
            else
            {
                _line = lnum;
                _col = col;
            }
        }
        else
        {
            for (var cnt = 1; cnt <= count; cnt++)
            {
                var s = L(lnum);
                var col1 = Math.Min(col, s.Length);
                var rest = s.Substring(col1);
                var lines = new List<string>(ysize) { s.Substring(0, col1) + yarr[0] };
                for (var i = 1; i < ysize - 1; i++) lines.Add(yarr[i]);
                lines.Add(yarr[ysize - 1] + rest);
                ReplaceRange(new TextPosition(lnum, 0), new TextPosition(lnum, s.Length), string.Join('\n', lines));
                nrLines += ysize - 1;
            }
            var newLnum = lnum + count * (ysize - 1);
            var len = yarr[^1].Length;
            _bs.OpStart = startPos;
            _bs.OpEnd = new TextPosition(newLnum, len > 1 ? len - 1 : 0);
            NoteChange(lnum, col);
            if ((flags & PutFlags.CursEnd) != 0)
            {
                _line = newLnum;
                _col = len;
                _bs.OpEnd = new TextPosition(newLnum, len > 1 ? len - 1 : len);
            }
            else { _line = lnum; _col = col; }
        }
        if (nrLines > O.Report) Message($"{nrLines} more lines");
        _setCurswant = true;
        var len2 = CurLine.Length;
        if (_col > len2) _col = len2;
        return true;
    }

    private void FixPutIndent(int firstLine, int count)
    {
        var refIndent = VimText.IndentWidth(L(_line), O.TabStop);
        var firstIndent = VimText.IndentWidth(L(firstLine), O.TabStop);
        var diff = refIndent - firstIndent;
        for (var i = 0; i < count; i++)
        {
            var l = firstLine + i;
            if (VimText.IsBlank(L(l))) continue;
            SetIndent(l, Math.Max(0, VimText.IndentWidth(L(l), O.TabStop) + diff));
        }
    }
}
