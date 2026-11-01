using System.Collections.Concurrent;
using CsCode.Core.Debugging;

namespace CsCode.Panels.Debug.Console;

/// <summary>
/// Turns the debuggee's output events into console lines OFF the UI thread and off the debug lane, with hard bounds:
/// <list type="bullet">
/// <item><see cref="Enqueue"/> (called on the debug lane for every event) does O(1) work: it appends to a bounded queue (≤ <see cref="MaxQueuedChars"/>
/// characters, oldest dropped first) and arms a timer.</item>
/// <item>At most once per <c>interval</c> (≥ 16 ms, one frame) a pool thread drains the queue, keeps only the newest
/// <see cref="ConsoleBuffer.DefaultCapacity"/> lines (the rest cannot be displayed anyway and is reported as "N lines not shown"), splits them
/// into lines, parses their ANSI escapes (<see cref="AnsiParser"/>) and hands one immutable batch to <c>deliver</c>.</item>
/// </list>
/// A debuggee printing a million lines a second therefore costs the UI one small batch per frame and costs memory nothing.
/// <para>REPL lines (what the user evaluated and its result) have a queue of their own (<see cref="EnqueueRepl"/>), which the output bound never
/// drops from (a flood cannot take the user's line away), and are placed among the output by <see cref="DebugOutputEventArgs.Sequence"/>, with
/// the same rule a new console uses to replay the transcript: after the newest output that had arrived when the line was entered.</para>
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
    // Separate from the output so the drop-oldest bound never drops one (a few lines per Enter, nothing to bound).
    private readonly ConcurrentQueue<ReplLine> _replQueue = new();
    private readonly object _gate = new();
    private readonly object _drainGate = new();
    // One ANSI style state per output category (stdout's colours do not leak into stderr); used under _drainGate only.
    private readonly Dictionary<string, AnsiParser> _parsers = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private DebugOutputEventArgs[] _seed = [];
    private HashSet<long>? _seedSequences;
    private IReadOnlyList<ReplLine> _seedRepl = [];
    private long _queuedChars;
    private long _lastDrainTicks;
    private long _sequence;
    private int _armed;
    private int _resetStyles;
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

    /// <summary>
    /// Events already in the debug service's history when the console was opened; shown first, and not shown twice if they also arrive live
    /// (recognised by <see cref="DebugOutputEventArgs.Sequence"/>, which the history's copies keep). <paramref name="repl"/> (what earlier
    /// consoles evaluated) is shown among them, each line after the output event it followed.
    /// </summary>
    public void SetSeed(IReadOnlyList<DebugOutputEventArgs> history, IReadOnlyList<ReplLine>? repl = null)
    {
        lock (_gate)
        {
            _seed = [.. history];
            _seedSequences = [.. _seed.Select(e => e.Sequence)];
            _seedRepl = repl ?? [];
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

    /// <summary>A REPL line entered in this console: shown in the next batch, after the output that had arrived when it was entered.</summary>
    public void EnqueueRepl(ReplLine line)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _replQueue.Enqueue(line);
        Arm(null);
    }

    /// <summary>The console was cleared: colours left on by earlier output do not carry into what comes next.</summary>
    public void ResetStyles() => Volatile.Write(ref _resetStyles, 1);

    private void Arm(TimeSpan? delay)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _armed, 1) != 0) return;
        // Wait out the rest of the window since the last drain (never more than one interval, never negative).
        var wait = delay ?? _interval - System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _lastDrainTicks));
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        else if (wait > _interval) wait = _interval;
        try { _timer.Change(wait, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void Drain()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        // One drain at a time (the timer can fire again while a big batch is being split): batches stay in order, and the ANSI state is the pump's.
        lock (_drainGate)
        {
            Volatile.Write(ref _lastDrainTicks, System.Diagnostics.Stopwatch.GetTimestamp());
            Volatile.Write(ref _armed, 0);
            if (Interlocked.Exchange(ref _resetStyles, 0) != 0) _parsers.Clear();

            DebugOutputEventArgs[] seed;
            HashSet<long>? seedSequences;
            IReadOnlyList<ReplLine> seedRepl;
            lock (_gate)
            {
                seed = _seed;
                seedSequences = _seedSequences;
                seedRepl = _seedRepl;
                _seed = [];
                _seedSequences = null;
                _seedRepl = [];
            }

            var events = new List<DebugOutputEventArgs>(seed.Length + _queue.Count);
            events.AddRange(seed);
            List<ReplLine>? repl = seedRepl.Count > 0 ? [.. seedRepl] : null;
            // REPL lines first: the output a line follows was enqueued before it, so it is in this batch too (taken after) and the line is
            // never shown ahead of it.
            while (_replQueue.TryDequeue(out var r)) (repl ??= new List<ReplLine>()).Add(r);
            while (_queue.TryDequeue(out var e))
            {
                Interlocked.Add(ref _queuedChars, -e.Output.Length);
                if (seedSequences is null || !seedSequences.Contains(e.Sequence)) events.Add(e);
            }
            if (events.Count > 0 || repl is not null)
            {
                var lines = ToLines(events, ConsoleBuffer.DefaultCapacity, repl);
                if (lines.Count > 0)
                {
                    _buffer.Append(lines);
                    var accepted = lines.Where(_accept).ToList();
                    if (accepted.Count > 0) _deliver(accepted);
                }
            }
        }
        // More may have arrived while we worked.
        if (!_queue.IsEmpty || !_replQueue.IsEmpty) Arm(null);
    }

    /// <summary>
    /// Splits events into lines keeping only the newest <paramref name="maxLines"/> (older text is dropped without being split: counting
    /// newlines is cheap, creating a million strings is not). A note line says how many were not shown. Each of the <paramref name="repl"/>
    /// lines (in the order they were entered) goes after the last shown event whose <see cref="DebugOutputEventArgs.Sequence"/> is not greater
    /// than the one it followed, and never before the line entered before it.
    /// </summary>
    internal List<ConsoleLine> ToLines(List<DebugOutputEventArgs> events, int maxLines, IReadOnlyList<ReplLine>? repl = null)
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

        var lines = new List<ConsoleLine>(maxLines - budget + 1 + (repl?.Count ?? 0));
        if (dropped > 0) lines.Add(new ConsoleLine(NextSequence(), ConsoleLineKind.Info, $"… {dropped:N0} lines of output were not shown (the program wrote faster than the console can display)"));
        if (repl is not { Count: > 0 })
        {
            foreach (var (e, text) in kept) AddLines(lines, e, text);
            return lines;
        }

        // newest[i]: the highest sequence among kept[0..i] (non-decreasing, so a binary search finds where a sequence belongs even if a
        // producer ever delivered out of order).
        var newest = new long[kept.Count];
        for (var i = 0; i < kept.Count; i++) newest[i] = Math.Max(i == 0 ? long.MinValue : newest[i - 1], kept[i].Event.Sequence);
        var after = new List<ReplLine>?[kept.Count];
        var previous = -1;
        foreach (var r in repl)
        {
            var at = Math.Max(LastNotAfter(newest, r.After), previous);
            previous = at;
            if (at < 0) lines.Add(new ConsoleLine(NextSequence(), r.Kind, r.Text));
            else (after[at] ??= new List<ReplLine>()).Add(r);
        }
        for (var i = 0; i < kept.Count; i++)
        {
            AddLines(lines, kept[i].Event, kept[i].Text);
            if (after[i] is { } list)
                foreach (var r in list) lines.Add(new ConsoleLine(NextSequence(), r.Kind, r.Text));
        }
        if (lines.Count > maxLines) lines.RemoveRange(0, lines.Count - maxLines);
        return lines;
    }

    /// <summary>The index of the last entry of the non-decreasing <paramref name="newest"/> that is ≤ <paramref name="sequence"/>, or -1.</summary>
    private static int LastNotAfter(long[] newest, long sequence)
    {
        int lo = 0, hi = newest.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (newest[mid] <= sequence) lo = mid + 1;
            else hi = mid;
        }
        return lo - 1;
    }

    private void AddLines(List<ConsoleLine> lines, DebugOutputEventArgs e, string text)
    {
        var kind = KindOf(e.Category);
        var parser = ParserFor(e.Category);
        var styled = parser.Needed(text);
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n') continue;
            if (i == text.Length && start == text.Length) break;
            var end = i > start && text[i - 1] == '\r' && i < text.Length ? i - 1 : i;
            var segment = text.AsSpan(start, end - start);
            if (styled)
            {
                var (plain, spans) = parser.ParseLine(segment, MaxLineChars);
                lines.Add(new ConsoleLine(NextSequence(), kind, plain, e.Source, e.Line, spans));
            }
            else lines.Add(new ConsoleLine(NextSequence(), kind, segment.Length > MaxLineChars ? segment[..MaxLineChars].ToString() + "…" : segment.ToString(), e.Source, e.Line));
            start = i + 1;
        }
    }

    private AnsiParser ParserFor(string category)
    {
        if (!_parsers.TryGetValue(category, out var parser)) _parsers[category] = parser = new AnsiParser();
        return parser;
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
        while (_replQueue.TryDequeue(out _)) { }
        lock (_gate) { _seed = []; _seedSequences = null; _seedRepl = []; }
    }
}
