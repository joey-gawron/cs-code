using System.Buffers;
using System.Text;

namespace CsCode.Dap.Transport;

public enum DapFrameStatus
{
    /// <summary>A complete message; <see cref="DapFrameReader.Payload"/> is valid until the next read.</summary>
    Frame,
    /// <summary>The announced length exceeded the cap; the body was skipped without buffering it (only a short prefix is kept).</summary>
    Oversized,
    /// <summary>The stream ended (cleanly between frames, or mid-frame).</summary>
    EndOfStream,
    /// <summary>Unparseable header block (no Content-Length, absurd header size); the connection can't be resynchronised.</summary>
    Malformed,
}

/// <summary>
/// Content-Length framing reader (the base protocol of DAP and LSP). Single consumer. The payload buffer is reused between
/// frames and grows only up to <c>maxFrameBytes</c>: a hostile/buggy adapter that announces a 2 GB message gets its body
/// skipped, never allocated — that is what keeps memory bounded when an adapter answers a variables request with millions of items.
/// </summary>
public sealed class DapFrameReader(Stream stream, int maxFrameBytes = 32 * 1024 * 1024)
{
    private const int InitialBuffer = 16 * 1024;
    private const int ShrinkAbove = 1024 * 1024;
    private const int MaxHeaderBytes = 8 * 1024;
    private const int PrefixBytes = 1024;
    private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();

    private byte[] _buffer = new byte[InitialBuffer];
    private int _start, _end;   // unread bytes live in [_start, _end)
    private int _payloadStart, _payloadLength;

    /// <summary>The last frame's body (valid until the next <see cref="ReadAsync"/>).</summary>
    public ReadOnlyMemory<byte> Payload => _buffer.AsMemory(_payloadStart, _payloadLength);

    /// <summary>For <see cref="DapFrameStatus.Oversized"/>: announced length of the skipped body.</summary>
    public long SkippedLength { get; private set; }

    /// <summary>For <see cref="DapFrameStatus.Oversized"/>: the first bytes of the skipped body (enough to find <c>request_seq</c>).</summary>
    public ReadOnlyMemory<byte> OversizedPrefix { get; private set; }

    public long TotalFrames { get; private set; }

    /// <summary>Current capacity of the reusable buffer (exposed so tests can assert it stays bounded).</summary>
    public int BufferCapacity => _buffer.Length;

    public async ValueTask<DapFrameStatus> ReadAsync(CancellationToken ct)
    {
        _payloadLength = 0;
        ShrinkIfOversized();

        // ---- header
        int headerEnd;
        while ((headerEnd = _buffer.AsSpan(_start, _end - _start).IndexOf(HeaderEnd)) < 0)
        {
            if (_end - _start > MaxHeaderBytes) return DapFrameStatus.Malformed;
            if (!await FillAsync(ct).ConfigureAwait(false)) return DapFrameStatus.EndOfStream;
        }

        var length = ParseContentLength(_buffer.AsSpan(_start, headerEnd));
        if (length < 0) return DapFrameStatus.Malformed;
        _start += headerEnd + HeaderEnd.Length; // header consumed

        if (length > maxFrameBytes)
        {
            SkippedLength = length;
            return await SkipBodyAsync(length, ct).ConfigureAwait(false);
        }

        // ---- body: make sure [_start, _start + length) fits in the buffer, then fill it
        if (_start + length > _buffer.Length)
        {
            var target = _buffer.Length;
            while (target < length) target *= 2;
            var have = _end - _start;
            if (target > _buffer.Length)
            {
                var next = new byte[target];
                Buffer.BlockCopy(_buffer, _start, next, 0, have);
                _buffer = next;
            }
            else
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, have);
            }
            _start = 0;
            _end = have;
        }
        while (_end - _start < length)
            if (!await FillAsync(ct).ConfigureAwait(false)) return DapFrameStatus.EndOfStream;

        _payloadStart = _start;
        _payloadLength = length;
        _start += length;
        if (_start == _end) _start = _end = 0;
        TotalFrames++;
        return DapFrameStatus.Frame;
    }

    /// <summary>A one-off 30 MB message must not pin 30 MB for the rest of the session: swap to a small buffer between frames.</summary>
    private void ShrinkIfOversized()
    {
        if (_buffer.Length <= ShrinkAbove || _end - _start > InitialBuffer) return;
        var small = new byte[InitialBuffer];
        var have = _end - _start;
        Buffer.BlockCopy(_buffer, _start, small, 0, have);
        _buffer = small;
        _start = 0;
        _end = have;
    }

    private async ValueTask<DapFrameStatus> SkipBodyAsync(int length, CancellationToken ct)
    {
        var prefix = new byte[Math.Min(PrefixBytes, length)];
        var prefixFilled = 0;
        var remaining = (long)length;
        while (remaining > 0)
        {
            if (_end == _start)
            {
                _start = _end = 0;
                if (!await FillAsync(ct).ConfigureAwait(false)) return DapFrameStatus.EndOfStream;
            }
            var take = (int)Math.Min(remaining, _end - _start);
            if (prefixFilled < prefix.Length)
            {
                var n = Math.Min(prefix.Length - prefixFilled, take);
                Buffer.BlockCopy(_buffer, _start, prefix, prefixFilled, n);
                prefixFilled += n;
            }
            _start += take;
            remaining -= take;
        }
        if (_start == _end) _start = _end = 0;
        OversizedPrefix = prefix;
        TotalFrames++;
        return DapFrameStatus.Oversized;
    }

    private async ValueTask<bool> FillAsync(CancellationToken ct)
    {
        if (_end == _buffer.Length)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            else
            {
                Array.Resize(ref _buffer, _buffer.Length * 2); // only reachable while scanning a (size-capped) header
            }
        }
        var n = await stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
        if (n <= 0) return false;
        _end += n;
        return true;
    }

    private static int ParseContentLength(ReadOnlySpan<byte> header)
    {
        var text = Encoding.ASCII.GetString(header);
        foreach (var line in text.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (!line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line.AsSpan(colon + 1).Trim();
            if (int.TryParse(value, out var len) && len >= 0) return len;
            return long.TryParse(value, out var big) && big > int.MaxValue ? int.MaxValue : -1;
        }
        return -1;
    }
}

public static class DapFrameWriter
{
    /// <summary>Writes header + payload as one write (adapters on pipes appreciate atomic-ish frames).</summary>
    public static async ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var rented = ArrayPool<byte>.Shared.Rent(payload.Length + 40);
        try
        {
            var total = Compose(rented, payload.Span);
            await stream.WriteAsync(rented.AsMemory(0, total), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int Compose(byte[] destination, ReadOnlySpan<byte> payload)
    {
        var written = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n", destination);
        payload.CopyTo(destination.AsSpan(written));
        return written + payload.Length;
    }
}
