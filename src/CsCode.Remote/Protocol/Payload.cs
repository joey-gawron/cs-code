using System.IO.Compression;

namespace CsCode.Remote.Protocol;

/// <summary>
/// A chunk of file bytes on the wire (base64 in JSON), gzip-compressed when both ends advertised <c>gzip</c> and it pays off (text usually
/// shrinks 3-5×, which more than wins back base64's third over a slow link). Chunks are at most <see cref="ChunkSize"/> bytes uncompressed.
/// </summary>
public sealed record Payload(byte[] Data, bool Gzip = false)
{
    /// <summary>Bytes per chunk of a large read or write (the brief's 256 KB).</summary>
    public const int ChunkSize = 256 * 1024;

    private const int MinCompressBytes = 1024;

    public static Payload Encode(ReadOnlySpan<byte> bytes, bool allowCompression)
    {
        if (allowCompression && bytes.Length >= MinCompressBytes)
        {
            using var ms = new MemoryStream(Math.Min(bytes.Length / 4, 16 * 1024)); // grows as needed: never a large-object allocation up front
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(bytes);
            if (ms.Length < bytes.Length * 9L / 10) return new Payload(ms.ToArray(), true);
        }
        return new Payload(bytes.ToArray());
    }

    /// <summary>The uncompressed bytes; <paramref name="maxBytes"/> bounds a hostile "zip bomb" chunk.</summary>
    public byte[] Decode(int maxBytes = ChunkSize * 2)
    {
        if (!Gzip) return Data;
        using var input = new GZipStream(new MemoryStream(Data, writable: false), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = input.Read(buffer)) > 0)
        {
            if (output.Length + n > maxBytes) throw new InvalidDataException($"A compressed chunk expands beyond {maxBytes} bytes");
            output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }
}
