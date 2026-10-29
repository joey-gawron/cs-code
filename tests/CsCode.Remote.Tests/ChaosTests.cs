using System.Diagnostics;
using CsCode.Core.Remote;
using CsCode.Remote.Client;
using CsCode.Remote.Transport;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>
/// Test wrapper for a link: adds latency per flushed batch (each direction), caps bandwidth, and can cut the link after a number of bytes
/// (a reset in the middle of a transfer).
/// </summary>
public sealed class ChaosStream(Stream inner, ChaosStream.Settings settings) : Stream
{
    public sealed class Settings
    {
        public TimeSpan Latency { get; set; }
        /// <summary>Bytes per second; 0 = unlimited.</summary>
        public long Bandwidth { get; set; }
        /// <summary>Cut the link once this many bytes went through (0 = never).</summary>
        public long CutAfterBytes { get; set; }
        public long Transferred;
        public bool Cut;
    }

    private void Account(int count)
    {
        if (settings.Cut) throw new IOException("chaos: link cut");
        var total = Interlocked.Add(ref settings.Transferred, count);
        if (settings.CutAfterBytes > 0 && total > settings.CutAfterBytes)
        {
            settings.Cut = true;
            inner.Dispose();
            throw new IOException("chaos: link cut");
        }
        if (settings.Bandwidth > 0) Thread.Sleep(TimeSpan.FromSeconds((double)count / settings.Bandwidth));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var n = inner.Read(buffer);
        if (n > 0)
        {
            Account(n);
            if (settings.Latency > TimeSpan.Zero) Thread.Sleep(settings.Latency); // the other direction's trip
        }
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    private bool _batchStarted;

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        // one batch (writes up to a flush) = one trip: delay before its first byte leaves (the writer thread is the link's own)
        if (!_batchStarted && settings.Latency > TimeSpan.Zero) Thread.Sleep(settings.Latency);
        _batchStarted = true;
        Account(buffer.Length);
        inner.Write(buffer);
    }

    public override void Flush()
    {
        _batchStarted = false;
        inner.Flush();
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }

    /// <summary>A transport whose channels pass through chaos streams (one transport is created per connection attempt).</summary>
    public sealed class Transport(IRemoteTransport inner, Func<Settings> settings) : IRemoteTransport
    {
        public string Description => "chaos(" + inner.Description + ")";

        public async Task<RemoteChannel> ConnectAsync(IProgress<string>? progress, CancellationToken ct)
        {
            var s = settings();
            var channel = await inner.ConnectAsync(progress, ct).ConfigureAwait(false);
            return channel.Wrap(i => new ChaosStream(i, s), o => new ChaosStream(o, s));
        }
    }
}

public class ChaosTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    [Fact]
    public async Task A_slow_link_slows_answers_but_never_the_caller()
    {
        var settings = new ChaosStream.Settings { Latency = TimeSpan.FromMilliseconds(250), Bandwidth = 4 * 1024 * 1024 }; // 500 ms round trip, 4 MB/s
        await using var host = new RemoteTestHost(wrap: t => new ChaosStream.Transport(t, () => settings));
        await host.ConnectAsync();
        File.WriteAllText(Path.Combine(host.Workspace.Path, "a.txt"), "a");
        var fs = host.FileSystem;
        var uri = host.Remote("a.txt");

        var sw = Stopwatch.StartNew();
        Assert.NotNull(await fs.StatAsync(uri, Ct));
        Assert.True(sw.ElapsedMilliseconds >= 400, $"a round trip took only {sw.ElapsedMilliseconds} ms: the latency was not applied");

        // many calls in flight: each costs the caller an enqueue; they pipeline instead of queueing one round trip each
        var pending = new List<Task>();
        var calls = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++) pending.Add(fs.StatAsync(uri, Ct).AsTask());
        calls.Stop();
        Assert.True(calls.Elapsed.TotalMilliseconds / 200 < 0.1, $"{calls.Elapsed.TotalMilliseconds / 200 * 1000:0.0} us per call on the caller");
        var all = Stopwatch.StartNew();
        await Task.WhenAll(pending);
        Assert.True(all.Elapsed < TimeSpan.FromSeconds(20), $"200 pipelined calls took {all.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public async Task A_link_cut_in_the_middle_of_a_transfer_fails_that_call_then_reconnects()
    {
        var big = new byte[2 * 1024 * 1024];
        new Random(3).NextBytes(big);
        var attempts = 0;
        await using var host = new RemoteTestHost(wrap: t => new ChaosStream.Transport(t, () => new ChaosStream.Settings { CutAfterBytes = Interlocked.Increment(ref attempts) == 1 ? 600_000 : 0 }));
        File.WriteAllBytes(Path.Combine(host.Workspace.Path, "big.bin"), big);
        await host.ConnectAsync();
        var uri = host.Remote("big.bin");

        var ex = await Assert.ThrowsAnyAsync<IOException>(async () => await host.FileSystem.ReadFileAsync(uri, Ct));
        Assert.IsType<CsCode.Remote.Protocol.RemoteDisconnectedException>(ex);
        await TestEnv.WaitUntilAsync(() => host.Manager.Status.State == RemoteConnectionState.Connected && host.Manager.Session is { IsOpen: true }, TimeSpan.FromSeconds(30), "reconnected");
        Assert.Equal(big, await host.FileSystem.ReadFileAsync(uri, Ct)); // the new link carries it whole
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_hung_server_times_calls_out_and_answers_again_when_it_resumes()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/kill"), "needs SIGSTOP/SIGCONT");
        await using var host = new RemoteTestHost(o => o.CallTimeout = TimeSpan.FromSeconds(2));
        await host.ConnectAsync();
        File.WriteAllText(Path.Combine(host.Workspace.Path, "a.txt"), "a");
        var pid = host.Manager.Session!.Server.ProcessId; // our own child process
        Signal("STOP", pid);
        try
        {
            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(async () => await host.FileSystem.StatAsync(host.Remote("a.txt"), Ct));
            Assert.InRange(sw.Elapsed.TotalSeconds, 1.5, 10);
        }
        finally { Signal("CONT", pid); }
        Assert.NotNull(await host.FileSystem.StatAsync(host.Remote("a.txt"), Ct));
        Assert.Equal(RemoteConnectionState.Connected, host.Manager.Status.State);
    }

    private static void Signal(string signal, int pid)
    {
        using var p = Process.Start(new ProcessStartInfo("/bin/kill", ["-" + signal, pid.ToString(System.Globalization.CultureInfo.InvariantCulture)]) { UseShellExecute = false })!;
        p.WaitForExit();
    }
}
