using System.Diagnostics;

namespace CsCode.Editor.Threading;

public enum WorkPriority
{
    /// <summary>Something the user is waiting for right now (re-tokenise the line being typed, filter the popup).</summary>
    Interactive = 0,
    /// <summary>Derived data for what is on screen.</summary>
    Visible = 1,
    /// <summary>Derived data for what is off screen (read-ahead).</summary>
    Background = 2,
    /// <summary>Housekeeping that may be postponed indefinitely.</summary>
    Idle = 3,
}

/// <summary>
/// The editor worker lane: ONE dedicated (non-pool) thread that runs all *derived* per-document work — tokenisation, folding,
/// bracket maps, completion filtering/sorting, snippet parsing, whole-document computations — from immutable snapshots, so none of
/// it can occupy the UI thread and a CPU-hungry language server cannot starve it through the shared thread pool.
///
/// Scheduling: strict priority (Interactive &gt; Visible &gt; Background &gt; Idle), FIFO within a priority. Jobs are cooperative: a job
/// that has more to do posts its own continuation, so a high-priority job never waits longer than one slice of a lower one.
/// Jobs posted with a <c>key</c> are LATEST-WINS: a queued job with the same key is replaced, and a running one has its
/// cancellation token cancelled — typing supersedes the previous request instead of queueing behind it.
/// </summary>
public sealed class EditorWorker : IDisposable
{
    private sealed class Job
    {
        public required WorkPriority Priority { get; init; }
        public required object? Key { get; init; }
        public required Action<CancellationToken> Run { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public bool Replaced { get; set; }
    }

    private const int MaxQueued = 20_000;

    private readonly object _gate = new();
    private readonly LinkedList<Job>[] _queues = [new(), new(), new(), new()];
    private readonly Dictionary<object, (Job Job, LinkedListNode<Job>? Node)> _keyed = new();
    private readonly Thread _thread;
    private bool _disposed;
    private int _queued;
    private long _completed;
    private long _maxJobTicks;
    private long _longJobs;

    /// <summary>Called when a job throws (the worker must survive a bad job). Default: ignored.</summary>
    public Action<Exception>? OnError { get; set; }

    public EditorWorker(string name = "cscode-editor-worker", ThreadPriority priority = ThreadPriority.Normal)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name, Priority = priority };
        _thread.Start();
    }

    public Thread Thread => _thread;
    public bool IsWorkerThread => Thread.CurrentThread == _thread;

    /// <summary>Jobs waiting (not counting the running one).</summary>
    public int QueueLength { get { lock (_gate) return _queued; } }

    public long CompletedJobs => Interlocked.Read(ref _completed);

    /// <summary>Longest single job so far (slice length: the worst delay an interactive job can see).</summary>
    public TimeSpan LongestJob => TimeSpan.FromTicks(Interlocked.Read(ref _maxJobTicks));

    /// <summary>Number of jobs that ran longer than 10 ms (cooperative slicing should keep this near zero).</summary>
    public long JobsOver10Ms => Interlocked.Read(ref _longJobs);

    /// <summary>Enqueues <paramref name="job"/>; costs the caller one lock and one allocation (it never runs inline).</summary>
    public void Post(WorkPriority priority, object? key, Action<CancellationToken> job)
    {
        var cts = new CancellationTokenSource();
        var j = new Job { Priority = priority, Key = key, Run = job, Cts = cts };
        lock (_gate)
        {
            if (_disposed)
            {
                cts.Dispose();
                return;
            }
            if (key is not null && _keyed.TryGetValue(key, out var existing))
            {
                existing.Job.Replaced = true;
                existing.Job.Cts.Cancel(); // supersede: queued jobs are dropped below, a running one sees its token cancelled
                if (existing.Node is { List: not null } node)
                {
                    node.List.Remove(node);
                    _queued--;
                    existing.Job.Cts.Dispose();
                }
            }
            if (_queued >= MaxQueued) DropOldestIdle();
            var n = _queues[(int)priority].AddLast(j);
            _queued++;
            if (key is not null) _keyed[key] = (j, n);
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>Runs <paramref name="func"/> on the worker and returns its result; the awaiting continuation never runs on the worker.</summary>
    public Task<T> RunAsync<T>(WorkPriority priority, Func<CancellationToken, T> func, CancellationToken ct = default, object? key = null)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.CanBeCanceled ? ct.Register(() => tcs.TrySetCanceled(ct)) : default;
        Post(priority, key, token =>
        {
            try
            {
                if (ct.IsCancellationRequested || token.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct.IsCancellationRequested ? ct : token);
                    return;
                }
                tcs.TrySetResult(func(CancellationTokenSource.CreateLinkedTokenSource(ct, token).Token));
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                registration.Dispose();
            }
        });
        return tcs.Task;
    }

    private void DropOldestIdle()
    {
        // Backstop only: a bounded worker. Idle then Background work is the first to go; keyed jobs are never dropped (they self-replace).
        for (var p = 3; p >= 2; p--)
        {
            var node = _queues[p].First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.Key is null)
                {
                    _queues[p].Remove(node);
                    node.Value.Cts.Dispose();
                    _queued--;
                    return;
                }
                node = next;
            }
        }
    }

    private void Loop()
    {
        while (true)
        {
            Job? job;
            lock (_gate)
            {
                while (true)
                {
                    if (_disposed) return;
                    job = PopNext();
                    if (job is not null) break;
                    Monitor.Wait(_gate);
                }
            }

            Execute(job);
            // Drop the reference BEFORE waiting for the next job: a stack slot still pointing at the last job (its closure, the view it captured)
            // would keep that view alive for as long as the worker idles.
            job = null;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void Execute(Job job)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            job.Run(job.Cts.Token); // a job superseded while queued was removed from the queue, so anything popped is live
        }
        catch (OperationCanceledException)
        {
            // a superseded job stopped itself: expected
        }
        catch (Exception ex)
        {
            try
            {
                OnError?.Invoke(ex);
            }
            catch (Exception)
            {
                // the error handler must not kill the lane
            }
        }
        var elapsed = Stopwatch.GetElapsedTime(start);
        Interlocked.Increment(ref _completed);
        if (elapsed > TimeSpan.FromMilliseconds(10)) Interlocked.Increment(ref _longJobs);
        long ticks = elapsed.Ticks, seen;
        while (ticks > (seen = Interlocked.Read(ref _maxJobTicks)) && Interlocked.CompareExchange(ref _maxJobTicks, ticks, seen) != seen) { }

        lock (_gate)
        {
            if (job.Key is not null && _keyed.TryGetValue(job.Key, out var cur) && ReferenceEquals(cur.Job, job)) _keyed.Remove(job.Key);
        }
        job.Cts.Dispose();
    }

    private Job? PopNext()
    {
        foreach (var q in _queues)
        {
            var node = q.First;
            if (node is null) continue;
            q.RemoveFirst();
            _queued--;
            // keep the key registered while it runs so a re-post can cancel it, but detach the queue node
            if (node.Value.Key is { } k && _keyed.TryGetValue(k, out var e) && ReferenceEquals(e.Job, node.Value)) _keyed[k] = (e.Job, null);
            return node.Value;
        }
        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var q in _queues)
                foreach (var j in q)
                    j.Cts.Cancel();
            Monitor.PulseAll(_gate);
        }
    }
}
