using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;

namespace CsCode.Vim;

/// <summary>
/// The Vim engine for ONE editor view. Feed it keys with <see cref="HandleKey(KeyStroke)"/>/<see cref="HandleText"/>; it edits the host's
/// buffer through <see cref="IVimHost"/>. It runs synchronously on the caller's (UI) thread and is designed to be fast and non-blocking:
/// typical keys are microseconds, and big operations are sliced (see <see cref="HasPendingWork"/>/<see cref="RunPendingWork"/>).
/// Not thread-safe: all calls must come from one thread.
/// </summary>
public sealed partial class VimEngine : IDisposable
{
    private const int NoRemapFlag = 1 << 30;

    private IVimHost? _host;
    private IVimBuffer _buf = null!;
    private BufferState _bs = null!;
    private readonly VimSession _s;
    private readonly DisposableStore _disp = new();

    // cursor + mode
    private int _line, _col;
    private int _curswant;
    private bool _setCurswant = true;
    private VimMode _mode = VimMode.Normal;

    // key pipeline
    private readonly IntQueue _stuff = new();
    private readonly IntQueue _typed = new();
    private readonly List<int> _mapBuf = new(8);
    private IDisposable? _mapTimer;
    private int _mapDepth;
    private int _noMap;            // >0 while an argument character is awaited (no mappings apply to it)
    private bool _inPump;
    private long _opCounter;
    private IEnumerator<int>? _work;
    private long _sliceTicks;

    // what we last told the host
    private TextPosition _sentCaret = new(-1, -1);
    private bool _sentInsertLike;
    private VimMode _sentMode = (VimMode)(-1);
    private string _sentShowCmd = "";
    private VimSelection _sentSelection = VimSelection.None;

    private readonly Emitter<VimMode> _modeChanged = new();
    private readonly Emitter<string> _messageChanged = new();
    private readonly Emitter<char?> _recordingChanged = new();
    private readonly Emitter<string?> _searchChanged = new();
    private readonly Emitter<bool> _workPending = new();

    public VimEngine(IVimHost host, VimSession? session = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _s = session ?? new VimSession();
        _sliceTicks = Stopwatch.Frequency * 3 / 1000;
        AttachBuffer(host.Buffer);
        var c = host.GetCaret();
        _line = Math.Clamp(c.Line, 0, _buf.LineCount - 1);
        _col = c.Character;
        CheckCursor();
        InitMacroPersistence();
        if (_s.StartInInsertMode) { StartInsert('i', 1, restart: false); }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Public surface
    // ------------------------------------------------------------------------------------------------------------

    public VimSession Session => _s;
    public VimOptions Options => _s.Options;

    /// <summary>When false every key passes through to the editor (Vim emulation off).</summary>
    public bool Enabled { get => _s.Enabled; set { _s.Enabled = value; if (!value) Reset(); } }

    public VimMode Mode => _opPendingShown && _mode is VimMode.Normal or VimMode.InsertNormal ? VimMode.OperatorPending : _mode;

    public TextPosition Cursor => new(_line, _col);

    /// <summary>Short mode code used by tests and the status line: n i R v V ^V no ni c.</summary>
    public string ModeCode => Mode switch
    {
        VimMode.Normal => "n", VimMode.Insert => "i", VimMode.Replace => "R", VimMode.Visual => "v", VimMode.VisualLine => "V",
        VimMode.VisualBlock => "^V", VimMode.OperatorPending => "no", VimMode.CommandLine => "c", VimMode.InsertNormal => "ni", _ => "n",
    };

    /// <summary>Position of a mark (a-z, A-Z, ' ` [ ] &lt; &gt; . ^ "), or null if unset. For '&gt; after a linewise Visual the column is 1 &lt;&lt; 30 like Vim's MAXCOL.</summary>
    public TextPosition? GetMarkPosition(char mark)
    {
        if (_host is null) return null;
        var p = GetMark(mark);
        if (p is { } q && mark == '>' && _bs.VisualMode == VimMode.VisualLine) return new TextPosition(q.Line, 1 << 30);
        if (p is { } q2 && mark == '<' && _bs.VisualMode == VimMode.VisualLine) return new TextPosition(q2.Line, 0);
        return p;
    }

    /// <summary>The keys typed so far of an incomplete command (for the status bar), e.g. "2d".</summary>
    public string PendingKeys => ShowCmdText();

    public bool IsRecording => _recReg != 0;
    public char? RecordingRegister => _recReg == 0 ? null : _recReg;
    public string? LastMessage { get; private set; }
    public string? LastError { get; private set; }

    public IEventSource<VimMode> ModeChanged => _modeChanged;
    public IEventSource<string> MessageChanged => _messageChanged;
    public IEventSource<char?> RecordingChanged => _recordingChanged;
    public IEventSource<string?> SearchChanged => _searchChanged;
    /// <summary>Fires true when sliced work is waiting for <see cref="RunPendingWork"/>, false when it finished.</summary>
    public IEventSource<bool> PendingWorkChanged => _workPending;

    public bool HasPendingWork => _work is not null || _stuff.Count > 0 || _typed.Count > 0;

    /// <summary>Time budget per slice of sliced work (default 3 ms, so a dispatcher callback stays under its 4 ms budget).</summary>
    public TimeSpan SliceBudget
    {
        get => TimeSpan.FromSeconds((double)_sliceTicks / Stopwatch.Frequency);
        set => _sliceTicks = Math.Max(1, (long)(value.TotalSeconds * Stopwatch.Frequency));
    }

    public VimKeyResult HandleKey(KeyStroke key)
    {
        var k = Keys.FromKeyStroke(key);
        return k < 0 ? VimKeyResult.PassThrough : HandleKey(k);
    }

    public VimKeyResult HandleText(string text)
    {
        if (_host is null || !_s.Enabled) return VimKeyResult.PassThrough;
        var result = VimKeyResult.Handled;
        for (var i = 0; i < text.Length;)
        {
            var cp = char.ConvertToUtf32(text, i);
            if (char.IsHighSurrogate(text[i]) && (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))) cp = text[i];
            i += cp > 0xFFFF ? 2 : 1;
            result = HandleKey(cp);
        }
        return result;
    }

    public VimKeyResult HandleKey(int key)
    {
        if (_host is null || !_s.Enabled) return VimKeyResult.PassThrough;
        if (key == 3 && (_work is not null || _stuff.Count > 0))
        {
            CancelWork();
            Reset(keepRecording: true);
            FlushHost();
            return VimKeyResult.Handled;
        }
        if (_inPump)
        {
            _typed.Enqueue(key);
            return VimKeyResult.Handled;
        }
        SyncFromHost();
        if (ShouldPassThrough(key)) return VimKeyResult.PassThrough;
        _typed.Enqueue(key);
        Pump(_sliceTicks);
        return VimKeyResult.Handled;
    }

    /// <summary>Runs sliced work (and keys queued behind it) for about <paramref name="budget"/>; returns true if more remains.</summary>
    public bool RunPendingWork(TimeSpan? budget = null)
    {
        if (_host is null) return false;
        Pump(budget is { } b ? Math.Max(1, (long)(b.TotalSeconds * Stopwatch.Frequency)) : _sliceTicks);
        return HasPendingWork;
    }

    /// <summary>Runs until every queued key and sliced work item has completed (tests; hosts should prefer <see cref="RunPendingWork"/> on dispatcher ticks).</summary>
    public void Drain()
    {
        while (HasPendingWork && _host is not null) Pump(long.MaxValue / 4);
    }

    /// <summary>Types a string in Vim notation as if the user did (convenience for tests and scripting).</summary>
    public void TypeKeys(string notation)
    {
        if (_host is null || !_s.Enabled) return;
        SyncFromHost();
        foreach (var k in Keys.ParseNotation(notation, _s.Leader, _s.LocalLeader)) _typed.Enqueue(k);
        Pump(long.MaxValue / 4);
        Drain();
    }

    /// <summary>Like <see cref="TypeKeys"/> but every key is a separate HandleKey call (no typeahead between keys), as with a slow human typist.</summary>
    public void TypeKeysOneByOne(string notation)
    {
        foreach (var k in Keys.ParseNotation(notation, _s.Leader, _s.LocalLeader))
        {
            HandleKey(k);
            Drain();
        }
    }

    /// <summary>Aborts any pending command, leaves Insert/Visual/Command-line mode and clears queued keys.</summary>
    public void Reset(bool keepRecording = false)
    {
        CancelWork();
        _stuff.Clear();
        _typed.Clear();
        _pend.Clear();
        _opPendingShown = false;
        _argPending = false;
        _noMap = 0;
        StopMapTimer();
        _mapBuf.Clear();
        CancelCommandLine();
        if (_insert is not null) AbortInsert();
        if (_mode is VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock) EndVisual();
        _mode = VimMode.Normal;
        if (!keepRecording && _recReg != 0) { _recReg = '\0'; _recBuf.Clear(); _recordingChanged.Fire(null); _host?.OnRecordingChanged(null); }
        CheckCursor();
        FlushHost();
    }

    /// <summary>Releases the host and buffer (the engine keeps nothing alive after this).</summary>
    public void Detach()
    {
        if (_host is null) return;
        CancelWork();
        StopMapTimer();
        _stuff.Clear();
        _typed.Clear();
        _insert = null;
        _cmdline = null;
        _pend.Clear();
        _host = null;
        _buf = null!;
        _bs = null!;
    }

    public void Dispose()
    {
        Detach();
        _disp.Dispose();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Host <-> engine synchronisation
    // ------------------------------------------------------------------------------------------------------------

    private IVimHost Host => _host ?? throw new ObjectDisposedException(nameof(VimEngine));
    private VimOptions O => _s.Options;

    private void AttachBuffer(IVimBuffer buffer)
    {
        _buf = buffer;
        _bs = _s.StateFor(buffer);
        _bs.KnownVersion = buffer.Version;
        _bs.Undo.Synced = true;
    }

    private void SyncFromHost()
    {
        var host = Host;
        if (!ReferenceEquals(host.Buffer, _buf))
        {
            if (_mode is VimMode.Insert or VimMode.Replace) AbortInsert();
            AttachBuffer(host.Buffer);
            _mode = VimMode.Normal;
            _pend.Clear();
            _sentCaret = new TextPosition(-1, -1);
        }
        else if (_buf.Version != _bs.KnownVersion)
        {
            // A change we did not make (LSP edit, formatter, paste outside Vim): our undo history no longer describes the buffer.
            _bs.Undo.Clear();
            _bs.ClampMarks(_buf.LineCount);
            _bs.KnownVersion = _buf.Version;
            _bs.ULineNr = -1;
        }
        var hc = host.GetCaret();
        if (hc != _sentCaret)
        {
            _line = Math.Clamp(hc.Line, 0, _buf.LineCount - 1);
            _col = hc.Character;
            _setCurswant = true;
            CheckCursor();
        }
    }

    private void FlushHost()
    {
        var host = _host;
        if (host is null) return;
        var insertLike = _mode is VimMode.Insert or VimMode.Replace || (_mode is VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock && O.VirtualOneMore);
        var caret = new TextPosition(_line, _col);
        if (caret != _sentCaret || insertLike != _sentInsertLike)
        {
            host.SetCaret(caret, insertLike);
            _sentCaret = caret;
            _sentInsertLike = insertLike;
        }
        var sel = CurrentSelection();
        if (sel != _sentSelection)
        {
            host.SetSelection(sel);
            _sentSelection = sel;
        }
        var shown = Mode;
        if (shown != _sentMode)
        {
            _sentMode = shown;
            host.OnModeChanged(shown, new VimModeInfo(_s.CursorStyles.GetValueOrDefault(shown, VimCursorShape.Block), ModeLabel(shown), _recReg != 0 ? $"recording @{_recReg}" : ""));
            _modeChanged.Fire(shown);
        }
        var sc = ShowCmdText();
        if (sc != _sentShowCmd)
        {
            _sentShowCmd = sc;
            host.OnShowCommand(sc);
        }
    }

    private static string ModeLabel(VimMode m) => m switch
    {
        VimMode.Normal => "NORMAL", VimMode.Insert => "INSERT", VimMode.Replace => "REPLACE", VimMode.Visual => "VISUAL",
        VimMode.VisualLine => "VISUAL LINE", VimMode.VisualBlock => "VISUAL BLOCK", VimMode.OperatorPending => "OP PENDING",
        VimMode.CommandLine => "COMMAND", VimMode.InsertNormal => "(insert)", _ => "",
    };

    // ------------------------------------------------------------------------------------------------------------
    // Messages and errors
    // ------------------------------------------------------------------------------------------------------------

    private void Message(string text, VimMessageKind kind = VimMessageKind.Info)
    {
        LastMessage = text;
        if (kind == VimMessageKind.Error) LastError = text;
        _host?.OnMessage(new VimMessage(kind, text));
        _messageChanged.Fire(text);
    }

    /// <summary>An error message (Vim's emsg): shows it, aborts macros/mappings in flight and cancels the pending command.</summary>
    private void Error(string text)
    {
        Message(text, VimMessageKind.Error);
        Beep();
    }

    /// <summary>Vim's beep_flush: an error. Flushes mapped/macro keys (typed keys stay), which is what makes macros stop at the first failure.</summary>
    private void Beep()
    {
        _stuff.Clear();
        _mapBuf.Clear();
        StopMapTimer();
        _macroAborted = true;
        _pend.Clear();
        _opPendingShown = false;
        _argPending = false;
        _noMap = 0;
    }

    // ------------------------------------------------------------------------------------------------------------
    // The pump: typed keys, stuffed keys (macros, mappings, dot-repeat) and sliced work, in Vim's order
    // ------------------------------------------------------------------------------------------------------------

    private void Pump(long budgetTicks)
    {
        if (_inPump) return;
        _inPump = true;
        var wasPending = _work is not null || _stuff.Count > 0;
        try
        {
            var deadline = budgetTicks >= long.MaxValue / 8 ? long.MaxValue : Stopwatch.GetTimestamp() + budgetTicks;
            _buf.BeginEditGroup();
            try
            {
                while (_host is not null)
                {
                    if (_work is not null)
                    {
                        if (!StepWork(deadline)) break;
                        continue;
                    }
                    if (_stuff.Count > 0) ProcessKey(_stuff.Dequeue(), typed: false);
                    else if (_typed.Count > 0) ProcessKey(_typed.Dequeue(), typed: true);
                    else break;
                    if ((++_opCounter & 31) == 0 && deadline != long.MaxValue && Stopwatch.GetTimestamp() > deadline && (_work is not null || _stuff.Count > 0 || _typed.Count > 0)) break;
                }
            }
            finally
            {
                _buf?.EndEditGroup();
            }
        }
        finally
        {
            _inPump = false;
            if (_host is not null)
            {
                if (_bs is not null) _bs.KnownVersion = _buf.Version;
                FlushHost();
                var pending = _work is not null || _stuff.Count > 0 || _typed.Count > 0;
                if (pending != wasPending) _workPending.Fire(pending);
            }
        }
    }

    private bool StepWork(long deadline)
    {
        var w = _work!;
        while (true)
        {
            bool more;
            try { more = w.MoveNext(); }
            catch (Exception) { _work = null; throw; }
            if (!more)
            {
                _work = null;
                return true;
            }
            if (deadline != long.MaxValue && Stopwatch.GetTimestamp() > deadline) return false;
        }
    }

    /// <summary>Starts a sliced work item (an iterator that yields every few ms of work). It runs to its first yield immediately.</summary>
    private void StartWork(IEnumerator<int> work)
    {
        _work = work;
    }

    private void CancelWork()
    {
        var w = _work;
        _work = null;
        w?.Dispose();
    }

    private bool InWork => _work is not null;

    // ------------------------------------------------------------------------------------------------------------
    // Key intake: undo sync, recording, mapping
    // ------------------------------------------------------------------------------------------------------------

    private void ProcessKey(int rawKey, bool typed)
    {
        if (typed)
        {
            if (_stuff.Count == 0 && _mapBuf.Count == 0) _mapDepth = 0;
            RecordTyped(rawKey);
            // Vim syncs undo when a key is TYPED outside Insert/Command-line mode; macro/mapping/dot keys never sync.
            if (_mode is not (VimMode.Insert or VimMode.Replace or VimMode.CommandLine) || _arrowUsed) _bs.Undo.Synced = true;
        }
        MapKey(rawKey, typed);
    }

    private MapModes CurrentMapMode() => _mode switch
    {
        VimMode.Insert or VimMode.Replace => MapModes.Insert,
        VimMode.CommandLine => MapModes.CommandLine,
        VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock => _opPendingShown && false ? MapModes.OperatorPending : MapModes.Visual,
        _ => _opPendingShown ? MapModes.OperatorPending : MapModes.Normal,
    };

    private void MapKey(int rawKey, bool typed)
    {
        var key = rawKey & ~NoRemapFlag;
        var noremap = (rawKey & NoRemapFlag) != 0;
        if (noremap || _noMap > 0 || _argPending || _s.Mappings.Items.Count == 0 || !_s.Mappings.Any(CurrentMapMode()))
        {
            if (_mapBuf.Count > 0) ReleaseMapBuf();
            Feed(key, typed);
            return;
        }
        _mapBuf.Add(key);
        ResolveMap(timedOut: false);
    }

    private void ReleaseMapBuf()
    {
        StopMapTimer();
        var keys = _mapBuf.ToArray();
        _mapBuf.Clear();
        foreach (var k in keys) Feed(k, typed: false);
    }

    private void ResolveMap(bool timedOut)
    {
        while (_mapBuf.Count > 0)
        {
            var mode = CurrentMapMode();
            var (exact, longer) = _s.Mappings.Lookup(mode, CollectionsMarshal.AsSpan(_mapBuf));
            if (exact is not null && (!longer || exact.NoWait || timedOut))
            {
                StopMapTimer();
                _mapBuf.Clear();
                ExpandMapping(exact);
                return;
            }
            if (longer && !timedOut)
            {
                StartMapTimer();
                return;
            }
            StopMapTimer();
            var first = _mapBuf[0];
            _mapBuf.RemoveAt(0);
            timedOut = false;
            Feed(first, typed: false);
            if (_host is null) return;
            // The mode may have changed (e.g. a Feed that started Insert): re-resolve the rest under the new mode.
            if (!_s.Mappings.Any(CurrentMapMode())) { ReleaseMapBuf(); return; }
        }
        StopMapTimer();
    }

    private void ExpandMapping(Mapping m)
    {
        if (++_mapDepth > 1000)
        {
            Error("E223: Recursive mapping");
            return;
        }
        if (m.Commands is { Count: > 0 })
            foreach (var c in m.Commands) RunBindingCommand(c);
        if (m.Rhs.Length == 0) return;
        var flag = m.NoRemap ? NoRemapFlag : 0;
        Span<int> buf = m.Rhs.Length <= 64 ? stackalloc int[m.Rhs.Length] : new int[m.Rhs.Length];
        for (var i = 0; i < m.Rhs.Length; i++) buf[i] = m.Rhs[i] | flag;
        _stuff.PushFront(buf);
    }

    private void RunBindingCommand(VimBindingCommand c)
    {
        if (c.Command.StartsWith(':')) ExecuteExLine(c.Command[1..]);
        else Host.ExecuteCommand(c.Command, c.Args);
    }

    private void StartMapTimer()
    {
        StopMapTimer();
        if (!O.Timeout || _host is null) return;
        _mapTimer = _host.Timer.Schedule(TimeSpan.FromMilliseconds(O.TimeoutLen), OnMapTimeout);
    }

    private void StopMapTimer()
    {
        var t = _mapTimer;
        _mapTimer = null;
        t?.Dispose();
    }

    private void OnMapTimeout()
    {
        _mapTimer = null;
        if (_host is null || _mapBuf.Count == 0) return;
        _inPump = true;
        try { ResolveMap(timedOut: true); }
        finally { _inPump = false; }
        Pump(_sliceTicks);
    }

    private void Feed(int key, bool typed)
    {
        switch (_mode)
        {
            case VimMode.Insert or VimMode.Replace: InsertKey(key); break;
            case VimMode.CommandLine: CmdLineKey(key); break;
            default: NormalKey(key); break;
        }
    }

    /// <summary>Pushes keys to be processed before anything typed (macro bodies, dot-repeat, mapping expansions).</summary>
    private void Stuff(ReadOnlySpan<int> keys) => _stuff.PushFront(keys);

    private void StuffText(string registerText, int count)
    {
        var keys = Keys.Decode(registerText);
        if (keys.Count == 0 || count <= 0) return;
        if (count == 1) { _stuff.PushFront(CollectionsMarshal.AsSpan(keys)); return; }
        var all = new int[keys.Count * count];
        for (var i = 0; i < count; i++) keys.CopyTo(all, i * keys.Count);
        _stuff.PushFront(all);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Pass-through decision
    // ------------------------------------------------------------------------------------------------------------

    private bool ShouldPassThrough(int key)
    {
        if (_s.UnhandledKeys.Count > 0 && _s.UnhandledKeys.Contains(key)) return true;
        var idle = _pend.Count == 0 && _mapBuf.Count == 0 && _insertLiteral == 0 && _cmdline is null && !_argPending;
        if (!idle) return false;
        if (IsVimKey(key)) return false;
        // A mapping may claim an otherwise unknown key (nnoremap <C-s> :w<CR>).
        var (exact, longer) = _s.Mappings.Lookup(CurrentMapMode(), [key]);
        return exact is null && !longer;
    }

    private bool IsVimKey(int key)
    {
        if ((key & Keys.ModMask) != 0) return key is (Keys.ModCtrl | Keys.BS) && false;
        if (Keys.IsSpecial(key))
            return key is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or Keys.Insert or Keys.Delete or Keys.BS or Keys.STab or Keys.Plug or Keys.Nop;
        if (key >= 32 || key is Keys.Esc or Keys.CR or Keys.NL or Keys.Tab or Keys.CtrlH) return true;
        var inInsert = _mode is VimMode.Insert or VimMode.Replace or VimMode.CommandLine;
        return key switch
        {
            1 or 2 or 3 or 4 or 5 or 6 or 7 or 15 or 17 or 18 or 20 or 21 or 22 or 23 or 24 or 25 or 29 or 30 or 31 or 28 or 0 => true,
            14 or 16 or 11 => inInsert || _mode is VimMode.Normal or VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock, // ^N ^P ^K
            12 => true,   // Ctrl-L: redraw
            _ => false,   // Ctrl-S, Ctrl-Z ...: not Vim keys, the editor keeps them
        };
    }
}

/// <summary>Allocation-free FIFO of ints with push-to-front (used for typeahead).</summary>
internal sealed class IntQueue
{
    private int[] _a = new int[64];
    private int _head, _count;

    public int Count => _count;

    public void Clear() { _head = 0; _count = 0; }

    public void Enqueue(int v)
    {
        Grow(1);
        _a[(_head + _count) & (_a.Length - 1)] = v;
        _count++;
    }

    public int Dequeue()
    {
        var v = _a[_head];
        _head = (_head + 1) & (_a.Length - 1);
        _count--;
        return v;
    }

    public int Peek() => _a[_head];

    public void PushFront(ReadOnlySpan<int> keys)
    {
        Grow(keys.Length);
        for (var i = keys.Length - 1; i >= 0; i--)
        {
            _head = (_head - 1) & (_a.Length - 1);
            _a[_head] = keys[i];
        }
        _count += keys.Length;
    }

    private void Grow(int extra)
    {
        if (_count + extra <= _a.Length) return;
        var size = _a.Length;
        while (size < _count + extra) size *= 2;
        var n = new int[size];
        for (var i = 0; i < _count; i++) n[i] = _a[(_head + i) & (_a.Length - 1)];
        _a = n;
        _head = 0;
    }
}
