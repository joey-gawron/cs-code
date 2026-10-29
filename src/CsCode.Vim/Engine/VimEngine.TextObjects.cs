using System.Text.RegularExpressions;
using CsCode.Core.Text;

namespace CsCode.Vim;

public sealed partial class VimEngine
{
    private TextPosition _visualStart;

    private bool RunTextObject(Cmd m, int count0, OpArgs oap)
    {
        var include = m.Key == 'a';
        var count = count0 > 0 ? count0 : 1;
        var ch = m.NChar;
        oap.Type = MType.Char;
        oap.Inclusive = true;
        bool ok;
        switch (ch)
        {
            case 'w' or 'W': ok = CurrentWord(oap, count, include, ch == 'W'); break;
            case 's': ok = CurrentSentence(oap, count, include); break;
            case 'p': ok = CurrentParagraph(oap, count, include); break;
            case '(' or ')' or 'b': ok = CurrentBlock(oap, count, include, '(', ')'); break;
            case '{' or '}' or 'B': ok = CurrentBlock(oap, count, include, '{', '}'); break;
            case '[' or ']': ok = CurrentBlock(oap, count, include, '[', ']'); break;
            case '<' or '>': ok = CurrentBlock(oap, count, include, '<', '>'); break;
            case '"' or '\'' or '`': ok = CurrentQuote(oap, count, include, ch); break;
            case 't': ok = CurrentTag(oap, count, include); break;
            default: ok = false; break;
        }
        if (ok && !VisualActive) { oap.IsVisual = false; }
        return ok;
    }

    private void BackInLine()
    {
        var sclass = Cls();
        while (true)
        {
            if (_col == 0) break;
            DecCursor();
            if (Cls() != sclass) { IncCursor(); break; }
        }
    }

    private bool CurrentWord(OpArgs oap, int count, bool include, bool bigword)
    {
        _clsBigword = bigword;
        var inclusive = true;
        var includeWhite = false;
        TextPosition startPos = default;
        var cur = () => new TextPosition(_line, _col);
        if (VisualActive && O.SelectionExclusive && _visualStart < cur()) DecCursor();
        if (!VisualActive || _visualStart == cur())
        {
            BackInLine();
            startPos = cur();
            if ((Cls() == 0) == include)
            {
                if (!EndWord(1, bigword, true, true)) return false;
            }
            else
            {
                FwdWord(1, bigword, true);
                if (_col == 0) Decl(ref _line, ref _col);
                else OneLeft();
                if (include) includeWhite = true;
            }
            if (VisualActive) _visualStart = startPos;
            else { oap.Start = startPos; oap.Type = MType.Char; }
            count--;
        }
        while (count > 0)
        {
            inclusive = true;
            if (VisualActive && cur() < _visualStart)
            {
                if (Decl(ref _line, ref _col) == -1) return false;
                if (include != (Cls() != 0))
                {
                    if (!BckWord(1, bigword, true)) return false;
                }
                else
                {
                    if (!BckendWord(1, bigword, true)) return false;
                    Incl(ref _line, ref _col);
                }
            }
            else
            {
                if (Incl(ref _line, ref _col) == -1) return false;
                if (include != (Cls() == 0))
                {
                    if (!FwdWord(1, bigword, true) && count > 1) return false;
                    if (!OneLeft()) inclusive = false;
                }
                else if (!EndWord(1, bigword, true, true)) return false;
            }
            count--;
        }
        if (includeWhite && (Cls() != 0 || (_col == 0 && !inclusive)))
        {
            var save = cur();
            _line = startPos.Line; _col = startPos.Character;
            if (OneLeft())
            {
                BackInLine();
                if (Cls() == 0 && _col > 0)
                {
                    if (VisualActive) _visualStart = cur(); else oap.Start = cur();
                }
            }
            _line = save.Line; _col = save.Character;
        }
        if (VisualActive)
        {
            if (O.SelectionExclusive && inclusive && _visualStart <= cur()) IncCursor();
            if (_mode == VimMode.VisualLine) SetMode(VimMode.Visual);
        }
        else oap.Inclusive = inclusive;
        return true;
    }

    private bool OneLeft()
    {
        if (_col == 0) return false;
        _col = VimText.PrevCharStart(CurLine, _col);
        return true;
    }

    private bool LineWhite(int line) => VimText.IsBlank(L(line));

    private bool CurrentParagraph(OpArgs oap, int count, bool include)
    {
        var startLnum = _line;
        var retval = true;
        if (VisualActive && startLnum != _visualStart.Line) return ExtendParagraph(startLnum, count, include);
        var whiteInFront = LineWhite(startLnum);
        while (startLnum > 0)
        {
            if (whiteInFront) { if (!LineWhite(startLnum - 1)) break; }
            else if (LineWhite(startLnum - 1) || StartPS(startLnum, 0, false)) break;
            startLnum--;
        }
        var endLnum = startLnum;
        while (endLnum <= LastLine && LineWhite(endLnum)) endLnum++;
        endLnum--;
        var i = count;
        if (!include && whiteInFront) i--;
        var doWhite = false;
        while (i-- > 0)
        {
            if (endLnum == LastLine) return false;
            if (!include) doWhite = LineWhite(endLnum + 1);
            if (include || !doWhite)
            {
                endLnum++;
                while (endLnum < LastLine && !LineWhite(endLnum + 1) && !StartPS(endLnum + 1, 0, false)) endLnum++;
            }
            if (i == 0 && whiteInFront && include) break;
            if (include || doWhite) while (endLnum < LastLine && LineWhite(endLnum + 1)) endLnum++;
        }
        if (!whiteInFront && !LineWhite(endLnum) && include) while (startLnum > 0 && LineWhite(startLnum - 1)) startLnum--;
        if (VisualActive)
        {
            if (_mode == VimMode.VisualLine && startLnum == _line) return ExtendParagraph(startLnum, count, include);
            if (_visualStart.Line != startLnum) _visualStart = new TextPosition(startLnum, 0);
            SetMode(VimMode.VisualLine);
        }
        else { oap.Start = new TextPosition(startLnum, 0); oap.Type = MType.Line; }
        _line = endLnum;
        _col = 0;
        return retval;
    }

    private bool ExtendParagraph(int startLnum, int count, bool include)
    {
        var dir = startLnum < _visualStart.Line ? -1 : 1;
        var retval = true;
        for (var i = count; --i >= 0;)
        {
            if (startLnum == (dir < 0 ? 0 : LastLine)) { retval = false; break; }
            var prevWhite = -1;
            for (var t = 0; t < 2; t++)
            {
                startLnum += dir;
                var isWhite = LineWhite(startLnum) ? 1 : 0;
                if (prevWhite == isWhite) { startLnum -= dir; break; }
                while (true)
                {
                    if (startLnum == (dir < 0 ? 0 : LastLine)) break;
                    if (isWhite != (LineWhite(startLnum + dir) ? 1 : 0) || (isWhite == 0 && StartPS(startLnum + (dir > 0 ? 1 : 0), 0, false))) break;
                    startLnum += dir;
                }
                if (!include) break;
                if (startLnum == (dir < 0 ? 0 : LastLine)) break;
                prevWhite = isWhite;
            }
        }
        _line = startLnum;
        _col = 0;
        return retval;
    }

    private void FindFirstBlank(ref int line, ref int col)
    {
        while (Decl(ref line, ref col) != -1)
        {
            var c = CharAt(line, col);
            if (!(c is ' ' or '\t')) { Incl(ref line, ref col); break; }
        }
    }

    private bool FindSentenceAt(ref int line, ref int col, bool forward, int count)
    {
        var sl = _line; var sc = _col;
        _line = line; _col = col;
        var ok = FindSentence(forward, count);
        line = _line; col = _col;
        _line = sl; _col = sc;
        return ok;
    }

    private void FindSentForward(int count, bool atStartSent)
    {
        while (count-- > 0)
        {
            FindSentence(true, 1);
            if (atStartSent) FindFirstBlank(ref _line, ref _col);
            if (count == 0 || atStartSent) Decl(ref _line, ref _col);
            atStartSent = !atStartSent;
        }
    }

    private bool CurrentSentence(OpArgs oap, int count, bool include)
    {
        var startPos = new TextPosition(_line, _col);
        var pl = _line; var pc = _col;
        FindSentence(true, 1);
        var cur = () => new TextPosition(_line, _col);
        var extend = false;
        if (VisualActive && startPos != _visualStart)
        {
            ExtendSentence();
            return true;
        }
        while (CharAt(pl, pc) is ' ' or '\t') Incl(ref pl, ref pc);
        var startBlank = new TextPosition(pl, pc) == cur();
        var sl = startPos.Line; var sc = startPos.Character;
        if (startBlank) FindFirstBlank(ref sl, ref sc);
        else
        {
            FindSentence(false, 1);
            sl = _line; sc = _col;
        }
        int ncount;
        if (include) ncount = count * 2;
        else { ncount = count; if (startBlank) ncount--; }
        if (ncount > 0) FindSentForward(ncount, true);
        else Decl(ref _line, ref _col);
        if (include)
        {
            if (startBlank)
            {
                FindFirstBlank(ref _line, ref _col);
                if (CharAt(_line, _col) is ' ' or '\t') Decl(ref _line, ref _col);
            }
            else if (!(CharAtCursor() is ' ' or '\t')) FindFirstBlank(ref sl, ref sc);
        }
        startPos = new TextPosition(sl, sc);
        if (VisualActive)
        {
            if (startPos == cur()) { extend = true; }
            else
            {
                if (O.SelectionExclusive) _col++;
                _visualStart = startPos;
                SetMode(VimMode.Visual);
                return true;
            }
        }
        if (extend) { ExtendSentence(); return true; }
        oap.Inclusive = Incl(ref _line, ref _col) == -1;
        oap.Start = startPos;
        oap.Type = MType.Char;
        return true;
    }

    private void ExtendSentence()
    {
        // Visual area bigger than one character: extend by sentences (simplified port of the "extend" branch)
        FindSentence(true, 1);
        var l = _line; var c = _col;
        Decl(ref l, ref c);
        _line = l; _col = c;
    }

    private bool CurrentBlock(OpArgs oap, int count, bool include, char what, char other)
    {
        var cur = () => new TextPosition(_line, _col);
        var oldPos = cur();
        var oldStart = oldPos;
        var oldEnd = oldPos;
        if (!VisualActive || _visualStart == cur())
        {
            SetPcmark();
            if (what == '{') while (InIndentAt(cur()) && cur().Character < CurLine.Length) { if (IncCursor() != 0) break; }
            if (CharAtCursor() == what) _col += 1;
        }
        else if (_visualStart < cur()) { oldStart = _visualStart; _line = _visualStart.Line; _col = _visualStart.Character; }
        else oldEnd = _visualStart;

        TextPosition? pos = FindMatchLimit(what, false, cur(), unmatched: true);
        TextPosition startPos = default;
        if (pos is not null)
        {
            while (count-- > 0)
            {
                pos = FindMatchLimit(what, false, cur(), unmatched: true);
                if (pos is null) break;
                _line = pos.Value.Line; _col = pos.Value.Character;
                startPos = pos.Value;
            }
        }
        else
        {
            while (count-- > 0)
            {
                pos = FindMatchLimit(what, true, cur(), unmatched: true);
                if (pos is null) break;
                _line = pos.Value.Line; _col = pos.Value.Character;
                startPos = pos.Value;
            }
        }
        TextPosition? endPos = pos is null ? null : FindMatchLimit(other, true, cur(), unmatched: true);
        if (pos is null || endPos is null) { _line = oldPos.Line; _col = oldPos.Character; return false; }
        _line = endPos.Value.Line; _col = endPos.Value.Character;
        var sol = false;
        while (!include)
        {
            var sl = startPos.Line; var sc = startPos.Character;
            Incl(ref sl, ref sc);
            startPos = new TextPosition(sl, sc);
            sol = _col == 0;
            Decl(ref _line, ref _col);
            while (InIndentAt(cur()) && CurLine.Length > 0 && VimText.FirstNonBlank(CurLine) > _col - 0 && cur().Character <= VimText.FirstNonBlank(CurLine) && VimText.FirstNonBlank(CurLine) > 0)
            {
                sol = true;
                if (Decl(ref _line, ref _col) != 0) break;
            }
            if (!(startPos < oldStart) && !(oldEnd < cur()) && startPos != cur() && VisualActive)
            {
                _line = oldStart.Line; _col = oldStart.Character;
                Decl(ref _line, ref _col);
                var p2 = FindMatchLimit(what, false, cur(), unmatched: true);
                if (p2 is null) { _line = oldPos.Line; _col = oldPos.Character; return false; }
                startPos = p2.Value; _line = p2.Value.Line; _col = p2.Value.Character;
                var e2 = FindMatchLimit(other, true, cur(), unmatched: true);
                if (e2 is null) { _line = oldPos.Line; _col = oldPos.Character; return false; }
                _line = e2.Value.Line; _col = e2.Value.Character;
            }
            else break;
        }
        if (VisualActive)
        {
            if (O.SelectionExclusive) IncCursor();
            if (sol && CharAtCursor() != 0) IncCursor();
            _visualStart = startPos;
            SetMode(VimMode.Visual);
        }
        else
        {
            oap.Start = startPos;
            oap.Type = MType.Char;
            oap.Inclusive = false;
            if (sol) Incl(ref _line, ref _col);
            else if (startPos <= cur()) oap.Inclusive = true;
            else { _line = startPos.Line; _col = startPos.Character; }
        }
        return true;
    }

    private static int FindNextQuote(string line, int col, int quote, string? escape)
    {
        while (true)
        {
            if (col >= line.Length) return -1;
            var c = line[col];
            if (escape is not null && escape.Contains(c)) { col++; if (col >= line.Length) return -1; }
            else if (c == quote) break;
            col++;
        }
        return col;
    }

    private static int FindPrevQuote(string line, int colStart, int quote, string? escape)
    {
        while (colStart > 0)
        {
            colStart--;
            var n = 0;
            if (escape is not null) while (colStart - n > 0 && escape.Contains(line[colStart - n - 1])) n++;
            if ((n & 1) == 1) colStart -= n;
            else if (line[colStart] == quote) break;
        }
        return colStart;
    }

    private bool CurrentQuote(OpArgs oap, int count, bool include, int quote)
    {
        var line = CurLine;
        int colEnd = 0;
        var colStart = _col;
        var inclusive = false;
        var visEmpty = true;
        var visBefCurs = false;
        var insideQuotes = false;
        var selectedQuote = false;
        const string esc = "\\";
        if (VisualActive)
        {
            if (_visualStart.Line != _line) return false;
            visBefCurs = _visualStart.Character < _col;
            visEmpty = _visualStart.Character == _col;
            if (!visEmpty)
            {
                int i;
                if (visBefCurs)
                {
                    insideQuotes = _visualStart.Character > 0 && line[_visualStart.Character - 1] == quote && _col + 1 < line.Length && line[_col + 1] == quote;
                    i = _visualStart.Character; colEnd = _col;
                }
                else
                {
                    insideQuotes = _col > 0 && line[_col - 1] == quote && _visualStart.Character + 1 < line.Length && line[_visualStart.Character + 1] == quote;
                    i = _col; colEnd = _visualStart.Character;
                }
                while (i <= colEnd && i < line.Length) if (line[i++] == quote) { selectedQuote = true; break; }
            }
        }
        char At(int i) => i >= 0 && i < line.Length ? line[i] : '\0';
        if (!visEmpty && At(colStart) == quote)
        {
            if (visBefCurs)
            {
                colStart = FindNextQuote(line, colStart + 1, quote, null);
                if (colStart < 0) return false;
                colEnd = FindNextQuote(line, colStart + 1, quote, esc);
                if (colEnd < 0) { colEnd = colStart; colStart = _col; }
            }
            else
            {
                colEnd = FindPrevQuote(line, colStart, quote, null);
                if (At(colEnd) != quote) return false;
                colStart = FindPrevQuote(line, colEnd, quote, esc);
                if (At(colStart) != quote) { colStart = colEnd; colEnd = _col; }
            }
        }
        else if (At(colStart) == quote || !visEmpty)
        {
            var firstCol = colStart;
            if (!visEmpty) firstCol = visBefCurs ? FindNextQuote(line, colStart, quote, null) : FindPrevQuote(line, colStart, quote, null);
            colStart = 0;
            while (true)
            {
                colStart = FindNextQuote(line, colStart, quote, null);
                if (colStart < 0 || colStart > firstCol) return false;
                colEnd = FindNextQuote(line, colStart + 1, quote, esc);
                if (colEnd < 0) return false;
                if (colStart <= firstCol && firstCol <= colEnd) break;
                colStart = colEnd + 1;
            }
        }
        else
        {
            colStart = FindPrevQuote(line, colStart, quote, esc);
            if (At(colStart) != quote)
            {
                colStart = FindNextQuote(line, colStart, quote, null);
                if (colStart < 0) return false;
            }
            colEnd = FindNextQuote(line, colStart + 1, quote, esc);
            if (colEnd < 0) return false;
        }
        if (include)
        {
            if (At(colEnd + 1) is ' ' or '\t') { while (At(colEnd + 1) is ' ' or '\t') colEnd++; }
            else while (colStart > 0 && At(colStart - 1) is ' ' or '\t') colStart--;
        }
        if (!include && count < 2 && (visEmpty || !insideQuotes)) colStart++;
        _col = colStart;
        if (VisualActive)
        {
            if (visEmpty || (visBefCurs && !selectedQuote && (insideQuotes || (At(_visualStart.Character) != quote && (_visualStart.Character == 0 || At(_visualStart.Character - 1) != quote)))))
                _visualStart = new TextPosition(_line, _col);
        }
        else { oap.Start = new TextPosition(_line, _col); oap.Type = MType.Char; }
        _col = colEnd;
        if ((include || count > 1 || (!visEmpty && insideQuotes)) && IncCursor() == 2) inclusive = true;
        if (VisualActive)
        {
            if (visEmpty || visBefCurs) { if (!O.SelectionExclusive) DecCursor(); }
            else
            {
                if (insideQuotes || (!selectedQuote && At(_visualStart.Character) != quote && (_visualStart.Character >= line.Length || At(_visualStart.Character + 1) != quote)))
                { DecCursor(); _visualStart = new TextPosition(_line, _col); }
                _col = colStart;
            }
            if (_mode == VimMode.VisualLine) SetMode(VimMode.Visual);
        }
        else oap.Inclusive = inclusive;
        return true;
    }

    // ---- tags ----

    private sealed record TagInfo(string Name, bool Closing, int StartOff, int EndOff);

    private static readonly Regex TagRx = new(@"<(/?)([A-Za-z][^\s>/]*)[^>]*?(/?)>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private bool CurrentTag(OpArgs oap, int count, bool include)
    {
        if (!VisualActive || _visualStart == new TextPosition(_line, _col)) SetPcmark();
        // Materialise the buffer once (tag objects are rare): offsets <-> positions via line starts.
        var text = string.Join('\n', Enumerable.Range(0, LineCount).Select(L));
        var starts = new int[LineCount];
        var off = 0;
        for (var i = 0; i < LineCount; i++) { starts[i] = off; off += L(i).Length + 1; }
        int OffsetOf(int line, int col) => starts[line] + col;
        TextPosition PosOf(int offset)
        {
            var idx = Array.BinarySearch(starts, offset);
            var l = idx >= 0 ? idx : ~idx - 1;
            return new TextPosition(l, offset - starts[l]);
        }
        var tags = new List<TagInfo>();
        foreach (Match m in TagRx.Matches(text))
        {
            if (m.Groups[3].Length > 0) continue; // self-closing
            tags.Add(new TagInfo(m.Groups[2].Value, m.Groups[1].Length > 0, m.Index, m.Index + m.Length));
        }
        var pairs = new List<(TagInfo Open, TagInfo Close)>();
        var stack = new List<TagInfo>();
        foreach (var t in tags)
        {
            if (!t.Closing) stack.Add(t);
            else
            {
                var idx = stack.FindLastIndex(o => string.Equals(o.Name, t.Name, StringComparison.Ordinal));
                if (idx < 0) continue;
                pairs.Add((stack[idx], t));
                stack.RemoveRange(idx, stack.Count - idx);
            }
        }
        var cursorOff = OffsetOf(_line, Math.Min(_col, L(_line).Length));
        var startOff = cursorOff;
        var endOff = cursorOff;
        if (VisualActive && _visualStart != new TextPosition(_line, _col))
        {
            var a = OffsetOf(_visualStart.Line, _visualStart.Character);
            startOff = Math.Min(a, cursorOff);
            endOff = Math.Max(a, cursorOff);
        }
        var containing = pairs.Where(p => p.Open.StartOff <= startOff && p.Close.EndOff > endOff).OrderBy(p => p.Close.EndOff - p.Open.StartOff).ToList();
        // when the selection already equals the object, move outwards
        var chosen = -1;
        for (var i = 0; i < containing.Count; i++)
        {
            var p = containing[i];
            var s = include ? p.Open.StartOff : p.Open.EndOff;
            var e = include ? p.Close.EndOff - 1 : p.Close.StartOff - 1;
            if (VisualActive && _visualStart != new TextPosition(_line, _col) && s >= startOff && e <= endOff) { if (s == startOff && e == endOff) continue; if (s > startOff || e < endOff) continue; }
            chosen = i;
            break;
        }
        if (chosen < 0) return false;
        chosen += count - 1;
        if (chosen >= containing.Count) return false;
        var pick = containing[chosen];
        int so, eo;
        if (include) { so = pick.Open.StartOff; eo = pick.Close.EndOff - 1; }
        else
        {
            so = pick.Open.EndOff;
            eo = pick.Close.StartOff - 1;
            // a closing tag at the start of a line: end exclusive AT the '<' so the line break is part of the object
            var closeAt = PosOf(pick.Close.StartOff);
            if (closeAt.Character == 0 && !VisualActive && eo >= so)
            {
                oap.Start = PosOf(so);
                oap.Type = MType.Char;
                oap.Inclusive = false;
                _line = closeAt.Line; _col = 0;
                return true;
            }
            if (eo < so) { // empty tag content: operate on nothing
                _line = PosOf(so).Line; _col = PosOf(so).Character;
                if (VisualActive) return false;
                oap.Start = PosOf(so); oap.Inclusive = false; oap.Type = MType.Char; return true;
            }
        }
        var sp = PosOf(so);
        var ep = PosOf(eo);
        if (VisualActive) { _visualStart = sp; SetMode(VimMode.Visual); }
        else { oap.Start = sp; oap.Type = MType.Char; oap.Inclusive = true; }
        _line = ep.Line; _col = ep.Character;
        return true;
    }
}
