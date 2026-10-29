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
/// thread (in order, never concurrently per subscription). Watches survive reconnects: they are re-armed on the new session, and the subscriber gets
/// a <c>Changed(root)</c> "rescan" then, since events during the outage were lost. Changes invalidate the directory cache before the callback runs.
/// </summary>
public sealed class RemoteWatcher : IFileWatcherProvider, IRemoteParticipant
{
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

    private sealed class Subscription(string id, Uri root, string path, bool recursive, IReadOnlyList<string>? excludes, Action<IReadOnlyList<FileChange>> onChanges, SshUriMapper mapper)
    {
        public string Id { get; } = id;
        public Uri Root { get; } = root;
        public string Path { get; } = path;
        public bool Recursive { get; } = recursive;
        public IReadOnlyList<string>? Excludes { get; } = excludes;
        public Action<IReadOnlyList<FileChange>>? OnChanges = onChanges;
        public SshUriMapper Mapper { get; } = mapper;
        public RemoteSession? ArmedOn;
        public readonly TaskCompletionSource FirstArmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A remote watch. <see cref="Ready"/> completes when the server's OS watch is armed (changes before that are not reported).</summary>
    public sealed class Handle(Action dispose, Task ready) : IDisposable
    {
        private Action? _dispose = dispose;
        public Task Ready { get; } = ready;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges)
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
        var sub = new Subscription(Guid.NewGuid().ToString("N"), root, path, recursive, excludeGlobs, onChanges, mapper);
        _subs[sub.Id] = sub;
        if (session is not null) _ = ArmAsync(sub, session, rescan: false);
        return new Handle(() =>
        {
            if (!_subs.TryRemove(sub.Id, out _)) return;
            sub.OnChanges = null; // a batch already queued is dropped, and the closure is not kept
            sub.FirstArmed.TrySetCanceled();
            if (sub.ArmedOn is { IsOpen: true } s) s.Peer.Fire(RemoteMethods.WatchStop, new WatchStopParams(sub.Id)); // fire and forget
        }, sub.FirstArmed.Task);
    }

    private async Task ArmAsync(Subscription sub, RemoteSession session, bool rescan)
    {
        try
        {
            await session.Peer.InvokeAsync<WatchStartParams, Empty>(RemoteMethods.WatchStart, new WatchStartParams(sub.Id, sub.Path, sub.Recursive, sub.Excludes)).ConfigureAwait(false);
            sub.ArmedOn = session;
            if (!_subs.ContainsKey(sub.Id)) { session.Peer.Fire(RemoteMethods.WatchStop, new WatchStopParams(sub.Id)); return; } // disposed meanwhile
            sub.FirstArmed.TrySetResult();
            if (rescan) Deliver(sub, [new FileChange(sub.Root, FileChangeKind.Changed)]);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            _log.LogWarning("Could not watch {Root} on the remote: {Error}", sub.Root, ex.Message);
        }
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
        foreach (var sub in _subs.Values.ToArray())
            if (sub.Mapper.Authority == (session.Mapper as SshUriMapper)?.Authority)
                await ArmAsync(sub, session, rescan: sub.ArmedOn is not null).ConfigureAwait(false);
    }

    void IRemoteParticipant.OnDisconnected()
    {
        _endpoint.Directories.Clear();
    }

    private void Deliver(Subscription sub, IReadOnlyList<FileChange> changes)
    {
        try { sub.OnChanges?.Invoke(changes); }
        catch (Exception ex) { _log.LogWarning(ex, "A watcher callback for {Root} failed", sub.Root); } // keeps receiving, like the local watcher
    }
}
