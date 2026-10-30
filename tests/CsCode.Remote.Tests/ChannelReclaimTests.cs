using CsCode.Core.Remote;
using CsCode.Remote.Client;
using CsCode.Remote.Protocol;
using CsCode.Remote.Transport;
using CsCode.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CsCode.Remote.Tests;

/// <summary>
/// A persistent server's channel side in memory: the real <see cref="ChannelHub"/> on an <see cref="RpcPeerSlot"/>, answering the handshake with
/// one instance id. Each client connection is a fresh pair of pipes attached to the slot (as the daemon attaches every accepted connection).
/// <see cref="Cut"/> drops the current connection; each <see cref="Transport"/> can refuse connections on its own (a client that "crashed").
/// </summary>
internal sealed class InMemoryChannelServer : IDisposable
{
    private readonly object _gate = new();
    private (RpcPeer Peer, MemoryPipe ToServer, MemoryPipe ToClient)? _current;

    public InMemoryChannelServer()
    {
        Slot = new RpcPeerSlot(RemoteJson.Create());
        Slot.OnRequest<HelloParams, HelloResult>(RemoteMethods.Hello, (_, _) => ValueTask.FromResult(new HelloResult(
            RemoteProtocol.Version, "test", "Linux", "x64", Environment.ProcessId, "/", "/", [RemoteProtocol.Capabilities.Persistent], InstanceId)));
        Slot.OnRequest<Empty, Empty>(RemoteMethods.Shutdown, (_, _) => ValueTask.FromResult(Empty.Value));
        Hub = new ChannelHub(Slot, NullLogger.Instance);
        Hub.Register();
    }

    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public RpcPeerSlot Slot { get; }
    public ChannelHub Hub { get; }

    public sealed class Link(InMemoryChannelServer server) : IRemoteTransport
    {
        public volatile bool Refuse;
        public string Description => "memory";
        public Task<RemoteChannel> ConnectAsync(IProgress<string>? progress, CancellationToken ct) =>
            Refuse ? Task.FromException<RemoteChannel>(new IOException("network unreachable (test)")) : Task.FromResult(server.Connect());
    }

    public Link Transport() => new(this);

    private RemoteChannel Connect()
    {
        var toServer = new MemoryPipe();
        var toClient = new MemoryPipe();
        var peer = new RpcPeer(toServer.Reader, toClient.Writer, new RpcPeerOptions { Name = "memory-server", HandlerThreads = 4, Json = Slot.Json });
        Slot.Attach(peer);
        peer.Start();
        lock (_gate) _current = (peer, toServer, toClient);
        return new RemoteChannel(toClient.Reader, toServer.Writer, "memory", _ => { Drop(peer, toServer, toClient); return Task.CompletedTask; });
    }

    private void Drop(RpcPeer peer, MemoryPipe toServer, MemoryPipe toClient)
    {
        peer.Close();
        toServer.Close();
        toClient.Close();
        Slot.Detach(peer);
        Hub.OnDetached();
    }

    /// <summary>The network drops: the current connection dies at both ends.</summary>
    public void Cut()
    {
        (RpcPeer Peer, MemoryPipe ToServer, MemoryPipe ToClient)? current;
        lock (_gate) current = _current;
        if (current is { } c) Drop(c.Peer, c.ToServer, c.ToClient);
    }

    public void Dispose()
    {
        Cut();
        Hub.Dispose();
    }
}

/// <summary>A channel source that only records what happens to it.</summary>
internal sealed class RecordingSource : IChannelSource
{
    public volatile bool Disposed;
    public bool Accept(ReadOnlySpan<byte> data) => true;
    public void Close(TimeSpan grace) { }
    public void Dispose() => Disposed = true;
}

/// <summary>A sink that consumes everything at once (an idle terminal that keeps up).</summary>
internal sealed class ConsumingSink : IChannelSink
{
    public ClientChannel? Channel;
    public long Delivered, LostBytes;
    public void Deliver(ReadOnlyMemory<byte> data)
    {
        Interlocked.Add(ref Delivered, data.Length);
        Channel?.Consumed(data.Length);
    }
    public void Lost(long bytes) => Interlocked.Add(ref LostBytes, bytes);
    public void Closed(int? exitCode, string? reason) => Channel?.Consumed(0); // the final acknowledgement, as a terminal sends it
}

/// <summary>
/// Channels (remote terminals, debug streams) across reconnects to a persistent server, in memory: flow control after output was lost while the
/// link was down (remote-2), and channels a previous client process left behind (remote-3).
/// </summary>
public class ChannelReclaimTests
{
    private static CancellationToken Ct => TestEnv.Ct;

    private static RemoteClient Client(IRemoteTransport transport, RemoteChannels channels, int attempts = 8)
    {
        var client = new RemoteClient("memory", () => transport, new SshUriMapper(new RemoteAuthority("memory")), "test", new ReconnectPolicy(attempts, () => 0.2));
        client.AddParticipant(channels);
        return client;
    }

    private static async Task WriteWithinAsync(ServerChannel channel, int bytes, TimeSpan within, string what)
    {
        var data = new byte[bytes];
        Array.Fill(data, (byte)'x');
        var write = Task.Run(() => channel.Write(data), Ct);
        Assert.True(await Task.WhenAny(write, Task.Delay(within, Ct)) == write, $"{what}: the producer is still blocked after {within.TotalSeconds:0} s (flow control never released)");
        await write;
    }

    [Fact]
    public async Task After_more_than_a_window_of_output_was_lost_while_disconnected_the_program_can_write_again()
    {
        using var server = new InMemoryChannelServer();
        var link = server.Transport();
        using var channels = new RemoteChannels();
        await using var client = Client(link, channels);
        await client.ConnectAsync(Ct);
        var channel = server.Hub.Create("test");
        channel.Source = new RecordingSource();
        var sink = new ConsumingSink();
        sink.Channel = channels.Open(channel.Id, sink);

        await WriteWithinAsync(channel, 100 * 1024, TimeSpan.FromSeconds(10), "connected");
        await TestEnv.WaitUntilAsync(() => Interlocked.Read(ref sink.Delivered) == 100 * 1024, TimeSpan.FromSeconds(10), "the first output");

        // The link drops; the program prints 3 MB meanwhile: 1 MB more than the 2 MB replay ring (and twice the 512 KB window) is lost.
        link.Refuse = true;
        server.Cut();
        await TestEnv.WaitUntilAsync(() => client.Session is null, TimeSpan.FromSeconds(10), "the client to notice the drop");
        await WriteWithinAsync(channel, 3 * 1024 * 1024, TimeSpan.FromSeconds(10), "detached"); // nobody to wait for: into the ring
        link.Refuse = false;
        await TestEnv.WaitUntilAsync(() => client.Session is { IsOpen: true } && client.Status.State == RemoteConnectionState.Connected, TimeSpan.FromSeconds(30), "the reconnect");
        Assert.True(client.Session!.Resumed);
        await TestEnv.WaitUntilAsync(() => Interlocked.Read(ref sink.Delivered) == 100 * 1024 + ServerChannel.RingBytes, TimeSpan.FromSeconds(10), "the replay");
        Assert.Equal(3 * 1024 * 1024 - ServerChannel.RingBytes, Interlocked.Read(ref sink.LostBytes));

        // The program goes on printing: the acknowledgements must cover the lost gap, or it blocks for good once a window is outstanding.
        await WriteWithinAsync(channel, 1024 * 1024, TimeSpan.FromSeconds(10), "after the reconnect");
        await TestEnv.WaitUntilAsync(() => Interlocked.Read(ref sink.Delivered) == 100 * 1024 + ServerChannel.RingBytes + 1024 * 1024, TimeSpan.FromSeconds(10), "the new output");
    }

    [Fact]
    public async Task A_closed_channel_with_lost_output_is_forgotten_by_the_server_once_the_client_read_its_end()
    {
        using var server = new InMemoryChannelServer();
        var link = server.Transport();
        using var channels = new RemoteChannels();
        await using var client = Client(link, channels);
        await client.ConnectAsync(Ct);
        var channel = server.Hub.Create("test");
        channel.Source = new RecordingSource();
        var sink = new ConsumingSink();
        sink.Channel = channels.Open(channel.Id, sink);

        link.Refuse = true;
        server.Cut();
        await TestEnv.WaitUntilAsync(() => client.Session is null, TimeSpan.FromSeconds(10), "the client to notice the drop");
        await WriteWithinAsync(channel, 3 * 1024 * 1024, TimeSpan.FromSeconds(10), "detached");
        channel.Complete(0, null); // the program exits while nobody is connected
        link.Refuse = false;
        await TestEnv.WaitUntilAsync(() => sink.Channel.IsClosed, TimeSpan.FromSeconds(30), "the close after the replay");
        await TestEnv.WaitUntilAsync(() => server.Hub.Count == 0, TimeSpan.FromSeconds(10), "the server to forget the fully acknowledged channel");
    }

    [Fact]
    public async Task A_restarted_client_ends_the_channels_the_crashed_client_left_on_the_server()
    {
        using var server = new InMemoryChannelServer();
        // Client A opens a terminal channel, then "crashes": its link dies and it never comes back (no chan/close, no server/shutdown).
        var crashedLink = server.Transport();
        var crashedChannels = new RemoteChannels();
        var crashed = Client(crashedLink, crashedChannels);
        await crashed.ConnectAsync(Ct);
        var orphan = server.Hub.Create("terminal");
        var source = new RecordingSource();
        orphan.Source = source;
        crashedChannels.Open(orphan.Id, new ConsumingSink());
        crashedLink.Refuse = true;
        server.Cut();
        Assert.Equal(1, server.Hub.Count);

        // Client B (the restarted program: a new RemoteChannels) reaches the same server instance. Nothing of A's can be shown again: it ends.
        using var channels = new RemoteChannels();
        await using (var restarted = Client(server.Transport(), channels))
        {
            await restarted.ConnectAsync(Ct);
            Assert.Equal(server.InstanceId, restarted.Session!.Server.InstanceId);
            Assert.Equal(0, server.Hub.Count);
            Assert.True(source.Disposed, "the orphaned program must be ended (its source disposed)");
        }
        await crashed.DisposeAsync();
        crashedChannels.Dispose();
    }

    [Fact]
    public async Task Connecting_again_after_giving_up_ends_the_channels_of_the_earlier_connection()
    {
        using var server = new InMemoryChannelServer();
        var link = server.Transport();
        using var channels = new RemoteChannels();
        await using var client = Client(link, channels, attempts: 1);
        await client.ConnectAsync(Ct);
        var orphan = server.Hub.Create("terminal");
        var source = new RecordingSource();
        orphan.Source = source;
        var sink = new ConsumingSink();
        sink.Channel = channels.Open(orphan.Id, sink);

        link.Refuse = true;
        server.Cut();
        await TestEnv.WaitUntilAsync(() => client.Status.State == RemoteConnectionState.Failed, TimeSpan.FromSeconds(20), "the client to give up");
        channels.FailAll("gave up (test)"); // what the connection manager does on Failed: the terminal shows it ended
        Assert.True(sink.Channel.IsClosed);

        link.Refuse = false;
        await client.ConnectAsync(Ct); // the user connects again; the persistent server is still the same
        Assert.Equal(0, server.Hub.Count);
        Assert.True(source.Disposed);
    }
}
