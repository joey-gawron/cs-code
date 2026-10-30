using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Debugging;
using CsCode.Core.Text;
using CsCode.Panels.Debug.Infrastructure;
using CsCode.Ui.Collections;

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
    /// <summary>The stopped location: the first frame of the thread execution stopped in (shows the yellow arrow, whatever frame is picked).</summary>
    public bool IsTopFrame { get; internal init; }
    public int HiddenFrames { get; internal init; }
    /// <summary>The frames an "[External Code]" row stands for (activating the row shows them).</summary>
    internal IReadOnlyList<int> HiddenFrameIds { get; init; } = [];

    /// <summary>The frame the Locals/Autos/Watch windows and the editor's stop marker currently follow.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set { if (SetProperty(ref _isCurrent, value)) { OnPropertyChanged(nameof(ArrowKind)); OnPropertyChanged(nameof(ShowArrow)); } }
    }

    public bool IsExpanded { get => _isExpanded; internal set { if (SetProperty(ref _isExpanded, value)) OnPropertyChanged(nameof(ChevronKind)); } }

    /// <summary>Takes over the volatile state of a freshly built row for the same frame/thread, keeping this instance (selection and focus stay put).</summary>
    internal void UpdateFrom(CallStackRow other)
    {
        IsCurrent = other._isCurrent;
        IsExpanded = other._isExpanded;
        OnPropertyChanged(nameof(ArrowKind));
        OnPropertyChanged(nameof(ShowArrow));
    }

    internal bool IsSameItem(CallStackRow other) =>
        Kind == other.Kind && ThreadId == other.ThreadId && Frame?.Id == other.Frame?.Id && Text == other.Text && Depth == other.Depth && IsTopFrame == other.IsTopFrame
        && HiddenFrames == other.HiddenFrames;

    public bool IsThread => Kind == CallStackRowKind.Thread;
    public bool IsFrame => Kind == CallStackRowKind.Frame;
    public bool IsPager => Kind is CallStackRowKind.More or CallStackRowKind.Message;
    public bool ShowArrow => Kind == CallStackRowKind.Frame && (_isCurrent || IsTopFrame);
    /// <summary>Yellow arrow on the stopped location (top frame); a caller frame picked by the user gets an outlined (green) arrow, as in Visual Studio.</summary>
    public string ArrowKind => IsTopFrame ? "debug-stackframe-active" : "debug-stackframe";
    public string? ChevronKind => Kind == CallStackRowKind.Thread ? (_isExpanded ? "triangle-down" : "triangle-right") : null;
    public Thickness Indent => new(Depth * 16, 0, 0, 0);
    public FontWeight TextWeight => Kind == CallStackRowKind.Thread ? FontWeight.SemiBold : FontWeight.Normal;
    public FontStyle TextStyle => Kind is CallStackRowKind.More or CallStackRowKind.ExternalCode ? FontStyle.Italic : FontStyle.Normal;
    public string Tooltip => Kind switch
    {
        CallStackRowKind.Frame => Location.Length > 0 ? $"{Text}\n{Location}" : Text,
        CallStackRowKind.ExternalCode => HiddenFrames == 1 ? "1 frame is hidden. Click to show it." : $"{HiddenFrames} frames are hidden. Click to show them.",
        _ => Text,
    };

    public override string ToString() => Text;
}

/// <summary>
/// Call Stack window. Shows the frames of the stopped session: a flat list when the debuggee has one thread, otherwise threads with their
/// frames below them. Long stacks are paged (<see cref="PageSize"/> frames per request, a "load more" row for the rest). Picking a frame
/// goes through <see cref="IDebugFrameFocus"/> (which moves the editor's stop marker and re-scopes Locals/Autos/Watch) and opens the
/// source through <see cref="INavigationService"/>. Frames without source (runtime frames) or hinted <c>subtle</c>/<c>label</c> are
/// greyed, and can be folded into one "[External Code]" row ("Show External Code" off).
/// <para>Frames the ADAPTER skips (hint <c>deemphasize</c>: js-debug's <c>skipFiles</c>, e.g. <c>&lt;node_internals&gt;/**</c>) are folded
/// even with "Show External Code" on, as VS Code does: a run of two or more becomes one "[External Code]" row. Activating any "[External
/// Code]" row shows the frames it stands for (greyed) until the next stop.</para>
/// </summary>
public sealed class CallStackViewModel : ObservableObject, IDisposable, IRowsReplacedNotifier
{
    public event Action? RowsReplacing;
    public event Action? RowsReplaced;

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
        /// <summary>Folded frames the user asked to see (by activating their "[External Code]" row); forgotten at the next stop.</summary>
        public HashSet<int> Revealed { get; } = new();
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
    private int _picks;
    private int _disposed;

    public CallStackViewModel(DebugPanelServices services)
    {
        _s = services;
        _tracker = new DebugFocusTracker(services, OnFocus);
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowStatus));
        _tracker.Start();
    }

    public BulkObservableCollection<CallStackRow> Rows { get; } = new();

    public CallStackRow? SelectedRow { get => _selected; set => SetProperty(ref _selected, value); }

    public string? StatusText { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(ShowStatus)); } }
    public bool ShowStatus => Rows.Count == 0 && !string.IsNullOrEmpty(_status);

    /// <summary>
    /// Show runtime/library frames (greyed) instead of folding them into "[External Code]". Frames the adapter skips are folded either way
    /// (see the class remarks); switching this forgets which folded frames the user revealed.
    /// </summary>
    public bool ShowExternalCode
    {
        get => _showExternal;
        set
        {
            if (!SetProperty(ref _showExternal, value)) return;
            foreach (var t in _threads) t.Revealed.Clear();
            Rebuild();
        }
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
        Rows.Clear();
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
        Rows.Clear();
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

    /// <summary>
    /// Greyed (and folded into "[External Code]" when that is off): runtime frames without source, frames hinted <c>subtle</c>, and frames the
    /// adapter de-emphasises — js-debug marks the Node internals it skips (<c>skipFiles</c>) with the hint <c>deemphasize</c> on the frame and on
    /// its source, which DAP defines for sources only.
    /// </summary>
    internal static bool IsExternalFrame(DebugStackFrame f) =>
        f.PresentationHint is "subtle" or "label" || IsSkippedFrame(f) || !HasSource(f);

    /// <summary>
    /// A frame the adapter itself de-emphasises because the configuration skips it (js-debug: <c>skipFiles</c>, hint <c>deemphasize</c> on the
    /// frame and on its source). Runs of these fold by default, whatever "Show External Code" says (VS Code collapses them too).
    /// </summary>
    internal static bool IsSkippedFrame(DebugStackFrame f) => f.PresentationHint == "deemphasize" || f.Source?.PresentationHint == "deemphasize";

    /// <summary>
    /// A separator such as js-debug's <c>await</c> between the frames of an async call stack (DAP hint <c>label</c>): greyed but never folded
    /// away — it is where the asynchronous part of the stack starts — and not a frame one can switch to.
    /// </summary>
    internal static bool IsLabelFrame(DebugStackFrame f) => f.PresentationHint == "label";

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
        if (Rows.Count == rows.Count && Rows.Zip(rows).All(p => p.First.IsSameItem(p.Second)))
        {
            // Same frames in the same order (the arrow moved): update in place so the list keeps its selection and focus.
            for (var i = 0; i < rows.Count; i++) Rows[i].UpdateFrom(rows[i]);
            return;
        }
        var keepKind = SelectedRow?.Kind;
        RowsReplacing?.Invoke();
        Rows.ReplaceAll(rows);
        if (keepKind is { } kind)
            SelectedRow = rows.FirstOrDefault(r => r.Kind == kind && r.ThreadId == keepThread && r.Frame?.Id == keepFrame);
        OnPropertyChanged(nameof(ShowStatus));
        RowsReplaced?.Invoke();
    }

    private void AddFrameRows(List<CallStackRow> rows, ThreadModel t, int depth)
    {
        var frames = t.Frames;
        for (var i = 0; i < frames.Count;)
        {
            // A run of frames that may be folded: external code when "Show External Code" is off, frames the adapter skips in any case.
            var end = i;
            while (end < frames.Count && Foldable(t, frames[end])) end++;
            var run = end - i;
            if (run > 0 && (!_showExternal || run >= 2))
            {
                var ids = new int[run];
                for (var k = 0; k < run; k++) ids[k] = frames[i + k].Id;
                rows.Add(new CallStackRow(CallStackRowKind.ExternalCode, depth, "[External Code]") { ThreadId = t.Thread.Id, IsExternal = true, HiddenFrames = run, HiddenFrameIds = ids });
                i = end;
                continue;
            }
            var stop = Math.Max(end, i + 1);
            for (; i < stop; i++) rows.Add(FrameRow(t, i, depth));
        }
        if (t.HasMore) rows.Add(new CallStackRow(CallStackRowKind.More, depth, "… load more frames") { ThreadId = t.Thread.Id });
    }

    /// <summary>Whether a frame may disappear into an "[External Code]" row: never the current frame, a separator or a frame the user revealed.</summary>
    private bool Foldable(ThreadModel t, DebugStackFrame f)
    {
        if (IsLabelFrame(f) || t.Revealed.Contains(f.Id) || (f.Id == _currentFrame && t.Thread.Id == _currentThread)) return false;
        return _showExternal ? IsSkippedFrame(f) : IsExternalFrame(f);
    }

    private CallStackRow FrameRow(ThreadModel t, int index, int depth)
    {
        var f = t.Frames[index];
        return new CallStackRow(CallStackRowKind.Frame, depth, f.Name)
        {
            Frame = f, ThreadId = t.Thread.Id, IsExternal = IsExternalFrame(f), IsTopFrame = index == 0 && t.Thread.Id == _currentThread, Location = LocationOf(f),
            Module = f.ModuleId, IsCurrent = f.Id == _currentFrame && t.Thread.Id == _currentThread,
        };
    }

    /// <summary>Shows the frames an "[External Code]" row stands for (greyed) and selects the first of them.</summary>
    private void Reveal(CallStackRow row)
    {
        if (_threads.FirstOrDefault(t => t.Thread.Id == row.ThreadId) is not { } model || row.HiddenFrameIds.Count == 0) return;
        foreach (var id in row.HiddenFrameIds) model.Revealed.Add(id);
        Rebuild();
        SelectedRow = Rows.FirstOrDefault(r => r.Kind == CallStackRowKind.Frame && r.ThreadId == row.ThreadId && r.Frame?.Id == row.HiddenFrameIds[0]) ?? SelectedRow;
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
                case CallStackRowKind.ExternalCode:
                    Reveal(row);
                    break;
                case CallStackRowKind.Frame when row.Frame is { } frame && !IsLabelFrame(frame):
                    var pick = ++_picks;
                    if (_s.FrameFocus is { } focus)
                    {
                        // The service THROWS for a frame it cannot focus (no longer valid, ended or unfocused session): say which frame.
                        try { await _s.Ui.OnUi(focus.SetFocusedFrameAsync(session.Id, row.ThreadId, frame.Id, _cts.Token), _cts.Token); }
                        catch (InvalidOperationException ex)
                        {
                            if (IsLive(generation) && pick == _picks)
                                _s.Window?.SetStatusBarMessage($"Could not switch to the frame '{frame.Name}': {ex.Message}", TimeSpan.FromSeconds(4));
                            return;
                        }
                        if (!IsLive(generation) || pick != _picks) return; // a newer pick (or stop) owns the arrow now
                        // The service may decline without an error (the real one only knows the frames it fetched for this stop). Only a frame
                        // it really focused gets the arrow and opens its source: Locals/Autos/Watch and the REPL follow the service, not this row.
                        if (focus.FocusedFrameId != frame.Id)
                        {
                            _s.Window?.SetStatusBarMessage($"Could not switch to the frame '{frame.Name}': the debugger did not select it.", TimeSpan.FromSeconds(4));
                            return;
                        }
                    }
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

    /// <summary>The window service (Copy of one frame), null when none is registered.</summary>
    internal Core.Platform.IWindowService? Window => _s.Window;

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
        RowsReplacing = null;
        RowsReplaced = null;
        Rows.Clear();
        _focus = default;
    }
}
