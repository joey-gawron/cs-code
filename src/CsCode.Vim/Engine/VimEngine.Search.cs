using System.Text;
using System.Text.RegularExpressions;
using CsCode.Core.Text;

namespace CsCode.Vim;

public sealed partial class VimEngine
{
    // ------------------------------------------------------------------------------------------------------------
    // compiled-pattern cache and buffer text snapshot for multi-line patterns
    // ------------------------------------------------------------------------------------------------------------

    private VimPattern? _lastCompiled;
    private string? _lastCompiledKey;
    private string? _snapText;
    private int[]? _snapStarts;
    private int _snapVersion = -1;
    private IVimBuffer? _snapBuf;

    private VimPattern? CompilePattern(string pattern, bool report = true, bool useSmartcase = true)
    {
        var key = pattern + "\u0001" + O.IgnoreCase + O.SmartCase + O.Magic + useSmartcase + _s.Search.SubReplacement;
        if (_lastCompiled is not null && _lastCompiledKey == key) return _lastCompiled;
        try
        {
            var p = VimRegex.Compile(pattern, O.IgnoreCase, O.SmartCase && useSmartcase, O.Magic, _s.Search.SubReplacement);
            _lastCompiled = p;
            _lastCompiledKey = key;
            return p;
        }
        catch (VimRegexException ex)
        {
            if (report) Error(ex.Message);
            return null;
        }
    }

    private void EnsureSnapshot()
    {
        if (_snapText is not null && _snapVersion == _buf.Version && ReferenceEquals(_snapBuf, _buf)) return;
        var sb = new StringBuilder();
        var starts = new int[LineCount];
        for (var i = 0; i < LineCount; i++)
        {
            starts[i] = sb.Length;
            sb.Append(L(i));
            if (i < LineCount - 1) sb.Append('\n');
        }
        _snapText = sb.ToString();
        _snapStarts = starts;
        _snapVersion = _buf.Version;
        _snapBuf = _buf;
    }

    private TextPosition PosOfOffset(int offset)
    {
        var starts = _snapStarts!;
        var idx = Array.BinarySearch(starts, offset);
        var l = idx >= 0 ? idx : ~idx - 1;
        return new TextPosition(l, offset - starts[l]);
    }

    private readonly record struct Hit(TextPosition Start, TextPosition End);

    private static (int Start, int End) GroupSpan(Match m)
    {
        var s = m.Groups["zs"] is { Success: true } zs ? zs.Index : m.Index;
        var e = m.Groups["ze"] is { Success: true } ze ? ze.Index : m.Index + m.Length;
        if (e < s) e = s;
        return (s, e);
    }

    /// <summary>All matches whose (\zs-adjusted) start lies in <paramref name="line"/>, found the way Vim retries (restart one char after each match start).</summary>
    private void MatchesInLine(VimPattern pat, int line, List<Hit> into)
    {
        into.Clear();
        if (!pat.Multiline)
        {
            var text = L(line);
            var at = 0;
            while (at <= text.Length)
            {
                var m = pat.Regex.Match(text, at);
                if (!m.Success) break;
                var (s, e) = GroupSpan(m);
                if (s < at && !pat.HasZs) { }
                into.Add(new Hit(new TextPosition(line, s), new TextPosition(line, Math.Min(e, text.Length))));
                int next;
                if (O.CpoHas('c'))
                {
                    next = e > s ? e : s + (s < text.Length ? VimText.CharLen(text, s) : 1);
                    if (next <= m.Index) next = m.Index + 1;
                }
                else next = Math.Max(m.Index, s) + (m.Index < text.Length ? VimText.CharLen(text, Math.Min(m.Index, Math.Max(0, text.Length - 1))) : 1);
                if (next <= at) next = at + 1;
                at = next;
            }
            return;
        }
        EnsureSnapshot();
        var snap = _snapText!;
        var lineStart = _snapStarts![line];
        var lineEnd = lineStart + L(line).Length;
        var pos = lineStart;
        while (pos <= lineEnd)
        {
            var m = pat.Regex.Match(snap, pos);
            if (!m.Success) break;
            var (s, e) = GroupSpan(m);
            if (s > lineEnd) break;
            if (s >= lineStart)
                into.Add(new Hit(PosOfOffset(s), PosOfOffset(Math.Min(e, snap.Length))));
            pos = O.CpoHas('c') && e > Math.Max(pos, m.Index) ? e : Math.Max(pos, m.Index) + 1;
            if (m.Index >= lineEnd + 1) break;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // searchit
    // ------------------------------------------------------------------------------------------------------------

    private bool SearchIt(VimPattern pat, TextPosition start, bool forward, bool acceptAtStart, bool useEnd, int count, out Hit hit, out bool wrapped)
    {
        hit = default;
        wrapped = false;
        var hits = new List<Hit>();
        var pos = start;
        var first = true;
        while (count-- > 0)
        {
            var found = false;
            var startLen = pos.Character < L(pos.Line).Length ? VimText.CharLen(L(pos.Line), pos.Character) : 1;
            var extra = forward ? (acceptAtStart ? 0 : startLen) : (acceptAtStart ? startLen : 0);
            var lineCount = LineCount;
            // scan the start line, following lines, wrapping if allowed
            for (var step = 0; step <= lineCount; step++)
            {
                int line;
                var loop = false;
                if (forward) { line = pos.Line + step; if (line >= lineCount) { if (!O.WrapScan) break; line -= lineCount; loop = true; } }
                else { line = pos.Line - step; if (line < 0) { if (!O.WrapScan) break; line += lineCount; loop = true; } }
                if (step == lineCount) { loop = true; }
                MatchesInLine(pat, line, hits);
                var onStartLine = step == 0;
                var wrapBack = step == lineCount; // the start line seen again after wrapping: everything counts
                Hit? pick = null;
                if (forward)
                {
                    foreach (var h in hits)
                    {
                        if (onStartLine && !wrapBack)
                        {
                            var atNul = h.Start.Character >= L(h.Start.Line).Length && h.Start.Character > 0 ? 1 : 0;
                            var ok = useEnd && first
                                ? (h.End.Line > pos.Line || h.End.Character - 1 >= pos.Character + extra)
                                : (h.Start.Line > pos.Line || h.Start.Character - atNul >= pos.Character + extra);
                            if (!ok) continue;
                        }
                        pick = h;
                        break;
                    }
                }
                else
                {
                    for (var i = hits.Count - 1; i >= 0; i--)
                    {
                        var h = hits[i];
                        if (onStartLine && !wrapBack)
                        {
                            var ok = useEnd && first
                                ? (h.End.Line < pos.Line || (h.End.Line == pos.Line && h.End.Character - 1 < pos.Character + extra))
                                : (h.Start.Line < pos.Line || (h.Start.Line == pos.Line && h.Start.Character < pos.Character + extra));
                            if (!ok) continue;
                        }
                        pick = h;
                        break;
                    }
                }
                if (pick is { } p)
                {
                    hit = p;
                    found = true;
                    if (loop) wrapped = true;
                    break;
                }
            }
            if (!found) return false;
            pos = useEnd ? PrevOf(hit.End) : hit.Start;
            first = false;
        }
        return true;
    }

    private TextPosition PrevOf(TextPosition p)
    {
        var l = p.Line; var c = p.Character;
        Dec(ref l, ref c);
        return new TextPosition(l, c);
    }

    // ------------------------------------------------------------------------------------------------------------
    // search offsets and the / ? commands
    // ------------------------------------------------------------------------------------------------------------

    private readonly record struct SearchOffset(bool Line, bool End, bool Start, int Value)
    {
        public bool IsNone => !Line && !End && !Start && Value == 0;
    }

    private static SearchOffset ParseOffset(string off)
    {
        if (off.Length == 0) return default;
        var i = 0;
        var line = false; var end = false; var start = false;
        if (off[0] == 'e') { end = true; i = 1; }
        else if (off[0] is 's' or 'b') { start = true; i = 1; }
        else line = true;
        var value = 0;
        if (i < off.Length)
        {
            var sign = 1;
            if (off[i] is '+' or '-') { sign = off[i] == '-' ? -1 : 1; i++; if (i >= off.Length) value = sign; }
            var s = i;
            while (i < off.Length && char.IsDigit(off[i])) i++;
            if (i > s) value = sign * int.Parse(off.AsSpan(s, i - s));
            else if (s == i && value == 0 && s > 0 && off[s - 1] is '+' or '-') value = sign;
        }
        return new SearchOffset(line, end, start, value);
    }

    /// <summary>Splits "pat/offset;rest" at the first unescaped delimiter.</summary>
    private static (string Pattern, string? Tail) SplitSearchText(string text, char delim)
    {
        var inColl = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length) { i++; continue; }
            if (c == '[' && !inColl) { var close = text.IndexOf(']', i + 1); if (close > 0 && delim != '[') { inColl = false; i = close; continue; } }
            if (c == delim) return (text[..i], text[(i + 1)..]);
        }
        return (text, null);
    }

    /// <summary>Executes search-command text ("pat/e+1;/other/"). Returns false (after reporting) when not found.</summary>
    private bool DoSearchText(string text, bool forward, int count, OpArgs? oap, bool setPcmark = true)
    {
        var delim = forward ? '/' : '?';
        var (pat, rest) = SplitSearchText(text, delim);
        string offText = "";
        string? chain = null;
        if (rest is not null)
        {
            var semi = rest.IndexOf(';');
            if (semi >= 0) { offText = rest[..semi]; chain = rest[(semi + 1)..]; }
            else offText = rest;
        }
        var mem = _s.Search;
        if (pat.Length == 0)
        {
            if (mem.Pattern.Length == 0) { Error("E35: No previous regular expression"); return false; }
            pat = mem.Pattern;
            if (rest is null) offText = mem.Offset;
        }
        mem.Pattern = pat;
        mem.Forward = forward;
        mem.Offset = offText;
        mem.HighlightActive = true;
        _s.Registers.LastSearch = pat;
        return DoSearchAgain(forward, count, oap, setPcmark, chain);
    }

    private bool DoSearchAgain(bool forward, int count, OpArgs? oap, bool setPcmark = true, string? chain = null)
    {
        var mem = _s.Search;
        var pattern = mem.Pattern;
        if (pattern.Length == 0) { Error("E35: No previous regular expression"); return false; }
        var pat = CompilePattern(pattern);
        if (pat is null) return false;
        var off = ParseOffset(mem.Offset);
        var cursor = new TextPosition(_line, _col);
        var startPos = cursor;
        var useEnd = off.End;
        var acceptAtStart = false;
        // Like Vim's do_search: a character offset is subtracted from the start position so "/pat/s-2" and "?pat?e+2" do not get stuck.
        // (Not for a line offset.) Running into the buffer edge starts the search before the first / after the last character.
        if (!off.Line && off.Value != 0)
        {
            var l = startPos.Line; var c = startPos.Character;
            var n = off.Value;
            var hitEdge = false;
            if (n > 0) { for (; n > 0; n--) if (Decl(ref l, ref c) == -1) { hitEdge = true; break; } }
            else { for (; n < 0; n++) if (Incl(ref l, ref c) == -1) { hitEdge = true; break; } }
            if (hitEdge)
            {
                // before the first line (forward) or after the last one (backward): the start position itself may match
                startPos = forward ? new TextPosition(0, 0) : new TextPosition(LastLine, L(LastLine).Length);
                acceptAtStart = true;
            }
            else startPos = new TextPosition(l, c);
        }
        if (!SearchIt(pat, startPos, forward, acceptAtStart, useEnd, count, out var hit, out var wrapped))
        {
            Error(O.WrapScan ? $"E486: Pattern not found: {pattern}" : forward ? $"E385: Search hit BOTTOM without match for: {pattern}" : $"E384: Search hit TOP without match for: {pattern}");
            return false;
        }
        if (wrapped) Message(forward ? "search hit BOTTOM, continuing at TOP" : "search hit TOP, continuing at BOTTOM", VimMessageKind.Warning);
        ApplySearchHit(hit, off, oap, setPcmark);
        if (chain is not null)
        {
            // ";/pat2/off": a second search starting at the first match
            var c2 = chain.TrimStart();
            if (c2.Length > 0 && c2[0] is '/' or '?')
            {
                var fwd = c2[0] == '/';
                return DoSearchText(c2[1..], fwd, 1, oap, setPcmark: false);
            }
        }
        FireSearchChanged();
        return true;
    }

    private void ApplySearchHit(Hit hit, SearchOffset off, OpArgs? oap, bool setPcmark)
    {
        if (setPcmark) SetPcmark();
        TextPosition target;
        if (off.Line)
        {
            var l = Math.Clamp(hit.Start.Line + off.Value, 0, LastLine);
            _line = l;
            _col = 0;
            if (oap is not null) oap.Type = MType.Line;
            Beginline(BL.White | BL.Fix);
            _setCurswant = true;
            return;
        }
        if (off.End)
        {
            var l = hit.End.Line; var c = hit.End.Character;
            // the last character of the match
            if (!(hit.End == hit.Start)) Dec(ref l, ref c);
            target = new TextPosition(l, c);
            target = MoveBy(target, off.Value);
            if (oap is not null) oap.Inclusive = true;
        }
        else
        {
            target = hit.Start;
            if (off.Start || off.Value != 0) target = MoveBy(target, off.Value);
            if (oap is not null) oap.Inclusive = false;
        }
        _line = Math.Clamp(target.Line, 0, LastLine);
        _col = target.Character;
        if (_line < LineCount && _col > L(_line).Length) _col = L(_line).Length;
        CheckCursorSearch();
        _setCurswant = true;
    }

    private void CheckCursorSearch()
    {
        // in Normal mode the cursor cannot rest after the last char (Vim moves it back when a match lands on the NUL)
        if (!VisualActive && _col >= CurLine.Length && _col > 0) _col = VimText.PrevCharStart(CurLine, CurLine.Length);
    }

    private TextPosition MoveBy(TextPosition p, int n)
    {
        var l = p.Line; var c = p.Character;
        for (; n > 0; n--) if (Incl(ref l, ref c) == -1) break;
        for (; n < 0; n++) if (Decl(ref l, ref c) == -1) break;
        return new TextPosition(l, c);
    }

    private void FireSearchChanged()
    {
        var pat = _s.Search.Pattern;
        _searchChanged.Fire(pat);
        UpdateSearchHighlight(incremental: false);
    }

    private void UpdateSearchHighlight(bool incremental, string? overridePattern = null)
    {
        var pattern = overridePattern ?? _s.Search.Pattern;
        if (_host is null) return;
        if ((!O.HlSearch || !_s.Search.HighlightActive) && !incremental) { _host.OnSearchHighlight(null); return; }
        if (pattern.Length == 0) { _host.OnSearchHighlight(null); return; }
        var p = CompilePattern(pattern, report: false);
        _host.OnSearchHighlight(p is null ? null : new VimSearchHighlight(p.DotNetSource, p.IgnoreCase, incremental));
    }

    // ------------------------------------------------------------------------------------------------------------
    // motions that search
    // ------------------------------------------------------------------------------------------------------------

    private bool _searchPendingOp;
    private Cmd? _opCmdForSearch;

    private bool SearchMotion(Cmd m, int count1, OpArgs oap)
    {
        switch (m.Key)
        {
            case 'n' or 'N':
            {
                var mem = _s.Search;
                if (mem.Pattern.Length == 0) { Error("E35: No previous regular expression"); return false; }
                var fwd = m.Key == 'n' ? mem.Forward : !mem.Forward;
                oap.Type = MType.Char;
                oap.UseRegOne = true;
                var ok = DoSearchAgain(fwd, count1, oap);
                _s.Search.Forward = mem.Forward;
                return ok;
            }
            case '*' or '#':
                return StarSearch(m.Key == '*', count1, wholeWord: true, oap);
            case '/' or '?':
                return BeginSearchCmdlineFromMotion(m.Key == '/', count1, oap);
        }
        return false;
    }

    private bool BeginSearchCmdlineFromMotion(bool forward, int count1, OpArgs oap)
    {
        // an operator waits for this search: the command line takes over, the operator completes when it is executed
        if (oap.Op != OpType.None)
        {
            var copy = new Cmd();
            _cmd.CopyTo(copy);
            _opCmdForSearch = copy;
            _searchPendingOp = true;
        }
        StartSearchCmdline(forward, count1, oap.Op != OpType.None ? _opCmdForSearch : null, oap.Op != OpType.None ? oap.Start : null);
        return true;
    }

    private bool StarSearch(bool forward, int count1, bool wholeWord, OpArgs oap)
    {
        var s = CurLine;
        // the keyword under or after the cursor, else the non-blank sequence
        var i = _col;
        var kw = O.Keywords;
        while (i < s.Length && !kw.IsKeyword(VimText.CodePointAt(s, i))) i += VimText.CharLen(s, i);
        int start, end;
        var isKeyword = true;
        if (i >= s.Length)
        {
            isKeyword = false;
            i = _col;
            while (i < s.Length && s[i] is ' ' or '\t') i++;
            if (i >= s.Length) { Error("E348: No string under cursor"); return false; }
            start = i;
            while (start > 0 && !(s[start - 1] is ' ' or '\t')) start--;
            end = i;
            while (end < s.Length && !(s[end] is ' ' or '\t')) end++;
        }
        else
        {
            start = i;
            while (start > 0 && kw.IsKeyword(VimText.CodePointAt(s, VimText.PrevCharStart(s, start)))) start = VimText.PrevCharStart(s, start);
            end = i;
            while (end < s.Length && kw.IsKeyword(VimText.CodePointAt(s, end))) end += VimText.CharLen(s, end);
        }
        var word = s.Substring(start, end - start);
        var sb = new StringBuilder();
        if (wholeWord && isKeyword) sb.Append("\\<");
        foreach (var ch in word)
        {
            if (ch is '\\' or '/' or '.' or '*' or '$' or '^' or '~' or '[' or ']') sb.Append('\\');
            sb.Append(ch);
        }
        if (wholeWord && isKeyword) sb.Append("\\>");
        var pattern = sb.ToString();
        var mem = _s.Search;
        mem.Pattern = pattern;
        mem.Forward = forward;
        mem.Offset = "";
        mem.HighlightActive = true;
        _s.Registers.LastSearch = pattern;
        SetPcmark();
        _col = start;
        var ok = DoSearchAgainNoSmartcase(forward, count1, oap);
        if (!ok) { return false; }
        oap.UseRegOne = true;
        return true;
    }

    private bool DoSearchAgainNoSmartcase(bool forward, int count, OpArgs oap)
    {
        var pat = CompilePattern(_s.Search.Pattern, useSmartcase: false);
        if (pat is null) return false;
        var cursor = new TextPosition(_line, _col);
        if (!SearchIt(pat, cursor, forward, false, false, count, out var hit, out var wrapped))
        {
            Error(O.WrapScan ? $"E486: Pattern not found: {_s.Search.Pattern}" : forward ? $"E385: Search hit BOTTOM without match for: {_s.Search.Pattern}" : $"E384: Search hit TOP without match for: {_s.Search.Pattern}");
            return false;
        }
        if (wrapped) Message(forward ? "search hit BOTTOM, continuing at TOP" : "search hit TOP, continuing at BOTTOM", VimMessageKind.Warning);
        _line = hit.Start.Line;
        _col = hit.Start.Character;
        CheckCursorSearch();
        _setCurswant = true;
        FireSearchChanged();
        return true;
    }

    // ---- gn / gN ----

    private bool GnMotion(bool forward, int count1, OpArgs oap)
    {
        var mem = _s.Search;
        if (mem.Pattern.Length == 0) { Error("E35: No previous regular expression"); return false; }
        var pat = CompilePattern(mem.Pattern);
        if (pat is null) return false;
        // a match at the cursor counts: start searching just before the cursor
        var cur = new TextPosition(_line, _col);
        // an active Visual selection is extended: the search continues from the character after (before) the cursor
        if (VisualActive) cur = forward ? NextOf(cur) : PrevOf(cur);
        if (!FindGnMatch(pat, cur, forward, count1, out var hit, acceptAtStart: VisualActive)) { Error($"E486: Pattern not found: {mem.Pattern}"); return false; }
        var last = hit.End == hit.Start ? hit.Start : PrevOf(hit.End);
        if (VisualActive)
        {
            var target = forward ? last : hit.Start;
            _line = target.Line; _col = target.Character;
            return true;
        }
        // gn selects the match with the cursor at its end, gN with the cursor at its start
        oap.Start = forward ? hit.Start : last;
        var end = forward ? last : hit.Start;
        _line = end.Line; _col = end.Character;
        oap.Inclusive = true;
        oap.Type = MType.Char;
        return true;
    }

    private bool FindGnMatch(VimPattern pat, TextPosition cur, bool forward, int count, out Hit hit, bool acceptAtStart = false)
    {
        hit = default;
        // is the cursor inside a match? then that match is the first one
        var hits = new List<Hit>();
        MatchesInLine(pat, cur.Line, hits);
        foreach (var h in hits)
            if (h.Start <= cur && cur < (h.End == h.Start ? NextOf(h.End) : h.End)) { hit = h; count--; if (count == 0) return true; cur = forward ? h.End : h.Start; break; }
        for (; count > 0; count--)
        {
            if (!SearchIt(pat, cur, forward, acceptAtStart: acceptAtStart, false, 1, out hit, out _)) return false;
            acceptAtStart = false;
            cur = forward ? hit.End : hit.Start;
        }
        return true;
    }

    private TextPosition NextOf(TextPosition p)
    {
        var l = p.Line; var c = p.Character;
        Inc(ref l, ref c);
        return new TextPosition(l, c);
    }

    private void GnCommand(Cmd c)
    {
        var oap = _oap;
        oap.ResetForMotion(OpType.None, 0);
        var start = new TextPosition(_line, _col);
        if (!GnMotion(c.Key2 == 'n', c.Count1, oap)) { _line = start.Line; _col = start.Character; Beep(); return; }
        if (!VisualActive)
        {
            _visualStart = oap.Start;
            SetMode(VimMode.Visual);
        }
        CheckCursor();
    }
}
