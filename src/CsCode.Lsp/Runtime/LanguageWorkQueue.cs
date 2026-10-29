using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CsCode.Lsp.Runtime;

/// <summary>
/// Priority classes of the Language lane. Strict priority: Interactive &gt; Background &gt; Idle.
/// Interactive = completion, hover, signature help, definition, rename, code actions, formatting, document sync.
/// Background = semantic tokens, inlay hints, code lens, folding, highlights, pull diagnostics.
/// Idle = workspace-symbol prefetch, colour, log/progress draining.
/// </summary>
public enum LanePriority { Interactive = 0, Background = 1, Idle = 2 }

/// <summary>Why a queued item never ran.</summary>
public enum DropReason { Superseded, Shed, Disposed }

/// <summary>"Latest wins" identity of a piece of work: (document, feature). Default = unkeyed.</summary>
public readonly record struct WorkKey(object? Owner, int Feature)
{
    public bool IsEmpty => Owner is null;
}

/// <summary>Per-item context handed to the work delegate.</summary>
public sealed class WorkContext
{
    internal WorkContext(LanguageWorkQueue queue, LanePriority priority, CancellationToken token)
    {
        Queue = queue;
        Priority = priority;
        Token = token;
    }

    internal LanguageWorkQueue Queue { get; }
    public LanePriority Priority { get; }
    /// <summary>Cancelled when superseded by a newer item with the same key, when the caller cancels, or when the queue is disposed.</summary>
    public CancellationToken Token { get; }

    /// <summary>Awaitable that continues on a lane thread (no-op when already on one). Use after awaiting network I/O so mapping stays on the lane.</summary>
    public LaneResume ResumeOnLane() => new(Queue, Priority);
}

/// <summary>Awaiter that re-enters the Language lane (never dropped: a posted continuation is always executed).</summary>
public readonly struct LaneResume(LanguageWorkQueue queue, LanePriority priority) : ICriticalNotifyCompletion
{
    public LaneResume GetAwaiter() => this;
    public bool IsCompleted => LanguageWorkQueue.IsOnLaneThread(queue);
    public void OnCompleted(Action continuation) => queue.PostContinuation(priority, continuation);
    public void UnsafeOnCompleted(Action continuation) => queue.PostContinuation(priority, continuation);
    public void GetResult() { }
}

/// <summary>
/// The dedicated Language lane: a few real threads (BelowNormal, not the shared pool) that execute all LSP client work —
/// request building, (de)serialisation, mapping, change coalescing — so a UI/typing thread only ever pays for an *enqueue*.
/// Bounded, strictly prioritised, latest-wins per key, sheds Idle then Background work under pressure.
/// A work delegate runs on a lane thread until its first real await; later phases re-enter via <see cref="WorkContext.ResumeOnLane"/>.
/// </summary>
public sealed class LanguageWorkQueue : IDisposable
{
    [ThreadStatic] private static LanguageWorkQueue? t_current;

    private abstract class Item
    {
        public LanePriority Priority;
        public WorkKey Key;
        public bool Exempt;           // continuations / internal control work: never shed
        public bool Dead;             // tombstone (superseded / shed)
        public long EnqueuedAt;       // Stopwatch ticks
        public CancellationTokenSource? Cts;
        public abstract void Execute(LanguageWorkQueue queue);
        public abstract void Drop(DropReason reason);
    }

    private sealed class ActionItem(Action action) : Item
    {
        public override void Execute(LanguageWorkQueue queue)
        {
            try { action(); }
            catch (Exception ex) { queue.OnUnhandled?.Invoke(ex); }
        }
        public override void Drop(DropReason reason) { }
    }

    private sealed class Job<T> : Item
    {
        private Func<WorkContext, ValueTask<T?>>? _work; // released as soon as it has been started (the closure may capture documents / big inputs)
        private readonly TaskCompletionSource<T?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationToken _callerToken;
        private CancellationTokenRegistration _callerReg;
        private int _completed;

        public Job(Func<WorkContext, ValueTask<T?>> work, CancellationToken callerToken)
        {
            _work = work;
            _callerToken = callerToken;
        }

        public Task<T?> Task => _tcs.Task;

        public void LinkCaller(CancellationTokenSource cts)
        {
            Cts = cts;
            if (_callerToken.CanBeCanceled)
                // Registration callback only *requests* cancellation lazily on a lane thread (see CancelAsync) — never inline on the caller's thread.
                _callerReg = _callerToken.Register(static s => ((Job<T>)s!).OnCallerCancelled(), this);
        }

        private void OnCallerCancelled()
        {
            // The caller asked to cancel: complete its task right away (cheap), and cancel the lane-side token off the caller's thread.
            if (Interlocked.Exchange(ref _completed, 1) == 0) _tcs.TrySetCanceled(_callerToken);
            Queue?.CancelOnLane(Cts);
        }

        internal LanguageWorkQueue? Queue;

        public override void Execute(LanguageWorkQueue queue)
        {
            if (Volatile.Read(ref _completed) != 0) { _callerReg.Dispose(); Queue?.Completed(this); Cts?.Dispose(); return; } // caller cancelled while queued
            ValueTask<T?> vt;
            var work = _work!;
            _work = null;
            try
            {
                vt = work(new WorkContext(queue, Priority, Cts?.Token ?? CancellationToken.None));
            }
            catch (OperationCanceledException) { Finish(default, canceled: true); return; }
            catch (Exception ex) { Fail(ex); return; }

            if (vt.IsCompletedSuccessfully) { Finish(vt.Result, false); return; }
            _ = Await(vt);
        }

        private async Task Await(ValueTask<T?> vt)
        {
            try { Finish(await vt.ConfigureAwait(false), false); }
            catch (OperationCanceledException) { Finish(default, canceled: true); }
            catch (Exception ex) { Fail(ex); }
        }

        private void Finish(T? result, bool canceled)
        {
            _callerReg.Dispose();
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                if (canceled && _callerToken.IsCancellationRequested) _tcs.TrySetCanceled(_callerToken);
                else _tcs.TrySetResult(canceled ? default : result); // superseded/cancelled-by-queue => "no result" (null), not an exception
            }
            Queue?.Completed(this);
            Cts?.Dispose();
        }

        private void Fail(Exception ex)
        {
            _callerReg.Dispose();
            if (Interlocked.Exchange(ref _completed, 1) == 0) _tcs.TrySetException(ex);
            Queue?.Completed(this);
            Cts?.Dispose();
        }

        public override void Drop(DropReason reason)
        {
            _work = null;
            _callerReg.Dispose();
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                if (_callerToken.IsCancellationRequested) _tcs.TrySetCanceled(_callerToken);
                else _tcs.TrySetResult(default);
            }
            Cts?.Dispose();
        }
    }

    private readonly object _gate = new();
    private readonly Queue<Item>[] _queues = [new(), new(), new()];
    private readonly int[] _live = new int[3];
    private readonly Dictionary<WorkKey, Item> _pending = new();
    private readonly Dictionary<WorkKey, Item> _running = new();
    private readonly Thread[] _threads;
    private readonly int _capacity;
    private bool _disposed;
    private long _superseded, _shed, _executed, _enqueued;
    private int _maxDepth;

    public LanguageWorkQueue(int threadCount = 2, int capacity = 4096, string name = "cscode-lsp-lane", ThreadPriority priority = ThreadPriority.BelowNormal, int linuxNice = 5)
    {
        _capacity = Math.Max(16, capacity);
        _threads = new Thread[Math.Clamp(threadCount, 1, 16)];
        for (var i = 0; i < _threads.Length; i++)
        {
            var t = new Thread(() => Loop(linuxNice), 2 * 1024 * 1024) { IsBackground = true, Name = $"{name}-{i}", Priority = priority };
            _threads[i] = t;
            t.Start();
        }
    }

    /// <summary>Raised when an <see cref="Post"/>ed action throws (never lets a bad item kill a lane thread).</summary>
    public Action<Exception>? OnUnhandled { get; set; }

    public int ThreadCount => _threads.Length;
    public int Capacity => _capacity;
    public int Depth { get { lock (_gate) return _live[0] + _live[1] + _live[2]; } }
    public int DepthOf(LanePriority p) { lock (_gate) return _live[(int)p]; }
    public int MaxObservedDepth => Volatile.Read(ref _maxDepth);
    public long SupersededCount => Interlocked.Read(ref _superseded);
    public long ShedCount => Interlocked.Read(ref _shed);
    public long ExecutedCount => Interlocked.Read(ref _executed);
    public long EnqueuedCount => Interlocked.Read(ref _enqueued);
    public bool IsDisposed { get { lock (_gate) return _disposed; } }
    public IReadOnlyList<Thread> Threads => _threads;

    public static bool IsOnLaneThread(LanguageWorkQueue queue) => ReferenceEquals(t_current, queue);

    /// <summary>True when optional work of this priority should be skipped right now (queue deep): the caller returns null instead of enqueueing.</summary>
    public bool ShouldShed(LanePriority priority)
    {
        lock (_gate)
        {
            var total = _live[0] + _live[1] + _live[2];
            return priority switch
            {
                LanePriority.Idle => total > _capacity / 8 || (_live[0] > 0 && total > 32),
                LanePriority.Background => total > _capacity / 2 || OldestWaitMsLocked(LanePriority.Interactive) > 500,
                _ => false,
            };
        }
    }

    public double OldestInteractiveWaitMs { get { lock (_gate) return OldestWaitMsLocked(LanePriority.Interactive); } }

    private double OldestWaitMsLocked(LanePriority p)
    {
        var q = _queues[(int)p];
        foreach (var it in q)
        {
            if (it.Dead) continue;
            return Stopwatch.GetElapsedTime(it.EnqueuedAt).TotalMilliseconds;
        }
        return 0;
    }

    /// <summary>Fire-and-forget synchronous work. Returns false if shed. Cheap enough to call from any thread (an enqueue, no work done).</summary>
    public bool Post(LanePriority priority, Action work, WorkKey key = default, bool exempt = false)
    {
        var item = new ActionItem(work) { Priority = priority, Key = key, Exempt = exempt };
        return Enqueue(item);
    }

    internal void PostContinuation(LanePriority priority, Action continuation)
    {
        var item = new ActionItem(continuation) { Priority = priority, Exempt = true };
        if (!Enqueue(item))
            ThreadPool.UnsafeQueueUserWorkItem(static c => c(), continuation, preferLocal: false); // disposed: never strand an awaiter
    }

    internal void CancelOnLane(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        var item = new ActionItem(() => { try { cts.Cancel(); } catch (ObjectDisposedException) { } }) { Priority = LanePriority.Interactive, Exempt = true };
        if (!Enqueue(item)) ThreadPool.UnsafeQueueUserWorkItem(static c => { try { c.Cancel(); } catch (ObjectDisposedException) { } }, cts, preferLocal: false);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the lane and returns its result. Synchronous cost on the calling thread: allocate + enqueue.
    /// With a <paramref name="key"/>: a newer item for the same key supersedes (cancels) an older one — before it starts if still queued,
    /// via its token if running. Superseded / shed / disposed items complete with <c>default</c> (null).
    /// A cancelled <paramref name="ct"/> completes the task as cancelled.
    /// </summary>
    public ValueTask<T?> RunAsync<T>(LanePriority priority, WorkKey key, Func<WorkContext, ValueTask<T?>> work, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return ValueTask.FromCanceled<T?>(ct);
        var job = new Job<T>(work, ct) { Priority = priority, Key = key, Queue = this };
        job.LinkCaller(new CancellationTokenSource());
        if (!Enqueue(job)) job.Drop(DropReason.Shed);
        return new ValueTask<T?>(job.Task);
    }

    private void Completed(Item item)
    {
        if (item.Key.IsEmpty) return;
        lock (_gate)
            if (_running.TryGetValue(item.Key, out var cur) && ReferenceEquals(cur, item)) _running.Remove(item.Key);
    }

    private bool Enqueue(Item item)
    {
        List<Item>? dropped = null;
        CancellationTokenSource? cancelRunning = null;
        var accepted = true;
        lock (_gate)
        {
            if (_disposed) return false;
            item.EnqueuedAt = Stopwatch.GetTimestamp();

            if (!item.Key.IsEmpty)
            {
                if (_pending.TryGetValue(item.Key, out var old))
                {
                    Kill(old, ref dropped, DropReason.Superseded);
                    Interlocked.Increment(ref _superseded);
                }
                if (_running.TryGetValue(item.Key, out var running) && running.Cts is { } rc)
                {
                    cancelRunning = rc; // cancel the in-flight one ($/cancelRequest) — done on a lane thread, below
                    Interlocked.Increment(ref _superseded);
                }
            }

            if (!item.Exempt && TotalLive() >= _capacity)
            {
                // Make room: shed Idle first, then Background, then the oldest Interactive; or refuse the newcomer if it is the least valuable.
                if (!MakeRoom(item.Priority, ref dropped)) { accepted = false; Interlocked.Increment(ref _shed); }
            }

            if (accepted)
            {
                _queues[(int)item.Priority].Enqueue(item);
                _live[(int)item.Priority]++;
                if (!item.Key.IsEmpty) _pending[item.Key] = item;
                Interlocked.Increment(ref _enqueued);
                var depth = TotalLive();
                if (depth > _maxDepth) _maxDepth = depth;
                CompactIfNeeded((int)item.Priority);
                Monitor.Pulse(_gate);
            }
        }

        if (dropped is not null) foreach (var d in dropped) d.Drop(DropReason.Superseded);
        if (cancelRunning is not null) CancelOnLane(cancelRunning);
        if (!accepted) item.Drop(DropReason.Shed);
        return accepted || item.Exempt;
    }

    private int TotalLive() => _live[0] + _live[1] + _live[2];

    private void Kill(Item victim, ref List<Item>? dropped, DropReason reason)
    {
        if (victim.Dead) return;
        victim.Dead = true;
        _live[(int)victim.Priority]--;
        if (!victim.Key.IsEmpty && _pending.TryGetValue(victim.Key, out var cur) && ReferenceEquals(cur, victim)) _pending.Remove(victim.Key);
        (dropped ??= new()).Add(victim);
    }

    private bool MakeRoom(LanePriority incoming, ref List<Item>? dropped)
    {
        // Victim order: lowest-value class first, oldest first. Exempt items are never victims.
        for (var p = 2; p >= (int)incoming; p--)
        {
            if (p == (int)incoming && incoming != LanePriority.Interactive) break; // never evict peers of Background/Idle for a same-class newcomer
            foreach (var it in _queues[p])
            {
                if (it.Dead || it.Exempt) continue;
                Kill(it, ref dropped, DropReason.Shed);
                Interlocked.Increment(ref _shed);
                return true;
            }
        }
        return false;
    }

    private void CompactIfNeeded(int p)
    {
        var q = _queues[p];
        if (q.Count <= 2 * _live[p] + 64) return;
        var keep = new Queue<Item>(_live[p] + 8);
        foreach (var it in q) if (!it.Dead) keep.Enqueue(it);
        _queues[p] = keep;
    }

    private Item? Dequeue()
    {
        for (var p = 0; p < 3; p++)
        {
            var q = _queues[p];
            while (q.Count > 0)
            {
                var it = q.Dequeue();
                if (it.Dead) continue;
                _live[p]--;
                if (!it.Key.IsEmpty)
                {
                    if (_pending.TryGetValue(it.Key, out var cur) && ReferenceEquals(cur, it)) _pending.Remove(it.Key);
                    if (it is not ActionItem) _running[it.Key] = it;
                }
                return it;
            }
        }
        return null;
    }

    private void Loop(int linuxNice)
    {
        t_current = this;
        NativePriority.TryLowerCurrentThread(linuxNice);
        while (WaitAndRunOne()) { }
    }

    // One item per call, so no item (and no result it holds — a 50k-item completion list, a million semantic tokens) stays reachable from
    // this thread's stack while it idles in Monitor.Wait.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool WaitAndRunOne()
    {
        Item? item;
        lock (_gate)
        {
            while ((item = Dequeue()) is null)
            {
                if (_disposed) return false;
                Monitor.Wait(_gate);
            }
        }
        Interlocked.Increment(ref _executed);
        try { item.Execute(this); }
        catch (Exception ex) { OnUnhandled?.Invoke(ex); }
        return true;
    }

    public void Dispose()
    {
        List<Item> pending = new();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var q in _queues)
                foreach (var it in q)
                    if (!it.Dead) { it.Dead = true; pending.Add(it); }
            foreach (var q in _queues) q.Clear();
            _live[0] = _live[1] = _live[2] = 0;
            _pending.Clear();
            Monitor.PulseAll(_gate);
        }
        // Remaining continuations only complete awaiters: run them on the pool so no awaiter is stranded; drop real work as Disposed.
        foreach (var it in pending)
        {
            if (it is ActionItem a && it.Exempt)
                ThreadPool.UnsafeQueueUserWorkItem(static s => { try { s.Execute(null!); } catch (Exception) { /* best effort during shutdown */ } }, a, preferLocal: false);
            else it.Drop(DropReason.Disposed);
        }
        foreach (var t in _threads)
            if (t != Thread.CurrentThread) t.Join(TimeSpan.FromSeconds(2));
    }
}
