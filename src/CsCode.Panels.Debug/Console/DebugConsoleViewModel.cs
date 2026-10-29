using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Panels.Debug.Infrastructure;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Debug.Console;

/// <summary>
/// Debug Console: scrolling program/debugger output (category-coloured, bounded to <see cref="MaxLines"/> lines, virtualised) and a REPL
/// that evaluates in the focused frame.
///
/// Output path (isolation from a flooding debuggee): the debug lane only enqueues into the <see cref="ConsoleOutputPump"/>; a pool thread
/// turns at most one batch per ≥ 16 ms into immutable lines; the UI thread applies a batch in slices of at most 4 ms
/// (<c>Lines</c> changes by at most one precise notification per slice) and trims the list to the bound.
/// </summary>
public sealed class DebugConsoleViewModel : ObservableObject, IDisposable
{
    public const int MaxLines = ConsoleBuffer.DefaultCapacity;
    public const int MaxCompletions = 100;
    private const double SliceMs = 4;
    /// <summary>Lines moved into the list per UI callback: small enough that one callback (list notification + container updates) stays ~1-2 ms.</summary>
    private const int MaxLinesPerSlice = 150;
    /// <summary>Lines waiting for the UI. A console can show 5,000 lines; if the UI is behind by more than this, older waiting lines are dropped (and said so).</summary>
    private const int MaxPendingLines = 2_000;

    private readonly DebugPanelServices _s;
    private readonly IUiDispatcher _ui;
    private readonly DisposableStore _subs = new();
    private readonly ConsoleBuffer _buffer = new();
    private readonly ConsoleOutputPump _pump;
    private readonly Queue<ConsoleLine> _pending = new();
    private readonly object _pendingGate = new();
    private readonly string _applyKey = "debug.console.apply." + Guid.NewGuid().ToString("N");
    private readonly ReplHistory _history;
    private readonly CancellationTokenSource _lifetime = new();
    // The newest program output event seen: a REPL line is recorded after it, so a later console shows it in the same place.
    private DebugOutputEventArgs? _lastOutput;
    private CancellationTokenSource? _completionCts;
    private string _filter = "";
    private string _input = "";
    private bool _autoScroll = true;
    private long _appliedSequence;
    private long _filterVersion;
    private long _droppedPending;
    private int _applyQueued;
    private bool _completionsOpen;
    private CompletionEntry? _selectedCompletion;
    private int _disposed;

    public DebugConsoleViewModel(DebugPanelServices services)
    {
        _s = services;
        _ui = services.Ui;
        _pump = new ConsoleOutputPump(_buffer, Accepts, EnqueueForUi);
        Lines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LineCount));
        ClearCommand = new UiCommand(Clear);
        SubmitCommand = new UiCommand(SubmitAsync, onError: ex => Append(ConsoleLineKind.ResultError, ex.Message));
        // The REPL's history and transcript outlive this view-model (the shell disposes it whenever the window is hidden).
        _history = services.Repl.History;
        _history.Reset();

        // Subscribe first, then read the history: events in both are recognised by reference and shown once.
        _subs.Add(services.Debug.Output.Subscribe(e =>
        {
            Volatile.Write(ref _lastOutput, e);
            _pump.Enqueue(e);
        }));
        IReadOnlyList<DebugOutputEventArgs> seed = [];
        if (services.OutputHistory is { } history)
        {
            _subs.Add(history.OutputCleared.Subscribe(_ => _ui.Post(ApplyCleared)));
            seed = history.GetOutputHistory();
            if (seed.Count > 0) Interlocked.CompareExchange(ref _lastOutput, seed[^1], null);
        }
        _pump.SetSeed(seed, services.Repl.Transcript());
    }

    /// <summary>The visible lines (≤ <see cref="MaxLines"/>), oldest first.</summary>
    public RangeObservableCollection<ConsoleLine> Lines { get; } = new();
    public int LineCount => Lines.Count;
    public CompletionList Completions { get; } = new();

    public UiCommand ClearCommand { get; }
    public UiCommand SubmitCommand { get; }

    /// <summary>Keep the newest line in view as output arrives (the lock button turns this off).</summary>
    public bool AutoScroll
    {
        get => _autoScroll;
        set { if (SetProperty(ref _autoScroll, value)) OnPropertyChanged(nameof(ScrollLock)); }
    }

    /// <summary>The "lock scroll" toggle: the inverse of <see cref="AutoScroll"/>.</summary>
    public bool ScrollLock { get => !_autoScroll; set => AutoScroll = !value; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Case-insensitive text filter over the retained lines.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value ?? "")) return;
            _ = RefilterAsync();
        }
    }

    /// <summary>The REPL's current input (multi-line: Shift+Enter inserts a line break).</summary>
    public string Input
    {
        get => _input;
        set { if (SetProperty(ref _input, value ?? "")) OnInputChanged(); }
    }

    public bool CompletionsOpen { get => _completionsOpen; private set => SetProperty(ref _completionsOpen, value); }
    public CompletionEntry? SelectedCompletion { get => _selectedCompletion; set => SetProperty(ref _selectedCompletion, value); }

    // ------------------------------------------------------------------ output path

    private bool Accepts(ConsoleLine line) => _filter.Length == 0 || line.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Pool thread: queue accepted lines for the UI (bounded; oldest dropped) and make sure one apply pass is scheduled.</summary>
    private void EnqueueForUi(IReadOnlyList<ConsoleLine> lines)
    {
        lock (_pendingGate)
        {
            foreach (var l in lines) _pending.Enqueue(l);
            while (_pending.Count > MaxPendingLines)
            {
                _pending.Dequeue();
                _droppedPending++;
            }
        }
        ScheduleApply();
    }

    private void ScheduleApply()
    {
        if (IsDisposed) return;
        if (Interlocked.Exchange(ref _applyQueued, 1) != 0) return;
        _ui.Post(ApplyPending, UiPriority.Background);
    }

    /// <summary>UI thread: moves pending lines into <see cref="Lines"/> in slices of ≤ 4 ms, then trims to the bound.</summary>
    internal void ApplyPending()
    {
        if (IsDisposed) return;
        Volatile.Write(ref _applyQueued, 0);
        var start = Stopwatch.GetTimestamp();
        var budget = (long)(SliceMs * Stopwatch.Frequency / 1000.0);
        var batch = new List<ConsoleLine>();
        long dropped;
        lock (_pendingGate)
        {
            dropped = _droppedPending;
            _droppedPending = 0;
            while (_pending.Count > 0 && batch.Count < MaxLinesPerSlice)
            {
                var line = _pending.Dequeue();
                if (line.Sequence > _appliedSequence) batch.Add(line);
                if (Stopwatch.GetTimestamp() - start >= budget && batch.Count > 0) break;
            }
        }
        if (batch.Count > 0)
        {
            var newest = batch.Max(l => l.Sequence);
            // The note borrows the first line's sequence so it never makes the "already applied" watermark skip older queued lines.
            if (dropped > 0) batch.Insert(0, new ConsoleLine(batch[0].Sequence, ConsoleLineKind.Info, $"… {dropped:N0} lines were not shown (the console could not keep up)"));
            Lines.AddRange(batch);
            _appliedSequence = Math.Max(_appliedSequence, newest);
        }
        else if (dropped > 0)
        {
            lock (_pendingGate) _droppedPending += dropped; // report with the next batch
        }
        TrimToBound();
        bool more;
        lock (_pendingGate) more = _pending.Count > 0;
        if (more) ScheduleApply();
    }

    private void TrimToBound()
    {
        if (Lines.Count > MaxLines) Lines.RemoveRange(0, Lines.Count - MaxLines);
    }

    /// <summary>Appends a line made here (the echo of an entered expression, a result or an error); it is also kept for the next console.</summary>
    private void Append(ConsoleLineKind kind, string text)
    {
        var line = new ConsoleLine(_pump.NextSequence(), kind, text.Length > ConsoleOutputPump.MaxLineChars ? text[..ConsoleOutputPump.MaxLineChars] + "…" : text);
        _s.Repl.Record(kind, line.Text, Volatile.Read(ref _lastOutput));
        _buffer.Append([line]);
        if (Accepts(line)) EnqueueForUi([line]);
    }

    private async Task RefilterAsync()
    {
        var version = Interlocked.Increment(ref _filterVersion);
        await Task.Delay(120).ConfigureAwait(false); // debounce typing
        if (IsDisposed || version != Interlocked.Read(ref _filterVersion)) return;
        var snapshot = _buffer.Snapshot();
        var matched = snapshot.Where(Accepts).ToArray();
        await _ui.SwitchToUi();
        if (IsDisposed || version != Interlocked.Read(ref _filterVersion)) return;
        // Lines already queued for the UI are in the snapshot too unless newer than it: keep only the newer ones.
        var newest = snapshot.Length == 0 ? 0 : snapshot[^1].Sequence;
        lock (_pendingGate)
        {
            var keep = _pending.Where(l => l.Sequence > newest).ToArray();
            _pending.Clear();
            foreach (var l in keep) _pending.Enqueue(l);
        }
        Lines.ReplaceAll(matched);
        _appliedSequence = newest;
        TrimToBound();
        ScheduleApply();
    }

    /// <summary>Copies the text of the given lines (the selection) to the clipboard, one per line.</summary>
    public async Task CopyAsync(IEnumerable<ConsoleLine> lines)
    {
        var text = string.Join(Environment.NewLine, lines.OrderBy(l => l.Sequence).Select(l => l.Text));
        if (text.Length > 0 && _s.Window is { } window) await window.CopyToClipboardAsync(text);
    }

    /// <summary>Clears the console and the history kept for late subscribers.</summary>
    public void Clear()
    {
        _s.OutputHistory?.ClearOutputHistory();
        ApplyCleared();
    }

    private void ApplyCleared()
    {
        if (IsDisposed) return;
        _s.Repl.ClearTranscript();
        Volatile.Write(ref _lastOutput, null);
        _buffer.Clear();
        lock (_pendingGate) { _pending.Clear(); _droppedPending = 0; }
        _appliedSequence = _pump.NextSequence();
        Lines.Reset();
    }

    // ------------------------------------------------------------------ REPL

    /// <summary>
    /// Evaluates the input in the focused frame with context <c>repl</c> and appends the echo, then the result (or the adapter's error).
    /// Empty input does nothing; the entry goes to the history first.
    /// </summary>
    public async Task SubmitAsync()
    {
        var text = _input.Trim();
        if (text.Length == 0) return;
        CloseCompletions();
        _s.Repl.AddHistory(text);
        Input = "";
        Append(ConsoleLineKind.Input, "> " + text.Replace("\r\n", "\n").Replace("\n", "\n  "));

        var session = _s.Debug.ActiveSession;
        if (session is null)
        {
            Append(ConsoleLineKind.ResultError, "There is no active debug session.");
            return;
        }
        var frame = session.State == DebugSessionState.Stopped ? _s.FrameFocus?.FocusedFrameId : null;
        var ct = _lifetime.Token;
        try
        {
            var result = await _ui.OnUi(session.EvaluateAsync(text, frame, "repl", ct), ct);
            if (IsDisposed) return;
            if (!string.IsNullOrEmpty(result.Result)) Append(ConsoleLineKind.Result, result.Result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed) Append(ConsoleLineKind.ResultError, ex.Message);
        }
    }

    /// <summary>↑: older history entry into the input (returns whether the input changed).</summary>
    public bool HistoryPrevious()
    {
        if (_history.Previous(_input) is not { } entry) return false;
        SetInputWithoutCompletions(entry);
        return true;
    }

    /// <summary>↓: newer history entry (ending at the draft being typed).</summary>
    public bool HistoryNext()
    {
        if (_history.Next() is not { } entry) return false;
        SetInputWithoutCompletions(entry);
        return true;
    }

    public IReadOnlyList<string> HistoryEntries => _history.Entries;

    private bool _suppressCompletion;

    private void SetInputWithoutCompletions(string text)
    {
        _suppressCompletion = true;
        Input = text;
        _suppressCompletion = false;
        CloseCompletions();
    }

    // ------------------------------------------------------------------ completions

    private void OnInputChanged()
    {
        if (_suppressCompletion || IsDisposed) return;
        if (_input.Length == 0) { CloseCompletions(); return; }
        _ = RequestCompletionsAsync(_input.Length);
    }

    /// <summary>
    /// Asks the adapter for completions of the text up to <paramref name="caret"/> (debounced; a newer request cancels the older one). Shows nothing
    /// when the adapter has no completions support, no session is stopped, or nothing matches.
    /// </summary>
    public async Task RequestCompletionsAsync(int caret)
    {
        _completionCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var old = Interlocked.Exchange(ref _completionCts, cts);
        old?.Dispose();
        var ct = cts.Token;
        var text = _input;
        caret = Math.Clamp(caret, 0, text.Length);
        var session = _s.Debug.ActiveSession;
        if (session is not { State: DebugSessionState.Stopped, Capabilities.SupportsCompletionsRequest: true }) { CloseCompletions(); return; }
        try
        {
            await Task.Delay(120, ct).ConfigureAwait(false);
            await _ui.SwitchToUi();
            if (ct.IsCancellationRequested || IsDisposed) return;
            var items = await _ui.OnUi(session.CompletionsAsync(_s.FrameFocus?.FocusedFrameId, text, caret, ct), ct);
            if (ct.IsCancellationRequested || IsDisposed || !string.Equals(text, _input, StringComparison.Ordinal)) return;
            var prefixStart = caret;
            while (prefixStart > 0 && (char.IsLetterOrDigit(text[prefixStart - 1]) || text[prefixStart - 1] is '_' or '$')) prefixStart--;
            var entries = items.Take(MaxCompletions)
                .Select(i => new CompletionEntry(i.Label, i.Text ?? i.Label, i.Type, i.Start ?? prefixStart, i.Length ?? (caret - (i.Start ?? prefixStart))))
                .ToList();
            Completions.ReplaceAll(entries);
            SelectedCompletion = entries.FirstOrDefault();
            CompletionsOpen = entries.Count > 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { CloseCompletions(); } // completions are a convenience: failures stay silent
    }

    public void CloseCompletions()
    {
        _completionCts?.Cancel();
        CompletionsOpen = false;
        SelectedCompletion = null;
        if (Completions.Count > 0) Completions.Reset();
    }

    /// <summary>Replaces the typed fragment with <paramref name="entry"/> and closes the list.</summary>
    public void AcceptCompletion(CompletionEntry entry)
    {
        var start = Math.Clamp(entry.Start, 0, _input.Length);
        var length = Math.Clamp(entry.Length, 0, _input.Length - start);
        _suppressCompletion = true;
        Input = string.Concat(_input.AsSpan(0, start), entry.Text, _input.AsSpan(start + length));
        _suppressCompletion = false;
        CloseCompletions();
        CaretAfterCompletion = start + entry.Text.Length;
        OnPropertyChanged(nameof(CaretAfterCompletion));
    }

    /// <summary>Where the caret belongs after <see cref="AcceptCompletion"/> (the view moves it).</summary>
    public int CaretAfterCompletion { get; private set; }

    public void SelectNextCompletion(int delta)
    {
        if (Completions.Count == 0) return;
        var index = SelectedCompletion is null ? -1 : Completions.IndexOf(SelectedCompletion);
        SelectedCompletion = Completions[Math.Clamp(index + delta, 0, Completions.Count - 1)];
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _lifetime.Cancel(); }
        catch (AggregateException) { }
        _completionCts?.Dispose();
        _subs.Dispose();
        _pump.Dispose();
        _buffer.Clear();
        lock (_pendingGate) _pending.Clear();
        Lines.Reset();
        Completions.Reset();
        _lifetime.Dispose();
    }
}

/// <summary>Completion candidates shown above the REPL input (≤ <see cref="DebugConsoleViewModel.MaxCompletions"/>).</summary>
public sealed class CompletionList : System.Collections.ObjectModel.ObservableCollection<CompletionEntry>
{
    public void ReplaceAll(IEnumerable<CompletionEntry> entries)
    {
        Items.Clear();
        foreach (var e in entries) Items.Add(e);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }

    public void Reset() => ReplaceAll([]);
}
