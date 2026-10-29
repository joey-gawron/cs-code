using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Threading;

namespace CsCode.Ui.Threading;

public enum UiPriority { Normal, Input, Background, Idle }

/// <summary>Counters exposed to the status bar diagnostics and the Profiling module (<c>ui.stalls</c>, <c>ui.longestDispatchMs</c>).</summary>
public sealed class UiDispatchStats
{
    private long _posted, _executed, _stalls, _longestTicks, _coalescedDropped;

    public long Posted => Interlocked.Read(ref _posted);
    public long Executed => Interlocked.Read(ref _executed);
    /// <summary><c>ui.stalls</c>: how many times the UI thread failed to pump for longer than the watchdog threshold.</summary>
    public long Stalls => Interlocked.Read(ref _stalls);
    /// <summary><c>ui.longestDispatchMs</c>: longest single dispatcher callback seen, in milliseconds.</summary>
    public double LongestDispatchMs => Interlocked.Read(ref _longestTicks) * 1000.0 / Stopwatch.Frequency;
    /// <summary>Posts that were superseded by a newer post with the same coalescing key.</summary>
    public long CoalescedDropped => Interlocked.Read(ref _coalescedDropped);

    internal void OnPosted() => Interlocked.Increment(ref _posted);
    internal void OnExecuted(long ticks)
    {
        Interlocked.Increment(ref _executed);
        long seen;
        while (ticks > (seen = Interlocked.Read(ref _longestTicks)) && Interlocked.CompareExchange(ref _longestTicks, ticks, seen) != seen) { }
    }
    internal void OnStall() => Interlocked.Increment(ref _stalls);
    internal void OnCoalesced() => Interlocked.Increment(ref _coalescedDropped);
    public void ResetLongest() => Interlocked.Exchange(ref _longestTicks, 0);
}

/// <summary>
/// The only sanctioned way for services to reach the UI thread. Every shell service accepts calls from any thread and
/// marshals through this; streaming producers use <see cref="PostCoalesced"/> / <see cref="PostBatched{T}"/> instead of one post per item.
/// </summary>
public interface IUiDispatcher
{
    bool CheckAccess();
    void Post(Action action, UiPriority priority = UiPriority.Normal);
    Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal);
    Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal);
    /// <summary>Latest-wins per key: if a post with the same key has not run yet, only the newest action runs (once).</summary>
    void PostCoalesced(string key, Action action, UiPriority priority = UiPriority.Normal);
    /// <summary>
    /// Applies <paramref name="items"/> in slices of at most <paramref name="maxSliceMs"/> milliseconds (at least one item per slice),
    /// yielding to the dispatcher between slices so a 100k-item update never freezes input or rendering.
    /// The returned task completes after the last item.
    /// </summary>
    Task PostBatched<T>(IReadOnlyList<T> items, Action<T> apply, double maxSliceMs = 4, UiPriority priority = UiPriority.Background);
    UiDispatchStats Stats { get; }
    /// <summary>Label of the callback currently executing (or last started); the watchdog reports it on a stall.</summary>
    string? CurrentCallback { get; }
}

public static class UiThreadAssert
{
    /// <summary>Debug-only: throws if called off the UI thread.</summary>
    [Conditional("DEBUG")]
    public static void AssertUiThread(this IUiDispatcher d)
    {
        if (!d.CheckAccess()) throw new InvalidOperationException("Must be called on the UI thread.");
    }

    /// <summary>Debug-only: throws if called on the UI thread (for code that does blocking-ish work and must be on a worker).</summary>
    [Conditional("DEBUG")]
    public static void AssertNotUiThread(this IUiDispatcher d)
    {
        if (d.CheckAccess()) throw new InvalidOperationException("Must not be called on the UI thread.");
    }
}

/// <summary>Shared implementation of coalescing, batching, timing and stats over an abstract post primitive.</summary>
public abstract class UiDispatcherBase : IUiDispatcher
{
    private sealed class Slot
    {
        public Action? Latest;
        public int Pending;
    }

    private readonly ConcurrentDictionary<string, Slot> _slots = new();
    private Action? _current;

    public UiDispatchStats Stats { get; } = new();

    public string? CurrentCallback => Volatile.Read(ref _current) is { } a ? Describe(a) : null;

    public abstract bool CheckAccess();
    protected abstract void PostCore(Action action, UiPriority priority);

    public void Post(Action action, UiPriority priority = UiPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(action);
        Stats.OnPosted();
        PostCore(() => Run(action), priority);
    }

    public Task InvokeAsync(Action action, UiPriority priority = UiPriority.Normal)
    {
        if (CheckAccess())
        {
            Run(action);
            return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, priority);
        return tcs.Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> func, UiPriority priority = UiPriority.Normal)
    {
        if (CheckAccess())
        {
            try { return Task.FromResult(func()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, priority);
        return tcs.Task;
    }

    public void PostCoalesced(string key, Action action, UiPriority priority = UiPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(action);
        var slot = _slots.GetOrAdd(key, static _ => new Slot());
        if (Interlocked.Exchange(ref slot.Latest, action) is not null) Stats.OnCoalesced();
        if (Interlocked.Exchange(ref slot.Pending, 1) != 0) return;
        Post(() =>
        {
            // Order matters: clear Pending first so a post racing with this run schedules a fresh runner.
            Volatile.Write(ref slot.Pending, 0);
            var latest = Interlocked.Exchange(ref slot.Latest, null);
            latest?.Invoke();
            if (Volatile.Read(ref slot.Latest) is null && Volatile.Read(ref slot.Pending) == 0)
                _slots.TryRemove(new KeyValuePair<string, Slot>(key, slot)); // keep the key table bounded
        }, priority);
    }

    public Task PostBatched<T>(IReadOnlyList<T> items, Action<T> apply, double maxSliceMs = 4, UiPriority priority = UiPriority.Background)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(apply);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var index = 0;
        var budgetTicks = (long)(maxSliceMs * Stopwatch.Frequency / 1000.0);

        void Step()
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                while (index < items.Count)
                {
                    apply(items[index++]);
                    if (index < items.Count && Stopwatch.GetTimestamp() - start >= budgetTicks)
                    {
                        Post(Step, priority);
                        return;
                    }
                }
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        Post(Step, priority);
        return tcs.Task;
    }

    private void Run(Action action)
    {
        Volatile.Write(ref _current, action);
        var start = Stopwatch.GetTimestamp();
        try { action(); }
        finally
        {
            Stats.OnExecuted(Stopwatch.GetTimestamp() - start);
            Volatile.Write(ref _current, null);
        }
    }

    private static string Describe(Action a) => a.Method.DeclaringType is { } t ? $"{t.FullName}.{a.Method.Name}" : a.Method.Name;
}

/// <summary>Production dispatcher over Avalonia's UI thread.</summary>
public sealed class AvaloniaUiDispatcher : UiDispatcherBase
{
    public static readonly AvaloniaUiDispatcher Instance = new();

    public override bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    protected override void PostCore(Action action, UiPriority priority) =>
        Dispatcher.UIThread.Post(action, priority switch
        {
            UiPriority.Input => DispatcherPriority.Input,
            UiPriority.Background => DispatcherPriority.Background,
            UiPriority.Idle => DispatcherPriority.ApplicationIdle,
            _ => DispatcherPriority.Normal,
        });
}
