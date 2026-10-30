using System.Diagnostics;
using System.Globalization;

namespace CsCode.Profiling.Testing;

/// <summary>What a <see cref="ResponsivenessProbe"/> measured: the tick latencies and how many ticks were dropped or still pending.</summary>
public sealed record ResponsivenessReport(LatencyReport Latency, long Dropped, long Pending, TimeSpan Duration)
{
    public double P50Ms => Latency.P50Ms;
    public double P99Ms => Latency.P99Ms;
    public double MaxMs => Latency.MaxMs;
    public long Samples => Latency.Samples;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"dispatcher {Latency} over {Duration.TotalSeconds:F1} s{(Dropped > 0 ? $", {Dropped} ticks dropped" : "")}{(Pending > 0 ? $", {Pending} pending" : "")}");

    /// <summary>The ARCHITECTURE §6 budget by default: p99 ≤ 16 ms, max ≤ 50 ms. A probe whose ticks are still pending fails too (a stuck thread).</summary>
    public void AssertWithin(double p99Ms = 16, double maxMs = 50)
    {
        Latency.AssertWithin(p99Ms, maxMs, "dispatcher latency");
        if (Dropped > 0 || Pending > 0) throw new MemoryAssertionException($"the thread under test fell behind: {this}");
    }
}

/// <summary>
/// The responsiveness probe (MUST 9, T23): a dedicated thread posts a tick to the thread under test every 10 ms through the <c>post</c> delegate
/// it was given (headless Avalonia: <c>a =&gt; Dispatcher.UIThread.Post(a)</c>; a custom loop: its own enqueue) and each tick records how long it
/// waited in that thread's queue — the latency any input or render job posted at that moment would have seen. Ticks are pre-allocated (a ring of
/// <c>maxOutstanding</c> slots, each with its own cached delegate) and latencies go into a <see cref="LatencyHistogram"/>, so measuring allocates
/// nothing beyond what <c>post</c> itself costs. Every period gets its own tick even while earlier ones are queued (a 100 ms stall yields ten
/// samples of 100, 90 … 10 ms, as the repo's earlier heartbeats measured); when the ring is full, ticks are counted as dropped.
/// UI-toolkit independent: no Avalonia reference. See docs/MEMORY.md "Measuring UI budgets headless" for how to drive frames and input.
/// </summary>
public sealed class ResponsivenessProbe : IDisposable
{
    private readonly Action<Action> _post;
    private readonly long _periodTicks;
    private readonly long[] _postedAt;
    private readonly Action[] _runners;
    private readonly LatencyHistogram _histogram = new();
    private Thread? _thread;
    private volatile bool _stop;
    private long _posted, _completed, _dropped, _started, _stopped;

    public ResponsivenessProbe(Action<Action> post, TimeSpan? period = null, int maxOutstanding = 4096)
    {
        ArgumentNullException.ThrowIfNull(post);
        _post = post;
        _periodTicks = (long)((period ?? TimeSpan.FromMilliseconds(10)).TotalSeconds * Stopwatch.Frequency);
        _postedAt = new long[maxOutstanding];
        _runners = new Action[maxOutstanding];
        for (var i = 0; i < maxOutstanding; i++)
        {
            var slot = i;
            _runners[i] = () => Complete(slot);
        }
    }

    /// <summary>Latencies recorded so far (ms buckets).</summary>
    public LatencyHistogram Histogram => _histogram;

    public ResponsivenessProbe Start()
    {
        if (_thread is not null) return this;
        _stop = false;
        Interlocked.Exchange(ref _started, Stopwatch.GetTimestamp());
        _thread = new Thread(Loop) { IsBackground = true, Name = "responsiveness-probe", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        return this;
    }

    /// <summary>Stops posting and reports what completed so far (ticks still queued count as pending).</summary>
    public ResponsivenessReport Stop()
    {
        _stop = true;
        _thread?.Join();
        _thread = null;
        Interlocked.Exchange(ref _stopped, Stopwatch.GetTimestamp());
        return Report();
    }

    /// <summary>
    /// Stops posting, then gives the queued ticks up to <paramref name="drain"/> to run (awaiting on a UI thread lets its loop run them) and reports.
    /// </summary>
    public async Task<ResponsivenessReport> StopAsync(TimeSpan? drain = null)
    {
        _stop = true;
        if (_thread is { } t) await Task.Run(() => t.Join()).ConfigureAwait(true);
        _thread = null;
        Interlocked.Exchange(ref _stopped, Stopwatch.GetTimestamp());
        var limit = Stopwatch.StartNew();
        while (Interlocked.Read(ref _completed) < Interlocked.Read(ref _posted) && limit.Elapsed < (drain ?? TimeSpan.FromSeconds(2))) await Task.Delay(5).ConfigureAwait(true);
        return Report();
    }

    public ResponsivenessReport Report()
    {
        var stopped = Interlocked.Read(ref _stopped);
        var end = stopped != 0 ? stopped : Stopwatch.GetTimestamp();
        return new ResponsivenessReport(_histogram.Report(), Interlocked.Read(ref _dropped), Interlocked.Read(ref _posted) - Interlocked.Read(ref _completed),
            Stopwatch.GetElapsedTime(Interlocked.Read(ref _started), end));
    }

    /// <summary>Forgets what was recorded (e.g. after a warm-up); keeps running.</summary>
    public void Reset()
    {
        _histogram.Reset();
        Interlocked.Exchange(ref _dropped, 0);
        Interlocked.Exchange(ref _started, Stopwatch.GetTimestamp());
    }

    private void Loop()
    {
        var start = Stopwatch.GetTimestamp();
        for (long n = 1; !_stop; n++)
        {
            var posted = Interlocked.Read(ref _posted);
            if (posted - Interlocked.Read(ref _completed) >= _postedAt.Length) Interlocked.Increment(ref _dropped);
            else
            {
                var slot = (int)(posted % _postedAt.Length);
                Volatile.Write(ref _postedAt[slot], Stopwatch.GetTimestamp());
                Interlocked.Increment(ref _posted);
                try { _post(_runners[slot]); }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { Interlocked.Increment(ref _completed); } // the loop is gone
            }
            var next = start + n * _periodTicks;
            while (!_stop && Stopwatch.GetTimestamp() < next) Thread.Sleep(1);
        }
    }

    private void Complete(int slot)
    {
        _histogram.RecordTicks(Stopwatch.GetTimestamp() - Volatile.Read(ref _postedAt[slot]));
        Interlocked.Increment(ref _completed);
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join();
        _thread = null;
    }
}
