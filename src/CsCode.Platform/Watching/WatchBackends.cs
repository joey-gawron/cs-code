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
/// Coarse fallback when the OS watch limit is exhausted: compare snapshots of (mtime, size) every <c>interval</c>. Cheap enough for the
/// directories that matter, bounded for the ones that do not (<see cref="MaxEntries"/>); prunes the default watcher excludes so
/// node_modules/.git/bin/obj never get walked. Runs on the IO lane in single work items and never blocks a caller.
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
        _timer = new Timer(static s => ((PollingBackend)s!).OnTick(), this, TimeSpan.Zero, interval);
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

    private void Scan()
    {
        var current = new Dictionary<string, (long, long, bool)>(StringComparer.Ordinal);
        Walk(_root, current);
        if (_disposed) return;

        var previous = _previous;
        _previous = current;
        if (previous is null) return; // first scan = baseline

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
