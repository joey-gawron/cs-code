using System.Globalization;
using System.Text;
using CsCode.Core.Text;

namespace CsCode.Vim;

internal sealed class ExCmd
{
    public string Name = "";
    public bool Bang;
    public string Args = "";
    public int Line1, Line2;
    public int AddrCount;
    public bool HasRange => AddrCount > 0;
}

public sealed partial class VimEngine
{
    private static readonly (string Full, int Min)[] ExTable =
    [
        ("append", 1), ("buffer", 1), ("bNext", 2), ("bdelete", 2), ("bfirst", 2), ("blast", 2), ("bnext", 2), ("bprevious", 2), ("brewind", 2), ("buffers", 7), ("bwipeout", 2),
        ("copy", 2), ("close", 3), ("center", 2), ("changes", 7), ("cquit", 2), ("delete", 1), ("delmarks", 4), ("display", 2), ("digraphs", 3), ("edit", 1), ("earlier", 2),
        ("echo", 2), ("enew", 3), ("exit", 3), ("files", 5), ("global", 1), ("goto", 2), ("help", 1), ("history", 3), ("join", 1), ("jumps", 2), ("k", 1), ("keepjumps", 5),
        ("keepmarks", 3), ("left", 2), ("later", 3), ("let", 3), ("ls", 2), ("move", 1), ("mark", 2), ("marks", 5), ("mapclear", 4), ("map", 3), ("new", 3), ("nohlsearch", 3),
        ("normal", 4), ("number", 2), ("only", 2), ("print", 1), ("put", 2), ("pwd", 2), ("quit", 1), ("quitall", 5), ("qall", 2), ("read", 1), ("redo", 3), ("registers", 3),
        ("retab", 3), ("right", 2), ("substitute", 1), ("set", 2), ("setlocal", 4), ("setglobal", 4), ("split", 2), ("source", 2), ("saveas", 3), ("startinsert", 4),
        ("stopinsert", 5), ("sort", 3), ("silent", 3), ("t", 1), ("tabnext", 4), ("tabprevious", 4), ("tabnew", 7), ("tabclose", 4), ("undo", 1), ("unmap", 3), ("vglobal", 1),
        ("vsplit", 2), ("vnew", 3), ("write", 1), ("wq", 2), ("wall", 2), ("wqall", 3), ("wincmd", 4), ("xit", 1), ("xall", 2), ("yank", 1), ("update", 2), ("unlet", 3),
        ("syntax", 2), ("filetype", 5), ("colorscheme", 4), ("highlight", 2), ("autocmd", 2), ("augroup", 3), ("command", 3), ("set", 2), ("noautocmd", 3), ("redraw", 4),
        ("fold", 2), ("foldopen", 5), ("foldclose", 5), ("echomsg", 5), ("echoerr", 5), ("execute", 3), ("runtime", 2), ("language", 3), ("messages", 3), ("version", 2),
        ("stop", 2), ("suspend", 3), ("cd", 2), ("lcd", 2), ("tcd", 3), ("nunmap", 3), ("registers", 3),
    ];

    private static readonly Dictionary<string, (MapModes Modes, bool NoRemap, bool Unmap, bool Clear, bool BangForm)> MapNames = BuildMapNames();

    private static Dictionary<string, (MapModes, bool, bool, bool, bool)> BuildMapNames()
    {
        var d = new Dictionary<string, (MapModes, bool, bool, bool, bool)>();
        void Add(string[] names, MapModes m, bool nore, bool unmap, bool clear) { foreach (var n in names) d[n] = (m, nore, unmap, clear, false); }
        Add(["map"], MapModes.All, false, false, false);
        Add(["nm", "nma", "nmap"], MapModes.Normal, false, false, false);
        Add(["vm", "vma", "vmap"], MapModes.Visual | MapModes.Select, false, false, false);
        Add(["xm", "xma", "xmap"], MapModes.Visual, false, false, false);
        Add(["sm", "sma", "smap"], MapModes.Select, false, false, false);
        Add(["om", "oma", "omap"], MapModes.OperatorPending, false, false, false);
        Add(["im", "ima", "imap"], MapModes.Insert, false, false, false);
        Add(["cm", "cma", "cmap"], MapModes.CommandLine, false, false, false);
        Add(["no", "nor", "nore", "norem", "noremap"], MapModes.All, true, false, false);
        Add(["nn", "nno", "nnor", "nnore", "nnorem", "nnoremap"], MapModes.Normal, true, false, false);
        Add(["vn", "vno", "vnor", "vnore", "vnorem", "vnoremap"], MapModes.Visual | MapModes.Select, true, false, false);
        Add(["xn", "xno", "xnor", "xnore", "xnorem", "xnoremap"], MapModes.Visual, true, false, false);
        Add(["snor", "snore", "snorem", "snoremap"], MapModes.Select, true, false, false);
        Add(["ono", "onor", "onore", "onorem", "onoremap"], MapModes.OperatorPending, true, false, false);
        Add(["ino", "inor", "inore", "inorem", "inoremap"], MapModes.Insert, true, false, false);
        Add(["cno", "cnor", "cnore", "cnorem", "cnoremap"], MapModes.CommandLine, true, false, false);
        Add(["unm", "unma", "unmap"], MapModes.All, false, true, false);
        Add(["nun", "nunm", "nunma", "nunmap"], MapModes.Normal, false, true, false);
        Add(["vu", "vun", "vunm", "vunma", "vunmap"], MapModes.Visual | MapModes.Select, false, true, false);
        Add(["xu", "xun", "xunm", "xunma", "xunmap"], MapModes.Visual, false, true, false);
        Add(["sunm", "sunma", "sunmap"], MapModes.Select, false, true, false);
        Add(["ou", "oun", "ounm", "ounma", "ounmap"], MapModes.OperatorPending, false, true, false);
        Add(["iu", "iun", "iunm", "iunma", "iunmap"], MapModes.Insert, false, true, false);
        Add(["cu", "cun", "cunm", "cunma", "cunmap"], MapModes.CommandLine, false, true, false);
        Add(["mapc", "mapcl", "mapcle", "mapclea", "mapclear"], MapModes.All, false, false, true);
        Add(["nmapc", "nmapclear"], MapModes.Normal, false, false, true);
        Add(["vmapc", "vmapclear"], MapModes.Visual | MapModes.Select, false, false, true);
        Add(["xmapc", "xmapclear"], MapModes.Visual, false, false, true);
        Add(["omapc", "omapclear"], MapModes.OperatorPending, false, false, true);
        Add(["imapc", "imapclear"], MapModes.Insert, false, false, true);
        Add(["cmapc", "cmapclear"], MapModes.CommandLine, false, false, true);
        return d;
    }

    private static string? ResolveExName(string typed)
    {
        if (typed.Length == 0) return null;
        foreach (var (full, min) in ExTable)
            if (typed.Length >= min && full.StartsWith(typed, StringComparison.Ordinal)) return full;
        return null;
    }

    // ------------------------------------------------------------------------------------------------------------
    // entry points
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Executes Ex command text (one or several commands separated by '|'). Returns false if an error was reported.</summary>
    public bool ExecuteEx(string text)
    {
        var errors = LastErrorCount;
        ExecuteExLine(text);
        Drain();
        FlushHost();
        return LastErrorCount == errors;
    }

    private int LastErrorCount;

    private void ExecuteExLine(string line)
    {
        var before = _exErrors;
        var i = 0;
        while (i <= line.Length)
        {
            // split at an unescaped '|' unless the command owns the rest of the line
            var cmdText = line[i..];
            var (head, rest) = SplitBar(cmdText);
            var single = head;
            var ok = ExecuteSingleEx(single, rest, out var consumedAll);
            if (!ok || consumedAll || rest is null) break;
            i = line.Length - rest.Length;
            if (_macroAborted && false) break;
        }
        if (_exErrors != before) LastErrorCount++;
    }

    private int _exErrors;

    private void ExError(string msg)
    {
        _exErrors++;
        Error(msg);
    }

    private static (string Head, string? Tail) SplitBar(string s)
    {
        // commands that take the rest of the line (:g, :normal) are detected by the caller; here find the first unescaped '|'
        var t = s.TrimStart(' ', '\t', ':');
        var lead = s.Length - t.Length;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { i++; continue; }
            if (s[i] == '\u0016' && i + 1 < s.Length) { i++; continue; }
            if (s[i] == '|')
            {
                var name = ReadName(t);
                if (name is "g" or "gl" or "glo" or "glob" or "globa" or "global" or "v" or "vg" or "vgl" or "vglo" or "vglob" or "vgloba" or "vglobal" or "norm" or "norma" or "normal") return (s, null);
                return (s[..i], s[(i + 1)..]);
            }
        }
        _ = lead;
        return (s, null);
    }

    private static string ReadName(string t)
    {
        // skip a range, then read letters
        var i = 0;
        while (i < t.Length && (char.IsDigit(t[i]) || t[i] is '.' or '$' or '%' or ',' or ';' or '+' or '-' or ' ' or '\'' or '<' or '>')) i++;
        var s = i;
        while (i < t.Length && char.IsLetter(t[i])) i++;
        return t[s..i];
    }

    // ------------------------------------------------------------------------------------------------------------
    // parsing
    // ------------------------------------------------------------------------------------------------------------

    private bool ExecuteSingleEx(string text, string? rest, out bool consumedAll)
    {
        consumedAll = false;
        var s = text.TrimStart(' ', '\t', ':');
        if (s.Length == 0) return true;
        if (s[0] == '"') return true; // comment
        var cmd = new ExCmd();
        var i = 0;
        if (!ParseRange(s, ref i, cmd)) return false;
        while (i < s.Length && s[i] is ' ' or '\t') i++;
        // command name
        var nameStart = i;
        string name;
        if (i < s.Length && char.IsLetter(s[i]))
        {
            var j = i;
            // :s, :g, :v and :k may be followed directly by their argument delimiter
            while (j < s.Length && char.IsLetter(s[j])) j++;
            name = s[i..j];
            if (name.Length > 1 && name[0] == 's' && ResolveExName(name) is null && !MapNames.ContainsKey(name) && IsSubstituteFlagWord(name)) { name = "s"; j = i + 1; }
            else if (name.Length > 1 && name[0] == 'k' && ResolveExName(name) is null && name != "keepjumps") { name = "k"; j = i + 1; }
            i = j;
        }
        else if (i < s.Length && s[i] is '&' or '~' or '<' or '>' or '@' or '*' or '=' or '!' or '#')
        {
            if (s[i] == '&' && i + 1 < s.Length && s[i + 1] == '&') { name = "&&"; i += 2; }
            else { name = s[i].ToString(); i++; }
        }
        else if (i >= s.Length)
        {
            // just a range: go to the line
            if (cmd.HasRange) { GotoLineEx(cmd.Line2); return true; }
            return true;
        }
        else { ExError($"E492: Not an editor command: {s}"); return false; }
        var nameEnd = i;
        // bang
        if (i < s.Length && s[i] == '!' && name != "!" && name != "&&") { cmd.Bang = true; i++; }
        var args = s[i..];
        cmd.Name = name;
        cmd.Args = args.TrimStart(' ', '\t');
        _ = nameStart; _ = nameEnd;
        if (name is "g" or "global" or "v" or "vglobal" || ResolveExName(name) is "global" or "vglobal" or "normal") consumedAll = true;
        var full = MapNames.ContainsKey(name) ? name : ResolveExName(name) ?? name;
        if (full is "global" or "vglobal" or "normal") consumedAll = true;
        // "args|more" for commands that stop at the bar was split by the caller; for consumedAll commands re-attach the rest
        if (consumedAll && rest is not null) cmd.Args += "|" + rest;
        return Dispatch(cmd, full);
    }

    private static bool IsSubstituteFlagWord(string name)
    {
        // :sg :s& and similar short forms: only treat as :s + flags when every following letter is a substitute flag
        for (var i = 1; i < name.Length; i++) if ("&cegiInp#lr".IndexOf(name[i]) < 0) return false;
        return true;
    }

    private bool ParseRange(string s, ref int i, ExCmd cmd)
    {
        cmd.Line1 = cmd.Line2 = _line;
        var count = 0;
        var line = _line;
        var haveFirst = false;
        while (true)
        {
            while (i < s.Length && s[i] is ' ' or '\t') i++;
            if (i >= s.Length) break;
            var c = s[i];
            int? addr = null;
            if (c == '%') { i++; cmd.Line1 = 0; cmd.Line2 = LastLine; count = 2; haveFirst = true; goto next; }
            if (c == '*') { i++; var a = GetMark('<'); var b = GetMark('>'); if (a is null || b is null) { ExError("E20: Mark not set"); return false; } cmd.Line1 = a.Value.Line; cmd.Line2 = b.Value.Line; count = 2; haveFirst = true; goto next; }
            if (c == '.') { i++; addr = _line; }
            else if (c == '$') { i++; addr = LastLine; }
            else if (c == '\'')
            {
                if (i + 1 >= s.Length) { ExError("E20: Mark not set"); return false; }
                var m = GetMark(s[i + 1]);
                if (m is null) { ExError("E20: Mark not set"); return false; }
                addr = m.Value.Line; i += 2;
            }
            else if (char.IsDigit(c))
            {
                var st = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                addr = int.Parse(s.AsSpan(st, i - st), CultureInfo.InvariantCulture) - 1;
            }
            else if (c is '/' or '?')
            {
                var delim = c;
                var end = i + 1;
                while (end < s.Length && s[end] != delim) { if (s[end] == '\\') end++; end++; }
                var pat = s.Substring(i + 1, Math.Min(end, s.Length) - i - 1);
                i = Math.Min(end + 1, s.Length);
                var found = FindLineForPattern(pat, delim == '/', haveFirst ? line : _line);
                if (found is null) return false;
                addr = found;
            }
            else if (c == '\\' && i + 1 < s.Length && s[i + 1] is '/' or '?' or '&')
            {
                var kind = s[i + 1]; i += 2;
                var pat = kind == '&' ? _s.Search.SubPattern : _s.Search.Pattern;
                if (pat.Length == 0) { ExError("E35: No previous regular expression"); return false; }
                var found = FindLineForPattern(pat, kind != '?', haveFirst ? line : _line);
                if (found is null) return false;
                addr = found;
            }
            else if (c is '+' or '-') addr = _line; // relative to the cursor line (or previous address)
            if (addr is null) break;
            var value = addr.Value;
            if (c is '+' or '-' && count > 0 && haveFirst) value = line;
            // trailing +n / -n
            while (i < s.Length && s[i] is '+' or '-' or ' ')
            {
                if (s[i] == ' ') { i++; continue; }
                var sign = s[i] == '-' ? -1 : 1;
                i++;
                var st = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                var n = i > st ? int.Parse(s.AsSpan(st, i - st), CultureInfo.InvariantCulture) : 1;
                value += sign * n;
            }
            // two addresses: the previous second becomes the first
            cmd.Line1 = count == 0 ? value : cmd.Line2;
            cmd.Line2 = value;
            if (count == 0) { cmd.Line1 = value; }
            count++;
            haveFirst = true;
            line = value;
        next:
            while (i < s.Length && s[i] is ' ' or '\t') i++;
            if (i < s.Length && s[i] is ',' or ';')
            {
                var semi = s[i] == ';';
                i++;
                if (semi && count > 0) _line = Math.Clamp(cmd.Line2, 0, LastLine);
                // a missing second address means the cursor line
                var save = cmd.Line2;
                if (count == 1) { cmd.Line1 = cmd.Line2; }
                else if (count >= 2) cmd.Line1 = cmd.Line2;
                // allow ",," and ",cmd": default the next to current
                while (i < s.Length && s[i] is ' ' or '\t') i++;
                if (i >= s.Length || !(char.IsDigit(s[i]) || s[i] is '.' or '$' or '\'' or '/' or '?' or '+' or '-' or '%' or '\\'))
                {
                    cmd.Line2 = _line;
                    count = Math.Max(count, 1) + 1;
                    break;
                }
                _ = save;
                // the next iteration reads the second address; remember the first
                var first = cmd.Line1;
                var haveSecond = false;
                var restCmd = new ExCmd();
                var j = i;
                // parse one more address by recursion on a fresh cmd
                if (!ParseRange(s, ref j, restCmd)) return false;
                i = j;
                cmd.Line1 = count >= 1 && restCmd.AddrCount == 0 ? first : first;
                cmd.Line2 = restCmd.AddrCount > 0 ? restCmd.Line2 : _line;
                haveSecond = true;
                count = 2;
                _ = haveSecond;
                break;
            }
            break;
        }
        if (count == 1) { cmd.Line1 = cmd.Line2; }
        cmd.AddrCount = count;
        if (count == 0) { cmd.Line1 = cmd.Line2 = _line; }
        if (count >= 2 && cmd.Line1 > cmd.Line2)
        {
            // backwards range: swap (Vim would ask for confirmation)
            (cmd.Line1, cmd.Line2) = (cmd.Line2, cmd.Line1);
        }
        return true;
    }

    private int? FindLineForPattern(string pat, bool forward, int fromLine)
    {
        if (pat.Length == 0) pat = _s.Search.Pattern;
        if (pat.Length == 0) { ExError("E35: No previous regular expression"); return null; }
        var p = CompilePattern(pat);
        if (p is null) return null;
        _s.Search.Pattern = pat;
        var hits = new List<Hit>();
        for (var step = 1; step <= LineCount; step++)
        {
            var l = forward ? fromLine + step : fromLine - step;
            if (l >= LineCount) { if (!O.WrapScan) break; l -= LineCount; }
            if (l < 0) { if (!O.WrapScan) break; l += LineCount; }
            MatchesInLine(p, l, hits);
            if (hits.Count > 0) return l;
        }
        ExError($"E486: Pattern not found: {pat}");
        return null;
    }

    private void GotoLineEx(int line)
    {
        _line = Math.Clamp(line, 0, LastLine);
        SetPcmark();
        Beginline(BL.Sol | BL.Fix);
    }

    // ------------------------------------------------------------------------------------------------------------
    // dispatch
    // ------------------------------------------------------------------------------------------------------------

    private bool CheckRangeValid(ExCmd c)
    {
        if (c.Line1 < 0 || c.Line2 > LastLine || c.Line1 > LastLine)
        {
            if (c.Line2 > LastLine || c.Line1 > LastLine) { ExError("E16: Invalid range"); return false; }
            c.Line1 = Math.Max(0, c.Line1);
        }
        return true;
    }

    private bool Dispatch(ExCmd c, string full)
    {
        if (MapNames.TryGetValue(c.Name, out var mapInfo)) return ExMap(c, mapInfo);
        if (c.Name is "&" or "&&" or "~") return ExSubstituteRepeat(c);
        switch (c.Name)
        {
            case "<" or ">": return ExShift(c);
            case "@" or "*": return ExAtRegister(c);
            case "=": Message((c.HasRange ? c.Line2 + 1 : LineCount).ToString()); return true;
            case "!" or "#": ExError("E319: The command is not available in this version (external commands are not supported)"); return false;
        }
        if (c.Name.Length == 0) return true;
        if (c.Name is "s" || full == "substitute") return ExSubstitute(c);
        if (full is null) { ExError($"E492: Not an editor command: {c.Name}"); return false; }
        var needValid = full is not ("k" or "mark" or "marks" or "set" or "setlocal" or "setglobal" or "let" or "echo" or "undo" or "redo" or "normal" or "source" or "registers" or "display" or "nohlsearch" or "wincmd");
        if (needValid && c.HasRange && c.Name != "put" && c.Name != "pu" && !CheckRangeValid(c)) return false;
        switch (full)
        {
            case "write" or "update": return HostAction(VimEditorAction.Write, c);
            case "wq" or "xit" or "exit": return HostAction(VimEditorAction.WriteQuit, c);
            case "wall": return HostAction(VimEditorAction.WriteAll, c);
            case "wqall" or "xall": return HostAction(VimEditorAction.WriteQuitAll, c);
            case "quit" or "close": return HostAction(full == "close" ? VimEditorAction.Close : VimEditorAction.Quit, c);
            case "quitall" or "qall" or "cquit": return HostAction(VimEditorAction.QuitAll, c);
            case "edit": return HostAction(c.Bang && c.Args.Length == 0 ? VimEditorAction.Reload : VimEditorAction.Edit, c);
            case "enew": return HostAction(VimEditorAction.NewBuffer, c);
            case "saveas": return HostAction(VimEditorAction.SaveAs, c);
            case "bnext": return HostAction(VimEditorAction.NextBuffer, c);
            case "bprevious" or "bNext": return HostAction(VimEditorAction.PreviousBuffer, c);
            case "bdelete" or "bwipeout": return HostAction(VimEditorAction.DeleteBuffer, c);
            case "ls" or "buffers" or "files": return HostAction(VimEditorAction.ListBuffers, c);
            case "buffer": return HostAction(c.Args.Length > 0 || c.HasRange ? VimEditorAction.GoToBuffer : VimEditorAction.AlternateBuffer, c);
            case "bfirst" or "brewind" or "blast": return HostAction(VimEditorAction.GoToBuffer, c);
            case "split": return HostAction(VimEditorAction.Split, c);
            case "vsplit": return HostAction(VimEditorAction.VSplit, c);
            case "new": return HostAction(VimEditorAction.NewSplit, c);
            case "vnew": return HostAction(VimEditorAction.NewVSplit, c);
            case "only": return HostAction(VimEditorAction.Only, c);
            case "help": return HostAction(VimEditorAction.Help, c);
            case "tabnext": return HostAction(VimEditorAction.NextTab, c);
            case "tabprevious": return HostAction(VimEditorAction.PreviousTab, c);
            case "tabnew": return HostAction(VimEditorAction.NewTab, c);
            case "tabclose": return HostAction(VimEditorAction.CloseTab, c);
            case "wincmd": return ExWincmd(c);
            case "delete": return ExDelete(c);
            case "yank": return ExYank(c);
            case "put": return ExPut(c);
            case "move": return ExMove(c);
            case "copy" or "t": return ExCopy(c);
            case "join": return ExJoin(c);
            case "normal": return ExNormal(c);
            case "global" or "vglobal": return ExGlobal(c, full == "vglobal" || c.Bang);
            case "nohlsearch": _s.Search.HighlightActive = false; Host.OnSearchHighlight(null); return true;
            case "registers" or "display": Message(_s.Registers.Describe(c.Args.Length > 0 ? c.Args.Replace(" ", "") : null)); return true;
            case "marks": Message(DescribeMarks()); return true;
            case "jumps": Message(DescribeJumps()); return true;
            case "changes": Message(string.Join("\n", _bs.ChangeList.Select(p => $"  {p.Line + 1} {p.Character}"))); return true;
            case "undo": return ExUndo(c);
            case "redo": RedoCommand(1); return true;
            case "earlier" or "later": return ExEarlierLater(c, full == "earlier");
            case "set" or "setlocal" or "setglobal": return ExSet(c.Args);
            case "let": return ExLet(c.Args);
            case "unlet": return true;
            case "echo" or "echomsg" or "echoerr": return ExEcho(c, full);
            case "execute": return ExExecute(c);
            case "source": return ExSource(c);
            case "mark" or "k": return ExMark(c);
            case "delmarks": return ExDelMarks(c);
            case "goto": GotoByte(Math.Max(1, c.HasRange ? c.Line2 + 1 : ParseIntOr(c.Args, 1))); return true;
            case "print" or "number": Message(L(Math.Clamp(c.Line2, 0, LastLine))); _line = Math.Clamp(c.Line2, 0, LastLine); return true;
            case "left" or "right" or "center": return ExAlign(c, full);
            case "retab": return ExRetab(c);
            case "sort": return ExSort(c);
            case "startinsert":
                if (_mode == VimMode.Normal && !VisualActive)
                {
                    if (c.Bang) _col = CurLine.Length;
                    StartInsert('i', 1, false);
                }
                return true;
            case "stopinsert": if (_insert is not null) InsertEscape(false); return true;
            case "read": ExError("E-read: :read is not supported (file access is the host's job)"); return false;
            case "fold": Host.FoldCommand(VimFoldCommand.Create, c.Line1, c.Line2); return true;
            case "foldopen": Host.FoldCommand(VimFoldCommand.Open, c.Line1, c.Line2); return true;
            case "foldclose": Host.FoldCommand(VimFoldCommand.Close, c.Line1, c.Line2); return true;
            case "history" or "digraphs" or "messages" or "version" or "pwd": return true;
            case "syntax" or "filetype" or "colorscheme" or "highlight" or "autocmd" or "augroup" or "command" or "noautocmd" or "redraw" or "runtime" or "language" or "stop" or "suspend" or "cd" or "lcd" or "tcd" or "append" or "keepmarks":
                return true;
            case "silent" or "keepjumps":
            {
                var saved = _noJumpList;
                if (full == "keepjumps") _noJumpList = true;
                try { ExecuteExLine(c.Args); } finally { _noJumpList = saved; }
                return true;
            }
        }
        ExError($"E492: Not an editor command: {c.Name}");
        return false;
    }

    private static int ParseIntOr(string s, int d) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;

    private bool HostAction(VimEditorAction action, ExCmd c)
    {
        var count = c.HasRange ? c.Line2 + 1 : (int?)null;
        var arg = c.Args.Length > 0 ? c.Args : null;
        if (!Host.PerformAction(action, new VimActionArgs(arg, c.Bang, count)))
        {
            ExError($"E-host: this host does not support :{c.Name}");
            return false;
        }
        return true;
    }

    private bool ExWincmd(ExCmd c)
    {
        var a = c.Args.Trim();
        if (a.Length == 0) { ExError("E471: Argument required"); return false; }
        var cmd = new Cmd { Key = 23, Key2 = a[0], Count0 = c.HasRange ? c.Line2 + 1 : 0 };
        WindowCommand(cmd);
        return true;
    }

    private string DescribeMarks()
    {
        var sb = new StringBuilder("mark line  col file/text");
        for (var i = 0; i < 26; i++) if (_bs.Marks[i] is { } p) sb.Append($"\n {(char)('a' + i)}   {p.Line + 1,5} {p.Character,4} {L(Math.Min(p.Line, LastLine))}");
        return sb.ToString();
    }

    private string DescribeJumps()
    {
        var sb = new StringBuilder(" jump line  col file/text");
        for (var i = 0; i < _jumps.Count; i++) sb.Append($"\n {Math.Abs(_jumpIdx - i),3} {_jumps[i].Pos.Line + 1,5} {_jumps[i].Pos.Character,4}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------------------------
    // line commands
    // ------------------------------------------------------------------------------------------------------------

    private (int Reg, int Count, bool ok) ParseRegCount(ExCmd c, out string rest)
    {
        var a = c.Args.TrimStart();
        var reg = 0;
        var count = 0;
        rest = a;
        if (a.Length > 0 && !char.IsDigit(a[0]) && RegisterFile.IsValidName(a[0]) && a[0] != '|') { reg = a[0]; a = a[1..].TrimStart(); }
        if (a.Length > 0 && char.IsDigit(a[0]))
        {
            var i = 0;
            while (i < a.Length && char.IsDigit(a[i])) i++;
            count = int.Parse(a.AsSpan(0, i), CultureInfo.InvariantCulture);
            a = a[i..];
        }
        rest = a;
        return (reg, count, true);
    }

    private bool ExDelete(ExCmd c)
    {
        if (!CanModify()) return false;
        var (reg, count, _) = ParseRegCount(c, out _);
        var l1 = c.Line1; var l2 = c.Line2;
        if (count > 0) { l1 = l2; l2 = Math.Min(LastLine, l1 + count - 1); }
        var oap = _oap;
        oap.ResetForMotion(OpType.Delete, reg);
        oap.Type = MType.Line;
        oap.Start = new TextPosition(l1, 0);
        oap.End = new TextPosition(l2, 0);
        oap.LineCount = l2 - l1 + 1;
        _line = l1; _col = 0;
        OpDelete(oap);
        _line = Math.Min(l1, LastLine);
        Beginline(BL.Sol | BL.Fix);
        return true;
    }

    private bool ExYank(ExCmd c)
    {
        var (reg, count, _) = ParseRegCount(c, out _);
        var l1 = c.Line1; var l2 = c.Line2;
        if (count > 0) { l1 = l2; l2 = Math.Min(LastLine, l1 + count - 1); }
        var oap = _oap;
        oap.ResetForMotion(OpType.Yank, reg);
        oap.Type = MType.Line;
        oap.Start = new TextPosition(l1, 0);
        oap.End = new TextPosition(l2, 0);
        oap.LineCount = l2 - l1 + 1;
        OpYank(oap, deleting: false);
        return true;
    }

    private bool ExPut(ExCmd c)
    {
        if (!CanModify()) return false;
        var reg = 0;
        var a = c.Args.Trim();
        if (a.Length > 0) reg = a[0];
        var line = c.HasRange ? c.Line2 : _line;
        var above = c.Bang;
        if (c.HasRange && c.Line2 < 0)
        {
            // :0put puts above the first line
            line = 0; above = true;
        }
        if (c.AddrCount > 0 && c.Line2 == -1) { line = 0; above = true; }
        _line = Math.Clamp(line, 0, LastLine);
        return DoPut(reg, !above, 1, PutFlags.Line | PutFlags.CursLine);
    }

    private bool ExMove(ExCmd c)
    {
        if (!CanModify()) return false;
        var s = c.Args.Trim();
        var j = 0;
        var dc = new ExCmd();
        if (!ParseRange(s, ref j, dc) || dc.AddrCount == 0) { ExError("E14: Invalid address"); return false; }
        var dest = dc.Line2;
        if (dest < -1 || dest > LastLine) { ExError("E16: Invalid range"); return false; }
        if (dest >= c.Line1 && dest < c.Line2) { ExError("E134: Cannot move a range of lines into itself"); return false; }
        DoMove(c.Line1, c.Line2, dest);
        return true;
    }

    /// <summary>Moves lines [l1,l2] to after line <paramref name="dest"/> (-1 = top). Cursor ends on the last moved line.</summary>
    private void DoMove(int l1, int l2, int dest)
    {
        if (dest >= l1 && dest <= l2) { _line = l2; Beginline(BL.Sol | BL.Fix); return; }
        var count = l2 - l1 + 1;
        var lines = new string[count];
        for (var i = 0; i < count; i++) lines[i] = L(l1 + i);
        // marks on the moved lines travel with them
        var moved = new List<(int idx, TextPosition?[] m)>();
        SaveUndo(Math.Min(l1, dest + 1), Math.Abs(dest - l1) + count + (dest > l2 ? 0 : 0), Math.Abs(dest - l1) + count);
        if (dest > l2)
        {
            InsertLinesRaw(dest + 1, lines);
            DeleteLinesRaw(l1, count);
            _bs.MarkAdjust(l1, l2, dest - l2, 0, false);
            _bs.MarkAdjust(l2 + 1, dest, -count, 0, false);
            _line = dest;
        }
        else
        {
            DeleteLinesRaw(l1, count);
            InsertLinesRaw(dest + 1, lines);
            _bs.MarkAdjust(l1, l2, dest + 1 - l1, 0, false);
            _bs.MarkAdjust(dest + 1, l1 - 1, count, 0, false);
            _line = dest + count;
        }
        _ = moved;
        _bs.OpStart = new TextPosition(Math.Min(l1, dest + 1), 0);
        _bs.OpEnd = new TextPosition(_line, 0);
        NoteChange(_line, 0);
        Beginline(BL.Sol | BL.Fix);
    }

    private void InsertLinesRaw(int at, IReadOnlyList<string> lines)
    {
        var text = string.Join('\n', lines);
        if (at < LineCount) HostEdit(new VimEdit(new TextPosition(at, 0), new TextPosition(at, 0), text + "\n"));
        else HostEdit(new VimEdit(new TextPosition(LastLine, L(LastLine).Length), new TextPosition(LastLine, L(LastLine).Length), "\n" + text));
    }

    private void DeleteLinesRaw(int first, int count) => RawDeleteLines(first, count);

    private bool ExCopy(ExCmd c)
    {
        if (!CanModify()) return false;
        var s = c.Args.Trim();
        var j = 0;
        var dc = new ExCmd();
        if (!ParseRange(s, ref j, dc) || dc.AddrCount == 0) { ExError("E14: Invalid address"); return false; }
        var dest = dc.Line2;
        if (dest < -1 || dest > LastLine) { ExError("E16: Invalid range"); return false; }
        var count = c.Line2 - c.Line1 + 1;
        var lines = new string[count];
        for (var i = 0; i < count; i++) lines[i] = L(c.Line1 + i);
        InsertLines(dest + 1, lines);
        _line = dest + count;
        _bs.OpStart = new TextPosition(dest + 1, 0);
        _bs.OpEnd = new TextPosition(_line, 0);
        Beginline(BL.Sol | BL.Fix);
        return true;
    }

    private bool ExJoin(ExCmd c)
    {
        if (!CanModify()) return false;
        var l1 = c.Line1; var l2 = c.Line2;
        var a = c.Args.Trim();
        if (a.Length > 0 && char.IsDigit(a[0]))
        {
            var n = ParseIntOr(a, 1);
            l1 = l2; l2 = Math.Min(LastLine, l1 + n - 1);
            if (n <= 1) l2 = Math.Min(LastLine, l1 + 1);
        }
        else if (l1 == l2) l2 = l1 + 1;
        if (l2 > LastLine) { if (l1 >= LastLine) { Beep(); return true; } l2 = LastLine; }
        _line = l1;
        DoJoin(l2 - l1 + 1, !c.Bang, setmark: true);
        Beginline(BL.White | BL.Fix);
        return true;
    }

    private bool ExShift(ExCmd c)
    {
        if (!CanModify()) return false;
        var amount = 1;
        var a = c.Args;
        while (a.Length > 0 && a[0] == c.Name[0]) { amount++; a = a[1..]; }
        a = a.Trim();
        var l1 = c.Line1; var l2 = c.Line2;
        if (a.Length > 0 && char.IsDigit(a[0])) { var n = ParseIntOr(a, 1); l1 = l2; l2 = Math.Min(LastLine, l1 + n - 1); }
        var oap = _oap;
        oap.ResetForMotion(c.Name == "<" ? OpType.ShiftLeft : OpType.ShiftRight, 0);
        oap.Type = MType.Line;
        oap.Start = new TextPosition(l1, 0);
        oap.End = new TextPosition(l2, 0);
        oap.LineCount = l2 - l1 + 1;
        _line = l1;
        OpShift(oap, false, amount);
        Beginline(BL.Sol | BL.Fix);
        return true;
    }

    private bool ExMark(ExCmd c)
    {
        var a = c.Args.Trim();
        if (a.Length == 0) { ExError("E471: Argument required"); return false; }
        var line = Math.Clamp(c.Line2, 0, LastLine);
        if (a[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '\'' or '`' or '[' or ']' or '<' or '>')
        {
            var save = new TextPosition(_line, _col);
            if (a[0] is '\'' or '`') { _line = line; _col = 0; SetPcmark(); _line = save.Line; _col = save.Character; }
            else SetMark(a[0], new TextPosition(line, 0));
            return true;
        }
        ExError("E191: Argument must be a letter or forward/backward quote");
        return false;
    }

    private bool ExDelMarks(ExCmd c)
    {
        var a = c.Args.Trim();
        if (c.Bang) { Array.Clear(_bs.Marks); return true; }
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == ' ') continue;
            if (i + 2 < a.Length && a[i + 1] == '-' && char.IsLetter(a[i]))
            {
                for (var ch = a[i]; ch <= a[i + 2]; ch++) DelMark(ch);
                i += 2;
            }
            else DelMark(a[i]);
        }
        return true;
    }

    private void DelMark(char ch)
    {
        if (ch is >= 'a' and <= 'z') _bs.Marks[ch - 'a'] = null;
        else if (ch is >= 'A' and <= 'Z') _s.GlobalMarks.Remove(ch);
        else if (ch == '<') _bs.VisualStart = null;
        else if (ch == '>') _bs.VisualEnd = null;
        else if (ch == '.') _bs.LastChange = null;
        else if (ch == '^') _bs.LastInsert = null;
        else if (ch == '[') _bs.OpStart = null;
        else if (ch == ']') _bs.OpEnd = null;
    }

    private bool ExUndo(ExCmd c)
    {
        var a = c.Args.Trim();
        if (a.Length == 0) { UndoCommand(1); return true; }
        if (int.TryParse(a, out var n)) return UndoToSeq(n);
        ExError("E474: Invalid argument");
        return false;
    }

    private bool ExEarlierLater(ExCmd c, bool earlier)
    {
        var a = c.Args.Trim();
        var n = 1;
        var unit = '\0';
        if (a.Length > 0)
        {
            var i = 0;
            while (i < a.Length && char.IsDigit(a[i])) i++;
            if (i > 0) n = int.Parse(a.AsSpan(0, i), CultureInfo.InvariantCulture);
            if (i < a.Length) unit = a[i];
        }
        if (unit == '\0') { UndoTimeTravel(earlier ? -n : n); return true; }
        if (unit == 'f') { if (earlier) UndoToSeq(0); else UndoToSeq(_bs.Undo.LastSeq); return true; }
        var span = unit switch { 's' => TimeSpan.FromSeconds(n), 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n), 'd' => TimeSpan.FromDays(n), _ => TimeSpan.Zero };
        // find the state at (current time -/+ span) among the headers on the tree by timestamp
        var all = new List<UndoHeader>();
        var stack = new Stack<UndoHeader>();
        stack.Push(_bs.Undo.Root);
        while (stack.Count > 0) { var h = stack.Pop(); all.Add(h); foreach (var ch in h.Children) stack.Push(ch); }
        var curTime = _bs.Undo.Current.Seq == 0 ? Now() : _bs.Undo.Current.TimeTicks;
        var target = earlier ? curTime - span.Ticks : curTime + span.Ticks;
        UndoHeader? pick = null;
        foreach (var h in all.Where(x => x.Seq > 0 && x.TimeTicks <= target).OrderBy(x => x.Seq)) pick = h;
        if (earlier) { UndoToSeq(pick?.Seq ?? 0); }
        else UndoToSeq(pick?.Seq ?? _bs.Undo.LastSeq);
        return true;
    }

    private bool ExAtRegister(ExCmd c)
    {
        var a = c.Args.Trim();
        var reg = a.Length > 0 ? a[0] : '@';
        if (c.HasRange) { _line = Math.Clamp(c.Line2, 0, LastLine); _col = 0; }
        if (reg == '@' && _s.Search.LastMacroRegister == '\0') { ExError("E748: No previously used register"); return false; }
        if (reg == '@') reg = _s.Search.LastMacroRegister;
        if (reg == ':') { var last = _s.Registers.LastCommandLine; if (last.Length == 0) { ExError("E30: No previous command line"); return false; } ExecuteExLine(last); return true; }
        var r = _s.Registers.ForRead(reg == '"' ? 0 : reg, () => Host.ReadClipboard());
        if (r is null || r.IsEmpty) { Beep(); return true; }
        _s.Search.LastMacroRegister = reg;
        // :@a executes the register as Ex commands, one per line
        foreach (var l in r.Lines) { ExecuteExLine(l); if (_macroAborted) break; }
        return true;
    }

    // ------------------------------------------------------------------------------------------------------------
    // :normal
    // ------------------------------------------------------------------------------------------------------------

    private bool ExNormal(ExCmd c)
    {
        var text = c.Args;
        if (text.Length == 0) { ExError("E471: Argument required"); return false; }
        var keys = Keys.ParseNotation(UnescapeNormalArg(text), _s.Leader, _s.LocalLeader);
        if (!c.HasRange)
        {
            RunNormalKeys(keys, remap: !c.Bang);
            return true;
        }
        for (var l = c.Line1; l <= c.Line2 && l <= LastLine; l++)
        {
            _line = l; _col = 0;
            RunNormalKeys(keys, remap: !c.Bang);
            // lines may have been added or removed by the command: adjust the end of the range
            if (_macroAborted && false) break;
        }
        return true;
    }

    // The argument of :normal is raw keys, not <Key> notation (Vim has no notation parsing here): control characters arrive literally.
    private static string UnescapeNormalArg(string text) => text.Replace("<", "\u0001LT\u0001").Replace("\u0001LT\u0001", "<lt>") is var x && false ? x : text.Replace("<", "<lt>") is var y && false ? y : EscapeLiteralLt(text);

    private static string EscapeLiteralLt(string text) => text;

    /// <summary>Executes keys like :normal: stuffed (not typed), run to completion, and an unfinished command or insert is aborted.</summary>
    private void RunNormalKeys(IReadOnlyList<int> keys, bool remap)
    {
        var savedStuff = new IntQueue();
        while (_stuff.Count > 0) savedStuff.Enqueue(_stuff.Dequeue());
        var savedPend = _pend.ToArray();
        var savedOpPending = _opPendingShown;
        var savedArg = _argPending;
        var savedMapBuf = _mapBuf.ToArray();
        var savedAborted = _macroAborted;
        _pend.Clear();
        _mapBuf.Clear();
        _opPendingShown = false;
        _argPending = false;
        _macroAborted = false;
        _nest++;
        try
        {
            var flag = remap ? 0 : NoRemapFlag;
            foreach (var k in keys) _stuff.Enqueue(k | flag);
            while (_stuff.Count > 0 && _host is not null)
            {
                var k = _stuff.Dequeue();
                if (!CheckMacroBudget()) { _stuff.Clear(); break; }
                ProcessKey(k, typed: false);
                if (_work is not null) DrainWorkNow();
            }
            if (_mapBuf.Count > 0) ReleaseMapBuf();
            // abort an unfinished command
            for (var guard = 0; guard < 4 && _host is not null; guard++)
            {
                if (_mode is VimMode.Insert or VimMode.Replace) { ProcessKey(Keys.Esc, false); continue; }
                if (_mode == VimMode.CommandLine) { ProcessKey(Keys.Esc, false); continue; }
                if (_pend.Count > 0) { ProcessKey(Keys.Esc, false); continue; }
                if (_mode == VimMode.InsertNormal) { ProcessKey(Keys.Esc, false); continue; }
                break;
            }
            while (_stuff.Count > 0) _stuff.Dequeue();
        }
        finally
        {
            _nest--;
            _stuff.Clear();
            while (savedStuff.Count > 0) _stuff.Enqueue(savedStuff.Dequeue());
            _pend.Clear();
            _pend.AddRange(savedPend);
            _mapBuf.Clear();
            _mapBuf.AddRange(savedMapBuf);
            _opPendingShown = savedOpPending;
            _argPending = savedArg;
            _macroAborted = savedAborted;
        }
    }

    private void DrainWorkNow()
    {
        while (_work is not null)
        {
            var w = _work;
            bool more;
            try { more = w.MoveNext(); } catch { _work = null; throw; }
            if (!more) _work = null;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // :set
    // ------------------------------------------------------------------------------------------------------------

    private bool ExSet(string args)
    {
        if (args.Length == 0) { return true; }
        var i = 0;
        var ok = true;
        while (i < args.Length)
        {
            while (i < args.Length && args[i] is ' ' or '\t') i++;
            if (i >= args.Length) break;
            var st = i;
            while (i < args.Length && !(args[i] is ' ' or '\t')) { if (args[i] == '\\' && i + 1 < args.Length) i++; i++; }
            var word = args[st..i];
            ok &= SetOneOption(word);
        }
        return ok;
    }

    private bool SetOneOption(string word)
    {
        if (word == "all" || word == "all&") return true;
        var m = System.Text.RegularExpressions.Regex.Match(word, @"^(no|inv)?([A-Za-z]+)([!?&])?(?:([+\-^]?=|:)(.*))?$", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!m.Success) { ExError($"E518: Unknown option: {word}"); return false; }
        var prefix = m.Groups[1].Value;
        var name = m.Groups[2].Value;
        var suffix = m.Groups[3].Value;
        var op = m.Groups[4].Value;
        var val = m.Groups[5].Success ? UnescapeOptionValue(m.Groups[5].Value) : "";
        var def = VimOptions.Find(name);
        if (def is null && prefix.Length > 0) { def = VimOptions.Find(prefix + name); if (def is not null) prefix = ""; }
        if (def is null) { ExError($"E518: Unknown option: {name}"); return false; }
        if (suffix == "?" || (op.Length == 0 && def.Kind != VimOptionKind.Bool && suffix.Length == 0 && prefix.Length == 0))
        {
            Message(VimOptions.Format(def, def.Get(O)));
            return true;
        }
        if (suffix == "&") { def.Set(O, def.Default); return true; }
        switch (def.Kind)
        {
            case VimOptionKind.Bool:
                if (op.Length > 0) { ExError($"E474: Invalid argument: {word}"); return false; }
                var cur = (bool)def.Get(O);
                var next = suffix == "!" || prefix == "inv" ? !cur : prefix != "no";
                def.Set(O, next);
                OptionChanged(def.Name);
                return true;
            case VimOptionKind.Number:
                if (op.Length == 0) { Message(VimOptions.Format(def, def.Get(O))); return true; }
                if (!int.TryParse(val, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) { ExError($"E521: Number required after =: {word}"); return false; }
                var curN = (int)def.Get(O);
                var newN = op switch { "+=" => curN + n, "-=" => curN - n, "^=" => curN * n, _ => n };
                def.Set(O, newN);
                OptionChanged(def.Name);
                return true;
            default:
            {
                var curS = (string)def.Get(O);
                string newS;
                if (op is "=" or ":") newS = val;
                else if (op == "+=") newS = curS.Length == 0 ? val : (curS.Contains(',') || def.Name is not ("formatoptions" or "cpoptions" or "selection") ? curS + "," + val : curS + val);
                else if (op == "^=") newS = curS.Length == 0 ? val : val + "," + curS;
                else if (op == "-=")
                {
                    if (def.Name is "formatoptions" or "cpoptions") newS = curS.Replace(val, "");
                    else newS = string.Join(',', curS.Split(',').Where(p => p != val));
                }
                else { Message(VimOptions.Format(def, curS)); return true; }
                def.Set(O, newS);
                OptionChanged(def.Name);
                return true;
            }
        }
    }

    private static string UnescapeOptionValue(string v)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < v.Length; i++)
        {
            if (v[i] == '\\' && i + 1 < v.Length) i++;
            sb.Append(v[i]);
        }
        return sb.ToString();
    }

    private void OptionChanged(string name)
    {
        if (name is "hlsearch") UpdateSearchHighlight(false);
        if (name == "hlsearch" && O.HlSearch) _s.Search.HighlightActive = true;
    }
}
