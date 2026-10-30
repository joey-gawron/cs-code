using System.Collections.Concurrent;

namespace CsCode.Sessions;

/// <summary>
/// A dedicated background thread that runs queued work items one at a time, in order (ARCHITECTURE section 6: session autosave and backup
/// writes have their own lane, so neither the UI thread nor the shared pool waits for the disk). Synchronous file I/O is fine here.
/// </summary>
public sealed class SerialLane : IDisposable
{
    private readonly BlockingCollection<(Action Work, TaskCompletionSource Done)> _queue = new(boundedCapacity: 256);
    private readonly Thread _thread;
    private int _disposed;

    public SerialLane(string name, ThreadPriority priority = ThreadPriority.BelowNormal)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name, Priority = priority };
        _thread.Start();
    }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    public Task RunAsync(Action work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { _queue.Add((work, done)); }
        catch (InvalidOperationException) { done.TrySetException(new ObjectDisposedException(nameof(SerialLane))); }
        return done.Task;
    }

    public async Task<T> RunAsync<T>(Func<T> work)
    {
        T result = default!;
        await RunAsync(() => { result = work(); }).ConfigureAwait(false);
        return result;
    }

    private void Run()
    {
        foreach (var (work, done) in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
        }
    }

    /// <summary>Runs what is queued, then stops (waits at most two seconds for the queue to drain).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.CompleteAdding();
        if (!IsCurrentThread) _thread.Join(TimeSpan.FromSeconds(2));
    }
}
