using System.Text;
using CsCode.Dap.Tests.Fakes;
using CsCode.Dap.Transport;
using Xunit;

namespace CsCode.Dap.Tests;

public class FramingTests
{
    private static byte[] Frame(string json) => Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}");

    private static async Task<DapFrameReader> ReaderOverAsync(params byte[][] chunks)
    {
        var stream = new ByteChannelStream();
        foreach (var c in chunks) await stream.WriteAsync(c);
        stream.CompleteWriting();
        return new DapFrameReader(stream);
    }

    [Fact]
    public async Task Reads_a_single_frame()
    {
        var reader = await ReaderOverAsync(Frame("""{"seq":1}"""));
        Assert.Equal(DapFrameStatus.Frame, await reader.ReadAsync(Ct));
        Assert.Equal("""{"seq":1}""", Encoding.UTF8.GetString(reader.Payload.Span));
        Assert.Equal(DapFrameStatus.EndOfStream, await reader.ReadAsync(Ct));
    }

    [Fact]
    public async Task Reads_several_frames_arriving_in_one_chunk()
    {
        var all = Frame("""{"a":1}""").Concat(Frame("""{"b":2}""")).Concat(Frame("""{"c":3}""")).ToArray();
        var reader = await ReaderOverAsync(all);
        var seen = new List<string>();
        while (await reader.ReadAsync(Ct) == DapFrameStatus.Frame) seen.Add(Encoding.UTF8.GetString(reader.Payload.Span));
        Assert.Equal(["""{"a":1}""", """{"b":2}""", """{"c":3}"""], seen);
    }

    [Fact]
    public async Task Reassembles_a_frame_split_at_every_byte()
    {
        var bytes = Frame("""{"seq":7,"type":"event","event":"x"}""");
        var reader = await ReaderOverAsync(bytes.Select(b => new[] { b }).ToArray());
        Assert.Equal(DapFrameStatus.Frame, await reader.ReadAsync(Ct));
        Assert.Contains("\"seq\":7", Encoding.UTF8.GetString(reader.Payload.Span));
    }

    [Fact]
    public async Task Counts_content_length_in_bytes_not_characters()
    {
        var reader = await ReaderOverAsync(Frame("""{"t":"héllo → 日本"}"""));
        Assert.Equal(DapFrameStatus.Frame, await reader.ReadAsync(Ct));
        Assert.Contains("日本", Encoding.UTF8.GetString(reader.Payload.Span));
    }

    [Fact]
    public async Task Tolerates_extra_headers_and_header_case()
    {
        var json = """{"x":1}""";
        var bytes = Encoding.ASCII.GetBytes($"content-type: application/vscode-jsonrpc\r\nCONTENT-LENGTH: {json.Length}\r\n\r\n{json}");
        var reader = await ReaderOverAsync(bytes);
        Assert.Equal(DapFrameStatus.Frame, await reader.ReadAsync(Ct));
    }

    [Fact]
    public async Task Missing_content_length_is_malformed()
    {
        var reader = await ReaderOverAsync(Encoding.ASCII.GetBytes("Foo: bar\r\n\r\n{}"));
        Assert.Equal(DapFrameStatus.Malformed, await reader.ReadAsync(Ct));
    }

    [Fact]
    public async Task Endless_header_is_malformed_not_an_oom()
    {
        var reader = await ReaderOverAsync(new byte[64 * 1024]);
        Assert.Equal(DapFrameStatus.Malformed, await reader.ReadAsync(Ct));
    }

    [Fact]
    public async Task Truncated_body_is_end_of_stream()
    {
        var bytes = Frame("""{"seq":1,"padding":"xxxxxxxxxx"}""");
        var reader = await ReaderOverAsync(bytes[..^5]);
        Assert.Equal(DapFrameStatus.EndOfStream, await reader.ReadAsync(Ct));
    }

    [Fact]
    public async Task Oversized_frames_are_skipped_without_buffering_and_the_stream_stays_in_sync()
    {
        var stream = new ByteChannelStream(capacity: 4096);
        var reader = new DapFrameReader(stream, maxFrameBytes: 100_000);
        const int huge = 5_000_000;
        var writer = Task.Run(async () =>
        {
            var prefix = """{"seq":9,"type":"response","request_seq":42,"success":true,"command":"variables","body":{"variables":[""";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {huge}\r\n\r\n"));
            await stream.WriteAsync(Encoding.UTF8.GetBytes(prefix));
            var remaining = huge - Encoding.UTF8.GetByteCount(prefix);
            var filler = new byte[64 * 1024];
            Array.Fill(filler, (byte)'x');
            while (remaining > 0) { var n = Math.Min(remaining, filler.Length); await stream.WriteAsync(filler.AsMemory(0, n)); remaining -= n; }
            await stream.WriteAsync(Frame("""{"after":true}"""));
            stream.CompleteWriting();
        }, Ct);

        Assert.Equal(DapFrameStatus.Oversized, await reader.ReadAsync(Ct));
        Assert.Equal(huge, reader.SkippedLength);
        Assert.Contains("\"request_seq\":42", Encoding.UTF8.GetString(reader.OversizedPrefix.Span));
        Assert.True(reader.BufferCapacity < 1_000_000, $"buffer grew to {reader.BufferCapacity}");
        Assert.Equal(DapFrameStatus.Frame, await reader.ReadAsync(Ct));
        Assert.Equal("""{"after":true}""", Encoding.UTF8.GetString(reader.Payload.Span));
        await writer;
    }

    [Fact]
    public async Task Buffer_shrinks_back_after_one_large_frame()
    {
        var big = new string('a', 3_000_000);
        var reader = await ReaderOverAsync(Frame($$"""{"d":"{{big}}"}"""), Frame("""{"small":1}"""), Frame("""{"small":2}"""));
        Assert.Equal(DapFrameStatus.Frame, await reader.ReadAsync(Ct));
        Assert.True(reader.BufferCapacity >= 3_000_000);
        Assert.Equal(DapFrameStatus.Frame, await reader.ReadAsync(Ct));
        Assert.True(reader.BufferCapacity < 1_000_000, $"capacity still {reader.BufferCapacity}");
    }

    [Fact]
    public async Task Writer_and_reader_round_trip()
    {
        var stream = new ByteChannelStream();
        await DapFrameWriter.WriteAsync(stream, Encoding.UTF8.GetBytes("""{"hello":"wörld"}"""), Ct);
        stream.CompleteWriting();
        var reader = new DapFrameReader(stream);
        Assert.Equal(DapFrameStatus.Frame, await reader.ReadAsync(Ct));
        Assert.Equal("""{"hello":"wörld"}""", Encoding.UTF8.GetString(reader.Payload.Span));
    }
}
