using System.Diagnostics;

namespace CsCode.Lsp.Runtime;

public enum ServerHealth { Healthy, Unresponsive }

/// <summary>
/// Consecutive-timeout circuit breaker for one server. Closed = healthy. After <c>threshold</c> consecutive request timeouts it opens
/// (server "Unresponsive"): callers skip non-essential requests; one probe request is let through per <c>probeInterval</c> (half-open);
/// a single success closes it again (auto-recovery).
/// </summary>
internal sealed class CircuitBreaker(int threshold = 5, TimeSpan? probeInterval = null)
{
    private readonly TimeSpan _probeInterval = probeInterval ?? TimeSpan.FromSeconds(5);
    private int _consecutive;
    private int _open; // 0/1
    private long _nextProbeTicks;

    public ServerHealth Health => Volatile.Read(ref _open) == 1 ? ServerHealth.Unresponsive : ServerHealth.Healthy;
    public int ConsecutiveTimeouts => Volatile.Read(ref _consecutive);

    /// <summary>Raised (once per transition) with the new health.</summary>
    public event Action<ServerHealth>? HealthChanged;

    public void RecordSuccess()
    {
        Volatile.Write(ref _consecutive, 0);
        if (Interlocked.Exchange(ref _open, 0) == 1) HealthChanged?.Invoke(ServerHealth.Healthy);
    }

    public void RecordTimeout()
    {
        if (Interlocked.Increment(ref _consecutive) >= threshold && Interlocked.Exchange(ref _open, 1) == 0)
        {
            Volatile.Write(ref _nextProbeTicks, Stopwatch.GetTimestamp() + (long)(_probeInterval.TotalSeconds * Stopwatch.Frequency));
            HealthChanged?.Invoke(ServerHealth.Unresponsive);
        }
    }

    /// <summary>
    /// Whether a request may be sent now. Always true when closed; when open, true for exactly one caller per probe interval.
    /// <paramref name="essential"/> requests (shutdown, initialize) always pass.
    /// </summary>
    public bool AllowRequest(bool essential = false)
    {
        if (essential || Volatile.Read(ref _open) == 0) return true;
        var now = Stopwatch.GetTimestamp();
        var next = Volatile.Read(ref _nextProbeTicks);
        if (now < next) return false;
        return Interlocked.CompareExchange(ref _nextProbeTicks, now + (long)(_probeInterval.TotalSeconds * Stopwatch.Frequency), next) == next;
    }

    public void Reset()
    {
        Volatile.Write(ref _consecutive, 0);
        Interlocked.Exchange(ref _open, 0);
    }
}

/// <summary>Fixed-size line ring (stderr tail, "last N log lines"): O(1) append, hard memory cap, thread-safe.</summary>
internal sealed class LineRing(int maxLines, int maxLineChars = 2048)
{
    private readonly string[] _lines = new string[Math.Max(1, maxLines)];
    private readonly object _gate = new();
    private int _next;
    private int _count;
    private long _total;

    public long TotalLines { get { lock (_gate) return _total; } }

    public void Add(string line)
    {
        if (line.Length > maxLineChars) line = line[..maxLineChars] + "…";
        lock (_gate)
        {
            _lines[_next] = line;
            _next = (_next + 1) % _lines.Length;
            if (_count < _lines.Length) _count++;
            _total++;
        }
    }

    public string[] Snapshot()
    {
        lock (_gate)
        {
            var result = new string[_count];
            var start = (_next - _count + _lines.Length) % _lines.Length;
            for (var i = 0; i < _count; i++) result[i] = _lines[(start + i) % _lines.Length];
            return result;
        }
    }

    public string Tail(int maxChars = 4000)
    {
        var text = string.Join('\n', Snapshot());
        return text.Length <= maxChars ? text : text[^maxChars..];
    }
}
