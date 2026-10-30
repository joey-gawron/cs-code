using System.Collections.Concurrent;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Client;

/// <summary>
/// Where a channel's output goes (a terminal's delivery thread, a debug adapter stream's read buffer). Called on the connection's handler thread,
/// in order: implementations queue and return (they report consumption back with <see cref="ClientChannel.Consumed"/>, which is the flow control).
/// </summary>
public interface IChannelSink
{
    void Deliver(ReadOnlyMemory<byte> data);

    /// <summary>Output the server could not replay after a reconnect (it fell out of its buffer while nobody was connected).</summary>
    void Lost(long bytes);

    /// <summary>The source ended (after its last output): exit code (null when it has none, -1 when lost) and a reason (adapter stderr, connection loss).</summary>
    void Closed(int? exitCode, string? reason);
}

/// <summary>
/// The client end of a server channel (R3/R4): a resumable byte stream with absolute offsets both ways. Output arrives in order and is de-duplicated
/// (a replay after a reconnect may overlap); its consumption is acknowledged every <see cref="AckEvery"/> bytes, which lets the server send more
/// (it holds a producer that is a window ahead). Input is numbered and the last <see cref="InputRingBytes"/> of it kept: written while disconnected
/// it waits in that ring, and after a reconnect everything the server did not get is sent again. Thread-safe; <see cref="Write"/> only copies and
/// enqueues (the typing path).
/// </summary>
public sealed class ClientChannel : IDisposable
{
    public const int InputRingBytes = 1024 * 1024;
    public const int AckEvery = 64 * 1024;

    private readonly RemoteChannels _owner;
    private readonly IChannelSink _sink;
    private readonly object _gate = new();
    private readonly ByteRing _input = new(InputRingBytes, 256); // typing is small: grows only for pastes and DAP requests
    private long _received, _consumed, _acked;
    private long _sent;        // input bytes written
    private long _unsentFrom = -1; // first input offset not handed to a connection (-1: everything was sent)
    private ChanClosedParams? _pendingClose;
    private bool _closed, _disposed;

    internal ClientChannel(RemoteChannels owner, long id, IChannelSink sink)
    {
        _owner = owner;
        Id = id;
        _sink = sink;
    }

    public long Id { get; }
    public bool IsClosed { get { lock (_gate) return _closed; } }
    public long Received { get { lock (_gate) return _received; } }

    /// <summary>Input waiting for a connection (written while disconnected).</summary>
    public long PendingWriteBytes { get { lock (_gate) return _unsentFrom < 0 ? 0 : _sent - _unsentFrom; } }

    internal void OnData(long offset, byte[] data)
    {
        lock (_gate)
        {
            if (_closed || offset + data.Length <= _received) return; // after the end, or a replayed part we have
            var skip = (int)Math.Max(0, _received - offset);
            if (offset > _received)
            {
                // A gap the server could not replay. Acknowledgements are stream offsets: the lost bytes count as consumed, or the client's acks
                // would stay that far behind the server's offsets for good and a producer more than a window ahead would never be released.
                var gap = offset - _received;
                _consumed += gap;
                _sink.Lost(gap);
            }
            _received = offset + data.Length;
            _sink.Deliver(data.AsMemory(skip));
            if (_pendingClose is { } close && _received >= close.FinalOffset) Close(close.ExitCode, close.Reason);
        }
    }

    internal void OnClosed(ChanClosedParams p)
    {
        lock (_gate)
        {
            if (_closed) return;
            if (_received >= p.FinalOffset) Close(p.ExitCode, p.Reason);
            else _pendingClose = p; // its last output is still on the way (or will be replayed)
        }
    }

    private void Close(int? exitCode, string? reason)
    {
        _closed = true;
        _sink.Closed(exitCode, reason);
    }

    /// <summary>The server lost the source (it is not the server the channel was opened on): ends the channel locally.</summary>
    internal void Gone(string reason)
    {
        lock (_gate)
        {
            if (_closed) return;
            Close(-1, reason);
        }
        _owner.Forget(this);
    }

    /// <summary>
    /// The sink passed <paramref name="bytes"/> of delivered output on (never text of its own, such as a lost-output marker): acknowledged in steps
    /// of <see cref="AckEvery"/> (and at the very end) as the stream offset consumed so far.
    /// </summary>
    public void Consumed(int bytes)
    {
        long ack;
        lock (_gate)
        {
            _consumed += bytes;
            var final = _closed && _consumed >= _received;
            if (_consumed - _acked < AckEvery && !final) return;
            ack = _acked = _consumed;
        }
        _owner.Peer?.Notify(RemoteMethods.ChanAck, new ChanAckParams(Id, ack));
    }

    /// <summary>
    /// Input for the source: copied into the ring and handed to the connection (enqueue only). While disconnected it waits in the ring for the
    /// reconnect. False when the channel ended or more than <see cref="InputRingBytes"/> are waiting.
    /// </summary>
    public bool Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return true;
        lock (_gate)
        {
            if (_closed || _disposed) return false;
            if (_unsentFrom >= 0 && _sent - _unsentFrom + data.Length > InputRingBytes) return false;
            if (data.Length > InputRingBytes) return false;
            var offset = _sent;
            _input.Append(data);
            _sent += data.Length;
            if (_unsentFrom >= 0) return true; // earlier input is still waiting: keep the order, send it all on reconnect
            if (_owner.Peer is { } peer && peer.Notify(RemoteMethods.ChanData, new ChanDataParams(Id, offset, Payload.Encode(data, allowCompression: false)))) return true;
            _unsentFrom = offset;
            return true;
        }
    }

    /// <summary>
    /// The connection went away: new input is held (not sent on the next connection) until <see cref="ReattachAsync"/> learned what the server got —
    /// input sent just before the drop may have been lost in flight, and it must go again before anything newer.
    /// </summary>
    internal void Suspend()
    {
        lock (_gate)
            if (_unsentFrom < 0) _unsentFrom = _sent;
    }

    /// <summary>After a reconnect to the same server: resume from what we received, send again what the server did not get.</summary>
    internal async Task ReattachAsync(RpcPeer peer, CancellationToken ct)
    {
        long received;
        lock (_gate)
        {
            if (_closed) return;
            received = _received;
        }
        var r = await peer.InvokeAsync<ChanAttachParams, ChanAttachResult>(RemoteMethods.ChanAttach, new ChanAttachParams(Id, received), ct).ConfigureAwait(false);
        if (r is null || !r.Exists)
        {
            Gone("The remote program is no longer running (the server lost it while disconnected).");
            return;
        }
        lock (_gate)
        {
            var from = Math.Max(r.ServerReceived, _input.Start);
            for (var pos = from; pos < _sent;)
            {
                var n = (int)Math.Min(32 * 1024, _sent - pos);
                var chunk = new byte[n];
                _input.CopyTo(pos, chunk);
                if (!peer.Notify(RemoteMethods.ChanData, new ChanDataParams(Id, pos, new Payload(chunk)))) return; // dropped again: the next reconnect resends
                pos += n;
            }
            _unsentFrom = -1;
        }
    }

    /// <summary>Asks the server to end the source (kill the process after <paramref name="grace"/>); the close arrives as usual.</summary>
    public void Close(TimeSpan grace)
    {
        if (IsClosed) return;
        _owner.Peer?.Fire(RemoteMethods.ChanClose, new ChanCloseParams(Id, (int)Math.Clamp(grace.TotalMilliseconds, 0, 30_000)));
    }

    /// <summary>Stops tracking the channel; a source still running is ended on the server.</summary>
    public void Dispose()
    {
        bool wasClosed;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            wasClosed = _closed;
        }
        _owner.Peer?.Fire(RemoteMethods.ChanClose, new ChanCloseParams(Id, wasClosed ? 0 : 1000)); // also lets the server forget a closed one
        _owner.Forget(this);
    }
}

/// <summary>
/// The client's channels of one connection manager host (a participant): routes <c>chan/data</c>/<c>chan/closed</c> to them and, after a
/// reconnect, resumes them when the server is the same process (persistent server, R4: same instance id) or ends them when it is not. Output for a
/// channel that is not open yet (it raced its creation answer) is held briefly.
/// </summary>
public sealed class RemoteChannels : IRemoteParticipant, IDisposable
{
    private const int MaxEarlyBytes = 1024 * 1024;
    private readonly ConcurrentDictionary<long, ClientChannel> _channels = new();
    private readonly ConcurrentDictionary<long, List<object>> _early = new(); // ChanDataParams / ChanClosedParams before Open
    private readonly ILogger _log;
    private volatile RemoteSession? _session;
    private string? _instance;
    private long _earlyBytes;

    public RemoteChannels(ILogger? log = null) => _log = log ?? NullLogger.Instance;

    internal RpcPeer? Peer => _session is { IsOpen: true } s ? s.Peer : null;
    public int Count => _channels.Count;

    /// <summary>The session's server instance; channels are resumable only on the instance that created them.</summary>
    public string? InstanceId => Volatile.Read(ref _instance);

    public void Attach(RpcPeer peer)
    {
        peer.OnNotification<ChanDataParams>(RemoteMethods.ChanData, p =>
        {
            if (_channels.TryGetValue(p.Id, out var c)) c.OnData(p.Offset, p.Data.Decode());
            else Hold(p.Id, p, p.Data.Data.Length);
        });
        peer.OnNotification<ChanClosedParams>(RemoteMethods.ChanClosed, p =>
        {
            if (_channels.TryGetValue(p.Id, out var c)) c.OnClosed(p);
            else Hold(p.Id, p, 0);
        });
    }

    private void Hold(long id, object message, int bytes)
    {
        if (Interlocked.Add(ref _earlyBytes, bytes) > MaxEarlyBytes) { Interlocked.Add(ref _earlyBytes, -bytes); return; }
        var created = new List<object>();
        var list = _early.GetOrAdd(id, created);
        lock (list) list.Add(message);
        // A channel nobody opens within a while (its creation failed on our side) is dropped.
        if (ReferenceEquals(list, created)) _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => Release(id), TaskScheduler.Default);
    }

    private List<object>? Release(long id)
    {
        if (!_early.TryRemove(id, out var list)) return null;
        lock (list) Interlocked.Add(ref _earlyBytes, -list.OfType<ChanDataParams>().Sum(p => p.Data.Data.Length));
        return list;
    }

    /// <summary>Starts tracking a channel the server created (delivering anything that arrived before).</summary>
    public ClientChannel Open(long id, IChannelSink sink)
    {
        var channel = new ClientChannel(this, id, sink);
        _channels[id] = channel;
        if (Release(id) is { } early)
            lock (early)
                foreach (var m in early)
                {
                    if (m is ChanDataParams d) channel.OnData(d.Offset, d.Data.Decode());
                    else if (m is ChanClosedParams c) channel.OnClosed(c);
                }
        return channel;
    }

    internal void Forget(ClientChannel channel) => _channels.TryRemove(new KeyValuePair<long, ClientChannel>(channel.Id, channel));

    public async Task OnConnectedAsync(RemoteSession session, CancellationToken ct)
    {
        var previous = Interlocked.Exchange(ref _instance, session.Server.InstanceId);
        var resumed = previous is not null && previous == session.Server.InstanceId;
        _session = session;
        var channels = _channels.Values.ToArray();
        if (!resumed)
            foreach (var c in channels) c.Gone("The connection to the remote server was lost and the server did not keep the program.");
        else
            foreach (var c in channels)
            {
                try { await c.ReattachAsync(session.Peer, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or TimeoutException) { _log.LogDebug("Reattaching channel {Id} failed: {Error}", c.Id, ex.Message); }
            }
        // Whatever we no longer have ends on the server: channels closed while we were away and — on a server this object has not seen (a restarted
        // client reattaching to the server a crashed one left running, or this client after it gave up and connected again) — every channel of
        // the earlier connection, whose programs would otherwise run on with nobody to see or end them. Nothing to end on a new server. No channel
        // can be created on this session before this runs (the session is published after the participants).
        if (session.Server.InstanceId is not null)
            await session.Peer.InvokeAsync<ChanKeepParams, Empty>(RemoteMethods.ChanKeep, new ChanKeepParams(_channels.Keys.ToArray()), ct).ConfigureAwait(false);
    }

    public void OnDisconnected()
    {
        _session = null;
        foreach (var c in _channels.Values) c.Suspend();
    }

    /// <summary>The connection is gone for good (failed, or closed by the user): every channel ends with <paramref name="reason"/>.</summary>
    public void FailAll(string reason)
    {
        foreach (var c in _channels.Values.ToArray()) c.Gone(reason);
        Volatile.Write(ref _instance, null);
    }

    public void Dispose() => FailAll("The remote connection was closed.");
}
