using CsCode.Core.Lifecycle;

namespace CsCode.Core.Remote;

// Additive Core contract added by the Remote module (docs/REMOTE.md). Implemented by CsCode.Remote; consumed by the shell
// (status-bar remote indicator, Open Remote Folder, connect/disconnect commands).

/// <summary>
/// Connection life cycle: <c>Disconnected → Connecting → Bootstrapping → Connected</c>; a dropped connection goes
/// <c>Connected → Reconnecting → Connected</c> (or <c>Failed</c> once the retries are spent); <c>DisconnectAsync</c> always ends in <c>Disconnected</c>.
/// </summary>
public enum RemoteConnectionState { Disconnected = 0, Connecting = 1, Bootstrapping = 2, Connected = 3, Reconnecting = 4, Failed = 5 }

/// <param name="Host">The destination as the user gave it (<c>[user@]host[:port]</c> or an ssh_config alias); null when disconnected.</param>
/// <param name="Authority">Root URI of the remote file system (<c>ssh://user@host:port/</c>) once known; documents of the remote workspace live below it.</param>
/// <param name="Message">Human-readable detail: progress step, last error.</param>
/// <param name="Attempt">Reconnect attempt number (0 when not reconnecting).</param>
public sealed record RemoteConnectionStatus(RemoteConnectionState State, string? Host = null, Uri? Authority = null, string? Message = null, int Attempt = 0)
{
    public static readonly RemoteConnectionStatus Disconnected = new(RemoteConnectionState.Disconnected);
    public bool IsConnected => State == RemoteConnectionState.Connected;
}

/// <summary>Remote (SSH) workspace connection. All calls are asynchronous, cancellable and safe from any thread; events fire on background threads.</summary>
public interface IRemoteConnectionManager
{
    RemoteConnectionStatus Status { get; }
    IEventSource<RemoteConnectionStatus> StatusChanged { get; }

    /// <summary>Connects (bootstrapping the server when needed). Completes when connected; throws on failure (the status then says why).</summary>
    ValueTask ConnectAsync(string host, CancellationToken ct = default);

    /// <summary>Closes the connection and stops the remote server process. Idempotent.</summary>
    ValueTask DisconnectAsync(CancellationToken ct = default);

    /// <summary>Drops the current connection (if any) and connects again to the same host.</summary>
    ValueTask ReconnectAsync(CancellationToken ct = default);

    /// <summary>The URI of a remote path on the connected host (<c>/home/me/src</c> → <c>ssh://me@host/home/me/src</c>); null when not connected.</summary>
    Uri? ToUri(string remotePath);
}
