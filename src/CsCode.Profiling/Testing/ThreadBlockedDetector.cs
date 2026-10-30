using System.Diagnostics;
using System.Globalization;

namespace CsCode.Profiling.Testing;

/// <summary>A stretch of time the watched thread did not run its queue.</summary>
public readonly record struct BlockedSpan(TimeSpan Duration, TimeSpan At);

/// <summary>
/// Asserts that a thread's callbacks never block it for more than a threshold (MUST 9). Watching: a watchdog thread keeps one acknowledgement posted
/// to the thread under test and measures how long it takes to run; every wait longer than the threshold is a <see cref="BlockedSpan"/> (the
/// pattern of the app's <c>UiStallWatchdog</c>). Calling: <see cref="AssertCallerNotBlocked(Action, TimeSpan, int)"/> measures the synchronous part
/// of an API call made on the caller's thread — the repo's "does not block the caller" tests (ARCHITECTURE §6: enqueueing costs &lt; 0.1 ms).
/// </summary>
public sealed class ThreadBlockedDetector : IDisposable
{
    private readonly Action<Action> _post;
    private readonly long _thresholdTicks;
    private readonly TimeSpan _poll;
    private readonly Action _ack;
    private readonly List<BlockedSpan> _blocks = new();
    private readonly long _created = Stopwatch.GetTimestamp();
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _postedAt, _longest;
    private int _outstanding;

    public ThreadBlockedDetector(Action<Action> post, TimeSpan threshold, TimeSpan? pollInterval = null)
    {
        _post = post;
        _thresholdTicks = (long)(threshold.TotalSeconds * Stopwatch.Frequency);
        _poll = pollInterval ?? TimeSpan.FromMilliseconds(Math.Clamp(threshold.TotalMilliseconds / 4, 1, 20));
        _ack = OnAck;
        _thread = new Thread(Loop) { IsBackground = true, Name = "blocked-detector" };
    }

    public ThreadBlockedDetector Start()
    {
        _thread.Start();
        return this;
    }

    public IReadOnlyList<BlockedSpan> Blocks { get { lock (_blocks) return _blocks.ToArray(); } }

    /// <summary>Longest wait of an acknowledgement seen so far (ms), blocked or not.</summary>
    public double LongestMs => Interlocked.Read(ref _longest) * 1000.0 / Stopwatch.Frequency;

    public void AssertNoBlocks(string what = "the watched thread")
    {
        var blocks = Blocks;
        if (blocks.Count == 0) return;
        throw new MemoryAssertionException($"{what} was blocked {blocks.Count} time(s) beyond {_thresholdTicks * 1000.0 / Stopwatch.Frequency:F0} ms: " +
            string.Join(", ", blocks.Take(10).Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.Duration.TotalMilliseconds:F0} ms at {b.At.TotalSeconds:F1} s"))));
    }

    private void Loop()
    {
        while (!_stop)
        {
            if (Interlocked.CompareExchange(ref _outstanding, 1, 0) == 0)
            {
                Volatile.Write(ref _postedAt, Stopwatch.GetTimestamp());
                try { _post(_ack); }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return; }
            }
            Thread.Sleep(_poll);
        }
    }

    private void OnAck()
    {
        var now = Stopwatch.GetTimestamp();
        var waited = now - Volatile.Read(ref _postedAt);
        long seen;
        while (waited > (seen = Interlocked.Read(ref _longest)) && Interlocked.CompareExchange(ref _longest, waited, seen) != seen) { }
        if (waited > _thresholdTicks)
            lock (_blocks) _blocks.Add(new BlockedSpan(Stopwatch.GetElapsedTime(0, waited), Stopwatch.GetElapsedTime(_created, Volatile.Read(ref _postedAt))));
        Volatile.Write(ref _outstanding, 0);
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join();
    }

    // ------------------------------------------------------------------ caller cost

    /// <summary>How long <paramref name="call"/> kept the calling thread (the synchronous part only).</summary>
    public static TimeSpan MeasureCall(Action call)
    {
        var t0 = Stopwatch.GetTimestamp();
        call();
        return Stopwatch.GetElapsedTime(t0);
    }

    /// <summary>
    /// Runs <paramref name="call"/> <paramref name="repeat"/> times and throws unless the median synchronous cost is within <paramref name="max"/>
    /// (the median: one call may pay for a first-time JIT or a GC that is not the API's).
    /// </summary>
    public static TimeSpan AssertCallerNotBlocked(Action call, TimeSpan max, int repeat = 5)
    {
        var costs = new double[repeat];
        for (var i = 0; i < repeat; i++) costs[i] = MeasureCall(call).TotalMilliseconds;
        Array.Sort(costs);
        var median = TimeSpan.FromMilliseconds(costs[repeat / 2]);
        if (median > max) throw new MemoryAssertionException(string.Create(CultureInfo.InvariantCulture, $"the call kept its caller {median.TotalMilliseconds:F3} ms (median of {repeat}; allowed {max.TotalMilliseconds:F3} ms)"));
        return median;
    }

    /// <summary>Async API variant: the time until the call returned its task (the part the caller pays before its first await).</summary>
    public static TimeSpan AssertCallerNotBlocked(Func<Task> call, TimeSpan max, int repeat = 5)
    {
        var pending = new List<Task>();
        var result = AssertCallerNotBlocked(() => pending.Add(call()), max, repeat);
        return result;
    }
}
