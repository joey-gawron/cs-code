using System.Text;
using CsCode.Core.Text;

namespace CsCode.Vim;

internal sealed class CmdLineState
{
    public char Prompt;
    public readonly StringBuilder Text = new();
    public int Cursor;
    public List<string> History = null!;
    public int HistIdx;
    public string HistPrefix = "";
    public int Count = 1;
    public Cmd? PendingOp;
    public TextPosition OpStart;
    public VimMode ReturnMode;
    public TextPosition SavedCursor;
    public int Literal;      // 0 none, 1 Ctrl-V, 2 Ctrl-R
    public bool FromVisual;
}

public sealed partial class VimEngine
{
    private CmdLineState? _cmdline;
    private readonly List<string> _exHistory = [];
    private readonly List<string> _searchHistory = [];

    private void OpenCommandLine(char prompt, string initial, int count, Cmd? pendingOp, TextPosition opStart, VimMode returnMode)
    {
        _cmdline = new CmdLineState
        {
            Prompt = prompt,
            Cursor = initial.Length,
            History = prompt == ':' ? _exHistory : _searchHistory,
            Count = count,
            PendingOp = pendingOp,
            OpStart = opStart,
            ReturnMode = returnMode,
            SavedCursor = new TextPosition(_line, _col),
        };
        _cmdline.Text.Append(initial);
        _cmdline.HistIdx = _cmdline.History.Count;
        _modeBeforeCmdline = _mode;
        _mode = VimMode.CommandLine;
        PublishCommandLine();
    }

    private VimMode _modeBeforeCmdline;

    private void PublishCommandLine()
    {
        var c = _cmdline;
        if (c is null) { _host?.OnCommandLineChanged(new VimCommandLine(false, ':', "", 0)); return; }
        _host?.OnCommandLineChanged(new VimCommandLine(true, c.Prompt, c.Text.ToString(), c.Cursor));
    }

    private void StartExFromNormal(int count0)
    {
        var init = "";
        if (count0 > 0) init = count0 == 1 ? "." : $".,.+{count0 - 1}";
        OpenCommandLine(':', init, 1, null, default, VimMode.Normal);
    }

    private void StartExFromVisual()
    {
        // leave Visual mode (setting '< '>) and pre-fill the range; the cursor goes to the start of the area
        var a = _visualStart;
        var b = new TextPosition(_line, _col);
        EndVisualKeepRestart();
        var lo = a < b ? a : b;
        _line = lo.Line; _col = lo.Character;
        CheckCursor();
        OpenCommandLine(':', "'<,'>", 1, null, default, VimMode.Normal);
    }

    private void EndVisualKeepRestart()
    {
        var restart = _restartEdit;
        _restartEdit = false;
        EndVisual();
        _restartEdit = restart;
    }

    private void StartSearchCmdline(bool forward, int count, Cmd? pendingOp, TextPosition? opStart)
    {
        var ret = VisualActive ? _mode : VimMode.Normal;
        OpenCommandLine(forward ? '/' : '?', "", count, pendingOp, opStart ?? new TextPosition(_line, _col), ret);
        _cmdline!.FromVisual = VisualActive;
        if (VisualActive) _modeBeforeCmdline = _mode;
    }

    private void CancelCommandLine()
    {
        if (_cmdline is null) return;
        var c = _cmdline;
        _cmdline = null;
        _searchPendingOp = false;
        _opCmdForSearch = null;
        _mode = c.ReturnMode == VimMode.CommandLine ? VimMode.Normal : c.ReturnMode;
        _line = c.SavedCursor.Line; _col = c.SavedCursor.Character;
        if (_modeBeforeCmdline is VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock && c.Prompt != ':') _mode = _modeBeforeCmdline;
        CheckCursor();
        PublishCommandLine();
        UpdateSearchHighlight(incremental: false);
        if (_restartEdit && _insert is not null && _mode == VimMode.Normal) ResumeInsert();
    }

    private void CmdLineKey(int k)
    {
        var c = _cmdline;
        if (c is null) { _mode = VimMode.Normal; return; }
        var t = c.Text;
        if (c.Literal == 1)
        {
            c.Literal = 0; _noMap--;
            if (k is >= 0 and < 0x110000) { t.Insert(c.Cursor, Keys.SafeString(k)); c.Cursor += k > 0xFFFF ? 2 : 1; }
            PublishCommandLine();
            return;
        }
        if (c.Literal == 2)
        {
            c.Literal = 0; _noMap--;
            var text = RegisterTextForCmdline(k);
            if (text is not null) { t.Insert(c.Cursor, text); c.Cursor += text.Length; }
            PublishCommandLine();
            return;
        }
        switch (k)
        {
            case Keys.Esc or 3:
                CancelCommandLine();
                return;
            case Keys.CR or Keys.NL:
                ExecuteCommandLine();
                return;
            case Keys.BS or Keys.CtrlH:
                if (t.Length == 0) { CancelCommandLine(); return; }
                if (c.Cursor > 0) { var p = PrevCharIdx(t, c.Cursor); t.Remove(p, c.Cursor - p); c.Cursor = p; }
                break;
            case Keys.Delete:
                if (c.Cursor < t.Length) t.Remove(c.Cursor, 1);
                else if (c.Cursor > 0) { var p = PrevCharIdx(t, c.Cursor); t.Remove(p, c.Cursor - p); c.Cursor = p; }
                break;
            case 23: // Ctrl-W
            {
                var i = c.Cursor;
                while (i > 0 && t[i - 1] is ' ' or '\t') i--;
                if (i > 0)
                {
                    var kw = O.Keywords.IsKeyword(t[i - 1]);
                    while (i > 0 && !(t[i - 1] is ' ' or '\t') && O.Keywords.IsKeyword(t[i - 1]) == kw) i--;
                }
                t.Remove(i, c.Cursor - i);
                c.Cursor = i;
                break;
            }
            case 21:
                t.Remove(0, c.Cursor);
                c.Cursor = 0;
                break;
            case Keys.Left or 2 when k == Keys.Left || k == 2:
                if (c.Cursor > 0) c.Cursor = PrevCharIdx(t, c.Cursor);
                break;
            case Keys.Right:
                if (c.Cursor < t.Length) c.Cursor++;
                break;
            case Keys.Home or 2:
                c.Cursor = 0;
                break;
            case Keys.End or 5:
                c.Cursor = t.Length;
                break;
            case Keys.Up or Keys.Down or 16 or 14:
                HistoryStep(c, k is Keys.Up or 16 ? -1 : 1);
                break;
            case 22 or 17:
                c.Literal = 1;
                _noMap++;
                return;
            case 18:
                c.Literal = 2;
                _noMap++;
                return;
            case Keys.Tab:
                break;
            default:
                if (Keys.IsChar(k) && k >= 32) { var s = Keys.SafeString(k); t.Insert(c.Cursor, s); c.Cursor += s.Length; }
                else if (Keys.IsChar(k) && k < 32) { }
                break;
        }
        PublishCommandLine();
        if (c.Prompt != ':' && O.IncSearch) UpdateSearchHighlight(incremental: true, overridePattern: SplitSearchText(t.ToString(), c.Prompt).Pattern);
    }

    private static int PrevCharIdx(StringBuilder t, int idx)
    {
        var i = idx - 1;
        if (i > 0 && char.IsLowSurrogate(t[i]) && char.IsHighSurrogate(t[i - 1])) i--;
        return Math.Max(0, i);
    }

    private string? RegisterTextForCmdline(int reg)
    {
        if (reg == 23) // Ctrl-R Ctrl-W: word under cursor
        {
            var s = L(_cmdline!.SavedCursor.Line);
            var i = _cmdline.SavedCursor.Character;
            while (i < s.Length && !O.Keywords.IsKeyword(s[i])) i++;
            var st = i;
            while (st > 0 && O.Keywords.IsKeyword(s[st - 1])) st--;
            var e = i;
            while (e < s.Length && O.Keywords.IsKeyword(s[e])) e++;
            return s.Substring(st, e - st);
        }
        var r = _s.Registers.ForRead(reg == '"' ? 0 : reg, ReadSystemRegister);
        if (r is null || r.IsEmpty) return null;
        var text = r.ToText();
        if (text.EndsWith('\n')) text = text[..^1];
        return text.Replace('\n', '\r');
    }

    private void HistoryStep(CmdLineState c, int dir)
    {
        var h = c.History;
        if (h.Count == 0) return;
        if (c.HistIdx == h.Count) c.HistPrefix = c.Text.ToString();
        var idx = c.HistIdx;
        while (true)
        {
            idx += dir;
            if (idx < 0) return;
            if (idx >= h.Count) { c.HistIdx = h.Count; c.Text.Clear().Append(c.HistPrefix); c.Cursor = c.Text.Length; return; }
            if (h[idx].StartsWith(c.HistPrefix, StringComparison.Ordinal)) break;
        }
        c.HistIdx = idx;
        c.Text.Clear().Append(h[idx]);
        c.Cursor = c.Text.Length;
    }

    private void AddHistory(List<string> h, string text)
    {
        if (text.Length == 0) return;
        h.Remove(text);
        h.Add(text);
        var max = Math.Max(1, O.History);
        if (h.Count > max) h.RemoveRange(0, h.Count - max);
    }

    private void ExecuteCommandLine()
    {
        var c = _cmdline!;
        var text = c.Text.ToString();
        _cmdline = null;
        PublishCommandLine();
        var returnMode = c.ReturnMode;
        _mode = returnMode == VimMode.CommandLine ? VimMode.Normal : returnMode;
        if (c.Prompt == ':')
        {
            AddHistory(_exHistory, text);
            if (_mode is VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock) EndVisual();
            if (text.Trim().Length > 0) ExecuteExLine(text);
            // the last command line is remembered when the command has finished (a ':s///c' prompt keeps it running)
            if (_awaitKey) _deferredLastCmd = text; else _s.Registers.LastCommandLine = text;
            if (_mode == VimMode.Normal) CheckCursor();
            if (_restartEdit && _insert is not null && _mode == VimMode.Normal) ResumeInsert();
            return;
        }
        // / or ?
        AddHistory(_searchHistory, text);
        var forward = c.Prompt == '/';
        var pending = c.PendingOp;
        _searchPendingOp = false;
        _opCmdForSearch = null;
        if (pending is not null)
        {
            var oap = _oap;
            oap.ResetForMotion(pending.Op, pending.Reg);
            oap.Start = c.OpStart;
            _line = c.SavedCursor.Line; _col = c.SavedCursor.Character;
            oap.Type = MType.Char;
            oap.UseRegOne = true;
            var count0 = EffCount(pending, pending.M);
            var ok = DoSearchText(text, forward, c.Count, oap);
            if (!ok) { _line = c.SavedCursor.Line; _col = c.SavedCursor.Character; Beep(); CheckCursor(); return; }
            oap.Force = pending.Force;
            // the typed pattern is part of the redo record
            pending.M.Key = forward ? '/' : '?';
            DoPendingOperatorWithSearchText(pending, oap, count0, text);
            CheckPcmark();
            FinishCommand();
            return;
        }
        // plain search motion (in Normal or Visual mode)
        var oap2 = _oap;
        oap2.ResetForMotion(OpType.None, 0);
        _line = c.SavedCursor.Line; _col = c.SavedCursor.Character;
        var before = new TextPosition(_line, _col);
        if (!DoSearchText(text, forward, c.Count, oap2)) { _line = before.Line; _col = before.Character; CheckCursor(); return; }
        CheckPcmark();
        if (!VisualActive) CheckCursor();
    }

    private void DoPendingOperatorWithSearchText(Cmd pending, OpArgs oap, int count0, string text)
    {
        DoPendingOperator(pending, oap, count0);
        // append the search text + <CR> to the redo record just created
        if (_redo is { } r && r.Keys.Count > 0)
        {
            var idx = r.Keys.IndexOf(-1);
            if (idx >= 0)
            {
                r.Keys.RemoveAt(idx);
                var ins = new List<int>();
                foreach (var cp in CodePoints(text)) ins.Add(cp);
                ins.Add(Keys.CR);
                r.Keys.InsertRange(idx, ins);
            }
        }
    }
}
