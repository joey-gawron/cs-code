using System.Collections.Concurrent;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Platform.Io;
using CsCode.Platform.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Platform.EditorConfig;

/// <summary>
/// <c>.editorconfig</c> resolution per the spec: for a file, every <c>.editorconfig</c> from its directory up to the filesystem root (or the first one
/// with <c>root = true</c>) is read; farther files apply first and nearer ones override, and within a file later sections override earlier ones.
/// <list type="bullet">
/// <item>File reading happens on the IO lane. Parsed files are cached per DIRECTORY (so siblings share one read) and resolved results per file; both bounded.</item>
/// <item>Invalidation: a recursive watch on each workspace folder (shared with other watchers on the same root) clears the caches and raises
/// <see cref="Changed"/> when an <c>.editorconfig</c> changes; for files outside the workspace, directory entries older than 5 s are re-validated by
/// timestamp/size the next time something is resolved.</item>
/// <item><see cref="TryGetCached"/> is a lock-free dictionary read: safe for the typing path.</item>
/// </list>
/// </summary>
public sealed class EditorConfigService : IEditorConfigService, IWorkspaceBound, IDisposable
{
    private const string FileName = ".editorconfig";
    private const int MaxDirectories = 1024;
    private const int MaxResults = 4096;
    private const int MaxChain = 64;
    private const int MaxFileBytes = 1024 * 1024;
    private const long DefaultRevalidateAfterMs = 5000;

    private sealed record DirEntry(EditorConfigFile? File, bool Exists, long Ticks, long Length, long LoadedAt);
    private sealed record Cached(EditorConfigSettings Settings, long At);

    private readonly IoLane _lane;
    private readonly IFileWatcher? _watcher;
    private readonly ILogger _log;
    private readonly Emitter<object?> _changed = new();
    private readonly ConcurrentDictionary<string, DirEntry> _dirs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Cached> _results = new(StringComparer.Ordinal);
    private readonly object _watchGate = new();
    private DisposableStore _watches = new();
    private bool _disposed;

    public EditorConfigService(IoLane lane, IFileWatcher? watcher = null, ILogger<EditorConfigService>? logger = null)
    {
        _lane = lane;
        _watcher = watcher;
        _log = logger ?? NullLogger<EditorConfigService>.Instance;
    }

    /// <summary>How long a cached directory entry / result is trusted before its file is re-checked by timestamp (tests shorten it).</summary>
    internal long RevalidateAfterMs { get; set; } = DefaultRevalidateAfterMs;

    internal int CachedDirectoryCount => _dirs.Count;
    internal int CachedResultCount => _results.Count;

    public IEventSource<object?> Changed => _changed;

    public EditorConfigSettings? TryGetCached(Uri file) =>
        file.IsFile && _results.TryGetValue(FileUri.ToPath(file), out var c) ? c.Settings : null;

    public ValueTask<EditorConfigSettings> GetAsync(Uri file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.IsFile) return ValueTask.FromResult(EditorConfigSettings.Empty);
        var path = FileUri.ToPath(file);
        if (_results.TryGetValue(path, out var cached) && Environment.TickCount64 - cached.At < RevalidateAfterMs)
            return ValueTask.FromResult(cached.Settings);
        return new ValueTask<EditorConfigSettings>(_lane.RunAsync(token => Resolve(path, token), ct));
    }

    // ---- resolution (IO lane) ----------------------------------------------------------------------------------------------------

    private EditorConfigSettings Resolve(string filePath, CancellationToken ct)
    {
        var chain = new List<(string Dir, EditorConfigFile File)>();
        var anyChanged = false;
        var dir = Path.GetDirectoryName(filePath);
        for (var depth = 0; dir is not null && depth < MaxChain; depth++, dir = Path.GetDirectoryName(dir))
        {
            ct.ThrowIfCancellationRequested();
            var entry = GetDirEntry(dir, ref anyChanged);
            if (entry.File is null) continue;
            chain.Add((dir, entry.File));
            if (entry.File.IsRoot) break;
        }

        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = chain.Count - 1; i >= 0; i--) // farthest first, nearest last: the nearest file wins
        {
            var rel = Path.GetRelativePath(chain[i].Dir, filePath).Replace('\\', '/');
            chain[i].File.Apply(rel, props);
        }
        var settings = props.Count == 0 ? EditorConfigSettings.Empty : EditorConfigParser.ToSettings(props);

        if (anyChanged) InvalidateAll(raise: true); // a file we had cached changed on disk: everything resolved before is suspect
        if (_results.Count >= MaxResults) _results.Clear();
        _results[filePath] = new Cached(settings, Environment.TickCount64);
        return settings;
    }

    private DirEntry GetDirEntry(string dir, ref bool changed)
    {
        var now = Environment.TickCount64;
        if (_dirs.TryGetValue(dir, out var existing))
        {
            if (now - existing.LoadedAt < RevalidateAfterMs) return existing;
            var (exists, ticks, length) = Stamp(Path.Combine(dir, FileName));
            if (exists == existing.Exists && ticks == existing.Ticks && length == existing.Length)
            {
                var refreshed = existing with { LoadedAt = now };
                _dirs[dir] = refreshed;
                return refreshed;
            }
            changed = true;
        }

        var loaded = Load(dir, now);
        if (_dirs.Count >= MaxDirectories) _dirs.Clear();
        _dirs[dir] = loaded;
        return loaded;
    }

    private DirEntry Load(string dir, long now)
    {
        var path = Path.Combine(dir, FileName);
        var (exists, ticks, length) = Stamp(path);
        if (!exists) return new DirEntry(null, false, 0, 0, now);
        if (length > MaxFileBytes) { _log.LogWarning("Ignoring {Path}: larger than {Max} bytes", path, MaxFileBytes); return new DirEntry(null, true, ticks, length, now); }
        try
        {
            return new DirEntry(EditorConfigParser.Parse(File.ReadAllText(path)), true, ticks, length, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Could not read {Path}", path);
            return new DirEntry(null, true, ticks, length, now);
        }
    }

    private static (bool Exists, long Ticks, long Length) Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (true, info.LastWriteTimeUtc.Ticks, info.Length) : (false, 0, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (false, 0, 0); }
    }

    // ---- invalidation ------------------------------------------------------------------------------------------------------------

    private void InvalidateAll(bool raise)
    {
        _dirs.Clear();
        _results.Clear();
        if (raise && !_disposed) _changed.Fire(null);
    }

    /// <summary>Drops every cached result and file (e.g. after an out-of-band change) and announces it.</summary>
    public void Invalidate() => InvalidateAll(raise: true);

    ValueTask IWorkspaceBound.OnWorkspaceFoldersChangedAsync(IReadOnlyList<Uri> folders, CancellationToken ct)
    {
        DisposableStore old;
        var next = new DisposableStore();
        lock (_watchGate) { old = _watches; _watches = next; }
        old.Dispose();
        if (_watcher is null || _disposed) return ValueTask.CompletedTask;

        foreach (var folder in folders.Where(f => f.IsFile))
        {
            // recursive: an .editorconfig can sit in any subdirectory; the watcher shares one OS watch per root with everyone else
            next.Add(_watcher.Watch(folder, recursive: true, excludeGlobs: null, changes =>
            {
                foreach (var c in changes)
                {
                    if (!c.Uri.IsFile || Path.GetFileName(FileUri.ToPath(c.Uri)) != FileName) continue;
                    InvalidateAll(raise: true);
                    return;
                }
            }));
        }
        InvalidateAll(raise: false); // files resolved for the previous workspace may now belong to a different tree
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposableStore stores;
        lock (_watchGate) { stores = _watches; _watches = new DisposableStore(); }
        stores.Dispose();
        _changed.Clear();
        _dirs.Clear();
        _results.Clear();
    }
}
