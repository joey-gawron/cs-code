using System.Diagnostics;
using System.Text;
using CsCode.Remote.Protocol;
using Xunit;

namespace CsCode.Remote.Tests;

public class RpcPeerTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    private sealed record Num(int Value);

    [Fact]
    public async Task Requests_round_trip_and_errors_come_back_as_the_same_exception_types()
    {
        using var pair = new PeerPair();
        pair.B.OnRequest<Num, Num>("double", (p, _) => ValueTask.FromResult(new Num(p.Value * 2)));
        pair.B.OnRequest<Num, Num>("missing", (_, _) => throw new FileNotFoundException("no such file"));
        pair.B.OnRequest<Num, Num>("denied", (_, _) => throw new UnauthorizedAccessException("nope"));
        pair.B.OnRequest<Num, Num>("boom", (_, _) => throw new InvalidOperationException("internal"));
        pair.Start();

        Assert.Equal(42, (await pair.A.InvokeAsync<Num, Num>("double", new Num(21), Ct))!.Value);
        await Assert.ThrowsAsync<FileNotFoundException>(() => pair.A.InvokeAsync<Num, Num>("missing", new Num(1), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pair.A.InvokeAsync<Num, Num>("denied", new Num(1), Ct));
        var internalError = await Assert.ThrowsAsync<RemoteException>(() => pair.A.InvokeAsync<Num, Num>("boom", new Num(1), Ct));
        Assert.Equal(RpcErrorCodes.InternalError, internalError.Code);
        var unknown = await Assert.ThrowsAsync<RemoteException>(() => pair.A.InvokeAsync<Num, Num>("nope", new Num(1), Ct));
        Assert.Equal(RpcErrorCodes.MethodNotFound, unknown.Code);
    }

    [Fact]
    public async Task Inline_notifications_are_applied_in_order_before_a_later_request_is_handled()
    {
        using var pair = new PeerPair(b: new RpcPeerOptions { Name = "B", HandlerThreads = 4 });
        var seen = new List<int>();
        pair.B.OnNotification<Num>("append", n => { lock (seen) seen.Add(n.Value); }, inline: true);
        pair.B.OnRequest<Num, Num>("count", (_, _) => { lock (seen) return ValueTask.FromResult(new Num(seen.Count)); });
        pair.Start();
        for (var i = 0; i < 2000; i++) pair.A.Notify("append", new Num(i));
        var count = await pair.A.InvokeAsync<Num, Num>("count", new Num(0), Ct);
        Assert.Equal(2000, count!.Value);
        lock (seen) Assert.Equal(Enumerable.Range(0, 2000), seen);
    }

    [Fact]
    public async Task Cancelling_a_call_completes_at_once_and_cancels_the_handler_on_the_other_end()
    {
        using var pair = new PeerPair();
        var handlerCancelled = new TaskCompletionSource();
        pair.B.OnRequest<Num, Num>("hang", async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { handlerCancelled.TrySetResult(); throw; }
            return new Num(0);
        });
        pair.Start();
        using var cts = new CancellationTokenSource();
        var call = pair.A.InvokeAsync<Num, Num>("hang", new Num(1), cts.Token);
        await Task.Delay(100, Ct);
        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"cancel took {sw.ElapsedMilliseconds} ms");
        await handlerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct); // $/cancelRequest reached the handler
        Assert.Equal(0, pair.A.PendingCalls);
    }

    [Fact]
    public async Task A_call_times_out_without_waiting_for_the_other_end()
    {
        using var pair = new PeerPair();
        pair.B.OnRequest<Num, Num>("slow", async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new Num(0); });
        pair.Start();
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => pair.A.InvokeAsync<Num, Num>("slow", new Num(1), Ct, TimeSpan.FromMilliseconds(200)));
        Assert.InRange(sw.ElapsedMilliseconds, 150, 5000);
        Assert.Equal(0, pair.A.PendingCalls);
    }

    [Fact]
    public async Task Closing_fails_every_pending_call_with_a_disconnect_and_later_calls_fail_fast()
    {
        using var pair = new PeerPair();
        pair.B.OnRequest<Num, Num>("hang", async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new Num(0); });
        pair.Start();
        var calls = Enumerable.Range(0, 20).Select(i => pair.A.InvokeAsync<Num, Num>("hang", new Num(i), Ct)).ToArray();
        await Task.Delay(100, Ct);
        pair.B.Close(); // the other end goes away: A's reader sees EOF
        foreach (var c in calls) await Assert.ThrowsAsync<RemoteDisconnectedException>(() => c);
        await pair.A.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(pair.A.IsClosed);
        await Assert.ThrowsAsync<RemoteDisconnectedException>(() => pair.A.InvokeAsync<Num, Num>("hang", new Num(0), Ct));
        Assert.Equal(0, pair.A.PendingCalls);
    }

    [Fact]
    public async Task A_message_larger_than_the_limit_closes_the_connection_instead_of_being_buffered()
    {
        var pipeIn = new MemoryPipe();
        var pipeOut = new MemoryPipe();
        using var peer = new RpcPeer(pipeIn.Reader, pipeOut.Writer, new RpcPeerOptions { MaxMessageBytes = 1024 });
        peer.Start();
        using var w = pipeIn.Writer;
        w.Write(Encoding.ASCII.GetBytes("Content-Length: 1000000\r\n\r\n"));
        var reason = await peer.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.IsType<InvalidDataException>(reason);
    }

    [Fact]
    public async Task Garbage_before_the_greeting_is_skipped_when_a_preamble_is_expected()
    {
        var pipeIn = new MemoryPipe();
        var pipeOut = new MemoryPipe();
        using var peer = new RpcPeer(pipeIn.Reader, pipeOut.Writer, new RpcPeerOptions { Preamble = RemoteProtocol.Greeting });
        var got = new TaskCompletionSource<int>();
        peer.OnNotification<Num>("n", n => got.TrySetResult(n.Value));
        peer.Start();
        var body = "{\"jsonrpc\":\"2.0\",\"method\":\"n\",\"params\":{\"value\":7}}";
        pipeIn.Writer.Write(Encoding.UTF8.GetBytes($"Welcome to box!\nLast login: yesterday\n{RemoteProtocol.Greeting}\nContent-Length: {body.Length}\r\n\r\n{body}"));
        Assert.Equal(7, await got.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Calls_cost_the_calling_thread_only_an_enqueue_even_when_the_other_end_is_stuck()
    {
        using var pair = new PeerPair();
        pair.B.OnRequest<Num, Num>("hang", async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new Num(0); });
        pair.Start();
        var payload = new Num(1);
        // warm up JIT
        for (var i = 0; i < 50; i++) _ = pair.A.InvokeAsync<Num, Num>("hang", payload, CancellationToken.None, Timeout.InfiniteTimeSpan);
        var sw = Stopwatch.StartNew();
        const int calls = 2000;
        var tasks = new Task[calls];
        var thread = Environment.CurrentManagedThreadId;
        for (var i = 0; i < calls; i++) tasks[i] = pair.A.InvokeAsync<Num, Num>("hang", payload, CancellationToken.None, Timeout.InfiniteTimeSpan);
        sw.Stop();
        Assert.Equal(thread, Environment.CurrentManagedThreadId);
        var perCall = sw.Elapsed.TotalMicroseconds / calls;
        Assert.True(perCall < 100, $"a call cost the caller {perCall:0.0} µs");
        Assert.All(tasks, t => Assert.False(t.IsCompleted));
    }

    [Fact]
    public async Task Overflowing_the_send_queue_closes_the_connection_instead_of_growing_or_blocking()
    {
        var pipeIn = new MemoryPipe();
        using var peer = new RpcPeer(pipeIn.Reader, new BlockingStream(), new RpcPeerOptions { MaxQueuedOutgoing = 100 });
        peer.Start();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) peer.Notify("n", new Num(i));
        Assert.True(sw.ElapsedMilliseconds < 2000);
        var reason = await peer.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Contains("not keeping up", reason!.Message);
    }

    /// <summary>A write end that never accepts data (a stalled network).</summary>
    private sealed class BlockingStream : Stream
    {
        private readonly ManualResetEventSlim _never = new(false);
        public override void Write(byte[] buffer, int offset, int count) => _never.Wait();
        public override void Write(ReadOnlySpan<byte> buffer) => _never.Wait();
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        protected override void Dispose(bool disposing) { _never.Set(); base.Dispose(disposing); }
    }

    [Fact]
    public async Task Gzip_payloads_round_trip_and_a_zip_bomb_chunk_is_refused()
    {
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello remote world ", 5000)));
        var p = Payload.Encode(text, allowCompression: true);
        Assert.True(p.Gzip);
        Assert.True(p.Data.Length < text.Length / 5);
        Assert.Equal(text, p.Decode());
        var random = new byte[4096];
        new Random(1).NextBytes(random);
        Assert.False(Payload.Encode(random, true).Gzip); // incompressible: sent raw
        var bomb = Payload.Encode(new byte[10 * 1024 * 1024], true);
        Assert.Throws<InvalidDataException>(() => bomb.Decode(1024 * 1024));
        await Task.CompletedTask;
    }
}
