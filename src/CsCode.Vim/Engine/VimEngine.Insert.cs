using System.Text;
using CsCode.Core.Text;

namespace CsCode.Vim;

internal sealed class InsertState
{
    public int Cmd;
    public int Count;
    public TextPosition Start;
    public TextPosition StartOrig;
    public readonly List<int> Typed = new(32);
    public readonly Stack<(int Line, int Col, string? Original)> ReplaceStack = new();
    public bool Replaying;
    public bool AtEolBeforeCtrlO;
    public int OLine;
    public bool WasInsertMode;
}

public sealed partial class VimEngine
{
    private InsertState? _insert;
    private bool _restartEdit;
    private bool _arrowUsed;
    private int _insertLiteral;          // 0 none, 1 after Ctrl-V, 2 after Ctrl-K (first), 3 (second), 4 after Ctrl-R, 5 Ctrl-R Ctrl-R/O/P
    private int _digraphFirst;
    private int _litAccum, _litCount, _litKind;
    private bool _ctrlG;
    private bool _ctrlBackslash;
    private int _regInsertKind;

    private void StartInsert(int cmdchar, int count, bool restart)
    {
        _insert = new InsertState { Cmd = cmdchar, Count = Math.Max(1, count), Start = new TextPosition(_line, _col), StartOrig = new TextPosition(_line, _col) };
        var replace = cmdchar == 'R';
        SetMode(replace ? VimMode.Replace : VimMode.Insert);
        _arrowUsed = false;
        _insertLiteral = 0;
        _restartEdit = false;
        if (_redoBuilding is null) _redoBuilding = null;
        _opPendingShown = false;
    }

    private void SetMode(VimMode m)
    {
        if (_mode == m) return;
        _mode = m;
    }

    private void AbortInsert()
    {
        _insert = null;
        _restartEdit = false;
        _insertLiteral = 0;
        if (_mode is VimMode.Insert or VimMode.Replace or VimMode.InsertNormal) _mode = VimMode.Normal;
    }

    // ------------------------------------------------------------------------------------------------------------
    // key handling in Insert / Replace mode
    // ------------------------------------------------------------------------------------------------------------

    private void InsertKey(int k)
    {
        var st = _insert!;
        if (_insertLiteral != 0) { InsertLiteralKey(k); return; }
        if (_ctrlG) { _ctrlG = false; InsertCtrlG(k); return; }
        if (_ctrlBackslash)
        {
            _ctrlBackslash = false;
            if (k is 14 or 7) { InsertEscape(ctrlC: false, noMove: k == 14 || true, toNormal: true); return; }
            if (k == 15) { InsertCtrlO(); return; }
        }

        if (!st.Replaying) st.Typed.Add(k);
        switch (k)
        {
            case Keys.Esc or 3 or 27:
                if (!st.Replaying && st.Typed.Count > 0) st.Typed.RemoveAt(st.Typed.Count - 1);
                InsertEscape(ctrlC: k == 3);
                return;
            case Keys.CR or Keys.NL:
                InsertNewline();
                return;
            case Keys.BS or Keys.CtrlH:
                InsertBackspace(0);
                return;
            case 23:
                InsertBackspace(1);
                return;
            case 21:
                InsertBackspace(2);
                return;
            case Keys.Delete:
                InsertDeleteChar();
                return;
            case Keys.Tab:
                InsertTab();
                return;
            case 20 or 4: // Ctrl-T / Ctrl-D
                InsertShift(k == 20);
                return;
            case 22 or 17:
                _literalMark = st.Typed.Count - 1;
                _insertLiteral = 1;
                _litAccum = 0; _litCount = 0; _litKind = 0;
                _noMap++;
                return;
            case 11:
                _literalMark = st.Typed.Count - 1;
                _insertLiteral = 2;
                _noMap++;
                return;
            case 18:
                _literalMark = st.Typed.Count - 1;
                _insertLiteral = 4;
                _regInsertKind = 0;
                _noMap++;
                return;
            case 15:
                InsertCtrlO();
                return;
            case 1:
                UnrecordLastKey();
                InsertLastInserted(stop: false);
                return;
            case 0:
                UnrecordLastKey();
                InsertLastInserted(stop: true);
                return;
            case 5 or 25:
                _literalMark = st.Typed.Count - 1;
                InsertCopyFromAdjacent(k == 5 ? 1 : -1);
                return;
            case 14 or 16:
                Host.RequestCompletion(k == 14 ? VimCompletionKind.Next : VimCompletionKind.Previous);
                return;
            case 24:
                Host.RequestCompletion(VimCompletionKind.Keyword);
                return;
            case 7:
                UnrecordLastKey();
                _ctrlG = true;
                return;
            case 28:
                _ctrlBackslash = true;
                return;
            case Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown:
                InsertCursorKey(k);
                return;
            case Keys.Insert:
                SetMode(_mode == VimMode.Insert ? VimMode.Replace : VimMode.Insert);
                return;
        }
        if (Keys.IsChar(k) && (k >= 32 || k == 9)) InsertTyped(k);
        else if (Keys.IsChar(k) && k < 32) { /* other control keys: ignored like Vim's default */ }
    }

    private void InsertCtrlG(int k)
    {
        if (k == 'u') { _bs.Undo.Synced = true; _arrowUsed = false; _insert!.Start = new TextPosition(_line, _col); }
        else if (k is 'j' or Keys.Down or 10) { InsertCursorKey(Keys.Down, keepStart: true); }
        else if (k is 'k' or Keys.Up or 11) { InsertCursorKey(Keys.Up, keepStart: true); }
    }

    private void InsertLiteralKey(int k)
    {
        var st = _insert!;
        switch (_insertLiteral)
        {
            case 1: // after Ctrl-V
            {
                if (_litCount == 0 && _litKind == 0)
                {
                    if (k is >= '0' and <= '9') { _litKind = 10; _litAccum = k - '0'; _litCount = 1; if (!st.Replaying) st.Typed.Add(k); return; }
                    if (k is 'x' or 'X') { _litKind = 16; _litCount = 0; _litMax = 2; if (!st.Replaying) st.Typed.Add(k); _litStarted = true; return; }
                    if (k is 'u') { _litKind = 16; _litMax = 4; _litStarted = true; if (!st.Replaying) st.Typed.Add(k); return; }
                    if (k is 'U') { _litKind = 16; _litMax = 8; _litStarted = true; if (!st.Replaying) st.Typed.Add(k); return; }
                    if (k is 'o' or 'O') { _litKind = 8; _litMax = 3; _litStarted = true; if (!st.Replaying) st.Typed.Add(k); return; }
                    _insertLiteral = 0; _noMap--;
                    if (!st.Replaying) st.Typed.Add(k);
                    InsertLiteralChar(k);
                    return;
                }
                var digit = HexVal(k, _litKind);
                if (digit < 0)
                {
                    _insertLiteral = 0; _noMap--;
                    if (_litStarted && _litCount == 0) { InsertLiteralChar(_litKind == 16 ? 'x' : 'o'); }
                    else InsertLiteralChar(_litAccum);
                    _litStarted = false; _litAccum = 0; _litCount = 0; _litKind = 0;
                    // the terminating key is processed normally
                    if (!st.Replaying && st.Typed.Count > 0) st.Typed.RemoveAt(st.Typed.Count - 1);
                    InsertKey(k);
                    return;
                }
                if (!st.Replaying) st.Typed.Add(k);
                _litAccum = _litAccum * _litKind + digit;
                _litCount++;
                var max = _litKind == 10 ? 3 : _litMax;
                if (_litCount >= max || (_litKind == 10 && _litAccum > 25 && _litCount >= 2 && _litAccum * 10 > 255))
                {
                    _insertLiteral = 0; _noMap--;
                    InsertLiteralChar(_litAccum);
                    _litStarted = false; _litAccum = 0; _litCount = 0; _litKind = 0;
                }
                return;
            }
            case 2: // Ctrl-K first char
                if (!st.Replaying) st.Typed.Add(k);
                if (k == Keys.Esc) { _insertLiteral = 0; _noMap--; return; }
                _digraphFirst = k;
                _insertLiteral = 3;
                return;
            case 3:
                if (!st.Replaying) st.Typed.Add(k);
                _insertLiteral = 0;
                _noMap--;
                if (k == Keys.Esc) { FinishLiteral([]); return; }
                InsertLiteralChar(Digraphs.Lookup(_digraphFirst, k));
                return;
            case 4: // Ctrl-R
                if (!st.Replaying) st.Typed.Add(k);
                if (k is 18 or 15 or 16) { _regInsertKind = k; return; }
                _insertLiteral = 0;
                _noMap--;
                InsertRegister(k, _regInsertKind);
                return;
        }
    }

    private bool _insertBatch;
    private bool _litStarted;

    /// <summary>Vim inserts runs of typed-ahead printable characters in ONE call, so '. keeps the column of the first one.</summary>
    private int[]? _batchKeys;
    private int _batchIdx;

    private bool NextKeyIsPlainChar()
    {
        int k;
        if (_batchKeys is not null) k = _batchIdx + 1 < _batchKeys.Length ? _batchKeys[_batchIdx + 1] : -1;
        else k = _stuff.Count > 0 ? _stuff.Peek() : _typed.Count > 0 ? _typed.Peek() : -1;
        k &= ~NoRemapFlag;
        return k >= 32 && k < 127;
    }
    private int _litMax;

    private static int HexVal(int k, int radix)
    {
        int v = k is >= '0' and <= '9' ? k - '0' : k is >= 'a' and <= 'f' ? k - 'a' + 10 : k is >= 'A' and <= 'F' ? k - 'A' + 10 : -1;
        return v >= 0 && v < radix ? v : -1;
    }

    private int _literalMark = -1;

    /// <summary>Replace the keys typed since the literal-insert command started by the character(s) it produced.</summary>
    private void FinishLiteral(IEnumerable<int> produced)
    {
        var st = _insert;
        if (st is { Replaying: false } && _literalMark >= 0 && _literalMark <= st.Typed.Count)
        {
            st.Typed.RemoveRange(_literalMark, st.Typed.Count - _literalMark);
            st.Typed.AddRange(produced);
        }
        _literalMark = -1;
    }

    private void InsertLiteralChar(int cp)
    {
        if (cp == 0) cp = 10; // a literal NUL is stored as NL, like Vim
        FinishLiteral([22, cp]);
        InsertTypedRaw(cp);
        _insertBatch = false;
    }

    private void InsertRegister(int name, int kind)
    {
        if (name == Keys.Esc) { FinishLiteral([]); return; }
        var reg = _s.Registers.ForRead(name == 0 ? '"' : name, () => Host.ReadClipboard());
        if (reg is null || reg.IsEmpty) { FinishLiteral([]); Beep(); return; }
        var text = reg.ToText();
        var produced = new List<int>();
        foreach (var cp in CodePoints(text)) produced.Add(cp == '\n' ? Keys.NL : cp);
        FinishLiteral(produced);
        // Ctrl-R inserts as if typed (autoindent/textwidth apply); Ctrl-R Ctrl-R inserts literally
        foreach (var part in SplitLinesKeep(text, out _))
        {
            if (part.Newline) { InsertNewlineRaw(); continue; }
            var cps = CodePoints(part.Text).ToArray();
            _insertBatch = false;
            _batchKeys = cps;
            for (_batchIdx = 0; _batchIdx < cps.Length; _batchIdx++) InsertTypedRaw(cps[_batchIdx]);
            _batchKeys = null;
        }
        _insertBatch = false;
    }

    private static IEnumerable<(string Text, bool Newline)> SplitLinesKeep(string text, out bool endsWithNl)
    {
        endsWithNl = text.EndsWith('\n');
        var list = new List<(string, bool)>();
        var from = 0;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') { if (i > from) list.Add((text[from..i], false)); list.Add(("", true)); from = i + 1; }
        if (from < text.Length) list.Add((text[from..], false));
        return list;
    }

    private static IEnumerable<int> CodePoints(string s)
    {
        for (var i = 0; i < s.Length;)
        {
            var cp = char.ConvertToUtf32(s, i);
            if (char.IsHighSurrogate(s[i]) && (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))) cp = s[i];
            i += cp > 0xFFFF ? 2 : 1;
            yield return cp;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // typing
    // ------------------------------------------------------------------------------------------------------------

    private void InsertTyped(int cp)
    {
        if (_arrowUsed) StopArrow();
        InsertTypedRaw(cp);
    }

    private void StopArrow()
    {
        // Vim starts a new undoable insert after cursor keys
        _arrowUsed = false;
        _bs.Undo.Synced = true;
        _insert!.Start = new TextPosition(_line, _col);
    }

    private void InsertTypedRaw(int cp)
    {
        if (!CanModify()) { return; }
        var s = CurLine;
        var text = CharStrings.Get(cp);
        if (cp is not (' ' or '\t')) _didAutoIndent = false;
        if (_mode == VimMode.Replace && _col < s.Length)
        {
            var len = VimText.CharLen(s, _col);
            _insert!.ReplaceStack.Push((_line, _col, s.Substring(_col, len)));
            ReplaceInLine(_line, _col, len, text);
        }
        else
        {
            if (_mode == VimMode.Replace) _insert!.ReplaceStack.Push((_line, _col, null));
            InsertInLine(_line, _col, text, note: !_insertBatch);
        }
        _insertBatch = _mode == VimMode.Insert && NextKeyIsPlainChar();
        _col += text.Length;
        _setCurswant = true;
        if (O.TextWidth > 0 && cp is not (' ' or '\t') && O.FoHas('t')) AutoWrap();
    }

    private void AutoWrap()
    {
        var tw = O.TextWidth;
        while (true)
        {
            var s = CurLine;
            if (VimText.VcolOf(s, _col, O.TabStop) <= tw) return;
            // find the last blank run whose left side fits within tw
            var brk = -1;
            for (var i = Math.Min(_col, s.Length) - 1; i > 0; i--)
            {
                if (s[i] is ' ' or '\t' && VimText.VcolOf(s, i, O.TabStop) <= tw)
                {
                    var j = i;
                    while (j > 0 && s[j - 1] is ' ' or '\t') j--;
                    if (j == VimText.FirstNonBlank(s)) { continue; }
                    brk = i;
                    break;
                }
            }
            if (brk < 0) return;
            var ws = brk;
            while (ws > 0 && s[ws - 1] is ' ' or '\t') ws--;
            var we = brk + 1;
            while (we < s.Length && s[we] is ' ' or '\t') we++;
            var indent = O.AutoIndent ? s.Substring(0, VimText.FirstNonBlank(s)) : "";
            var rest = s.Substring(we);
            var newCol = _col - we + indent.Length;
            ReplaceRange(new TextPosition(_line, ws), new TextPosition(_line, s.Length), "\n" + indent + rest);
            _line++;
            _col = newCol;
        }
    }

    private void InsertTab()
    {
        if (_arrowUsed) StopArrow();
        var sts = O.Sts;
        var s = CurLine;
        if (!O.ExpandTab && sts == 0 && !O.SmartTab) { InsertTypedRaw('\t'); _insertBatch = false; return; }
        var ts = sts > 0 ? sts : (O.SmartTab && VimText.FirstNonBlank(s) >= _col ? O.Sw : O.TabStop);
        var vcol = VimText.VcolOf(s, _col, O.TabStop);
        var want = ts - vcol % ts;
        if (O.ExpandTab) { for (var i = 0; i < want; i++) InsertTypedRaw(' '); _insertBatch = false; return; }
        // noexpandtab with softtabstop: use tabs where possible
        var target = vcol + want;
        // remove spaces before the cursor that can be merged, then insert tabs+spaces up to the target column
        var start = _col;
        while (start > 0 && s[start - 1] == ' ') start--;
        var startV = VimText.VcolOf(s, start, O.TabStop);
        var sb = new StringBuilder();
        var v = startV;
        while (true)
        {
            var nextTab = (v / O.TabStop + 1) * O.TabStop;
            if (nextTab <= target) { sb.Append('\t'); v = nextTab; } else break;
        }
        sb.Append(' ', target - v);
        if (sb.Length > 0 && start < _col) { ReplaceInLine(_line, start, _col - start, sb.ToString()); _col = start + sb.Length; }
        else foreach (var ch in sb.ToString()) InsertTypedRaw(ch);
        _insertBatch = false;
    }

    private void InsertShift(bool indent)
    {
        var s = CurLine;
        var before = s.Length;
        var wasWs = VimText.FirstNonBlank(s) >= _col;
        ShiftLine(_line, left: !indent, round: true, 1);
        var after = CurLine.Length;
        _col = Math.Max(0, _col + (after - before));
        _ = wasWs;
        _didAutoIndent = false;
    }

    private void InsertNewline()
    {
        if (_arrowUsed) StopArrow();
        InsertNewlineRaw();
    }

    private void InsertNewlineRaw()
    {
        if (!CanModify()) return;
        var s = CurLine;
        var col = Math.Min(_col, s.Length);
        var rest = s.Substring(col);
        var head = s.Substring(0, col);
        var indent = "";
        if (O.AutoIndent)
        {
            indent = s.Substring(0, VimText.FirstNonBlank(s));
            if (indent.Length > col) indent = indent.Substring(0, col);
            // If the line contained only the automatically inserted indent, it is removed.
            if (_didAutoIndent && VimText.IsBlank(head)) head = "";
            rest = rest.TrimStart(' ', '\t');
        }
        else if (_didAutoIndent && VimText.IsBlank(head)) head = "";
        if (_mode == VimMode.Replace && _insert is { } ins) ins.ReplaceStack.Push((_line, col, null));
        ReplaceRange(new TextPosition(_line, 0), new TextPosition(_line, s.Length), head + "\n" + indent + rest);
        NoteChange(_line, col);
        _line++;
        _col = indent.Length;
        _didAutoIndent = indent.Length > 0;
        _aiCol = indent.Length;
        _setCurswant = true;
    }

    private void UnrecordLastKey()
    {
        var st = _insert;
        if (st is { Replaying: false } && st.Typed.Count > 0) st.Typed.RemoveAt(st.Typed.Count - 1);
    }

    private void InsertBackspace(int mode)
    {
        if (_arrowUsed) StopArrow();
        var st = _insert!;
        var bs = O.Backspace;
        bool Can(string what) => bs == "2" || bs == "3" || OptionsHas(bs, what) || (bs == "1" && what is "indent" or "eol");
        var s = CurLine;
        if (_line == 0 && _col == 0) { Beep(); UnrecordLastKey(); return; }

        if (_col == 0)
        {
            if (!Can("eol")) { Beep(); return; }
            if (_mode == VimMode.Replace)
            {
                // in Replace mode a backspace at line start undoes a split or just moves up
                if (st.ReplaceStack.Count > 0 && st.ReplaceStack.Peek() is { Original: null } top && top.Line == _line - 1)
                {
                    st.ReplaceStack.Pop();
                    var prevLen = L(_line - 1).Length;
                    ReplaceRange(new TextPosition(_line - 1, prevLen), new TextPosition(_line, 0), "");
                    _line--; _col = prevLen;
                    return;
                }
                _line--; _col = L(_line).Length;
                return;
            }
            var pl = L(_line - 1).Length;
            if (st.Start.Line == _line) st.Start = new TextPosition(_line - 1, pl);
            if (st.StartOrig.Line == _line) st.StartOrig = new TextPosition(_line - 1, pl);
            ReplaceRange(new TextPosition(_line - 1, pl), new TextPosition(_line, 0), "");
            _line--;
            _col = pl;
            _didAutoIndent = false;
            return;
        }
        var startBefore = st.StartOrig.Line == _line && _col <= st.StartOrig.Character && !Can("start");
        if (startBefore) { Beep(); return; }

        if (_mode == VimMode.Replace)
        {
            // restore replaced characters; before the start of the replacement just move left
            var n = mode == 0 ? 1 : 0;
            if (mode != 0) { WordBackReplace(); return; }
            if (st.ReplaceStack.Count > 0 && st.ReplaceStack.Peek() is var (rl, _, orig) && rl == _line)
            {
                st.ReplaceStack.Pop();
                var prev = VimText.PrevCharStart(s, _col);
                if (orig is null) DeleteInLine(_line, prev, _col - prev);
                else ReplaceInLine(_line, prev, _col - prev, orig);
                _col = prev;
            }
            else _col = VimText.PrevCharStart(s, _col);
            _ = n;
            return;
        }

        var mincol = 0;
        if (mode == 2 && O.AutoIndent)
        {
            var fnb = VimText.FirstNonBlank(s);
            if (fnb < _col) mincol = fnb;
        }
        if (mode == 2 && st.StartOrig.Line == _line && st.StartOrig.Character < _col && !OptionsHas(bs, "nostop") && st.StartOrig.Character >= mincol) mincol = Math.Max(mincol, st.StartOrig.Character);
        int from;
        if (mode == 0)
        {
            from = VimText.PrevCharStart(s, _col);
            // softtabstop: delete spaces back to the previous soft tab stop
            if (O.Sts > 0 && s[_col - 1] == ' ' && VimText.FirstNonBlank(s) >= _col || (O.Sts > 0 && s[_col - 1] == ' ' && O.SmartTab && VimText.FirstNonBlank(s) >= _col))
            {
                var vcol = VimText.VcolOf(s, _col, O.TabStop);
                var target = (vcol - 1) / O.Sts * O.Sts;
                var i = _col;
                while (i > 0 && s[i - 1] == ' ' && VimText.VcolOf(s, i - 1, O.TabStop) >= target) i--;
                from = i;
            }
        }
        else if (mode == 1)
        {
            from = _col;
            var i = _col;
            var cls = -1;
            var inSpace = true;
            while (i > mincol)
            {
                var p = VimText.PrevCharStart(s, i);
                var c = VimText.CodePointAt(s, p);
                var isSpace = c is ' ' or '\t';
                var kw = O.Keywords.IsKeyword(c) ? 2 : 1;
                if (inSpace)
                {
                    if (!isSpace) { inSpace = false; cls = kw; }
                }
                else if (isSpace || kw != cls) break;
                i = p;
                if (st.StartOrig.Line == _line && !OptionsHas(bs, "nostop") && i == st.StartOrig.Character && i < _col && i > mincol) { break; }
            }
            from = i;
            if (from > _col) from = _col;
        }
        else
        {
            from = mincol;
            if (from >= _col) from = VimText.PrevCharStart(s, _col);
        }
        from = Math.Max(0, Math.Min(from, _col));
        if (from == _col) from = VimText.PrevCharStart(s, _col);
        if (mode != 0 && st.Start.Line == _line) st.Start = new TextPosition(_line, _col);
        DeleteInLine(_line, from, _col - from);
        _col = from;
        if (st.StartOrig.Line == _line && _col < st.StartOrig.Character) st.StartOrig = new TextPosition(_line, _col);
        _didAutoIndent = false;
    }

    private void WordBackReplace()
    {
        var st = _insert!;
        while (_col > 0 && st.ReplaceStack.Count > 0 && st.ReplaceStack.Peek().Line == _line)
        {
            var (_, _, orig) = st.ReplaceStack.Pop();
            var prev = VimText.PrevCharStart(CurLine, _col);
            if (orig is null) DeleteInLine(_line, prev, _col - prev); else ReplaceInLine(_line, prev, _col - prev, orig);
            _col = prev;
        }
    }

    private static bool OptionsHas(string list, string item) => list.Split(',').Contains(item);

    private void InsertDeleteChar()
    {
        if (_arrowUsed) StopArrow();
        var s = CurLine;
        if (_col >= s.Length)
        {
            if (_line >= LastLine) { Beep(); return; }
            if (!OptionsHas(O.Backspace, "eol") && O.Backspace is not ("2" or "3")) { Beep(); return; }
            ReplaceRange(new TextPosition(_line, s.Length), new TextPosition(_line + 1, 0), "");
            return;
        }
        DeleteInLine(_line, _col, VimText.CharLen(s, _col));
    }

    private void InsertCursorKey(int k, bool keepStart = false)
    {
        var s = CurLine;
        switch (k)
        {
            case Keys.Left:
                if (_col > 0) _col = VimText.PrevCharStart(s, _col);
                else if (_line > 0 && O.WhichWrapHas('[')) { _line--; _col = L(_line).Length; }
                else { Beep(); return; }
                break;
            case Keys.Right:
                if (_col < s.Length) _col += VimText.CharLen(s, _col);
                else if (_line < LastLine && O.WhichWrapHas(']')) { _line++; _col = 0; }
                else { Beep(); return; }
                break;
            case Keys.Up or Keys.Down:
            {
                UpdateCurswant();
                var tgt = _line + (k == Keys.Up ? -1 : 1);
                if (tgt < 0 || tgt > LastLine) { Beep(); return; }
                _line = tgt;
                Coladvance(_curswant);
                if (_col > CurLine.Length) _col = CurLine.Length;
                break;
            }
            case Keys.Home: _col = 0; _setCurswant = true; break;
            case Keys.End: _col = s.Length; _setCurswant = true; break;
            case Keys.PageUp or Keys.PageDown:
                ScrollCommand(k == Keys.PageUp ? 2 : 6, 1);
                break;
        }
        if (k is Keys.Left or Keys.Right) _setCurswant = true;
        if (!keepStart) { _arrowUsed = true; _insert!.Typed.Clear(); _bs.Undo.Synced = true; _redoBuilding = null; }
        _didAutoIndent = false;
    }

    private void InsertLastInserted(bool stop)
    {
        var last = _s.Registers.LastInserted;
        if (string.IsNullOrEmpty(last)) return;
        var keys = Keys.Decode(last).Where(kk => kk != Keys.Esc).ToArray();
        _insertBatch = false;
        _batchKeys = keys;
        for (_batchIdx = 0; _batchIdx < keys.Length; _batchIdx++) InsertKeyReplay(keys[_batchIdx]);
        _batchKeys = null;
        if (stop) InsertEscape(false);
    }

    private void InsertKeyReplay(int k)
    {
        var st = _insert!;
        var was = st.Replaying;
        st.Replaying = true;
        try { InsertKey(k); }
        finally { st.Replaying = was; }
        if (!was) st.Typed.Add(k);
    }

    private void InsertCopyFromAdjacent(int dir)
    {
        var tgt = _line + dir;
        if (tgt < 0 || tgt > LastLine) { FinishLiteral([]); Beep(); return; }
        var other = L(tgt);
        var vcol = VimText.VcolOf(CurLine, _col, O.TabStop);
        var idx = VimText.IndexOfVcol(other, vcol, O.TabStop, lastIfBeyond: false);
        if (idx >= other.Length) { FinishLiteral([]); Beep(); return; }
        var cp = VimText.CodePointAt(other, idx);
        FinishLiteral([cp]);
        InsertTypedRaw(cp);
    }

    private void InsertCtrlO()
    {
        var st = _insert!;
        st.AtEolBeforeCtrlO = _col >= CurLine.Length;
        st.OLine = _line;
        st.WasInsertMode = _mode == VimMode.Insert;
        _restartEdit = true;
        SetMode(VimMode.InsertNormal);
        // like Esc: the cursor moves left when it sat on the end-of-line position
        if (_col > 0 && _col >= CurLine.Length) _col = VimText.PrevCharStart(CurLine, CurLine.Length);
        _arrowUsed = true;
        if (!st.Replaying) st.Typed.Clear();
        _bs.Undo.Synced = true;
        _redoBuilding = null;
    }

    private void ResumeInsert()
    {
        var st = _insert;
        if (st is null) { _restartEdit = false; return; }
        _restartEdit = false;
        SetMode(st.WasInsertMode ? VimMode.Insert : VimMode.Replace);
        UpdateCurswant();
        var s = CurLine;
        if (((st.AtEolBeforeCtrlO && _line == st.OLine) || _curswant > VimText.VcolOf(s, _col, O.TabStop)) && _col < s.Length)
        {
            var next = _col + VimText.CharLen(s, _col);
            if (next >= s.Length) _col = next;
        }
        st.AtEolBeforeCtrlO = false;
        st.Start = new TextPosition(_line, _col);
    }

    // ------------------------------------------------------------------------------------------------------------
    // leaving Insert mode
    // ------------------------------------------------------------------------------------------------------------

    private void InsertEscape(bool ctrlC, bool noMove = false, bool toNormal = false)
    {
        var st = _insert!;
        // [count]: repeat what was typed (for o/O each repetition opens a new line)
        if (st.Count > 1 && !ctrlC && !st.Replaying)
        {
            var count = st.Count;
            st.Count = 1;
            var typed = st.Typed.ToArray();
            var wasRep = st.Replaying;
            st.Replaying = true;
            try
            {
                for (var i = 1; i < count; i++)
                {
                    if (st.Cmd is 'o' or 'O') InsertNewlineRaw();
                    _insertBatch = false;
                    _batchKeys = typed;
                    for (_batchIdx = 0; _batchIdx < typed.Length; _batchIdx++) InsertKey(typed[_batchIdx]);
                    _batchKeys = null;
                }
            }
            finally { st.Replaying = wasRep; }
        }
        // remove an auto-inserted indent that was never used
        if (_didAutoIndent)
        {
            var s = CurLine;
            if (VimText.IsBlank(s) && s.Length > 0 && _col >= s.Length) { DeleteInLine(_line, 0, s.Length); _col = 0; }
            _didAutoIndent = false;
        }
        var end = new TextPosition(_line, _col);
        _bs.LastInsert = end;
        _bs.OpStart = st.Start;
        _bs.OpEnd = end;
        // remember the typed text for ". and Ctrl-A
        if (!ctrlC || st.Typed.Count > 0)
        {
            var sbk = new StringBuilder();
            foreach (var k in st.Typed) Keys.Encode(sbk, k == Keys.CR ? Keys.NL : k);
            _s.Registers.LastInserted = sbk.ToString();
        }
        // complete the change being recorded for `.`
        if (_redoBuilding is { } rb && !ctrlC)
        {
            foreach (var k in st.Typed) rb.Keys.Add(k);
            rb.Keys.Add(Keys.Esc);
        }
        _redoBuilding = null;
        var blockIns = _blockInsert;
        _insert = null;
        var wasRestart = _restartEdit;
        _restartEdit = false;
        _insertLiteral = 0;
        _arrowUsed = false;
        _mode = VimMode.Normal;
        if (!noMove && _col > 0) _col = VimText.PrevCharStart(CurLine, Math.Min(_col, CurLine.Length));
        _setCurswant = true;
        CheckCursor();
        if (blockIns is not null) FinishBlockInsert(end, 0);
        _ = wasRestart; _ = toNormal;
    }
}

/// <summary>A small RFC 1345 digraph table (the common Latin, punctuation and symbol digraphs).</summary>
internal static class Digraphs
{
    private static readonly Dictionary<(int, int), int> Table = Build();

    private static Dictionary<(int, int), int> Build()
    {
        var d = new Dictionary<(int, int), int>();
        void A(string pair, int cp) { d[(pair[0], pair[1])] = cp; }
        foreach (var (v, set) in new (char, string)[]
        {
            (':', "AÄ EË IÏ OÖ UÜ aä eë iï oö uü yÿ"), ('\'', "AÁ EÉ IÍ OÓ UÚ YÝ aá eé ií oó uú yý"), ('!', "AÀ EÈ IÌ OÒ UÙ aà eè iì oò uù"),
            ('>', "AÂ EÊ IÎ OÔ UÛ aâ eê iî oô uû"), ('?', "AÃ NÑ OÕ aã nñ oõ"), ('o', "AÅ aå"), (',', "CÇ cç"),
        })
            foreach (var pair in set.Split(' ')) A(pair[0].ToString() + v, pair[1]);
        A("ss", 0xDF); A("Co", 0xA9); A("Rg", 0xAE); A("Eu", 0x20AC); A("Pd", 0xA3); A("Ye", 0xA5); A("Ct", 0xA2); A("SE", 0xA7); A("DG", 0xB0);
        A("+-", 0xB1); A("12", 0xBD); A("14", 0xBC); A("34", 0xBE); A("MU", 0xB5); A("PI", 0x3C0); A("*X", 0xD7); A("-:", 0xF7); A("1S", 0xB9); A("2S", 0xB2); A("3S", 0xB3);
        A("->", 0x2192); A("<-", 0x2190); A("-!", 0x2191); A("-v", 0x2193); A("=>", 0x21D2); A("==", 0x21D4); A("!=", 0x2260); A("=<", 0x2264); A(">=", 0x2265); A("OK", 0x2713); A("XX", 0x2717);
        A("a*", 0x3B1); A("b*", 0x3B2); A("g*", 0x3B3); A("d*", 0x3B4); A("l*", 0x3BB); A("m*", 0x3BC); A("p*", 0x3C0); A("s*", 0x3C3); A("w*", 0x3C9); A("D*", 0x394); A("W*", 0x3A9); A("S*", 0x3A3);
        A("00", 0x221E); A("RT", 0x221A); A("FA", 0x2200); A("TE", 0x2203); A("In", 0x222B); A("Sb", 0xB7); A("..", 0x2025); A(",.", 0x2026); A("<<", 0xAB); A(">>", 0xBB); A("'6", 0x2018); A("'9", 0x2019); A("\"6", 0x201C); A("\"9", 0x201D);
        return d;
    }

    public static int Lookup(int a, int b)
    {
        if (Table.TryGetValue((a, b), out var cp)) return cp;
        if (Table.TryGetValue((b, a), out cp)) return cp;
        return b;
    }
}
