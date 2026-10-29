using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;

namespace CsCode.Editor.Fallbacks;

/// <summary>
/// <see cref="IFileWatcher"/> over <see cref="FileSystemWatcher"/>, debounced (changes inside the window are coalesced per URI)
/// and exclude-aware. Used when the Platform watcher is not registered. Disposing stops the OS watch and the timer.
/// </summary>
internal sealed class FswFileWatcher : IFileWatcher
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(80);

    public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges)
    {
        if (!root.IsFile) return Disposable.Empty;
        var path = root.LocalPath;
        var fsw = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
            InternalBufferSize = 64 * 1024,
        };
        var gate = new object();
        var pending = new Dictionary<Uri, FileChangeKind>(); // bounded: one entry per distinct path per window
        var disposed = false;
        Timer? timer = null;

        void Flush()
        {
            FileChange[] batch;
            lock (gate)
            {
                if (disposed || pending.Count == 0) return;
                batch = pending.Select(kv => new FileChange(kv.Key, kv.Value)).ToArray();
                pending.Clear();
            }
            try
            {
                onChanges(batch);
            }
            catch (Exception)
            {
                // a bad subscriber must not kill the watcher
            }
        }

        void Add(string fullPath, FileChangeKind kind)
        {
            if (excludeGlobs is { Count: > 0 } && excludeGlobs.Any(g => Glob.IsMatch(g, fullPath))) return;
            lock (gate)
            {
                if (disposed) return;
                var uri = new Uri(fullPath);
                // Created+Changed stays Created; anything then Deleted is Deleted; Deleted then Created becomes Changed.
                pending[uri] = pending.TryGetValue(uri, out var prev) ? Merge(prev, kind) : kind;
                timer ??= new Timer(_ => Flush());
                timer.Change(Debounce, Timeout.InfiniteTimeSpan);
            }
        }

        fsw.Created += (_, e) => Add(e.FullPath, FileChangeKind.Created);
        fsw.Changed += (_, e) => Add(e.FullPath, FileChangeKind.Changed);
        fsw.Deleted += (_, e) => Add(e.FullPath, FileChangeKind.Deleted);
        fsw.Renamed += (_, e) =>
        {
            Add(e.OldFullPath, FileChangeKind.Deleted);
            Add(e.FullPath, FileChangeKind.Created);
        };
        fsw.EnableRaisingEvents = true;

        return Disposable.Create(() =>
        {
            lock (gate)
            {
                disposed = true;
                pending.Clear();
                timer?.Dispose();
            }
            fsw.EnableRaisingEvents = false;
            fsw.Dispose();
        });
    }

    private static FileChangeKind Merge(FileChangeKind previous, FileChangeKind next) => (previous, next) switch
    {
        (FileChangeKind.Created, FileChangeKind.Changed) => FileChangeKind.Created,
        (FileChangeKind.Deleted, FileChangeKind.Created) => FileChangeKind.Changed,
        (_, FileChangeKind.Deleted) => FileChangeKind.Deleted,
        _ => next,
    };
}
