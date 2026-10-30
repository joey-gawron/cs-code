using CsCode.Core.Text;

namespace CsCode.Vim;

/// <summary>
/// Text objects and motions from the buffer's syntax tree, as nvim-treesitter-textobjects maps them: <c>af</c>/<c>if</c> a function, <c>ac</c>/<c>ic</c>
/// a class, <c>aa</c>/<c>ia</c> an argument or parameter, after an operator and in Visual mode, with counts (the count-th enclosing object;
/// Visual mode grows the selection to the next enclosing one), and <c>]m [m ]M [M</c> to the next/previous function start/end (also after an
/// operator). When nothing encloses the cursor the next object after it is taken (nvim-treesitter-textobjects' <c>lookahead</c>).
/// <para>
/// The tree is the host's and lives on another thread, so the ranges arrive asynchronously (<see cref="IVimHost.RequestSyntaxRanges"/>): the
/// command is suspended as sliced work that waits for the host instead of for time (nothing blocks), keys typed meanwhile queue behind it (as
/// behind <c>:s///c</c>'s prompt) and run after it, in order, and <see cref="HasPendingWork"/> stays false while only the host is awaited so the
/// host does not poll. The answer resumes the command on the engine's thread; no answer within <see cref="SyntaxRequestTimeout"/> ends it with a
/// message. A buffer without a syntax tree (a language the host does not parse) fails at once with one message, like an unknown text object:
/// the pending operator is cancelled and a macro running it stops there, so repeating it cannot pile up errors.
/// </para>
/// </summary>
public sealed partial class VimEngine
{
    /// <summary>The longest a syntax command waits for the host's answer (keys typed meanwhile wait behind it).</summary>
    public static TimeSpan SyntaxRequestTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The request the suspended command waits for (null when none).</summary>
    private SyntaxRequest? _syntax;

    private sealed class SyntaxRequest(int bufferVersion)
    {
        public readonly int BufferVersion = bufferVersion;
        public bool Answered;
        public bool TimedOut;
        public IReadOnlyList<VimSyntaxRange>? Ranges;
        public IDisposable? Timeout;
    }

    /// <summary>True while a suspended command waits for the host (no work to do meanwhile).</summary>
    private bool AwaitingHost => _syntax is { Answered: false };

    /// <summary>True while a command waits for the host's syntax ranges (tests, diagnostics).</summary>
    public bool IsAwaitingSyntax => AwaitingHost;

    private static bool IsSyntaxObject(Cmd m) => m.Key is 'a' or 'i' && m.NChar is 'f' or 'c' or 'a';

    private static bool IsSyntaxMotion(Cmd m) => m.Key is '[' or ']' && m.Key2 is 'm' or 'M';

    private static VimSyntaxObjectKind SyntaxKindOf(int nchar) => nchar switch
    {
        'f' => VimSyntaxObjectKind.Function,
        'c' => VimSyntaxObjectKind.Class,
        _ => VimSyntaxObjectKind.Parameter,
    };

    // ------------------------------------------------------------------------------------------------------------
    // entry points (from the operator, Visual and plain motion paths)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>An operator followed by a syntax text object or motion (<c>daf</c>, <c>c2ia</c>, <c>y]m</c>): it runs when the ranges arrive.</summary>
    private void StartSyntaxOperator(Cmd c, int count0)
    {
        var copy = new Cmd();
        c.CopyTo(copy);
        StartSyntaxWork(SyntaxOperatorWork(copy, count0));
    }

    /// <summary>A syntax text object in Visual mode (<c>vaf</c>, <c>v2ic</c>) or a syntax motion without an operator (<c>]m</c>, <c>v3[M</c>).</summary>
    private void StartSyntaxPlain(Cmd c, int count0)
    {
        var copy = new Cmd();
        c.CopyTo(copy);
        StartSyntaxWork(SyntaxPlainWork(copy, count0));
    }

    /// <summary>
    /// Typed keys, macros, mappings and <c>.</c> suspend the command until the answer. Inside <c>:normal</c> and <c>:g</c> nothing can wait (they
    /// run their keys to completion within one command, around other work): there only a host that answers at once is served, and a later
    /// answer ends the command with a message.
    /// </summary>
    private void StartSyntaxWork(IEnumerator<int> work)
    {
        if (_nest == 0 && !_globalBusy && _work is null)
        {
            StartWork(work);
            return;
        }
        using (work)
        {
            while (work.MoveNext())
            {
                if (!AwaitingHost) continue;
                CancelSyntaxRequest();
                Error("af/if, ac/ic, aa/ia and ]m [m cannot wait for the syntax tree inside :normal or :g");
                return;
            }
        }
    }

    private IEnumerator<int> SyntaxOperatorWork(Cmd c, int count0)
    {
        // after Ctrl-O the command goes back to Insert mode however it ends (done, failed, timed out, cancelled)
        try
        {
            var m = c.M;
            var motion = IsSyntaxMotion(m);
            var request = RequestSyntax(motion ? VimSyntaxObjectKind.Function : SyntaxKindOf(m.NChar), inner: !motion && m.Key == 'i');
            if (request is null) yield break;
            while (!request.Answered) yield return 0;
            if (TakeSyntaxAnswer(request) is not { } ranges) yield break;

            var oap = _oap;
            oap.ResetForMotion(c.Op, c.Reg);
            var cursor = new TextPosition(_line, _col);
            oap.Start = cursor;
            oap.CursorStart = cursor;
            var count1 = count0 > 0 ? count0 : 1;
            if (motion)
            {
                if (SyntaxMotionTarget(ranges, m, count1, cursor) is not { } target) { SyntaxFailed(); yield break; }
                _line = target.Line;
                _col = target.Character;
                oap.Type = MType.Char;
                oap.Inclusive = m.Key2 == 'M'; // to the end of a function includes its last character (as e does); to a start excludes it
            }
            else
            {
                if (EnclosingSyntaxRange(ranges, cursor, cursor, count1, strictlyLarger: false) is not { } range) { SyntaxFailed(); yield break; }
                oap.Start = range.Start;
                var last = LastCharOf(range);
                _line = last.Line;
                _col = last.Character;
                oap.Type = MType.Char;
                oap.Inclusive = true;
            }
            oap.Force = c.Force;
            DoPendingOperator(c, oap, count0);
            CheckPcmark();
            FinishCommand();
        }
        finally
        {
            MaybeResumeInsert();
        }
    }

    private IEnumerator<int> SyntaxPlainWork(Cmd c, int count0)
    {
        // after Ctrl-O the command goes back to Insert mode however it ends (done, failed, timed out, cancelled)
        try
        {
            var motion = IsSyntaxMotion(c);
            var request = RequestSyntax(motion ? VimSyntaxObjectKind.Function : SyntaxKindOf(c.NChar), inner: !motion && c.Key == 'i');
            if (request is null) yield break;
            while (!request.Answered) yield return 0;
            if (TakeSyntaxAnswer(request) is not { } ranges) yield break;

            var count1 = count0 > 0 ? count0 : 1;
            var cursor = new TextPosition(_line, _col);
            if (motion)
            {
                if (SyntaxMotionTarget(ranges, c, count1, cursor) is not { } target) { SyntaxFailed(); yield break; }
                if (!VisualActive) SetPcmark(); // a jump, as nvim-treesitter-textobjects' set_jumps
                _line = target.Line;
                _col = target.Character;
                _setCurswant = true;
                CheckCursor();
            }
            else
            {
                if (!VisualActive) { SyntaxFailed(); yield break; }
                var (selStart, selEnd) = _visualStart <= cursor ? (_visualStart, cursor) : (cursor, _visualStart);
                // a selection of one character is the cursor; a wider one grows to the next object that encloses more than it
                var grow = selStart != selEnd;
                if (EnclosingSyntaxRange(ranges, selStart, selEnd, count1, strictlyLarger: grow) is not { } range) { SyntaxFailed(); yield break; }
                _visualStart = range.Start;
                var last = LastCharOf(range);
                _line = last.Line;
                _col = last.Character;
                if (O.SelectionExclusive) IncCursor();
                SetMode(VimMode.Visual);
                _setCurswant = true;
            }
            CheckPcmark();
            FinishCommand();
        }
        finally
        {
            MaybeResumeInsert();
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // the request and the answer
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Asks the host; null (reported) when the buffer has no syntax tree. The answer may come back inside this call.</summary>
    private SyntaxRequest? RequestSyntax(VimSyntaxObjectKind kind, bool inner)
    {
        var request = new SyntaxRequest(_buf.Version);
        _syntax = request;
        bool asked;
        try { asked = Host.RequestSyntaxRanges(kind, inner, ranges => OnSyntaxAnswer(request, ranges)); }
        catch (Exception) { asked = false; }
        if (!asked)
        {
            _syntax = null;
            Error("No syntax tree for this file: af/if, ac/ic, aa/ia and ]m [m ]M [M need a language CsCode parses with tree-sitter");
            return null;
        }
        if (!request.Answered && _host is { } host)
            request.Timeout = host.Timer.Schedule(SyntaxRequestTimeout, () => OnSyntaxTimeout(request));
        return request;
    }

    /// <summary>The host's answer (on the engine's thread). Resumes the suspended command, and the keys queued behind it.</summary>
    private void OnSyntaxAnswer(SyntaxRequest request, IReadOnlyList<VimSyntaxRange>? ranges)
    {
        if (request.Answered || !ReferenceEquals(_syntax, request)) return; // late (timed out, cancelled, superseded)
        request.Answered = true;
        request.Ranges = ranges;
        request.Timeout?.Dispose();
        request.Timeout = null;
        if (_inPump || _host is null) return; // answered inside the request: the command goes on as soon as the call returns
        SyncFromHost();
        Pump(_sliceTicks);
    }

    private void OnSyntaxTimeout(SyntaxRequest request)
    {
        if (request.Answered || !ReferenceEquals(_syntax, request)) return;
        request.TimedOut = true;
        OnSyntaxAnswer(request, null);
    }

    /// <summary>The ranges of an answered request, or null (reported) when there are none or the text changed while they were computed.</summary>
    private IReadOnlyList<VimSyntaxRange>? TakeSyntaxAnswer(SyntaxRequest request)
    {
        if (ReferenceEquals(_syntax, request)) _syntax = null;
        if (request.TimedOut)
        {
            Error("The syntax tree did not answer in time");
            return null;
        }
        if (_buf.Version != request.BufferVersion)
        {
            Error("The text changed while the syntax tree was asked: try again");
            return null;
        }
        if (request.Ranges is null)
        {
            Error("No syntax tree for this text yet");
            return null;
        }
        return request.Ranges;
    }

    /// <summary>Nothing of that kind there: Vim's answer to a text object it cannot find (the operator is cancelled; a macro stops).</summary>
    private void SyntaxFailed()
    {
        Beep();
        CheckCursor();
    }

    private void CancelSyntaxRequest()
    {
        var request = _syntax;
        _syntax = null;
        request?.Timeout?.Dispose();
    }

    // ------------------------------------------------------------------------------------------------------------
    // choosing the object (pure: tests drive it through InMemoryVimHost)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The <paramref name="count"/>-th smallest range that encloses [<paramref name="selStart"/>, <paramref name="selEnd"/>] (inclusive character
    /// positions), strictly larger than it when <paramref name="strictlyLarger"/>; when none encloses it, the count-th range starting after it
    /// (lookahead). Null when there are not that many.
    /// </summary>
    internal static VimSyntaxRange? EnclosingSyntaxRange(IReadOnlyList<VimSyntaxRange> ranges, TextPosition selStart, TextPosition selEnd, int count, bool strictlyLarger)
    {
        var enclosing = new List<VimSyntaxRange>();
        foreach (var r in ranges)
        {
            if (r.End <= r.Start) continue;
            if (r.Start > selStart || r.End <= selEnd) continue; // must hold the first and the last selected character
            if (strictlyLarger && r.Start == selStart && LastCharOfRange(r) <= selEnd) continue;
            enclosing.Add(r);
        }
        if (enclosing.Count > 0)
        {
            enclosing.Sort(static (a, b) => CompareSize(a, b));
            // nested objects with the same extent count once
            var distinct = new List<VimSyntaxRange>(enclosing.Count);
            foreach (var r in enclosing)
                if (distinct.Count == 0 || distinct[^1] != r) distinct.Add(r);
            return count <= distinct.Count ? distinct[count - 1] : null;
        }
        var after = ranges.Where(r => r.End > r.Start && r.Start > selEnd).OrderBy(r => r.Start).ThenByDescending(r => r.End).ToList();
        var starts = new List<VimSyntaxRange>(after.Count);
        foreach (var r in after)
            if (starts.Count == 0 || starts[^1].Start != r.Start) starts.Add(r); // one object per start: the largest
        return count <= starts.Count ? starts[count - 1] : null;
    }

    /// <summary>]m [m: the count-th function start after (before) <paramref name="cursor"/>; ]M [M: the count-th function end (its last character).</summary>
    internal static TextPosition? SyntaxMotionTarget(IReadOnlyList<VimSyntaxRange> ranges, Cmd m, int count, TextPosition cursor) =>
        SyntaxMotionTarget(ranges, forward: m.Key == ']', toEnd: m.Key2 == 'M', count, cursor);

    internal static TextPosition? SyntaxMotionTarget(IReadOnlyList<VimSyntaxRange> ranges, bool forward, bool toEnd, int count, TextPosition cursor)
    {
        var points = ranges.Where(r => r.End > r.Start).Select(r => toEnd ? LastCharOfRange(r) : r.Start).Distinct().ToList();
        points.Sort();
        var candidates = forward ? points.Where(p => p > cursor).ToList() : points.Where(p => p < cursor).Reverse().ToList();
        return count <= candidates.Count ? candidates[count - 1] : null;
    }

    private static int CompareSize(VimSyntaxRange a, VimSyntaxRange b)
    {
        // smaller first: a range inside another is smaller; otherwise by extent (lines, then characters)
        if (a.Start >= b.Start && a.End <= b.End && (a.Start != b.Start || a.End != b.End)) return -1;
        if (b.Start >= a.Start && b.End <= a.End && (a.Start != b.Start || a.End != b.End)) return 1;
        var la = a.End.Line - a.Start.Line;
        var lb = b.End.Line - b.Start.Line;
        if (la != lb) return la.CompareTo(lb);
        return (a.End.Character - a.Start.Character).CompareTo(b.End.Character - b.Start.Character);
    }

    /// <summary>The last character of an end-exclusive range, as an inclusive position (a range ending at a line start ends on the line before).</summary>
    private static TextPosition LastCharOfRange(VimSyntaxRange r) =>
        r.End.Character > 0 ? new TextPosition(r.End.Line, r.End.Character - 1)
        : r.End.Line > r.Start.Line ? new TextPosition(r.End.Line - 1, int.MaxValue) : r.Start;

    /// <summary><see cref="LastCharOfRange"/> clamped to the buffer's line (a range ending at a line start: that line's last character).</summary>
    private TextPosition LastCharOf(VimSyntaxRange r)
    {
        var p = LastCharOfRange(r);
        if (p.Character != int.MaxValue) return p;
        var len = L(p.Line).Length;
        return new TextPosition(p.Line, len == 0 ? 0 : VimText.PrevCharStart(L(p.Line), len));
    }
}
