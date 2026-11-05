using System.Text;
using CsCode.Core.Text;

namespace CsCode.Vim;

/// <summary>What `.` repeats: the keys of the last change (with its register and count split out so `3.` can replace the count).</summary>
internal sealed class RedoRecord
{
    public int Reg;
    public int Count;
    public readonly List<int> Keys = [];
    // Visual redo: same-size region from the cursor
    public bool IsVisual;
    public VimMode VisualMode;
    public int VisualLines;
    public int VisualVcol;
    public bool VisualDollar;

    public RedoRecord Clone()
    {
        var r = new RedoRecord { Reg = Reg, Count = Count, IsVisual = IsVisual, VisualMode = VisualMode, VisualLines = VisualLines, VisualVcol = VisualVcol, VisualDollar = VisualDollar };
        r.Keys.AddRange(Keys);
        return r;
    }
}

public sealed partial class VimEngine
{
    private RedoRecord? _redo;
    private RedoRecord? _redoBuilding;   // the change in progress that continues with typed Insert text

    private void AppendCmdKeys(List<int> keys, Cmd c)
    {
        keys.Add(c.Key);
        if (c.Key2 != 0) keys.Add(c.Key2);
        if (c.NChar != 0) { keys.Add(c.NChar); if (c.NChar2 != 0) keys.Add(c.NChar2); }
        if (c.Op != OpType.None && c.HasMotion && !c.Visual)
        {
            if (c.Force != 0) keys.Add(c.Force);
            if (c.Doubled)
            {
                keys.Add(c.Key == 'g' || c.Key == 'z' ? c.Key2 : c.Key);
            }
            else
            {
                var m = c.M;
                keys.Add(m.Key);
                if (m.Key2 != 0) keys.Add(m.Key2);
                if (m.NChar != 0) keys.Add(m.NChar);
            }
        }
        if (c.SurroundTag is not null) { foreach (var ch in c.SurroundTag) keys.Add(ch); keys.Add('>'); }
    }

    private void SetRedoSurround(Cmd c)
    {
        var r = new RedoRecord { Reg = 0, Count = c.Count0 };
        r.Keys.Add(c.Op == OpType.SurroundDelete ? 'd' : 'c');
        r.Keys.Add('s');
        r.Keys.Add(c.NChar);
        if (c.Op == OpType.SurroundChange) r.Keys.Add(c.NChar2);
        AddSurroundTagKeys(r.Keys, c);
        _redo = r;
        _redoBuilding = null;
    }

    private static void AddSurroundTagKeys(List<int> keys, Cmd c)
    {
        if (c.SurroundTag is null) return;
        foreach (var ch in c.SurroundTag) keys.Add(ch);
        keys.Add('>');
    }

    private void RecordRedoForCommand(Cmd c, OpArgs oap, int count0)
    {
        if (oap.Op is OpType.Yank or OpType.Fold) return;
        var r = new RedoRecord { Reg = c.Reg, Count = count0 };
        if (c.Op == OpType.SurroundAdd)
        {
            r.Keys.Add('y'); r.Keys.Add('s');
            if (c.Doubled) r.Keys.Add('s');
            else
            {
                var sm = c.M;
                r.Keys.Add(sm.Key);
                if (sm.Key2 != 0) r.Keys.Add(sm.Key2);
                if (sm.NChar != 0) r.Keys.Add(sm.NChar);
            }
            r.Keys.Add(c.NChar);
            AddSurroundTagKeys(r.Keys, c);
            _redo = r;
            _redoBuilding = null;
            return;
        }
        // operator keys, then motion keys
        r.Keys.Add(c.Key);
        if (c.Key2 != 0) r.Keys.Add(c.Key2);
        if (c.NChar != 0) r.Keys.Add(c.NChar);
        if (c.Force != 0) r.Keys.Add(c.Force);
        if (c.Doubled) r.Keys.Add(c.Key is 'g' or 'z' ? c.Key2 : c.Key);
        else
        {
            var m = c.M;
            r.Keys.Add(m.Key);
            if (m.Key2 != 0) r.Keys.Add(m.Key2);
            if (m.NChar != 0) r.Keys.Add(m.NChar);
            if (m.Key is '/' or '?') { r.Keys.Add(-1); } // search text appended on <CR>
        }
        if (c.SurroundTag is not null) { foreach (var ch in c.SurroundTag) r.Keys.Add(ch); r.Keys.Add('>'); }
        _redo = r;
        _redoBuilding = r;
    }

    private void SetRedoPlain(Cmd c)
    {
        var r = new RedoRecord { Reg = c.Reg, Count = c.Count0 };
        AppendCmdKeys(r.Keys, c);
        _redo = r;
        _redoBuilding = r;
    }

    private void RepeatLast(int count0)
    {
        if (_redo is null) { Beep(); return; }
        var r = _redo;
        if (count0 > 0) r.Count = count0;
        var keys = new List<int>();
        if (r.Reg != 0) { keys.Add('"'); keys.Add(r.Reg); }
        if (r.IsVisual)
        {
            // re-create a Visual selection of the same size at the cursor, then replay the operator keys
            _pendingVisualRedo = r;
            keys.Clear();
            if (r.Reg != 0) { keys.Add('"'); keys.Add(r.Reg); }
            keys.Add(Keys.Plug);   // internal marker consumed by ExecuteCmd: starts the redo selection
            foreach (var k in r.Keys) keys.Add(k);
            _stuff.PushFront(keys.ToArray());
            return;
        }
        if (r.Count > 0) foreach (var ch in r.Count.ToString()) keys.Add(ch);
        foreach (var k in r.Keys) if (k >= 0) keys.Add(k);
        _stuff.PushFront(keys.ToArray());
    }

    private RedoRecord? _pendingVisualRedo;

    // ------------------------------------------------------------------------------------------------------------
    // execution
    // ------------------------------------------------------------------------------------------------------------

    private void ExecuteCmd(Cmd c)
    {
        if (_opCmdForSearch is not null) { }
        if (c.Visual && c.Op != OpType.None) { ApplyVisualOperator(c); return; }
        if (c.Op is OpType.SurroundDelete or OpType.SurroundChange)
        {
            SetRedoSurround(c);
            if (c.Op == OpType.SurroundDelete) SurroundDeleteCommand(c); else SurroundChangeCommand(c);
            CheckPcmark();
            FinishCommand();
            return;
        }
        if (c.Op != OpType.None)
        {
            ExecOperatorMotion(c);
            CheckPcmark();
            FinishCommand();
            return;
        }
        ExecPlain(c);
        FinishCommand();
    }

    private void FinishCommand()
    {
        if (_mode is VimMode.Normal or VimMode.InsertNormal && _insert is null) CheckCursor();
        else if (VisualActive) CheckCursor();
    }

    private static Cmd Synth(int key, int count0, int reg = 0, int key2 = 0, int nchar = 0)
    {
        var c = new Cmd { Key = key, Count0 = count0, Reg = reg, Key2 = key2, NChar = nchar };
        return c;
    }

    /// <summary>Rewrites x X D C s S Y (and &lt;Del&gt;) into operator + motion like Vim's nv_optrans.</summary>
    private bool TryOperatorAlias(Cmd c)
    {
        int op, m1;
        int m2 = 0;
        switch (c.Key)
        {
            case 'x' or Keys.Delete: op = 'd'; m1 = 'l'; break;
            case 'X': op = 'd'; m1 = 'h'; break;
            case 'D': op = 'd'; m1 = '$'; break;
            case 'C': op = 'c'; m1 = '$'; break;
            case 's': op = 'c'; m1 = 'l'; break;
            case 'S': op = 'c'; m1 = 'c'; break;
            case 'Y': op = 'y'; m1 = 'y'; break;
            default: return false;
        }
        var x = new Cmd { Key = op, Op = c.Key == 'Y' || c.Key == 'S' ? (c.Key == 'Y' ? OpType.Yank : OpType.Change) : (op == 'd' ? OpType.Delete : OpType.Change), Count0 = c.Count0, Reg = c.Reg, HasMotion = true };
        if (c.Key is 'Y' or 'S') { x.Doubled = true; x.M.Key = m1; }
        else { x.M.Key = m1; x.M.Key2 = m2; }
        ExecOperatorMotion(x);
        CheckPcmark();
        return true;
    }

    private void ExecPlain(Cmd c)
    {
        var k = c.Key;
        var count0 = c.Count0;
        var count1 = c.Count1;

        if (k == Keys.Plug && _pendingVisualRedo is not null) { StartVisualRedo(); return; }

        // ---- commands that are only meaningful in Visual mode
        if (VisualActive && ExecVisualOnly(c)) return;

        switch (k)
        {
            case 'x' or 'X' or 'D' or 'C' or 's' or 'S' or 'Y' or Keys.Delete:
                if (!VisualActive && TryOperatorAlias(c)) return;
                break;
            case '~' when !O.TildeOp:
                TildeCommand(c);
                return;
            case 'r':
                ReplaceCharCommand(c);
                return;
            case 'R':
                SetRedoPlain(c);
                StartInsert('R', count1, restart: false);
                return;
            case 'i' or 'a' or 'I' or 'A' or 'o' or 'O' when !(VisualActive && k is 'i' or 'a'):
                if (!CanModify()) return;
                SetRedoPlain(c);
                InsertCommand(c);
                return;
            case 'p' or 'P':
                PutCommand(c, forward: k == 'p', flags: PutFlags.None);
                return;
            case 'J':
                if (!CanModify()) return;
                JoinCommand(c, insertSpace: true);
                return;
            case 'u':
                UndoCommand(count1);
                return;
            case 18:
                RedoCommand(count1);
                return;
            case 'U':
                LineUndo();
                return;
            case '.':
                RepeatLast(count0);
                return;
            case '&':
                if (_s.Search.SubPattern.Length == 0 && _s.Search.LastExCommand.Length == 0) { Error("E33: No previous substitute regular expression"); return; }
                _s.Registers.LastCommandLine = "s";
                ExecuteExLine("s");
                return;
            case 'v' or 'V' or 22:
                StartVisualMode(k == 'v' ? VimMode.Visual : k == 'V' ? VimMode.VisualLine : VimMode.VisualBlock, count0);
                return;
            case ':':
                StartExFromNormal(count0);
                return;
            case '/' or '?':
                StartSearchCmdline(k == '/', count1, null, null);
                return;
            case '@':
                ExecuteRegisterMacro(c.NChar, count1);
                return;
            case 'Q':
                if (_lastRecorded == '\0') { Error("E354: Invalid register name: '^@'"); return; }
                ExecuteRegisterMacro(_lastRecorded, count1);
                return;
            case 'q':
                RecordCommand(c.NChar);
                return;
            case 'm':
                MarkCommand(c.NChar);
                return;
            case 1 or 24:
                IncrementCommand(c, k == 1 ? count1 : -count1);
                return;
            case 15:
                JumpOlder(count1);
                return;
            case 9 or 'I' - 64 + 0 when k == 9:
                JumpNewer(count1);
                return;
            case 5 or 25 or 4 or 21 or 6 or 2:
                ScrollCommand(k, count0);
                return;
            case 7 or 12:
                if (k == 7) Message($"\"{_buf.Name}\" {LineCount} lines");
                return;
            case 26 or 'Q':
                return;
            case 'K':
                Host.ExecuteCommand("editor.action.showHover", null);
                return;
            case Keys.Insert:
                if (!CanModify()) return;
                SetRedoPlain(c);
                StartInsert('i', count1, restart: false);
                return;
            case Keys.PageDown: ScrollCommand(6, count0); return;
            case Keys.PageUp: ScrollCommand(2, count0); return;
            case 'z':
                ZCommand(c);
                return;
            case 'Z':
                if (c.Key2 == 'Z') ExecuteExLine("x");
                else if (c.Key2 == 'Q') ExecuteExLine("q!");
                return;
            case 23:
                WindowCommand(c);
                return;
            case 'g':
                if (ExecGCommand(c)) return;
                break;
            case '[' or ']':
                if (ExecBracketCommand(c)) return;
                break;
            case Keys.Tab:
                JumpNewer(count1);
                return;
        }

        // ---- everything else is a motion
        if (IsPlainMotion(c))
        {
            var oap = _oap;
            oap.ResetForMotion(OpType.None, 0);
            var before = new TextPosition(_line, _col);
            if (IsSyntaxObject(c) || IsSyntaxMotion(c))
            {
                StartSyntaxPlain(c, count0); // from the host's syntax tree, when its answer arrives
                return;
            }
            bool ok;
            if (c.Key is 'a' or 'i') ok = RunTextObject(c, count0, oap);
            else ok = RunMotion(c, count0, oap);
            if (_searchPendingOp || _cmdline is not null) return;
            if (!ok) { Beep(); CheckCursor(); return; }
            CheckPcmark();
            if (!VisualActive) CheckCursor();
            return;
        }
        Beep();
    }

    private bool IsPlainMotion(Cmd c)
    {
        switch (c.Key)
        {
            case 'a' or 'i': return VisualActive;
            case 's' or 'z': return false;
            case 'g': return c.Key2 is 'g' or '_' or 'j' or 'k' or '0' or '^' or '$' or 'm' or 'M' or 'e' or 'E' or 'o' or '\'' or '`' or 'n' or 'N' or '*' or '#';
        }
        return IsMotionKey(c);
    }

    // ------------------------------------------------------------------------------------------------------------
    // simple commands
    // ------------------------------------------------------------------------------------------------------------

    private void TildeCommand(Cmd c)
    {
        if (!CanModify()) return;
        var s = CurLine;
        if (s.Length == 0) { Beep(); return; }
        SetRedoPlain(c);
        var count = c.Count1;
        var start = _col;
        var i = _col;
        var n = 0;
        while (n < count && i < s.Length) { i += VimText.CharLen(s, i); n++; }
        var swapped = SwapChars(OpType.Tilde, s, start, i);
        if (swapped != s) SetLine(_line, swapped, start);
        _col = i >= s.Length ? VimText.PrevCharStart(s, s.Length) : i;
        _setCurswant = true;
        if (swapped != s)
        {
            NoteChange(_line, start);
            _bs.OpStart = new TextPosition(_line, start);
            _bs.OpEnd = new TextPosition(_line, _col > 0 ? VimText.PrevCharStart(CurLine, _col) : 0);
        }
    }

    private void ReplaceCharCommand(Cmd c)
    {
        if (!CanModify()) return;
        var s = CurLine;
        var count = c.Count1;
        var ch = c.NChar2 != 0 ? c.NChar2 : c.NChar;
        // count characters must exist
        var i = _col;
        var n = 0;
        while (n < count && i < s.Length) { i += VimText.CharLen(s, i); n++; }
        if (n < count) { Beep(); return; }
        SetRedoPlain(c);
        if (c.NChar2 == 0 && ch is Keys.CR or Keys.NL)
        {
            // Strange Vi behaviour kept by Vim: the characters are deleted and ONE line break is inserted, as if by R<CR><Esc>.
            DeleteInLine(_line, _col, i - _col);
            _redoBuilding = null;
            StartInsert('r', 1, restart: false);
            _insert!.Typed.Add(Keys.CR);
            InsertNewlineRaw();
            InsertEscape(false);
            _s.Registers.LastInserted = "\n";
            return;
        }
        var rep = ch == 0 ? "" : Keys.SafeString(ch);
        var text = count == 1 ? rep : VimText.Repeat(rep, count);
        var start = _col;
        ReplaceInLine(_line, start, i - start, text);
        // cursor on the last replaced char
        var lastLen = text.Length > 0 ? VimText.CharLen(text, VimText.PrevCharStart(text, text.Length)) : 0;
        _col = start + text.Length - lastLen;
        NoteChange(_line, _col);
        _bs.OpStart = new TextPosition(_line, start);
        _bs.OpEnd = new TextPosition(_line, _col);
        _s.Registers.LastInserted = Keys.Encode(ch);
        _setCurswant = true;
    }

    private void InsertCommand(Cmd c)
    {
        var count1 = c.Count1;
        var k = c.Key;
        switch (k)
        {
            case 'a':
                if (CurLine.Length > 0) _col += VimText.CharLen(CurLine, _col);
                _bs.Undo.Synced = _bs.Undo.Synced;
                StartInsert('a', count1, restart: false);
                break;
            case 'A':
                _col = CurLine.Length;
                StartInsert('A', count1, restart: false);
                break;
            case 'I':
                _col = VimText.FirstNonBlank(CurLine);
                StartInsert('I', count1, restart: false);
                break;
            case 'i':
                StartInsert('i', count1, restart: false);
                break;
            case 'o' or 'O':
                OpenLineCommand(k == 'o', count1);
                break;
        }
    }

    private void OpenLineCommand(bool below, int count)
    {
        var line = _line;
        var indent = O.AutoIndent ? CurLine.Substring(0, VimText.FirstNonBlank(CurLine)) : "";
        if (O.AutoIndent && VimText.IsBlank(CurLine)) indent = CurLine;
        var at = below ? line + 1 : line;
        // a closed fold: open below its end
        if (_host!.TryGetClosedFold(line, out var fs, out var fe)) at = below ? fe + 1 : fs;
        InsertLines(at, [indent]);
        _line = at;
        _col = indent.Length;
        _bs.OpStart = new TextPosition(at, 0);
        _bs.OpEnd = new TextPosition(at, 0);
        _didAutoIndent = indent.Length > 0;
        _aiCol = indent.Length;
        StartInsert(below ? 'o' : 'O', count, restart: false);
        _insert!.Start = new TextPosition(_line, 0);
        _insert.StartOrig = new TextPosition(_line, _col);
    }

    private void JoinCommand(Cmd c, bool insertSpace)
    {
        var count = Math.Max(2, c.Count0);
        if (_line + count - 1 > LastLine)
        {
            // Vim joins as many lines as possible when the count is too big, but fails if there is no next line
            if (_line >= LastLine) { Beep(); return; }
            count = LastLine - _line + 1;
            if (c.Count0 == 0 || c.Count0 <= 2) { }
        }
        SetRedoPlain(c);
        DoJoin(count, insertSpace, setmark: true);
    }

    private void PutCommand(Cmd c, bool forward, PutFlags flags)
    {
        if (!CanModify()) return;
        SetRedoPlain(c);
        var reg = c.Reg;
        DoPut(reg, forward, c.Count1, flags);
    }

    private void MarkCommand(int ch)
    {
        if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '\'' or '`' or '[' or ']' or '<' or '>')
        {
            if (ch is '\'' or '`') SetPcmark();
            else SetMark((char)ch, new TextPosition(_line, _col));
        }
        else Beep();
    }

    private void IncrementCommand(Cmd c, int delta)
    {
        if (!CanModify()) return;
        SetRedoPlain(c);
        if (!AddToNumberAtCursor(delta)) Beep();
    }

    // ------------------------------------------------------------------------------------------------------------
    // g-commands and brackets
    // ------------------------------------------------------------------------------------------------------------

    private bool ExecGCommand(Cmd c)
    {
        var count0 = c.Count0;
        var count1 = c.Count1;
        switch (c.Key2)
        {
            case 'J':
                if (!CanModify()) return true;
                JoinCommand(c, insertSpace: false);
                return true;
            case 'p' or 'P':
                PutCommand(c, forward: c.Key2 == 'p', flags: PutFlags.CursEnd);
                return true;
            case 'v':
                ReselectVisual();
                return true;
            case 'i':
                if (!CanModify()) return true;
                SetRedoPlain(c);
                if (_bs.LastInsert is { } li && li.Line <= LastLine) { _line = li.Line; _col = Math.Min(li.Character, L(li.Line).Length); }
                StartInsert('i', count1, restart: false);
                return true;
            case 'I':
                if (!CanModify()) return true;
                SetRedoPlain(c);
                _col = 0;
                StartInsert('I', count1, restart: false);
                return true;
            case ';' or ',':
                ChangeListJump(c.Key2 == ';' ? -count1 : count1);
                return true;
            case '&':
                if (_s.Search.SubPattern.Length == 0) { Error("E35: No previous regular expression"); return true; }
                ExecuteExLine("%s//~/&");
                return true;
            case '-' or '+':
                UndoTimeTravel(c.Key2 == '-' ? -count1 : count1);
                return true;
            case 'a':
                Message(DescribeCharUnderCursor());
                return true;
            case '8' or 'x':
                return true;
            case 'r':
                Host.ExecuteCommand(Core.Platform.KnownCommands.FindReferences, null);
                return true;
            case 'd' or 'D':
                Host.ExecuteCommand(Core.Platform.KnownCommands.GotoDefinition, null);
                return true;
            case 'h':
                Host.ExecuteCommand("editor.action.showHover", null);
                return true;
            case 't' or 'T':
                Host.PerformAction(c.Key2 == 't' ? VimEditorAction.NextTab : VimEditorAction.PreviousTab, new VimActionArgs(Count: count0 > 0 ? count0 : null));
                return true;
            case 'c' or 'C' when _s.Plugins.Commentary:
                return false;
            case 'n' or 'N':
                GnCommand(c);
                return true;
            case 'u' or 'U' or '~' or '?' or 'q' or 'w' or '@':
                return false;
            case 'f' or 'F' or 'x' + 1:
                return true;
            case 'm' or 'M' or 'j' or 'k' or '0' or '^' or '$' or 'e' or 'E' or 'g' or '_' or 'o' or '\'' or '`' or '*' or '#':
                return false;
        }
        Beep();
        return true;
    }

    private string DescribeCharUnderCursor()
    {
        var cp = CharAtCursor();
        if (cp == 0) return "NUL";
        return $"<{Keys.SafeString(cp)}> {cp}, Hex {cp:x2}, Oct {Convert.ToString(cp, 8)}";
    }

    private bool ExecBracketCommand(Cmd c)
    {
        var k = c.Key;
        var k2 = c.Key2;
        switch (k2)
        {
            case 'p' or 'P':
                PutCommand(c, forward: k == ']' && k2 == 'p', flags: PutFlags.FixIndent);
                return true;
            case 's' or 'S':
                // ]s [s (and ]S [S: CsCode reports no "rare" or "local" words, so they are the same): the spell checker's issues
                if (!O.Spell) Error("E756: Spell checking is not enabled");
                else Host.ExecuteCommand(k == ']' ? SpellCommands.Next : SpellCommands.Previous, [c.Count1]);
                return true;
            case 'z':
                return true;
            case 'c' or 'd' or 'D' or 'i' or 'I' or '#' or '*' or '/':
                return true; // (]m [m ]M [M are motions: the syntax tree's functions)
        }
        return false;
    }

    private void ChangeListJump(int delta)
    {
        var list = _bs.ChangeList;
        if (list.Count == 0) { Error("E664: Changelist is empty"); return; }
        var idx = _bs.ChangeListIdx + delta;
        if (idx < 0 || idx >= list.Count)
        {
            if (delta < 0 && _bs.ChangeListIdx == 0) { Error("E662: At start of changelist"); return; }
            if (delta > 0 && _bs.ChangeListIdx >= list.Count - 1) { Error("E663: At end of changelist"); return; }
            idx = Math.Clamp(idx, 0, list.Count - 1);
        }
        _bs.ChangeListIdx = idx;
        var p = list[idx];
        _line = Math.Min(p.Line, LastLine);
        _col = p.Character;
        CheckCursor();
        _setCurswant = true;
    }

    private void UndoTimeTravel(int delta)
    {
        var cur = _bs.Undo.Current.Seq;
        var target = cur + delta;
        if (cur == 0 && delta < 0) { Message("Already at oldest change"); Beep(); return; }
        if (cur == _bs.Undo.LastSeq && delta > 0) { Message("Already at newest change"); Beep(); return; }
        UndoToSeq(Math.Clamp(target, 0, _bs.Undo.LastSeq));
    }

    // ------------------------------------------------------------------------------------------------------------
    // jump list
    // ------------------------------------------------------------------------------------------------------------

    private void CleanupJumplist()
    {
        // remove entries for the same line, keeping the newest
        for (var i = 0; i < _jumps.Count; i++)
            for (var j = i + 1; j < _jumps.Count; j++)
                if (ReferenceEquals(_jumps[i].Buf, _jumps[j].Buf) && _jumps[i].Pos.Line == _jumps[j].Pos.Line) { _jumps.RemoveAt(i); i--; if (_jumpIdx > i + 1) _jumpIdx--; break; }
        _jumpIdx = Math.Min(_jumpIdx, _jumps.Count);
    }

    private void JumpOlder(int count)
    {
        CleanupJumplist();
        if (_jumps.Count == 0) { Beep(); return; }
        var idx = _jumpIdx - count;
        if (idx < 0) { Beep(); return; }
        if (_jumpIdx == _jumps.Count)
        {
            // first Ctrl-O: remember where we are so Ctrl-I can come back
            var here = new TextPosition(_line, _col);
            SetPcmark();
            CleanupJumplist();
            if (_jumps.Count > 0 && _jumps[^1].Pos.Line == here.Line) { }
            idx = _jumps.Count - 1 - count;
            if (_jumps.Count - 1 < count) { _jumps.RemoveAt(_jumps.Count - 1); _jumpIdx = _jumps.Count; Beep(); return; }
        }
        MoveToJump(idx);
    }

    private void JumpNewer(int count)
    {
        CleanupJumplist();
        var idx = _jumpIdx + count;
        if (idx >= _jumps.Count) { Beep(); return; }
        MoveToJump(idx);
    }

    private void MoveToJump(int idx)
    {
        var j = _jumps[idx];
        _jumpIdx = idx;
        _line = Math.Min(j.Pos.Line, LastLine);
        _col = j.Pos.Character;
        CheckCursor();
        _setCurswant = true;
    }

    // ------------------------------------------------------------------------------------------------------------
    // z-commands and scrolling
    // ------------------------------------------------------------------------------------------------------------

    private void ZCommand(Cmd c)
    {
        var host = Host;
        var vp = host.GetViewport();
        var count0 = c.Count0;
        switch (c.Key2)
        {
            case Keys.CR or 't':
            case '.' or 'z':
            case '-' or 'b':
            {
                if (count0 > 0) _line = Math.Min(count0 - 1, LastLine);
                var rowsAbove = c.Key2 is Keys.CR or 't' ? 0 : c.Key2 is '.' or 'z' ? Math.Max(0, (vp.Height - 1) / 2) : Math.Max(0, vp.Height - 1);
                host.SetTopLine(Math.Max(0, _line - rowsAbove));
                if (c.Key2 is Keys.CR or '.' or '-') Beginline(BL.White | BL.Fix);
                return;
            }
            case 'o' or 'O' or 'c' or 'C' or 'a' or 'A' or 'v' or 'x' or 'X' or 'm' or 'M' or 'r' or 'R' or 'n' or 'N' or 'i' or 'd' or 'D' or 'E' or 'F' or 'j' or 'k':
                FoldCommandKey(c.Key2, c.Count1);
                return;
            case 'h' or 'l' or 'H' or 'L' or 's' or 'e':
                return;
            // spelling (docs/SPELLCHECK.md): the spell checker's commands act at the cursor, which the editor's caret follows. zG and zW
            // (Vim's session-only word list) act as zg and zw: CsCode has no session-only list. Undoing them (zu...) is not supported.
            case '=':
                Host.ExecuteCommand(SpellCommands.Suggest, null);
                return;
            case 'g' or 'G':
                Host.ExecuteCommand(SpellCommands.AddWord, null);
                return;
            case 'w' or 'W':
                Host.ExecuteCommand(SpellCommands.FlagWord, null);
                return;
        }
        Beep();
    }

    /// <summary>The spell checker's command ids (cspell's where cspell has the command; CsCode.SpellCheck registers them).</summary>
    internal static class SpellCommands
    {
        public const string Next = "cSpell.goToNextSpellingIssue";
        public const string Previous = "cSpell.goToPreviousSpellingIssue";
        public const string Suggest = "cSpell.suggestSpellingCorrections";
        public const string AddWord = "cSpell.addWordToUserDictionary";
        public const string FlagWord = "cscode.spellCheck.flagWord";
    }

    private void FoldCommandKey(int k, int count)
    {
        var l = _line;
        var h = Host;
        switch (k)
        {
            case 'o': h.FoldCommand(VimFoldCommand.Open, l, l); break;
            case 'O': h.FoldCommand(VimFoldCommand.OpenRecursive, l, l); break;
            case 'c': h.FoldCommand(VimFoldCommand.Close, l, l); break;
            case 'C': h.FoldCommand(VimFoldCommand.CloseRecursive, l, l); break;
            case 'a': h.FoldCommand(VimFoldCommand.Toggle, l, l); break;
            case 'A': h.FoldCommand(VimFoldCommand.Toggle, l, l); break;
            case 'v': h.FoldCommand(VimFoldCommand.Open, l, l); break;
            case 'R' or 'n': h.FoldCommand(VimFoldCommand.OpenAll, 0, LastLine); break;
            case 'M' or 'N': h.FoldCommand(VimFoldCommand.CloseAll, 0, LastLine); break;
            case 'r': h.FoldCommand(VimFoldCommand.OpenAll, 0, LastLine); break;
            case 'm': h.FoldCommand(VimFoldCommand.CloseAll, 0, LastLine); break;
            case 'x' or 'X': h.FoldCommand(VimFoldCommand.OpenAll, 0, LastLine); break;
            case 'i': h.FoldCommand(VimFoldCommand.ToggleEnable, 0, LastLine); break;
            case 'd': h.FoldCommand(VimFoldCommand.Delete, l, l); break;
            case 'D': h.FoldCommand(VimFoldCommand.Delete, l, l); break;
            case 'E': h.FoldCommand(VimFoldCommand.DeleteAll, 0, LastLine); break;
            case 'F': h.FoldCommand(VimFoldCommand.Create, l, Math.Min(LastLine, l + count - 1)); break;
            case 'j': h.FoldCommand(VimFoldCommand.MoveNext, l, l); break;
            case 'k': h.FoldCommand(VimFoldCommand.MovePrevious, l, l); break;
        }
    }

    private void ScrollCommand(int key, int count0)
    {
        var host = Host;
        var vp = host.GetViewport();
        var height = Math.Max(1, vp.Height);
        switch (key)
        {
            case 5: // Ctrl-E
            {
                var n = count0 > 0 ? count0 : 1;
                var top = Math.Min(LastLine, vp.FirstLine + n);
                host.SetTopLine(top);
                var so = Math.Min(O.ScrollOff, (height - 1) / 2);
                if (_line < top + so) { _line = Math.Min(LastLine, top + so); Coladvance(_curswant == 0 && _setCurswant ? VimText.VcolOf(CurLine, _col, O.TabStop) : _curswant); }
                break;
            }
            case 25: // Ctrl-Y
            {
                var n = count0 > 0 ? count0 : 1;
                var top = Math.Max(0, vp.FirstLine - n);
                host.SetTopLine(top);
                var vp2 = host.GetViewport();
                var so = Math.Min(O.ScrollOff, (height - 1) / 2);
                if (_line > vp2.LastLine - so) { _line = Math.Max(0, vp2.LastLine - so); }
                break;
            }
            case 4 or 21: // Ctrl-D / Ctrl-U
            {
                if (count0 > 0) O.Scroll = count0;
                var n = O.Scroll > 0 ? O.Scroll : Math.Max(1, height / 2);
                if (key == 4)
                {
                    if (_line >= LastLine) { Beep(); return; }
                    host.SetTopLine(Math.Min(Math.Max(0, LastLine), vp.FirstLine + n));
                    _line = Math.Min(LastLine, _line + n);
                }
                else
                {
                    if (_line <= 0) { Beep(); return; }
                    host.SetTopLine(Math.Max(0, vp.FirstLine - n));
                    _line = Math.Max(0, _line - n);
                }
                Beginline(BL.Sol | BL.Fix);
                break;
            }
            case 6 or 2: // Ctrl-F / Ctrl-B
            {
                var pages = Math.Max(1, count0);
                if (key == 6)
                {
                    if (vp.FirstLine >= LastLine && _line >= LastLine) { Beep(); return; }
                    var top = vp.FirstLine;
                    for (var i = 0; i < pages; i++) top = Math.Min(LastLine, Math.Max(top + 1, host.GetViewport().LastLine - 1));
                    host.SetTopLine(top);
                    _line = Math.Max(_line, Math.Min(LastLine, top + Math.Min(O.ScrollOff, (height - 1) / 2)));
                }
                else
                {
                    if (vp.FirstLine <= 0) { Beep(); return; }
                    var top = vp.FirstLine;
                    for (var i = 0; i < pages; i++) top = Math.Max(0, top - height + 2);
                    host.SetTopLine(top);
                    var vp2 = host.GetViewport();
                    _line = Math.Min(_line, Math.Max(0, vp2.LastLine - Math.Min(O.ScrollOff, (height - 1) / 2)));
                }
                Beginline(BL.Sol | BL.Fix);
                break;
            }
        }
    }

    private void WindowCommand(Cmd c)
    {
        var count = c.Count0 > 0 ? c.Count0 : (int?)null;
        var k = c.Key2;
        if (k < 32 && k > 0) k += 96; // Ctrl-W Ctrl-x == Ctrl-W x
        var action = k switch
        {
            'h' => VimEditorAction.FocusLeft, 'l' => VimEditorAction.FocusRight, 'k' => VimEditorAction.FocusUp, 'j' => VimEditorAction.FocusDown,
            'w' => VimEditorAction.FocusNext, 'W' or 'p' => VimEditorAction.FocusPrevious, 't' => VimEditorAction.FocusTop, 'b' => VimEditorAction.FocusBottom,
            's' or 'S' => VimEditorAction.Split, 'v' => VimEditorAction.VSplit, 'n' => VimEditorAction.NewSplit, 'c' => VimEditorAction.Close,
            'o' => VimEditorAction.Only, 'q' => VimEditorAction.Quit, '=' => VimEditorAction.ResizeEqual, '_' or '|' => VimEditorAction.ResizeMaximize,
            '+' => VimEditorAction.ResizeIncreaseHeight, '-' => VimEditorAction.ResizeDecreaseHeight, '>' => VimEditorAction.ResizeIncreaseWidth,
            '<' => VimEditorAction.ResizeDecreaseWidth, 'x' or 'r' or 'R' => VimEditorAction.ExchangeWindow,
            'H' => VimEditorAction.MoveWindowLeft, 'L' => VimEditorAction.MoveWindowRight, 'K' => VimEditorAction.MoveWindowUp, 'J' => VimEditorAction.MoveWindowDown,
            _ => (VimEditorAction?)null,
        };
        if (action is null) { Beep(); return; }
        if (!Host.PerformAction(action.Value, new VimActionArgs(Count: count))) Error("E-window: this host does not support window commands");
    }
}
