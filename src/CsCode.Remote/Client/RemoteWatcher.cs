using System.Collections.Concurrent;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Remote.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Remote.Client;

/// <summary>
/// The <c>ssh:</c> file watcher. Each <see cref="Watch"/> becomes a server-side Platform watch (recursive, debounced, exclude-aware, a missing root
/// waits for it); its batches come back as <c>watch/changes</c> and are delivered, mapped to <c>ssh:</c> URIs, on the connection's single handler
/// thread (in order, never concurrently per subscription). Watches survive reconnects: they are re-armed on the new session and the server is told
/// which watches the client still has (<c>watch/keep</c>): one stopped while disconnected, or left by a previous client process, ends there.
/// Wanted paths (<see cref="FileWatchWants"/>) go to a server that takes them (<see cref="RemoteProtocol.Capabilities.WatchWants"/>), with the
/// watch and whenever they change, and the server serves them like local ones; an older server watches without them. Since
/// events during the outage were lost, each re-armed subscriber then gets a "rescan": <c>Changed(root)</c> and, for a watch of one directory (not a
/// tree), <c>Changed</c> for each of its entries, so a subscriber that looks for its own file (an open editor checks the disk for it) sees it. The
/// rescan's listings go out once the reconnected session is published, never on the reconnect's own path (terminal output, debug streams and file
/// operations resume first). Changes invalidate the directory cache before the callback runs.
/// </summary>
public sealed class RemoteWatcher : IFileWatcherProvider, IRemoteParticipant
{
    /// <summary>A directory with more entries is rescanned as <c>Changed(root)</c> only (bounded work and allocation per reconnect).</summary>
    public const int MaxRescanEntries = 20_000;

    private readonly IRemoteEndpoint _endpoint;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, Subscription> _subs = new(StringComparer.Ordinal);

    public RemoteWatcher(IRemoteEndpoint endpoint, ILogger? log = null)
    {
        _endpoint = endpoint;
        _log = log ?? NullLogger.Instance;
    }

    public string Scheme => SshUriMapper.Scheme;
    public int Count => _subs.Count;

    private sealed class Subscription(string id, Uri root, string path, bool recursive, IReadOnlyList<string>? excludes, Action<IReadOnlyList<FileChange>> onChanges,
        SshUriMapper mapper, FileWatchWants? wants)
    {
        public string Id { get; } = id;
        public FileWatchWants? Wants { get; } = wants;
        public IDisposable? WantsSubscription;
        public Uri Root { get; } = root;
        public string Path { get; } = path;
        public bool Recursive { get; } = recursive;
        public IReadOnlyList<string>? Excludes { get; } = excludes;
        public Action<IReadOnlyList<FileChange>>? OnChanges = onChanges;
        public SshUriMapper Mapper { get; } = mapper;
        public RemoteSession? ArmedOn;
        public bool WasArmed;
        /// <summary>
        /// Held while the wanted paths go to the server — with <c>watch/start</c> or as <c>watch/wants</c> — so a change made while the watch is
        /// being armed (its <c>watch/start</c> answers once the server's walk is done) is sent after it, never lost or overtaken.
        /// </summary>
        public readonly SemaphoreSlim SendingWants = new(1, 1);
        /// <summary>Held while delivering: the handler thread's batches and a rescan never reach the subscriber at the same time.</summary>
        public readonly object Delivering = new();
        public readonly TaskCompletionSource FirstArmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A remote watch. <see cref="Ready"/> completes when the server's OS watch is armed (changes before that are not reported).</summary>
    public sealed class Handle(Action dispose, Task ready) : IDisposable
    {
        private Action? _dispose = dispose;
        public Task Ready { get; } = ready;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges) =>
        Watch(root, recursive, excludeGlobs, onChanges, null);

    public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges, FileWatchWants? wants)
    {
        ArgumentNullException.ThrowIfNull(onChanges);
        SshUriMapper mapper;
        RemoteSession? session = null;
        try { (session, mapper) = _endpoint.Resolve(root); }
        catch (IOException)
        {
            // Not connected (yet): nothing to watch now; the subscription is armed when a session for this authority comes up.
            if (_endpoint.MapperFor(root) is { } pending) mapper = pending;
            else return Disposable.Empty;
        }
        var path = mapper.ToRemotePath(root);
        var sub = new Subscription(Guid.NewGuid().ToString("N"), root, path, recursive, excludeGlobs, onChanges, mapper, wants);
        sub.WantsSubscription = wants?.Subscribe(() => SendWantsAsync(sub));
        _subs[sub.Id] = sub;
        if (session is not null) _ = ArmAsync(sub, session, reconnect: false);
        return new Handle(() =>
        {
            if (!_subs.TryRemove(sub.Id, out _)) return;
            sub.WantsSubscription?.Dispose();
            sub.OnChanges = null; // a batch already queued is dropped, and the closure is not kept
            sub.FirstArmed.TrySetCanceled();
            if (sub.ArmedOn is { IsOpen: true } s) s.Peer.Fire(RemoteMethods.WatchStop, new WatchStopParams(sub.Id)); // fire and forget
        }, sub.FirstArmed.Task);
    }

    /// <param name="reconnect">Armed again after a reconnect (else: a first arm).</param>
    /// <returns>Whether the subscriber is due a rescan: it was armed before the connection dropped, so it missed the outage's events.</returns>
    private async Task<bool> ArmAsync(Subscription sub, RemoteSession session, bool reconnect)
    {
        await sub.SendingWants.WaitAsync().ConfigureAwait(false);
        try
        {
            var (directories, trees) = TakesWants(session) ? WantPaths(sub) : (null, null);
            await session.Peer.InvokeAsync<WatchStartParams, Empty>(RemoteMethods.WatchStart,
                new WatchStartParams(sub.Id, sub.Path, sub.Recursive, sub.Excludes, directories, trees)).ConfigureAwait(false);
            var rescan = reconnect && sub.WasArmed;
            sub.ArmedOn = session;
            sub.WasArmed = true;
            if (!_subs.ContainsKey(sub.Id)) { session.Peer.Fire(RemoteMethods.WatchStop, new WatchStopParams(sub.Id)); return false; } // disposed meanwhile
            sub.FirstArmed.TrySetResult();
            return rescan;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            _log.LogWarning("Could not watch {Root} on the remote: {Error}", sub.Root, ex.Message);
            return false;
        }
        finally { sub.SendingWants.Release(); }
    }

    /// <summary>
    /// The rescans of one reconnect, once its session is published (the reconnect does not wait for them: each listing is a round trip plus up to
    /// <see cref="MaxRescanEntries"/> entries). A session dropped again meanwhile is left alone: the next reconnect rescans.
    /// </summary>
    private async Task RescanAfterPublishAsync(RemoteSession session, IReadOnlyList<Subscription> due)
    {
        try
        {
            var (published, _) = await _endpoint.ResolveAsync(due[0].Root, CancellationToken.None).ConfigureAwait(false);
            if (!ReferenceEquals(published, session)) return;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { return; }
        var listings = new Dictionary<string, Task<ReadDirResult?>>(StringComparer.Ordinal); // several editors watch the same directory: one listing
        foreach (var sub in due)
        {
            if (!_subs.ContainsKey(sub.Id) || !session.IsOpen) continue;
            Deliver(sub, await RescanAsync(sub, session, listings).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// What may have changed while nobody was watching: the root, and every entry of a watched directory. A subscriber that filters for one file
    /// (an editor watching its file's directory) never matches <c>Changed(root)</c>, so without its own entry it would not look at the disk again
    /// and a later save would overwrite whatever changed on the server during the outage. A tree (recursive) watch gets <c>Changed(root)</c> only:
    /// its subscribers refresh from the root.
    /// </summary>
    private async Task<IReadOnlyList<FileChange>> RescanAsync(Subscription sub, RemoteSession session, Dictionary<string, Task<ReadDirResult?>> listings)
    {
        var changes = new List<FileChange> { new(sub.Root, FileChangeKind.Changed) };
        if (sub.Recursive) return changes;
        if (!listings.TryGetValue(sub.Path, out var listing))
            listings[sub.Path] = listing = session.Peer.InvokeAsync<PathParams, ReadDirResult>(RemoteMethods.FsReadDir, new PathParams(sub.Path));
        try
        {
            if (await listing.ConfigureAwait(false) is { Entries: { Count: > 0 and <= MaxRescanEntries } entries })
            {
                var dir = sub.Path.EndsWith('/') ? sub.Path : sub.Path + "/";
                foreach (var e in entries) changes.Add(new FileChange(sub.Mapper.FromRemotePath(dir + e.Name), FileChangeKind.Changed));
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            _log.LogDebug("Rescanning {Root} after the reconnect failed: {Error}", sub.Root, ex.Message);
        }
        return changes;
    }

    private static bool TakesWants(RemoteSession session) => session.Server.Capabilities.Contains(RemoteProtocol.Capabilities.WatchWants);

    /// <summary>A subscription's wanted paths as server paths (those not on its host are left out); nulls when it has none.</summary>
    private static (IReadOnlyList<string>? Directories, IReadOnlyList<string>? Trees) WantPaths(Subscription sub)
    {
        if (sub.Wants is not { } wants) return (null, null);
        return (Map(wants.Directories), Map(wants.Trees));

        List<string> Map(IReadOnlyList<Uri> uris)
        {
            var paths = new List<string>(uris.Count);
            foreach (var uri in uris)
            {
                try { paths.Add(sub.Mapper.ToRemotePath(uri)); }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentException) { } // another host's path
            }
            return paths;
        }
    }

    /// <summary>
    /// The subscription's wants changed: tell the server its watch is on (an older server, or none connected: nothing; the next arming sends
    /// them). While the watch is being armed this waits for the arming, then sends: the paths the arming took may be older than these.
    /// Completes once the server's OS watch follows them, or the server could not be reached.
    /// </summary>
    private async Task SendWantsAsync(Subscription sub)
    {
        await sub.SendingWants.WaitAsync().ConfigureAwait(false);
        try
        {
            if (sub.ArmedOn is not { IsOpen: true } session || !TakesWants(session) || !_subs.ContainsKey(sub.Id)) return;
            var (directories, trees) = WantPaths(sub);
            await session.Peer.InvokeAsync<WatchWantsParams, Empty>(RemoteMethods.WatchWants, new WatchWantsParams(sub.Id, directories ?? [], trees ?? [])).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or RemoteException)
        {
            _log.LogDebug("Could not send the wanted paths of {Root}: {Error}", sub.Root, ex.Message);
        }
        finally { sub.SendingWants.Release(); }
    }

    void IRemoteParticipant.Attach(RpcPeer peer) =>
        peer.OnNotification<WatchChangesParams>(RemoteMethods.WatchChanges, p =>
        {
            if (!_subs.TryGetValue(p.WatchId, out var sub)) return;
            var changes = new List<FileChange>(p.Changes.Count);
            foreach (var c in p.Changes)
            {
                if (c.Kind == FileChangeKind.Changed) _endpoint.Directories.InvalidatePath(c.Path);
                else _endpoint.Directories.InvalidateTree(c.Path);
                changes.Add(new FileChange(sub.Mapper.FromRemotePath(c.Path), c.Kind));
            }
            Deliver(sub, changes);
        });

    async Task IRemoteParticipant.OnConnectedAsync(RemoteSession session, CancellationToken ct)
    {
        var authority = (session.Mapper as SshUriMapper)?.Authority;
        var due = new List<Subscription>();
        foreach (var sub in _subs.Values.ToArray())
            if (sub.Mapper.Authority == authority && await ArmAsync(sub, session, reconnect: true).ConfigureAwait(false))
                due.Add(sub);
        // The server stops every watch we no longer have: one disposed while we were disconnected (its watch/stop could not be sent), or all of a
        // previous client process's (a restarted client reattaching to the server a crashed one left running).
        var keep = _subs.Values.Where(s => s.Mapper.Authority == authority).Select(s => s.Id).ToArray();
        try { await session.Peer.InvokeAsync<WatchKeepParams, Empty>(RemoteMethods.WatchKeep, new WatchKeepParams(keep), ct).ConfigureAwait(false); }
        catch (RemoteException ex) when (ex.Code == RpcErrorCodes.MethodNotFound) { _log.LogDebug("The server cannot drop unclaimed watches: {Error}", ex.Message); }
        if (due.Count > 0) _ = RescanAfterPublishAsync(session, due);
    }

    void IRemoteParticipant.OnDisconnected()
    {
        foreach (var sub in _subs.Values) sub.ArmedOn = null; // never keep a closed session alive
        _endpoint.Directories.Clear();
    }

    private void Deliver(Subscription sub, IReadOnlyList<FileChange> changes)
    {
        lock (sub.Delivering)
        {
            try { sub.OnChanges?.Invoke(changes); }
            catch (Exception ex) { _log.LogWarning(ex, "A watcher callback for {Root} failed", sub.Root); } // keeps receiving, like the local watcher
        }
    }
}
