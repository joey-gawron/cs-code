using System.Text;
using CsCode.Core.Text;

namespace CsCode.Vim;

// Editing primitives. EVERY change to the buffer goes through here, so each one (1) records undo like Vim's u_save, (2) adjusts marks
// like mark_adjust, (3) updates '. and the change list, (4) forwards a minimal edit to the host. Line numbers are 0-based.
public sealed partial class VimEngine
{
    private string L(int l) => (uint)l < (uint)_buf.LineCount ? _buf.GetLine(l) : "";
    private int LineCount => _buf.LineCount;
    private int LastLine => _buf.LineCount - 1;
    private string CurLine => _buf.GetLine(_line);

    private bool CanModify()
    {
        if (!_buf.IsReadOnly) return true;
        Error("E21: Cannot make changes, 'modifiable' is off");
        return false;
    }

    // ------------------------------------------------------------------------------------------------------------
    // cursor helpers
    // ------------------------------------------------------------------------------------------------------------

    private bool VisualActive => _mode is VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock;

    /// <summary>Largest legal column for the cursor on <paramref name="line"/> in the current mode (one past the end where Vim allows it).</summary>
    private int MaxCol(int line)
    {
        var s = L(line);
        if (s.Length == 0) return 0;
        var allowEnd = _mode is VimMode.Insert or VimMode.Replace || _restartEdit || VisualActive || O.VirtualOneMore;
        return allowEnd ? s.Length : VimText.PrevCharStart(s, s.Length);
    }

    /// <summary>Vim's check_cursor: clamp line and column, never in the middle of a character.</summary>
    private void CheckCursor()
    {
        _line = Math.Clamp(_line, 0, _buf.LineCount - 1);
        var s = L(_line);
        var max = MaxCol(_line);
        if (_col > max) _col = max;
        if (_col < 0) _col = 0;
        if (_col < s.Length) _col = VimText.CharStartAt(s, _col);
    }

    private void SetCursor(int line, int col)
    {
        _line = line;
        _col = col;
    }

    /// <summary>Virtual column of the cursor: on a TAB in Normal mode the cursor sits on the tab's last cell (Vim's getvcol "cursor" column).</summary>
    private int CurVcol()
    {
        var s = CurLine;
        if (_col < s.Length && s[_col] == '\t' && !O.List && _mode is not (VimMode.Insert or VimMode.Replace)
            && !(VisualActive && new TextPosition(_line, _col) < _visualStart)) return VimText.VcolEndOf(s, _col, O.TabStop);
        return VimText.VcolOf(s, _col, O.TabStop);
    }

    private void UpdateCurswant()
    {
        if (!_setCurswant) return;
        _curswant = CurVcol();
        _setCurswant = false;
    }

    /// <summary>Vim's coladvance: put the cursor on the character covering virtual column <paramref name="vcol"/> (int.MaxValue = end of line).</summary>
    private void Coladvance(int vcol)
    {
        var s = CurLine;
        if (vcol == int.MaxValue) { _col = MaxCol(_line); if (_col < s.Length) _col = VimText.CharStartAt(s, _col); return; }
        var idx = VimText.IndexOfVcol(s, vcol, O.TabStop, lastIfBeyond: true);
        var max = MaxCol(_line);
        // In Insert/Visual-like modes a column beyond the end lands after the last character.
        if (max == s.Length && s.Length > 0 && VimText.DisplayWidth(s, O.TabStop) <= vcol) idx = s.Length;
        _col = Math.Min(idx, max);
        if (s.Length == 0) _col = 0;
    }

    private enum BL { Sol = 1, White = 2, Fix = 4 }

    /// <summary>Vim's beginline.</summary>
    private void Beginline(BL flags)
    {
        if ((flags & BL.Sol) != 0 && !O.StartOfLine)
        {
            Coladvance(_curswant);
        }
        else
        {
            var s = CurLine;
            _col = 0;
            if ((flags & (BL.White | BL.Sol)) != 0) _col = VimText.FirstNonBlank(s);
            _setCurswant = true;
        }
        if ((flags & BL.Fix) != 0 && _col >= CurLine.Length && _col > 0 && !(_mode is VimMode.Insert or VimMode.Replace)) _col = VimText.PrevCharStart(CurLine, CurLine.Length);
    }

    // ------------------------------------------------------------------------------------------------------------
    // undo bookkeeping (u_save)
    // ------------------------------------------------------------------------------------------------------------

    private static long Now() => DateTime.UtcNow.Ticks;

    private void EnsureUndoHeader()
    {
        var u = _bs.Undo;
        if (!u.Synced) return;
        var header = u.NewHeader(new TextPosition(_line, _col), _bs.Marks, Now(), Math.Max(1, O.UndoLevels));
        header.VisualStart = _bs.VisualStart; header.VisualEnd = _bs.VisualEnd; header.VisualMode = _bs.VisualMode; header.VisualCurswant = _bs.VisualCurswant;
        _bs.NewChange = true;
    }

    /// <summary>Remembers lines [first, first+oldCount) so they can be restored; <paramref name="newCount"/> lines will occupy the region afterwards.</summary>
    private void SaveUndo(int first, int oldCount, int newCount, bool saveULine = true)
    {
        if (O.UndoLevels < 0) return;
        EnsureUndoHeader();
        var h = _bs.Undo.Current;
        if (oldCount == 1 && newCount == 1)
        {
            // The same single line was already saved earlier in this step (no lines added/removed since): nothing more to remember.
            for (int i = h.Entries.Count - 1, seen = 0; i >= 0 && seen < 10; i--, seen++)
            {
                var e = h.Entries[i];
                if (e.Old.Length != e.NewCount) break;
                if (e.Start == first && e.Old.Length == 1) { if (saveULine) SaveULine(first); return; }
            }
        }
        var old = oldCount == 0 ? [] : new string[oldCount];
        var chars = 0;
        for (var i = 0; i < oldCount; i++) { old[i] = L(first + i); chars += old[i].Length + 16; }
        h.Entries.Add(new UndoEntry { Start = first, Old = old, NewCount = newCount });
        _bs.Undo.AddSaved(h, chars);
        if (saveULine && oldCount == 1) SaveULine(first);
    }

    private void SaveULine(int line)
    {
        if (_bs.ULineNr == line) return;
        _bs.ULineNr = line;
        _bs.ULineText = L(line);
        _bs.ULineCol = _line == line ? _col : 0;
    }

    private void NoteChange(int line, int col)
    {
        _bs.AddChange(new TextPosition(Math.Max(0, line), Math.Max(0, col)), O.TextWidth);
    }

    private void HostEdit(in VimEdit edit)
    {
        _buf.ApplyEdit(edit);
        _bs.KnownVersion = _buf.Version;
    }

    // ------------------------------------------------------------------------------------------------------------
    // primitives
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Replaces one line's text. The host receives only the differing middle part.</summary>
    private void SetLine(int l, string text, int changedCol = -1)
    {
        var old = L(l);
        if (ReferenceEquals(old, text) || old == text) return;
        SaveUndo(l, 1, 1);
        RawSetLine(l, old, text);
        NoteChange(l, changedCol >= 0 ? changedCol : CommonPrefix(old, text));
    }

    private void RawSetLine(int l, string old, string text)
    {
        var pre = CommonPrefix(old, text);
        var maxSuf = Math.Min(old.Length, text.Length) - pre;
        var suf = 0;
        while (suf < maxSuf && old[old.Length - 1 - suf] == text[text.Length - 1 - suf]) suf++;
        // never split a surrogate pair or combining sequence
        while (pre > 0 && pre < old.Length && pre < text.Length && (VimText.IsCombining(old[pre]) || char.IsLowSurrogate(old[pre]))) pre--;
        var insLen = text.Length - pre - suf;
        var ins = insLen == 0 ? "" : insLen == 1 ? CharStrings.Get(text[pre]) : text.Substring(pre, insLen);
        HostEdit(new VimEdit(new TextPosition(l, pre), new TextPosition(l, old.Length - suf), ins));
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    private void InsertInLine(int l, int col, string text, bool note = true)
    {
        if (text.Length == 0) return;
        SaveUndo(l, 1, 1);
        HostEdit(new VimEdit(new TextPosition(l, col), new TextPosition(l, col), text));
        if (note) NoteChange(l, col);
    }

    private void DeleteInLine(int l, int col, int len)
    {
        if (len <= 0) return;
        SaveUndo(l, 1, 1);
        HostEdit(new VimEdit(new TextPosition(l, col), new TextPosition(l, col + len), ""));
        NoteChange(l, col);
    }

    private void ReplaceInLine(int l, int col, int len, string text)
    {
        if (len <= 0) { InsertInLine(l, col, text); return; }
        SaveUndo(l, 1, 1);
        HostEdit(new VimEdit(new TextPosition(l, col), new TextPosition(l, col + len), text));
        NoteChange(l, col);
    }

    /// <summary>Inserts whole lines before line index <paramref name="at"/> (at == LineCount appends).</summary>
    private void InsertLines(int at, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        SaveUndo(at, 0, lines.Count, saveULine: false);
        var text = lines.Count == 1 ? lines[0] : string.Join('\n', lines);
        if (at < LineCount) HostEdit(new VimEdit(new TextPosition(at, 0), new TextPosition(at, 0), text + "\n"));
        else HostEdit(new VimEdit(new TextPosition(LastLine, L(LastLine).Length), new TextPosition(LastLine, L(LastLine).Length), "\n" + text));
        _bs.MarkAdjust(at, int.MaxValue / 2, lines.Count, 0, delete: false);
        NoteChange(at, 0);
    }

    /// <summary>Deletes whole lines; the buffer always keeps one (empty) line.</summary>
    private void DeleteLines(int first, int count)
    {
        count = Math.Min(count, LineCount - first);
        if (count <= 0) return;
        var all = count >= LineCount;
        SaveUndo(first, count, all ? 1 : 0, saveULine: false);
        RawDeleteLines(first, count);
        if (all)
        {
            // Everything deleted: the buffer is one empty line (which the undo entry above treats as "1 new line").
            _bs.MarkAdjust(0, int.MaxValue / 2, 0, 0, delete: true);
        }
        else _bs.MarkAdjust(first, first + count - 1, 0, -count, delete: true);
        NoteChange(first, 0);
    }

    private void RawDeleteLines(int first, int count)
    {
        var n = LineCount;
        if (count >= n) HostEdit(new VimEdit(new TextPosition(0, 0), new TextPosition(n - 1, L(n - 1).Length), ""));
        else if (first + count < n) HostEdit(new VimEdit(new TextPosition(first, 0), new TextPosition(first + count, 0), ""));
        else HostEdit(new VimEdit(new TextPosition(first - 1, L(first - 1).Length), new TextPosition(n - 1, L(n - 1).Length), ""));
    }

    /// <summary>General replacement of [start, end) by text (which may contain '\n'). Used for charwise deletes across lines, joins, splits.</summary>
    private void ReplaceRange(TextPosition start, TextPosition end, string text)
    {
        var oldLines = end.Line - start.Line + 1;
        var newLines = 1;
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') newLines++;
        if (oldLines == 1 && newLines == 1) { ReplaceInLine(start.Line, start.Character, end.Character - start.Character, text); return; }
        var endLineText = L(end.Line);
        SaveUndo(start.Line, oldLines, newLines);
        HostEdit(new VimEdit(start, end, text));
        var delta = newLines - oldLines;
        // where the text that followed the replaced range starts now
        var lastNew = text.LastIndexOf('\n');
        var tailStartCol = lastNew < 0 ? start.Character + text.Length : text.Length - lastNew - 1;
        if (delta != 0)
        {
            if (delta < 0)
            {
                // marks in fully removed middle lines vanish; marks on the old last line follow the joined text
                if (end.Line - 1 >= start.Line + 1) _bs.MarkAdjust(start.Line + 1, end.Line - 1, 0, 0, delete: true);
                _bs.MarkColAdjust(end.Line, 0, start.Line + newLines - 1 - end.Line, tailStartCol);
                _bs.MarkAdjust(end.Line + 1, int.MaxValue / 2, delta, 0, delete: false);
            }
            else
            {
                _bs.MarkAdjust(end.Line + 1, int.MaxValue / 2, delta, 0, delete: false);
                _bs.MarkColAdjust(end.Line, end.Character, start.Line + newLines - 1 - end.Line, tailStartCol - end.Character);
            }
        }
        _ = endLineText;
        NoteChange(delta < 0 ? start.Line + 1 : start.Line, delta < 0 ? 0 : start.Character);
    }

    // ------------------------------------------------------------------------------------------------------------
    // undo / redo
    // ------------------------------------------------------------------------------------------------------------

    private void RawReplaceLines(int top, int oldCount, string[] newLines)
    {
        var n = LineCount;
        if (oldCount == newLines.Length)
        {
            for (var i = 0; i < oldCount; i++)
            {
                var cur = L(top + i);
                if (!ReferenceEquals(cur, newLines[i]) && cur != newLines[i]) RawSetLine(top + i, cur, newLines[i]);
            }
        }
        else if (oldCount > 0 && newLines.Length > 0)
            HostEdit(new VimEdit(new TextPosition(top, 0), new TextPosition(top + oldCount - 1, L(top + oldCount - 1).Length), string.Join('\n', newLines)));
        else if (oldCount > 0) RawDeleteLines(top, oldCount);
        else if (top < n) HostEdit(new VimEdit(new TextPosition(top, 0), new TextPosition(top, 0), string.Join('\n', newLines) + "\n"));
        else HostEdit(new VimEdit(new TextPosition(n - 1, L(n - 1).Length), new TextPosition(n - 1, L(n - 1).Length), "\n" + string.Join('\n', newLines)));
    }

    /// <summary>Applies one undo header in the given direction (port of Vim's u_undoredo, including its cursor placement rules).</summary>
    private void ApplyUndoHeader(UndoHeader h, bool undo, out int oldCountSum, out int newCountSum)
    {
        var savedMarks = (TextPosition?[])_bs.Marks.Clone();
        var savedVisual = (_bs.VisualStart, _bs.VisualEnd, _bs.VisualMode, _bs.VisualCurswant);
        var opStart = new TextPosition(LastLine, 0);
        var opEnd = new TextPosition(-1, 0);
        var curLnum = _line + 1;
        var curCol = _col;
        var newlnum = int.MaxValue;
        var uhLnum = h.Cursor.Line + 1;
        oldCountSum = newCountSum = 0;

        var order = undo ? Enumerable.Range(0, h.Entries.Count).Reverse().ToArray() : Enumerable.Range(0, h.Entries.Count).ToArray();
        for (var oi = 0; oi < order.Length; oi++)
        {
            var e = h.Entries[order[oi]];
            var top = e.Start;
            var oldsize = e.NewCount;
            var newsize = e.Old.Length;
            var isLast = oi == order.Length - 1;
            if (top < newlnum)
            {
                var lnum = uhLnum;
                if (lnum >= top && lnum <= top + newsize + 1)
                {
                    curLnum = h.Cursor.Line + 1;
                    curCol = h.Cursor.Character;
                    newlnum = curLnum - 1;
                }
                else
                {
                    var i = 0;
                    for (; i < newsize && i < oldsize; i++)
                        if (L(top + i) != e.Old[i]) break;
                    if (i == newsize && newlnum == int.MaxValue && isLast) { newlnum = top; curLnum = newlnum + 1; }
                    else if (i < newsize) { newlnum = top + i; curLnum = newlnum + 1; }
                }
            }

            var replaced = new string[oldsize];
            for (var i = 0; i < oldsize; i++) replaced[i] = L(top + i);
            RawReplaceLines(top, oldsize, e.Old);
            if (oldsize != newsize)
            {
                // mark_adjust(top + 1, top + oldsize, MAXLNUM, newsize - oldsize) in Vim's 1-based terms
                if (oldsize > 0) _bs.MarkAdjust(top, top + oldsize - 1, 0, newsize - oldsize, delete: true);
                else _bs.MarkAdjust(top, int.MaxValue / 2, newsize, 0, delete: false);
                if (opStart.Line + 1 > top + oldsize) opStart = new TextPosition(opStart.Line + newsize - oldsize, 0);
                if (opEnd.Line + 1 > top + oldsize) opEnd = new TextPosition(opEnd.Line + newsize - oldsize, 0);
            }
            if (top < opStart.Line) opStart = new TextPosition(top, 0);
            if (newsize == 0 && top > opEnd.Line) opEnd = new TextPosition(top, 0);
            else if (top + newsize - 1 > opEnd.Line) opEnd = new TextPosition(top + newsize - 1, 0);
            NoteChange(undo ? top : Math.Max(top, top + newsize - 1), 0);
            newCountSum += newsize;
            oldCountSum += oldsize;
            e.Old = replaced;
            e.NewCount = newsize;
        }

        _bs.OpStart = new TextPosition(Math.Clamp(opStart.Line, 0, LastLine), 0);
        _bs.OpEnd = new TextPosition(Math.Clamp(opEnd.Line, 0, LastLine), 0);

        // restore the marks as they were before the change, remember the current ones for the opposite direction
        if (h.NamedMarks is { } nm)
        {
            for (var i = 0; i < 26; i++)
            {
                if (nm[i] is { } pm) _bs.Marks[i] = pm;
                nm[i] = savedMarks[i];
            }
        }

        // the Visual area comes back as it was before the change; the current one is kept for the opposite direction
        if (h.VisualStart is not null)
        {
            _bs.VisualStart = h.VisualStart; _bs.VisualEnd = h.VisualEnd; _bs.VisualMode = h.VisualMode; _bs.VisualCurswant = h.VisualCurswant;
            h.VisualStart = savedVisual.VisualStart; h.VisualEnd = savedVisual.VisualEnd; h.VisualMode = savedVisual.VisualMode; h.VisualCurswant = savedVisual.VisualCurswant;
        }

        if (h.Cursor.Line + 2 == curLnum && curLnum > 1) curLnum--;
        _line = curLnum - 1;
        _col = curCol;
        if (curLnum <= LineCount)
        {
            if (h.Cursor.Line + 1 == curLnum) _col = h.Cursor.Character;
            else Beginline(BL.Sol | BL.Fix);
        }
        else
        {
            _col = 0;
        }
        _setCurswant = true;
        CheckCursor();
        _bs.KnownVersion = _buf.Version;
    }

    private void UndoCommand(int count)
    {
        var tree = _bs.Undo;
        tree.Synced = true;
        for (var n = 0; n < Math.Max(1, count); n++)
        {
            if (tree.Current.Parent is null)
            {
                Beep();
                Message("Already at oldest change");
                return;
            }
            if (n == 0) SetPcmark();
            var h = tree.Current;
            ApplyUndoHeader(h, undo: true, out var oc, out var nc);
            h.Parent!.Preferred = h;
            tree.Current = h.Parent;
            if (n == Math.Max(1, count) - 1) UndoMessage(true, oc, nc, tree.Current.Seq == 0 ? 0 : h.Seq);
        }
    }

    private void RedoCommand(int count)
    {
        var tree = _bs.Undo;
        tree.Synced = true;
        for (var n = 0; n < Math.Max(1, count); n++)
        {
            var h = tree.Current.Preferred;
            if (h is null)
            {
                Beep();
                Message("Already at newest change");
                return;
            }
            ApplyUndoHeader(h, undo: false, out var oc, out var nc);
            tree.Current = h;
            if (n == Math.Max(1, count) - 1) UndoMessage(false, oc, nc, h.Seq);
        }
    }

    private void UndoMessage(bool undid, int oldCount, int newCount, int seq)
    {
        var d = oldCount - newCount;
        string what;
        int n;
        if (d == -1) { what = "more line"; n = 1; }
        else if (d < 0) { what = "more lines"; n = -d; }
        else if (d == 1) { what = "line less"; n = 1; }
        else if (d > 1) { what = "fewer lines"; n = d; }
        else { n = newCount; what = n == 1 ? "change" : "changes"; }
        Message($"{n} {what}; {(undid ? "before" : "after")} #{seq}  just now");
    }

    /// <summary>Moves to the state after change <paramref name="seq"/> (0 = original text), undoing up to the common ancestor and redoing down.</summary>
    private bool UndoToSeq(int seq)
    {
        var tree = _bs.Undo;
        tree.Synced = true;
        var target = tree.FindSeq(seq);
        if (target is null) { Error($"E830: Undo number {seq} not found"); return false; }
        var path = new List<UndoHeader>();
        for (var t = target; t is not null; t = t.Parent) path.Add(t);
        var ancestors = new HashSet<UndoHeader>(path);
        var oc = 0;
        var nc = 0;
        if (target != tree.Current) SetPcmark();
        while (!ancestors.Contains(tree.Current))
        {
            var h = tree.Current;
            ApplyUndoHeader(h, true, out var a, out var b);
            oc += a; nc += b;
            h.Parent!.Preferred = h;
            tree.Current = h.Parent;
        }
        var down = new List<UndoHeader>();
        for (var t = target; t != tree.Current; t = t!.Parent) down.Add(t!);
        if (down.Count > 0 || !ancestors.Contains(tree.Current) || tree.Current != target) { }
        down.Reverse();
        foreach (var h in down)
        {
            ApplyUndoHeader(h, false, out var a, out var b);
            oc += a; nc += b;
            h.Parent!.Preferred = h;
            tree.Current = h;
        }
        return true;
    }

    private void LineUndo()
    {
        if (_bs.ULineText is null || _bs.ULineNr >= LineCount || _bs.ULineNr < 0 || O.UndoLevels < 0)
        {
            Beep();
            return;
        }
        var l = _bs.ULineNr;
        var restore = _bs.ULineText;
        var before = L(l);
        var savedCol = _bs.ULineCol;
        _bs.Undo.Synced = true;
        SaveUndo(l, 1, 1, saveULine: false);
        RawSetLine(l, before, restore);
        NoteChange(l, 0);
        _bs.ULineText = before;
        if (_line == l) _bs.ULineCol = _col;
        _line = l;
        _col = savedCol;
        CheckCursor();
        _setCurswant = true;
    }

    // ------------------------------------------------------------------------------------------------------------
    // marks, jump list
    // ------------------------------------------------------------------------------------------------------------

    private struct Jump
    {
        public BufferState? Buf;
        public TextPosition Pos;
    }

    private readonly List<Jump> _jumps = [];
    private int _jumpIdx;
    private bool _noJumpList;

    /// <summary>Vim's setpcmark: remember the position before a jump.</summary>
    private void SetPcmark()
    {
        if (_noJumpList) return;
        _bs.PrevPcMark = _bs.PcMark;
        _bs.PcMark = new TextPosition(_line, _col);
        if (_jumps.Count >= BufferState.JumpListSize) _jumps.RemoveAt(0);
        _jumps.Add(new Jump { Buf = _bs, Pos = _bs.PcMark.Value });
        _jumpIdx = _jumps.Count;
    }

    private void CheckPcmark()
    {
        if (_bs.PrevPcMark is { } prev && (_bs.PcMark is null || _bs.PcMark == new TextPosition(_line, _col))) _bs.PcMark = prev;
        _bs.PrevPcMark = null;
    }

    private void SetMark(char c, TextPosition pos)
    {
        if (c is >= 'a' and <= 'z') _bs.Marks[c - 'a'] = pos;
        else if (c is >= 'A' and <= 'Z') _s.GlobalMarks[c] = (_buf.Uri, pos);
        else if (c is '\'' or '`') SetPcmark();
        else if (c == '[') _bs.OpStart = pos;
        else if (c == ']') _bs.OpEnd = pos;
        else if (c == '<') _bs.VisualStart = pos;
        else if (c == '>') _bs.VisualEnd = pos;
    }

    /// <summary>Position of a mark, or null (unset). For A-Z in another buffer returns null (switching buffers is the host's business).</summary>
    private TextPosition? GetMark(int c)
    {
        TextPosition? p = c switch
        {
            >= 'a' and <= 'z' => _bs.Marks[c - 'a'],
            >= 'A' and <= 'Z' => _s.GlobalMarks.TryGetValue((char)c, out var g) && (g.Uri is null || g.Uri == _buf.Uri) ? g.Pos : null,
            '\'' or '`' => _bs.PcMark,
            '[' => _bs.OpStart,
            ']' => _bs.OpEnd,
            '<' => _bs.VisualStart is { } vs && _bs.VisualEnd is { } ve ? (vs <= ve ? vs : ve) : _bs.VisualStart,
            '>' => _bs.VisualStart is { } vs2 && _bs.VisualEnd is { } ve2 ? (vs2 <= ve2 ? ve2 : vs2) : _bs.VisualEnd,
            '.' => _bs.LastChange,
            '^' => _bs.LastInsert,
            '"' => _bs.LastCursor ?? new TextPosition(0, 0),
            _ => null,
        };
        if (p is { } q && q.Line >= LineCount) return null;
        return p;
    }

    private void SetChangeMarks(TextPosition start, TextPosition end)
    {
        _bs.OpStart = start;
        _bs.OpEnd = end;
    }

    private StringBuilder Sb(int capacity = 64) => new(capacity);
}
