using System.IO.Pipes;
using System.Net.Sockets;

namespace CsCode.Sessions.Ipc;

public enum IpcFailure
{
    /// <summary>No endpoint (the process is gone, or never listened).</summary>
    NoEndpoint,
    /// <summary>The endpoint exists but nothing accepts on it (the process died and left its socket behind, or it is not listening yet).</summary>
    Refused,
    /// <summary>Connected or not, no complete reply within the timeout (a wedged process).</summary>
    Timeout,
    /// <summary>The endpoint belongs to another user.</summary>
    WrongOwner,
    /// <summary>The connection broke or the reply was malformed.</summary>
    Broken,
}

public sealed class IpcException(IpcFailure failure, string message, Exception? inner = null) : IOException(message, inner)
{
    public IpcFailure Failure { get; } = failure;
}

/// <summary>Launcher side of the protocol: connect, send one request, read one reply, all bounded by timeouts (a wedged process never blocks the launcher).</summary>
public static class IpcClient
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The blocking variant (the launcher: a short-lived console process with nothing else to do, where async socket machinery would only add
    /// start-up cost). Bounded the same way: a non-blocking connect polled for <paramref name="connectTimeout"/>, then socket send/receive timeouts.
    /// </summary>
    public static IpcMessage Request(string endpoint, IpcMessage request, TimeSpan connectTimeout, TimeSpan? replyTimeout)
    {
        if (OperatingSystem.IsWindows()) return RequestAsync(endpoint, request, connectTimeout, replyTimeout).GetAwaiter().GetResult();
        if (!File.Exists(endpoint)) throw new IpcException(IpcFailure.NoEndpoint, "no endpoint at " + endpoint);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Blocking = false;
            try { socket.Connect(new UnixDomainSocketEndPoint(endpoint)); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress or SocketError.TryAgain)
            {
                // A listener whose backlog is full (it stopped accepting): wait a little, then count it as wedged.
                if (!socket.Poll((int)connectTimeout.TotalMicroseconds, SelectMode.SelectWrite) || !socket.Connected)
                    throw new IpcException(IpcFailure.Timeout, "connect timed out", ex);
            }
            socket.Blocking = true;
        }
        catch (SocketException ex)
        {
            throw ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => new IpcException(IpcFailure.Refused, "nothing listens on " + endpoint, ex),
                SocketError.AddressNotAvailable or SocketError.HostNotFound => new IpcException(IpcFailure.NoEndpoint, "no endpoint at " + endpoint, ex),
                _ => new IpcException(IpcFailure.Broken, ex.Message, ex),
            };
        }
        if (UnixNative.EffectiveUid() is { } uid && UnixNative.PeerCredentials(socket) is { } peer && peer.Uid != uid)
            throw new IpcException(IpcFailure.WrongOwner, $"{endpoint} belongs to uid {peer.Uid}");
        socket.SendTimeout = Math.Max(1, (int)connectTimeout.TotalMilliseconds);
        socket.ReceiveTimeout = replyTimeout is { } t ? Math.Max(1, (int)(t + connectTimeout).TotalMilliseconds) : 0;
        try
        {
            var frame = IpcFraming.Encode(request);
            for (var sent = 0; sent < frame.Length;) sent += socket.Send(frame, sent, frame.Length - sent, SocketFlags.None);
            var header = new byte[4];
            if (!Fill(socket, header)) throw new IpcException(IpcFailure.Broken, "the process closed the connection without replying");
            var body = new byte[IpcFraming.BodyLength(header)];
            if (!Fill(socket, body)) throw new IpcException(IpcFailure.Broken, "connection closed mid-message");
            return IpcCodec.Decode(body);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
        {
            throw new IpcException(IpcFailure.Timeout, $"no reply to '{request.Type}' in time", ex);
        }
        catch (SocketException ex) { throw new IpcException(IpcFailure.Broken, ex.Message, ex); }
        catch (IpcProtocolException ex) { throw new IpcException(IpcFailure.Broken, ex.Message, ex); }
    }

    private static bool Fill(Socket socket, byte[] buffer)
    {
        for (var read = 0; read < buffer.Length;)
        {
            var n = socket.Receive(buffer, read, buffer.Length - read, SocketFlags.None);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    /// <param name="replyTimeout">How long to wait for the reply once the request was sent (null = until the peer replies or disconnects: <c>--wait</c>).</param>
    public static async Task<IpcMessage> RequestAsync(string endpoint, IpcMessage request, TimeSpan connectTimeout, TimeSpan? replyTimeout, CancellationToken ct = default)
    {
        Stream stream;
        Socket? socket = null;
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds, ct).ConfigureAwait(false); }
            catch (TimeoutException ex) { await pipe.DisposeAsync().ConfigureAwait(false); throw new IpcException(IpcFailure.Timeout, "connect timed out", ex); }
            catch (UnauthorizedAccessException ex) { await pipe.DisposeAsync().ConfigureAwait(false); throw new IpcException(IpcFailure.WrongOwner, ex.Message, ex); }
            catch (IOException ex) { await pipe.DisposeAsync().ConfigureAwait(false); throw new IpcException(IpcFailure.Refused, ex.Message, ex); }
            stream = pipe;
        }
        else
        {
            if (!File.Exists(endpoint)) throw new IpcException(IpcFailure.NoEndpoint, "no endpoint at " + endpoint);
            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(connectTimeout);
                try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), connect.Token).ConfigureAwait(false); }
                catch (SocketException ex)
                {
                    socket.Dispose();
                    throw ex.SocketErrorCode switch
                    {
                        SocketError.ConnectionRefused => new IpcException(IpcFailure.Refused, "nothing listens on " + endpoint, ex),
                        SocketError.AddressNotAvailable or SocketError.HostNotFound => new IpcException(IpcFailure.NoEndpoint, "no endpoint at " + endpoint, ex),
                        _ => new IpcException(IpcFailure.Broken, ex.Message, ex),
                    };
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    socket.Dispose();
                    throw new IpcException(IpcFailure.Timeout, "connect timed out", ex);
                }
            }
            // The other side must be this user too: a socket planted by someone else is never talked to.
            if (UnixNative.EffectiveUid() is { } uid && UnixNative.PeerCredentials(socket) is { } peer && peer.Uid != uid)
            {
                socket.Dispose();
                throw new IpcException(IpcFailure.WrongOwner, $"{endpoint} belongs to uid {peer.Uid}");
            }
            stream = new NetworkStream(socket, ownsSocket: true);
        }

        await using (stream.ConfigureAwait(false))
        {
            using var exchange = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (replyTimeout is { } t) exchange.CancelAfter(t + connectTimeout);
            try
            {
                await IpcFraming.WriteAsync(stream, request, exchange.Token).ConfigureAwait(false);
                return await IpcFraming.ReadAsync(stream, exchange.Token).ConfigureAwait(false)
                    ?? throw new IpcException(IpcFailure.Broken, "the process closed the connection without replying");
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new IpcException(IpcFailure.Timeout, $"no reply to '{request.Type}' in time", ex); }
            catch (IpcProtocolException ex) { throw new IpcException(IpcFailure.Broken, ex.Message, ex); }
            catch (IOException ex) when (ex is not IpcException) { throw new IpcException(IpcFailure.Broken, ex.Message, ex); }
        }
    }
}
