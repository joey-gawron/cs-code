using System.Text.Json;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Dap.Tests.Fakes;

/// <summary>Round-trips values through JSON like the real storage would, so non-serialisable shapes fail in tests.</summary>
public sealed class FakeStateStorage : IStateStorage
{
    private readonly object _gate = new();
    private readonly Dictionary<(string, StorageScope), string> _values = new();
    public int SetCount { get; private set; }
    public int FlushCount { get; private set; }

    public T? Get<T>(string key, StorageScope scope = StorageScope.Application)
    {
        lock (_gate) return _values.TryGetValue((key, scope), out var json) ? JsonSerializer.Deserialize<T>(json, CoreJson.Options) : default;
    }

    public void Set<T>(string key, T? value, StorageScope scope = StorageScope.Application)
    {
        lock (_gate)
        {
            SetCount++;
            _values[(key, scope)] = JsonSerializer.Serialize(value, CoreJson.Options);
        }
    }

    public void Remove(string key, StorageScope scope = StorageScope.Application)
    {
        lock (_gate) _values.Remove((key, scope));
    }

    public Task FlushAsync()
    {
        lock (_gate) FlushCount++;
        return Task.CompletedTask;
    }

    public string? Raw(string key, StorageScope scope = StorageScope.Workspace)
    {
        lock (_gate) return _values.GetValueOrDefault((key, scope));
    }
}

public sealed class LocalFileSystem : IFileSystem
{
    public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default)
    {
        var p = uri.LocalPath;
        if (File.Exists(p)) return ValueTask.FromResult<FileStat?>(new FileStat(FileType.File, new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
        if (Directory.Exists(p)) return ValueTask.FromResult<FileStat?>(new FileStat(FileType.Directory, 0, Directory.GetLastWriteTimeUtc(p)));
        return ValueTask.FromResult<FileStat?>(null);
    }

    public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<DirectoryEntry>>([.. Directory.EnumerateFileSystemEntries(uri.LocalPath).Select(e => new DirectoryEntry(Path.GetFileName(e), Directory.Exists(e) ? FileType.Directory : FileType.File))]);

    public async ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default) => await File.ReadAllBytesAsync(uri.LocalPath, ct);

    public async ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default)
    {
        if (createParents) Directory.CreateDirectory(Path.GetDirectoryName(uri.LocalPath)!);
        await File.WriteAllBytesAsync(uri.LocalPath, content.ToArray(), ct);
    }

    public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) { Directory.CreateDirectory(uri.LocalPath); return ValueTask.CompletedTask; }
    public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) { if (File.Exists(uri.LocalPath)) File.Delete(uri.LocalPath); else if (Directory.Exists(uri.LocalPath)) Directory.Delete(uri.LocalPath, recursive); return ValueTask.CompletedTask; }
    public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) { File.Move(source.LocalPath, target.LocalPath, overwrite); return ValueTask.CompletedTask; }
    public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) { File.Copy(source.LocalPath, target.LocalPath, overwrite); return ValueTask.CompletedTask; }
}

public sealed class FakeFileWatcher : IFileWatcher
{
    private readonly object _gate = new();
    private readonly List<Action<IReadOnlyList<FileChange>>> _handlers = new();
    public int ActiveWatches { get { lock (_gate) return _handlers.Count; } }

    public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges)
    {
        lock (_gate) _handlers.Add(onChanges);
        return Disposable.Create(() => { lock (_gate) _handlers.Remove(onChanges); });
    }

    public void Raise(params FileChange[] changes)
    {
        Action<IReadOnlyList<FileChange>>[] hs;
        lock (_gate) hs = [.. _handlers];
        foreach (var h in hs) h(changes);
    }
}

public sealed class FakeWorkspace : IWorkspaceService
{
    private readonly Emitter<WorkspaceFoldersChangedEventArgs> _folders = new();
    private List<Uri> _list = new();

    public FakeWorkspace(Uri? folder = null)
    {
        if (folder is not null) _list.Add(folder);
    }

    public IReadOnlyList<Uri> Folders => _list;
    public string? Name => _list.Count > 0 ? Path.GetFileName(_list[0].LocalPath.TrimEnd('/')) : null;
    public IFileSystem FileSystem { get; } = new LocalFileSystem();
    public FakeFileWatcher FakeWatcher { get; } = new();
    public IFileWatcher Watcher => FakeWatcher;
    public IReadOnlyList<Uri> RecentFolders => [];
    public IEventSource<WorkspaceFoldersChangedEventArgs> FoldersChanged => _folders;

    public ValueTask OpenFolderAsync(Uri folder, CancellationToken ct = default)
    {
        var removed = _list.ToArray();
        _list = [folder];
        _folders.Fire(new WorkspaceFoldersChangedEventArgs([folder], removed));
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseFolderAsync(CancellationToken ct = default)
    {
        var removed = _list.ToArray();
        _list = [];
        _folders.Fire(new WorkspaceFoldersChangedEventArgs([], removed));
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<Uri> FindFilesAsync(string? includeGlob, int maxResults, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
