using CsCode.Lsp.Client;
using CsCode.Lsp.Features;
using CsCode.Lsp.Runtime;
using Xunit;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Tests;

/// <summary>The small bounded building blocks, tested directly: mailboxes, rings, the circuit breaker, caches, position encodings.</summary>
public class BoundedStructuresTests
{
    [Fact]
    public void Latest_per_key_mailbox_keeps_only_the_newest_value_per_key_and_is_capped()
    {
        var box = new LatestPerKeyMailbox<string, int>(capacity: 100);
        for (var i = 0; i < 10_000; i++) box.Put("k" + i, i);
        Assert.Equal(100, box.Count);
        Assert.Equal(9_900, box.Dropped);

        box.Put("k9999", -1); // same key again: replaces, does not grow
        Assert.Equal(100, box.Count);
        var drained = box.Drain();
        Assert.Equal(100, drained.Count);
        Assert.Equal(-1, drained.Single(x => x.Key == "k9999").Value);
        Assert.Equal(0, box.Count);
        Assert.False(box.HasItems);
    }

    [Fact]
    public void Latest_per_key_mailbox_supports_partial_drains_in_arrival_order()
    {
        var box = new LatestPerKeyMailbox<int, string>(10);
        for (var i = 0; i < 5; i++) box.Put(i, "v" + i);
        box.Put(2, "v2b");
        var first = box.Drain(2);
        Assert.Equal([0, 1], first.Select(x => x.Key));
        var rest = box.Drain();
        Assert.Equal([2, 3, 4], rest.Select(x => x.Key));
        Assert.Equal("v2b", rest[0].Value);
    }

    [Fact]
    public void Bounded_fifo_drops_the_oldest_and_counts()
    {
        var q = new BoundedFifo<int>(10);
        for (var i = 0; i < 25; i++) q.Enqueue(i);
        Assert.Equal(15, q.Dropped);
        Assert.Equal(Enumerable.Range(15, 10), q.Drain(100));
        Assert.False(q.HasItems);
    }

    [Fact]
    public void Line_ring_keeps_the_last_lines_and_truncates_long_ones()
    {
        var ring = new LineRing(maxLines: 3, maxLineChars: 10);
        for (var i = 0; i < 10; i++) ring.Add("line" + i);
        ring.Add(new string('z', 50));
        Assert.Equal(11, ring.TotalLines);
        var lines = ring.Snapshot();
        Assert.Equal(3, lines.Length);
        Assert.Equal("line8", lines[0]);
        Assert.StartsWith("zzzzzzzzzz", lines[2]);
        Assert.True(lines[2].Length <= 11);
        Assert.True(ring.Tail(5).Length <= 5);
    }

    [Fact]
    public void Circuit_breaker_opens_after_n_timeouts_probes_once_per_interval_and_closes_on_success()
    {
        var breaker = new CircuitBreaker(threshold: 3, probeInterval: TimeSpan.FromMilliseconds(100));
        var transitions = new List<ServerHealth>();
        breaker.HealthChanged += transitions.Add;
        Assert.True(breaker.AllowRequest());
        breaker.RecordTimeout();
        breaker.RecordTimeout();
        breaker.RecordSuccess(); // a success resets the streak
        breaker.RecordTimeout();
        breaker.RecordTimeout();
        Assert.Equal(ServerHealth.Healthy, breaker.Health);
        breaker.RecordTimeout();
        Assert.Equal(ServerHealth.Unresponsive, breaker.Health);
        Assert.Equal([ServerHealth.Unresponsive], transitions);

        Assert.False(breaker.AllowRequest());           // open: nothing gets through...
        Assert.True(breaker.AllowRequest(essential: true)); // ...except essential requests (shutdown)
        Thread.Sleep(150);
        Assert.True(breaker.AllowRequest());            // one probe
        Assert.False(breaker.AllowRequest());           // and only one per interval
        breaker.RecordSuccess();
        Assert.Equal(ServerHealth.Healthy, breaker.Health);
        Assert.Equal([ServerHealth.Unresponsive, ServerHealth.Healthy], transitions);
        Assert.True(breaker.AllowRequest());
    }

    [Fact]
    public void Semantic_token_cache_is_bounded_by_documents_and_bytes_and_trims()
    {
        var cache = new SemanticTokenCache();
        for (var i = 0; i < 100; i++) cache.Put(new Uri("file:///d" + i), "r", new uint[1000]);
        Assert.True(cache.Count <= 32);
        cache.Put(new Uri("file:///huge"), "r", new uint[9_000_000]); // 36 MB > 32 MB cap
        Assert.True(cache.EstimatedBytes <= 32L * 1024 * 1024 + 36_000_000, "the single oversized entry may stay, everything else is evicted");
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(new Uri("file:///huge"), out var id, out var data));
        Assert.Equal(("r", 9_000_000), (id, data.Length));
        Assert.True(cache.Trim(CsCode.Core.Diagnostics.MemoryTrimLevel.Aggressive) > 0);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Semantic_token_edits_apply_against_the_original_array()
    {
        uint[] orig = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Assert.True(SemanticTokenCache.ApplyEdits(orig, [new P.SemanticTokensEdit { Start = 2, DeleteCount = 3, Data = [90, 91] }, new P.SemanticTokensEdit { Start = 8, DeleteCount = 0, Data = [100] }], out var r));
        Assert.Equal([1u, 2, 90, 91, 6, 7, 8, 100, 9, 10], r); // an insertion at index 8 lands BEFORE the original element 8 (value 9)
        Assert.True(SemanticTokenCache.ApplyEdits(orig, [], out var same));
        Assert.Equal(orig, same);
        Assert.False(SemanticTokenCache.ApplyEdits(orig, [new P.SemanticTokensEdit { Start = 8, DeleteCount = 5 }], out _)); // out of range
        Assert.False(SemanticTokenCache.ApplyEdits(orig, [new P.SemanticTokensEdit { Start = 3, DeleteCount = 4 }, new P.SemanticTokensEdit { Start = 5, DeleteCount = 1 }], out _)); // overlapping
    }

    [Theory]
    [InlineData("hello", 3, 3, 3)]                       // ASCII: all encodings agree
    [InlineData("héllo", 3, 4, 3)]                       // é = 2 UTF-8 bytes, 1 UTF-32 code point
    [InlineData("a😀b", 3, 5, 2)]                        // 😀 = surrogate pair (2 UTF-16 units), 4 UTF-8 bytes, 1 code point; col 3 is after it
    [InlineData("日本語", 2, 6, 2)]                      // 3 UTF-8 bytes each
    public void Position_encodings_convert_in_both_directions(string line, int utf16Column, int utf8Column, int utf32Column)
    {
        Assert.Equal(utf16Column, PositionEncoder.Utf16.ToLsp(line, utf16Column));
        Assert.Equal(utf8Column, PositionEncoder.Utf8.ToLsp(line, utf16Column));
        Assert.Equal(utf32Column, PositionEncoder.Utf32.ToLsp(line, utf16Column));
        Assert.Equal(utf16Column, PositionEncoder.Utf8.FromLsp(line, utf8Column));
        Assert.Equal(utf16Column, PositionEncoder.Utf32.FromLsp(line, utf32Column));
        Assert.Equal(utf16Column, PositionEncoder.Utf16.FromLsp(line, utf16Column));
    }

    [Fact]
    public void Position_columns_past_the_end_of_the_line_clamp_as_the_spec_requires()
    {
        Assert.Equal(5, PositionEncoder.Utf8.FromLsp("hello", 99));
        Assert.Equal(5, PositionEncoder.Utf32.FromLsp("hello", 99));
        Assert.Equal(0, PositionEncoder.Utf8.FromLsp("", 3));
        Assert.Equal(0, PositionEncoder.Utf8.ToLsp("", 0));
        Assert.Equal(PositionEncodingMode.Utf8, PositionEncoder.FromName("utf-8").Kind);
        Assert.Equal(PositionEncodingMode.Utf16, PositionEncoder.FromName("anything-else").Kind);
    }

    [Fact]
    public void Mapper_converts_non_utf16_positions_using_the_line_text_of_open_documents()
    {
        var lines = new Dictionary<int, string> { [0] = "a😀b", [1] = "ünï" };
        var mapper = new LspMapper(PositionEncoder.Utf8, (_, line) => lines.GetValueOrDefault(line));
        var uri = new Uri("file:///x");
        // UTF-16 column 3 (after the emoji) is UTF-8 byte 5; line 1: after "ü" (2 bytes)
        Assert.Equal(5, mapper.ToLsp(new CsCode.Core.Text.TextPosition(0, 3), uri).Character);
        Assert.Equal(new CsCode.Core.Text.TextPosition(0, 3), mapper.ToCore(new P.Position { Line = 0, Character = 5 }, uri));
        Assert.Equal(new CsCode.Core.Text.TextPosition(1, 1), mapper.ToCore(new P.Position { Line = 1, Character = 2 }, uri));
        // closed documents (no line text available) keep the server's columns
        Assert.Equal(7, mapper.ToCore(new P.Position { Line = 9, Character = 7 }, uri).Character);
    }

    [Fact]
    public void Utf16_mapper_never_asks_for_line_text()
    {
        var asked = 0;
        var mapper = new LspMapper(PositionEncoder.Utf16, (_, _) => { asked++; return "x"; });
        mapper.ToLsp(new CsCode.Core.Text.TextPosition(1, 2), new Uri("file:///x"));
        mapper.ToCore(new P.Position { Line = 1, Character = 2 }, new Uri("file:///x"));
        Assert.Equal(0, asked); // identity: zero cost, no document access
    }
}
