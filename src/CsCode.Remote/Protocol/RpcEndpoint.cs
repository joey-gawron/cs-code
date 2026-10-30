using System.Text.Json;
using CsCode.Core.Lifecycle;

namespace CsCode.Remote.Protocol;

/// <summary>
/// What a service needs from "the other end": register handlers, call, notify. Implemented by <see cref="RpcPeer"/> (one connection) and by
/// <see cref="RpcPeerSlot"/> (whichever connection is current: a persistent server outlives its client connections).
/// </summary>
public interface IRpcEndpoint
{
    JsonSerializerOptions Json { get; }

    /// <summary>True when nothing is connected (a slot between connections) or the connection is closed.</summary>
    bool IsClosed { get; }

    void OnRequest<TParams, TResult>(string method, Func<TParams, CancellationToken, ValueTask<TResult>> handler);

    void OnNotification<TParams>(string method, Action<TParams> handler, bool inline = false);

    Task<TResult?> InvokeAsync<TParams, TResult>(string method, TParams args, CancellationToken ct = default, TimeSpan? timeout = null);

    void Fire<TParams>(string method, TParams args);

    /// <summary>Sends a notification (enqueue-only); false when nothing is connected (the notification is dropped).</summary>
    bool Notify<T>(string method, T args);
}

/// <summary>
/// The server's link to its client across reconnects: handlers are registered once and applied to every connection that is attached; calls and
/// notifications go to the current connection (a call while none is attached fails with <see cref="RemoteDisconnectedException"/>, a notification
/// is dropped — the client resynchronises when it reattaches). Thread-safe.
/// </summary>
public sealed class RpcPeerSlot(JsonSerializerOptions json) : IRpcEndpoint
{
    private readonly object _gate = new();
    private readonly List<Action<RpcPeer>> _registrations = new();
    private readonly Emitter<RpcPeer> _attached = new();
    private RpcPeer? _current;

    public JsonSerializerOptions Json { get; } = json;

    /// <summary>The attached connection, or null between connections.</summary>
    public RpcPeer? Current => Volatile.Read(ref _current);

    public bool IsClosed => Current is not { IsClosed: false };

    /// <summary>Fires after a connection was attached (handlers registered, before it starts reading): per-connection work (republishing state).</summary>
    public IEventSource<RpcPeer> Attached => _attached;

    /// <summary>Registers every handler on <paramref name="peer"/> (call before <see cref="RpcPeer.Start"/>) and makes it the current connection.</summary>
    public void Attach(RpcPeer peer)
    {
        Action<RpcPeer>[] registrations;
        lock (_gate)
        {
            registrations = _registrations.ToArray();
            Volatile.Write(ref _current, peer);
        }
        foreach (var r in registrations) r(peer);
        _attached.Fire(peer);
    }

    /// <summary>Forgets <paramref name="peer"/> if it is the current connection (it closed).</summary>
    public void Detach(RpcPeer peer)
    {
        lock (_gate)
            if (ReferenceEquals(_current, peer)) Volatile.Write(ref _current, null);
    }

    private void Register(Action<RpcPeer> registration)
    {
        RpcPeer? current;
        lock (_gate)
        {
            _registrations.Add(registration);
            current = _current;
        }
        if (current is not null) registration(current);
    }

    public void OnRequest<TParams, TResult>(string method, Func<TParams, CancellationToken, ValueTask<TResult>> handler) =>
        Register(p => p.OnRequest(method, handler));

    public void OnNotification<TParams>(string method, Action<TParams> handler, bool inline = false) =>
        Register(p => p.OnNotification(method, handler, inline));

    public Task<TResult?> InvokeAsync<TParams, TResult>(string method, TParams args, CancellationToken ct = default, TimeSpan? timeout = null) =>
        Current is { } peer ? peer.InvokeAsync<TParams, TResult>(method, args, ct, timeout)
            : Task.FromException<TResult?>(new RemoteDisconnectedException($"No client is connected ({method})"));

    public void Fire<TParams>(string method, TParams args) => Current?.Fire(method, args);

    public bool Notify<T>(string method, T args) => Current is { } peer && peer.Notify(method, args);
}
