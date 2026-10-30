using System.Collections.Concurrent;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.Logging;

namespace CsCode.Server;

/// <summary>What feeds a channel and takes its input: a PTY (terminal) or a debug adapter's stream. Called from the hub's threads.</summary>
internal interface IChannelSource : IDisposable
{
    /// <summary>Client input for the source (keystrokes, DAP requests). Must not block: queue it (false = the source's queue is full).</summary>
    bool Accept(ReadOnlySpan<byte> data);

    /// <summary>Ends the source (kill the process). The source still reports its exit through <see cref="ServerChannel.Complete"/>.</summary>
    void Close(TimeSpan grace);
}

/// <summary>
/// One resumable byte stream between a source on the server and the client. Output is numbered by absolute offset and kept in a replay ring, so a
/// client that reconnects (to this same, persistent server) gets exactly what it missed. Flow control: while a client is connected, a producer that
/// is more than <see cref="Window"/> bytes ahead of the client's acknowledgements waits (the program is throttled, as by a full PTY); while nobody is
/// connected nothing waits — the ring keeps the newest <see cref="RingBytes"/> and older output is counted as lost. Input from the client is
/// de-duplicated by offset (a resend after a reconnect delivers each byte once).
/// <para>A channel sends only to the connection it belongs to: the one it was created on, or the one that reattached it (<c>chan/attach</c>).
/// A new connection gets nothing live until the client reattached the channel there, so the replay always comes before newer output.</para>
/// </summary>
internal sealed class ServerChannel
{
    public const int Window = 512 * 1024;
    public const int RingBytes = 2 * 1024 * 1024;
    private const int MaxChunk = 32 * 1024;

    private readonly object _gate = new();
    private readonly RpcPeerSlot _link;
    private readonly ByteRing _ring = new(RingBytes, 16 * 1024); // grows with the output: a quiet terminal holds a few KB
    private RpcPeer? _peer; // the connection this channel currently sends to
    private long _sent;      // total bytes produced
    private long _acked;     // bytes the client consumed
    private long _received;  // client input accepted
    private bool _closed;
    private int? _exitCode;
    private string? _reason;
    private volatile bool _disposed;

    public ServerChannel(long id, string kind, RpcPeerSlot link)
    {
        Id = id;
        Kind = kind;
        _link = link;
        _peer = link.Current; // created by a request of the current connection
    }

    /// <summary>The connection to send to, while it is still the current one and open.</summary>
    private RpcPeer? Live => _peer is { IsClosed: false } p && ReferenceEquals(p, _link.Current) ? p : null;

    public long Id { get; }
    public string Kind { get; }
    public IChannelSource? Source { get; set; }

    /// <summary>The task start a task's terminal belongs to (<see cref="TaskTerminalTag"/>): kept by <c>chan/keep</c> while the client still opens it.</summary>
    public string? TaskTag { get; set; }
    public bool IsClosed { get { lock (_gate) return _closed; } }
    public long Produced { get { lock (_gate) return _sent; } }

    /// <summary>Output from the source (its reader thread). Waits while the connected client is a window behind (back-pressure); never while detached.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            var n = Math.Min(data.Length, MaxChunk);
            WriteChunk(data[..n]);
            data = data[n..];
        }
    }

    private void WriteChunk(ReadOnlySpan<byte> chunk)
    {
        lock (_gate)
        {
            while (!_disposed && Live is not null && _sent - _acked > Window) Monitor.Wait(_gate, 250);
            if (_disposed) return;
            var offset = _sent;
            _ring.Append(chunk);
            _sent += chunk.Length;
            // Sent under the lock so offsets reach the peer's queue in order (Notify only enqueues).
            Live?.Notify(RemoteMethods.ChanData, new ChanDataParams(Id, offset, Payload.Encode(chunk, allowCompression: false)));
        }
    }

    /// <summary>The source ended (after its last output). Reported now if a client is connected, else on its reattach.</summary>
    public void Complete(int? exitCode, string? reason)
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _exitCode = exitCode;
            _reason = reason;
            Live?.Notify(RemoteMethods.ChanClosed, new ChanClosedParams(Id, _sent, exitCode, reason));
            Monitor.PulseAll(_gate);
        }
    }

    public void OnAck(long offset)
    {
        lock (_gate)
        {
            if (offset > _acked) _acked = Math.Min(offset, _sent);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>A client connection went away: release a producer waiting for acknowledgements.</summary>
    public void OnDetached()
    {
        lock (_gate) Monitor.PulseAll(_gate);
    }

    /// <summary>Client input at <paramref name="offset"/>; the part already accepted (a resend) is skipped.</summary>
    public void OnClientData(long offset, byte[] data)
    {
        lock (_gate)
        {
            var skip = _received - offset;
            if (skip >= data.Length) return;
            var fresh = skip > 0 ? data.AsSpan((int)skip) : data.AsSpan();
            // A gap (offset beyond what we have) means input was lost on the client side; take what arrived.
            if (Source is { } source && !source.Accept(fresh)) return; // the source's own queue is full: dropped, as a local PTY drops it
            _received = Math.Max(_received, offset) + fresh.Length;
        }
    }

    /// <summary>Resumes the channel for a reattached client: replays [received, produced) and the close if it happened.</summary>
    public ChanAttachResult Attach(long receivedOffset)
    {
        lock (_gate)
        {
            _peer = _link.Current; // the reattaching connection: from now on this channel's output goes there
            var peer = _peer;
            var ringStart = _ring.Start;
            var from = Math.Clamp(receivedOffset, ringStart, _sent);
            var lost = Math.Max(0, ringStart - receivedOffset);
            _acked = Math.Max(_acked, from); // what the client had (or can no longer get) needs no acknowledgement
            for (var pos = from; pos < _sent;)
            {
                var n = (int)Math.Min(MaxChunk, _sent - pos);
                var chunk = new byte[n];
                _ring.CopyTo(pos, chunk);
                peer?.Notify(RemoteMethods.ChanData, new ChanDataParams(Id, pos, Payload.Encode(chunk, allowCompression: false)));
                pos += n;
            }
            if (_closed) peer?.Notify(RemoteMethods.ChanClosed, new ChanClosedParams(Id, _sent, _exitCode, _reason));
            Monitor.PulseAll(_gate);
            return new ChanAttachResult(true, _received, lost);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_gate) Monitor.PulseAll(_gate);
        try { Source?.Dispose(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
    }
}

/// <summary>
/// The server's channels (terminals, debug adapters) and the <c>chan/*</c> handlers. Channels belong to the server process, not to one connection:
/// a persistent server keeps them across reconnects. A closed channel is forgotten once the client acknowledged all of it (or did not claim it
/// after a reattach).
/// </summary>
internal sealed class ChannelHub : IDisposable
{
    private readonly RpcPeerSlot _link;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<long, ServerChannel> _channels = new();
    private long _nextId;

    public ChannelHub(RpcPeerSlot link, ILogger log)
    {
        _link = link;
        _log = log;
    }

    public int Count => _channels.Count;

    public IReadOnlyCollection<ServerChannel> Channels => _channels.Values.ToArray();

    public ServerChannel Create(string kind)
    {
        var channel = new ServerChannel(Interlocked.Increment(ref _nextId), kind, _link);
        _channels[channel.Id] = channel;
        return channel;
    }

    public ServerChannel? Get(long id) => _channels.GetValueOrDefault(id);

    public void Register()
    {
        // Input must reach a source in order: inline on the reader (cheap: Accept only queues).
        _link.OnNotification<ChanDataParams>(RemoteMethods.ChanData, p => _channels.GetValueOrDefault(p.Id)?.OnClientData(p.Offset, p.Data.Decode()), inline: true);
        _link.OnNotification<ChanAckParams>(RemoteMethods.ChanAck, p =>
        {
            if (_channels.GetValueOrDefault(p.Id) is not { } c) return;
            c.OnAck(p.Offset);
            if (c.IsClosed && p.Offset >= c.Produced) Remove(c.Id); // everything delivered
        }, inline: true);
        _link.OnRequest<ChanCloseParams, Empty>(RemoteMethods.ChanClose, (p, _) =>
        {
            if (_channels.GetValueOrDefault(p.Id) is { } c)
            {
                if (c.IsClosed) Remove(c.Id);
                else c.Source?.Close(TimeSpan.FromMilliseconds(Math.Clamp(p.GraceMs, 0, 30_000)));
            }
            return ValueTask.FromResult(Empty.Value);
        });
        _link.OnRequest<ChanAttachParams, ChanAttachResult>(RemoteMethods.ChanAttach, (p, _) =>
            ValueTask.FromResult(_channels.GetValueOrDefault(p.Id)?.Attach(p.ReceivedOffset) ?? new ChanAttachResult(false, 0, 0)));
        _link.OnRequest<ChanKeepParams, Empty>(RemoteMethods.ChanKeep, (p, _) =>
        {
            var keep = p.Ids.ToHashSet();
            // A task's terminal whose term/create answer the client lost is not among its channels yet: it asks for it again (by its tag) instead.
            var opening = p.TaskTags is { Count: > 0 } tags ? tags.ToHashSet(StringComparer.Ordinal) : null;
            foreach (var id in _channels.Where(c => !keep.Contains(c.Key) && !(c.Value.TaskTag is { } tag && opening?.Contains(tag) == true))
                         .Select(c => c.Key).ToArray())
            {
                _log.LogInformation("Channel {Id} was not reclaimed by the reconnected client: ending it", id);
                Remove(id);
            }
            return ValueTask.FromResult(Empty.Value);
        });
    }

    /// <summary>The client connection went away: producers waiting for its acknowledgements go on (into the replay ring).</summary>
    public void OnDetached()
    {
        foreach (var c in _channels.Values) c.OnDetached();
    }

    public void Remove(long id)
    {
        if (_channels.TryRemove(id, out var c)) c.Dispose();
    }

    public void Dispose()
    {
        foreach (var id in _channels.Keys.ToArray()) Remove(id);
    }
}
