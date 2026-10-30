using CsCode.Core.Diagnostics;
using CsCode.Core.Platform;
using CsCode.Remote.Protocol;

namespace CsCode.Remote.Client;

/// <summary>What the <c>ssh:</c> providers need from the connection: the live session for a URI and the shared listing cache.</summary>
public interface IRemoteEndpoint
{
    /// <summary>The session serving <paramref name="uri"/> and its mapper; throws <see cref="RemoteDisconnectedException"/> (an IOException) when that host is not connected.</summary>
    (RemoteSession Session, SshUriMapper Mapper) Resolve(Uri uri);
    /// <summary>The mapper of the host <paramref name="uri"/> belongs to when it is the configured/connecting one (even while disconnected), else null.</summary>
    SshUriMapper? MapperFor(Uri uri);
    DirectoryCache Directories { get; }
}

/// <summary>
/// The <c>ssh:</c> file system (<c>ssh://[user@]host[:port]/abs/path</c>): every call is a request to the connected server, which performs it with
/// Platform's local file system next to the files (atomic temp+rename writes, trash, symlink rules). Same contract and exception types as the
/// local file system (FileNotFound, DirectoryNotFound, UnauthorizedAccess, NotSupported, IOException). Calls only enqueue on the caller's
/// thread; requests pipeline. Large files move in 256 KB chunks with up to <see cref="Window"/> chunks in flight (gzip when the server
/// supports it); a read whose chunks see different etags (file changed mid-read) starts over. Directory listings are cached briefly
/// (<see cref="DirectoryCache"/>). When the host is not connected, calls fail with an IOException that says so.
/// </summary>
public sealed class RemoteFileSystem : IFileSystemProvider
{
    /// <summary>Chunks in flight per transfer.</summary>
    public const int Window = 8;
    /// <summary>Largest file read in one call (the editor's large-file limit is far below).</summary>
    public const long MaxReadBytes = 512L * 1024 * 1024;

    private readonly IRemoteEndpoint _endpoint;

    public RemoteFileSystem(IRemoteEndpoint endpoint) => _endpoint = endpoint;

    public string Scheme => SshUriMapper.Scheme;

    private (RpcPeer Peer, string Path, RemoteSession Session) Target(Uri uri)
    {
        var (session, mapper) = _endpoint.Resolve(uri);
        return (session.Peer, mapper.ToRemotePath(uri), session);
    }

    public async ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default) => (await StatWithEtagAsync(uri, ct).ConfigureAwait(false))?.ToFileStat();

    /// <summary>Stat plus the etag (mtime/size) for <see cref="WriteFileAsync(Uri, ReadOnlyMemory{byte}, string?, bool, CancellationToken)"/>.</summary>
    public async ValueTask<WireStat?> StatWithEtagAsync(Uri uri, CancellationToken ct = default)
    {
        var (peer, path, _) = Target(uri);
        return await peer.InvokeAsync<PathParams, WireStat?>(RemoteMethods.FsStat, new PathParams(path), ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default)
    {
        var (peer, path, _) = Target(uri);
        if (_endpoint.Directories.TryGet(path, out var cached)) return cached;
        var r = await peer.InvokeAsync<PathParams, ReadDirResult>(RemoteMethods.FsReadDir, new PathParams(path), ct).ConfigureAwait(false);
        var entries = r?.Entries ?? [];
        _endpoint.Directories.Put(path, entries);
        return entries;
    }

    public async ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default)
    {
        var (peer, path, _) = Target(uri);
        for (var attempt = 0; ; attempt++)
        {
            var first = await peer.InvokeAsync<ReadParams, ReadResult>(RemoteMethods.FsRead, new ReadParams(path, 0, Payload.ChunkSize), ct).ConfigureAwait(false)
                        ?? throw new IOException($"Empty answer reading {uri}");
            if (first.Size > MaxReadBytes) throw new IOException($"'{path}' is {first.Size / (1024 * 1024)} MB; remote reads are limited to {MaxReadBytes / (1024 * 1024)} MB");
            var head = first.Chunk.Decode();
            if (first.Size <= head.Length) return head.Length == first.Size ? head : head[..(int)first.Size];

            var result = new byte[first.Size];
            head.CopyTo(result, 0);
            var consistent = await ReadRestAsync(peer, path, result, head.Length, first.Etag, ct).ConfigureAwait(false);
            if (consistent) return result;
            if (attempt >= 2) throw new IOException($"'{path}' keeps changing on the remote while it is being read");
        }
    }

    private static async Task<bool> ReadRestAsync(RpcPeer peer, string path, byte[] target, int from, string etag, CancellationToken ct)
    {
        var offsets = new Queue<long>();
        for (long o = from; o < target.Length; o += Payload.ChunkSize) offsets.Enqueue(o);
        var inFlight = new List<(long Offset, Task<ReadResult?> Task)>();
        var ok = true;
        while (offsets.Count > 0 || inFlight.Count > 0)
        {
            while (offsets.Count > 0 && inFlight.Count < Window)
            {
                var o = offsets.Dequeue();
                inFlight.Add((o, peer.InvokeAsync<ReadParams, ReadResult>(RemoteMethods.FsRead, new ReadParams(path, o, Payload.ChunkSize), ct)));
            }
            var done = await Task.WhenAny(inFlight.Select(f => f.Task)).ConfigureAwait(false);
            var index = inFlight.FindIndex(f => f.Task == done);
            var (offset, task) = inFlight[index];
            inFlight.RemoveAt(index);
            var r = await task.ConfigureAwait(false) ?? throw new IOException($"Empty answer reading {path}");
            if (r.Etag != etag || r.Size != target.Length) { ok = false; continue; } // changed mid-read: let the rest drain, then start over
            var bytes = r.Chunk.Decode();
            var n = (int)Math.Min(bytes.Length, target.Length - offset);
            bytes.AsSpan(0, n).CopyTo(target.AsSpan((int)offset));
        }
        return ok;
    }

    public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default) =>
        new(WriteCoreAsync(uri, content, null, createParents, ct));

    /// <summary>
    /// Conditional write: fails with <see cref="RemoteFileModifiedException"/> when the remote file's etag is no longer <paramref name="expectedEtag"/>
    /// (someone else changed it since it was read; "" = it must not exist). Returns the new stat (with its etag).
    /// </summary>
    public async ValueTask<WireStat?> WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, string? expectedEtag, bool createParents = true, CancellationToken ct = default) =>
        await WriteCoreAsync(uri, content, expectedEtag, createParents, ct).ConfigureAwait(false);

    private async Task<WireStat?> WriteCoreAsync(Uri uri, ReadOnlyMemory<byte> content, string? expectedEtag, bool createParents, CancellationToken ct)
    {
        var (peer, path, session) = Target(uri);
        await new PoolHop(); // encoding/compressing chunks is CPU work: never on the caller's (UI) thread
        try
        {
            if (content.Length <= Payload.ChunkSize)
                return await peer.InvokeAsync<WriteParams, WireStat?>(RemoteMethods.FsWrite,
                    new WriteParams(path, Payload.Encode(content.Span, session.Gzip), createParents, expectedEtag), ct).ConfigureAwait(false);

            var begin = await peer.InvokeAsync<WriteBeginParams, WriteBeginResult>(RemoteMethods.FsWriteBegin,
                new WriteBeginParams(path, content.Length, createParents, expectedEtag), ct).ConfigureAwait(false) ?? throw new IOException("Empty answer to writeBegin");
            try
            {
                var inFlight = new List<Task>();
                for (var offset = 0; offset < content.Length; offset += Payload.ChunkSize)
                {
                    if (inFlight.Count >= Window)
                    {
                        var done = await Task.WhenAny(inFlight).ConfigureAwait(false);
                        inFlight.Remove(done);
                        await done.ConfigureAwait(false);
                    }
                    var chunk = content.Slice(offset, Math.Min(Payload.ChunkSize, content.Length - offset));
                    inFlight.Add(peer.InvokeAsync<WriteChunkParams, Empty>(RemoteMethods.FsWriteChunk,
                        new WriteChunkParams(begin.UploadId, offset, Payload.Encode(chunk.Span, session.Gzip)), ct));
                }
                await Task.WhenAll(inFlight).ConfigureAwait(false);
                return await peer.InvokeAsync<UploadParams, WireStat?>(RemoteMethods.FsWriteCommit, new UploadParams(begin.UploadId), ct).ConfigureAwait(false);
            }
            catch
            {
                peer.Fire(RemoteMethods.FsWriteAbort, new UploadParams(begin.UploadId)); // fire and forget: the server also expires idle uploads
                throw;
            }
        }
        finally { _endpoint.Directories.InvalidatePath(path); }
    }

    public async ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default)
    {
        var (peer, path, _) = Target(uri);
        try { await peer.InvokeAsync<PathParams, Empty>(RemoteMethods.FsMkdir, new PathParams(path), ct).ConfigureAwait(false); }
        finally { _endpoint.Directories.InvalidatePath(path); }
    }

    public async ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default)
    {
        var (peer, path, _) = Target(uri);
        try { await peer.InvokeAsync<DeleteParams, Empty>(RemoteMethods.FsDelete, new DeleteParams(path, recursive, useTrash), ct).ConfigureAwait(false); }
        finally { _endpoint.Directories.InvalidateTree(path); }
    }

    public async ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default)
    {
        var (peer, from, _) = Target(source);
        var (_, to, _) = Target(target);
        try { await peer.InvokeAsync<MoveParams, Empty>(RemoteMethods.FsRename, new MoveParams(from, to, overwrite), ct).ConfigureAwait(false); }
        finally
        {
            _endpoint.Directories.InvalidateTree(from);
            _endpoint.Directories.InvalidateTree(to);
        }
    }

    public async ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default)
    {
        var (peer, from, _) = Target(source);
        var (_, to, _) = Target(target);
        try { await peer.InvokeAsync<MoveParams, Empty>(RemoteMethods.FsCopy, new MoveParams(from, to, overwrite), ct).ConfigureAwait(false); }
        finally { _endpoint.Directories.InvalidateTree(to); }
    }
}
