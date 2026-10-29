using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Debugging;
using CsCode.Core.Text;
using CsCode.Panels.Debug.Infrastructure;

namespace CsCode.Panels.Debug.CallStack;

public enum CallStackRowKind { Thread, Frame, More, ExternalCode, Message }

/// <summary>One row of the Call Stack window: a thread header, a stack frame, the "load more frames" pager or a collapsed "[External Code]" run.</summary>
public sealed class CallStackRow : ObservableObject
{
    private bool _isCurrent;
    private bool _isExpanded;

    internal CallStackRow(CallStackRowKind kind, int depth, string text)
    {
        Kind = kind;
        Depth = depth;
        Text = text;
    }

    public CallStackRowKind Kind { get; }
    public int Depth { get; }
    public string Text { get; }
    /// <summary><c>Program.cs : 12</c> (1-based line), empty for frames without source.</summary>
    public string Location { get; internal init; } = "";
    public string? Module { get; internal init; }
    public int ThreadId { get; internal init; }
    public DebugStackFrame? Frame { get; internal init; }
    public bool IsExternal { get; internal init; }
    /// <summary>First frame of its thread (the one execution stopped in).</summary>
    public bool IsTopFrame { get; internal init; }
    public int HiddenFrames { get; internal init; }

    /// <summary>The frame the Locals/Autos/Watch windows and the editor's stop marker currently follow.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set { if (SetProperty(ref _isCurrent, value)) { OnPropertyChanged(nameof(ArrowKind)); OnPropertyChanged(nameof(ShowArrow)); } }
    }

    public bool IsExpanded { get => _isExpanded; internal set { if (SetProperty(ref _isExpanded, value)) OnPropertyChanged(nameof(ChevronKind)); } }

    public bool IsThread => Kind == CallStackRowKind.Thread;
    public bool IsFrame => Kind == CallStackRowKind.Frame;
    public bool IsPager => Kind is CallStackRowKind.More or CallStackRowKind.Message;
    public bool ShowArrow => Kind == CallStackRowKind.Frame && _isCurrent;
    /// <summary>Yellow arrow on the stopped location (top frame), outlined arrow for a caller frame picked by the user.</summary>
    public string ArrowKind => IsTopFrame ? "debug-stackframe-active" : "debug-stackframe";
    public string? ChevronKind => Kind == CallStackRowKind.Thread ? (_isExpanded ? "chevron-down" : "chevron-right") : null;
    public Thickness Indent => new(Depth * 16, 0, 0, 0);
    public string Tooltip => Kind == CallStackRowKind.Frame ? (Location.Length > 0 ? $"{Text}\n{Location}" : Text) : Text;

    public override string ToString() => Text;
}

/// <summary>
/// Call Stack window. Shows the frames of the stopped session: a flat list when the debuggee has one thread, otherwise threads with their
/// frames below them. Long stacks are paged (<see cref="PageSize"/> frames per request, a "load more" row for the rest). Picking a frame
/// goes through <see cref="IDebugFrameFocus"/> (which moves the editor's stop marker and re-scopes Locals/Autos/Watch) and opens the
/// source through <see cref="INavigationService"/>. Frames without source (runtime frames) or hinted <c>subtle</c>/<c>label</c> are
/// greyed, and can be folded into one "[External Code]" row.
/// </summary>
public sealed class CallStackViewModel : ObservableObject, IDisposable
{
    public const int PageSize = 50;
    public const int MaxThreads = 500;
    public const int MaxFramesPerThread = 5_000;

    private sealed class ThreadModel(DebugThread thread)
    {
        public DebugThread Thread { get; } = thread;
        public List<DebugStackFrame> Frames { get; } = new();
        public bool Loaded { get; set; }
        public bool Expanded { get; set; }
        public int NextStart { get; set; }
        public bool HasMore { get; set; }
        public bool Loading { get; set; }
    }

    private readonly DebugPanelServices _s;
    private readonly DebugFocusTracker _tracker;
    private readonly List<ThreadModel> _threads = new();
    private CancellationTokenSource _cts = new();
    private long _generation;
    private IDebugSession? _session;
    private long _loadedEpoch = -1;
    private DebugFocus _focus;
    private int? _currentFrame;
    private int _currentThread = -1;
    private bool _showExternal = true;
    private string? _status = "Not debugging";
    private CallStackRow? _selected;
    private int _disposed;

    public CallStackViewModel(DebugPanelServices services)
    {
        _s = services;
        _tracker = new DebugFocusTracker(services, OnFocus);
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowStatus));
        _tracker.Start();
    }

    public RangeObservableCollection<CallStackRow> Rows { get; } = new();

    public CallStackRow? SelectedRow { get => _selected; set => SetProperty(ref _selected, value); }

    public string? StatusText { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(ShowStatus)); } }
    public bool ShowStatus => Rows.Count == 0 && !string.IsNullOrEmpty(_status);

    /// <summary>Show runtime/library frames (greyed) instead of folding them into "[External Code]".</summary>
    public bool ShowExternalCode
    {
        get => _showExternal;
        set { if (SetProperty(ref _showExternal, value)) Rebuild(); }
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private bool IsLive(long generation) => generation == Volatile.Read(ref _generation) && !IsDisposed;

    // ------------------------------------------------------------------ following the debugger

    private void OnFocus(DebugFocus focus)
    {
        if (IsDisposed) return;
        _focus = focus;
        if (!focus.IsStopped)
        {
            Clear(focus.Session is null || focus.State == DebugSessionState.Terminated ? "Not debugging" : focus.State == DebugSessionState.Initializing ? "Starting…" : "Running…");
            return;
        }
        if (!ReferenceEquals(focus.Session, _session) || focus.Epoch != _loadedEpoch)
        {
            _ = LoadAsync(focus);
            return;
        }
        if (focus.HasFrame && (focus.FrameId != _currentFrame || focus.ThreadId != _currentThread))
        {
            _currentFrame = focus.FrameId;
            _currentThread = focus.ThreadId!.Value;
            _ = EnsureThreadShownAsync(_currentThread, _generation);
        }
    }

    private void Clear(string status)
    {
        CancelPending();
        _session = null;
        _loadedEpoch = -1;
        _currentFrame = null;
        _currentThread = -1;
        _threads.Clear();
        SelectedRow = null;
        Rows.Reset();
        StatusText = status;
    }

    private void CancelPending()
    {
        Interlocked.Increment(ref _generation);
        var old = _cts;
        _cts = new CancellationTokenSource();
        try { old.Cancel(); }
        catch (AggregateException) { }
        old.Dispose();
    }

    /// <summary>Loads threads and the focus thread's first page of frames; completes when the rows are in place.</summary>
    internal async Task LoadAsync(DebugFocus focus)
    {
        var session = focus.Session!;
        CancelPending();
        var generation = Generation;
        var ct = _cts.Token;
        _session = session;
        _loadedEpoch = focus.Epoch;
        _threads.Clear();
        SelectedRow = null;
        Rows.Reset();
        StatusText = "Loading…";
        try
        {
            var threads = await _s.Ui.OnUi(session.GetThreadsAsync(ct), ct);
            if (!IsLive(generation)) return;
            foreach (var t in threads.Take(MaxThreads)) _threads.Add(new ThreadModel(t));
            if (_threads.Count == 0) { StatusText = "No threads"; return; }

            var focusThread = focus.ThreadId ?? session.FocusedThreadId ?? _threads[0].Thread.Id;
            var model = _threads.FirstOrDefault(t => t.Thread.Id == focusThread) ?? _threads[0];
            model.Expanded = true;
            _currentThread = model.Thread.Id;
            await LoadFramesAsync(model, session, generation, ct);
            if (!IsLive(generation)) return;
            _currentFrame = focus.FrameId ?? model.Frames.FirstOrDefault(f => HasSource(f))?.Id ?? model.Frames.FirstOrDefault()?.Id;
            if (_focus.HasFrame && ReferenceEquals(_focus.Session, session) && _focus.Epoch == focus.Epoch)
            {
                _currentFrame = _focus.FrameId;
                _currentThread = _focus.ThreadId!.Value;
            }
            StatusText = _threads.Count > 0 && model.Frames.Count == 0 ? "No stack frames" : null;
            Rebuild();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsLive(generation)) StatusText = ex is InvalidOperationException ? "Not available" : "Cannot load the call stack: " + ex.Message;
        }
    }

    private async Task LoadFramesAsync(ThreadModel model, IDebugSession session, long generation, CancellationToken ct)
    {
        if (model.Loading) return;
        model.Loading = true;
        try
        {
            var frames = await _s.Ui.OnUi(session.GetStackTraceAsync(model.Thread.Id, model.NextStart, PageSize, ct), ct);
            if (!IsLive(generation)) return;
            var room = Math.Max(0, MaxFramesPerThread - model.Frames.Count);
            var take = Math.Min(frames.Count, room);
            for (var i = 0; i < take; i++) model.Frames.Add(frames[i]);
            model.NextStart += frames.Count;
            model.HasMore = frames.Count >= PageSize && model.Frames.Count < MaxFramesPerThread;
            model.Loaded = true;
        }
        finally { model.Loading = false; }
    }

    private async Task EnsureThreadShownAsync(int threadId, long generation)
    {
        var model = _threads.FirstOrDefault(t => t.Thread.Id == threadId);
        var session = _session;
        if (model is null || session is null) { Rebuild(); return; }
        try
        {
            if (!model.Loaded) await LoadFramesAsync(model, session, generation, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* the stack of an unfocused thread is optional; the rows we have stay */ }
        if (!IsLive(generation)) return;
        model.Expanded = true;
        Rebuild();
    }

    // ------------------------------------------------------------------ rows

    private static bool HasSource(DebugStackFrame f) => f.Source is { } s && (!string.IsNullOrEmpty(s.Path) || s.SourceReference is > 0);

    internal static bool IsExternalFrame(DebugStackFrame f) =>
        f.PresentationHint is "subtle" or "label" || !HasSource(f);

    internal static string LocationOf(DebugStackFrame f)
    {
        if (!HasSource(f)) return "";
        var file = f.Source!.Name is { Length: > 0 } n ? n : System.IO.Path.GetFileName(f.Source.Path ?? "");
        return file.Length == 0 ? "" : $"{file} : {f.Line + 1}";
    }

    /// <summary>Recomputes the visible rows from the loaded frames (selection is restored by frame id).</summary>
    internal void Rebuild()
    {
        if (IsDisposed) return;
        var keepFrame = SelectedRow?.Frame?.Id;
        var keepThread = SelectedRow?.ThreadId;
        var rows = new List<CallStackRow>();
        var flat = _threads.Count <= 1;
        foreach (var t in _threads)
        {
            var depth = 0;
            if (!flat)
            {
                rows.Add(new CallStackRow(CallStackRowKind.Thread, 0, $"{(string.IsNullOrWhiteSpace(t.Thread.Name) ? "Thread" : t.Thread.Name)} (ID {t.Thread.Id})")
                { ThreadId = t.Thread.Id, IsExpanded = t.Expanded });
                if (!t.Expanded) continue;
                depth = 1;
            }
            AddFrameRows(rows, t, depth);
        }
        Rows.ReplaceAll(rows);
        if (keepFrame is { } id) SelectedRow = rows.FirstOrDefault(r => r.Frame?.Id == id && r.ThreadId == keepThread);
        OnPropertyChanged(nameof(ShowStatus));
    }

    private void AddFrameRows(List<CallStackRow> rows, ThreadModel t, int depth)
    {
        var hidden = 0;
        void FlushHidden()
        {
            if (hidden == 0) return;
            rows.Add(new CallStackRow(CallStackRowKind.ExternalCode, depth, "[External Code]") { ThreadId = t.Thread.Id, IsExternal = true, HiddenFrames = hidden });
            hidden = 0;
        }
        for (var i = 0; i < t.Frames.Count; i++)
        {
            var f = t.Frames[i];
            var external = IsExternalFrame(f);
            var isCurrent = f.Id == _currentFrame && t.Thread.Id == _currentThread;
            if (external && !_showExternal && !isCurrent) { hidden++; continue; }
            FlushHidden();
            rows.Add(new CallStackRow(CallStackRowKind.Frame, depth, f.Name)
            {
                Frame = f, ThreadId = t.Thread.Id, IsExternal = external, IsTopFrame = i == 0, Location = LocationOf(f), Module = f.ModuleId,
                IsCurrent = isCurrent,
            });
        }
        FlushHidden();
        if (t.HasMore) rows.Add(new CallStackRow(CallStackRowKind.More, depth, "… load more frames") { ThreadId = t.Thread.Id });
    }

    // ------------------------------------------------------------------ activation

    /// <summary>
    /// A row was clicked (<paramref name="takeFocus"/> false: keep keyboard focus in the window) or double-clicked / Enter (true: move focus to the editor).
    /// Frames are focused through <see cref="IDebugFrameFocus"/> and their source is opened; threads toggle; the pager loads more frames.
    /// </summary>
    public async Task ActivateAsync(CallStackRow row, bool takeFocus)
    {
        var session = _session;
        if (session is null || IsDisposed) return;
        var generation = Generation;
        try
        {
            switch (row.Kind)
            {
                case CallStackRowKind.Thread:
                    await ToggleThreadAsync(row.ThreadId);
                    break;
                case CallStackRowKind.More:
                    await LoadMoreAsync(row.ThreadId);
                    break;
                case CallStackRowKind.Frame when row.Frame is { } frame:
                    if (_s.FrameFocus is { } focus) await _s.Ui.OnUi(focus.SetFocusedFrameAsync(session.Id, row.ThreadId, frame.Id, _cts.Token), _cts.Token);
                    if (!IsLive(generation)) return;
                    _currentFrame = frame.Id;
                    _currentThread = row.ThreadId;
                    foreach (var r in Rows) r.IsCurrent = r.Kind == CallStackRowKind.Frame && r.Frame?.Id == frame.Id && r.ThreadId == row.ThreadId;
                    await OpenSourceAsync(session, frame, takeFocus);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsLive(generation)) _s.Window?.SetStatusBarMessage("Could not switch frames: " + ex.Message, TimeSpan.FromSeconds(4));
        }
    }

    private async Task OpenSourceAsync(IDebugSession session, DebugStackFrame frame, bool takeFocus)
    {
        if (_s.Navigation is not { } navigation || frame.Source is not { } source) return;
        Uri? uri = null;
        try { uri = source.ToUri(); }
        catch (UriFormatException) { }
        if (uri is null && source.SourceReference is > 0 and var reference) uri = DebugSourceUri.Create(session.Id, reference, source.Name);
        if (uri is null) return;
        await _s.Ui.OnUi(navigation.OpenAsync(uri, TextRange.At(Math.Max(0, frame.Line), Math.Max(0, frame.Column)), preview: !takeFocus, preserveFocus: !takeFocus, _cts.Token), _cts.Token);
    }

    private async Task ToggleThreadAsync(int threadId)
    {
        var model = _threads.FirstOrDefault(t => t.Thread.Id == threadId);
        var session = _session;
        if (model is null || session is null) return;
        if (model.Expanded) { model.Expanded = false; Rebuild(); return; }
        var generation = Generation;
        model.Expanded = true;
        if (!model.Loaded) await LoadFramesAsync(model, session, generation, _cts.Token);
        if (IsLive(generation)) Rebuild();
    }

    private async Task LoadMoreAsync(int threadId)
    {
        var model = _threads.FirstOrDefault(t => t.Thread.Id == threadId);
        var session = _session;
        if (model is null || session is null || !model.HasMore) return;
        var generation = Generation;
        await LoadFramesAsync(model, session, generation, _cts.Token);
        if (IsLive(generation)) Rebuild();
    }

    /// <summary>The stack of the current thread as text (Copy Call Stack), one frame per line.</summary>
    public string GetCallStackText()
    {
        var model = _threads.FirstOrDefault(t => t.Thread.Id == _currentThread) ?? _threads.FirstOrDefault();
        if (model is null) return "";
        return string.Join(Environment.NewLine, model.Frames.Select(f => LocationOf(f) is { Length: > 0 } loc ? $"{f.Name}  [{loc}]" : f.Name));
    }

    public async Task CopyCallStackAsync()
    {
        if (_s.Window is { } window) await window.CopyToClipboardAsync(GetCallStackText());
    }

    private long Generation => Volatile.Read(ref _generation);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _tracker.Dispose();
        try { _cts.Cancel(); }
        catch (AggregateException) { }
        _cts.Dispose();
        _threads.Clear();
        _session = null;
        _selected = null;
        Rows.Reset();
        _focus = default;
    }
}
