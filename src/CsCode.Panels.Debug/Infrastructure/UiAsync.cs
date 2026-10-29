using System.Runtime.CompilerServices;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Debug.Infrastructure;

/// <summary>
/// View-models mutate their state only on the UI thread but must not depend on a captured <c>SynchronizationContext</c> (tests drive them
/// from a plain thread, and the Debug-lane thread that completes a service call must never run view code). So every service call is
/// awaited through <see cref="OnUi{T}(IUiDispatcher, ValueTask{T}, CancellationToken)"/>: it completes the call off-thread without
/// capturing a context, then hops back through the <see cref="IUiDispatcher"/>. In production the hop is the dispatcher post the
/// SynchronizationContext would have made anyway.
/// </summary>
public static class UiAsync
{
    /// <summary>An awaitable that continues on the UI thread (synchronously when already there).</summary>
    public static UiSwitch SwitchToUi(this IUiDispatcher ui) => new(ui);

    public readonly struct UiSwitch(IUiDispatcher ui) : INotifyCompletion
    {
        public UiSwitch GetAwaiter() => this;
        public bool IsCompleted => ui.CheckAccess();
        public void OnCompleted(Action continuation) => ui.Post(continuation);
        public void GetResult() { }
    }

    /// <summary>
    /// Awaits a service call and resumes on the UI thread. A cancelled <paramref name="ct"/> unblocks the await even when the callee ignores
    /// the token (a hung adapter): the abandoned call keeps running on its own lane but nothing in the view-model waits for it any more,
    /// so a closed panel cannot be kept alive by a request that never answers.
    /// </summary>
    public static async Task<T> OnUi<T>(this IUiDispatcher ui, ValueTask<T> call, CancellationToken ct = default)
    {
        var task = call.AsTask();
        try
        {
            if (!task.IsCompleted && ct.CanBeCanceled)
            {
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = ct.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), cancelled);
                if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                {
                    Observe(task);
                    await ui.SwitchToUi();
                    throw new OperationCanceledException(ct);
                }
            }
            var result = await task.ConfigureAwait(false);
            await ui.SwitchToUi();
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await ui.SwitchToUi();
            throw;
        }
    }

    public static async Task OnUi(this IUiDispatcher ui, ValueTask call, CancellationToken ct = default)
    {
        await ui.OnUi(Wrap(call), ct);

        static async ValueTask<bool> Wrap(ValueTask c)
        {
            await c.ConfigureAwait(false);
            return true;
        }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
