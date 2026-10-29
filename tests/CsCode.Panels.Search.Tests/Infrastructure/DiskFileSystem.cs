using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Panels.Search.Tests;

/// <summary>Plain System.IO-backed <see cref="IFileSystem"/> for tests (the real LocalFileSystem is exercised in the integration tests).</summary>
public sealed class DiskFileSystem : IFileSystem
{
    public int ReadDirectoryCalls;
    public int ReadFileCalls;

    public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default)
    {
        var p = uri.LocalPath;
        if (Directory.Exists(p)) return ValueTask.FromResult<FileStat?>(new FileStat(FileType.Directory, 0, Directory.GetLastWriteTimeUtc(p)));
        if (File.Exists(p)) return ValueTask.FromResult<FileStat?>(new FileStat(FileType.File, new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
        return ValueTask.FromResult<FileStat?>(null);
    }

    public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default)
    {
        Interlocked.Increment(ref ReadDirectoryCalls);
        var list = new List<DirectoryEntry>();
        foreach (var e in new DirectoryInfo(uri.LocalPath).EnumerateFileSystemInfos())
        {
            var link = e.LinkTarget is not null;
            var isDir = e is DirectoryInfo;
            var type = isDir ? FileType.Directory : FileType.File;
            list.Add(new DirectoryEntry(e.Name, link ? type | FileType.SymbolicLink : type));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return ValueTask.FromResult<IReadOnlyList<DirectoryEntry>>(list);
    }

    public ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default)
    {
        Interlocked.Increment(ref ReadFileCalls);
        return ValueTask.FromResult(File.ReadAllBytes(uri.LocalPath));
    }

    public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default)
    {
        if (createParents) Directory.CreateDirectory(Path.GetDirectoryName(uri.LocalPath)!);
        File.WriteAllBytes(uri.LocalPath, content.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) { Directory.CreateDirectory(uri.LocalPath); return ValueTask.CompletedTask; }

    public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default)
    {
        if (Directory.Exists(uri.LocalPath)) Directory.Delete(uri.LocalPath, recursive); else File.Delete(uri.LocalPath);
        return ValueTask.CompletedTask;
    }

    public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) { File.Move(source.LocalPath, target.LocalPath, overwrite); return ValueTask.CompletedTask; }
    public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) { File.Copy(source.LocalPath, target.LocalPath, overwrite); return ValueTask.CompletedTask; }
}

/// <summary>Workspace over real folders. <see cref="FindFilesAsync"/> mimics the platform enumeration minus .gitignore (tests of ignore handling use the walker).</summary>
public sealed class TestWorkspace : IWorkspaceService
{
    private readonly List<Uri> _folders;
    private readonly Emitter<WorkspaceFoldersChangedEventArgs> _changed = new();

    public TestWorkspace(params Uri[] folders)
    {
        _folders = folders.ToList();
        FileSystem = new DiskFileSystem();
    }

    public IReadOnlyList<Uri> Folders => _folders;
    public string? Name => "test";
    public IFileSystem FileSystem { get; set; }
    public IFileWatcher Watcher => throw new NotSupportedException();
    public IReadOnlyList<Uri> RecentFolders => [];
    public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => _changed;

    /// <summary>When set, <see cref="FindFilesAsync"/> yields exactly these (simulates the platform's own exclude/gitignore handling).</summary>
    public IReadOnlyList<Uri>? Files { get; set; }
    public int FindFilesCalls;

    public ValueTask OpenFolderAsync(Uri folder, CancellationToken ct = default)
    {
        _folders.Add(folder);
        _changed.Fire(new WorkspaceFoldersChangedEventArgs([folder], []));
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseFolderAsync(CancellationToken ct = default)
    {
        var removed = _folders.ToArray();
        _folders.Clear();
        _changed.Fire(new WorkspaceFoldersChangedEventArgs([], removed));
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Interlocked.Increment(ref FindFilesCalls);
        await Task.Yield();
        if (Files is { } canned)
        {
            foreach (var u in canned) { ct.ThrowIfCancellationRequested(); yield return u; }
            yield break;
        }
        foreach (var root in _folders)
        {
            foreach (var path in Directory.EnumerateFiles(root.LocalPath, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (path.Contains("/.git/", StringComparison.Ordinal)) continue;
                yield return new Uri(path);
            }
        }
    }
}
