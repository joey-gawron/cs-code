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
        if (nw is '<' or 't')
        {
            var sb = new StringBuilder();
            while (true)
            {
                if (!Next(out var tk)) return NeedMoreArg(true);
                if (tk == Keys.Esc) return ParseResult.Cancelled;
                if (tk is '>' or Keys.CR) break;
                sb.Append(char.ConvertFromUtf32(tk));
            }
            c.SurroundTag = sb.ToString();
        }
        return ParseResult.Complete;
    }

    private void SurroundAddOperator(Cmd c, OpArgs oap)
    {
        if (!CanModify()) return;
        var (open, close) = SurroundPair(c.NChar, c.SurroundTag);
        var start = oap.Start;
        var end = oap.End;
        TextPosition endExcl;
        if (oap.Type == MType.Line)
        {
            start = new TextPosition(start.Line, VimText.FirstNonBlank(L(start.Line)));
            endExcl = new TextPosition(end.Line, L(end.Line).Length);
        }
        else endExcl = oap.Inclusive ? new TextPosition(end.Line, Math.Min(L(end.Line).Length, end.Character + (end.Character < L(end.Line).Length ? VimText.CharLen(L(end.Line), end.Character) : 0))) : end;
        // insert the closing text first so the start position stays valid
        ReplaceRange(endExcl, endExcl, close);
        ReplaceRange(start, start, open);
        _line = start.Line;
        _col = start.Character;
        _bs.OpStart = start;
        _bs.OpEnd = new TextPosition(endExcl.Line, endExcl.Character + open.Length * (start.Line == endExcl.Line ? 1 : 0) + close.Length - 1);
        CheckCursor();
    }

    /// <summary>Finds the surrounding delimiter pair of <paramref name="ch"/> around the cursor. Returns (openPos, openLen, closePos, closeLen).</summary>
    private bool FindSurround(int ch, out TextPosition openPos, out int openLen, out TextPosition closePos, out int closeLen)
    {
        openPos = closePos = default;
        openLen = closeLen = 1;
        var cur = new TextPosition(_line, _col);
        switch (ch)
        {
            case '(' or ')' or 'b' or '{' or '}' or 'B' or '[' or ']' or 'r' or '<' or '>' or 'a':
            {
                char open, close;
                if (ch is '(' or ')' or 'b') { open = '('; close = ')'; }
                else if (ch is '{' or '}' or 'B') { open = '{'; close = '}'; }
                else if (ch is '[' or ']' or 'r') { open = '['; close = ']'; }
                else { open = '<'; close = '>'; }
                var l = _line; var c = _col;
                var save = cur;
                if (CharAt(l, c) == open) c += 1;
                var o = FindMatchLimit(open, false, new TextPosition(l, c), unmatched: true);
                if (CharAt(save.Line, save.Character) == open) o = save;
                if (o is null) return false;
                var cl = FindMatchLimit(close, true, new TextPosition(o!.Value.Line, o.Value.Character + 1), unmatched: true);
                if (CharAt(save.Line, save.Character) == close && o is not null)
                {
                    // cursor on the closing char
                    var o2 = FindMatchLimit(open, false, save, unmatched: true);
                    if (o2 is null) return false;
                    o = o2;
                    cl = save;
                }
                if (cl is null) return false;
                openPos = o!.Value; closePos = cl.Value;
                return true;
            }
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
                var q = (char)ch;
                var s = CurLine;
                // quote pair on this line around (or after) the cursor
                var before = _col;
                var left = -1;
                for (var i = Math.Min(_col, s.Length - 1); i >= 0; i--) if (s[i] == q && (i == 0 || s[i - 1] != '\\')) { left = i; break; }
                if (left < 0) return false;
                var right = -1;
                for (var i = left + 1; i < s.Length; i++) if (s[i] == q && s[i - 1] != '\\') { right = i; break; }
                if (right < 0 || right < before && false) return false;
                if (right < _col)
                {
                    // the cursor is past this pair: find the next pair containing it
                    return false;
                }
                openPos = new TextPosition(_line, left);
                closePos = new TextPosition(_line, right);
                return true;
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

    private void CommentOperator(OpArgs oap)
    {
        if (!CanModify()) return;
        var first = oap.Start.Line;
        var last = oap.End.Line;
        ToggleComment(first, last);
        _line = first;
        if (oap.IsVisual || true) Beginline(BL.Sol | BL.Fix);
        _bs.OpStart = new TextPosition(first, 0);
        _bs.OpEnd = new TextPosition(last, 0);
        CheckCursor();
    }

    private void ToggleComment(int first, int last)
    {
        var tokens = Host.GetCommentTokens();
        var line = tokens.Line;
        string? bo = null, bc = null;
        if (string.IsNullOrEmpty(line)) { bo = tokens.BlockOpen; bc = tokens.BlockClose; if (string.IsNullOrEmpty(bo) || string.IsNullOrEmpty(bc)) { Error("E-comment: no comment syntax for this language"); return; } }
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
        if (!any) return;
        for (var l = first; l <= last; l++)
        {
            var s = L(l);
            if (VimText.IsBlank(s)) continue;
            string neu;
            if (allCommented)
            {
                var ind = s.Length - s.TrimStart(' ', '\t').Length;
                var body = s.Substring(ind);
                if (line is { Length: > 0 })
                {
                    body = body.Substring(line.Length);
                    if (body.StartsWith(' ')) body = body.Substring(1);
                }
                else
                {
                    body = body.Substring(bo!.Length);
                    if (body.StartsWith(' ')) body = body.Substring(1);
                    body = body.TrimEnd();
                    body = body.Substring(0, body.Length - bc!.Length);
                    if (body.EndsWith(' ')) body = body.Substring(0, body.Length - 1);
                }
                neu = s.Substring(0, ind) + body;
            }
            else
            {
                if (line is { Length: > 0 }) neu = s.Substring(0, minIndent) + line + " " + s.Substring(minIndent);
                else neu = s.Substring(0, minIndent) + bo + " " + s.Substring(minIndent) + " " + bc;
            }
            SetLine(l, neu);
        }
    }
}
