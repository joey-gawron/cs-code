using System.IO.Enumeration;
using CsCode.Core.Text;
using CsCode.Platform.Io;
using Microsoft.Extensions.Logging;

namespace CsCode.Platform.Watching;

internal delegate void RawSink(RawKind kind, string path, string? oldPath);

internal interface IWatchBackend : IDisposable { }

/// <summary>Creates the OS-level backend for one root. Throws IOException when the OS watch limit is exhausted (the caller then falls back to polling).</summary>
internal delegate IWatchBackend BackendFactory(string root, bool recursive, RawSink sink, Action<Exception> onFatal);

/// <summary>One <see cref="FileSystemWatcher"/> for a whole tree. NOTE: each instance consumes one inotify instance on Linux, which is why <see cref="FileWatcher"/> shares one per root.</summary>
internal sealed class FswBackend : IWatchBackend
{
    /// <summary>
    /// On Linux a FileSystemWatcher whose directory was deleted can never be released: its reader thread blocks on an inotify instance with no
    /// watch left, and Dispose only removes watches — so the instance and the thread stay until the process exits (measured on .NET 10:
    /// +1 inotify fd and +1 thread per watcher disposed after its directory was deleted, none when disposed before, or after a move).
    /// </summary>
    public static bool LeaksWhenItsRootIsDeleted => OperatingSystem.IsLinux();

    private readonly FileSystemWatcher _fsw;
    private readonly MacRootWatch? _mac;

    public FswBackend(string root, bool recursive, RawSink sink, Action<Exception> onFatal)
    {
        // macOS: FSEvents' flags are sticky, so what FileSystemWatcher raises is classified again from the disk (MacWatchBackends.cs).
        if (OperatingSystem.IsMacOS())
        {
            _mac = new MacRootWatch(root, sink);
            sink = _mac.Reconciler.Handle;
        }
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
        _fsw.Error += (_, e) => OnError(e.GetException(), root, sink, onFatal, _mac);
        try
        {
            _fsw.EnableRaisingEvents = true; // on Linux this walks the tree and adds inotify watches: may throw when the limit is hit
        }
        catch
        {
            _fsw.Dispose();
            _mac?.Dispose();
            throw;
        }
    }

    /// <summary>The stream itself (tests raise its errors).</summary>
    internal FileSystemWatcher Watcher => _fsw;

    private static void OnError(Exception ex, string root, RawSink sink, Action<Exception> onFatal, MacRootWatch? mac)
    {
        // Lost events (queue overflow): we cannot know what changed, so announce the root as changed and let consumers rescan.
        if (ex is InternalBufferOverflowException) { mac?.Reconciler.Overflowed(); sink(RawKind.Changed, root, null); }
        // macOS (files-4): FileSystemWatcher raises an IOException for every FSEvents event that asks for a rescan (events dropped by the kernel
        // or fseventsd, MustScanSubDirs, a volume mounted or unmounted below the root). The stream is still alive: rescan, do not give up on it.
        // A root that is gone is the root watch's business (FSEvents follows the path, so the stream reports a directory made there again).
        else if (mac is not null && ex is IOException)
        {
            if (Directory.Exists(root)) { mac.Reconciler.Overflowed(); sink(RawKind.Changed, root, null); }
            else mac.OnRootLost();
        }
        else onFatal(ex);
    }

    public void Dispose()
    {
        _fsw.Dispose();
        _mac?.Dispose();
    }
}

/// <summary>
/// Coarse fallback when the OS watch limit is exhausted, or for a root that keeps losing its OS watch: compare snapshots of (mtime, size)
/// every <c>interval</c>. Cheap enough for the directories that matter, bounded for the ones that do not (<see cref="MaxEntries"/>); prunes
/// the default watcher excludes so node_modules/.git/bin/obj never get walked. It follows the path: a root that is missing is an empty
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
                if (e.IsDir && _isPruned(e.Path)) continue;
                into[e.Path] = (e.Ticks, e.Length, e.IsDir);
                if (e.IsDir && _recursive && !e.Link) subdirs.Add(e.Path);
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

/// <summary>Excludes for one subscription: glob patterns tested against the root-relative path (and, for directories, the path with a trailing '/').</summary>
internal sealed class ExcludeMatcher
{
    public static readonly IReadOnlyList<string> BuiltInWatcherExcludes =
        ["**/.git/objects/**", "**/.git/subtree-cache/**", "**/.hg/store/**", "**/node_modules/**", "**/bin/**", "**/obj/**"];

    private readonly string[] _patterns;
    private readonly string _root;

    public ExcludeMatcher(string root, IEnumerable<string> patterns)
    {
        _root = root.TrimEnd('/', '\\');
        _patterns = patterns.Where(static p => !string.IsNullOrWhiteSpace(p)).ToArray();
    }

    public bool IsEmpty => _patterns.Length == 0;

    public bool IsExcluded(string fullPath)
    {
        if (_patterns.Length == 0) return false;
        var rel = Relative(fullPath);
        if (rel.Length == 0) return false; // the root itself is never excluded
        var asDir = rel + "/";
        foreach (var p in _patterns)
            if (Glob.IsMatch(p, rel) || Glob.IsMatch(p, asDir)) return true;
        return false;
    }

    /// <summary>For callers that already have the root-relative path (the workspace walker).</summary>
    public bool IsExcludedRelative(string rel)
    {
        if (_patterns.Length == 0 || rel.Length == 0) return false;
        var asDir = rel + "/";
        foreach (var p in _patterns)
            if (Glob.IsMatch(p, rel) || Glob.IsMatch(p, asDir)) return true;
        return false;
    }

    private string Relative(string full)
    {
        if (full.Length >= _root.Length && full.StartsWith(_root, StringComparison.Ordinal))
            return full[_root.Length..].TrimStart('/', '\\').Replace('\\', '/');
        return Path.GetRelativePath(_root, full).Replace('\\', '/');
    }
}
