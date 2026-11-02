using System.Threading.Channels;

namespace CsCode.Build.CMake;

/// <summary>
/// The CMake integration's own lane (ARCHITECTURE §6): one dedicated background thread, not the shared pool, on which the service keeps its
/// state, reads presets and parses File API replies. It installs a <see cref="SynchronizationContext"/>, so async work started here resumes here
/// after every <c>await</c> and the state needs no locks; callers on any thread (the UI's included) pay only an enqueue. The configure and build
/// processes themselves run as child processes; only their output lines come back through here.
/// </summary>
internal sealed class CMakeLane : IDisposable
{
    private readonly Channel<(SendOrPostCallback Callback, object? State)> _queue =
        Channel.CreateUnbounded<(SendOrPostCallback, object?)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private int _threadId;

    public CMakeLane(string name = "CsCode.CMake")
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        _ready.Wait();
    }

    public bool IsOnLane => Environment.CurrentManagedThreadId == Volatile.Read(ref _threadId);

    public void Post(Action action) => _queue.Writer.TryWrite((static s => ((Action)s!)(), action));

    /// <summary>Runs <paramref name="work"/> on the lane; the task completes off the lane. Inline when already on it.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        if (IsOnLane) return work();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrote = !_cts.IsCancellationRequested && _queue.Writer.TryWrite((static async s =>
        {
            var (w, t, token) = ((Func<Task<T>>, TaskCompletionSource<T>, CancellationToken))s!;
            if (token.IsCancellationRequested) { t.TrySetCanceled(token); return; }
            try { t.TrySetResult(await w()); }
            catch (OperationCanceledException oce) { t.TrySetCanceled(oce.CancellationToken); }
            catch (Exception ex) { t.TrySetException(ex); }
        }, (work, tcs, ct)));
        if (!wrote) tcs.TrySetException(new ObjectDisposedException(nameof(CMakeLane)));
        return tcs.Task;
    }

    public Task RunAsync(Func<Task> work, CancellationToken ct = default) => RunAsync(async () => { await work(); return 0; }, ct);

    private void Loop()
    {
        Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
        SynchronizationContext.SetSynchronizationContext(new LaneContext(this));
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
                    catch (Exception) { /* a faulting item must never kill the lane; RunAsync routes errors to its task */ }
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

    private sealed class LaneContext(CMakeLane lane) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => lane._queue.Writer.TryWrite((d, state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (lane.IsOnLane) d(state);
            else throw new NotSupportedException("Synchronous Send to the CMake lane is not supported (it would block the caller).");
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
