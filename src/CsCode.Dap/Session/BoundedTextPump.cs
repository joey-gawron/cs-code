using System.Text;
using CsCode.Dap.Client;

namespace CsCode.Dap.Session;

/// <summary>
/// Thread-safe, bounded hand-off from a pipe reader (any thread) to the lane: text accumulates under a lock (oldest dropped past the
/// cap) and AT MOST ONE flush is ever queued on the lane, however fast the producer writes. Used for debuggee and task output.
/// </summary>
internal sealed class BoundedTextPump(DebugLane lane, Action<string> sink, int maxPending = 512 * 1024)
{
    private readonly object _gate = new();
    private readonly StringBuilder _pending = new();
    private bool _flushQueued;
    private long _dropped;

    public long DroppedChars { get { lock (_gate) return _dropped; } }

    public void Append(ReadOnlySpan<char> text)
    {
        bool post;
        lock (_gate)
        {
            _pending.Append(text);
            if (_pending.Length > maxPending)
            {
                var drop = _pending.Length - maxPending / 2;
                _pending.Remove(0, drop);
                _dropped += drop;
            }
            post = !_flushQueued;
            _flushQueued = true;
        }
        if (post) lane.Post(Flush);
    }

    /// <summary>Final flush (producer finished).</summary>
    public void Complete() => lane.Post(Flush);

    private void Flush()
    {
        string text;
        lock (_gate)
        {
            _flushQueued = false;
            if (_pending.Length == 0) return;
            text = _pending.ToString();
            _pending.Clear();
        }
        sink(text);
    }
}
