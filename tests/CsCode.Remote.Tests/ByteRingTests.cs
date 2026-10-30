using CsCode.Remote.Protocol;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>The replay/resend buffer of channels: the newest bytes by absolute offset, growing on demand up to its capacity.</summary>
public class ByteRingTests
{
    [Fact]
    public void Holds_the_newest_bytes_at_their_offsets_while_growing_and_wrapping()
    {
        var random = new Random(7);
        var reference = new List<byte>();
        var ring = new ByteRing(capacity: 1000, initial: 16);
        for (var i = 0; i < 400; i++)
        {
            var chunk = new byte[random.Next(1, i % 50 == 0 ? 1500 : 90)];
            random.NextBytes(chunk);
            ring.Append(chunk);
            reference.AddRange(chunk);
            Assert.Equal(reference.Count, ring.End);
            Assert.Equal(Math.Max(0, reference.Count - ring.Allocated), ring.Start);
            Assert.True(ring.Allocated <= 1000);
            var held = (int)(ring.End - ring.Start);
            var from = ring.Start + random.Next(0, Math.Max(1, held));
            var copy = new byte[(int)(ring.End - from)];
            ring.CopyTo(from, copy);
            Assert.Equal(reference.Skip((int)from).ToArray(), copy);
        }
        Assert.Equal(1000, ring.Allocated);
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.CopyTo(ring.Start - 1, new byte[1]));
    }

    [Fact]
    public void A_quiet_stream_allocates_little_and_a_burst_keeps_only_the_capacity()
    {
        var ring = new ByteRing(capacity: 2 * 1024 * 1024, initial: 16 * 1024);
        ring.Append("$ ls\r\n"u8);
        Assert.Equal(16 * 1024, ring.Allocated); // not the 2 MB capacity
        var burst = new byte[5 * 1024 * 1024];
        new Random(1).NextBytes(burst);
        ring.Append(burst);
        Assert.Equal(2 * 1024 * 1024, ring.Allocated);
        Assert.Equal(6 + burst.Length, ring.End);
        var tail = new byte[1024];
        ring.CopyTo(ring.End - 1024, tail);
        Assert.Equal(burst[^1024..], tail);
    }
}
