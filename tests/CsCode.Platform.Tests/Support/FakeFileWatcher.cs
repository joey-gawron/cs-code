using CsCode.Core.Platform;
using CsCode.Platform.Io;

namespace CsCode.Platform.Tests.Support;

/// <summary>Deterministic <see cref="IFileWatcher"/>: tests raise events by hand and can see which watches are still alive.</summary>
public sealed class FakeFileWatcher : IFileWatcher
{
    private sealed class Registration(string root, bool recursive, Action<IReadOnlyList<FileChange>> callback)
    {
        public string Root { get; } = root.TrimEnd('/');
        public bool Recursive { get; } = recursive;
        public Action<IReadOnlyList<FileChange>> Callback { get; } = callback;
        public bool Disposed;
    }

    private readonly List<Registration> _watches = new();

    private int _totalCreated;

    public int ActiveCount { get { lock (_watches) return _watches.Count; } }
    public int TotalCreated => Volatile.Read(ref _totalCreated);

    public IReadOnlyList<string> ActiveRoots { get { lock (_watches) return _watches.Select(w => w.Root).ToArray(); } }

    public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges)
    {
        var w = new Registration(FileUri.ToPath(root), recursive, onChanges);
        lock (_watches) _watches.Add(w);
        Interlocked.Increment(ref _totalCreated);
        // Like a real watcher, a disposed watch must stop referencing its callback (otherwise it would pin the subscriber).
        return CsCode.Core.Lifecycle.Disposable.Create(() => { lock (_watches) { w.Disposed = true; _watches.Remove(w); } });
    }

    /// <summary>Delivers <paramref name="path"/> to every live watch that covers it, on the calling thread.</summary>
    public void Raise(string path, FileChangeKind kind = FileChangeKind.Changed)
    {
        Registration[] targets;
        lock (_watches)
        {
            targets = _watches.Where(w => !w.Disposed && Covers(w, path)).ToArray();
        }
        var change = new FileChange(FileUri.FromPath(path), kind);
        foreach (var w in targets) w.Callback([change]);
    }

    private static bool Covers(Registration w, string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        return string.Equals(dir, w.Root, StringComparison.Ordinal) || (w.Recursive && path.StartsWith(w.Root + "/", StringComparison.Ordinal));
    }
}
