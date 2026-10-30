using System.Collections.Concurrent;
using CsCode.Core.Platform;
using CsCode.Platform.Io;
using CsCode.Platform.Watching;
using CsCode.Remote.Protocol;

namespace CsCode.Server;

/// <summary>
/// <c>watch/*</c>: client watches become Platform <see cref="IFileWatcher"/> watches on the server (recursive, debounced, exclude-aware, missing
/// roots wait); each delivered batch is sent as one <c>watch/changes</c> notification with paths. A watch's wanted paths (the client Explorer's
/// expanded folders inside excluded ones) come with <c>watch/start</c> and change through <c>watch/wants</c>; they are served like local ones,
/// through the root's one OS watch. All watches end with the server. A client that reconnected to a persistent server re-arms its watches
/// with the same ids (the new watch replaces the old one), then names the ones it still has (<c>watch/keep</c>): every other watch ends.
/// </summary>
internal sealed class WatchHandlers(IFileWatcher watcher) : IDisposable
{
    public const int MaxWatches = 512;
    private readonly ConcurrentDictionary<string, IDisposable> _watches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FileWatchWants> _wants = new(StringComparer.Ordinal);

    private static IEnumerable<Uri> Directories(IReadOnlyList<string>? paths)
    {
        foreach (var p in paths ?? [])
        {
            Uri uri;
            try { uri = FileUri.FromDirectoryPath(FileSystemHandlers.Uri(p).LocalPath); }
            catch (ArgumentException) { continue; } // not an absolute path: nothing to want
            yield return uri;
        }
    }

    public int Count => _watches.Count;

    public void Register(IRpcEndpoint peer)
    {
        peer.OnRequest<WatchStartParams, Empty>(RemoteMethods.WatchStart, async (p, ct) =>
        {
            if (_watches.Count >= MaxWatches && !_watches.ContainsKey(p.WatchId)) throw new RemoteException(RpcErrorCodes.Busy, $"More than {MaxWatches} watches");
            var root = FileUri.FromDirectoryPath(FileSystemHandlers.Uri(p.Root).LocalPath);
            var id = p.WatchId;
            var wants = new FileWatchWants(Directories(p.WantDirectories), Directories(p.WantTrees));
            var handle = watcher.Watch(root, p.Recursive, p.Excludes, changes =>
            {
                var wire = new List<WireFileChange>(changes.Count);
                foreach (var c in changes)
                {
                    try { wire.Add(new WireFileChange(FileUri.ToPath(c.Uri), c.Kind)); }
                    catch (NotSupportedException) { /* not a file: URI: nothing the client can map */ }
                }
                if (wire.Count > 0) peer.Notify(RemoteMethods.WatchChanges, new WatchChangesParams(id, wire));
            }, wants);
            if (_watches.TryRemove(id, out var replaced)) replaced.Dispose(); // re-armed after a reconnect
            _watches[id] = handle;
            _wants[id] = wants;
            // Answer once the OS watch is armed (events before that are not reported), so the client knows from when on it sees changes.
            if (handle is FileWatchHandle armed)
                await Task.WhenAny(armed.Ready, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);
            return Empty.Value;
        });
        peer.OnRequest<WatchStopParams, Empty>(RemoteMethods.WatchStop, (p, _) =>
        {
            if (_watches.TryRemove(p.WatchId, out var w)) w.Dispose();
            _wants.TryRemove(p.WatchId, out FileWatchWants? _);
            return ValueTask.FromResult(Empty.Value);
        });
        // Answered once the server's OS watch follows the new wants (FileWatchWants.SetAsync), so the client's SetAsync means the same as locally.
        peer.OnRequest<WatchWantsParams, Empty>(RemoteMethods.WatchWants, async (p, _) =>
        {
            if (_wants.TryGetValue(p.WatchId, out var wants)) await wants.SetAsync(Directories(p.Directories), Directories(p.Trees)).ConfigureAwait(false);
            return Empty.Value;
        });
        // After a (re)connect: watches the client stopped while it was away (its watch/stop could not reach us) and those of a previous client
        // process of this session are not claimed, and end here.
        peer.OnRequest<WatchKeepParams, Empty>(RemoteMethods.WatchKeep, (p, _) =>
        {
            var keep = p.WatchIds.ToHashSet(StringComparer.Ordinal);
            foreach (var id in _watches.Keys.Where(id => !keep.Contains(id)).ToArray())
            {
                if (_watches.TryRemove(id, out var w)) w.Dispose();
                _wants.TryRemove(id, out FileWatchWants? _);
            }
            return ValueTask.FromResult(Empty.Value);
        });
    }

    public void Dispose()
    {
        foreach (var id in _watches.Keys.ToArray())
            if (_watches.TryRemove(id, out var w)) w.Dispose();
        _wants.Clear();
    }
}
