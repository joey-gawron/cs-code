using CsCode.Core.Platform;
using CsCode.Platform.Io;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Platform.Watching;

/// <summary>Handle returned by <see cref="FileWatcher.Watch"/>. <see cref="Ready"/> completes once the OS watch is armed (events that happen before are not reported).</summary>
public sealed class FileWatchHandle : IDisposable
{
    private readonly Action _dispose;
    internal FileWatchHandle(Task ready, Action dispose) { Ready = ready; _dispose = dispose; }
    public Task Ready { get; }
    public void Dispose() => _dispose();
}

/// <summary>
/// Recursive, debounced, exclude-aware watcher.
/// <list type="bullet">
/// <item><b>Threading</b>: <see cref="Watch"/> returns at once — the OS watch is armed (a tree walk for inotify) on the IO lane — and so is
/// disposal. Callbacks run on IO-lane threads in batches (≤ <see cref="MaxBatch"/> changes), never concurrently for one subscription.
/// The raw OS callbacks only enqueue into a coalescer.</item>
/// <item><b>Coalescing</b>: a quiet period of <see cref="PlatformOptions.WatchDebounce"/> after the last event delivers one batch; a
/// continuously busy watch still delivers every <see cref="PlatformOptions.WatchMaxDelay"/>. See <see cref="ChangeCoalescer"/> for the merge rules.</item>
/// <item><b>Excludes</b>: <c>excludeGlobs</c> (null = the <c>files.watcherExclude</c> setting, else built-ins; empty = none) filter events by
/// root-relative path. (The OS still watches excluded directories — FileSystemWatcher cannot skip them — it is the events that are dropped.)</item>
/// <item><b>Handles</b>: one <see cref="FileSystemWatcher"/> (= one inotify instance) is shared by every subscription on the same root and
/// disposed with the last one. When the OS watch limit is exhausted that root degrades to a coarse polling watcher (logged once per root).</item>
/// </list>
/// </summary>
public sealed class FileWatcher : IFileWatcher, IDisposable
{
    public const int MaxBatch = 1000;

    private sealed class Source(string root, bool recursive)
    {
        public string Root { get; } = root;
        public bool Recursive { get; } = recursive;
        public Subscription[] Subscriptions = [];
        public IWatchBackend? Backend;
        public bool Closed;
        public bool UsingPolling;
        public Task Ready = Task.CompletedTask;
    }

    private readonly IoLane _lane;
    private readonly IConfigurationServiceRead? _config;
    private readonly PlatformOptions _options;
    private readonly ILogger _log;
    private readonly BackendFactory _fswFactory;
    private readonly object _gate = new();
    private readonly Dictionary<(string, bool), Source> _sources = new();
    private bool _disposed;

    /// <summary>Read-only view of the one setting the watcher needs, so the watcher does not depend on the whole configuration surface.</summary>
    internal interface IConfigurationServiceRead { IReadOnlyList<string>? WatcherExcludes(); }

    public FileWatcher(IoLane lane, IConfigurationService? configuration = null, PlatformOptions? options = null, ILogger<FileWatcher>? logger = null)
        : this(lane, configuration is null ? null : new ConfigRead(configuration), options, logger, null) { }

    internal FileWatcher(IoLane lane, IConfigurationServiceRead? config, PlatformOptions? options, ILogger<FileWatcher>? logger, BackendFactory? fswFactory)
    {
        _lane = lane;
        _config = config;
        _options = options ?? new PlatformOptions();
        _log = logger ?? NullLogger<FileWatcher>.Instance;
        _fswFactory = fswFactory ?? ((root, recursive, sink, fatal) => new FswBackend(root, recursive, sink, fatal));
    }

    private sealed class ConfigRead(IConfigurationService config) : IConfigurationServiceRead
    {
        public IReadOnlyList<string>? WatcherExcludes() =>
            config.Get<Dictionary<string, bool>>(SettingKeys.FilesWatcherExclude)?.Where(kv => kv.Value).Select(kv => kv.Key).ToArray();
    }

    // ---- public API --------------------------------------------------------------------------------------------------------------

    public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges)
    {
        ArgumentNullException.ThrowIfNull(onChanges);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = Path.GetFullPath(FileUri.ToPath(root)).TrimEnd('/', '\\');
        if (path.Length == 0) path = "/";

        var excludes = excludeGlobs ?? _config?.WatcherExcludes() ?? ExcludeMatcher.BuiltInWatcherExcludes;
        var sub = new Subscription(this, path, new ExcludeMatcher(path, excludes), onChanges);

        Source source;
        lock (_gate)
        {
            if (!_sources.TryGetValue((path, recursive), out source!))
            {
                source = new Source(path, recursive);
                _sources[(path, recursive)] = source;
                source.Ready = _lane.RunAsync(_ => Arm(source));
            }
            source.Subscriptions = [.. source.Subscriptions, sub];
        }
        return new FileWatchHandle(source.Ready, () => Release(source, sub));
    }

    public void Dispose()
    {
        Source[] all;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            all = _sources.Values.ToArray();
            _sources.Clear();
        }
        foreach (var s in all) CloseSource(s);
    }

    /// <summary>Number of live OS-level backends (tests/diagnostics).</summary>
    public int ActiveBackends { get { lock (_gate) return _sources.Values.Count(s => s.Backend is not null); } }

    // ---- arming / releasing ------------------------------------------------------------------------------------------------------

    private void Arm(Source source)
    {
        lock (_gate) if (source.Closed) return;
        if (!Directory.Exists(source.Root))
        {
            var ex = new DirectoryNotFoundException($"Cannot watch '{source.Root}': it is not a directory");
            _log.LogWarning("{Message}", ex.Message);
            throw ex;
        }

        IWatchBackend backend;
        var polling = false;
        try
        {
            backend = _fswFactory(source.Root, source.Recursive, (k, p, o) => OnRaw(source, k, p, o), ex => OnBackendFailed(source, ex));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            _log.LogWarning("Cannot create an OS file watch for {Root} ({Reason}); falling back to polling every {Interval:F1}s. On Linux, raise fs.inotify.max_user_watches / max_user_instances.",
                source.Root, ex.Message, _options.PollInterval.TotalSeconds);
            backend = CreatePolling(source);
            polling = true;
        }

        bool close;
        lock (_gate)
        {
            close = source.Closed;
            if (!close) { source.Backend = backend; source.UsingPolling = polling; }
        }
        if (close) backend.Dispose();
    }

    private IWatchBackend CreatePolling(Source source)
    {
        var pruner = new ExcludeMatcher(source.Root, _config?.WatcherExcludes() ?? ExcludeMatcher.BuiltInWatcherExcludes);
        return new PollingBackend(source.Root, source.Recursive, (k, p, o) => OnRaw(source, k, p, o), _lane, _options.PollInterval, pruner.IsExcluded, _log);
    }

    // The OS reported a fatal problem after arming (e.g. the inotify limit hit while a new subdirectory was added): degrade, once.
    private void OnBackendFailed(Source source, Exception ex)
    {
        _lane.Post(() =>
        {
            IWatchBackend? old;
            lock (_gate)
            {
                if (source.Closed || source.UsingPolling) return;
                source.UsingPolling = true;
                old = source.Backend;
                source.Backend = null;
            }
            _log.LogWarning("File watch on {Root} failed ({Reason}); falling back to polling.", source.Root, ex.Message);
            old?.Dispose();
            var polling = CreatePolling(source);
            lock (_gate) { if (source.Closed) { polling.Dispose(); return; } source.Backend = polling; }
        });
    }

    private void Release(Source source, Subscription sub)
    {
        sub.Dispose();
        var close = false;
        lock (_gate)
        {
            source.Subscriptions = source.Subscriptions.Where(s => !ReferenceEquals(s, sub)).ToArray();
            if (source.Subscriptions.Length == 0 && !source.Closed)
            {
                close = true;
                _sources.Remove((source.Root, source.Recursive));
            }
        }
        if (close) CloseSource(source);
    }

    private void CloseSource(Source source)
    {
        IWatchBackend? backend;
        lock (_gate)
        {
            source.Closed = true;
            backend = source.Backend;
            source.Backend = null;
        }
        foreach (var s in source.Subscriptions) s.Dispose();
        // FileSystemWatcher.Dispose can take a while (it stops its reader): never on the caller's thread.
        if (backend is not null) _lane.Post(backend.Dispose);
    }

    private void OnRaw(Source source, RawKind kind, string path, string? oldPath)
    {
        foreach (var sub in Volatile.Read(ref source.Subscriptions)) sub.Add(kind, path, oldPath);

        // FileSystemWatcher cannot see what happens inside a directory in the instant between its creation and its own watch being armed
        // (`mkdir -p a/b/c` reports only `a`). So when a directory appears, look inside it ourselves and announce what is already there.
        if (source.Recursive && !source.UsingPolling && kind is RawKind.Created or RawKind.Renamed)
            _lane.Post(() => ScanNewDirectory(source, path), ex => _log.LogDebug(ex, "Scanning new directory {Path} failed", path));
    }

    private const int MaxScanEntries = 20_000;

    private void ScanNewDirectory(Source source, string root)
    {
        if (!Directory.Exists(root)) return; // a plain file, or already gone
        var stack = new Stack<string>();
        stack.Push(root);
        var seen = 0;
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false };
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (source.Closed) return;
            var subs = Volatile.Read(ref source.Subscriptions);
            if (subs.Length == 0 || subs.All(s => s.IsExcluded(dir))) continue; // nobody wants this subtree: do not even enumerate it
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir, "*", options); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                if (++seen > MaxScanEntries) { _log.LogDebug("Stopped scanning new directory {Root} after {Max} entries", root, MaxScanEntries); return; }
                foreach (var sub in Volatile.Read(ref source.Subscriptions)) sub.Add(RawKind.Created, entry, null);
                var info = new FileInfo(entry);
                if (info.LinkTarget is null && Directory.Exists(entry)) stack.Push(entry); // never follow symlinked directories
            }
        }
    }

    // ---- one subscriber ----------------------------------------------------------------------------------------------------------

    private sealed class Subscription : IDisposable
    {
        private readonly FileWatcher _owner;
        private readonly string _root;
        private readonly ExcludeMatcher _excludes;
        private readonly Action<IReadOnlyList<FileChange>> _callback;
        private readonly ChangeCoalescer _pending = new();
        private readonly object _gate = new();
        private readonly Timer _timer;
        private long _firstTick;      // when the current burst began (0 = idle)
        private bool _delivering;
        private bool _flushAgain;
        private volatile bool _disposed;

        public Subscription(FileWatcher owner, string root, ExcludeMatcher excludes, Action<IReadOnlyList<FileChange>> callback)
        {
            _owner = owner;
            _root = root;
            _excludes = excludes;
            _callback = callback;
            _timer = new Timer(static s => ((Subscription)s!).OnTimer(), this, Timeout.Infinite, Timeout.Infinite);
        }

        public bool IsExcluded(string path) => _excludes.IsExcluded(path);

        public void Add(RawKind kind, string path, string? oldPath)
        {
            if (_disposed) return;
            // Excluded paths are dropped here, before they cost a dictionary slot. For a rename only the excluded half is dropped.
            if (kind == RawKind.Renamed)
            {
                var newExcluded = _excludes.IsExcluded(path);
                var oldExcluded = oldPath is not null && _excludes.IsExcluded(oldPath);
                if (newExcluded && oldExcluded) return;
                if (newExcluded) { kind = RawKind.Deleted; path = oldPath!; oldPath = null; }
                else if (oldExcluded) oldPath = null;
            }
            else if (_excludes.IsExcluded(path)) return;

            lock (_gate)
            {
                if (_disposed) return;
                _pending.Add(kind, path, oldPath);
                var now = Environment.TickCount64;
                if (_firstTick == 0) _firstTick = now;
                var debounce = (long)_owner._options.WatchDebounce.TotalMilliseconds;
                var maxDelay = (long)_owner._options.WatchMaxDelay.TotalMilliseconds;
                var due = Math.Clamp(maxDelay - (now - _firstTick), 0, debounce); // quiet for `debounce`, but never later than `maxDelay` after the burst began
                try { _timer.Change(due, Timeout.Infinite); } catch (ObjectDisposedException) { }
            }
        }

        // Timer callbacks run on the shared pool: only enqueue the real work onto the IO lane.
        private void OnTimer()
        {
            if (_disposed) return;
            _owner._lane.Post(Flush, ex => _owner._log.LogDebug(ex, "Watch flush failed"));
        }

        private void Flush()
        {
            while (true)
            {
                List<KeyValuePair<string, FileChangeKind>> changes;
                bool overflowed;
                lock (_gate)
                {
                    if (_disposed) return;
                    if (_delivering) { _flushAgain = true; return; } // a callback is running: it will loop for us (keeps batches ordered, never concurrent)
                    if (_pending.IsEmpty) { _firstTick = 0; return; }
                    (changes, overflowed) = _pending.Drain();
                    _firstTick = 0;
                    _delivering = true;
                }

                try { Deliver(changes, overflowed); }
                catch (Exception ex) { _owner._log.LogWarning(ex, "Delivering file changes for {Root} failed", _root); }

                bool again;
                lock (_gate)
                {
                    _delivering = false;
                    again = _flushAgain;
                    _flushAgain = false;
                }
                if (!again) return;
            }
        }

        private void Deliver(List<KeyValuePair<string, FileChangeKind>> changes, bool overflowed)
        {
            if (overflowed) // too many distinct paths: tell the consumer to rescan rather than hold unbounded state
            {
                Invoke([new FileChange(FileUri.FromPath(_root), FileChangeKind.Changed)]);
                return;
            }
            for (var i = 0; i < changes.Count && !_disposed; i += MaxBatch)
            {
                var n = Math.Min(MaxBatch, changes.Count - i);
                var batch = new FileChange[n];
                for (var j = 0; j < n; j++) batch[j] = new FileChange(FileUri.FromPath(changes[i + j].Key), changes[i + j].Value);
                Invoke(batch);
            }
        }

        private void Invoke(IReadOnlyList<FileChange> batch)
        {
            if (_disposed) return; // never call back after Dispose
            try { _callback(batch); }
            catch (Exception ex) { _owner._log.LogWarning(ex, "A file-watch callback for {Root} threw; it keeps receiving events", _root); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            lock (_gate) _pending.Drain();
        }
    }
}
