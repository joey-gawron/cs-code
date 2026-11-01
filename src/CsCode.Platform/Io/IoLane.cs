using System.Collections.Concurrent;

namespace CsCode.Platform.Io;

/// <summary>
/// The dedicated IO lane (ARCHITECTURE §6): a small set of OWN threads that perform every blocking file-system call, directory
/// walk and watcher flush, so that (a) callers on the UI thread pay only the cost of enqueueing and (b) a slow disk or a huge
/// directory walk can never starve the shared thread pool that the rest of the process (language servers' pipes, timers) uses.
///
/// Work items are short, synchronous, leaf operations that check their <see cref="CancellationToken"/> between chunks. A work item
/// must never wait for another lane item (that could deadlock a saturated lane); long jobs (directory walks) are therefore sliced
/// into small items that re-enqueue themselves.
/// The queue is bounded: when it is full the caller's task simply completes later (back-pressure), the caller is never blocked.
/// </summary>
public sealed class IoLane : IDisposable
{
    [ThreadStatic] private static bool t_isLaneThread;

    private readonly string _name;
    private readonly int _maxThreads;
    private readonly ThreadPriority _priority;
    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly SemaphoreSlim _items = new(0);
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Thread> _threads = new();
    private readonly object _threadGate = new();
    private int _idle;
    private bool _disposed;

    public IoLane(string name = "CsCode.IO", int maxThreads = 4, int capacity = 8192, ThreadPriority priority = ThreadPriority.Normal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxThreads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _name = name;
        _maxThreads = maxThreads;
        _priority = priority;
        _slots = new SemaphoreSlim(capacity, capacity);
    }

    /// <summary>True when the current thread is one of the lane threads (used by tests and by sync-over-async guards).</summary>
    public static bool IsLaneThread => t_isLaneThread;

    public int ThreadCount { get { lock (_threadGate) return _threads.Count; } }

    /// <summary>Number of queued, not yet started items (diagnostics/tests).</summary>
    public int Pending => _queue.Count;

    /// <summary>
    /// Runs <paramref name="work"/> on a lane thread. Returns immediately (never blocks the caller); the task's continuations are
    /// scheduled asynchronously so a caller continuation can never run on (and hog) a lane thread.
    /// </summary>
    public Task<T> RunAsync<T>(Func<CancellationToken, T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var item = new WorkItem<T>(work, ct);
        Enqueue(item);
        return item.Task;
    }

    public Task RunAsync(Action<CancellationToken> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync<bool>(t => { work(t); return true; }, ct);
    }

    /// <summary>Fire-and-forget. Exceptions are passed to <paramref name="onError"/> (or swallowed): a bad item must not kill a lane thread.</summary>
    public void Post(Action work, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_disposed) return;
        Enqueue(new PostItem(work, onError));
    }

    private void Enqueue(WorkItem item)
    {
        if (_slots.Wait(0))
        {
            _queue.Enqueue(item);
            _items.Release();
            EnsureThread();
            return;
        }
        _ = EnqueueSlowAsync(item); // queue full: back-pressure by deferring, never by blocking the caller
    }

    private async Task EnqueueSlowAsync(WorkItem item)
    {
        try
        {
            // A caller-side cancellation completes the item's task through its own registration; here only shutdown matters.
            await _slots.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            item.Cancel();
            return;
        }
        _queue.Enqueue(item);
        _items.Release();
        EnsureThread();
    }

    private void EnsureThread()
    {
        // Idle threads that are about to pick up already-queued items do not count as spare capacity: compare queued items to idle threads,
        // otherwise two quick enqueues with one idle thread would leave the second item waiting behind a possibly long-running first one.
        if (_queue.Count <= Volatile.Read(ref _idle)) return;
        lock (_threadGate)
        {
            if (_disposed || _threads.Count >= _maxThreads || _queue.Count <= Volatile.Read(ref _idle)) return;
            var t = new Thread(Loop) { IsBackground = true, Name = $"{_name}-{_threads.Count + 1}", Priority = _priority };
            _threads.Add(t);
            t.Start();
        }
    }

    private void Loop()
    {
        t_isLaneThread = true;
        while (true)
        {
            Interlocked.Increment(ref _idle);
            try { _items.Wait(_shutdown.Token); }
            // ObjectDisposed: Dispose gave up waiting for a busy thread and released the token source; this thread must just leave, not crash the process.
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { Interlocked.Decrement(ref _idle); break; }
            Interlocked.Decrement(ref _idle);

            if (!_queue.TryDequeue(out var item)) continue;
            _slots.Release();
            try { item.Execute(); }
            catch { /* WorkItem implementations capture their own failures; this keeps the lane thread alive no matter what */ }
        }
        // Shutdown: fail whatever is still queued so no awaiter hangs forever.
        while (_queue.TryDequeue(out var rest)) rest.Cancel();
    }

    public void Dispose()
    {
        Thread[] threads;
        lock (_threadGate)
        {
            if (_disposed) return;
            _disposed = true;
            threads = _threads.ToArray();
        }
        _shutdown.Cancel();
        foreach (var t in threads)
            if (t != Thread.CurrentThread) t.Join(TimeSpan.FromSeconds(2));
        while (_queue.TryDequeue(out var rest)) rest.Cancel();
        _shutdown.Dispose();
    }

    private abstract class WorkItem
    {
        public abstract CancellationToken Token { get; }
        public abstract void Execute();
        public abstract void Cancel();
    }

    // An item lets go of its delegate once it has run (or was cancelled): a lane thread may keep the last item it ran in a stack slot until it
    // runs the next one (Debug builds keep every local of the endless loop alive), and that must not keep the caller's closure — a disposed
    // subscription's callback, a document — reachable.
    private sealed class PostItem(Action work, Action<Exception>? onError) : WorkItem
    {
        private Action? _work = work;
        private Action<Exception>? _onError = onError;

        public override CancellationToken Token => CancellationToken.None;
        public override void Cancel() { _work = null; _onError = null; }
        public override void Execute()
        {
            var w = _work;
            var e = _onError;
            _work = null;
            _onError = null;
            if (w is null) return;
            try { w(); }
            catch (Exception ex) { try { e?.Invoke(ex); } catch { /* swallow */ } }
        }
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<CancellationToken, T>? _work;
        private readonly CancellationToken _ct;
        private CancellationTokenRegistration _reg;
        private int _state; // Queued, Running or CancelledWhileQueued: whichever of the token and the lane thread gets there first decides
        private const int Queued = 0, Running = 1, CancelledWhileQueued = 2;

        public WorkItem(Func<CancellationToken, T> work, CancellationToken ct)
        {
            _work = work;
            _ct = ct;
            // Cancelling while still queued completes the caller's task immediately, even when the lane is jammed. Once the work runs, its task
            // completes when it returns: a cancelled write has removed its temp file by then (and one that committed anyway reports success).
            if (ct.CanBeCanceled)
                _reg = ct.Register(static s =>
                {
                    var item = (WorkItem<T>)s!;
                    if (Interlocked.CompareExchange(ref item._state, CancelledWhileQueued, Queued) == Queued) item._tcs.TrySetCanceled(item._ct);
                }, this);
        }

        public Task<T> Task => _tcs.Task;
        public override CancellationToken Token => _ct;

        public override void Cancel()
        {
            _work = null;
            _tcs.TrySetCanceled(_ct.IsCancellationRequested ? _ct : new CancellationToken(true));
        }

        public override void Execute()
        {
            var work = _work;
            _work = null;
            try
            {
                if (Interlocked.CompareExchange(ref _state, Running, Queued) != Queued || _tcs.Task.IsCompleted || work is null) return; // cancelled while queued
                _ct.ThrowIfCancellationRequested();
                _tcs.TrySetResult(work(_ct));
            }
            catch (OperationCanceledException oce)
            {
                _tcs.TrySetCanceled(oce.CancellationToken.IsCancellationRequested ? oce.CancellationToken : _ct);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
            finally
            {
                _reg.Dispose();
            }
        }
    }
}
