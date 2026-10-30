using System.Diagnostics;
using CsCode.Core.Platform;
using CsCode.Platform.FileSystem;
using CsCode.Platform.Io;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCode.Platform.Watching;

/// <summary>
/// Handle returned by <see cref="FileWatcher.Watch"/>. <see cref="Ready"/> completes once the OS watch is armed (events that happen before are
/// not reported) or, for a root that does not exist yet, once the watch for its appearance is.
/// </summary>
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
/// <item><b>Roots that come and go</b>: a root that does not exist yet (<c>.cscode/</c> of a folder opened without one) waits through a
/// non-recursive watch on its nearest existing ancestor — shared like any other, usually the folder's own — and arms its OS watch when the
/// directory appears, reporting what is already inside as created. An OS watch dies silently with its directory (no error on Linux; one on
/// a moved directory keeps reporting it under the old path), so a root is re-checked when a watched parent reports it deleted, moved or
/// (re-)created, after deletions below it, and whenever another subscriber joins it: a deleted root goes back to waiting, a replaced one is
/// re-armed. Not detected: a root replaced (or an empty one removed) while nothing watches its parent and nobody re-watches it.</item>
/// <item><b>Roots that keep going away</b>: on Linux a <see cref="FileSystemWatcher"/> whose directory was deleted can never be released — its
/// reader thread waits forever on an inotify instance that has no watch left, so disposing it frees neither (.NET 10). Every directory lost
/// under an OS watch therefore costs one inotify instance (the per-user limit is often 128, shared with every other program) and one thread
/// until the process exits. To bound that, a path that has lost <see cref="MaxOsWatchLosses"/> OS watches is polled from then on: polling
/// follows the path, so it survives any number of deletions and re-creations at no OS cost (≤ <see cref="MaxOsWatchLosses"/> leaked
/// instances per path, instead of one per <c>rm -rf</c> of a build output or <c>git checkout</c> that removes <c>.vscode/</c>).</item>
/// </list>
/// </summary>
public sealed class FileWatcher : IFileWatcher, IDisposable
{
    public const int MaxBatch = 1000;

    /// <summary>
    /// OS watches a path may lose (its directory deleted, moved or replaced under the watch) before it is polled instead. Two, not one: the
    /// same creation reported late by a second watched parent looks like a replacement (and re-arms a watch that was fine), and a single
    /// such report must not demote a settings directory to polling for the rest of the session.
    /// </summary>
    public const int MaxOsWatchLosses = 2;

    // Bounds the bookkeeping, not the leak: each tracked path stands for at least one lost OS watch, so the OS limit is reached long before.
    private const int MaxTrackedLossPaths = 4096;

    // Per volume on macOS (files-8): on a case-sensitive APFS volume "Foo" and "foo" are two roots, whose events must not be taken for each other's.
    private static readonly IEqualityComparer<string> PathComparer = VolumePathComparer.Current;

    private sealed class Source(string root, bool recursive)
    {
        public string Root { get; } = root;
        public bool Recursive { get; } = recursive;
        public Subscription[] Subscriptions = [];
        // At most one of Backend/Waiter is set (neither while being (re-)armed): the OS watch of the root, or — while the root does not
        // exist — the watch on its nearest existing ancestor (WaitingIn) that tells us when it appears. Guarded by the watcher's _gate.
        public IWatchBackend? Backend;
        public IDisposable? Waiter;
        public string? WaitingIn;
        public long ArmedAt;         // Stopwatch timestamp taken just before the backend was created
        public long RootChangedAt;   // when a watched parent last reported the root created, deleted or moved
        public bool Closed;
        public bool UsingPolling;    // the backend polls (by policy or after the OS watch failed): it follows the path and is never re-armed
        public int Generation;       // bumped for every backend put in place; a failure report names the one it is about
        public Exception? PendingFailure; // the backend of the current generation failed before Arm had put it in place
        public int CheckScheduled;   // 1 while a re-check is pending: a burst of events costs one stat
        public Task Ready = Task.CompletedTask;
    }

    private readonly IoLane _lane;
    private readonly IConfigurationServiceRead? _config;
    private readonly PlatformOptions _options;
    private readonly ILogger _log;
    private readonly BackendFactory _fswFactory;
    private readonly object _gate = new();
    private readonly Dictionary<(string, bool), Source> _sources = new();
    private HashSet<string> _roots = new(PathComparer);   // every source's root; replaced (never mutated) under _gate, read lock-free from OS callbacks
    private readonly Dictionary<string, int> _osWatchLosses = new(PathComparer); // under _gate: OS watches each path lost with its directory
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
        var path = Path.GetFullPath(FileUri.ToPath(root)).TrimEnd('/', '\\');
        if (path.Length == 0) path = "/";
        return WatchPath(path, recursive, excludeGlobs, onChanges);
    }

    private FileWatchHandle WatchPath(string path, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var excludes = excludeGlobs ?? _config?.WatcherExcludes() ?? ExcludeMatcher.BuiltInWatcherExcludes;
        var sub = new Subscription(this, path, new ExcludeMatcher(path, excludes), onChanges);

        Source source;
        bool joined;
        lock (_gate)
        {
            joined = _sources.TryGetValue((path, recursive), out source!);
            if (!joined)
            {
                source = new Source(path, recursive);
                _sources[(path, recursive)] = source;
                PublishRoots();
                source.Ready = _lane.RunAsync<Task>(_ => Arm(source, announce: false)).Unwrap();
            }
            source.Subscriptions = [.. source.Subscriptions, sub];
        }
        // The shared watch may be on a directory that was deleted, moved or replaced since. Consumers re-watch when their parent watch reports
        // such a change, so a joining subscriber is the moment to look: whoever re-watches revives the watch for every subscriber instead of
        // joining a dead one.
        var ready = joined ? Task.WhenAll(source.Ready, _lane.RunAsync<Task>(_ => Recheck(source)).Unwrap()) : source.Ready;
        return new FileWatchHandle(ready, () => Release(source, sub));
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
            PublishRoots();
        }
        foreach (var s in all) CloseSource(s);
    }

    /// <summary>Number of live OS-level backends (tests/diagnostics).</summary>
    public int ActiveBackends { get { lock (_gate) return _sources.Values.Count(s => s.Backend is not null); } }

    // Under _gate. Sources come and go rarely (a watch on a new root, the last subscriber leaving), events are frequent: copy on write.
    private void PublishRoots() => Volatile.Write(ref _roots, new HashSet<string>(_sources.Keys.Select(static k => k.Item1), PathComparer));

    // ---- arming / releasing ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// On the IO lane: arms the OS watch of an existing root, or starts waiting for a missing one; a root that lost too many OS watches
    /// (<see cref="MaxOsWatchLosses"/>) is polled, present or not. <paramref name="announce"/>: the root appeared (or was replaced) after
    /// it was first watched, so whatever is already inside was created unseen and is reported now. Returns when the watch (or the wait) is armed.
    /// </summary>
    private Task Arm(Source source, bool announce)
    {
        int generation;
        bool polling;
        lock (_gate)
        {
            if (source.Closed) return Task.CompletedTask;
            generation = ++source.Generation;
            source.PendingFailure = null;
            polling = IsPolledForLosses(source.Root);
        }
        // Polling follows the path: it needs neither a wait for a missing root nor a re-arm when the directory goes and comes back.
        if (!polling && !Directory.Exists(source.Root)) return BeginWaiting(source);

        var armedAt = Stopwatch.GetTimestamp();
        IWatchBackend backend;
        if (polling) backend = CreatePolling(source);
        else
        {
            try
            {
                backend = _fswFactory(source.Root, source.Recursive, (k, p, o) => OnRaw(source, k, p, o), ex => OnBackendFailed(source, generation, ex));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                if (!Directory.Exists(source.Root)) return BeginWaiting(source); // deleted again in the meantime: not an OS limit
                _log.LogWarning("Cannot create an OS file watch for {Root} ({Reason}); falling back to polling every {Interval:F1}s. On Linux, raise fs.inotify.max_user_watches / max_user_instances.",
                    source.Root, ex.Message, _options.PollInterval.TotalSeconds);
                backend = CreatePolling(source);
                polling = true;
            }
        }

        bool close;
        Exception? failed = null;
        lock (_gate)
        {
            close = source.Closed || source.Generation != generation;
            if (!close)
            {
                source.Backend = backend;
                source.UsingPolling = polling;
                source.ArmedAt = armedAt;
                failed = source.PendingFailure;
                source.PendingFailure = null;
            }
        }
        if (close)
        {
            backend.Dispose();
            return Task.CompletedTask;
        }
        if (failed is not null) OnBackendFailed(source, generation, failed); // it failed before it was in place: degrade it now that it is
        // After the backend is in place (and, for polling, has taken its baseline): whatever appears from now on is reported by the backend,
        // whatever was there before by this scan — at worst twice, never not at all.
        if (announce) ScanNewDirectory(source, source.Root);
        return Task.CompletedTask;
    }

    // Under _gate.
    private bool IsPolledForLosses(string root) => _osWatchLosses.TryGetValue(root, out var n) && n >= MaxOsWatchLosses;

    /// <summary>Under _gate: counts one OS watch lost by <paramref name="root"/>. True when that makes the root polled from now on.</summary>
    private bool CountOsWatchLoss(string root)
    {
        _osWatchLosses.TryGetValue(root, out var n);
        if (n >= MaxOsWatchLosses || (n == 0 && _osWatchLosses.Count >= MaxTrackedLossPaths)) return false;
        _osWatchLosses[root] = ++n;
        return n == MaxOsWatchLosses;
    }

    // Only FileSystemWatcher on Linux keeps its inotify instance (and thread) when its directory is deleted; other backends cost nothing to lose.
    private static bool LeaksWhenItsRootGoes(IWatchBackend backend) => backend is FswBackend && FswBackend.LeaksWhenItsRootIsDeleted;

    private void LogPollingFromNowOn(string root) =>
        _log.LogInformation("'{Root}' has lost {Count} OS file watches with its directory (deleted, moved or replaced); each costs an inotify instance and a thread until CsCode exits, so it is polled every {Interval:F1}s from now on.",
            root, MaxOsWatchLosses, _options.PollInterval.TotalSeconds);

    private Task BeginWaiting(Source source)
    {
        var (ancestor, next) = NearestExistingAncestor(source.Root);
        if (ancestor is null)
        {
            _log.LogWarning("Cannot watch '{Root}': none of its parent directories exists", source.Root);
            return Task.CompletedTask;
        }

        FileWatchHandle waiter;
        try { waiter = WatchPath(ancestor, recursive: false, excludeGlobs: [], changes => OnAncestorChanged(source, next, changes)); }
        catch (ObjectDisposedException) { return Task.CompletedTask; } // the watcher is shutting down
        bool close;
        lock (_gate)
        {
            close = source.Closed;
            if (!close) { source.Waiter = waiter; source.WaitingIn = ancestor; }
        }
        if (close) { waiter.Dispose(); return Task.CompletedTask; }
        _log.LogDebug("'{Root}' is not a directory (yet); watching '{Ancestor}' for it to appear", source.Root, ancestor);

        // `next` may have appeared between the existence check above and the ancestor's OS watch being armed: look once more after.
        return waiter.Ready.ContinueWith(_ => ScheduleRecheck(source, TimeSpan.Zero), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>The deepest existing directory above <paramref name="root"/>, and its child on the way down to the root.</summary>
    private static (string? Ancestor, string Next) NearestExistingAncestor(string root)
    {
        var next = root;
        var dir = Path.GetDirectoryName(root);
        while (dir is not null && !Directory.Exists(dir)) { next = dir; dir = Path.GetDirectoryName(dir); }
        return (dir, next);
    }

    // On the IO lane (the ancestor subscription's delivery). Anything happening to the next step towards the root — or the ancestor's
    // "rescan" marker after an overflow — may mean that the root, or a directory on the way to it, exists now.
    private void OnAncestorChanged(Source source, string next, IReadOnlyList<FileChange> changes)
    {
        var ancestor = Path.GetDirectoryName(next);
        foreach (var c in changes)
        {
            string p;
            try { p = FileUri.ToPath(c.Uri).TrimEnd('/', '\\'); } catch (NotSupportedException) { continue; }
            if (PathComparer.Equals(p, next) || PathComparer.Equals(p, ancestor))
            {
                ScheduleRecheck(source, TimeSpan.Zero);
                return;
            }
        }
    }

    /// <summary>Queues <see cref="Recheck"/> on the IO lane after <paramref name="delay"/>; one pending check per source at a time.</summary>
    private void ScheduleRecheck(Source source, TimeSpan delay)
    {
        if (source.Closed || Interlocked.Exchange(ref source.CheckScheduled, 1) == 1) return;
        void Post() => _lane.Post(() => _ = Recheck(source), ex => _log.LogDebug(ex, "Re-checking the watch on {Root} failed", source.Root));
        if (delay <= TimeSpan.Zero) Post();
        else _ = Task.Delay(delay).ContinueWith(_ => Post(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// On the IO lane: compares a source with its root on disk. A waiting source whose root exists now arms its OS watch (announcing the
    /// content); one waiting in an ancestor that is no longer the nearest existing one moves its wait; an armed source whose root is gone
    /// goes back to waiting, and one whose root was re-created since it armed is re-armed on the new directory. A polling source needs
    /// neither. Returns when the new watch (if any) is armed.
    /// </summary>
    private Task Recheck(Source source)
    {
        Volatile.Write(ref source.CheckScheduled, 0); // anything after this point schedules another check
        var exists = Directory.Exists(source.Root);
        var nearest = exists ? null : NearestExistingAncestor(source.Root).Ancestor;
        IDisposable? waiter = null;
        IWatchBackend? backend = null;
        var pollingFromNowOn = false;
        lock (_gate)
        {
            if (source.Closed) return Task.CompletedTask;
            if (source.Waiter is not null && (exists || !PathComparer.Equals(nearest, source.WaitingIn)))
            {
                waiter = source.Waiter;
                source.Waiter = null;
                source.WaitingIn = null;
            }
            else if (source.Backend is not null && !source.UsingPolling && (!exists || source.RootChangedAt >= source.ArmedAt))
            {
                backend = source.Backend;
                source.Backend = null;
                if (LeaksWhenItsRootGoes(backend)) pollingFromNowOn = CountOsWatchLoss(source.Root);
            }
            else return Task.CompletedTask; // consistent (or being armed right now by another lane item, which looks at the disk itself)
        }

        waiter?.Dispose();
        if (backend is not null)
        {
            if (exists) _log.LogDebug("'{Root}' was re-created; watching the new directory", source.Root);
            else _log.LogDebug("'{Root}' was deleted or moved away; watching for it to appear again", source.Root);
            backend.Dispose();
        }
        if (pollingFromNowOn) LogPollingFromNowOn(source.Root);
        // Always announce: whatever directory Arm finds at the root now appeared after the watch was armed — also when it appears between
        // the look above and Arm's own.
        return Arm(source, announce: true);
    }

    private IWatchBackend CreatePolling(Source source)
    {
        var pruner = new ExcludeMatcher(source.Root, _config?.WatcherExcludes() ?? ExcludeMatcher.BuiltInWatcherExcludes);
        return new PollingBackend(source.Root, source.Recursive, (k, p, o) => OnRaw(source, k, p, o), _lane, _options.PollInterval, pruner.IsExcluded, _log);
    }

    // The OS reported a fatal problem after arming (e.g. the inotify limit hit while a new subdirectory was added): degrade, once.
    // `generation` names the backend that failed: a report from one that was already replaced (a re-check re-armed the source) is stale.
    private void OnBackendFailed(Source source, int generation, Exception ex)
    {
        _lane.Post(() =>
        {
            IWatchBackend old;
            int pollingGeneration;
            lock (_gate)
            {
                if (source.Closed || source.Generation != generation || source.UsingPolling) return;
                if (source.Backend is null) { source.PendingFailure ??= ex; return; } // Arm is still putting it in place: it degrades it after
                old = source.Backend;
                source.Backend = null;
                source.UsingPolling = true;
                pollingGeneration = ++source.Generation;
            }
            _log.LogWarning("File watch on {Root} failed ({Reason}); falling back to polling.", source.Root, ex.Message);
            old.Dispose();
            var polling = CreatePolling(source);
            lock (_gate)
            {
                if (!source.Closed && source.Generation == pollingGeneration)
                {
                    source.Backend = polling;
                    source.ArmedAt = Stopwatch.GetTimestamp();
                    return;
                }
            }
            polling.Dispose(); // closed meanwhile
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
                PublishRoots();
            }
        }
        if (close) CloseSource(source);
    }

    private void CloseSource(Source source)
    {
        IWatchBackend? backend;
        IDisposable? waiter;
        long armedAt;
        lock (_gate)
        {
            source.Closed = true;
            backend = source.Backend;
            source.Backend = null;
            waiter = source.Waiter;
            source.Waiter = null;
            armedAt = source.ArmedAt;
        }
        foreach (var s in source.Subscriptions) s.Dispose();
        waiter?.Dispose();
        if (backend is null) return;
        // FileSystemWatcher.Dispose can take a while (it stops its reader): never on the caller's thread — and neither is the look at the disk.
        _lane.Post(() =>
        {
            // Modules drop their watch when its directory goes away and watch the path again when it is back (the settings service does):
            // a watch closed after its directory went counts as lost just like one re-armed by a re-check.
            if (LeaksWhenItsRootGoes(backend) && !Volatile.Read(ref _disposed))
            {
                var gone = !Directory.Exists(source.Root);
                bool pollingFromNowOn;
                lock (_gate) pollingFromNowOn = (gone || source.RootChangedAt >= armedAt) && CountOsWatchLoss(source.Root);
                if (pollingFromNowOn) LogPollingFromNowOn(source.Root);
            }
            backend.Dispose();
        });
    }

    private void OnRaw(Source source, RawKind kind, string path, string? oldPath)
    {
        foreach (var sub in Volatile.Read(ref source.Subscriptions)) sub.Add(kind, path, oldPath);

        if (kind != RawKind.Changed)
        {
            NoticeRootEvent(kind, path, oldPath);
            // Deletions below this root may be the root itself going away (`rm -r`): look once the burst has settled. (Polling follows the path.)
            if (kind == RawKind.Deleted && !source.UsingPolling) ScheduleRecheck(source, _options.WatchDebounce);
        }

        // FileSystemWatcher cannot see what happens inside a directory in the instant between its creation and its own watch being armed
        // (`mkdir -p a/b/c` reports only `a`). So when a directory appears, look inside it ourselves and announce what is already there.
        if (source.Recursive && !source.UsingPolling && kind is RawKind.Created or RawKind.Renamed)
            _lane.Post(() => ScanNewDirectory(source, path), ex => _log.LogDebug(ex, "Scanning new directory {Path} failed", path));
    }

    /// <summary>
    /// A watched parent reported something about the root of another source: it was created, deleted or renamed — which that source's own OS
    /// watch cannot see. Called from OS callbacks, so the common case (the path is no source's root) is one lock-free set lookup.
    /// </summary>
    private void NoticeRootEvent(RawKind kind, string path, string? oldPath)
    {
        var roots = Volatile.Read(ref _roots);
        var newIsRoot = roots.Contains(path);
        var oldIsRoot = oldPath is not null && roots.Contains(oldPath);
        if (!newIsRoot && !oldIsRoot) return;

        var now = Stopwatch.GetTimestamp();
        List<Source>? hits = null;
        lock (_gate)
        {
            foreach (var s in _sources.Values)
            {
                if (!(newIsRoot && PathComparer.Equals(s.Root, path)) && !(oldIsRoot && PathComparer.Equals(s.Root, oldPath))) continue;
                // Created, deleted or moved (into place or away): an OS watch armed before this is on a directory that is no longer there —
                // even if the path exists again by the time anyone looks.
                s.RootChangedAt = now;
                (hits ??= []).Add(s);
            }
        }
        if (hits is not null) foreach (var s in hits) ScheduleRecheck(s, TimeSpan.Zero);
    }

    private const int MaxScanEntries = 20_000;

    /// <summary>Reports everything below <paramref name="root"/> (only its direct entries for a non-recursive source) as created.</summary>
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
                if (!source.Recursive) continue;
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
