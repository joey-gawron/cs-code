using System.Threading.Channels;

namespace CsCode.Dap.Client;

/// <summary>
/// The "Debug lane" (ARCHITECTURE §6): ONE dedicated thread (not the shared thread pool) that runs every DAP client loop —
/// reading frames, JSON, response correlation, event dispatch, variable/stack mapping, session state machines. Because it is
/// single-threaded and installs a <see cref="SynchronizationContext"/>, async code started on it resumes on it after every
/// <c>await</c> (no <c>ConfigureAwait(false)</c> there, on purpose), so session state needs no locks and a
/// debug-adapter flood can only ever delay other debug work, never the UI or the typing path.
/// Callers on any thread pay only an enqueue: <see cref="RunAsync{T}"/> posts and returns a task.
/// </summary>
public sealed class DebugLane : IDisposable
{
    private readonly Channel<(SendOrPostCallback Callback, object? State)> _queue =
        Channel.CreateUnbounded<(SendOrPostCallback, object?)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private int _managedThreadId;
    private long _processed;

    public DebugLane(string name = "CsCode.Debug")
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
        _ready.Wait(); // the thread has installed its SynchronizationContext before anyone can use the lane
    }

    /// <summary>Context that routes continuations back onto the lane thread.</summary>
    public SynchronizationContext Context { get; private set; } = null!;

    public bool IsOnLane => Environment.CurrentManagedThreadId == Volatile.Read(ref _managedThreadId);
    public int ManagedThreadId => Volatile.Read(ref _managedThreadId);
    public long ProcessedItems => Interlocked.Read(ref _processed);
    public bool IsDisposed => _cts.IsCancellationRequested;

    /// <summary>Fire-and-forget: enqueue only (&lt; 0.1 ms). Dropped silently after disposal.</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _queue.Writer.TryWrite((static s => ((Action)s!)(), action));
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the lane and returns a task that completes (asynchronously, never inline on the lane)
    /// when it does. From the lane itself the work just runs inline. Cancelling <paramref name="ct"/> before the lane
    /// picks the item up skips the work.
    /// </summary>
    public Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        if (IsOnLane) return work();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_cts.IsCancellationRequested)
        {
            tcs.SetException(new ObjectDisposedException(nameof(DebugLane)));
            return tcs.Task;
        }
        var wrote = _queue.Writer.TryWrite((static async s =>
        {
            var (w, t, token) = ((Func<Task<T>>, TaskCompletionSource<T>, CancellationToken))s!;
            if (token.IsCancellationRequested) { t.TrySetCanceled(token); return; }
            try { t.TrySetResult(await w()); }
            catch (OperationCanceledException oce) { t.TrySetCanceled(oce.CancellationToken); }
            catch (Exception ex) { t.TrySetException(ex); }
        }, (work, tcs, ct)));
        if (!wrote) tcs.TrySetException(new ObjectDisposedException(nameof(DebugLane)));
        return tcs.Task;
    }

    public Task RunAsync(Func<Task> work, CancellationToken ct = default) =>
        RunAsync(async () => { await work(); return 0; }, ct);

    public Task<T> Run<T>(Func<T> work, CancellationToken ct = default) =>
        RunAsync(() => Task.FromResult(work()), ct);

    /// <summary>Runs <paramref name="action"/> on the lane after <paramref name="delay"/> (cancel the token to drop it).</summary>
    public void PostDelayed(TimeSpan delay, Action action, CancellationToken ct = default) => _ = DelayThenPostAsync(delay, action, ct);

    private async Task DelayThenPostAsync(TimeSpan delay, Action action, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        Post(action);
    }

    private void Loop()
    {
        Volatile.Write(ref _managedThreadId, Environment.CurrentManagedThreadId);
        var ctx = new LaneContext(this);
        Context = ctx;
        SynchronizationContext.SetSynchronizationContext(ctx);
        _ready.Set();
        var reader = _queue.Reader;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!reader.WaitToReadAsync(_cts.Token).AsTask().GetAwaiter().GetResult()) break; // dedicated thread: blocking here is the point
                while (reader.TryRead(out var item))
                {
                    try { item.Callback(item.State); }
                    catch (Exception) { /* a faulting work item must never kill the lane; RunAsync wrappers already route errors to their task */ }
                    Interlocked.Increment(ref _processed);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _queue.Writer.TryComplete();
        if (!IsOnLane) _thread.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class LaneContext(DebugLane lane) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => lane._queue.Writer.TryWrite((d, state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (lane.IsOnLane) d(state);
            else throw new NotSupportedException("Synchronous Send to the debug lane is not supported (it would block the caller).");
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
