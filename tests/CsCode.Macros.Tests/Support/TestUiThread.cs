using System.Collections.Concurrent;
using System.Diagnostics;
using CsCode.Macros;

namespace CsCode.Macros.Tests.Support;

/// <summary>
/// A stand-in for the UI thread: one dedicated thread running posted callbacks in order, with its own <see cref="SynchronizationContext"/>
/// (like Avalonia's, so awaits behave as on the real UI thread). Records how long every callback ran and any exception one threw.
/// </summary>
public sealed class TestUiThread : IMacroScheduler, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly ConcurrentQueue<double> _callbackMs = new();

    public TestUiThread()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "test-ui" };
        _thread.Start();
    }

    public ConcurrentQueue<Exception> Errors { get; } = new();
    public IReadOnlyCollection<double> CallbackDurations => _callbackMs;
    public int Posted;

    public bool IsUiThread => Thread.CurrentThread == _thread;

    public void Post(Action action)
    {
        Interlocked.Increment(ref Posted);
        _queue.Add(action);
    }

    private void Loop()
    {
        SynchronizationContext.SetSynchronizationContext(new Context(this));
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            var start = Stopwatch.GetTimestamp();
            try { action(); }
            catch (Exception ex) { Errors.Enqueue(ex); }
            _callbackMs.Enqueue(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    /// <summary>Runs <paramref name="work"/> on the thread (its awaits come back to it) and returns its result.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try { tcs.SetResult(await work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public Task<T> Run<T>(Func<T> work) => RunAsync(() => Task.FromResult(work()));

    public Task Run(Action work) => RunAsync(() => { work(); return Task.FromResult(0); });

    /// <summary>Waits until everything posted so far (and what it posted in turn, up to <paramref name="rounds"/> times) has run.</summary>
    public async Task DrainAsync(int rounds = 3)
    {
        for (var i = 0; i < rounds; i++) await Run(() => { });
    }

    public void Dispose() => _queue.CompleteAdding();

    private sealed class Context(TestUiThread ui) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => ui.Post(() => d(state));
        public override void Send(SendOrPostCallback d, object? state)
        {
            if (ui.IsUiThread) d(state);
            else ui.Run(() => d(state)).Wait();
        }
        public override SynchronizationContext CreateCopy() => this;
    }
}

/// <summary>
/// Dispatcher latency on the <see cref="TestUiThread"/>: a heartbeat posts a timestamped no-op every millisecond and records how long each one
/// waited behind the work queued before it (the stand-in for CsCode.Profiling's ResponsivenessProbe, a parked module; same measurement as the
/// shell's ResponsivenessTests).
/// </summary>
public sealed class ResponsivenessProbe : IDisposable
{
    private readonly TestUiThread _ui;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<double> _latencies = new();
    private volatile bool _stop;

    public ResponsivenessProbe(TestUiThread ui)
    {
        _ui = ui;
        _thread = new Thread(Loop) { IsBackground = true, Name = "test-heartbeat" };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop)
        {
            var stamp = Stopwatch.GetTimestamp();
            _ui.Post(() => _latencies.Enqueue(Stopwatch.GetElapsedTime(stamp).TotalMilliseconds));
            Thread.Sleep(1);
        }
    }

    public (int Samples, double P50, double P99, double Max) Stop()
    {
        _stop = true;
        _thread.Join();
        Thread.Sleep(30);
        var all = _latencies.OrderBy(x => x).ToArray();
        if (all.Length == 0) return (0, 0, 0, 0);
        return (all.Length, all[all.Length / 2], all[Math.Min(all.Length - 1, (int)(all.Length * 0.99))], all[^1]);
    }

    public void Dispose() => _stop = true;
}

/// <summary>A clock the test moves: timestamps only (timers are the system's).</summary>
public sealed class StepClock : TimeProvider
{
    private long _ticks = 1;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
