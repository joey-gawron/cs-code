using CsCode.Editor.Threading;

namespace CsCode.Editor.Tests.Support;

/// <summary>
/// Headless "UI thread" for plain (non-Avalonia) tests: the thread that constructs it IS the owner. <see cref="Post"/> queues onto
/// a pump the owner drains with <see cref="Pump"/>; <see cref="Invoke"/> from another thread blocks until the owner pumps.
/// </summary>
public sealed class ThreadAffinityUiThread : IUiThread
{
    private readonly Queue<(Action Action, TaskCompletionSource? Done)> _queue = new();

    public ThreadAffinityUiThread() => OwnerThread = Thread.CurrentThread;

    public Thread OwnerThread { get; }

    public bool CheckAccess() => Thread.CurrentThread == OwnerThread;

    public void Post(Action action)
    {
        lock (_queue) _queue.Enqueue((action, null));
    }

    public Task InvokeAsync(Action action)
    {
        if (CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_queue) _queue.Enqueue((action, tcs));
        return tcs.Task;
    }

    public void Invoke(Action action)
    {
        if (CheckAccess())
        {
            action();
            return;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_queue) _queue.Enqueue((action, tcs));
        tcs.Task.Wait(); // documented purpose of Invoke(): block a NON-owner thread until the owner pumps
    }

    /// <summary>Runs everything queued so far (owner thread only). Returns the number of items run.</summary>
    public int Pump()
    {
        var n = 0;
        while (true)
        {
            (Action Action, TaskCompletionSource? Done) item;
            lock (_queue)
            {
                if (_queue.Count == 0) return n;
                item = _queue.Dequeue();
            }
            try
            {
                item.Action();
                item.Done?.TrySetResult();
            }
            catch (Exception ex)
            {
                if (item.Done is null) throw;
                item.Done.TrySetException(ex);
            }
            n++;
        }
    }
}
