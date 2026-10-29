using CsCode.Core.Lifecycle;

namespace CsCode.Core.Platform;

public enum FileType { Unknown = 0, File = 1, Directory = 2, SymbolicLink = 64 }

public sealed record FileStat(FileType Type, long Size, DateTimeOffset ModifiedUtc, bool IsReadOnly = false);
public sealed record DirectoryEntry(string Name, FileType Type);

public enum FileChangeKind { Created = 1, Changed = 2, Deleted = 3 }
public sealed record FileChange(Uri Uri, FileChangeKind Kind);

/// <summary>Scheme-aware file system. `file` is always provided by Platform; extensions may add schemes later.</summary>
public interface IFileSystem
{
    ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default);
    ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default);
    ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default);
    ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default);
    ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default);
    ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default);
    ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default);
    ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default);
}

/// <summary>Recursive, debounced, exclude-aware watcher (honours files.watcherExclude). Disposing stops OS watches.</summary>
public interface IFileWatcher
{
    IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges);
}

public sealed record WorkspaceFoldersChangedEventArgs(IReadOnlyList<Uri> Added, IReadOnlyList<Uri> Removed);

public interface IWorkspaceService
{
    IReadOnlyList<Uri> Folders { get; }
    Uri? PrimaryFolder => Folders.Count > 0 ? Folders[0] : null;
    string? Name { get; }
    ValueTask OpenFolderAsync(Uri folder, CancellationToken ct = default);
    ValueTask CloseFolderAsync(CancellationToken ct = default);
    IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged { get; }
    IFileSystem FileSystem { get; }
    IFileWatcher Watcher { get; }
    /// <summary>Recently opened folders/files (persisted), most recent first.</summary>
    IReadOnlyList<Uri> RecentFolders { get; }
    /// <summary>Fuzzy file search under the folders, honouring files.exclude and .gitignore; cancellable, streaming-friendly.</summary>
    IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, CancellationToken ct = default);
}

/// <summary>Per-user and per-workspace state that survives restarts (open editors, layout, breakpoints, recents).</summary>
public interface IStateStorage
{
    T? Get<T>(string key, StorageScope scope = StorageScope.Application);
    void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application);
    void Remove(string key, StorageScope scope = StorageScope.Application);
    Task FlushAsync();
}

public enum StorageScope { Application = 0, Workspace = 1 }

/// <summary>Well-known filesystem locations (resolved by Platform; honours CSCODE_HOME for portable/test installs).</summary>
public interface IAppPaths
{
    string UserDataDir { get; }
    string ExtensionsDir { get; }
    string LogsDir { get; }
    string CacheDir { get; }
    string GlobalStorageDir { get; }
    string ToolsDir { get; }
    string? WorkspaceStorageDir(Uri? workspaceFolder);
    /// <summary>Directory containing bundled assets (extension host JS, built-in grammars/themes), next to the executable.</summary>
    string InstallDir { get; }
}
