using System.Diagnostics;
using CsCode.Core.Lifecycle;

namespace CsCode.Panels.Search.Engine;

/// <summary>
/// One running search. Everything here is thread-safe and non-blocking for the caller: <see cref="Cancel"/> only flips a token,
/// results are pushed to subscribers from engine threads in small immutable batches (about every 16 ms, never from the UI thread),
/// and <see cref="Completed"/> fires exactly once. Subscribers must marshal to their own thread.
/// </summary>
public sealed class SearchSession : IDisposable
{
    internal enum StopReason { None = 0, User, ResultCap, Failed }

    private readonly CancellationTokenSource _cts = new();
    private readonly object _pendingGate = new();
    private readonly object _flushGate = new();
    private readonly Emitter<SearchBatch> _batches = new();
    private readonly Emitter<SearchProgress> _progress = new();
    private readonly Emitter<SearchCompletion> _completed = new();
    private readonly TaskCompletionSource<SearchCompletion> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _workersStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _startTicks = Stopwatch.GetTimestamp();
    private List<FileMatches> _pending = [];
    private SearchProgress? _lastProgress;
    private int _reserved, _filesScanned, _filesMatched, _skippedBinary, _skippedTooLarge, _skippedTimedOut, _skippedUnreadable;
    private int _stopReason, _done, _activeWorkers, _sequence;
    private volatile bool _enumerating = true;
    private volatile bool _truncated;
    private string? _error;

    internal SearchSession(int id, SearchQuery query, SearchLimits limits)
    {
        Id = id;
        Query = query;
        Limits = limits;
        Token = _cts.Token;
    }

    public int Id { get; }
    public SearchQuery Query { get; }
    internal SearchLimits Limits { get; set; }
    internal CancellationToken Token { get; }

    public IEventSource<SearchBatch> Batches => _batches;
    public IEventSource<SearchProgress> Progress => _progress;
    public IEventSource<SearchCompletion> Completed => _completed;

    /// <summary>Completes (never faults) with the outcome once the search is over, cancelled or failed.</summary>
    public Task<SearchCompletion> Result => _result.Task;

    /// <summary>Completes when every worker thread of this session has exited (tests assert nothing is left running after cancel/dispose).</summary>
    public Task WorkersStopped => _workersStopped.Task;

    internal bool CapReached => _truncated;

    /// <summary>Completes on the first stop request (user cancel, result cap or failure); the coordinator stops waiting for workers then.</summary>
    internal Task StopRequested => _stopRequested.Task;

    public int ActiveWorkers => Volatile.Read(ref _activeWorkers);
    public bool IsCompleted => Volatile.Read(ref _done) != 0;

    public SearchProgress CurrentProgress => new(Volatile.Read(ref _filesScanned), Volatile.Read(ref _filesMatched), Volatile.Read(ref _reserved), _enumerating);

    /// <summary>Stops the search. Returns immediately; workers notice within a line or a chunk. Safe from any thread, idempotent.</summary>
    public void Cancel() => Stop(StopReason.User);

    public void Dispose() => Cancel();

    // ------------------------------------------------------------------------------------------- engine-side

    internal void Stop(StopReason reason)
    {
        if (Interlocked.CompareExchange(ref _stopReason, (int)reason, (int)StopReason.None) != (int)StopReason.None) return;
        _stopRequested.TrySetResult();
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* all workers already gone */ }
    }

    internal void StopForFailure(string error)
    {
        _error ??= error;
        Stop(StopReason.Failed);
    }

    internal void MarkEnumerationDone() => _enumerating = false;

    internal void SetWorkerCount(int count) => Volatile.Write(ref _activeWorkers, count);

    internal void WorkerExited()
    {
        if (Interlocked.Decrement(ref _activeWorkers) > 0) return;
        _workersStopped.TrySetResult();
        _cts.Dispose();
    }

    /// <summary>Reserves one result slot; false once <c>search.maxResults</c> is reached (the search then stops, truncated).</summary>
    internal bool TryReserve()
    {
        if (Interlocked.Increment(ref _reserved) <= Limits.MaxResults) return true;
        Interlocked.Decrement(ref _reserved);
        _truncated = true;
        Stop(StopReason.ResultCap);
        return false;
    }

    internal void Release(int count) => Interlocked.Add(ref _reserved, -count);

    internal void Report(in WorkItem item, in ScanResult result)
    {
        if (result.Matches is { Count: > 0 } matches)
        {
            Interlocked.Increment(ref _filesMatched);
            var file = new FileMatches(item.Uri, matches, item.Text is not null, item.Version, result.Encoding, result.LineEnding);
            lock (_pendingGate) _pending.Add(file);
        }
        switch (result.Status)
        {
            case ScanStatus.Ok: Interlocked.Increment(ref _filesScanned); break;
            case ScanStatus.Cancelled: break;
            case ScanStatus.Binary: Interlocked.Increment(ref _skippedBinary); break;
            case ScanStatus.TooLarge: Interlocked.Increment(ref _skippedTooLarge); break;
            case ScanStatus.Unreadable: Interlocked.Increment(ref _skippedUnreadable); break;
            case ScanStatus.TimedOut:
                if (Interlocked.Increment(ref _skippedTimedOut) >= Limits.MaxTimedOutFiles)
                    StopForFailure($"The regular expression timed out on {Limits.MaxTimedOutFiles} files; it is too expensive. Simplify it or avoid lookarounds and backreferences.");
                break;
        }
    }

    internal void CountUnreadable() => Interlocked.Increment(ref _skippedUnreadable);
    internal void CountTooLarge() => Interlocked.Increment(ref _skippedTooLarge);

    /// <summary>Delivers pending results and changed progress to subscribers (called by the engine's flusher about every 16 ms).</summary>
    internal void Flush()
    {
        lock (_flushGate)
        {
            List<FileMatches>? batch = null;
            lock (_pendingGate)
            {
                if (_pending.Count > 0)
                {
                    batch = _pending;
                    _pending = [];
                }
            }
            if (batch is not null) _batches.Fire(new SearchBatch(++_sequence, batch));
            var p = CurrentProgress;
            if (p != _lastProgress)
            {
                _lastProgress = p;
                _progress.Fire(p);
            }
        }
    }

    internal void Complete(string? error = null)
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        _enumerating = false;
        Flush();
        var reason = (StopReason)Volatile.Read(ref _stopReason);
        var outcome = reason switch
        {
            StopReason.User => SearchOutcome.Cancelled,
            StopReason.ResultCap => SearchOutcome.Truncated,
            StopReason.Failed => SearchOutcome.Failed,
            _ => error is null ? SearchOutcome.Completed : SearchOutcome.Failed,
        };
        var stats = new SearchStats(
            Volatile.Read(ref _filesScanned), Volatile.Read(ref _filesMatched), Math.Max(0, Volatile.Read(ref _reserved)),
            Volatile.Read(ref _skippedBinary), Volatile.Read(ref _skippedTooLarge), Volatile.Read(ref _skippedTimedOut), Volatile.Read(ref _skippedUnreadable),
            _truncated, Stopwatch.GetElapsedTime(_startTicks), error ?? _error);
        var completion = new SearchCompletion(outcome, stats);
        _completed.Fire(completion);
        _result.TrySetResult(completion);
        if (Volatile.Read(ref _activeWorkers) == 0)
        {
            _workersStopped.TrySetResult(); // no worker was ever started (bad pattern, nothing to scan)
            _cts.Dispose();
        }
        // Drop subscribers so a finished session can never keep a view-model alive.
        _batches.Clear();
        _progress.Clear();
        _completed.Clear();
    }

}
