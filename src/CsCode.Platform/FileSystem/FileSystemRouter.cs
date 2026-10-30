using CsCode.Core.Platform;

namespace CsCode.Platform.FileSystem;

/// <summary>
/// The process-wide <see cref="IFileSystem"/>: dispatches each call by URI scheme. <c>file:</c> goes to <see cref="LocalFileSystem"/>; any other scheme
/// to the <see cref="IFileSystemProvider"/> registered for it (remote <c>ssh:</c> workspaces). An unknown scheme fails like the local file system
/// always did (<see cref="NotSupportedException"/>, on the returned task).
/// <para>Providers are resolved lazily, on the first non-<c>file:</c> call: a provider (the remote connection) depends on configuration, which
/// depends on the file watcher, so resolving them while the container builds the router would be a cycle.</para>
/// <para>Rename/copy across schemes (a remote file onto the local disk and back) copy the bytes of a single file; a directory cannot cross schemes.</para>
/// </summary>
public sealed class FileSystemRouter : IFileSystem
{
    private readonly IFileSystem _local;
    private readonly Lazy<IReadOnlyDictionary<string, IFileSystemProvider>> _providers;

    public FileSystemRouter(IFileSystem local, Func<IEnumerable<IFileSystemProvider>>? providers = null)
    {
        _local = local;
        _providers = new(() => Index(providers?.Invoke() ?? []), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The <c>file:</c> implementation.</summary>
    public IFileSystem Local => _local;

    private static Dictionary<string, IFileSystemProvider> Index(IEnumerable<IFileSystemProvider> providers)
    {
        var map = new Dictionary<string, IFileSystemProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in providers)
        {
            if (string.Equals(p.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)) continue; // file: is always local
            map.TryAdd(p.Scheme, p); // first registration wins, like every TryAdd in this module
        }
        return map;
    }

    /// <summary>True when a call for <paramref name="uri"/> has somewhere to go (file: or a registered provider's scheme).</summary>
    public bool Handles(Uri uri) => uri.IsAbsoluteUri && (uri.IsFile || _providers.Value.ContainsKey(uri.Scheme));

    private IFileSystem For(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.IsFile) return _local; // the local file system reports relative / malformed URIs itself
        return _providers.Value.TryGetValue(uri.Scheme, out var p) ? p : _local;
    }

    public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default) => For(uri).StatAsync(uri, ct);
    public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) => For(uri).ReadDirectoryAsync(uri, ct);
    public ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default) => For(uri).ReadFileAsync(uri, ct);
    public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default) =>
        For(uri).WriteFileAsync(uri, content, createParents, ct);
    public bool PreservesFileOnWrite(Uri uri) => For(uri).PreservesFileOnWrite(uri);
    public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) => For(uri).CreateDirectoryAsync(uri, ct);
    public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) =>
        For(uri).DeleteAsync(uri, recursive, useTrash, ct);

    public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default)
    {
        var from = For(source);
        var to = For(target);
        return ReferenceEquals(from, to) && SameAuthority(source, target)
            ? from.RenameAsync(source, target, overwrite, ct)
            : CrossRenameAsync(from, source, to, target, overwrite, ct);
    }

    public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default)
    {
        var from = For(source);
        var to = For(target);
        return ReferenceEquals(from, to) && SameAuthority(source, target)
            ? from.CopyAsync(source, target, overwrite, ct)
            : CrossCopyAsync(from, source, to, target, overwrite, ct);
    }

    // Two remote hosts are two different file systems even though one provider serves both.
    private static bool SameAuthority(Uri a, Uri b) =>
        a.IsFile && b.IsFile || string.Equals(a.Authority, b.Authority, StringComparison.OrdinalIgnoreCase);

    private static async ValueTask CrossCopyAsync(IFileSystem from, Uri source, IFileSystem to, Uri target, bool overwrite, CancellationToken ct)
    {
        var stat = await from.StatAsync(source, ct).ConfigureAwait(false) ?? throw new FileNotFoundException($"File not found: {source}");
        if ((stat.Type & FileType.Directory) != 0) throw new NotSupportedException($"A directory cannot be copied between file systems ({source.Scheme}: → {target.Scheme}:)");
        if (!overwrite && await to.StatAsync(target, ct).ConfigureAwait(false) is not null) throw new IOException($"Target already exists: {target}");
        var bytes = await from.ReadFileAsync(source, ct).ConfigureAwait(false);
        await to.WriteFileAsync(target, bytes, createParents: false, ct).ConfigureAwait(false);
    }

    private static async ValueTask CrossRenameAsync(IFileSystem from, Uri source, IFileSystem to, Uri target, bool overwrite, CancellationToken ct)
    {
        await CrossCopyAsync(from, source, to, target, overwrite, ct).ConfigureAwait(false);
        await from.DeleteAsync(source, recursive: false, useTrash: false, ct).ConfigureAwait(false);
    }
}
