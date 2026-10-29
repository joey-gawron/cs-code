using System.Text;

namespace CsCode.Lsp.Runtime;

/// <summary>A server announced a message larger than the configured maximum; the connection is dropped instead of buffering it.</summary>
public sealed class MessageTooLargeException(long announced, long limit)
    : IOException($"language server announced a {announced:N0}-byte message (limit {limit:N0}); connection dropped")
{
    public long Announced { get; } = announced;
}

/// <summary>
/// Read-side guard in front of the JSON-RPC parser. It follows the LSP base-protocol framing (<c>Content-Length: n\r\n\r\n</c> + n bytes) while
/// bytes pass through untouched, and fails the read the moment a header announces more than <c>maxMessageBytes</c>. JSON parsing rents buffers of
/// roughly 9x the message size from shared pools, so one hostile multi-hundred-megabyte message would otherwise balloon the process; this bounds
/// the cost per message. Scanning is O(bytes) with no allocation except one small header buffer.
/// </summary>
internal sealed class FramingGuardStream(Stream inner, long maxMessageBytes) : Stream
{
    private readonly byte[] _header = new byte[1024];
    private int _headerLength;
    private long _bodyRemaining;
    private bool _inBody;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (n > 0) Scan(buffer.Span[..n]);
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        if (n > 0) Scan(buffer.AsSpan(offset, n));
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Scan(ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length)
        {
            if (_inBody)
            {
                var skip = (int)Math.Min(_bodyRemaining, data.Length - i);
                _bodyRemaining -= skip;
                i += skip;
                if (_bodyRemaining == 0) { _inBody = false; _headerLength = 0; }
                continue;
            }

            var b = data[i++];
            if (_headerLength == _header.Length) { _headerLength = 0; continue; } // garbage / absurd header: stay robust, the parser reports the protocol error
            _header[_headerLength++] = b;
            if (b != '\n' || _headerLength < 4 || _header[_headerLength - 2] != '\r' || _header[_headerLength - 3] != '\n' || _header[_headerLength - 4] != '\r') continue;

            var length = ParseContentLength(_header.AsSpan(0, _headerLength));
            _headerLength = 0;
            if (length > maxMessageBytes) throw new MessageTooLargeException(length, maxMessageBytes);
            if (length > 0) { _inBody = true; _bodyRemaining = length; }
        }
    }

    private static long ParseContentLength(ReadOnlySpan<byte> header)
    {
        var text = Encoding.ASCII.GetString(header);
        foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && long.TryParse(line.AsSpan(15).Trim(), out var n))
                return n;
        return 0;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
