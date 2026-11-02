using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Panels.Debug.Infrastructure;

namespace CsCode.Panels.Debug.Native;

public enum DisassemblyRowKind { Instruction, Source, Symbol }

/// <summary>One row of the Disassembly window: an instruction, or the source line / function its following instructions come from.</summary>
public sealed class DisassemblyRow(DisassemblyRowKind kind, ulong address, string addressText, string bytes, string text, bool invalid = false) : ObservableObject
{
    private bool _isCurrent, _hasBreakpoint, _breakpointVerified;

    public DisassemblyRowKind Kind { get; } = kind;
    public bool IsInstruction => Kind == DisassemblyRowKind.Instruction;
    public bool IsSource => Kind == DisassemblyRowKind.Source;
    public bool IsSymbol => Kind == DisassemblyRowKind.Symbol;
    public ulong Address { get; } = address;
    /// <summary>The address as the adapter wrote it (instruction breakpoints use it).</summary>
    public string AddressText { get; } = addressText;
    public string Bytes { get; } = bytes;
    /// <summary>The instruction, the source line's text, or the function name.</summary>
    public string Text { get; } = text;
    /// <summary>Filler where there is no code ("??").</summary>
    public bool IsInvalid { get; } = invalid;
    public string? Symbol { get; init; }
    /// <summary>A source row's file and line (0-based), for "go to source".</summary>
    public DebugSource? Source { get; init; }
    public int? Line { get; init; }

    /// <summary>The focused frame's current instruction.</summary>
    public bool IsCurrent { get => _isCurrent; set => SetProperty(ref _isCurrent, value); }
    public bool HasBreakpoint { get => _hasBreakpoint; set => SetProperty(ref _hasBreakpoint, value); }
    public bool BreakpointVerified
    {
        get => _breakpointVerified;
        set { if (SetProperty(ref _breakpointVerified, value)) OnPropertyChanged(nameof(BreakpointOpacity)); }
    }
    /// <summary>An unverified breakpoint is drawn faded (VS Code's hollow glyph).</summary>
    public double BreakpointOpacity => _breakpointVerified ? 1.0 : 0.5;
}

/// <summary>
/// Disassembly window (VS Code's Disassembly view): the focused frame's code around its instruction pointer (<see cref="DebugStackFrame.InstructionPointerReference"/>),
/// or any address (the address box, "Open Disassembly View" on a Call Stack frame). Instructions are fetched in pages of <see cref="PageSize"/>
/// around the anchor with DAP <c>disassemble</c> (<c>instructionOffset</c>) only when the view scrolls near an edge — never one request per
/// row, one request in flight per direction — and at most <see cref="MaxInstructions"/> are kept (the far end is dropped). The source line and
/// function each run of instructions comes from is shown above it when the adapter gives locations. Instruction breakpoints toggle per row
/// (<see cref="INativeBreakpointManager"/>); while the window has the keyboard, stepping goes by instruction (<see cref="FocusKey"/>, read by the
/// debugger's step commands). Refreshes at each stop: the highlight moves if the new instruction is loaded, else the code around it is loaded.
/// </summary>
public sealed class DisassemblyViewModel : ObservableObject, IDisposable
{
    public const int PageSize = 64;
    public const int MaxInstructions = 4_096;
    /// <summary>VS Code's context key: true while the Disassembly window has the keyboard (the step commands step by instruction).</summary>
    public const string FocusKey = "disassemblyViewFocus";

    private readonly DebugPanelServices _s;
    private readonly DebugFocusTracker _tracker;
    private readonly DisposableStore _subs = new();
    private readonly SourceLineCache _sources;
    private readonly string _key = "debug.disassembly." + Interlocked.Increment(ref s_ids);
    private static int s_ids;
    private readonly List<DebugInstruction> _instructions = [];
    private CancellationTokenSource _cts = new();
    private IDebugSession? _session;
    private string? _anchor;
    private int _first, _last; // instruction offsets of the first / last loaded instruction, relative to the anchor
    private bool _loadingEarlier, _loadingLater, _startReached, _endReached;
    private long _generation, _focusEpoch = -1;
    private int? _focusFrame;
    private ulong? _current;
    private string? _status = "Not debugging", _addressInput;
    private DisassemblyRow? _selected;
    private bool _following = true;
    private int _disposed;

    public DisassemblyViewModel(DebugPanelServices services)
    {
        _s = services;
        _sources = new SourceLineCache(services);
        GoCommand = new UiCommand(() => GoToAsync(AddressInput));
        RefreshCommand = new UiCommand(() => _anchor is { } a ? ReloadAsync(a, null) : Task.CompletedTask);
        CurrentCommand = new UiCommand(GoToCurrentAsync);
        ToggleBreakpointCommand = new UiCommand(() => { if (SelectedRow is { } r) ToggleBreakpoint(r); });
        if (Native is { } native) _subs.Add(native.NativeChanged.Subscribe(_ => _s.Ui.PostCoalesced(_key + ".bp", RefreshBreakpoints)));
        _subs.Add(_s.Breakpoints.Changed.Subscribe(_ => _s.Ui.PostCoalesced(_key + ".bp", RefreshBreakpoints)));
        _subs.Add(_s.NativeViews.DisassemblyRequested.Subscribe(t => _s.Ui.Post(() => Navigate(t))));
        _tracker = new DebugFocusTracker(services, OnFocus);
        if (_s.NativeViews.TakeDisassembly() is { } pending) _pendingTarget = pending;
        _tracker.Start();
    }

    private NativeTarget? _pendingTarget;
    private DebugFocus _focus;

    public RangeObservableCollection<DisassemblyRow> Rows { get; } = new();
    public DisassemblyRow? SelectedRow { get => _selected; set => SetProperty(ref _selected, value); }
    public string? StatusText { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(ShowStatus)); } }
    public bool ShowStatus => !string.IsNullOrEmpty(_status);
    /// <summary>The address box: an address (<c>0x1000</c>) or an expression whose value has a memory reference (<c>&amp;x</c>, a pointer).</summary>
    public string? AddressInput { get => _addressInput; set => SetProperty(ref _addressInput, value); }
    public UiCommand GoCommand { get; }
    public UiCommand RefreshCommand { get; }
    public UiCommand CurrentCommand { get; }
    public UiCommand ToggleBreakpointCommand { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    /// <summary>Rows were inserted above the first (the view keeps what it shows in place); the count.</summary>
    public event Action<int>? RowsPrepended;
    /// <summary>The view should bring this row on screen (the current instruction after a stop).</summary>
    public event Action<DisassemblyRow>? RevealRequested;

    private INativeBreakpointManager? Native => _s.Breakpoints as INativeBreakpointManager;

    /// <summary>The view got or lost the keyboard: the step commands step by instruction while it has it.</summary>
    public void SetKeyboardFocus(bool focused) => _s.ContextKeys?.Set(FocusKey, focused);

    // ------------------------------------------------------------------ following the debugger

    private void OnFocus(DebugFocus focus)
    {
        if (IsDisposed) return;
        _focus = focus;
        if (focus.Session is null || focus.State == DebugSessionState.Terminated)
        {
            Clear("Not debugging");
            _session = null;
            return;
        }
        if (!ReferenceEquals(focus.Session, _session))
        {
            Clear(null);
            _session = focus.Session;
        }
        if (focus.Session is not IDebugSessionNative || !focus.Session.Capabilities.SupportsDisassemble)
        {
            Clear("The debug adapter does not support disassembly.");
            return;
        }
        if (!focus.IsStopped)
        {
            SetCurrent(null);
            if (Rows.Count == 0) StatusText = "Running…";
            return;
        }
        if (!focus.HasFrame || (focus.Epoch == _focusEpoch && focus.FrameId == _focusFrame)) return;
        _focusEpoch = focus.Epoch;
        _focusFrame = focus.FrameId;
        if (Interlocked.Exchange(ref _pendingTarget, null) is { } target)
        {
            _following = false;
            _ = ReloadAsync(target.MemoryReference, null);
            _ = UpdateCurrentAsync(focus, reveal: false);
            return;
        }
        _ = UpdateCurrentAsync(focus, reveal: _following);
    }

    /// <summary>The focused frame's instruction: highlighted (and revealed when following); loaded around it when it is not in the loaded code.</summary>
    private async Task UpdateCurrentAsync(DebugFocus focus, bool reveal)
    {
        var generation = Volatile.Read(ref _generation);
        string? ip;
        try
        {
            var frames = await _s.Ui.OnUi(focus.Session!.GetStackTraceAsync(focus.ThreadId!.Value, 0, 64, _cts.Token), _cts.Token);
            ip = frames.FirstOrDefault(f => f.Id == focus.FrameId)?.InstructionPointerReference;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or NotSupportedException) { return; }
        if (IsDisposed || generation != Volatile.Read(ref _generation)) return;
        if (ip is null || !DebugAddress.TryParse(ip, out var address))
        {
            SetCurrent(null);
            if (Rows.Count == 0) StatusText = "The debug adapter gave no instruction pointer for this frame.";
            return;
        }
        if (reveal && !IsLoaded(address)) await ReloadAsync(ip, address);
        else SetCurrent(address);
        if (reveal && Rows.FirstOrDefault(r => r.IsCurrent) is { } row) RevealRequested?.Invoke(row);
    }

    private bool IsLoaded(ulong address) =>
        _instructions.Count > 0 && address >= _instructions[0].Address && address <= _instructions[^1].Address && _instructions.Any(i => i.Address == address);

    private void SetCurrent(ulong? address)
    {
        _current = address;
        foreach (var r in Rows)
            if (r.IsInstruction) r.IsCurrent = address is { } a && r.Address == a;
    }

    private void Navigate(NativeTarget target)
    {
        if (IsDisposed || _session is null) return;
        _s.NativeViews.Handled(target);
        _following = false;
        _ = ReloadAsync(target.MemoryReference, null);
    }

    /// <summary>Back to the focused frame's instruction (and following the debugger again).</summary>
    private Task GoToCurrentAsync()
    {
        _following = true;
        _focusEpoch = -1;
        var focus = _focus;
        return focus.HasFrame ? UpdateCurrentAsync(focus, reveal: true) : Task.CompletedTask;
    }

    /// <summary>The address box: an address goes as it is; an expression is evaluated in the focused frame and its memory reference used.</summary>
    internal async Task GoToAsync(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || _session is not { } session) return;
        var text = input.Trim();
        string? reference = DebugAddress.TryParse(text, out _) ? text : null;
        if (reference is null)
        {
            try
            {
                var r = await _s.Ui.OnUi(session.EvaluateAsync(text, _focus.FrameId, "watch", _cts.Token), _cts.Token);
                reference = MemoryViewModel.AddressOfResult(r);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusText = $"Cannot evaluate '{text}': {ex.Message}";
                return;
            }
        }
        if (reference is null) { StatusText = $"'{text}' has no address."; return; }
        _following = false;
        await ReloadAsync(reference, null);
    }

    // ------------------------------------------------------------------ paging

    private void Clear(string? status)
    {
        Cancel();
        _instructions.Clear();
        Rows.Reset();
        _anchor = null;
        _current = null;
        _focusEpoch = -1;
        _focusFrame = null;
        StatusText = status;
    }

    private void Cancel()
    {
        Interlocked.Increment(ref _generation);
        var old = _cts;
        _cts = new CancellationTokenSource();
        try { old.Cancel(); } catch (AggregateException) { }
        old.Dispose();
        _loadingEarlier = _loadingLater = false;
    }

    /// <summary>Loads <see cref="PageSize"/> instructions before and after <paramref name="anchor"/>; <paramref name="current"/> is highlighted.</summary>
    internal async Task ReloadAsync(string anchor, ulong? current)
    {
        if (_session is not IDebugSessionNative native) return;
        Cancel();
        var generation = Volatile.Read(ref _generation);
        var ct = _cts.Token;
        StatusText = "Loading…";
        IReadOnlyList<DebugInstruction> page;
        try { page = await _s.Ui.OnUi(native.DisassembleAsync(anchor, 0, -PageSize, 2 * PageSize, ct), ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _generation)) { Rows.Reset(); _instructions.Clear(); StatusText = "Cannot disassemble: " + ex.Message; }
            return;
        }
        page = Ordered(page);
        await _sources.LoadAsync(page.Select(i => i.Location).OfType<DebugSource>(), ct);
        if (IsDisposed || generation != Volatile.Read(ref _generation)) return;
        _anchor = anchor;
        _first = -PageSize;
        _last = _first + page.Count - 1;
        _startReached = _endReached = false;
        _instructions.Clear();
        _instructions.AddRange(page);
        Rows.ReplaceAll(BuildRows(page, null, null));
        StatusText = page.Count == 0 ? "No instructions at this address." : null;
        SetCurrent(current ?? _current);
        RefreshBreakpoints();
        var anchorRow = DebugAddress.TryParse(anchor, out var a) ? Rows.FirstOrDefault(r => r.IsInstruction && r.Address == a) : null;
        if ((Rows.FirstOrDefault(r => r.IsCurrent) ?? anchorRow) is { } reveal) RevealRequested?.Invoke(reveal);
    }

    /// <summary>The view scrolled near the top: one more page before the first instruction (nothing while one is on its way).</summary>
    public async Task LoadEarlierAsync()
    {
        if (_loadingEarlier || _startReached || _anchor is not { } anchor || _session is not IDebugSessionNative native || _instructions.Count == 0) return;
        _loadingEarlier = true;
        var generation = Volatile.Read(ref _generation);
        var ct = _cts.Token;
        try
        {
            var page = await _s.Ui.OnUi(native.DisassembleAsync(anchor, 0, _first - PageSize, PageSize, ct), ct);
            await _sources.LoadAsync(page.Select(i => i.Location).OfType<DebugSource>(), ct);
            if (IsDisposed || generation != Volatile.Read(ref _generation)) return;
            var firstAddress = _instructions[0].Address;
            var fresh = Ordered(page).Where(i => i.Address < firstAddress).ToList();
            _first -= PageSize;
            if (fresh.Count == 0) { _startReached = true; return; }
            var leading = Rows.TakeWhile(r => !r.IsInstruction).Count();
            var rows = BuildRows(fresh, null, _instructions[0]);
            _instructions.InsertRange(0, fresh);
            Rows.RemoveRange(0, leading);
            Rows.InsertRange(0, rows);
            ApplyState(rows);
            RowsPrepended?.Invoke(rows.Count - leading);
            TrimEnd();
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or NotSupportedException) { }
        finally { if (generation == Volatile.Read(ref _generation)) _loadingEarlier = false; }
    }

    /// <summary>The view scrolled near the bottom: one more page after the last instruction.</summary>
    public async Task LoadLaterAsync()
    {
        if (_loadingLater || _endReached || _anchor is not { } anchor || _session is not IDebugSessionNative native || _instructions.Count == 0) return;
        _loadingLater = true;
        var generation = Volatile.Read(ref _generation);
        var ct = _cts.Token;
        try
        {
            var page = await _s.Ui.OnUi(native.DisassembleAsync(anchor, 0, _last + 1, PageSize, ct), ct);
            await _sources.LoadAsync(page.Select(i => i.Location).OfType<DebugSource>(), ct);
            if (IsDisposed || generation != Volatile.Read(ref _generation)) return;
            var lastAddress = _instructions[^1].Address;
            var fresh = Ordered(page).Where(i => i.Address > lastAddress).ToList();
            _last += PageSize;
            if (fresh.Count == 0) { _endReached = true; return; }
            var rows = BuildRows(fresh, _instructions[^1], null);
            _instructions.AddRange(fresh);
            Rows.AddRange(rows);
            ApplyState(rows);
            TrimStart();
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or NotSupportedException) { }
        finally { if (generation == Volatile.Read(ref _generation)) _loadingLater = false; }
    }

    /// <summary>Over the bound after an append: the first instructions go (with their source/function rows).</summary>
    private void TrimStart()
    {
        var excess = _instructions.Count - MaxInstructions;
        if (excess <= 0) return;
        var keepFrom = _instructions[excess];
        var cut = Rows.TakeWhile(r => !(r.IsInstruction && r.Address == keepFrom.Address)).Count();
        cut -= LeadingHeaders(keepFrom, _instructions[excess - 1]); // the kept instruction's own header rows stay
        _instructions.RemoveRange(0, excess);
        _first += excess;
        _startReached = false;
        if (cut > 0)
        {
            Rows.RemoveRange(0, cut);
            RowsPrepended?.Invoke(-cut);
        }
    }

    /// <summary>Over the bound after a prepend: the last instructions go.</summary>
    private void TrimEnd()
    {
        var excess = _instructions.Count - MaxInstructions;
        if (excess <= 0) return;
        var lastKept = _instructions[MaxInstructions - 1];
        var index = 0;
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (Rows[i].IsInstruction && Rows[i].Address == lastKept.Address) { index = i; break; }
        _instructions.RemoveRange(MaxInstructions, excess);
        _last -= excess;
        _endReached = false;
        if (index + 1 < Rows.Count) Rows.RemoveRange(index + 1, Rows.Count - index - 1);
    }

    private static IReadOnlyList<DebugInstruction> Ordered(IReadOnlyList<DebugInstruction> page)
    {
        // adapters answer in address order; drop duplicates an overlapping page may repeat
        var result = new List<DebugInstruction>(page.Count);
        foreach (var i in page)
            if (result.Count == 0 || i.Address > result[^1].Address) result.Add(i);
        return result;
    }

    // ------------------------------------------------------------------ rows

    /// <summary>The rows of <paramref name="page"/>: each instruction preceded by its function's name and source line when they change.
    /// <paramref name="after"/>: the instruction before the page; <paramref name="before"/>: the one after it (its header rows are rebuilt).</summary>
    private List<DisassemblyRow> BuildRows(IReadOnlyList<DebugInstruction> page, DebugInstruction? after, DebugInstruction? before)
    {
        var rows = new List<DisassemblyRow>(page.Count + page.Count / 4);
        var previous = after;
        foreach (var i in page)
        {
            AddHeaders(rows, i, previous);
            rows.Add(new DisassemblyRow(DisassemblyRowKind.Instruction, i.Address, i.AddressText, i.Bytes ?? "", i.Instruction, i.IsInvalid) { Symbol = i.Symbol });
            previous = i;
        }
        if (before is not null) AddHeaders(rows, before, previous);
        return rows;
    }

    private int LeadingHeaders(DebugInstruction i, DebugInstruction? previous)
    {
        var rows = new List<DisassemblyRow>(2);
        AddHeaders(rows, i, previous);
        return rows.Count;
    }

    private void AddHeaders(List<DisassemblyRow> rows, DebugInstruction i, DebugInstruction? previous)
    {
        if (i.Symbol is { Length: > 0 } symbol && symbol != previous?.Symbol && !symbol.Contains('+'))
            rows.Add(new DisassemblyRow(DisassemblyRowKind.Symbol, i.Address, i.AddressText, "", symbol + ":"));
        if (i.Location?.Path is { Length: > 0 } path && i.Line is { } line && (previous?.Location?.Path != path || previous.Line != line))
        {
            var lines = _sources.TryGet(path);
            var text = lines is not null && line >= 0 && line < lines.Length ? lines[line].Trim() : "";
            rows.Add(new DisassemblyRow(DisassemblyRowKind.Source, i.Address, i.AddressText, "", $"{Path.GetFileName(path)}:{line + 1}  {text}")
            {
                Source = i.Location,
                Line = line,
            });
        }
    }

    private void ApplyState(IEnumerable<DisassemblyRow> rows)
    {
        var breakpoints = SessionBreakpoints();
        foreach (var r in rows)
        {
            if (!r.IsInstruction) continue;
            r.IsCurrent = _current is { } c && r.Address == c;
            Apply(r, breakpoints);
        }
    }

    private Dictionary<ulong, InstructionBreakpoint> SessionBreakpoints()
    {
        var map = new Dictionary<ulong, InstructionBreakpoint>();
        if (Native is not { } native || _session is null) return map;
        foreach (var b in native.InstructionBreakpoints)
            if (b.SessionId == _session.Id && DebugAddress.TryParse(b.InstructionReference, out var a)) map[a + (ulong)b.Offset] = b;
        return map;
    }

    private void Apply(DisassemblyRow row, Dictionary<ulong, InstructionBreakpoint> breakpoints)
    {
        var bp = breakpoints.GetValueOrDefault(row.Address);
        row.HasBreakpoint = bp is { Enabled: true };
        row.BreakpointVerified = bp is not null && _s.Breakpoints.GetStatus(bp.Id) is { Verified: true };
    }

    private void RefreshBreakpoints()
    {
        if (IsDisposed) return;
        var breakpoints = SessionBreakpoints();
        foreach (var r in Rows)
            if (r.IsInstruction) Apply(r, breakpoints);
    }

    /// <summary>Adds or removes the breakpoint on an instruction row (F9, a click in the margin).</summary>
    public void ToggleBreakpoint(DisassemblyRow row)
    {
        if (!row.IsInstruction || row.IsInvalid || _session is not { } session || Native is not { } native) return;
        if (session is not IDebugSessionNative { SupportsInstructionBreakpoints: true })
        {
            _s.Window?.SetStatusBarMessage("The debug adapter does not support instruction breakpoints.", TimeSpan.FromSeconds(5));
            return;
        }
        native.ToggleInstruction(row.AddressText, row.Symbol is { Length: > 0 } s ? $"{s} ({row.AddressText})" : row.AddressText, session.Id);
    }

    /// <summary>The Memory window at an instruction's address.</summary>
    public void ShowMemory(DisassemblyRow row) => _ = _s.NativeViews.ShowMemory(new NativeTarget(row.AddressText, row.Symbol ?? row.AddressText));

    public Task CopyAsync(string text) => _s.Window?.CopyToClipboardAsync(text).AsTask() ?? Task.CompletedTask;

    /// <summary>Opens a source row's line in the editor.</summary>
    public async Task GoToSourceAsync(DisassemblyRow row)
    {
        if (row.Source?.ToUri() is not { } uri || row.Line is not { } line || _s.Navigation is not { } nav) return;
        try { await nav.OpenAsync(uri, Core.Text.TextRange.At(line, 0)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        SetKeyboardFocus(false);
        _tracker.Dispose();
        _subs.Dispose();
        try { _cts.Cancel(); } catch (AggregateException) { }
        _cts.Dispose();
        _instructions.Clear();
        Rows.Reset();
        _session = null;
        _focus = default;
        _selected = null;
        RowsPrepended = null;
        RevealRequested = null;
    }
}
