using System.Collections.Concurrent;
using CsCode.Core.Debugging;

namespace CsCode.Panels.Debug.Console;

/// <summary>
/// Turns the debuggee's output events into console lines OFF the UI thread and off the debug lane, with hard bounds:
/// <list type="bullet">
/// <item><see cref="Enqueue"/> (called on the debug lane for every event) does O(1) work: it appends to a bounded queue (≤ <see cref="MaxQueuedChars"/>
/// characters, oldest dropped first) and arms a timer.</item>
/// <item>At most once per <c>interval</c> (≥ 16 ms) a pool thread drains the queue, keeps only the newest <see cref="ConsoleBuffer.DefaultCapacity"/>
/// lines (the rest cannot be displayed anyway and is reported as "N lines not shown"), splits them into lines and hands one immutable batch
/// to <c>deliver</c>.</item>
/// </list>
/// A debuggee printing a million lines a second therefore costs the UI one small batch per window and costs memory nothing.
/// </summary>
internal sealed class ConsoleOutputPump : IDisposable
{
    public const int MaxQueuedChars = 4 * 1024 * 1024;
    public const int MaxLineChars = 4_000;

    private readonly ConsoleBuffer _buffer;
    private readonly Func<ConsoleLine, bool> _accept;
    private readonly Action<IReadOnlyList<ConsoleLine>> _deliver;
    private readonly TimeSpan _interval;
    private readonly ConcurrentQueue<DebugOutputEventArgs> _queue = new();
    private readonly object _gate = new();
    private readonly Timer _timer;
    private DebugOutputEventArgs[] _seed = [];
    private HashSet<DebugOutputEventArgs>? _seedSet;
    private long _queuedChars;
    private long _lastDrainTicks;
    private long _sequence;
    private int _armed;
    private int _disposed;

    public ConsoleOutputPump(ConsoleBuffer buffer, Func<ConsoleLine, bool> accept, Action<IReadOnlyList<ConsoleLine>> deliver, TimeSpan? interval = null)
    {
        _buffer = buffer;
        _accept = accept;
        _deliver = deliver;
        _interval = interval is { } i && i >= TimeSpan.FromMilliseconds(16) ? i : TimeSpan.FromMilliseconds(16);
        _timer = new Timer(static s => ((ConsoleOutputPump)s!).Drain(), this, Timeout.Infinite, Timeout.Infinite);
    }

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>Events already in the debug service's history when the console was opened; shown first, and not shown twice if they also arrive live.</summary>
    public void SetSeed(IReadOnlyList<DebugOutputEventArgs> history)
    {
        lock (_gate)
        {
            _seed = [.. history];
            _seedSet = new HashSet<DebugOutputEventArgs>(_seed, ReferenceEqualityComparer.Instance as IEqualityComparer<DebugOutputEventArgs>);
        }
        Arm(TimeSpan.Zero);
    }

    /// <summary>Called on the debug lane for every output event: O(1), never blocks.</summary>
    public void Enqueue(DebugOutputEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _queue.Enqueue(e);
        var total = Interlocked.Add(ref _queuedChars, e.Output.Length);
        while (total > MaxQueuedChars && _queue.TryDequeue(out var dropped))
            total = Interlocked.Add(ref _queuedChars, -dropped.Output.Length); // drop-oldest: the newest output is what a console is for
        Arm(null);
    }

    private void Arm(TimeSpan? delay)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _armed, 1) != 0) return;
        var wait = delay ?? _interval - TimeSpan.FromTicks(Math.Max(0, System.Diagnostics.Stopwatch.GetTimestamp() - Volatile.Read(ref _lastDrainTicks)) * TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency);
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        try { _timer.Change(wait, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void Drain()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Volatile.Write(ref _lastDrainTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        Volatile.Write(ref _armed, 0);

        DebugOutputEventArgs[] seed;
        HashSet<DebugOutputEventArgs>? seedSet;
        lock (_gate)
        {
            seed = _seed;
            seedSet = _seedSet;
            _seed = [];
            _seedSet = null;
        }

        var events = new List<DebugOutputEventArgs>(seed.Length + _queue.Count);
        events.AddRange(seed);
        while (_queue.TryDequeue(out var e))
        {
            Interlocked.Add(ref _queuedChars, -e.Output.Length);
            if (seedSet is null || !seedSet.Contains(e)) events.Add(e);
        }
        if (events.Count > 0)
        {
            var lines = ToLines(events, ConsoleBuffer.DefaultCapacity);
            if (lines.Count > 0)
            {
                _buffer.Append(lines);
                var accepted = lines.Where(_accept).ToList();
                if (accepted.Count > 0) _deliver(accepted);
            }
        }
        // More may have arrived while we worked.
        if (!_queue.IsEmpty) Arm(null);
    }

    /// <summary>
    /// Splits events into lines keeping only the newest <paramref name="maxLines"/> (older text is dropped without being split: counting
    /// newlines is cheap, creating a million strings is not). A note line says how many were not shown.
    /// </summary>
    internal List<ConsoleLine> ToLines(List<DebugOutputEventArgs> events, int maxLines)
    {
        var budget = maxLines;
        long dropped = 0;
        var kept = new List<(DebugOutputEventArgs Event, string Text)>();
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var e = events[i];
            var count = CountLines(e.Output);
            if (count <= budget)
            {
                kept.Add((e, e.Output));
                budget -= count;
            }
            else
            {
                if (budget > 0)
                {
                    kept.Add((e, TailLines(e.Output, budget)));
                    dropped += count - budget;
                    budget = 0;
                }
                else dropped += count;
            }
        }
        kept.Reverse();

        var lines = new List<ConsoleLine>(maxLines - budget + 1);
        if (dropped > 0) lines.Add(new ConsoleLine(NextSequence(), ConsoleLineKind.Info, $"… {dropped:N0} lines of output were not shown (the program wrote faster than the console can display)"));
        foreach (var (e, text) in kept) AddLines(lines, e, text);
        return lines;
    }

    private void AddLines(List<ConsoleLine> lines, DebugOutputEventArgs e, string text)
    {
        var kind = KindOf(e.Category);
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n') continue;
            if (i == text.Length && start == text.Length) break;
            var end = i > start && text[i - 1] == '\r' && i < text.Length ? i - 1 : i;
            var segment = text.AsSpan(start, end - start);
            lines.Add(new ConsoleLine(NextSequence(), kind, segment.Length > MaxLineChars ? segment[..MaxLineChars].ToString() + "…" : segment.ToString(), e.Source, e.Line));
            start = i + 1;
        }
    }

    internal static ConsoleLineKind KindOf(string category) => category switch
    {
        "stdout" => ConsoleLineKind.Output,
        "stderr" => ConsoleLineKind.Error,
        "important" => ConsoleLineKind.Important,
        _ => ConsoleLineKind.Console,
    };

    /// <summary>Number of lines <paramref name="text"/> turns into (a trailing partial line counts).</summary>
    internal static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var newlines = text.AsSpan().Count('\n');
        return text[^1] == '\n' ? newlines : newlines + 1;
    }

    /// <summary>The last <paramref name="lines"/> lines of <paramref name="text"/> (without splitting anything else).</summary>
    internal static string TailLines(string text, int lines)
    {
        var end = text.Length;
        if (end > 0 && text[end - 1] == '\n') end--; // a terminating newline does not start another line
        var pos = end;
        for (var n = 0; n < lines && pos > 0; n++)
        {
            pos = text.LastIndexOf('\n', pos - 1);
            if (pos < 0) return text;
        }
        return text[(pos + 1)..];
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
        while (_queue.TryDequeue(out _)) { }
        lock (_gate) { _seed = []; _seedSet = null; }
    }
}
