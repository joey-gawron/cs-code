using CsCode.Ui.Threading;

namespace CsCode.App.Tests.Infrastructure;

/// <summary>
/// Deterministic dispatcher for model-level tests: posts queue up and run only when the test calls <see cref="RunPending"/>.
/// The thread that constructs it counts as "the UI thread", so the debug thread asserts keep working.
/// </summary>
public sealed class ManualUiDispatcher : UiDispatcherBase
{
    private readonly Queue<(Action Action, UiPriority Priority)> _queue = new();
    private readonly int _uiThread = Environment.CurrentManagedThreadId;
    private readonly object _gate = new();

    public override bool CheckAccess() => Environment.CurrentManagedThreadId == _uiThread;

    protected override void PostCore(Action action, UiPriority priority)
    {
        lock (_gate) _queue.Enqueue((action, priority));
    }

    public int PendingCount { get { lock (_gate) return _queue.Count; } }

    /// <summary>Runs everything queued (including work queued while running) on the calling thread; returns how many callbacks ran.</summary>
    public int RunPending(int max = 1_000_000)
    {
        var n = 0;
        while (n < max)
        {
            (Action Action, UiPriority Priority) next;
            lock (_gate)
            {
                if (_queue.Count == 0) break;
                next = _queue.Dequeue();
            }
            next.Action();
            n++;
        }
        return n;
    }

    /// <summary>Runs one "turn": only the callbacks queued right now, not those they enqueue.</summary>
    public int RunOneTurn()
    {
        int count;
        lock (_gate) count = _queue.Count;
        for (var i = 0; i < count; i++)
        {
            Action a;
            lock (_gate) a = _queue.Dequeue().Action;
            a();
        }
        return count;
    }
}

public static class ManualUiDispatcherExtensions
{
    /// <summary>Pumps the dispatcher on the calling thread until <paramref name="task"/> completes, then returns its result (sync on purpose, see Rig).</summary>
    public static T Pump<T>(this ManualUiDispatcher ui, Task<T> task, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted && Environment.TickCount64 < deadline) { ui.RunPending(); Thread.Sleep(1); }
        ui.RunPending();
        return task.GetAwaiter().GetResult();
    }
}

/// <summary>Runs every post inline on the calling thread (for tests that do not care about dispatch timing).</summary>
public sealed class ImmediateUiDispatcher : UiDispatcherBase
{
    public override bool CheckAccess() => true;
    protected override void PostCore(Action action, UiPriority priority) => action();
}
