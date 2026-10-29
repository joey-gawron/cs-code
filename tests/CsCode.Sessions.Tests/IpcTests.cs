using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using CsCode.Sessions.Ipc;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Sessions.Tests;

public class IpcTests
{
    private sealed class EchoHandler : IIpcHandler
    {
        public TimeSpan Delay { get; init; }
        public int Calls;

        public async Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            return new IpcMessage { Type = "reply", Ok = true, Session = request.Session, Text = request.Type, Pid = Environment.ProcessId };
        }
    }

    [Fact]
    public async Task A_request_gets_its_reply_and_the_endpoint_is_private()
    {
        using var rt = new TestRuntime();
        using var server = new IpcServer(rt.Runtime.EndpointOf(Environment.ProcessId), new EchoHandler());
        server.Start();

        var reply = await IpcClient.RequestAsync(server.Endpoint, new IpcMessage { Type = IpcVerbs.Hello, Session = "s" }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        Assert.True(reply.Ok);
        Assert.Equal("s", reply.Session);
        Assert.Equal(IpcVerbs.Hello, reply.Text);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(server.Endpoint));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(rt.Runtime.Path));
        }
    }

    [Fact]
    public async Task A_peer_running_as_another_user_is_disconnected_before_anything_is_read()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Windows uses a current-user-only pipe ACL instead of peer credentials.");
        using var rt = new TestRuntime();
        var handler = new EchoHandler();
        var me = UnixNative.EffectiveUid()!.Value;
        // The server expects another uid: this (real) connection's SO_PEERCRED says "me", so it must be refused.
        using var server = new IpcServer(rt.Runtime.EndpointOf(Environment.ProcessId), handler, new IpcServerOptions { ExpectedUid = me + 1 });
        server.Start();

        var ex = await Assert.ThrowsAsync<IpcException>(() =>
            IpcClient.RequestAsync(server.Endpoint, new IpcMessage { Type = IpcVerbs.Hello }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)));

        Assert.Equal(IpcFailure.Broken, ex.Failure);
        Assert.Equal(1, server.RejectedConnections);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Peer_credentials_report_this_process()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("SO_PEERCRED is Linux-only (macOS uses LOCAL_PEERCRED).");
        var (a, b) = SocketPair();
        using (a)
        using (b)
        {
            var peer = UnixNative.PeerCredentials(a);
            Assert.NotNull(peer);
            Assert.Equal(UnixNative.EffectiveUid(), peer!.Value.Uid);
            Assert.Equal(Environment.ProcessId, peer.Value.Pid);
        }
    }

    private static (Socket, Socket) SocketPair()
    {
        var path = Path.Combine("/tmp", "csp-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        client.Connect(new UnixDomainSocketEndPoint(path));
        var server = listener.Accept();
        File.Delete(path);
        return (server, client);
    }

    [Fact]
    public async Task Test_only_verbs_are_refused_by_a_normal_process()
    {
        using var rt = new TestRuntime();
        var handler = new EchoHandler();
        using var server = new IpcServer(rt.Runtime.EndpointOf(Environment.ProcessId), handler);
        server.Start();

        var reply = await IpcClient.RequestAsync(server.Endpoint, new IpcMessage { Type = IpcVerbs.TestEdit }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        Assert.False(reply.Ok);
        Assert.Contains("only served by test processes", reply.Error);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_garbage_frame_closes_the_connection_and_the_server_keeps_serving()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("raw socket test");
        using var rt = new TestRuntime();
        using var server = new IpcServer(rt.Runtime.EndpointOf(Environment.ProcessId), new EchoHandler());
        server.Start();

        using (var raw = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            await raw.ConnectAsync(new UnixDomainSocketEndPoint(server.Endpoint));
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue); // absurd length
            await raw.SendAsync(header);
            var buffer = new byte[16];
            var n = await raw.ReceiveAsync(buffer).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, n); // closed by the server
        }

        var reply = await IpcClient.RequestAsync(server.Endpoint, new IpcMessage { Type = IpcVerbs.Hello }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        Assert.True(reply.Ok);
    }

    [Fact]
    public async Task A_slow_request_does_not_hold_up_another_connection()
    {
        using var rt = new TestRuntime();
        var handler = new SlowFirst();
        using var server = new IpcServer(rt.Runtime.EndpointOf(Environment.ProcessId), handler);
        server.Start();

        var slow = IpcClient.RequestAsync(server.Endpoint, new IpcMessage { Type = "slow" }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        await handler.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clock = Stopwatch.StartNew();
        var fast = await IpcClient.RequestAsync(server.Endpoint, new IpcMessage { Type = IpcVerbs.Hello }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        Assert.True(fast.Ok);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"hello waited {clock.ElapsedMilliseconds} ms behind a slow request");
        handler.Release.SetResult();
        Assert.True((await slow).Ok);
    }

    private sealed class SlowFirst : IIpcHandler
    {
        public TaskCompletionSource SlowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IpcMessage> HandleAsync(IpcMessage request, CancellationToken ct)
        {
            if (request.Type == "slow")
            {
                SlowStarted.SetResult();
                await Release.Task.WaitAsync(ct);
            }
            return IpcMessage.Reply();
        }
    }

    [Fact]
    public async Task A_wedged_process_times_out_within_the_handshake_budget()
    {
        using var rt = new TestRuntime();
        using var wedged = new FakeHost(rt.Runtime, "w", ProcessStates.Shown) { Wedged = true };
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<IpcException>(() =>
            IpcClient.RequestAsync(rt.Runtime.EndpointOf(wedged.Pid), new IpcMessage { Type = IpcVerbs.Hello }, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300)));

        Assert.Equal(IpcFailure.Timeout, ex.Failure);
        Assert.True(clock.ElapsedMilliseconds < 2000, $"took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Dispose_removes_the_endpoint_and_connecting_afterwards_fails_cleanly()
    {
        using var rt = new TestRuntime();
        var server = new IpcServer(rt.Runtime.EndpointOf(Environment.ProcessId), new EchoHandler());
        server.Start();
        server.Dispose();

        if (!OperatingSystem.IsWindows()) Assert.False(File.Exists(server.Endpoint));
        var ex = await Assert.ThrowsAsync<IpcException>(() =>
            IpcClient.RequestAsync(server.Endpoint, new IpcMessage { Type = IpcVerbs.Hello }, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300)));
        Assert.Contains(ex.Failure, new[] { IpcFailure.NoEndpoint, IpcFailure.Timeout });
    }

    [Fact]
    public async Task Discovery_lists_live_processes_removes_stale_sockets_and_flags_wedged_ones()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Unix sockets");
        using var rt = new TestRuntime();
        using var live = new FakeHost(rt.Runtime, "work", ProcessStates.Hidden) { LastShown = DateTimeOffset.UtcNow };
        using var spare = new FakeHost(rt.Runtime, null, ProcessStates.Spare);
        using var wedged = new FakeHost(rt.Runtime, "stuck", ProcessStates.Shown, pid: Environment.ProcessId) { Wedged = true }; // a live pid that does not answer
        // A socket left behind by a process that died (bound, never unlinked; nothing listens; pid not running).
        var stalePid = 4_194_000;
        var stale = rt.Runtime.EndpointOf(stalePid);
        StaleSocket.Create(stale);

        var clock = Stopwatch.StartNew();
        var found = await ResidentDiscovery.DiscoverAsync(rt.Runtime, TimeSpan.FromMilliseconds(300));

        Assert.True(clock.ElapsedMilliseconds < 3000, $"discovery took {clock.ElapsedMilliseconds} ms");
        Assert.False(File.Exists(stale), "the stale socket of a dead process is removed");
        Assert.DoesNotContain(found, p => p.Pid == stalePid);
        var w = Assert.Single(found, p => p.Pid == live.Pid);
        Assert.Equal(("work", ProcessStates.Hidden, true), (w.Session, w.State, w.Responsive));
        Assert.True(Assert.Single(found, p => p.Pid == spare.Pid).IsSpare);
        var stuck = Assert.Single(found, p => p.Pid == Environment.ProcessId);
        Assert.False(stuck.Responsive);
        Assert.True(File.Exists(rt.Runtime.EndpointOf(Environment.ProcessId)), "a live but wedged process keeps its endpoint");
    }

    [Fact]
    public async Task Starting_a_server_replaces_a_stale_socket_at_its_own_endpoint()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Unix sockets");
        using var rt = new TestRuntime();
        var endpoint = rt.Runtime.EndpointOf(Environment.ProcessId);
        StaleSocket.Create(endpoint);
        using var server = new IpcServer(endpoint, new EchoHandler());
        server.Start();
        var reply = await IpcClient.RequestAsync(endpoint, new IpcMessage { Type = IpcVerbs.Hello }, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        Assert.True(reply.Ok);
    }

    [Fact]
    public void A_disposed_server_is_not_retained()
    {
        using var rt = new TestRuntime();
        LeakAssert.NotRetained(() =>
        {
            var handler = new EchoHandler();
            var server = new IpcServer(rt.Runtime.EndpointOf(Environment.ProcessId), handler);
            server.Start();
            server.Dispose();
            return [server, handler];
        });
    }

    [Fact]
    public void Start_binds_synchronously_and_returns_quickly()
    {
        using var rt = new TestRuntime();
        var clock = Stopwatch.StartNew();
        using var server = new IpcServer(rt.Runtime.EndpointOf(Environment.ProcessId), new EchoHandler());
        server.Start();
        Assert.True(clock.ElapsedMilliseconds < 200, $"Start took {clock.ElapsedMilliseconds} ms");
        if (!OperatingSystem.IsWindows()) Assert.True(File.Exists(server.Endpoint));
    }
}
