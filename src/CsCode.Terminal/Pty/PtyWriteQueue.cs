namespace CsCode.Terminal.Pty;

/// <summary>
/// The bounded queue between callers (the UI thread typing, a paste, the debugger) and a PTY host's writer thread. Enqueueing copies the
/// bytes and never waits; when <see cref="Capacity"/> bytes are already queued (the program stopped reading its input) the write is refused
/// instead of growing memory or blocking. Urgent writes (an interrupt) go to the front so they overtake a stuck paste.
/// </summary>
internal sealed class PtyWriteQueue(long capacity)
{
    private readonly object _gate = new();
    private readonly LinkedList<Segment> _segments = new();
    private long _pending;
    private bool _closed;

    private sealed class Segment(byte[] data)
    {
        public readonly byte[] Data = data;
        public int Offset;
    }

    public long Capacity { get; } = capacity;

    public long Pending => Interlocked.Read(ref _pending);

    public bool IsEmpty { get { lock (_gate) return _segments.Count == 0; } }

    /// <summary>Queues a copy of <paramref name="data"/>; false when it does not fit or the queue was closed.</summary>
    public bool Enqueue(ReadOnlySpan<byte> data, bool urgent)
    {
        if (data.IsEmpty) return true;
        lock (_gate)
        {
            if (_closed) return false;
            // An urgent write (a few bytes) is always accepted: it is how the user gets a program that ignores its input to stop.
            if (!urgent && _pending + data.Length > Capacity) return false;
            var segment = new Segment(data.ToArray());
            if (urgent) _segments.AddFirst(segment); else _segments.AddLast(segment);
            Interlocked.Add(ref _pending, data.Length);
            return true;
        }
    }

    /// <summary>
    /// Writer thread: the next bytes to write (empty when nothing is queued), with a token for <see cref="Advance"/> (an urgent write may
    /// be queued in front of it meanwhile, so the token says which segment the written bytes came from).
    /// </summary>
    public ReadOnlyMemory<byte> Peek(out object? token)
    {
        lock (_gate)
        {
            token = null;
            if (_segments.First is not { } first) return ReadOnlyMemory<byte>.Empty;
            var s = first.Value;
            token = first;
            return s.Data.AsMemory(s.Offset);
        }
    }

    public void Advance(object? token, int written)
    {
        if (written <= 0 || token is not LinkedListNode<Segment> node) return;
        lock (_gate)
        {
            if (node.List is null) return; // closed meanwhile
            var s = node.Value;
            s.Offset += written;
            if (s.Offset >= s.Data.Length) _segments.Remove(node);
            Interlocked.Add(ref _pending, -written);
        }
    }

    /// <summary>Drops everything and refuses further writes (the program is gone).</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _segments.Clear();
            Interlocked.Exchange(ref _pending, 0);
        }
    }
}
