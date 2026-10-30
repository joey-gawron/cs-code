namespace CsCode.Remote.Protocol;

/// <summary>
/// The newest bytes of a stream whose bytes are numbered by absolute offset, kept up to <see cref="Capacity"/> (channel replay/resend buffers). The
/// buffer grows on demand, so a quiet terminal costs a few kilobytes, not the capacity. Not thread-safe: the owner locks.
/// </summary>
public sealed class ByteRing(int capacity, int initial = 4096)
{
    private byte[] _buffer = [];

    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    /// <summary>Total bytes appended (the offset after the last one).</summary>
    public long End { get; private set; }

    /// <summary>The oldest offset still held.</summary>
    public long Start => End - Math.Min(End, _buffer.Length);

    /// <summary>Bytes allocated now (diagnostics/tests).</summary>
    public int Allocated => _buffer.Length;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        var held = End - Start;
        if (_buffer.Length < Capacity && held + data.Length > _buffer.Length)
        {
            var size = (int)Math.Min(Capacity, Math.Max(Math.Max(initial, (long)_buffer.Length * 2), held + data.Length));
            var bigger = new byte[size];
            for (var pos = Start; pos < End;) // re-place what is held at its offsets in the bigger buffer
            {
                var from = (int)(pos % _buffer.Length);
                var n = (int)Math.Min(End - pos, _buffer.Length - from);
                var to = (int)(pos % size);
                n = Math.Min(n, size - to);
                _buffer.AsSpan(from, n).CopyTo(bigger.AsSpan(to));
                pos += n;
            }
            _buffer = bigger;
        }
        var length = data.Length;
        var offset = End;
        if (data.Length > _buffer.Length)
        {
            offset += data.Length - _buffer.Length;
            data = data[(data.Length - _buffer.Length)..];
        }
        var start = (int)(offset % _buffer.Length);
        var first = Math.Min(data.Length, _buffer.Length - start);
        data[..first].CopyTo(_buffer.AsSpan(start));
        if (first < data.Length) data[first..].CopyTo(_buffer);
        End += length;
    }

    /// <summary>Copies the held bytes at [<paramref name="from"/>, from + destination length) (which must lie within [Start, End)).</summary>
    public void CopyTo(long from, Span<byte> destination)
    {
        if (from < Start || from + destination.Length > End) throw new ArgumentOutOfRangeException(nameof(from));
        var done = 0;
        while (done < destination.Length)
        {
            var at = (int)((from + done) % _buffer.Length);
            var n = Math.Min(destination.Length - done, _buffer.Length - at);
            _buffer.AsSpan(at, n).CopyTo(destination[done..]);
            done += n;
        }
    }
}
