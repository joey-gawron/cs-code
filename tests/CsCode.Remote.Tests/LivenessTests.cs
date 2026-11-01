using System.Text;
using CsCode.Core.Remote;
using CsCode.Remote.Client;
using CsCode.Remote.Install;
using CsCode.Remote.Transport;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>
/// A link that goes SILENT, as a network that stops carrying packets does: no process dies and no stream ends, the bytes just stop. While
/// frozen, what the server sends is held back and what the client sends is dropped. <see cref="Control.Freeze"/> freezes the current connection
/// only (a reconnect gets a working one); <see cref="Control.Thaw"/> lets the old one end.
/// </summary>
public sealed class FreezableTransport(IRemoteTransport inner, FreezableTransport.Control control) : IRemoteTransport
{
    public sealed class Control
    {
        public int Connects;
        private readonly List<Gate> _gates = new();
        internal Gate Add() { var g = new Gate(); lock (_gates) _gates.Add(g); return g; }
        public void Freeze() { lock (_gates) _gates[^1].Frozen = true; }
        public void Thaw() { lock (_gates) foreach (var g in _gates) g.Frozen = false; }
    }

    internal sealed class Gate
    {
        public volatile bool Frozen;
    }

    private sealed class GateStream(Stream inner, Gate gate, bool reading) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            var deadline = Environment.TickCount64 + 120_000;
            while (reading && gate.Frozen && Environment.TickCount64 < deadline) Thread.Sleep(20); // held back: the client hears nothing
            return n;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { if (!gate.Frozen) inner.Write(buffer); } // dropped on the floor
        public override void Flush() { if (!gate.Frozen) inner.Flush(); }
        public override bool CanRead => reading;
        public override bool CanWrite => !reading;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    public string Description => "freezable(" + inner.Description + ")";

    public async Task<RemoteChannel> ConnectAsync(IProgress<string>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref control.Connects);
        var channel = await inner.ConnectAsync(progress, ct).ConfigureAwait(false);
        var gate = control.Add();
        return channel.Wrap(i => new GateStream(i, gate, reading: true), o => new GateStream(o, gate, reading: false));
    }
}

/// <summary>
/// The client's liveness check (found against a real host: a session multiplexed over a user's ssh ControlMaster whose link died silently stayed
/// "connected" with every call timing out, because ssh's keep-alive belonged to the master's owner) and the start timeout that tells a hung
/// connection attempt from a refused one.
/// </summary>
public sealed class LivenessTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    [Fact]
    public async Task A_link_that_goes_silent_is_treated_as_lost_and_the_session_resumes_on_a_new_connection()
    {
        var control = new FreezableTransport.Control();
        await using var host = new RemoteTestHost(o => o.Heartbeat = new HeartbeatPolicy(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2)),
            wrap: t => new FreezableTransport(t, control), persistent: TimeSpan.FromSeconds(60));
        await host.ConnectAsync();
        File.WriteAllText(Path.Combine(host.Workspace.Path, "a.txt"), "before");
        var first = host.Manager.Session!;
        try
        {
            control.Freeze(); // nothing ends: only silence
            await TestEnv.WaitUntilAsync(() => control.Connects >= 2 && host.Manager.Session is { IsOpen: true } s && !ReferenceEquals(s, first) && host.Manager.Status.IsConnected,
                TimeSpan.FromSeconds(30), "the client to notice the silent link and reconnect\n" + host.Dump());
        }
        finally { control.Thaw(); }
        Assert.Equal(first.Server.InstanceId, host.Manager.Session!.Server.InstanceId); // the same persistent server: resumed
        Assert.True(host.Manager.Session.Resumed);
        Assert.Contains("No answer from " + RemoteTestHost.Host, host.Output.Text(RemoteConnectionManager.OutputChannel));
        Assert.Equal("before", RemoteTestHost.Text(await host.FileSystem.ReadFileAsync(host.Remote("a.txt"), Ct)));
    }

    [Fact]
    public async Task Closing_a_peer_whose_process_is_alive_and_silent_returns_at_once()
    {
        // The heartbeat closes the peer of a silent link; on Unix disposing a process pipe with a read in flight waited for that read, i.e. for
        // ever, so Close hung on the heartbeat's thread and the reconnect never started (found against a real host).
        Assert.SkipWhen(OperatingSystem.IsWindows() || !File.Exists("/bin/sleep"), "a Unix process pipe");
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sleep", ["30"])
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true })!;
        try
        {
            var peer = new Protocol.RpcPeer(p.StandardOutput.BaseStream, p.StandardInput.BaseStream, new Protocol.RpcPeerOptions { Name = "silent" });
            peer.Start();
            await Task.Delay(300, Ct); // the reader is in its read now: sleep writes nothing and keeps the pipe open
            var closing = Task.Run(() => peer.Close(new IOException("no answer (test)")), Ct);
            Assert.Same(closing, await Task.WhenAny(closing, Task.Delay(5000, Ct)));
            Assert.True(peer.IsClosed);
        }
        finally { p.Kill(); }
    }

    [Fact]
    public async Task A_quiet_link_that_still_answers_is_kept()
    {
        var control = new FreezableTransport.Control();
        await using var host = new RemoteTestHost(o => o.Heartbeat = new HeartbeatPolicy(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)),
            wrap: t => new FreezableTransport(t, control));
        await host.ConnectAsync();
        await Task.Delay(TimeSpan.FromSeconds(3), Ct); // three times the dead time without a single call of ours: the pings keep it alive
        Assert.Equal(1, control.Connects);
        Assert.Equal(RemoteConnectionState.Connected, host.Manager.Status.State);
        Assert.DoesNotContain("No answer", host.Output.Text(RemoteConnectionManager.OutputChannel));
    }

    [Fact]
    public async Task A_connection_attempt_that_never_answers_fails_as_timed_out_not_as_a_refusal()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX sh");
        using var dir = new TempDirectory("cscode-start-timeout-");
        const string hash = "0123456789abcdef";
        // An "installed server" that never greets: what a session over a stale shared ssh connection looks like from here.
        var server = dir.WriteFile($"root/bin/{hash}/cscode-server", "#!/bin/sh\nexec sleep 30\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(server, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var transport = new ShellServerTransport(new LocalRemoteShell(), new ServerLaunchOptions
        {
            InstallRoot = dir.Combine("root"),
            DataDir = dir.Combine("data"),
            Bundles = new ServerBundleSet(dir.Path, [new ServerBundle(BundleFlavour.SelfContained, "linux-x64", "x.tar.gz", "aa", hash)]),
            StartTimeout = TimeSpan.FromSeconds(1),
        });
        var started = Environment.TickCount64;
        var ex = await Assert.ThrowsAsync<RemoteBootstrapException>(() => transport.ConnectAsync(null, Ct));
        Assert.True(ex.TimedOut, ex.Message);
        Assert.False(ex.IsPermanent);                                   // retried, around a shared connection (RemoteConnectionManager)
        Assert.StartsWith("No answer from local /bin/sh within 1 s", ex.Message);
        Assert.InRange(Environment.TickCount64 - started, 900, 15_000);
    }

    [Fact]
    public async Task A_timed_out_attempt_is_given_up_even_when_another_process_keeps_its_output_open()
    {
        // ssh multiplexed over a ControlMaster hands its stdin/stdout to the master: killing the timed-out ssh does not end the read of its output,
        // because the (stale) master still holds the pipe. Found against a real host: the reconnect loop then waited for ever. Here a process
        // outside the killed tree keeps the start script's stdout open.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX sh");
        using var dir = new TempDirectory("cscode-start-held-");
        const string hash = "0123456789abcdef";
        var tag = "23." + Random.Shared.Next(1000, 9999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var server = dir.WriteFile($"root/bin/{hash}/cscode-server", $"#!/bin/sh\n( sleep {tag} & )\nexec sleep 30\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(server, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var transport = new ShellServerTransport(new LocalRemoteShell(), new ServerLaunchOptions
        {
            InstallRoot = dir.Combine("root"),
            DataDir = dir.Combine("data"),
            Bundles = new ServerBundleSet(dir.Path, [new ServerBundle(BundleFlavour.SelfContained, "linux-x64", "x.tar.gz", "aa", hash)]),
            StartTimeout = TimeSpan.FromSeconds(1),
        });
        try
        {
            var started = Environment.TickCount64;
            var ex = await Assert.ThrowsAsync<RemoteBootstrapException>(() => transport.ConnectAsync(null, Ct)).WaitAsync(TimeSpan.FromSeconds(20), Ct);
            Assert.True(ex.TimedOut, ex.Message);
            Assert.InRange(Environment.TickCount64 - started, 900, 12_000); // the timeout plus the give-up grace, not the 23 s the orphan holds the pipe
        }
        finally
        {
            using var kill = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("pkill", ["-f", "sleep " + tag]) { UseShellExecute = false })!;
            await kill.WaitForExitAsync(Ct);
        }
    }

    [Fact]
    public async Task A_cancelled_attempt_ends_promptly_as_a_cancellation_even_when_another_process_keeps_its_output_open()
    {
        // Disconnect (or closing the window) while a reconnect waits on a stale ControlMaster: the cancelled attempt waited for the whole start
        // timeout (3 minutes by default) and then reported a timeout, which also switched the next attempts off connection sharing.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX sh");
        using var dir = new TempDirectory("cscode-start-held-cancel-");
        const string hash = "0123456789abcdef";
        var tag = "23." + Random.Shared.Next(1000, 9999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var server = dir.WriteFile($"root/bin/{hash}/cscode-server", $"#!/bin/sh\n( sleep {tag} & )\nexec sleep 30\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(server, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var transport = new ShellServerTransport(new LocalRemoteShell(), new ServerLaunchOptions
        {
            InstallRoot = dir.Combine("root"),
            DataDir = dir.Combine("data"),
            Bundles = new ServerBundleSet(dir.Path, [new ServerBundle(BundleFlavour.SelfContained, "linux-x64", "x.tar.gz", "aa", hash)]),
            StartTimeout = TimeSpan.FromSeconds(15),
        });
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cts.CancelAfter(TimeSpan.FromSeconds(1));
            var started = Environment.TickCount64;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ConnectAsync(null, cts.Token)).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.InRange(Environment.TickCount64 - started, 900, 8_000); // the cancellation plus its grace, not the 15 s start timeout
        }
        finally
        {
            using var kill = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("pkill", ["-f", "sleep " + tag]) { UseShellExecute = false })!;
            await kill.WaitForExitAsync(Ct);
        }
    }
}
