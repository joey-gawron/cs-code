using CsCode.Core.Lifecycle;
using CsCode.Core.Remote;
using CsCode.Remote.Protocol;
using CsCode.Remote.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Client;

/// <summary>A component that lives across sessions of a <see cref="RemoteClient"/> (file watcher, document sync, language bridge…).</summary>
public interface IRemoteParticipant
{
    /// <summary>Registers handlers on a new connection, before it starts reading (so nothing the server sends early is missed).</summary>
    void Attach(RpcPeer peer);

    /// <summary>The connection is up (first time or after a reconnect): push the client's state (watches, folders, settings, open documents).</summary>
    Task OnConnectedAsync(RemoteSession session, CancellationToken ct);

    /// <summary>The connection went away (dropped or closed): clear mirrored state (diagnostics, bridge providers). Never throws.</summary>
    void OnDisconnected();
}

/// <summary>Reconnect delays: exponential (0.5 s doubling, capped at 30 s) with ±20 % jitter, then give up.</summary>
public sealed class ReconnectPolicy(int maxAttempts = 8, Func<double>? random = null)
{
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;
    public int MaxAttempts { get; } = maxAttempts;

    /// <summary>The wait before attempt <paramref name="attempt"/> (1-based), or null once the attempts are spent.</summary>
    public TimeSpan? Delay(int attempt)
    {
        if (attempt < 1 || attempt > MaxAttempts) return null;
        var baseMs = Math.Min(30_000, 500 * Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds(baseMs * (0.8 + 0.4 * _random()));
    }
}

/// <summary>
/// The connection state machine shared by the SSH remote and the local process language host:
/// <c>Disconnected → Connecting → Bootstrapping → Connected</c>; a dropped connection → <c>Reconnecting</c> (backoff + jitter, a fresh session
/// each attempt, participants resynchronised) → <c>Connected</c>, or <c>Failed</c> once the policy gives up. <see cref="DisconnectAsync"/> always
/// ends in <c>Disconnected</c> with no process left. Thread-safe; status events fire on background threads.
/// </summary>
public sealed class RemoteClient : IAsyncDisposable
{
    private readonly Func<IRemoteTransport> _transport;
    private readonly string _mode;
    private readonly ReconnectPolicy _policy;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Action<string>? _logLine;
    private readonly List<IRemoteParticipant> _participants = new();
    private readonly Emitter<RemoteConnectionStatus> _statusChanged = new();
    private readonly Emitter<RemoteSession?> _sessionChanged = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RemoteConnectionStatus _status;
    private RemoteSession? _session;
    private CancellationTokenSource? _lifetime;
    private int _generation;
    private string? _lastInstance;

    public RemoteClient(string host, Func<IRemoteTransport> transport, UriMapper mapper, string mode, ReconnectPolicy? policy = null,
        TimeProvider? time = null, ILogger? log = null, Action<string>? logLine = null, Uri? authority = null)
    {
        Host = host;
        _transport = transport;
        Mapper = mapper;
        _mode = mode;
        _policy = policy ?? new ReconnectPolicy();
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _logLine = logLine;
        Authority = authority;
        _status = RemoteConnectionStatus.Disconnected with { Host = host };
    }

    public string Host { get; }
    public Uri? Authority { get; }
    public UriMapper Mapper { get; }
    public RemoteConnectionStatus Status => Volatile.Read(ref _status);
    public IEventSource<RemoteConnectionStatus> StatusChanged => _statusChanged;
    /// <summary>Fires with the new session after a (re)connect and null when it is gone.</summary>
    public IEventSource<RemoteSession?> SessionChanged => _sessionChanged;
    public RemoteSession? Session => Volatile.Read(ref _session);

    /// <summary>Per-call timeout for the RPC peer (tests shorten it).</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public void AddParticipant(IRemoteParticipant participant)
    {
        lock (_participants) _participants.Add(participant);
    }

    private IRemoteParticipant[] Participants { get { lock (_participants) return _participants.ToArray(); } }

    /// <summary>The live session or an <see cref="RemoteDisconnectedException"/> (callers on any thread; never waits).</summary>
    public RemoteSession Require() =>
        Session is { IsOpen: true } s ? s : throw new RemoteDisconnectedException(Status.State switch
        {
            RemoteConnectionState.Reconnecting => $"Reconnecting to {Host}…",
            RemoteConnectionState.Connecting or RemoteConnectionState.Bootstrapping => $"Still connecting to {Host}…",
            RemoteConnectionState.Failed => $"Not connected to {Host}: {Status.Message}",
            _ => $"Not connected to {Host}",
        });

    private void SetStatus(RemoteConnectionStatus status)
    {
        Volatile.Write(ref _status, status);
        _logLine?.Invoke($"[{status.State}] {status.Message}");
        _statusChanged.Fire(status);
    }

    /// <summary>Connects (no-op when connected). Completes when connected; throws on failure (status <c>Failed</c>).</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await new PoolHop(); // the caller (maybe the UI thread) pays only this: settings, bundle lookup and process start happen off it
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Session is { IsOpen: true }) return;
            _lifetime?.Cancel();
            _lifetime = new CancellationTokenSource();
            SetStatus(new RemoteConnectionStatus(RemoteConnectionState.Connecting, Host, Authority, $"Connecting to {Host}"));
            try
            {
                await EstablishAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                SetStatus(new RemoteConnectionStatus(RemoteConnectionState.Failed, Host, Authority, ex.Message));
                throw;
            }
            catch (OperationCanceledException)
            {
                SetStatus(new RemoteConnectionStatus(RemoteConnectionState.Disconnected, Host, Authority, "Cancelled"));
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>One attempt: transport (+bootstrap), handshake, participants resynchronised. Leaves <see cref="Session"/> set on success.</summary>
    private async Task EstablishAsync(CancellationToken ct)
    {
        var progress = new Progress(this);
        var participants = Participants;
        var session = await RemoteSession.ConnectAsync(_transport(), Mapper, _mode, peer => { foreach (var p in participants) p.Attach(peer); },
            progress, _log, ct, CallTimeout).ConfigureAwait(false);
        session.Resumed = session.Server.InstanceId is { } instance && instance == Volatile.Read(ref _lastInstance);
        Volatile.Write(ref _lastInstance, session.Server.InstanceId);
        try
        {
            foreach (var p in participants) await p.OnConnectedAsync(session, ct).ConfigureAwait(false);
        }
        catch
        {
            foreach (var p in participants) Safe(p.OnDisconnected);
            // Resynchronising failed half-way: drop this connection but leave a persistent server running (the next attempt reattaches to it).
            await session.DetachAsync().ConfigureAwait(false);
            throw;
        }
        var generation = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _session, session);
        // Watch for the drop on the session itself: whatever thread notices, the reconnect runs detached from it.
        session.Peer.Closed.Subscribe(reason => _ = OnLostAsync(generation, reason));
        if (session.Peer.IsClosed) _ = OnLostAsync(generation, null);
        SetStatus(new RemoteConnectionStatus(RemoteConnectionState.Connected, Host, Authority,
            $"Connected to {Host} (server {session.Server.ServerVersion}, {session.Server.Os} {session.Server.Arch}{(session.Resumed ? "; resumed: terminals, debug sessions and tasks kept" : "")})"));
        _sessionChanged.Fire(session);
    }

    private sealed class Progress(RemoteClient client) : IProgress<string>
    {
        public void Report(string value)
        {
            var state = value.StartsWith("Installing", StringComparison.Ordinal) || value.StartsWith("Starting the installed", StringComparison.Ordinal)
                ? RemoteConnectionState.Bootstrapping
                : client.Status.State == RemoteConnectionState.Reconnecting ? RemoteConnectionState.Reconnecting
                : client.Status.State == RemoteConnectionState.Bootstrapping ? RemoteConnectionState.Bootstrapping : RemoteConnectionState.Connecting;
            client.SetStatus(client.Status with { State = state, Message = value });
        }
    }

    private async Task OnLostAsync(int generation, Exception? reason)
    {
        await new PoolHop(); // never on the reader thread that noticed the drop
        var lifetime = _lifetime;
        if (Volatile.Read(ref _generation) != generation || lifetime is null || lifetime.IsCancellationRequested) return; // a deliberate disconnect, or old news
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _generation) != generation || lifetime.IsCancellationRequested) return;
            var old = Interlocked.Exchange(ref _session, null);
            foreach (var p in Participants) Safe(p.OnDisconnected);
            _sessionChanged.Fire(null);
            if (old is not null) await old.DetachAsync().ConfigureAwait(false); // lost, not closed: a persistent server waits for the reconnect
            _log.LogWarning("Connection to {Host} lost: {Reason}", Host, reason?.Message ?? "closed");

            for (var attempt = 1; ; attempt++)
            {
                if (_policy.Delay(attempt) is not { } delay)
                {
                    SetStatus(new RemoteConnectionStatus(RemoteConnectionState.Failed, Host, Authority, $"Connection lost and {attempt - 1} reconnect attempts failed: {reason?.Message}"));
                    return;
                }
                SetStatus(new RemoteConnectionStatus(RemoteConnectionState.Reconnecting, Host, Authority, $"Reconnecting in {delay.TotalSeconds:0.0} s (attempt {attempt})", attempt));
                try
                {
                    await Task.Delay(delay, _time, lifetime.Token).ConfigureAwait(false);
                    await EstablishAsync(lifetime.Token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (RemoteBootstrapException ex) when (attempt >= 1 && IsPermanent(ex))
                {
                    SetStatus(new RemoteConnectionStatus(RemoteConnectionState.Failed, Host, Authority, ex.Message));
                    return;
                }
                catch (Exception ex)
                {
                    reason = ex;
                    _log.LogInformation("Reconnect attempt {Attempt} to {Host} failed: {Error}", attempt, Host, ex.Message);
                }
            }
        }
        finally { _gate.Release(); }
    }

    // Retrying cannot fix these (no bundle for the platform, checksum, protocol mismatch).
    private static bool IsPermanent(RemoteBootstrapException ex) =>
        ex.Message.Contains("bundle", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("protocol", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase);

    /// <summary>Closes the connection (stopping the server and its transport process). Idempotent; ends in <c>Disconnected</c>.</summary>
    public async Task DisconnectAsync()
    {
        await new PoolHop();
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        lifetime?.Cancel(); // stops a reconnect loop (which holds the gate while it waits)
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref _generation);
            var old = Interlocked.Exchange(ref _session, null);
            if (old is not null)
            {
                foreach (var p in Participants) Safe(p.OnDisconnected);
                _sessionChanged.Fire(null);
                await old.DisposeAsync().ConfigureAwait(false);
            }
            SetStatus(new RemoteConnectionStatus(RemoteConnectionState.Disconnected, Host, Authority, "Disconnected"));
        }
        finally
        {
            _gate.Release();
            lifetime?.Dispose();
        }
    }

    /// <summary>Drops the current connection (if any) and connects again.</summary>
    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        await DisconnectAsync().ConfigureAwait(false);
        await ConnectAsync(ct).ConfigureAwait(false);
    }

    private void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { _log.LogWarning(ex, "Remote participant failed to detach"); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _statusChanged.Clear();
        _sessionChanged.Clear();
    }
}
