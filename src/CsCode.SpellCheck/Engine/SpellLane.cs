using System.Collections.Concurrent;

namespace CsCode.SpellCheck.Engine;

/// <summary>
/// The spell checker's own lane: one dedicated below-normal thread (never the UI thread, never the shared pool a language-server client may
/// saturate) that loads dictionaries and checks documents in slices. Work is posted, never run inline, so a caller on the UI thread pays only
/// for enqueueing.
/// </summary>
public sealed class SpellLane : TaskScheduler, IDisposable
{
    public const string ThreadName = "cscode-spell";

    private readonly BlockingCollection<Task> _queue = new();
    private readonly Thread _thread;

    public SpellLane()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = ThreadName, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public override int MaximumConcurrencyLevel => 1;

    /// <summary>True on the lane's thread.</summary>
    public bool IsCurrent => Thread.CurrentThread == _thread;

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
            // disposed: the work is abandoned, as at shutdown
        }
    }

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();

    private void Loop()
    {
        try
        {
            // not foreach over GetConsumingEnumerable: its enumerator would keep the last task (and what it captured) alive while idle
            while (_queue.TryTake(out var task, Timeout.Infinite))
            {
                TryExecuteTask(task);
                task = null;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
