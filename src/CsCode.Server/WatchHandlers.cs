using System.Collections.Concurrent;
using CsCode.Core.Platform;
using CsCode.Platform.Io;
using CsCode.Remote.Protocol;

namespace CsCode.Server;

/// <summary>
/// <c>watch/*</c>: client watches become Platform <see cref="IFileWatcher"/> watches on the server (recursive, debounced, exclude-aware, missing
/// roots wait); each delivered batch is sent as one <c>watch/changes</c> notification with paths. All watches end with the connection.
/// </summary>
internal sealed class WatchHandlers(IFileWatcher watcher) : IDisposable
{
    public const int MaxWatches = 512;
    private readonly ConcurrentDictionary<string, IDisposable> _watches = new(StringComparer.Ordinal);

    public int Count => _watches.Count;

    public void Register(RpcPeer peer)
    {
        peer.OnRequest<WatchStartParams, Empty>(RemoteMethods.WatchStart, (p, _) =>
        {
            if (_watches.Count >= MaxWatches) throw new RemoteException(RpcErrorCodes.Busy, $"More than {MaxWatches} watches");
            var root = FileUri.FromDirectoryPath(FileSystemHandlers.Uri(p.Root).LocalPath);
            var id = p.WatchId;
            var handle = watcher.Watch(root, p.Recursive, p.Excludes, changes =>
            {
                var wire = new List<WireFileChange>(changes.Count);
                foreach (var c in changes)
                {
                    try { wire.Add(new WireFileChange(FileUri.ToPath(c.Uri), c.Kind)); }
                    catch (NotSupportedException) { /* not a file: URI: nothing the client can map */ }
                }
                if (wire.Count > 0) peer.Notify(RemoteMethods.WatchChanges, new WatchChangesParams(id, wire));
            });
            if (!_watches.TryAdd(id, handle))
            {
                handle.Dispose();
                throw new ArgumentException($"Duplicate watch id '{id}'");
            }
            return ValueTask.FromResult(Empty.Value);
        });
        peer.OnRequest<WatchStopParams, Empty>(RemoteMethods.WatchStop, (p, _) =>
        {
            if (_watches.TryRemove(p.WatchId, out var w)) w.Dispose();
            return ValueTask.FromResult(Empty.Value);
        });
    }

    public void Dispose()
    {
        foreach (var id in _watches.Keys.ToArray())
            if (_watches.TryRemove(id, out var w)) w.Dispose();
    }
}
