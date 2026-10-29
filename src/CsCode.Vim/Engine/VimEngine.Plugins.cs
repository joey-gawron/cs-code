using System.Text;
using CsCode.Core.Text;

namespace CsCode.Vim;

// Built-in emulations of vim-surround and vim-commentary (VSCodeVim parity), toggled by vim.surround / vim.commentary.
public sealed partial class VimEngine
{
    // ------------------------------------------------------------------------------------------------------------
    // surround
    // ------------------------------------------------------------------------------------------------------------

    private static (string Open, string Close) SurroundPair(int ch, string? tag)
    {
        switch (ch)
        {
            case ')' or 'b': return ("(", ")");
            case '(': return ("( ", " )");
            case ']' or 'r': return ("[", "]");
            case '[': return ("[ ", " ]");
            case '}' or 'B': return ("{", "}");
            case '{': return ("{ ", " }");
            case '>' or 'a': return ("<", ">");
            case '<' or 't':
            {
                var t = tag ?? "";
                if (t.Length == 0) return ("<>", "</>");
                var name = t.Split(' ', '\t')[0];
                return ($"<{t}>", $"</{name}>");
            }
            default:
                return (char.ConvertFromUtf32(ch), char.ConvertFromUtf32(ch));
        }
    }

    private ParseResult ParseSurroundDeleteChange(Cmd c)
    {
        // called for d/c followed by 's': ds{char} | cs{old}{new}[tag]
        if (!Next(out var ch)) return NeedMoreArg(true);
        if (ch == Keys.Esc) return ParseResult.Cancelled;
        c.NChar = ch;
        c.HasMotion = true;
        if (c.Op == OpType.SurroundDelete) return ParseResult.Complete;
        if (!Next(out var nw)) return NeedMoreArg(true);
        if (nw == Keys.Esc) return ParseResult.Cancelled;
        c.NChar2 = nw;
        if (nw is '<' or 't' && !ReadSurroundTag(c)) return _tagCancelled ? ParseResult.Cancelled : NeedMoreArg(true);
        return ParseResult.Complete;
    }

    private void SurroundAddOperator(Cmd c, OpArgs oap)
    {
        if (!CanModify()) return;
        var (open, close) = SurroundPair(c.NChar, c.SurroundTag);
        var start = oap.Start;
        var end = oap.End;
        if (oap.IsVisual && oap.Type == MType.Line)
        {
            // linewise Visual: the delimiters go on their own lines above and below (indent of the first line)
            var indent = L(start.Line).Substring(0, VimText.FirstNonBlank(L(start.Line)));
            ReplaceRange(new TextPosition(end.Line, L(end.Line).Length), new TextPosition(end.Line, L(end.Line).Length), "\n" + indent + close.TrimStart());
            ReplaceRange(new TextPosition(start.Line, 0), new TextPosition(start.Line, 0), indent + open.TrimEnd() + "\n");
            _line = start.Line;
            _col = 0;
            Beginline(BL.White | BL.Fix);
            _bs.OpStart = new TextPosition(start.Line, 0);
            _bs.OpEnd = new TextPosition(end.Line + 2, 0);
            CheckCursor();
            return;
        }
        TextPosition endExcl;
        if (oap.Type == MType.Line)
        {
            start = new TextPosition(start.Line, VimText.FirstNonBlank(L(start.Line)));
            endExcl = new TextPosition(end.Line, L(end.Line).Length);
        }
        else endExcl = oap.Inclusive ? new TextPosition(end.Line, Math.Min(L(end.Line).Length, end.Character + (end.Character < L(end.Line).Length ? VimText.CharLen(L(end.Line), end.Character) : 0))) : end;
        if (endExcl < start) endExcl = start;
        // insert the closing text first so the start position stays valid
        ReplaceRange(endExcl, endExcl, close);
        ReplaceRange(start, start, open);
        _line = start.Line;
        _col = start.Character;
        _bs.OpStart = start;
        _bs.OpEnd = new TextPosition(endExcl.Line, endExcl.Character + (start.Line == endExcl.Line ? open.Length : 0) + close.Length - 1);
        CheckCursor();
    }

    /// <summary>The innermost <paramref name="open"/>/<paramref name="close"/> pair enclosing the cursor (the cursor may be on either delimiter).</summary>
    private bool FindBracketPair(char open, char close, out TextPosition openPos, out TextPosition closePos)
    {
        openPos = closePos = default;
        var line = _line;
        var col = _col;
        var found = false;
        if (CharAt(line, col) == open) { openPos = new TextPosition(line, col); found = true; }
        else
        {
            var depth = 0;
            var l = line;
            var i = col - 1;
            while (!found && l >= 0)
            {
                var s = L(l);
                if (i >= s.Length) i = s.Length - 1;
                for (; i >= 0; i--)
                {
                    var ch = s[i];
                    if (ch == close) depth++;
                    else if (ch == open) { if (depth == 0) { openPos = new TextPosition(l, i); found = true; break; } depth--; }
                }
                l--;
                if (l >= 0) i = L(l).Length - 1;
            }
        }
        if (!found) return false;
        {
            var depth = 0;
            var l = openPos.Line;
            var i = openPos.Character + 1;
            while (l < LineCount)
            {
                var s = L(l);
                for (; i < s.Length; i++)
                {
                    var ch = s[i];
                    if (ch == open) depth++;
                    else if (ch == close) { if (depth == 0) { closePos = new TextPosition(l, i); return true; } depth--; }
                }
                l++;
                i = 0;
            }
        }
        return false;
    }

    /// <summary>Finds the surrounding delimiter pair of <paramref name="ch"/> around the cursor. Returns (openPos, openLen, closePos, closeLen).</summary>
    private bool FindSurround(int ch, out TextPosition openPos, out int openLen, out TextPosition closePos, out int closeLen)
    {
        openPos = closePos = default;
        openLen = closeLen = 1;
        switch (ch)
        {
            case '(' or ')' or 'b': return FindBracketPair('(', ')', out openPos, out closePos);
            case '{' or '}' or 'B': return FindBracketPair('{', '}', out openPos, out closePos);
            case '[' or ']' or 'r': return FindBracketPair('[', ']', out openPos, out closePos);
            case '<' or '>' or 'a': return FindBracketPair('<', '>', out openPos, out closePos);
            case 't':
            {
                var oap = new OpArgs();
                var sl = _line; var sc = _col;
                var ok = CurrentTag(oap, 1, include: true);
                var s = oap.Start; var e = new TextPosition(_line, _col);
                _line = sl; _col = sc;
                if (!ok) return false;
                var text = string.Join('\n', Enumerable.Range(0, LineCount).Select(L));
                var starts = new int[LineCount];
                var off = 0;
                for (var i = 0; i < LineCount; i++) { starts[i] = off; off += L(i).Length + 1; }
                var so = starts[s.Line] + s.Character;
                var eo = starts[e.Line] + e.Character;
                var gt = text.IndexOf('>', so);
                var lt = text.LastIndexOf('<', eo);
                if (gt < 0 || lt < 0) return false;
                int PosLine(int offset) { var idx = Array.BinarySearch(starts, offset); return idx >= 0 ? idx : ~idx - 1; }
                openPos = s; openLen = gt - so + 1;
                var cl2 = new TextPosition(PosLine(lt), lt - starts[PosLine(lt)]);
                closePos = cl2; closeLen = eo - lt + 1;
                return true;
            }
            default:
            {
                // a quote-like pair on the cursor line: quotes pair up from the start of the line; the cursor picks the pair it is in,
                // or the next pair after it (like the i" text object)
                var q = (char)ch;
                var s = CurLine;
                var quotes = new List<int>();
                for (var i = 0; i < s.Length; i++) if (s[i] == q && (i == 0 || s[i - 1] != '\\')) quotes.Add(i);
                for (var k = 0; k + 1 < quotes.Count; k += 2)
                {
                    if (_col <= quotes[k + 1])
                    {
                        openPos = new TextPosition(_line, quotes[k]);
                        closePos = new TextPosition(_line, quotes[k + 1]);
                        return true;
                    }
                }
                return false;
            }
        }
    }

    private void SurroundDeleteCommand(Cmd c)
    {
        if (!CanModify()) return;
        if (!FindSurround(c.NChar, out var op, out var oLen, out var cp, out var cLen)) { Beep(); return; }
        var trimPad = c.NChar is '(' or '{' or '[';
        // delete the closing delimiter first (keeps the opening position valid)
        var closeStart = cp;
        var closeEndCol = cp.Character + cLen;
        var openEndCol = op.Character + oLen;
        if (trimPad)
        {
            var ol = L(op.Line);
            while (openEndCol < ol.Length && ol[openEndCol] == ' ' && !(op.Line == cp.Line && openEndCol >= cp.Character)) openEndCol++;
            var cl = L(cp.Line);
            var cs = cp.Character;
            while (cs > 0 && cl[cs - 1] == ' ' && !(op.Line == cp.Line && cs - 1 < openEndCol)) cs--;
            closeStart = new TextPosition(cp.Line, cs);
        }
        ReplaceRange(closeStart, new TextPosition(cp.Line, closeEndCol), "");
        ReplaceRange(op, new TextPosition(op.Line, openEndCol), "");
        _line = op.Line;
        _col = op.Character;
        _bs.OpStart = op;
        CheckCursor();
    }

    private void SurroundChangeCommand(Cmd c)
    {
        if (!CanModify()) return;
        if (!FindSurround(c.NChar, out var op, out var oLen, out var cp, out var cLen)) { Beep(); return; }
        var (nopen, nclose) = SurroundPair(c.NChar2, c.SurroundTag);
        // vim-surround: cs with an opening bracket removes existing padding, then re-adds the new one
        var trimPad = c.NChar is '(' or '{' or '[';
        var closeStart = cp;
        var openEndCol = op.Character + oLen;
        if (trimPad)
        {
            var ol = L(op.Line);
            while (openEndCol < ol.Length && ol[openEndCol] == ' ' && !(op.Line == cp.Line && openEndCol >= cp.Character)) openEndCol++;
            var cl = L(cp.Line);
            var cs = cp.Character;
            while (cs > 0 && cl[cs - 1] == ' ' && !(op.Line == cp.Line && cs - 1 < openEndCol)) cs--;
            closeStart = new TextPosition(cp.Line, cs);
        }
        ReplaceRange(closeStart, new TextPosition(cp.Line, cp.Character + cLen), nclose);
        ReplaceRange(op, new TextPosition(op.Line, openEndCol), nopen);
        _line = op.Line;
        _col = op.Character;
        CheckCursor();
    }

    // ------------------------------------------------------------------------------------------------------------
    // commentary
    // ------------------------------------------------------------------------------------------------------------

    private void CommentOperator(OpArgs oap, bool block)
    {
        if (!CanModify()) return;
        var first = oap.Start.Line;
        var last = oap.End.Line;
        var col = ToggleComment(first, last, block, oap.Start.Line == first ? oap.Start.Character : 0);
        _line = first;
        _col = col;
        _setCurswant = true;
        _bs.OpStart = new TextPosition(first, 0);
        _bs.OpEnd = new TextPosition(last, 0);
        CheckCursor();
    }

    /// <summary>Toggles comments on the lines; returns where the cursor column of the first line ends up (it follows its text).</summary>
    private int ToggleComment(int first, int last, bool block, int cursorCol)
    {
        var tokens = Host.GetCommentTokens();
        var line = block && !string.IsNullOrEmpty(tokens.BlockOpen) && !string.IsNullOrEmpty(tokens.BlockClose) ? null : tokens.Line;
        string? bo = null, bc = null;
        if (string.IsNullOrEmpty(line)) { bo = tokens.BlockOpen; bc = tokens.BlockClose; if (string.IsNullOrEmpty(bo) || string.IsNullOrEmpty(bc)) { Error("E-comment: no comment syntax for this language"); return cursorCol; } }
        var allCommented = true;
        var any = false;
        var minIndent = int.MaxValue;
        for (var l = first; l <= last; l++)
        {
            var s = L(l);
            if (VimText.IsBlank(s)) continue;
            any = true;
            var t = s.TrimStart(' ', '\t');
            minIndent = Math.Min(minIndent, s.Length - t.Length);
            if (line is { Length: > 0 }) { if (!t.StartsWith(line, StringComparison.Ordinal)) allCommented = false; }
            else if (!(t.StartsWith(bo!, StringComparison.Ordinal) && t.TrimEnd().EndsWith(bc!, StringComparison.Ordinal))) allCommented = false;
        }
        if (!any) return cursorCol;
        var newCol = cursorCol;
        for (var l = first; l <= last; l++)
        {
            var s = L(l);
            if (VimText.IsBlank(s)) continue;
            string neu;
            if (allCommented)
            {
                var ind = s.Length - s.TrimStart(' ', '\t').Length;
                var body = s.Substring(ind);
                var tokenLen = line is { Length: > 0 } ? line.Length : bo!.Length;
                body = body.Substring(tokenLen);
                if (body.StartsWith(' ')) { body = body.Substring(1); tokenLen++; }
                if (line is not { Length: > 0 })
                {
                    body = body.TrimEnd();
                    body = body.Substring(0, body.Length - bc!.Length);
                    if (body.EndsWith(' ')) body = body.Substring(0, body.Length - 1);
                }
                neu = s.Substring(0, ind) + body;
                if (l == first) newCol = cursorCol >= ind + tokenLen ? cursorCol - tokenLen : cursorCol > ind ? ind : cursorCol;
            }
            else
            {
                if (line is { Length: > 0 }) neu = s.Substring(0, minIndent) + line + " " + s.Substring(minIndent);
                else neu = s.Substring(0, minIndent) + bo + " " + s.Substring(minIndent) + " " + bc;
                if (l == first && cursorCol >= minIndent) newCol = cursorCol + (line is { Length: > 0 } ? line.Length : bo!.Length) + 1;
            }
            SetLine(l, neu);
        }
        return newCol;
    }
}
