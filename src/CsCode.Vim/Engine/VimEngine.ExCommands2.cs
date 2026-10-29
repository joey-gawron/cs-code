using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsCode.Core.Text;

namespace CsCode.Vim;

public sealed partial class VimEngine
{
    private readonly record struct LineEdit(int Line, int OldCount, string[] NewLines);

    // ------------------------------------------------------------------------------------------------------------
    // batched line edits (one host ApplyEdits for the whole batch)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Applies non-overlapping, ascending per-line replacements as one buffer change, recording undo and adjusting marks.</summary>
    private void ApplyLineBatch(List<LineEdit> edits)
    {
        if (edits.Count == 0) return;
        // undo entries bottom-to-top so each entry's Start is valid in the numbering of the state it starts from
        var runs = new List<(int Start, int OldCount, int NewCount)>();
        foreach (var e in edits)
        {
            if (runs.Count > 0 && runs[^1].Start + runs[^1].OldCount == e.Line) runs[^1] = (runs[^1].Start, runs[^1].OldCount + e.OldCount, runs[^1].NewCount + e.NewLines.Length);
            else runs.Add((e.Line, e.OldCount, e.NewLines.Length));
        }
        for (var i = runs.Count - 1; i >= 0; i--)
            SaveUndo(runs[i].Start, runs[i].OldCount, runs[i].NewCount, saveULine: runs.Count == 1 && runs[i].OldCount == 1);
        var vedits = new VimEdit[edits.Count];
        for (var i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            var oldLast = e.Line + e.OldCount - 1;
            if (e.OldCount == 1 && e.NewLines.Length == 1)
            {
                var old = L(e.Line);
                var neu = e.NewLines[0];
                var pre = CommonPrefix(old, neu);
                var maxSuf = Math.Min(old.Length, neu.Length) - pre;
                var suf = 0;
                while (suf < maxSuf && old[old.Length - 1 - suf] == neu[neu.Length - 1 - suf]) suf++;
                while (pre > 0 && pre < old.Length && pre < neu.Length && (VimText.IsCombining(old[pre]) || char.IsLowSurrogate(old[pre]))) pre--;
                vedits[i] = new VimEdit(new TextPosition(e.Line, pre), new TextPosition(e.Line, old.Length - suf), neu.Substring(pre, neu.Length - pre - suf));
            }
            else vedits[i] = new VimEdit(new TextPosition(e.Line, 0), new TextPosition(oldLast, L(oldLast).Length), string.Join('\n', e.NewLines));
        }
        var firstEditLine = edits[0].Line;
        _buf.ApplyEdits(vedits);
        _bs.KnownVersion = _buf.Version;
        // marks: adjust bottom-to-top for line count changes
        for (var i = edits.Count - 1; i >= 0; i--)
        {
            var e = edits[i];
            var delta = e.NewLines.Length - e.OldCount;
            if (delta == 0) continue;
            if (delta < 0)
            {
                if (e.OldCount > e.NewLines.Length) _bs.MarkAdjust(e.Line + e.NewLines.Length, e.Line + e.OldCount - 1, 0, delta, delete: true);
            }
            else _bs.MarkAdjust(e.Line + e.OldCount, int.MaxValue / 2, delta, 0, delete: false);
        }
        NoteChange(firstEditLine, 0);
    }

    // ------------------------------------------------------------------------------------------------------------
    // :substitute
    // ------------------------------------------------------------------------------------------------------------

    private sealed class SubFlags
    {
        public bool Global, IgnoreCase, NoIgnoreCase, CountOnly, NoError, Confirm, Print, UseLastSearch;
        public string Text = "";
    }

    private static SubFlags ParseSubFlags(string flags, SubFlags? previous, ref int idx, bool allowAmp)
    {
        var f = new SubFlags();
        var i = idx;
        if (allowAmp && i < flags.Length && flags[i] == '&' && previous is not null)
        {
            f.Global = previous.Global; f.IgnoreCase = previous.IgnoreCase; f.NoIgnoreCase = previous.NoIgnoreCase; f.Confirm = previous.Confirm; f.NoError = previous.NoError; f.Print = previous.Print;
            i++;
        }
        for (; i < flags.Length; i++)
        {
            var c = flags[i];
            switch (c)
            {
                case 'g': f.Global = !f.Global; break;
                case 'i': f.IgnoreCase = true; f.NoIgnoreCase = false; break;
                case 'I': f.NoIgnoreCase = true; f.IgnoreCase = false; break;
                case 'n': f.CountOnly = true; break;
                case 'e': f.NoError = true; break;
                case 'c': f.Confirm = true; break;
                case 'p' or '#' or 'l': f.Print = true; break;
                case 'r': f.UseLastSearch = true; break;
                default: idx = i; return f;
            }
        }
        idx = i;
        return f;
    }

    private SubFlags? _lastSubFlags;

    private bool ExSubstitute(ExCmd c)
    {
        var args = c.Args;
        var mem = _s.Search;
        string pat, rep;
        SubFlags flags;
        var idx = 0;
        if (args.Length > 0 && !char.IsLetterOrDigit(args[0]) && args[0] is not ('"' or '|' or '\\' or ' ' or '\t'))
        {
            var delim = args[0];
            var (p, restA) = SplitUnescaped(args[1..], delim);
            pat = p;
            string restB;
            if (restA is null) { rep = ""; restB = ""; }
            else
            {
                var (r, rb) = SplitUnescaped(restA, delim);
                rep = r;
                restB = rb ?? "";
            }
            if (pat.Length == 0) pat = mem.Pattern;
            else if (delim != '/' || true) pat = UnescapeDelimiter(pat, delim);
            if (pat.Length == 0) { ExError("E476: Invalid command"); return false; }
            // `~` and `&` inside the replacement refer to the previous replacement / the match
            rep = ExpandTildeInReplacement(rep, mem.SubReplacement);
            mem.SubReplacement = rep;
            mem.SubPattern = pat;
            mem.Pattern = pat;
            _s.Registers.LastSearch = pat;
            mem.HighlightActive = true;
            var fl = restB.TrimStart();
            var fi = 0;
            flags = ParseSubFlags(fl, _lastSubFlags, ref fi, allowAmp: true);
            idx = fi;
            args = fl[idx..].Trim();
        }
        else
        {
            // :s [flags] [count] repeats the last substitute with the same pattern and replacement
            if (mem.SubPattern.Length == 0) { ExError("E33: No previous substitute regular expression"); return false; }
            var fl = args.TrimStart();
            var fi = 0;
            flags = ParseSubFlags(fl, _lastSubFlags, ref fi, allowAmp: true);
            if (flags.UseLastSearch) mem.SubPattern = mem.Pattern;
            pat = flags.UseLastSearch || mem.SubPattern.Length == 0 ? mem.Pattern : mem.SubPattern;
            rep = mem.SubReplacement;
            args = fl[fi..].Trim();
        }
        var l1 = c.Line1; var l2 = c.Line2;
        if (args.Length > 0 && char.IsDigit(args[0]))
        {
            var n = ParseIntOr(new string(args.TakeWhile(char.IsDigit).ToArray()), 1);
            l1 = l2; l2 = Math.Min(LastLine, l1 + n - 1);
        }
        _lastSubFlags = flags;
        return DoSubstitute(pat, rep, flags, l1, l2);
    }

    private bool ExSubstituteRepeat(ExCmd c)
    {
        var mem = _s.Search;
        if (mem.SubPattern.Length == 0) { ExError("E33: No previous substitute regular expression"); return false; }
        var fl = c.Args.TrimStart();
        var fi = 0;
        var keep = c.Name == "&&";
        var flags = ParseSubFlags(fl, keep ? _lastSubFlags : null, ref fi, allowAmp: true);
        if (keep && _lastSubFlags is not null && fl.Length == 0) flags = _lastSubFlags;
        var pat = c.Name == "~" || flags.UseLastSearch ? mem.Pattern : (mem.SubPattern.Length > 0 ? mem.SubPattern : mem.Pattern);
        var rest = fl[fi..].Trim();
        var l1 = c.Line1; var l2 = c.Line2;
        if (rest.Length > 0 && char.IsDigit(rest[0]))
        {
            var n = ParseIntOr(new string(rest.TakeWhile(char.IsDigit).ToArray()), 1);
            l1 = l2; l2 = Math.Min(LastLine, l1 + n - 1);
        }
        mem.SubPattern = pat;
        return DoSubstitute(pat, mem.SubReplacement, flags, l1, l2);
    }

    private static (string Head, string? Tail) SplitUnescaped(string s, char delim)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { i++; continue; }
            if (s[i] == delim) return (s[..i], s[(i + 1)..]);
        }
        return (s, null);
    }

    private static string UnescapeDelimiter(string pat, char delim)
    {
        if (delim == '\\') return pat;
        var sb = new StringBuilder();
        for (var i = 0; i < pat.Length; i++)
        {
            if (pat[i] == '\\' && i + 1 < pat.Length && pat[i + 1] == delim && !char.IsLetterOrDigit(delim) && delim is not ('.' or '*' or '[' or ']' or '^' or '$' or '~' or '(' or ')' or '|' or '+' or '?' or '{' or '}' or '=' or '@' or '%' or '&' or '<' or '>'))
            {
                sb.Append(delim);
                i++;
            }
            else if (pat[i] == '\\' && i + 1 < pat.Length) { sb.Append(pat[i]).Append(pat[i + 1]); i++; }
            else sb.Append(pat[i]);
        }
        return sb.ToString();
    }

    private string ExpandTildeInReplacement(string rep, string previous)
    {
        if (rep.IndexOf('~') < 0) return rep;
        var sb = new StringBuilder();
        for (var i = 0; i < rep.Length; i++)
        {
            if (rep[i] == '\\' && i + 1 < rep.Length) { sb.Append(rep[i]).Append(rep[i + 1]); i++; }
            else if (rep[i] == '~' && O.Magic) sb.Append(previous);
            else sb.Append(rep[i]);
        }
        return sb.ToString();
    }

    private const char LineBreakMark = '\u0001';

    /// <summary>Builds the replacement for one match. Line breaks are returned as <see cref="LineBreakMark"/>.</summary>
    private string ExpandReplacement(string rep, Match m, string text, int matchStart, int matchEnd, string? wholeText = null)
    {
        var sb = new StringBuilder();
        var caseNext = '\0';   // u / l for the next char
        var caseAll = '\0';    // U / L until \E
        void Add(string s)
        {
            foreach (var ch in s)
            {
                var o = ch;
                if (caseNext != '\0') { o = caseNext == 'u' ? char.ToUpperInvariant(o) : char.ToLowerInvariant(o); caseNext = '\0'; }
                else if (caseAll != '\0') o = caseAll == 'U' ? char.ToUpperInvariant(o) : char.ToLowerInvariant(o);
                sb.Append(o);
            }
        }
        for (var i = 0; i < rep.Length; i++)
        {
            var c = rep[i];
            if (c == '\\' && i + 1 < rep.Length)
            {
                var e = rep[++i];
                switch (e)
                {
                    case '&': Add(O.Magic ? "&" : (wholeText ?? text.Substring(matchStart, matchEnd - matchStart))); break;
                    case 'n': Add("\0"); break;
                    case 'r': sb.Append(LineBreakMark); break;
                    case 't': Add("\t"); break;
                    case '\\': Add("\\"); break;
                    case >= '0' and <= '9':
                    {
                        if (e == '0') Add(wholeText ?? text.Substring(matchStart, matchEnd - matchStart));
                        else if (m.Groups[e - '0'] is { Success: true } g) Add(g.Value);
                        break;
                    }
                    case 'u' or 'l': caseNext = e; break;
                    case 'U' or 'L': caseAll = e; break;
                    case 'e' or 'E': caseAll = '\0'; caseNext = '\0'; break;
                    case '=': throw new VimRegexException("E-sub: \\= (expression) replacement is not supported");
                    default: Add(e.ToString()); break;
                }
                continue;
            }
            if (c == '&' && O.Magic) { Add(wholeText ?? text.Substring(matchStart, matchEnd - matchStart)); continue; }
            if (c is '\r') { sb.Append(LineBreakMark); continue; }
            Add(c.ToString());
        }
        return sb.ToString();
    }

    private bool DoSubstitute(string pattern, string rep, SubFlags flags, int l1, int l2)
    {
        if (!CanModify() && !flags.CountOnly) return false;
        VimPattern? pat;
        try
        {
            var ic = flags.IgnoreCase ? true : flags.NoIgnoreCase ? false : (bool?)null;
            pat = ic is null ? CompilePattern(pattern, report: false) : VimRegex.Compile((ic.Value ? "\\c" : "\\C") + pattern, O.IgnoreCase, O.SmartCase, O.Magic, _s.Search.SubReplacement);
        }
        catch (VimRegexException ex) { ExError(ex.Message); return false; }
        if (pat is null) { ExError(VimRegexMessage(pattern)); return false; }
        var global = flags.Global ^ O.GDefault;
        var ask = flags.Confirm && !flags.CountOnly && !_globalBusy && _nest == 0;
        var work = ask ? SubstituteAskWork(pat, rep, flags, global, l1, l2) : SubstituteWork(pat, rep, flags, global, l1, l2);
        if (_nest > 0 || _globalBusy) { DrainEnumerator(work); return _subResultOk; }
        StartWork(work);
        // run the first slice right away so small substitutions finish inside the key that started them
        return true;
    }

    private string VimRegexMessage(string pattern)
    {
        try { VimRegex.Compile(pattern, O.IgnoreCase, O.SmartCase, O.Magic); return "E486: Pattern not found: " + pattern; }
        catch (VimRegexException ex) { return ex.Message; }
    }

    private bool _subResultOk;

    private static void DrainEnumerator(IEnumerator<int> e)
    {
        while (e.MoveNext()) { }
    }

    private bool _awaitKey;
    private int _confirmKey;

    /// <summary>Position of an offset in the search snapshot with one virtual newline after the last line.</summary>
    private TextPosition PosOfOffsetV(int offset, int textLength) => offset > textLength ? new TextPosition(LineCount, 0) : PosOfOffset(offset);

    /// <summary>Waits (cooperatively) for the next key and answers a ":s///c" prompt: y n a q l Esc, or 0 for keys that are not an answer.</summary>
    private IEnumerator<int> AskConfirm(string prompt)
    {
        Message(prompt, VimMessageKind.Info);
        while (true)
        {
            _confirmKey = 0;
            _awaitKey = true;
            yield return 0;
            var k = _confirmKey;
            if (k is 'y' or 'n' or 'a' or 'q' or 'l' or Keys.Esc or 3) { _confirmKey = k; yield break; }
        }
    }

    /// <summary>Finds the next match of <paramref name="pat"/> that starts in line <paramref name="firstLnum"/> at or after <paramref name="col"/>; multi-line patterns see one virtual newline after the last line.</summary>
    private (TextPosition S, TextPosition E, Match M)? FindSubMatch(VimPattern pat, int firstLnum, string cur, int col)
    {
        if (!pat.Multiline)
        {
            var mm = pat.Regex.Match(cur, Math.Min(col, cur.Length));
            if (!mm.Success) return null;
            var (s0, e0) = GroupSpan(mm);
            return (new TextPosition(firstLnum, s0), new TextPosition(firstLnum, Math.Min(e0, cur.Length)), mm);
        }
        EnsureSnapshot();
        var m = pat.Regex.Match(_snapText! + "\n", _snapStarts![firstLnum] + col);
        if (!m.Success) return null;
        var (s, e) = GroupSpan(m);
        var textLength = _snapText!.Length;
        var sp = PosOfOffsetV(s, textLength);
        if (sp.Line != firstLnum) return null;     // the match does not start in this line
        return (sp, PosOfOffsetV(e, textLength), m);
    }

    private string ExpandSubMatch(VimPattern pat, string rep, Match m, TextPosition sp)
    {
        var (s0, e0) = GroupSpan(m);
        var src = pat.Multiline ? _snapText! + "\n" : L(sp.Line);
        return ExpandReplacement(rep, m, src, s0, e0, src.Substring(Math.Min(s0, src.Length), Math.Max(0, Math.Min(e0, src.Length) - Math.Min(s0, src.Length))));
    }

    private IEnumerator<int> SubstituteWork(VimPattern pat, string rep, SubFlags flags, bool global, int l1, int l2)
    {
        // A port of the loop of do_sub: per starting line the new text is built piece by piece; a match that spans line breaks
        // continues in the line where it ended. Nothing is written to the buffer until all lines are done (one batched edit).
        _subResultOk = false;
        var edits = new List<LineEdit>();
        var subs = 0;
        var linesChanged = 0;
        var matchedLines = 0;
        var gotMatch = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var origCursor = new TextPosition(_line, _col);
        var deltaBefore = 0;       // lines added by the substitutions of earlier line groups
        var lastGroupLine = -1;    // new line number of the group with the last substitution
        var lastGroupBreaks = 0;
        var lnum = l1;
        var line2 = Math.Min(l2, LastLine);

        while (lnum <= line2)
        {
            var cur = L(lnum);
            var f = FindSubMatch(pat, lnum, cur, 0);
            if (f is null) { lnum++; if ((lnum & 63) == 0 && sw.ElapsedMilliseconds >= 2) { sw.Restart(); yield return 0; } continue; }
            var subFirst = lnum;
            var matchcol = 0;
            var prevMatchcol = -1;
            var copycol = 0;
            StringBuilder? sbNew = null;
            var skipMatch = false;
            var doAllHere = global;
            var nsubsHere = 0;
            while (true)
            {
                var (sp, ep, m) = f!.Value;
                var doAgain = false;
                if (!gotMatch && !_globalBusy && !flags.CountOnly) SetPcmark();
                gotMatch = true;
                var nmatch = ep.Line - subFirst + 1;
                if (ep.Line > LastLine)
                {
                    // the match included the line break of the last line: it goes no further than the end of the buffer
                    ep = new TextPosition(LastLine, L(LastLine).Length);
                    nmatch = LastLine - subFirst + 1;
                    skipMatch = true;
                }
                // 1. an empty match right after the previous match does not count (except the first one)
                if (matchcol == prevMatchcol && ep.Line == subFirst && matchcol == ep.Character)
                {
                    if (matchcol >= cur.Length) skipMatch = true;
                    else matchcol += VimText.CharLen(cur, matchcol);
                }
                else
                {
                    matchcol = ep.Character;
                    prevMatchcol = matchcol;
                    // 3. substitute
                    string expansion;
                    try { expansion = ExpandSubMatch(pat, rep, m, sp); }
                    catch (VimRegexException ex) { ExError(ex.Message); yield break; }
                    if (!flags.CountOnly)
                    {
                        sbNew ??= new StringBuilder(cur.Length + 16);
                        sbNew.Append(cur, copycol, Math.Max(0, sp.Character - copycol));
                        sbNew.Append(expansion);
                    }
                    copycol = ep.Character;
                    subs++;
                    nsubsHere++;
                    if (nmatch > 1)
                    {
                        subFirst += nmatch - 1;
                        cur = L(subFirst);
                        doAgain = subFirst <= line2;
                        if (!doAgain) doAllHere = false;
                    }
                }
                // 4. next match in this line
                var lastone = skipMatch || !(doAllHere || doAgain) || (matchcol >= cur.Length && nmatch <= 1 && !pat.Multiline);
                if (lastone) break;
                f = FindSubMatch(pat, subFirst, cur, matchcol);
                if (f is null) break;
            }
            if (nsubsHere > 0 && !flags.CountOnly)
            {
                var tail = copycol < cur.Length ? cur.Substring(copycol) : "";
                var result = (sbNew?.ToString() ?? "") + tail;
                var parts = result.Split(LineBreakMark);
                edits.Add(new LineEdit(lnum, subFirst - lnum + 1, parts));
                linesChanged++;
                lastGroupLine = lnum + deltaBefore;
                lastGroupBreaks = parts.Length - 1;
                deltaBefore += parts.Length - (subFirst - lnum + 1);
            }
            if (nsubsHere > 0) matchedLines++;
            lnum = subFirst + 1;
            if ((lnum & 63) == 0 && sw.ElapsedMilliseconds >= 2) { sw.Restart(); yield return 0; }
        }

        if (subs == 0)
        {
            if (!gotMatch && !flags.NoError)
            {
                if (_globalBusy) { _globalNoMatch = true; }
                else ExError($"E486: Pattern not found: {_s.Search.Pattern}");
            }
            yield break;
        }
        _subResultOk = true;
        if (flags.CountOnly)
        {
            Message($"{subs} match{(subs == 1 ? "" : "es")} on {matchedLines} line{(matchedLines == 1 ? "" : "s")}");
            _line = origCursor.Line; _col = origCursor.Character;
            yield break;
        }
        if (_globalBusy) _globalSubs += subs;
        // like do_sub, the undo entry records the cursor at the start of the first substituted line
        _line = Math.Clamp(edits[0].Line, 0, LastLine);
        _col = 0;
        ApplyLineBatch(edits);
        var delta = edits.Sum(e => e.NewLines.Length - e.OldCount);
        _bs.OpStart = new TextPosition(l1, 0);
        _bs.OpEnd = new TextPosition(Math.Clamp(l2 + delta, 0, LastLine), 0);
        _line = Math.Clamp(lastGroupLine + lastGroupBreaks, 0, LastLine);
        Beginline(BL.White | BL.Fix);
        if (subs > O.Report && !_globalBusy) Message($"{subs} substitution{(subs == 1 ? "" : "s")} on {linesChanged} line{(linesChanged == 1 ? "" : "s")}");
        yield return 0;
    }

    /// <summary>":s///c": the same loop, but on the live buffer so every answered prompt shows its result; the next key answers each prompt.</summary>
    private IEnumerator<int> SubstituteAskWork(VimPattern pat, string rep, SubFlags flags, bool global, int l1, int l2)
    {
        _subResultOk = false;
        var subs = 0;
        var gotMatch = false;
        var doAll = false;
        var quit = false;
        var line2 = Math.Min(l2, LastLine);
        var lnum = l1;
        var lastLine = -1;
        var cleared = false;     // 'a' or 'l' answered: the command ends like one without confirmation
        var savedLastChange = _bs.LastChange;
        while (lnum <= line2 && !quit)
        {
            var matchcol = 0;
            var prevMatchcol = -1;
            var skipMatch = false;
            while (!quit)
            {
                var cur = L(lnum);
                var f = FindSubMatch(pat, lnum, cur, matchcol);
                if (f is null) break;
                var (sp, ep, m) = f.Value;
                if (!gotMatch) SetPcmark();
                gotMatch = true;
                var nmatch = ep.Line - lnum + 1;
                if (ep.Line > LastLine)
                {
                    ep = new TextPosition(LastLine, L(LastLine).Length);
                    nmatch = LastLine - lnum + 1;
                    skipMatch = true;
                }
                if (matchcol == prevMatchcol && ep.Line == lnum && matchcol == ep.Character)
                {
                    if (matchcol >= cur.Length) skipMatch = true; else matchcol += VimText.CharLen(cur, matchcol);
                    if (skipMatch || !global) break;
                    continue;
                }
                string expansion;
                try { expansion = ExpandSubMatch(pat, rep, m, sp); }
                catch (VimRegexException ex) { ExError(ex.Message); yield break; }
                var doThis = true;
                if (!doAll)
                {
                    _line = sp.Line; _col = Math.Min(sp.Character, L(sp.Line).Length);
                    _setCurswant = true;
                    var ev = AskConfirm($"replace with {expansion.Replace(LineBreakMark, '\r')} (y/n/a/q/l/^E/^Y)?");
                    while (ev.MoveNext()) yield return 0;
                    switch (_confirmKey)
                    {
                        case 'y': break;
                        case 'l': quit = true; cleared = true; break;
                        case 'a': doAll = true; cleared = true; break;
                        case 'n': doThis = false; break;
                        default: quit = true; doThis = false; break;
                    }
                }
                if (doThis)
                {
                    var newText = expansion.Replace(LineBreakMark, '\n');
                    var breaks = 0;
                    foreach (var ch in newText) if (ch == '\n') breaks++;
                    ReplaceRange(sp, ep, newText);
                    _bs.LastChange = savedLastChange;   // '. is only set when the command is done
                    subs++;
                    lastLine = lnum;
                    line2 += breaks - (nmatch - 1);
                    var lastSeg = newText.Contains('\n') ? newText.Substring(newText.LastIndexOf('\n') + 1) : newText;
                    lnum += breaks;
                    matchcol = (breaks > 0 ? 0 : sp.Character) + lastSeg.Length;
                    prevMatchcol = matchcol;
                    if (!global && nmatch <= 1) break;
                }
                else
                {
                    if (nmatch > 1) { matchcol = L(lnum).Length; skipMatch = true; }
                    else { matchcol = ep.Character; prevMatchcol = matchcol; }
                    if (!global || skipMatch) break;
                }
                if (matchcol >= L(lnum).Length && nmatch <= 1 && !pat.Multiline) break;
            }
            lnum++;
        }
        if (subs == 0)
        {
            if (!gotMatch && !flags.NoError) ExError($"E486: Pattern not found: {_s.Search.Pattern}");
            yield break;
        }
        _subResultOk = true;
        _bs.OpStart = new TextPosition(l1, 0);
        _bs.OpEnd = new TextPosition(Math.Clamp(line2, 0, LastLine), 0);
        _bs.LastChange = new TextPosition(Math.Clamp(lastLine, 0, LastLine), 0);
        if (cleared || doAll)
        {
            _line = Math.Clamp(lastLine, 0, LastLine);
            Beginline(BL.White | BL.Fix);
        }
        else CheckCursor();
        if (subs > O.Report) Message($"{subs} substitution{(subs == 1 ? "" : "s")}");
        yield return 0;
    }

    // ------------------------------------------------------------------------------------------------------------
    // :global
    // ------------------------------------------------------------------------------------------------------------

    private bool _globalBusy;
    private bool _globalNoMatch;
    private int _globalSubs;

    private bool ExGlobal(ExCmd c, bool invert)
    {
        var a = c.Args;
        if (a.Length == 0) { ExError("E148: Regular expression missing from global"); return false; }
        var delim = a[0];
        if (char.IsLetterOrDigit(delim) || delim is '"' or '|' or '\\') { ExError("E146: Regular expressions can't be delimited by letters"); return false; }
        var (pat, cmdText) = SplitUnescaped(a[1..], delim);
        if (pat.Length == 0) pat = _s.Search.Pattern;
        else pat = UnescapeDelimiter(pat, delim);
        if (pat.Length == 0) { ExError("E476: Invalid command"); return false; }
        var cmd = (cmdText ?? "").Trim();
        if (cmd.Length == 0) cmd = "p";
        _s.Search.Pattern = pat;
        _s.Registers.LastSearch = pat;
        _s.Search.HighlightActive = true;
        var p = CompilePattern(pat);
        if (p is null) return false;
        if (_globalBusy)
        {
            // a :global inside a :global only looks at the current line
            var here = new List<Hit>();
            MatchesInLine(p, _line, here);
            if ((here.Count > 0) != invert) ExecuteExLine(cmd);
            return true;
        }
        var l1 = c.HasRange ? c.Line1 : 0;
        var l2 = c.HasRange ? c.Line2 : LastLine;
        var work = GlobalWork(p, pat, cmd, invert, l1, l2);
        if (_nest > 0 || _globalBusy) { DrainEnumerator(work); return true; }
        StartWork(work);
        return true;
    }

    private IEnumerator<int> GlobalWork(VimPattern p, string pat, string cmd, bool invert, int l1, int l2)
    {
        var marked = new List<int>();
        var hits = new List<Hit>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var l = l1; l <= l2 && l <= LastLine; l++)
        {
            MatchesInLine(p, l, hits);
            if ((hits.Count > 0) != invert) marked.Add(l);
            if ((l & 255) == 0 && sw.ElapsedMilliseconds >= 2) { sw.Restart(); yield return 0; }
        }
        if (marked.Count == 0)
        {
            if (invert) ExError($"E538: Pattern found in every line: {pat}");
            else ExError($"E486: Pattern not found: {pat}");
            yield break;
        }
        _globalBusy = true;
        var saved = _noJumpList;
        SetPcmark();
        _noJumpList = true;
        _bs.GlobalLines = marked;
        try
        {
            var trimmed = cmd.Trim();
            // fast path: :g/pat/d deletes all matching lines in one batch
            if (trimmed is "d" or "delete")
            {
                GlobalDeleteBatch(marked);
                yield break;
            }
            var startCount = LineCount;
            var executed = 0;
            for (var i = 0; i < marked.Count; i++)
            {
                var l = marked[i];
                if (l < 0 || l > LastLine) continue;
                _line = l; _col = 0;
                ExecuteExLine(cmd);
                executed++;
                if ((i & 15) == 0 && sw.ElapsedMilliseconds >= 2) { sw.Restart(); yield return 0; }
            }
            _ = startCount; _ = executed;
            if (_globalNoMatch) { }
        }
        finally
        {
            _bs.GlobalLines = null;
            _globalBusy = false;
            _globalNoMatch = false;
            _noJumpList = saved;
        }
    }

    private void GlobalDeleteBatch(List<int> marked)
    {
        if (!CanModify()) return;
        var lastMarked = marked[^1];   // the list is adjusted while lines are deleted
        // register effects of deleting each line in turn: unnamed/"1.."9 end up with the last lines deleted
        var oap = _oap;
        var runs = new List<(int Start, int Count)>();
        foreach (var l in marked)
        {
            if (runs.Count > 0 && runs[^1].Start + runs[^1].Count == l) runs[^1] = (runs[^1].Start, runs[^1].Count + 1);
            else runs.Add((l, 1));
        }
        var tail = marked.Count > 9 ? marked.GetRange(marked.Count - 9, 9) : marked;
        foreach (var l in tail)
        {
            oap.ResetForMotion(OpType.Delete, 0);
            oap.Type = MType.Line;
            oap.Start = new TextPosition(l, 0);
            oap.End = new TextPosition(l, 0);
            oap.LineCount = 1;
            YankForDelete(oap);
        }
        // delete bottom-to-top as ONE undo step; every run is a separate undo entry
        var total = marked.Count;
        var all = total >= LineCount;
        for (var i = runs.Count - 1; i >= 0; i--)
        {
            var (start, count) = runs[i];
            if (all && i == 0) { DeleteLines(start, count); }
            else DeleteLines(start, count);
        }
        _line = Math.Clamp(lastMarked - (total - 1), 0, LastLine);
        _bs.OpStart = _bs.OpEnd = new TextPosition(_line, 0);
        _bs.LastChange = new TextPosition(_line, 0);
        Beginline(BL.White | BL.Fix);
        if (total > O.Report) Message($"{total} fewer lines");
    }

    // ------------------------------------------------------------------------------------------------------------
    // :map family
    // ------------------------------------------------------------------------------------------------------------

    private bool ExMap(ExCmd c, (MapModes Modes, bool NoRemap, bool Unmap, bool Clear, bool BangForm) info)
    {
        var modes = info.Modes;
        if (c.Bang && modes == MapModes.All) modes = MapModes.InsertAndCommandLine;
        if (info.Clear) { _s.Mappings.Clear(modes); return true; }
        var a = c.Args;
        var silent = false; var nowait = false; var expr = false;
        while (true)
        {
            a = a.TrimStart();
            if (a.StartsWith("<silent>", StringComparison.OrdinalIgnoreCase)) { silent = true; a = a[8..]; }
            else if (a.StartsWith("<nowait>", StringComparison.OrdinalIgnoreCase)) { nowait = true; a = a[8..]; }
            else if (a.StartsWith("<expr>", StringComparison.OrdinalIgnoreCase)) { expr = true; a = a[6..]; }
            else if (a.StartsWith("<buffer>", StringComparison.OrdinalIgnoreCase) || a.StartsWith("<unique>", StringComparison.OrdinalIgnoreCase) || a.StartsWith("<script>", StringComparison.OrdinalIgnoreCase)) a = a[8..];
            else break;
        }
        if (a.Length == 0)
        {
            if (info.Unmap) { ExError("E474: Invalid argument"); return false; }
            Message(DescribeMappings(modes));
            return true;
        }
        var sp = 0;
        while (sp < a.Length && !(a[sp] is ' ' or '\t')) { if (a[sp] == '\u0016' && sp + 1 < a.Length) sp++; sp++; }
        var lhsText = a[..sp];
        var rhsText = a[sp..].TrimStart();
        var lhs = Keys.ParseNotation(lhsText, _s.Leader, _s.LocalLeader).ToArray();
        if (info.Unmap)
        {
            if (!_s.Mappings.Remove(lhs, modes)) { ExError("E31: No such mapping"); return false; }
            return true;
        }
        if (rhsText.Length == 0)
        {
            // :map lhs lists mappings starting with lhs
            Message(DescribeMappings(modes, lhs));
            return true;
        }
        if (expr) { ExError("E-expr: <expr> mappings are not supported"); return false; }
        var rhs = rhsText.Equals("<Nop>", StringComparison.OrdinalIgnoreCase) ? [] : Keys.ParseNotation(rhsText, _s.Leader, _s.LocalLeader).ToArray();
        _s.Mappings.Add(new Mapping { Lhs = lhs, Rhs = rhs, Modes = modes, NoRemap = info.NoRemap, Silent = silent, NoWait = nowait, LhsText = lhsText, RhsText = rhsText, Origin = _loadingOrigin });
        return true;
    }

    private string _loadingOrigin = "ex";

    private string DescribeMappings(MapModes modes, int[]? prefix = null)
    {
        var sb = new StringBuilder();
        foreach (var m in _s.Mappings.Items)
        {
            if ((m.Modes & modes) == 0) continue;
            if (prefix is not null && !m.Lhs.AsSpan().StartsWith(prefix)) continue;
            var ml = ((m.Modes & MapModes.Normal) != 0 ? "n" : " ") + ((m.Modes & MapModes.Visual) != 0 ? "x" : " ") + ((m.Modes & MapModes.OperatorPending) != 0 ? "o" : " ") + ((m.Modes & MapModes.Insert) != 0 ? "i" : " ") + ((m.Modes & MapModes.CommandLine) != 0 ? "c" : " ");
            sb.Append($"{ml} {m.LhsText,-12} {(m.NoRemap ? "*" : " ")} {m.RhsText}\n");
        }
        return sb.Length == 0 ? "No mapping found" : sb.ToString().TrimEnd('\n');
    }

    // ------------------------------------------------------------------------------------------------------------
    // :let / :echo / :execute (a tiny expression subset)
    // ------------------------------------------------------------------------------------------------------------

    private readonly Dictionary<string, string> _vars = new();

    private bool ExLet(string args)
    {
        var m = Regex.Match(args, @"^\s*(?<lhs>@.|&([lg]:)?[A-Za-z]+|\$[A-Za-z_]+|[A-Za-z_:][A-Za-z0-9_:.]*)\s*(?<op>\.=|\+=|-=|=)\s*(?<rhs>.*)$", RegexOptions.Singleline);
        if (!m.Success)
        {
            if (args.Trim().Length == 0) return true;
            ExError($"E15: Invalid expression: {args.Trim()}");
            return false;
        }
        var lhs = m.Groups["lhs"].Value;
        var op = m.Groups["op"].Value;
        string val;
        try { val = EvalExpr(m.Groups["rhs"].Value); }
        catch (FormatException ex) { ExError("E15: Invalid expression: " + ex.Message); return false; }
        if (lhs[0] == '@')
        {
            var r = lhs[1];
            if (!RegisterFile.IsValidName(r) || r is '.' or ':' or '%' or '#') { ExError($"E354: Invalid register name: '{r}'"); return false; }
            if (r == '/') { _s.Registers.LastSearch = val; _s.Search.Pattern = val; return true; }
            if (op == ".=")
            {
                var cur = _s.Registers.ForRead(r == '"' ? 0 : r);
                val = (cur is null ? "" : string.Join('\n', cur.Lines)) + val;
            }
            var target = _s.Registers.ForWrite(r, out var append, updatePrevious: false);
            if (target is null) return true;
            // a value ending in a newline becomes a linewise register
            var type = val.EndsWith('\n') ? VimRegisterType.Line : VimRegisterType.Character;
            var body = type == VimRegisterType.Line ? val[..^1] : val;
            RegisterFile.Store(target, body.Split('\n'), type, 0, append && op != ".=");
            NotifyNamedRegisterChange();
            return true;
        }
        if (lhs[0] == '&')
        {
            var optName = lhs.Length > 3 && lhs[2] == ':' ? lhs[3..] : lhs[1..];
            var def = VimOptions.Find(optName);
            if (def is null) { ExError($"E518: Unknown option: {optName}"); return false; }
            return SetOptionFromValue(def, val);
        }
        var name = lhs.StartsWith("g:", StringComparison.Ordinal) ? lhs[2..] : lhs;
        if (name == "mapleader") _s.Leader = val;
        else if (name == "maplocalleader") _s.LocalLeader = val;
        _vars[name] = val;
        return true;
    }

    private bool SetOptionFromValue(VimOptionDef def, string val)
    {
        switch (def.Kind)
        {
            case VimOptionKind.Bool: def.Set(O, val is "1" or "true"); break;
            case VimOptionKind.Number: if (!int.TryParse(val, out var n)) { ExError("E521: Number required after ="); return false; } def.Set(O, n); break;
            default: def.Set(O, val); break;
        }
        return true;
    }

    private bool ExEcho(ExCmd c, string full)
    {
        try
        {
            var v = EvalExprList(c.Args);
            if (full == "echoerr") { ExError(v); return false; }
            Message(v);
            return true;
        }
        catch (FormatException ex) { ExError("E15: Invalid expression: " + ex.Message); return false; }
    }

    private bool ExExecute(ExCmd c)
    {
        try
        {
            var v = EvalExprList(c.Args);
            ExecuteExLine(v);
            return true;
        }
        catch (FormatException ex) { ExError("E15: Invalid expression: " + ex.Message); return false; }
    }

    private string EvalExprList(string s)
    {
        var parts = new List<string>();
        var rest = s.Trim();
        while (rest.Length > 0)
        {
            var (val, used) = EvalTerm(rest, true);
            parts.Add(val);
            rest = rest[used..].TrimStart();
        }
        return string.Join(' ', parts);
    }

    private string EvalExpr(string s)
    {
        var (val, used) = EvalTerm(s.Trim(), true);
        if (s.Trim()[used..].Trim().Length > 0) throw new FormatException(s.Trim()[used..]);
        return val;
    }

    private (string Value, int Used) EvalTerm(string s, bool allowConcat)
    {
        var i = 0;
        var sb = new StringBuilder();
        var first = true;
        while (true)
        {
            while (i < s.Length && s[i] is ' ' or '\t') i++;
            if (i >= s.Length) break;
            if (!first)
            {
                if (s[i] != '.') break;
                i++;
                if (i < s.Length && s[i] == '.') i++;
                while (i < s.Length && s[i] is ' ' or '\t') i++;
            }
            var c = s[i];
            string piece;
            if (c == '"')
            {
                var j = i + 1;
                var b = new StringBuilder();
                while (j < s.Length && s[j] != '"')
                {
                    if (s[j] == '\\' && j + 1 < s.Length)
                    {
                        j++;
                        switch (s[j])
                        {
                            case 'n': b.Append('\n'); break;
                            case 't': b.Append('\t'); break;
                            case 'r': b.Append('\r'); break;
                            case 'e': b.Append('\u001b'); break;
                            case 'b': b.Append('\b'); break;
                            case '\\': b.Append('\\'); break;
                            case '"': b.Append('"'); break;
                            case '<':
                            {
                                var end = s.IndexOf('>', j);
                                if (end > j)
                                {
                                    var notation = s.Substring(j, end - j + 1);
                                    var keys = Keys.ParseNotation(notation, _s.Leader, _s.LocalLeader);
                                    if (keys.Count == 1 || !notation.Equals(Keys.ToNotation(keys), StringComparison.Ordinal)) { b.Append(Keys.Encode(keys)); j = end; break; }
                                }
                                b.Append('<');
                                break;
                            }
                            case 'x' or 'X':
                            {
                                var k = j + 1;
                                var v = 0;
                                var n = 0;
                                while (k < s.Length && n < 2 && Uri.IsHexDigit(s[k])) { v = v * 16 + Convert.ToInt32(s[k].ToString(), 16); k++; n++; }
                                b.Append((char)v);
                                j = k - 1;
                                break;
                            }
                            case 'u':
                            {
                                var k = j + 1;
                                var v = 0;
                                var n = 0;
                                while (k < s.Length && n < 4 && Uri.IsHexDigit(s[k])) { v = v * 16 + Convert.ToInt32(s[k].ToString(), 16); k++; n++; }
                                b.Append((char)v);
                                j = k - 1;
                                break;
                            }
                            default: b.Append(s[j]); break;
                        }
                        j++;
                        continue;
                    }
                    b.Append(s[j]);
                    j++;
                }
                if (j >= s.Length) throw new FormatException("missing \"");
                piece = b.ToString();
                i = j + 1;
            }
            else if (c == '\'')
            {
                var j = i + 1;
                var b = new StringBuilder();
                while (j < s.Length)
                {
                    if (s[j] == '\'') { if (j + 1 < s.Length && s[j + 1] == '\'') { b.Append('\''); j += 2; continue; } break; }
                    b.Append(s[j]);
                    j++;
                }
                if (j >= s.Length) throw new FormatException("missing '");
                piece = b.ToString();
                i = j + 1;
            }
            else if (c == '@' && i + 1 < s.Length)
            {
                var r = s[i + 1];
                if (r == '/') piece = _s.Registers.LastSearch;
                else if (r == ':') piece = _s.Registers.LastCommandLine;
                else
                {
                    var reg = _s.Registers.ForRead(r == '"' ? 0 : r, () => Host.ReadClipboard());
                    piece = reg is null ? "" : reg.ToText();
                }
                i += 2;
            }
            else if (c == '&' && i + 1 < s.Length && char.IsLetter(s[i + 1]))
            {
                var j = i + 1;
                while (j < s.Length && char.IsLetter(s[j])) j++;
                var def = VimOptions.Find(s[(i + 1)..j]);
                piece = def is null ? "" : Convert.ToString(def.Get(O) is bool bb ? (bb ? 1 : 0) : def.Get(O), CultureInfo.InvariantCulture) ?? "";
                i = j;
            }
            else if (char.IsDigit(c))
            {
                var j = i;
                while (j < s.Length && char.IsDigit(s[j])) j++;
                piece = s[i..j];
                i = j;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var j = i;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] is '_' or ':' or '.')) j++;
                var name = s[i..j];
                if (name.EndsWith('.')) { j--; name = name[..^1]; }
                var key = name.StartsWith("g:", StringComparison.Ordinal) ? name[2..] : name;
                if (!_vars.TryGetValue(key, out piece!)) throw new FormatException($"undefined variable {name}");
                i = j;
            }
            else throw new FormatException(s[i..]);
            sb.Append(piece);
            first = false;
            if (!allowConcat) break;
        }
        return (sb.ToString(), i);
    }

    // ------------------------------------------------------------------------------------------------------------
    // :source and vimrc
    // ------------------------------------------------------------------------------------------------------------

    private bool ExSource(ExCmd c)
    {
        var path = c.Args.Trim();
        if (path.Length == 0) { ExError("E471: Argument required"); return false; }
        var reader = _s.ReadFile;
        if (reader is null) { ExError("E484: Can't open file " + path); return false; }
        string? text;
        try { text = reader(path); }
        catch (IOException) { text = null; }
        if (text is null) { ExError("E484: Can't open file " + path); return false; }
        LoadScript(text, "source");
        return true;
    }

    /// <summary>Runs a vimrc-style script. Errors are reported on the message line and returned ("line 3: E492: ..."), but never abort loading.</summary>
    public IReadOnlyList<string> LoadScript(string text, string origin)
    {
        var errors = new List<string>();
        var old = _loadingOrigin;
        _loadingOrigin = origin;
        try
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var lineNumber = i + 1;
                var line = lines[i];
                // a following line starting with a backslash continues this one
                while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('\\')) { line += lines[i + 1].TrimStart()[1..]; i++; }
                var t = line.Trim();
                if (t.Length == 0 || t[0] == '"') continue;
                var before = _exErrors;
                LastError = null;
                ExecuteExLine(t);
                _macroAborted = false;
                if (_exErrors != before) errors.Add($"line {lineNumber}: {LastError ?? "error"}");
            }
        }
        finally { _loadingOrigin = old; }
        return errors;
    }

    // ------------------------------------------------------------------------------------------------------------
    // small text commands
    // ------------------------------------------------------------------------------------------------------------

    private bool ExAlign(ExCmd c, string full)
    {
        if (!CanModify()) return false;
        var arg = ParseIntOr(c.Args, full == "left" ? 0 : (O.TextWidth > 0 ? O.TextWidth : 80));
        for (var l = c.Line1; l <= c.Line2; l++)
        {
            var s = L(l);
            var body = s.Trim(' ', '\t');
            string indent;
            if (full == "left") indent = VimText.MakeIndent(arg, O.TabStop, O.ExpandTab);
            else
            {
                var w = VimText.DisplayWidth(body, O.TabStop);
                var pad = full == "right" ? arg - w : (arg - w) / 2;
                indent = body.Length == 0 ? "" : VimText.MakeIndent(Math.Max(0, pad), O.TabStop, O.ExpandTab);
            }
            SetLine(l, body.Length == 0 ? "" : indent + body);
        }
        _line = c.Line2;
        Beginline(BL.White | BL.Fix);
        return true;
    }

    private bool ExRetab(ExCmd c)
    {
        if (!CanModify()) return false;
        var newTs = ParseIntOr(c.Args, O.TabStop);
        var l1 = c.HasRange ? c.Line1 : 0;
        var l2 = c.HasRange ? c.Line2 : LastLine;
        for (var l = l1; l <= l2; l++)
        {
            var s = L(l);
            if (s.IndexOf('\t') < 0 && !c.Bang) continue;
            var sb = new StringBuilder();
            var vcol = 0;
            var i = 0;
            while (i < s.Length)
            {
                if (s[i] is ' ' or '\t')
                {
                    var st = i;
                    var startV = vcol;
                    var hasTab = false;
                    while (i < s.Length && s[i] is ' ' or '\t') { if (s[i] == '\t') { hasTab = true; vcol += O.TabStop - vcol % O.TabStop; } else vcol++; i++; }
                    if (!hasTab && !c.Bang) { sb.Append(s, st, i - st); continue; }
                    var width = vcol - startV;
                    if (O.ExpandTab) sb.Append(' ', width);
                    else
                    {
                        var v = startV;
                        while ((v / newTs + 1) * newTs <= startV + width) { sb.Append('\t'); v = (v / newTs + 1) * newTs; }
                        sb.Append(' ', startV + width - v);
                    }
                }
                else { sb.Append(s[i]); vcol += VimText.WidthAt(s, i, vcol, O.TabStop); i++; }
            }
            SetLine(l, sb.ToString());
        }
        O.TabStop = newTs;
        return true;
    }

    private bool ExSort(ExCmd c)
    {
        if (!CanModify()) return false;
        var l1 = c.HasRange ? c.Line1 : 0;
        var l2 = c.HasRange ? c.Line2 : LastLine;
        var flags = c.Args.Replace(" ", "");
        var numeric = flags.Contains('n');
        var ic = flags.Contains('i');
        var unique = flags.Contains('u');
        var lines = Enumerable.Range(l1, l2 - l1 + 1).Select(L).ToList();
        IEnumerable<string> sorted;
        if (numeric)
        {
            long Num(string s) { var m = Regex.Match(s, @"-?\d+"); return m.Success && long.TryParse(m.Value, out var v) ? v : long.MinValue; }
            sorted = lines.Select((s, i) => (s, i)).OrderBy(x => Num(x.s)).ThenBy(x => x.i).Select(x => x.s);
        }
        else sorted = lines.OrderBy(s => s, ic ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var list = sorted.ToList();
        if (c.Bang) list.Reverse();
        if (unique) list = list.Where((s, i) => i == 0 || !(ic ? string.Equals(s, list[i - 1], StringComparison.OrdinalIgnoreCase) : s == list[i - 1])).ToList();
        ApplyLineBatch([new LineEdit(l1, l2 - l1 + 1, list.ToArray())]);
        _line = l1;
        Beginline(BL.Sol | BL.Fix);
        return true;
    }
}
