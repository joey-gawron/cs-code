using CsCode.Core.Platform;

namespace CsCode.Remote.Client;

// The ssh: providers serve docker:// and wsl:// workspaces too (one connection manager, one host at a time): these put them under the other schemes
// for Platform's routing file system, watcher and searches, which dispatch by URI scheme. Every call goes to the same instance.

internal sealed class SchemeFileSystem(string scheme, IFileSystemProvider inner) : IFileSystemProvider
{
    public string Scheme => scheme;
    public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default) => inner.StatAsync(uri, ct);
    public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) => inner.ReadDirectoryAsync(uri, ct);
    public ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default) => inner.ReadFileAsync(uri, ct);
    public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default) => inner.WriteFileAsync(uri, content, createParents, ct);
    public bool PreservesFileOnWrite(Uri uri) => inner.PreservesFileOnWrite(uri);
    public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) => inner.CreateDirectoryAsync(uri, ct);
    public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) => inner.DeleteAsync(uri, recursive, useTrash, ct);
    public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => inner.RenameAsync(source, target, overwrite, ct);
    public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => inner.CopyAsync(source, target, overwrite, ct);
}

internal sealed class SchemeWatcher(string scheme, IFileWatcherProvider inner) : IFileWatcherProvider
{
    public string Scheme => scheme;
    public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges) =>
        inner.Watch(root, recursive, excludeGlobs, onChanges);
}

internal sealed class SchemeSearch(string scheme, RemoteSearch inner) : IFileSearchProvider, ITextSearchProvider
{
    public string Scheme => scheme;
    public IAsyncEnumerable<Uri> FindFilesAsync(Uri folder, FileSearchOptions options, CancellationToken ct = default) => inner.FindFilesAsync(folder, options, ct);
    public IAsyncEnumerable<IReadOnlyList<TextSearchHit>> SearchAsync(TextSearchQuery query, CancellationToken ct = default) => inner.SearchAsync(query, ct);
}
