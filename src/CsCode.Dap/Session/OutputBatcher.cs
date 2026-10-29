using System.Text;
using CsCode.Core.Debugging;
using CsCode.Dap.Client;

namespace CsCode.Dap.Session;

/// <summary>
/// Coalesces a debuggee's output into small immutable batches delivered at most once per window (default 16 ms) so a program that
/// prints a million lines a second costs the UI about one event per frame per category, not a million. The pending buffer is a
/// BOUNDED ring: when a window collects more than <c>maxPendingChars</c> (128 K chars) the oldest text is dropped (and a one-line
/// marker says how much), which is the "drop-oldest" policy of ARCHITECTURE §6. Lane-affine: every member is called on the debug lane.
/// </summary>
internal sealed class OutputBatcher
{
    private sealed class Entry(string category, DebugSource? source, int? line)
    {
        public string Category { get; } = category;
        public DebugSource? Source { get; } = source;
        public int? Line { get; } = line;
        public StringBuilder Text { get; } = new();
    }

    private readonly DebugLane _lane;
    private readonly IDebugSession _session;
    private readonly Action<DebugOutputEventArgs> _deliver;
    private readonly TimeSpan _window;
    private readonly int _maxPendingChars;
    private readonly int _maxBatchChars;
    private readonly Queue<Entry> _pending = new();
    private Entry? _last;
    private int _pendingChars;
    private long _droppedChars, _reportedDropped;
    private bool _scheduled, _disposed;

    public OutputBatcher(DebugLane lane, IDebugSession session, Action<DebugOutputEventArgs> deliver, TimeSpan window, int maxPendingChars = 128 * 1024, int maxBatchChars = 128 * 1024)
    {
        _lane = lane;
        _session = session;
        _deliver = deliver;
        _window = window;
        _maxPendingChars = maxPendingChars;
        _maxBatchChars = maxBatchChars;
    }

    public long DroppedChars => _droppedChars;
    public int PendingChars => _pendingChars;

    public void Add(string category, string text, DebugSource? source = null, int? line = null)
    {
        if (_disposed || text.Length == 0) return;
        if (category == "telemetry") return; // DAP: telemetry must not be shown to the user

        // Merge with the previous entry when the category matches and neither carries a source location.
        if (_last is not null && source is null && _last.Source is null && _last.Category == category && _last.Text.Length < _maxBatchChars)
            _last.Text.Append(text);
        else
        {
            _last = new Entry(category, source, line);
            _last.Text.Append(text);
            _pending.Enqueue(_last);
        }
        _pendingChars += text.Length;
        if (_pendingChars > _maxPendingChars) Shed();

        if (!_scheduled)
        {
            _scheduled = true;
            _lane.PostDelayed(_window, Flush);
        }
    }

    /// <summary>Drops the oldest text until there is headroom again (a quarter of the cap at a time, so shedding is amortised).</summary>
    private void Shed()
    {
        var toDrop = _pendingChars - _maxPendingChars + _maxPendingChars / 4;
        while (toDrop > 0 && _pending.Count > 0)
        {
            var oldest = _pending.Peek();
            if (oldest.Text.Length <= toDrop)
            {
                _pending.Dequeue();
                toDrop -= oldest.Text.Length;
                _pendingChars -= oldest.Text.Length;
                _droppedChars += oldest.Text.Length;
                if (ReferenceEquals(oldest, _last)) _last = null;
            }
            else
            {
                oldest.Text.Remove(0, toDrop);
                _pendingChars -= toDrop;
                _droppedChars += toDrop;
                toDrop = 0;
            }
        }
    }

    /// <summary>Delivers everything pending right now (also used before stop/terminate events so output is ordered before them).</summary>
    public void Flush()
    {
        _scheduled = false;
        if (_disposed) return;
        ReportDropped();
        if (_pending.Count == 0) return;
        var batch = _pending.ToArray();
        _pending.Clear();
        _pendingChars = 0;
        _last = null;
        foreach (var e in batch)
        {
            var text = e.Text.ToString();
            // Split pathological giant entries so one event never carries more than maxBatchChars.
            for (var i = 0; i < text.Length; i += _maxBatchChars)
                _deliver(new DebugOutputEventArgs(_session, e.Category, text.Substring(i, Math.Min(_maxBatchChars, text.Length - i)), e.Source, e.Line));
        }
    }

    private void ReportDropped()
    {
        if (_droppedChars == _reportedDropped) return;
        var n = _droppedChars - _reportedDropped;
        _reportedDropped = _droppedChars;
        _deliver(new DebugOutputEventArgs(_session, "important", $"[debug output flooded: {n:N0} characters dropped]\n"));
    }

    public void Dispose()
    {
        _disposed = true;
        _pending.Clear();
        _pendingChars = 0;
        _last = null;
    }
}
