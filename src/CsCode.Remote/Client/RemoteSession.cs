using CsCode.Remote.Protocol;
using CsCode.Remote.Transport;
using Microsoft.Extensions.Logging;

namespace CsCode.Remote.Client;

/// <summary>
/// One live connection to a server: the transport's channel, the JSON-RPC peer on it and the server's handshake answer. Created by
/// <see cref="RemoteClient"/>; a reconnect creates a new session. Disposing asks the server to shut down (briefly), closes the peer and stops the
/// transport process (ssh or the local server), killing its tree if it lingers.
/// </summary>
public sealed class RemoteSession : IAsyncDisposable
{
    private int _disposed;

    private RemoteSession(RemoteChannel channel, RpcPeer peer, UriMapper mapper, HelloResult server)
    {
        Channel = channel;
        Peer = peer;
        Mapper = mapper;
        Server = server;
    }

    public RemoteChannel Channel { get; }
    public RpcPeer Peer { get; }
    public UriMapper Mapper { get; }
    public HelloResult Server { get; }
    public bool Gzip => Server.Capabilities.Contains(RemoteProtocol.Capabilities.Gzip);
    public bool IsOpen => !Peer.IsClosed;

    /// <param name="attach">Registers the client-side handlers on the peer before it starts reading (participants).</param>
    public static async Task<RemoteSession> ConnectAsync(IRemoteTransport transport, UriMapper mapper, string mode, Action<RpcPeer> attach,
        IProgress<string>? progress, ILogger log, CancellationToken ct, TimeSpan? callTimeout = null)
    {
        var channel = await transport.ConnectAsync(progress, ct).ConfigureAwait(false);
        RpcPeer? peer = null;
        try
        {
            peer = new RpcPeer(channel.Input, channel.Output, new RpcPeerOptions
            {
                Name = "Remote",
                HandlerThreads = 1, // one ordered stream of server notifications (watch events, diagnostics) for the client
                Json = RemoteJson.Create(mapper),
                DefaultTimeout = callTimeout ?? TimeSpan.FromSeconds(60),
            }, log);
            attach(peer);
            peer.Start();
            progress?.Report("Handshake");
            var hello = await peer.InvokeAsync<HelloParams, HelloResult>(RemoteMethods.Hello,
                new HelloParams(RemoteProtocol.Version, ClientVersion, [RemoteProtocol.Capabilities.Gzip], mode), ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false)
                ?? throw new RemoteBootstrapException("The server sent an empty handshake");
            if (hello.ProtocolVersion != RemoteProtocol.Version)
                throw new RemoteBootstrapException($"The server speaks protocol v{hello.ProtocolVersion}, this client v{RemoteProtocol.Version} (reinstall the server)") { IsPermanent = true };
            return new RemoteSession(channel, peer, mapper, hello);
        }
        catch
        {
            peer?.Close();
            await channel.StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            throw;
        }
    }

    public static string ClientVersion =>
        typeof(RemoteSession).Assembly.GetName().Version?.ToString(3) ?? "dev";

    /// <summary>Whether this connection reached the same server process as the one before it (a persistent server, R4).</summary>
    public bool Resumed { get; internal set; }

    public ValueTask DisposeAsync() => CloseAsync(shutdownServer: true);

    /// <summary>
    /// Drops this connection WITHOUT asking the server to shut down: a persistent server keeps its terminals, debug adapters and tasks for the
    /// next connection (a reconnect attempt that failed half-way, a link given up as dead). A deliberate disconnect uses <see cref="DisposeAsync"/>.
    /// </summary>
    public ValueTask DetachAsync() => CloseAsync(shutdownServer: false);

    private async ValueTask CloseAsync(bool shutdownServer)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (shutdownServer && !Peer.IsClosed)
        {
            try { await Peer.InvokeAsync<Empty, Empty>(RemoteMethods.Shutdown, Empty.Value, CancellationToken.None, TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { /* it is going away either way */ }
        }
        Peer.Close();
        await Channel.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }
}
