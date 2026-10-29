using System.Collections.Concurrent;

namespace CsCode.Editor.Threading;

/// <summary>
/// A <see cref="TaskScheduler"/> backed by dedicated (non-pool) threads. The editor's I/O lane uses one so that file work never
/// competes with the shared thread pool (which a CPU-hungry language-server client may saturate) and never runs on the UI thread.
/// </summary>
public sealed class DedicatedLane : TaskScheduler, IDisposable
{
    private readonly BlockingCollection<Task> _queue = new();
    private readonly Thread[] _threads;

    public DedicatedLane(string name, int threads = 1, ThreadPriority priority = ThreadPriority.Normal)
    {
        _threads = new Thread[threads];
        for (var i = 0; i < threads; i++)
        {
            _threads[i] = new Thread(Loop) { IsBackground = true, Name = threads == 1 ? name : $"{name}-{i}", Priority = priority };
            _threads[i].Start();
        }
    }

    public override int MaximumConcurrencyLevel => _threads.Length;

    /// <summary>Runs <paramref name="work"/> on the lane's thread.</summary>
    public Task Run(Action work, CancellationToken ct = default) =>
        Task.Factory.StartNew(work, ct, TaskCreationOptions.DenyChildAttach, this);

    public Task<T> Run<T>(Func<T> work, CancellationToken ct = default) =>
        Task.Factory.StartNew(work, ct, TaskCreationOptions.DenyChildAttach, this);

    protected override void QueueTask(Task task)
    {
        try
        {
            _queue.Add(task);
        }
        catch (InvalidOperationException)
        {
            // lane disposed: run nothing; the task is abandoned like the process is shutting down
        }
    }

    // Never inline: callers may be the UI thread, which must not execute lane work.
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();

    private void Loop()
    {
        try
        {
            // (not foreach over GetConsumingEnumerable: its enumerator keeps the LAST task — and so its result, e.g. a freshly loaded document —
            //  referenced while the thread waits for the next one)
            while (_queue.TryTake(out var task, Timeout.Infinite))
            {
                ExecuteOne(task);
                task = null;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ExecuteOne(Task task) => TryExecuteTask(task);

    public void Dispose() => _queue.CompleteAdding();
}
