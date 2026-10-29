using System.Text;
using CsCode.Core.Text;

namespace CsCode.Vim;

internal enum OpType
{
    None, Delete, Yank, Change, ShiftLeft, ShiftRight, Filter, Indent, Tilde, Upper, Lower, Rot13, Format, FormatKeep, Fold,
    Join, JoinNoSpace, Replace, Insert, Append, NrAdd, NrSub, Function, Comment, SurroundAdd, SurroundDelete, SurroundChange,
}

/// <summary>A parsed Normal/Visual command (Vim's cmdarg_T): optional register and count, the command key(s), and for operators the motion part.</summary>
internal sealed class Cmd
{
    public int Reg, Count0, Key, Key2, NChar, NChar2;
    public OpType Op;
    public int OpKey2;           // second key of a two-key operator (g~, gu, gq ...)
    public int Force;            // v / V / Ctrl-V between operator and motion
    public bool Doubled;         // dd, g~~ ...
    public bool HasMotion;
    public bool Visual;          // parsed while Visual mode was active
    public string? SurroundTag;  // tag text for ys/cs with <tag>
    public Cmd M = null!;        // the motion (or text object) after an operator
    private readonly bool _isMotion;

    public Cmd() { M = new Cmd(true); }
    private Cmd(bool isMotion) { _isMotion = isMotion; }

    public int Count1 => Count0 > 0 ? Count0 : 1;

    public void Reset()
    {
        Reg = Count0 = Key = Key2 = NChar = NChar2 = OpKey2 = Force = 0;
        Op = OpType.None;
        Doubled = HasMotion = Visual = false;
        SurroundTag = null;
        if (!_isMotion) M.Reset();
    }

    public void CopyTo(Cmd o)
    {
        o.Reg = Reg; o.Count0 = Count0; o.Key = Key; o.Key2 = Key2; o.NChar = NChar; o.NChar2 = NChar2; o.Op = Op; o.OpKey2 = OpKey2;
        o.Force = Force; o.Doubled = Doubled; o.HasMotion = HasMotion; o.Visual = Visual; o.SurroundTag = SurroundTag;
        if (!_isMotion) { M.CopyTo(o.M); }
    }
}

public sealed partial class VimEngine
{
    private readonly List<int> _pend = new(16);
    private readonly Cmd _cmd = new();
    private bool _opPendingShown;     // an operator was parsed and is waiting for its motion
    private bool _argPending;         // the parser waits for an argument character (mappings must not apply)

    private enum ParseResult { NeedMore, Invalid, Cancelled, Complete }

    // ------------------------------------------------------------------------------------------------------------
    // key entry
    // ------------------------------------------------------------------------------------------------------------

    private void NormalKey(int k)
    {
        if (_pend.Count == 0 && _opCmdForSearch is null)
        {
            if (k is Keys.Esc or 3)
            {
                if (VisualActive) EndVisual();
                else if (_mode == VimMode.InsertNormal) { }
                ClearOpState();
                return;
            }
        }
        else if (k is Keys.Esc or 3)
        {
            _pend.Clear();
            ClearOpState();
            return;
        }

        _pend.Add(k);
        var r = TryParse(_cmd);
        switch (r)
        {
            case ParseResult.NeedMore:
                return;
            case ParseResult.Invalid:
                _pend.Clear();
                ClearOpState();
                Beep();
                return;
            case ParseResult.Cancelled:
                _pend.Clear();
                ClearOpState();
                return;
        }
        _opPendingShown = false;
        _argPending = false;
        var cmdKeysCount = _pend.Count;
        try { ExecuteCmd(_cmd); }
        finally
        {
            if (_pend.Count == cmdKeysCount) _pend.Clear();
            _opPendingShown = false;
        }
    }

    private void ClearOpState()
    {
        _opPendingShown = false;
        _argPending = false;
        _noMap = 0;
    }

    // ------------------------------------------------------------------------------------------------------------
    // parser
    // ------------------------------------------------------------------------------------------------------------

    private int _pp;

    private bool Next(out int k)
    {
        if (_pp < _pend.Count) { k = _pend[_pp++]; return true; }
        k = 0;
        return false;
    }

    private ParseResult NeedMoreArg(bool arg)
    {
        _argPending = arg;
        return ParseResult.NeedMore;
    }

    private ParseResult TryParse(Cmd c)
    {
        c.Reset();
        _pp = 0;
        c.Visual = VisualActive;
        _argPending = false;

        // register and count prefix (count digits before and after "x concatenate, like Vim)
        int k;
        while (true)
        {
            if (!Next(out k)) return ParseResult.NeedMore;
            if (k == '"')
            {
                if (!Next(out var r)) return NeedMoreArg(true);
                if (!RegisterFile.IsValidName(r)) return r is Keys.Esc ? ParseResult.Cancelled : ParseResult.Invalid;
                c.Reg = r;
                continue;
            }
            if ((k >= '1' && k <= '9') || (k == '0' && c.Count0 > 0))
            {
                c.Count0 = (int)Math.Min((long)c.Count0 * 10 + (k - '0'), 99999999);
                continue;
            }
            break;
        }

        c.Key = k;
        if (k == Keys.Plug && _pendingVisualRedo is not null) return ParseResult.Complete;
        var kind = ClassifyFirst(k);
        if (kind == Kind.Invalid) return ParseResult.Invalid;

        if (kind == Kind.Prefix)
        {
            if (k == 23 /* Ctrl-W */)
            {
                // Ctrl-W [count] {char}
                while (true)
                {
                    if (!Next(out var k2)) return ParseResult.NeedMore;
                    if ((k2 >= '1' && k2 <= '9') || (k2 == '0' && c.Count0 > 0 && false)) { c.Count0 = c.Count0 * 10 + (k2 - '0'); continue; }
                    c.Key2 = k2;
                    break;
                }
                if (c.Key2 == 'g' || c.Key2 == 7) { if (!Next(out var k3)) return ParseResult.NeedMore; c.NChar = k3; }
                return ParseResult.Complete;
            }
            if (!Next(out var key2)) return ParseResult.NeedMore;
            c.Key2 = key2;
            if (key2 == Keys.Esc) return ParseResult.Cancelled;
            if (!PrefixValid(k, key2)) return ParseResult.Invalid;
            if (PrefixNeedsArg(k, key2)) { if (!Next(out var n)) return NeedMoreArg(true); c.NChar = n; if (n == Keys.Esc) return ParseResult.Cancelled; }
        }
        else if (NeedsNChar(c, k, opActive: false))
        {
            if (!Next(out var n)) return NeedMoreArg(true);
            if (n == Keys.Esc) return ParseResult.Cancelled;
            c.NChar = n;
            if (!ReadLiteralArg(c, k)) return ParseResult.NeedMore;
        }

        // operator? then its motion
        var op = OperatorOf(c);
        if (op != OpType.None)
        {
            c.Op = op;
            if (c.Visual) return ParseResult.Complete;
            return ParseMotionPart(c);
        }
        // surround / commentary commands that are not plain operators
        return ParseSpecial(c);
    }

    private bool ReadLiteralArg(Cmd c, int k)
    {
        // r<C-v>x / r<C-k>ab: gather the literal / digraph characters
        if (k != 'r') return true;
        if (c.NChar is 22 or 17)
        {
            if (!Next(out var lit)) { _argPending = true; return false; }
            c.NChar2 = lit;
        }
        else if (c.NChar == 11)
        {
            if (!Next(out var d1)) { _argPending = true; return false; }
            if (!Next(out var d2)) { _argPending = true; return false; }
            c.NChar2 = Digraphs.Lookup(d1, d2);
        }
        return true;
    }

    private enum Kind { Invalid, Cmd, Prefix }

    private Kind ClassifyFirst(int k)
    {
        switch (k)
        {
            case 'g' or 'z' or 'Z' or '[' or ']' or 23: return Kind.Prefix;
            case Keys.Plug or Keys.Nop or Keys.Ignore: return Kind.Invalid;
        }
        if (k < 0x80 && k >= 32 || k >= 0x80 && Keys.IsChar(k)) return Kind.Cmd; // printable (unknown ones Beep at exec)
        if (Keys.IsSpecial(k) || k is Keys.Esc or Keys.CR or Keys.NL or Keys.Tab or Keys.CtrlH) return Kind.Cmd;
        return k switch
        {
            1 or 2 or 3 or 4 or 5 or 6 or 7 or 12 or 15 or 17 or 18 or 20 or 21 or 22 or 24 or 25 or 29 or 30 or 31 => Kind.Cmd,
            _ => Kind.Invalid,
        };
    }

    private bool PrefixValid(int k, int k2) => k switch
    {
        'g' => true, // unknown g-commands are rejected at execution (Vim beeps)
        'z' => true,
        'Z' => k2 is 'Z' or 'Q',
        '[' or ']' => true,
        _ => false,
    };

    private bool PrefixNeedsArg(int k, int k2) => k == 'g' && k2 is '\'' or '`' || (k == 'z' && false);

    private bool NeedsNChar(Cmd c, int k, bool opActive)
    {
        switch (k)
        {
            case 'f' or 'F' or 't' or 'T' or '\'' or '`': return true;
            case 'r' or 'm' or '@': return !opActive;
            case 'q': return !opActive && _recReg == 0 && !c.Visual || (!opActive && _recReg == 0 && c.Visual);
            case 'a' or 'i': return opActive || c.Visual;
            default: return false;
        }
    }

    private static OpType OperatorOf(Cmd c)
    {
        switch (c.Key)
        {
            case 'd': return OpType.Delete;
            case 'y': return OpType.Yank;
            case 'c': return OpType.Change;
            case '<': return OpType.ShiftLeft;
            case '>': return OpType.ShiftRight;
            case '=': return OpType.Indent;
            case '!': return OpType.Filter;
            case 'g':
                return c.Key2 switch
                {
                    '~' => OpType.Tilde, 'u' => OpType.Lower, 'U' => OpType.Upper, '?' => OpType.Rot13, 'q' => OpType.Format,
                    'w' => OpType.FormatKeep, '@' => OpType.Function, 'c' or 'C' => OpType.Comment, _ => OpType.None,
                };
            case 'z': return c.Key2 == 'f' ? OpType.Fold : OpType.None;
        }
        return OpType.None;
    }

    /// <summary>Parses [count][v|V|^V]motion after an operator (or the doubled operator form, or a text object).</summary>
    private ParseResult ParseMotionPart(Cmd c)
    {
        var m = c.M;
        m.Reset();
        _opPendingShown = true;
        // `ys{motion}{char}`: y followed by s is the surround operator
        while (true)
        {
            if (!Next(out var k)) return ParseResult.NeedMore;
            if (k == Keys.Esc) return ParseResult.Cancelled;
            if ((k >= '1' && k <= '9') || (k == '0' && m.Count0 > 0)) { m.Count0 = (int)Math.Min((long)m.Count0 * 10 + (k - '0'), 99999999); continue; }
            if (k is 'v' or 'V' or 22) { c.Force = k; continue; }
            if (k == '"') { if (!Next(out var r)) return NeedMoreArg(true); if (!RegisterFile.IsValidName(r)) return ParseResult.Invalid; c.Reg = r; continue; }
            m.Key = k;
            break;
        }

        // surround: ys / ds / cs
        if (_s.Plugins.Surround && m.Key == 's' && c.Op == OpType.Yank && c.Key == 'y')
        {
            c.Op = OpType.SurroundAdd;
            return ParseSurroundAddMotion(c);
        }

        // doubled operator (dd, yy, g~~, g~g~ ...)
        var opLast = c.Key == 'g' || (c.Key == 'z') ? c.Key2 : c.Key;
        if (m.Key == opLast && !(c.Key is 'g' or 'z' && false)) { c.Doubled = true; c.HasMotion = true; m.Key2 = 0; return ParseResult.Complete; }
        if ((c.Key is 'g' or 'z') && m.Key == c.Key)
        {
            if (!Next(out var k2)) return ParseResult.NeedMore;
            if (k2 == opLast) { c.Doubled = true; c.HasMotion = true; return ParseResult.Complete; }
            m.Key2 = k2;
            return ParseMotionRest(c, m, haveKey2: true);
        }
        return ParseMotionRest(c, m, haveKey2: false);
    }

    private ParseResult ParseMotionRest(Cmd c, Cmd m, bool haveKey2)
    {
        var k = m.Key;
        if (k is 'g' or 'z' or '[' or ']' && !haveKey2)
        {
            if (!Next(out var k2)) return ParseResult.NeedMore;
            m.Key2 = k2;
        }
        if (k == 'g' && m.Key2 is '\'' or '`') { if (!Next(out var n)) return NeedMoreArg(true); m.NChar = n; }
        else if (k is 'a' or 'i')
        {
            if (!Next(out var n)) return NeedMoreArg(true);
            if (n == Keys.Esc) return ParseResult.Cancelled;
            m.NChar = n;
        }
        else if (NeedsNChar(m, k, opActive: true) && k is 'f' or 'F' or 't' or 'T' or '\'' or '`')
        {
            if (!Next(out var n)) return NeedMoreArg(true);
            if (n == Keys.Esc) return ParseResult.Cancelled;
            m.NChar = n;
        }
        if (!IsMotionKey(m)) return ParseResult.Invalid;
        c.HasMotion = true;
        return ParseResult.Complete;
    }

    private static bool IsMotionKey(Cmd m)
    {
        switch (m.Key)
        {
            case 'h' or 'j' or 'k' or 'l' or 'w' or 'W' or 'b' or 'B' or 'e' or 'E' or '0' or '^' or '$' or '|' or '_' or '+' or '-'
                or 'f' or 'F' or 't' or 'T' or ';' or ',' or '%' or '(' or ')' or '{' or '}' or 'H' or 'M' or 'L' or 'G'
                or '\'' or '`' or 'n' or 'N' or '*' or '#' or '/' or '?' or 'a' or 'i' or ' ' or 's' or 'z':
                return true;
            case Keys.CR or Keys.BS or 8 or Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.Delete or Keys.NL: return true;
            case 14 or 16 or 10: return true; // ^N ^P ^J
            case 'g': return m.Key2 is 'g' or '_' or 'j' or 'k' or '0' or '^' or '$' or 'm' or 'M' or 'e' or 'E' or 'o' or '\'' or '`' or 'n' or 'N' or '*' or '#' or 'D' or 'd';
            case '[' or ']': return m.Key2 is '[' or ']' or '(' or ')' or '{' or '}' or '\'' or '`';
        }
        return false;
    }

    private ParseResult ParseSpecial(Cmd c)
    {
        var k = c.Key;
        // ds{char} / cs{old}{new} (surround) — only when the plugin is on
        if (_s.Plugins.Surround && !c.Visual) { }
        return ParseResult.Complete;
    }

    private ParseResult ParseSurroundAddMotion(Cmd c)
    {
        var m = c.M;
        m.Reset();
        // after "ys": [count]{motion or text object} then the surrounding char (a tag takes input up to '>')
        while (true)
        {
            if (!Next(out var k)) return ParseResult.NeedMore;
            if (k == Keys.Esc) return ParseResult.Cancelled;
            if ((k >= '1' && k <= '9') || (k == '0' && m.Count0 > 0)) { m.Count0 = m.Count0 * 10 + (k - '0'); continue; }
            m.Key = k;
            break;
        }
        if (m.Key == 's')
        {
            // yss
            c.Doubled = true;
        }
        else
        {
            var r = ParseMotionRest(c, m, haveKey2: false);
            if (r != ParseResult.Complete) return r;
        }
        c.HasMotion = true;
        // the surround character (or <tag> ... >)
        if (!Next(out var ch)) return NeedMoreArg(true);
        c.NChar = ch;
        if (ch == Keys.Esc) return ParseResult.Cancelled;
        if (ch is '<' or 't')
        {
            // collect up to and including '>' (or Enter for 't' form)
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
}
