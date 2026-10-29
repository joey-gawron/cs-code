using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Panels.Debug.Infrastructure;
using CsCode.Ui.Collections;

namespace CsCode.Panels.Debug.Threads;

/// <summary>One thread of the debuggee: id, name, where it is (top frame) and whether it is running or stopped.</summary>
public sealed class ThreadRow : ObservableObject
{
    private string _location = "";
    private bool _isStopped;
    private bool _isCurrent;

    internal ThreadRow(DebugThread thread)
    {
        Id = thread.Id;
        Name = string.IsNullOrWhiteSpace(thread.Name) ? "Thread " + thread.Id : thread.Name;
    }

    public int Id { get; }
    public string Name { get; private set; }
    public string Location { get => _location; internal set => SetProperty(ref _location, value); }
    public bool IsStopped { get => _isStopped; internal set { if (SetProperty(ref _isStopped, value)) OnPropertyChanged(nameof(State)); } }
    public string State => _isStopped ? "Stopped" : "Running";
    public bool IsCurrent { get => _isCurrent; internal set => SetProperty(ref _isCurrent, value); }
    internal DebugStackFrame? TopFrame { get; set; }

    /// <summary>Takes over a freshly listed thread with the same id, keeping this row (and with it the list's selection and focus).</summary>
    internal void UpdateFrom(ThreadRow other)
    {
        Name = other.Name;
        IsStopped = other.IsStopped;
        OnPropertyChanged(nameof(Name));
    }
}

/// <summary>
/// Threads window: every thread of the focused session with its state and the function it is in. Switching threads focuses the thread's top
/// frame (through <see cref="IDebugFrameFocus"/>), which re-scopes Call Stack, Locals, Autos and Watch. Freezing/thawing threads is not
/// offered: the debug contracts have no such operation.
/// </summary>
public sealed class ThreadsViewModel : ObservableObject, IDisposable, IRowsReplacedNotifier
{
    public event Action? RowsReplacing;
    public event Action? RowsReplaced;

    public const int MaxThreads = 500;
    private const int MaxLocationLookups = 100;

    private readonly DebugPanelServices _s;
    private readonly DebugFocusTracker _tracker;
    private readonly string _reloadKey = "debug.threads.reload." + Guid.NewGuid().ToString("N");
    private DisposableStore? _sessionSubs;
    private CancellationTokenSource _cts = new();
    private long _generation;
    private IDebugSession? _session;
    private long _loadedEpoch = -1;
    private DebugFocus _focus;
    private string? _status = "Not debugging";
    private ThreadRow? _selected;
    private int _picks;
    private int _disposed;

    public ThreadsViewModel(DebugPanelServices services)
    {
        _s = services;
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowStatus));
        _tracker = new DebugFocusTracker(services, OnFocus);
        _tracker.Start();
    }

    public BulkObservableCollection<ThreadRow> Rows { get; } = new();
    public ThreadRow? SelectedRow { get => _selected; set => SetProperty(ref _selected, value); }
    public string? StatusText { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(ShowStatus)); } }
    public bool ShowStatus => Rows.Count == 0 && !string.IsNullOrEmpty(_status);
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private bool IsLive(long generation) => generation == Volatile.Read(ref _generation) && !IsDisposed;

    private void OnFocus(DebugFocus focus)
    {
        if (IsDisposed) return;
        _focus = focus;
        if (focus.Session is null || focus.State == DebugSessionState.Terminated)
        {
            Clear("Not debugging");
            return;
        }
        if (!ReferenceEquals(focus.Session, _session)) Bind(focus.Session);
        if (focus.State == DebugSessionState.Initializing) { StatusText = "Starting…"; return; }

        if (!focus.IsStopped)
        {
            // Running: keep the thread list, mark everything running, forget top frames (they are stale now).
            CancelPending();
            _loadedEpoch = -1;
            foreach (var r in Rows) { r.IsStopped = false; r.Location = ""; r.TopFrame = null; }
            if (Rows.Count == 0) _ = LoadAsync(focus);
            return;
        }
        if (focus.Epoch != _loadedEpoch) { _ = LoadAsync(focus); return; }
        MarkCurrent(focus.ThreadId);
    }

    private void Bind(IDebugSession session)
    {
        _sessionSubs?.Dispose();
        _session = session;
        _loadedEpoch = -1;
        var subs = new DisposableStore();
        subs.Add(session.ThreadChanged.Subscribe(_ => _s.Ui.PostCoalesced(_reloadKey, Reload)));
        _sessionSubs = subs;
    }

    private void Reload()
    {
        if (!IsDisposed && _focus.Session is not null) _ = LoadAsync(_focus);
    }

    private void Clear(string status)
    {
        CancelPending();
        _sessionSubs?.Dispose();
        _sessionSubs = null;
        _session = null;
        _loadedEpoch = -1;
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

    internal async Task LoadAsync(DebugFocus focus)
    {
        var session = focus.Session!;
        CancelPending();
        var generation = Volatile.Read(ref _generation);
        var ct = _cts.Token;
        _loadedEpoch = focus.IsStopped ? focus.Epoch : -1;
        try
        {
            var threads = await _s.Ui.OnUi(session.GetThreadsAsync(ct), ct);
            if (!IsLive(generation)) return;
            var stopped = focus.IsStopped;
            var rows = threads.Take(MaxThreads).Select(t => new ThreadRow(t) { IsStopped = stopped }).ToList();
            if (Rows.Count == rows.Count && Rows.Zip(rows).All(p => p.First.Id == p.Second.Id))
            {
                for (var i = 0; i < rows.Count; i++) Rows[i].UpdateFrom(rows[i]);
                rows = [.. Rows];
            }
            else
            {
                var keep = SelectedRow?.Id;
                RowsReplacing?.Invoke();
                Rows.ReplaceAll(rows);
                if (keep is { } id) SelectedRow = rows.FirstOrDefault(r => r.Id == id);
                RowsReplaced?.Invoke();
            }
            StatusText = rows.Count == 0 ? "No threads" : null;
            MarkCurrent(focus.ThreadId ?? session.FocusedThreadId);
            if (stopped) await LoadLocationsAsync(session, rows, generation, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsLive(generation) && Rows.Count == 0) StatusText = ex is InvalidOperationException ? "Not available" : "Cannot load threads: " + ex.Message;
        }
    }

    private async Task LoadLocationsAsync(IDebugSession session, List<ThreadRow> rows, long generation, CancellationToken ct)
    {
        // One request in flight at a time and a hard cap: a debuggee with thousands of threads must not turn one stop into thousands of requests.
        foreach (var row in rows.Take(MaxLocationLookups))
        {
            if (!IsLive(generation)) return;
            try
            {
                var frames = await _s.Ui.OnUi(session.GetStackTraceAsync(row.Id, 0, 1, ct), ct);
                if (!IsLive(generation)) return;
                if (frames.Count > 0)
                {
                    row.TopFrame = frames[0];
                    row.Location = frames[0].Name;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* a thread that cannot be inspected simply shows no location */ }
        }
    }

    private void MarkCurrent(int? threadId)
    {
        foreach (var r in Rows) r.IsCurrent = threadId is { } id && r.Id == id;
    }

    /// <summary>Makes <paramref name="row"/>'s thread the current one: focuses its top frame (and opens its source when it has one).</summary>
    public async Task SwitchToAsync(ThreadRow row, bool openSource = true)
    {
        var session = _session;
        if (session is null || IsDisposed) return;
        var generation = Volatile.Read(ref _generation);
        var ct = _cts.Token;
        try
        {
            var frame = row.TopFrame;
            if (frame is null)
            {
                var frames = await _s.Ui.OnUi(session.GetStackTraceAsync(row.Id, 0, 1, ct), ct);
                if (!IsLive(generation) || frames.Count == 0) return;
                frame = row.TopFrame = frames[0];
            }
            var pick = ++_picks;
            if (_s.FrameFocus is { } focus)
            {
                try { await _s.Ui.OnUi(focus.SetFocusedFrameAsync(session.Id, row.Id, frame.Id, ct), ct); }
                catch (InvalidOperationException ex) // the service throws for a frame it cannot focus
                {
                    if (IsLive(generation) && pick == _picks)
                        _s.Window?.SetStatusBarMessage($"Could not switch to the thread '{row.Name}': {ex.Message}", TimeSpan.FromSeconds(4));
                    return;
                }
                if (!IsLive(generation) || pick != _picks) return;
                // The service may decline silently; the variables windows follow it, so only a thread it really switched to is marked.
                if (focus.FocusedFrameId != frame.Id)
                {
                    _s.Window?.SetStatusBarMessage($"Could not switch to the thread '{row.Name}': the debugger did not select its frame.", TimeSpan.FromSeconds(4));
                    return;
                }
            }
            if (!IsLive(generation)) return;
            MarkCurrent(row.Id);
            if (openSource && _s.Navigation is { } navigation && frame.Source is { } source)
            {
                Uri? uri = null;
                try { uri = source.ToUri(); }
                catch (UriFormatException) { }
                if (uri is not null) await _s.Ui.OnUi(navigation.OpenAsync(uri, TextRange.At(Math.Max(0, frame.Line), Math.Max(0, frame.Column)), preview: true, preserveFocus: true, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsLive(generation)) _s.Window?.SetStatusBarMessage("Could not switch threads: " + ex.Message, TimeSpan.FromSeconds(4));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _tracker.Dispose();
        _sessionSubs?.Dispose();
        try { _cts.Cancel(); }
        catch (AggregateException) { }
        _cts.Dispose();
        _session = null;
        _selected = null;
        RowsReplacing = null;
        RowsReplaced = null;
        Rows.Clear();
        _focus = default;
    }
}
