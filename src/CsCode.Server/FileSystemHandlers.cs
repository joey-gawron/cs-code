using System.Collections.Concurrent;
using CsCode.Core.Platform;
using CsCode.Platform.Io;
using CsCode.Remote.Protocol;

namespace CsCode.Server;

/// <summary>
/// <c>fs/*</c>: the server side of the client's <c>ssh:</c> file system. Everything goes through the Platform file system (atomic writes, trash,
/// symlink rules — the same behaviour as a local workspace) except range reads, which read just the requested chunk on the IO lane.
/// Uploads of large files arrive in chunks into a bounded buffer and are committed with one atomic write; conditional writes compare etags.
/// </summary>
internal sealed class FileSystemHandlers : IDisposable
{
    /// <summary>Largest file accepted by a chunked upload (the editor refuses to open bigger files anyway).</summary>
    public const long MaxUploadBytes = 512L * 1024 * 1024;
    public const int MaxConcurrentUploads = 8;
    private static readonly TimeSpan UploadIdleTimeout = TimeSpan.FromMinutes(2);

    private readonly IFileSystem _fs;
    private readonly IoLane _lane;
    private readonly Func<bool> _gzip;
    private readonly ConcurrentDictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
    private readonly Timer _sweeper;

    private sealed class Upload(string path, byte[] buffer, bool createParents, string? expectedEtag)
    {
        public string Path { get; } = path;
        public byte[] Buffer { get; } = buffer;
        public bool CreateParents { get; } = createParents;
        public string? ExpectedEtag { get; } = expectedEtag;
        public long Received;
        public long LastTouched = Environment.TickCount64;
    }

    public FileSystemHandlers(IFileSystem fs, IoLane lane, Func<bool> gzip)
    {
        _fs = fs;
        _lane = lane;
        _gzip = gzip;
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public int ActiveUploads => _uploads.Count;

    public void Register(RpcPeer peer)
    {
        peer.OnRequest<PathParams, WireStat?>(RemoteMethods.FsStat, async (p, ct) => await StatAsync(p.Path, ct).ConfigureAwait(false));
        peer.OnRequest<PathParams, ReadDirResult>(RemoteMethods.FsReadDir, async (p, ct) =>
            new ReadDirResult(await _fs.ReadDirectoryAsync(Uri(p.Path), ct).ConfigureAwait(false)));
        peer.OnRequest<ReadParams, ReadResult>(RemoteMethods.FsRead, (p, ct) => new ValueTask<ReadResult>(ReadRange(p, ct)));
        peer.OnRequest<WriteParams, WireStat?>(RemoteMethods.FsWrite, async (p, ct) =>
        {
            await CheckEtagAsync(p.Path, p.ExpectedEtag, ct).ConfigureAwait(false);
            await _fs.WriteFileAsync(Uri(p.Path), p.Content.Decode(), p.CreateParents, ct).ConfigureAwait(false);
            return await StatAsync(p.Path, ct).ConfigureAwait(false);
        });
        peer.OnRequest<WriteBeginParams, WriteBeginResult>(RemoteMethods.FsWriteBegin, (p, _) => ValueTask.FromResult(Begin(p)));
        peer.OnRequest<WriteChunkParams, Empty>(RemoteMethods.FsWriteChunk, (p, _) => { Chunk(p); return ValueTask.FromResult(Empty.Value); });
        peer.OnRequest<UploadParams, WireStat?>(RemoteMethods.FsWriteCommit, async (p, ct) => await CommitAsync(p.UploadId, ct).ConfigureAwait(false));
        peer.OnRequest<UploadParams, Empty>(RemoteMethods.FsWriteAbort, (p, ct) => { _uploads.TryRemove(p.UploadId, out var _); return ValueTask.FromResult(Empty.Value); });
        peer.OnRequest<PathParams, Empty>(RemoteMethods.FsMkdir, async (p, ct) => { await _fs.CreateDirectoryAsync(Uri(p.Path), ct).ConfigureAwait(false); return Empty.Value; });
        peer.OnRequest<DeleteParams, Empty>(RemoteMethods.FsDelete, async (p, ct) =>
        {
            await _fs.DeleteAsync(Uri(p.Path), p.Recursive, p.UseTrash, ct).ConfigureAwait(false);
            return Empty.Value;
        });
        peer.OnRequest<MoveParams, Empty>(RemoteMethods.FsRename, async (p, ct) =>
        {
            await _fs.RenameAsync(Uri(p.Source), Uri(p.Target), p.Overwrite, ct).ConfigureAwait(false);
            return Empty.Value;
        });
        peer.OnRequest<MoveParams, Empty>(RemoteMethods.FsCopy, async (p, ct) =>
        {
            await _fs.CopyAsync(Uri(p.Source), Uri(p.Target), p.Overwrite, ct).ConfigureAwait(false);
            return Empty.Value;
        });
    }

    internal static Uri Uri(string path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.Path.IsPathRooted(path)) throw new ArgumentException($"Not an absolute path: '{path}'");
        return FileUri.FromPath(path);
    }

    private async ValueTask<WireStat?> StatAsync(string path, CancellationToken ct)
    {
        var s = await _fs.StatAsync(Uri(path), ct).ConfigureAwait(false);
        return s is null ? null : new WireStat(s.Type, s.Size, s.ModifiedUtc, s.IsReadOnly, WireStat.EtagOf(s.ModifiedUtc, s.Size));
    }

    private async ValueTask CheckEtagAsync(string path, string? expected, CancellationToken ct)
    {
        if (expected is null) return;
        var current = await StatAsync(path, ct).ConfigureAwait(false);
        var actual = current?.Etag ?? "";
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new RemoteFileModifiedException(current is null
                ? $"'{path}' was deleted on the remote since it was read"
                : $"'{path}' was changed on the remote since it was read");
    }

    private Task<ReadResult> ReadRange(ReadParams p, CancellationToken ct)
    {
        var path = p.Path;
        _ = Uri(path); // validates
        var length = Math.Clamp(p.Length, 0, Payload.ChunkSize);
        var gzip = _gzip();
        return _lane.RunAsync(token =>
        {
            if (Directory.Exists(path)) throw new UnauthorizedAccessException($"'{path}' is a directory");
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var info = new FileInfo(path);
            var size = RandomAccess.GetLength(handle);
            var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            var n = (int)Math.Max(0, Math.Min(length, size - p.Offset));
            var buffer = new byte[n];
            var read = 0;
            while (read < n)
            {
                token.ThrowIfCancellationRequested();
                var r = RandomAccess.Read(handle, buffer.AsSpan(read), p.Offset + read);
                if (r <= 0) break;
                read += r;
            }
            return new ReadResult(Payload.Encode(buffer.AsSpan(0, read), gzip), size, WireStat.EtagOf(modified, size));
        }, ct);
    }

    private WriteBeginResult Begin(WriteBeginParams p)
    {
        _ = Uri(p.Path);
        if (p.Size < 0 || p.Size > MaxUploadBytes) throw new IOException($"Uploads are limited to {MaxUploadBytes / (1024 * 1024)} MB ({p.Size} bytes requested)");
        if (_uploads.Count >= MaxConcurrentUploads) throw new RemoteException(RpcErrorCodes.Busy, "Too many uploads in progress");
        var id = Guid.NewGuid().ToString("N");
        _uploads[id] = new Upload(p.Path, new byte[p.Size], p.CreateParents, p.ExpectedEtag);
        return new WriteBeginResult(id);
    }

    private void Chunk(WriteChunkParams p)
    {
        if (!_uploads.TryGetValue(p.UploadId, out var u)) throw new IOException("Unknown or expired upload");
        var bytes = p.Chunk.Decode();
        if (p.Offset < 0 || p.Offset + bytes.Length > u.Buffer.Length) throw new ArgumentException("Chunk outside the announced size");
        bytes.CopyTo(u.Buffer.AsSpan((int)p.Offset));
        Interlocked.Add(ref u.Received, bytes.Length);
        Volatile.Write(ref u.LastTouched, Environment.TickCount64);
    }

    private async ValueTask<WireStat?> CommitAsync(string id, CancellationToken ct)
    {
        if (!_uploads.TryRemove(id, out var u)) throw new IOException("Unknown or expired upload");
        if (Interlocked.Read(ref u.Received) != u.Buffer.Length) throw new IOException($"Upload incomplete: {u.Received} of {u.Buffer.Length} bytes");
        await CheckEtagAsync(u.Path, u.ExpectedEtag, ct).ConfigureAwait(false);
        await _fs.WriteFileAsync(Uri(u.Path), u.Buffer, u.CreateParents, ct).ConfigureAwait(false);
        return await StatAsync(u.Path, ct).ConfigureAwait(false);
    }

    private void Sweep()
    {
        var now = Environment.TickCount64;
        foreach (var (id, u) in _uploads)
            if (now - Volatile.Read(ref u.LastTouched) > UploadIdleTimeout.TotalMilliseconds) _uploads.TryRemove(id, out _);
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        _uploads.Clear();
    }
}
