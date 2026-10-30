using System.Diagnostics;

namespace CsCode.Profiling.Testing;

/// <summary>Typing latency split as the terminal's budget tests split it: how long keys waited for the thread, how long handling took, and the total.</summary>
public sealed record TypingReport(LatencyReport Total, LatencyReport Dispatch, LatencyReport Handling, long Keys, long Missing)
{
    public void AssertWithin(double p99Ms = 20, double maxMs = double.PositiveInfinity)
    {
        Total.AssertWithin(p99Ms, maxMs, "typing latency");
        if (Missing > 0) throw new MemoryAssertionException($"{Missing} of {Keys} keys never reached the buffer");
    }

    public override string ToString() => $"typing {Total}; waited for the thread {Dispatch}; handling {Handling}; {Missing} missing";
}

/// <summary>
/// Typing latency (MUST 9): time from a simulated key event to the applied buffer mutation. Each key is posted to the UI thread at input priority
/// through <c>postInput</c> (as the platform delivers input: a dispatcher job, NOT a direct call, so the time it waits behind other work counts) and
/// delivered by <c>deliver</c> on that thread (raise TextInput on the focused editor, or apply the edit to a buffer). The key's latency ends when the
/// buffer changed: call <see cref="NotifyMutated"/> from the document's change event when it is wired (<see cref="UseMutationSignal"/>), otherwise
/// at the end of <c>deliver</c> (the typing path is synchronous: keystroke → mutation → return). Pre-allocated slots; no allocation per key.
/// </summary>
public sealed class TypingLatencyProbe
{
    private readonly Action<Action> _postInput;
    private readonly Action<char> _deliver;
    private readonly long[] _posted;
    private readonly char[] _chars;
    private readonly Action[] _runners;
    private readonly LatencyHistogram _total = new(), _dispatch = new(), _handling = new();
    private long _typed, _delivered, _mutated;

    public TypingLatencyProbe(Action<Action> postInput, Action<char> deliver, int maxOutstanding = 1024)
    {
        _postInput = postInput;
        _deliver = deliver;
        _posted = new long[maxOutstanding];
        _chars = new char[maxOutstanding];
        _runners = new Action[maxOutstanding];
        for (var i = 0; i < maxOutstanding; i++)
        {
            var slot = i;
            _runners[i] = () => Deliver(slot);
        }
    }

    /// <summary>True: latency ends at <see cref="NotifyMutated"/> (wire it to the buffer's change event); false: at the end of delivery.</summary>
    public bool UseMutationSignal { get; init; }

    /// <summary>Posts one key (from any thread). Returns false when too many keys are still undelivered.</summary>
    public bool Type(char c)
    {
        var n = Interlocked.Read(ref _typed);
        if (n - Interlocked.Read(ref _mutated) >= _posted.Length) return false;
        var slot = (int)(n % _posted.Length);
        _chars[slot] = c;
        Volatile.Write(ref _posted[slot], Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _typed);
        _postInput(_runners[slot]);
        return true;
    }

    /// <summary>Types <paramref name="text"/> one key every <paramref name="interval"/> (a fast typist: 50 ms ≈ 240 wpm).</summary>
    public async Task TypeAsync(string text, TimeSpan interval, CancellationToken ct = default)
    {
        foreach (var c in text)
        {
            ct.ThrowIfCancellationRequested();
            Type(c);
            await Task.Delay(interval, ct).ConfigureAwait(true);
        }
    }

    /// <summary>The buffer changed (call from its change event on the UI thread): completes the oldest key still waiting for its mutation.</summary>
    public void NotifyMutated()
    {
        var done = Interlocked.Read(ref _mutated);
        if (done >= Interlocked.Read(ref _delivered)) return; // a change that was not ours (an auto-closed bracket, a format edit)
        var slot = (int)(done % _posted.Length);
        _total.RecordTicks(Stopwatch.GetTimestamp() - Volatile.Read(ref _posted[slot]));
        Interlocked.Increment(ref _mutated);
    }

    public TypingReport Report() => new(_total.Report(), _dispatch.Report(), _handling.Report(), Interlocked.Read(ref _typed),
        Interlocked.Read(ref _typed) - Interlocked.Read(ref _mutated));

    /// <summary>Waits (yielding, so a UI caller's loop runs the keys) until every typed key was applied or <paramref name="timeout"/> passed.</summary>
    public async Task<TypingReport> DrainAsync(TimeSpan? timeout = null)
    {
        var limit = Stopwatch.StartNew();
        while (Interlocked.Read(ref _mutated) < Interlocked.Read(ref _typed) && limit.Elapsed < (timeout ?? TimeSpan.FromSeconds(5))) await Task.Delay(5).ConfigureAwait(true);
        return Report();
    }

    private void Deliver(int slot)
    {
        var start = Stopwatch.GetTimestamp();
        var posted = Volatile.Read(ref _posted[slot]);
        _dispatch.RecordTicks(start - posted);
        Interlocked.Increment(ref _delivered);
        _deliver(_chars[slot]);
        var end = Stopwatch.GetTimestamp();
        _handling.RecordTicks(end - start);
        if (UseMutationSignal) return;
        _total.RecordTicks(end - posted);
        Interlocked.Increment(ref _mutated);
    }
}
