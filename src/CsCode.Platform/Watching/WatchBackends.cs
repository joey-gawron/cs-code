using System.IO.Enumeration;
using CsCode.Core.Platform;
using CsCode.Platform.Io;
using Microsoft.Extensions.Logging;

namespace CsCode.Platform.Watching;

internal delegate void RawSink(RawKind kind, string path, string? oldPath);

internal interface IWatchBackend : IDisposable
{
    /// <summary>
    /// True when disposing this backend after its root directory was deleted cannot release what it holds from the OS, so that every such loss
    /// counts towards polling the path instead (<see cref="FileWatcher.MaxOsWatchLosses"/>). Only <see cref="FswBackend"/> on Linux.
    /// </summary>
    bool LeaksWhenItsRootIsDeleted => false;

    /// <summary>
    /// The subscriptions on the root or what they want changed (one joined, one's <see cref="FileWatchWants"/> changed): watch the directories
    /// left out that someone wants now, and let go of those watched only for a want that is gone. Called on the IO lane once the backend is in
    /// place. Backends that watch (or poll) everything they will ever report ignore it.
    /// </summary>
    void RevisitPruned() { }
}

/// <summary>What a backend does with a directory below its root (<see cref="DirectoryVerdict"/>; read live, as subscriptions come and go).</summary>
internal enum WatchVerdict
{
    /// <summary>A subscription wants what is in it: watch it.</summary>
    Watch,
    /// <summary>
    /// Left out (every subscription's excludes cover it, or it is or lies in a cache directory), but a subscription wants a path at or below
    /// it: watch it, and ask again when wants change (<see cref="IWatchBackend.RevisitPruned"/>).
    /// </summary>
    WatchForWant,
    /// <summary>Nobody wants what is in it: watch neither it nor anything below it. Its own appearance and removal its parent's watch reports.</summary>
    Prune,
    /// <summary>
    /// A cache directory (<see cref="WatchExclusion.IsCacheDirectory"/>) that subscriptions would otherwise watch: watch the directory itself but
    /// nothing below it, so that its tag going or a virtual environment's pyvenv.cfg arriving (uv writes the tag first) is seen and the
    /// directory is looked at again. One watch per cache directory.
    /// </summary>
    Shallow,
}

/// <summary>The <see cref="WatchVerdict"/> for a directory below the root. May read the disk (a cache tag): called on a backend's walk and reader.</summary>
internal delegate WatchVerdict DirectoryVerdict(string directory);

/// <summary>
/// Creates the OS-level backend for one root. A backend that walks the tree asks <paramref name="verdict"/> about every directory below the
/// root before watching it. <paramref name="onRootLost"/>: the backend saw its root directory go (moved away, or removed under it) and watches
/// nothing any more — the caller looks at the path again. Throws IOException when the OS watch limit is exhausted (the caller then falls back
/// to polling), <see cref="DirectoryNotFoundException"/> when the root is not a directory.
/// </summary>
internal delegate IWatchBackend BackendFactory(string root, bool recursive, DirectoryVerdict verdict, RawSink sink, Action<Exception> onFatal, Action onRootLost);

/// <summary>
/// One <see cref="FileSystemWatcher"/> for a whole tree: the backend everywhere but Linux (where <see cref="InotifyBackend"/> is used). It cannot
/// skip excluded directories: on Linux it would put an inotify watch on every directory below the root. Each instance consumes one inotify
/// instance on Linux, which is why <see cref="FileWatcher"/> shares one per root.
/// </summary>
internal sealed class FswBackend : IWatchBackend
{
    /// <summary>
    /// On Linux a FileSystemWatcher whose directory was deleted can never be released: its reader thread blocks on an inotify instance with no
    /// watch left, and Dispose only removes watches — so the instance and the thread stay until the process exits (measured on .NET 10:
    /// +1 inotify fd and +1 thread per watcher disposed after its directory was deleted, none when disposed before, or after a move).
    /// </summary>
    public bool LeaksWhenItsRootIsDeleted => OperatingSystem.IsLinux();

    private readonly FileSystemWatcher _fsw;

    public FswBackend(string root, bool recursive, RawSink sink, Action<Exception> onFatal)
    {
        _fsw = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _fsw.Created += (_, e) => sink(RawKind.Created, e.FullPath, null);
        _fsw.Changed += (_, e) => sink(RawKind.Changed, e.FullPath, null);
        _fsw.Deleted += (_, e) => sink(RawKind.Deleted, e.FullPath, null);
        _fsw.Renamed += (_, e) => sink(RawKind.Renamed, e.FullPath, e.OldFullPath);
        _fsw.Error += (_, e) =>
        {
            var ex = e.GetException();
            // Lost events (queue overflow): we cannot know what changed, so announce the root as changed and let consumers rescan.
            if (ex is InternalBufferOverflowException) sink(RawKind.Changed, root, null);
            else onFatal(ex);
        };
        try
        {
            _fsw.EnableRaisingEvents = true; // on Linux this walks the tree and adds inotify watches: may throw when the limit is hit
        }
        catch
        {
            _fsw.Dispose();
            throw;
        }
    }

    public void Dispose() => _fsw.Dispose();
}

/// <summary>
/// Coarse fallback when the OS watch limit is exhausted, or for a root that keeps losing its OS watch: compare snapshots of (mtime, size)
/// every <c>interval</c>. Cheap enough for the directories that matter, bounded for the ones that do not (<see cref="MaxEntries"/>); does not
/// enter the directories <c>isPruned</c> names (the default watcher excludes and cache directories, unless a subscription wants a path there),
/// so node_modules/.git/bin/obj/target never get walked — their own appearance and removal are reported. It follows the path: a root that is missing is an empty
/// snapshot, so its deletion and re-creation are reported like any other change. Construct it on the IO lane — it takes its baseline there,
/// so everything after construction is reported; the periodic scans run on the IO lane in single work items and never block a caller.
/// </summary>
internal sealed class PollingBackend : IWatchBackend
{
    public const int MaxEntries = 50_000;

    private readonly string _root;
    private readonly bool _recursive;
    private readonly RawSink _sink;
    private readonly IoLane _lane;
    private readonly Func<string, bool> _isPruned;
    private readonly ILogger _log;
    private readonly Timer _timer;
    private readonly TimeSpan _interval;
    private Dictionary<string, (long Ticks, long Length, bool IsDir)>? _previous;
    private int _scanning;
    private volatile bool _disposed;
    private bool _capWarned;

    public PollingBackend(string root, bool recursive, RawSink sink, IoLane lane, TimeSpan interval, Func<string, bool> isPruned, ILogger log)
    {
        _root = root;
        _recursive = recursive;
        _sink = sink;
        _lane = lane;
        _interval = interval;
        _isPruned = isPruned;
        _log = log;
        // The baseline now, not in the first tick: a change made right after the watch is armed must not end up in the baseline.
        _previous = Snapshot();
        _timer = new Timer(static s => ((PollingBackend)s!).OnTick(), this, interval, interval);
    }

    // Timer callbacks run on the shared pool: only hand the work to the lane (and never let two scans overlap).
    private void OnTick()
    {
        if (_disposed || Interlocked.Exchange(ref _scanning, 1) == 1) return;
        _lane.Post(() =>
        {
            try { Scan(); }
            finally { Volatile.Write(ref _scanning, 0); }
        }, ex => _log.LogDebug(ex, "Polling scan of {Root} failed", _root));
    }

    private Dictionary<string, (long Ticks, long Length, bool IsDir)> Snapshot()
    {
        var current = new Dictionary<string, (long, long, bool)>(StringComparer.Ordinal);
        Walk(_root, current);
        return current;
    }

    private void Scan()
    {
        var current = Snapshot();
        if (_disposed) return;

        var previous = _previous;
        _previous = current;
        if (previous is null) return; // no baseline (cannot happen after construction; kept as a guard)

        foreach (var (path, now) in current)
        {
            if (!previous.TryGetValue(path, out var before)) _sink(RawKind.Created, path, null);
            else if (before.Ticks != now.Item1 || before.Length != now.Item2) _sink(RawKind.Changed, path, null);
        }
        foreach (var path in previous.Keys)
            if (!current.ContainsKey(path)) _sink(RawKind.Deleted, path, null);
    }

    private void Walk(string dir, Dictionary<string, (long, long, bool)> into)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false };
        var subdirs = new List<string>();
        try
        {
            var entries = new FileSystemEnumerable<(string Path, long Ticks, long Length, bool IsDir, bool Link)>(dir,
                static (ref FileSystemEntry e) => (e.ToFullPath(), e.LastWriteTimeUtc.UtcTicks, e.IsDirectory ? 0 : e.Length, e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0), options);

            foreach (var e in entries)
            {
                if (_disposed) return;
                if (into.Count >= MaxEntries)
                {
                    if (!_capWarned) { _capWarned = true; _log.LogWarning("Polling watch of {Root} stops at {Max} entries; later changes may be missed. Raise the OS inotify limit for real-time watching.", _root, MaxEntries); }
                    return;
                }
                into[e.Path] = (e.Ticks, e.Length, e.IsDir);
                if (e.IsDir && _recursive && !e.Link && !_isPruned(e.Path)) subdirs.Add(e.Path);
            }
        }
        // Deleted (or replaced by a file) since it was listed — the root itself included: what is not there is reported as deleted.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var sub in subdirs) Walk(sub, into);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}

/// <summary>
/// Excludes for one subscription: glob patterns tested against the root-relative path (<see cref="WatchExclusion"/>). <see cref="IsExcluded"/>
/// asks about a directory's contents (the path, or the path with a trailing '/', matches: what the OS need not watch);
/// <see cref="IsExcludedEntry"/> about an event (the path itself matches), so <c>bin/</c> appearing is reported while what is in it is not.
/// </summary>
internal sealed class ExcludeMatcher
{
    public static readonly IReadOnlyList<string> BuiltInWatcherExcludes = WatchExclusion.DefaultExcludes;

    private readonly string[] _patterns;
    private readonly string _root;

    public ExcludeMatcher(string root, IEnumerable<string> patterns)
    {
        _root = root.TrimEnd('/', '\\');
        _patterns = patterns.Where(static p => !string.IsNullOrWhiteSpace(p)).ToArray();
    }

    public bool IsEmpty => _patterns.Length == 0;

    /// <summary>The directory (or file) at <paramref name="fullPath"/> is excluded with everything below it. The root itself never is.</summary>
    public bool IsExcluded(string fullPath) => _patterns.Length > 0 && WatchExclusion.IsExcludedDirectory(WatchExclusion.Relative(_root, fullPath), _patterns);

    /// <summary>An event for <paramref name="fullPath"/> is dropped: the glob matches the path itself.</summary>
    public bool IsExcludedEntry(string fullPath) => _patterns.Length > 0 && WatchExclusion.IsExcludedEntry(WatchExclusion.Relative(_root, fullPath), _patterns);

    /// <summary>For callers that already have the root-relative path (the workspace walker).</summary>
    public bool IsExcludedRelative(string rel) => _patterns.Length > 0 && WatchExclusion.IsExcludedDirectory(rel, _patterns);
}
