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
/// <item><b>Excludes</b> (<see cref="WatchExclusion"/>): <c>excludeGlobs</c> (null = the <c>files.watcherExclude</c> setting, else built-ins;
/// empty = none) filter events by their root-relative path itself, so a folder like <c>bin/</c> appearing or going is reported while what is
/// in it is not. A directory whose contents every subscription on a root excludes — by glob, or because it holds a <c>CACHEDIR.TAG</c>
/// (Cargo's <c>target/</c>) — is not watched by the OS at all on Linux (<see cref="InotifyBackend"/> watches one directory at a time and
/// skips it), and the events of a cache directory's contents are dropped for everyone. A subscription can name paths it wants despite all
/// that (<see cref="FileWatchWants"/>): the walk goes down to them and their events are delivered to it, through the same OS watch. A
/// subscription that joins with fewer excludes, or whose wants change, has what it wants watched before its <see cref="FileWatchHandle.Ready"/>
/// (or its <see cref="FileWatchWants.SetAsync"/>) completes. Elsewhere <see cref="FileSystemWatcher"/> watches the whole tree (kqueue the
/// one directory of a non-recursive watch on macOS, <see cref="OsWatchBackend"/>) and only the events are filtered (cache tags are seen only where the watcher scans a new directory).</item>
/// <item><b>Handles</b>: one OS watch (one inotify instance on Linux) is shared by every subscription on the same root and disposed with
/// the last one. When the OS watch limit is exhausted that root degrades to a coarse polling watcher (logged once per root).</item>
/// <item><b>Roots that come and go</b>: a root that does not exist yet (<c>.cscode/</c> of a folder opened without one) waits through a
/// non-recursive watch on its nearest existing ancestor — shared like any other, usually the folder's own — and arms its OS watch when the
/// directory appears, reporting what is already inside as created. A FileSystemWatcher dies silently with its directory (no error on Linux;
/// one on a moved directory keeps reporting it under the old path); the inotify backend reports its root moved away or removed under it
/// (onRootLost). So a root is re-checked then, when a watched parent reports it deleted, moved or (re-)created, after deletions below it, and
/// whenever another subscriber joins it: a deleted root goes back to waiting, a replaced one is re-armed (what the new OS watch reports while
/// the content is announced is held until the announcement is out). Not detected with a FileSystemWatcher: a root replaced (or an empty one
/// removed) while nothing watches its parent and nobody re-watches it.</item>
/// <item><b>Roots that keep going away</b>: on Linux a <see cref="FileSystemWatcher"/> whose directory was deleted can never be released — its
/// reader thread waits forever on an inotify instance that has no watch left, so disposing it frees neither (.NET 10). Every directory lost
/// under such a watch would cost one inotify instance (the per-user limit is often 128, shared with every other program) and one thread
/// until the process exits. To bound that, a path that has lost <see cref="MaxOsWatchLosses"/> leaking OS watches is polled from then on:
/// polling follows the path, so it survives any number of deletions and re-creations at no OS cost (≤ <see cref="MaxOsWatchLosses"/> leaked
/// instances per path, instead of one per <c>rm -rf</c> of a build output or <c>git checkout</c> that removes <c>.vscode/</c>). Only a backend
/// that leaks counts (<see cref="IWatchBackend.LeaksWhenItsRootIsDeleted"/>): the Linux default, <see cref="InotifyBackend"/>, wakes its reader
/// and closes its instance on dispose whatever happened to the directory, so its root is re-armed (or waited for) every time, never polled.</item>
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
        // While a re-armed root's content is announced: what its new OS watch reports meanwhile, delivered after the announcement.
        public List<(RawKind Kind, string Path, string? OldPath)>? Held;
        public readonly object HoldGate = new();
        // Directories below the root found to hold a cache tag (copy on write; read lock-free on every event). Their contents are left out
        // for every subscription. An entry is refreshed whenever the directory is looked at again and dropped when it is reported gone.
        private string[] _cacheDirs = [];
        private readonly object _cacheGate = new();

        /// <summary>True when <paramref name="path"/> lies strictly inside a known cache directory.</summary>
        public bool InsideKnownCache(string path)
        {
            foreach (var dir in Volatile.Read(ref _cacheDirs))
                if (path.Length > dir.Length && path[dir.Length] == '/' && path.StartsWith(dir, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// <paramref name="dir"/> (below the root) is a cache directory or lies in one. Looks at the disk for its own tag every time (a fresh look
        /// also clears a stale entry: a directory re-created without the tag), and inherits the known ones above it.
        /// </summary>
        public bool InCache(string dir)
        {
            if (InsideKnownCache(dir)) return true;
            var tagged = WatchExclusion.IsCacheDirectory(dir);
            if (!tagged && Array.IndexOf(Volatile.Read(ref _cacheDirs), dir) < 0) return false; // the common case: no tag, none known
            lock (_cacheGate)
            {
                var known = Array.IndexOf(_cacheDirs, dir) >= 0;
                if (tagged && !known) Volatile.Write(ref _cacheDirs, [.. _cacheDirs, dir]);
                else if (!tagged && known) Volatile.Write(ref _cacheDirs, _cacheDirs.Where(d => !string.Equals(d, dir, StringComparison.Ordinal)).ToArray());
            }
            return tagged;
        }

        /// <summary><paramref name="path"/> was deleted or moved away: forget the cache directories at or below it.</summary>
        public void ForgetCacheAtOrBelow(string path)
        {
            if (Volatile.Read(ref _cacheDirs).Length == 0) return;
            lock (_cacheGate)
                Volatile.Write(ref _cacheDirs, _cacheDirs.Where(d => !(d.StartsWith(path, StringComparison.Ordinal) && (d.Length == path.Length || d[path.Length] == '/'))).ToArray());
        }
    }

    private readonly IoLane _lane;
    private readonly IConfigurationServiceRead? _config;
    private readonly PlatformOptions _options;
    private readonly ILogger _log;
    private readonly BackendFactory _backendFactory;
    private readonly object _gate = new();
    private readonly Dictionary<(string, bool), Source> _sources = new();
    private HashSet<string> _roots = new(PathComparer);   // every source's root; replaced (never mutated) under _gate, read lock-free from OS callbacks
    private readonly Dictionary<string, int> _osWatchLosses = new(PathComparer); // under _gate: OS watches each path lost with its directory
    private bool _disposed;

    /// <summary>Read-only view of the one setting the watcher needs, so the watcher does not depend on the whole configuration surface.</summary>
    internal interface IConfigurationServiceRead { IReadOnlyList<string>? WatcherExcludes(); }

    public FileWatcher(IoLane lane, IConfigurationService? configuration = null, PlatformOptions? options = null, ILogger<FileWatcher>? logger = null)
        : this(lane, configuration is null ? null : new ConfigRead(configuration), options, logger, null) { }

    internal FileWatcher(IoLane lane, IConfigurationServiceRead? config, PlatformOptions? options, ILogger<FileWatcher>? logger, BackendFactory? backendFactory)
    {
        _lane = lane;
        _config = config;
        _options = options ?? new PlatformOptions();
        _log = logger ?? NullLogger<FileWatcher>.Instance;
        _backendFactory = backendFactory ?? ((root, recursive, verdict, sink, fatal, rootLost) => DefaultBackend(root, recursive, verdict, sink, fatal, rootLost, _options.PollInterval));
    }

    /// <summary>
    /// inotify one directory at a time on Linux (excluded directories cost no watch); elsewhere <see cref="OsWatchBackend"/>: a
    /// <see cref="FileSystemWatcher"/>, except kqueue for a non-recursive watch on macOS (which polls the files it cannot watch every
    /// <paramref name="pollInterval"/>). The macOS backends report a lost root as the deletion of what it held, not through <paramref name="onRootLost"/>.
    /// </summary>
    internal static IWatchBackend DefaultBackend(string root, bool recursive, DirectoryVerdict verdict, RawSink sink, Action<Exception> onFatal, Action onRootLost,
        TimeSpan pollInterval) =>
        OperatingSystem.IsLinux() ? new InotifyBackend(root, recursive, verdict, sink, onFatal, onRootLost) : OsWatchBackend.Create(root, recursive, sink, onFatal, pollInterval);

    /// <summary>The setting the watcher needs, read from <paramref name="configuration"/> (tests combine it with a backend factory).</summary>
    internal static IConfigurationServiceRead ConfigReadOf(IConfigurationService configuration) => new ConfigRead(configuration);

    private sealed class ConfigRead(IConfigurationService config) : IConfigurationServiceRead
    {
        public IReadOnlyList<string>? WatcherExcludes() =>
            config.Get<Dictionary<string, bool>>(SettingKeys.FilesWatcherExclude)?.Where(kv => kv.Value).Select(kv => kv.Key).ToArray();
    }

    // ---- public API --------------------------------------------------------------------------------------------------------------

    public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges) =>
        Watch(root, recursive, excludeGlobs, onChanges, null);

    public IDisposable Watch(Uri root, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges, FileWatchWants? wants)
    {
        ArgumentNullException.ThrowIfNull(onChanges);
        var path = NormalizedPath(FileUri.ToPath(root));
        return WatchPath(path, recursive, excludeGlobs, onChanges, wants);
    }

    private static string NormalizedPath(string path)
    {
        path = Path.GetFullPath(path).TrimEnd('/', '\\');
        return path.Length == 0 ? "/" : path;
    }

    private FileWatchHandle WatchPath(string path, bool recursive, IReadOnlyList<string>? excludeGlobs, Action<IReadOnlyList<FileChange>> onChanges, FileWatchWants? wants = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var excludes = excludeGlobs ?? _config?.WatcherExcludes() ?? ExcludeMatcher.BuiltInWatcherExcludes;
        var sub = new Subscription(this, path, new ExcludeMatcher(path, excludes), wants, onChanges);

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
            sub.Source = source;
            source.Subscriptions = [.. source.Subscriptions, sub];
        }
        // What it wants may change: the subscription takes the new wants at once (a walk under way sees them), the OS watch on the lane.
        if (wants is not null)
        {
            sub.WantsSubscription = wants.Subscribe(() => { sub.RefreshWants(); return RevisitAsync(source); });
            sub.RefreshWants(); // a change between its construction and the subscription above
        }
        // The shared watch may be on a directory that was deleted, moved or replaced since. Consumers re-watch when their parent watch reports
        // such a change, so a joining subscriber is the moment to look: whoever re-watches revives the watch for every subscriber instead of
        // joining a dead one. And it may want a directory the others excluded, which the OS watch then left out.
        var ready = joined ? Task.WhenAll(source.Ready, _lane.RunAsync<Task>(_ => Recheck(source, joined: true)).Unwrap()) : source.Ready;
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

        // Announcing: what the new OS watch reports is held until the announcement is out, so that a file the scan finds is reported created
        // before any change the watch saw to it — a write under way while the watch was armed would otherwise come first, and Changed
        // followed by Created merges to Changed.
        if (announce && !polling) Volatile.Write(ref source.Held, []);
        try { return ArmBackend(source, announce, generation, polling); }
        finally { ReleaseHeld(source); }
    }

    private Task ArmBackend(Source source, bool announce, int generation, bool polling)
    {
        var armedAt = Stopwatch.GetTimestamp();
        IWatchBackend backend;
        if (polling) backend = CreatePolling(source);
        else
        {
            try
            {
                backend = _backendFactory(source.Root, source.Recursive, dir => Verdict(source, dir), (k, p, o) => OnRaw(source, k, p, o),
                    ex => OnBackendFailed(source, generation, ex), () => OnRootLost(source, generation));
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
        bool rootChanged = false;
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
                rootChanged = source.RootChangedAt >= armedAt;
            }
        }
        if (close)
        {
            backend.Dispose();
            return Task.CompletedTask;
        }
        if (failed is not null) OnBackendFailed(source, generation, failed); // it failed before it was in place: degrade it now that it is
        // A subscription that joined (or changed its wants) while the tree was walked may want a directory the walk had already skipped, and
        // its own re-check may have run before the backend was in place. (The re-check of any later change finds the backend.)
        else backend.RevisitPruned();
        // The root went (or was reported gone by its parent) while this watch was being armed; a re-check then may not have found the backend.
        if (rootChanged) ScheduleRecheck(source, TimeSpan.Zero);
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
    private static bool LeaksWhenItsRootGoes(IWatchBackend backend) => backend.LeaksWhenItsRootIsDeleted;

    /// <summary>
    /// What to do with <paramref name="dir"/> (a directory below the root of <paramref name="source"/>): watch it when a subscription wants
    /// what is in it — neither its excludes nor a cache tag leave it out —, watch it only for a want when a subscription wants a path at or
    /// below it, else leave it and everything below it out (the OS need not watch it, a new-directory scan need not enter it). Read live —
    /// from a backend's walk and reader thread — as subscriptions and their wants come and go. Looks for a cache tag only when that decides.
    /// </summary>
    private static WatchVerdict Verdict(Source source, string dir)
    {
        var subs = Volatile.Read(ref source.Subscriptions);
        bool plain = false, wanted = false;
        foreach (var sub in subs)
        {
            if (!sub.ExcludesContentsOf(dir)) plain = true;
            else if (sub.KeepsForWant(dir)) wanted = true;
        }
        if (plain && !source.InCache(dir)) return WatchVerdict.Watch;
        if (wanted || (plain && subs.Any(s => s.KeepsForWant(dir)))) return WatchVerdict.WatchForWant;
        // A cache directory itself is watched at its top (its tag or a virtual-environment marker may change); what lies below it is not.
        if (plain && !source.InsideKnownCache(dir)) return WatchVerdict.Shallow;
        return WatchVerdict.Prune;
    }

    /// <summary>A subscription on <paramref name="source"/> wants a path at or below <paramref name="dir"/> (<see cref="FileWatchWants"/>).</summary>
    private static bool AnyWantKeeps(Source source, string dir)
    {
        foreach (var sub in Volatile.Read(ref source.Subscriptions))
            if (sub.KeepsForWant(dir)) return true;
        return false;
    }

    /// <summary>On the IO lane: the subscriptions' wants changed; the OS watch picks them up. Completes when it has.</summary>
    private Task RevisitAsync(Source source) => _lane.RunAsync(_ => Revisit(source));

    private void Revisit(Source source)
    {
        IWatchBackend? backend;
        lock (_gate) backend = source.Closed ? null : source.Backend;
        backend?.RevisitPruned(); // none in place: it is being armed and revisits itself once it is (or the root is missing, and walks fresh)
    }

    /// <summary>
    /// The backend of <paramref name="generation"/> saw its root go — moved away, or removed under it (for one inside a directory nobody else
    /// watches, no parent reports it) —: look at the path again, as when a watched parent reports it.
    /// </summary>
    private void OnRootLost(Source source, int generation)
    {
        lock (_gate)
        {
            if (source.Closed || source.Generation != generation) return;
            source.RootChangedAt = Stopwatch.GetTimestamp();
        }
        ScheduleRecheck(source, TimeSpan.Zero);
    }

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
        try { waiter = WatchPath(ancestor, recursive: false, excludeGlobs: [], onChanges: changes => OnAncestorChanged(source, next, changes)); }
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
    /// neither. <paramref name="joined"/>: a subscription just joined, so an OS watch that stays in place also watches what it wants and the
    /// others excluded (a re-armed one walks with it already). Returns when the new watch (if any) is armed.
    /// </summary>
    private Task Recheck(Source source, bool joined = false)
    {
        Volatile.Write(ref source.CheckScheduled, 0); // anything after this point schedules another check
        var exists = Directory.Exists(source.Root);
        var nearest = exists ? null : NearestExistingAncestor(source.Root).Ancestor;
        IDisposable? waiter = null;
        IWatchBackend? backend = null;
        IWatchBackend? stays = null;
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
            // Consistent (or being armed right now by another lane item, which looks at the disk itself and revisits what it pruned).
            else if (joined && source.Backend is { } current) stays = current;
            else return Task.CompletedTask;
        }

        if (stays is not null)
        {
            stays.RevisitPruned();
            return Task.CompletedTask;
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
        // The configured excludes whatever the subscriptions say (a subscriber that excludes nothing must not make every poll walk node_modules),
        // and cache directories — unless a subscription wants a path there.
        var pruner = new ExcludeMatcher(source.Root, _config?.WatcherExcludes() ?? ExcludeMatcher.BuiltInWatcherExcludes);
        return new PollingBackend(source.Root, source.Recursive, (k, p, o) => OnRaw(source, k, p, o), _lane, _options.PollInterval,
            dir => (pruner.IsExcluded(dir) || source.InCache(dir)) && !AnyWantKeeps(source, dir), _log);
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
        // The others keep the root watched: what only this one wanted is let go (FileWatchWants: "lets go of the ones no longer wanted").
        else if (sub.HasWants) _lane.Post(() => Revisit(source), ex => _log.LogDebug(ex, "Revisiting the watch on {Root} failed", source.Root));
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

    /// <summary>Delivers what <see cref="Arm"/> held back while announcing a root's content, in order, ahead of anything reported after.</summary>
    private void ReleaseHeld(Source source)
    {
        if (Volatile.Read(ref source.Held) is null) return;
        lock (source.HoldGate)
        {
            if (source.Held is not { } held) return;
            foreach (var (kind, path, oldPath) in held) Deliver(source, kind, path, oldPath);
            Volatile.Write(ref source.Held, null); // only now: a report arriving meanwhile waits for the lock, then finds nothing held
        }
    }

    private void OnRaw(Source source, RawKind kind, string path, string? oldPath)
    {
        if (Volatile.Read(ref source.Held) is not null)
        {
            lock (source.HoldGate)
            {
                if (source.Held is { } held)
                {
                    held.Add((kind, path, oldPath));
                    return;
                }
            }
        }
        Deliver(source, kind, path, oldPath);
    }

    private void Deliver(Source source, RawKind kind, string path, string? oldPath)
    {
        foreach (var sub in Volatile.Read(ref source.Subscriptions)) sub.Add(kind, path, oldPath);
        // A cache directory that went is not one any more (a new directory at its path is looked at afresh).
        if (kind == RawKind.Deleted) source.ForgetCacheAtOrBelow(path);
        else if (kind == RawKind.Renamed && oldPath is not null) source.ForgetCacheAtOrBelow(oldPath);

        if (kind != RawKind.Changed)
        {
            NoticeRootEvent(kind, path, oldPath);
            // Deletions below this root may be the root itself going away (`rm -r`): look once the burst has settled. (Polling follows the path.)
            if (kind == RawKind.Deleted && !source.UsingPolling) ScheduleRecheck(source, _options.WatchDebounce);
        }

        // No OS watch sees what happens inside a directory in the instant between its creation and its own watch being armed (`mkdir -p a/b/c`
        // reports only `a`). So when a directory appears, look inside it ourselves and announce what is already there. (A backend arms a new
        // directory's watches before it reports the directory, so whatever this scan comes too early for, the backend reports.)
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
            if (!PathComparer.Equals(dir, source.Root) && Verdict(source, dir) is WatchVerdict.Prune or WatchVerdict.Shallow) continue; // nobody wants this subtree: do not even enumerate it
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir, "*", options); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                if (++seen > MaxScanEntries) { _log.LogDebug("Stopped scanning new directory {Root} after {Max} entries", root, MaxScanEntries); return; }
                foreach (var sub in Volatile.Read(ref source.Subscriptions)) sub.Add(RawKind.Announced, entry, null);
                if (!source.Recursive) continue;
                var info = new FileInfo(entry);
                if (info.LinkTarget is null && Directory.Exists(entry)) stack.Push(entry); // never follow symlinked directories
            }
        }
    }

    // ---- one subscriber ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A subscription's <see cref="FileWatchWants"/> as local paths below its root, with the directories on the way down to them, so that the
    /// per-directory and per-event questions are a few set lookups.
    /// </summary>
    private sealed class WantSet
    {
        private readonly HashSet<string> _directories = new(PathComparer); // direct entries wanted
        private readonly string[] _trees;                                   // whole subtrees wanted
        private readonly HashSet<string> _ways = new(PathComparer);         // directories between the root and a wanted path

        private WantSet(string root, FileWatchWants wants)
        {
            var trees = new List<string>();
            foreach (var (uri, tree) in wants.Directories.Select(static u => (u, false)).Concat(wants.Trees.Select(static u => (u, true))))
            {
                string path;
                try { path = NormalizedPath(FileUri.ToPath(uri)); }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentException) { continue; } // not a local path
                if (!IsAtOrBelow(path, root)) continue;
                if (tree) trees.Add(path); else _directories.Add(path);
                for (var dir = Path.GetDirectoryName(path); dir is not null && dir.Length > root.Length && IsAtOrBelow(dir, root); dir = Path.GetDirectoryName(dir))
                    _ways.Add(dir);
            }
            _trees = [.. trees];
        }

        public static WantSet? From(string root, FileWatchWants? wants) =>
            wants is null || (wants.Directories.Count == 0 && wants.Trees.Count == 0) ? null : new WantSet(root, wants);

        /// <summary>A wanted path is at or below <paramref name="dir"/>, or <paramref name="dir"/> is in a wanted tree: it must be watched.</summary>
        public bool Keeps(string dir) => _ways.Contains(dir) || _directories.Contains(dir) || InTree(dir);

        /// <summary>An event for <paramref name="path"/> is wanted: an entry of a wanted directory (or that directory), or in a wanted tree.</summary>
        public bool Covers(string path) =>
            _directories.Contains(path) || (Path.GetDirectoryName(path) is { } parent && _directories.Contains(parent)) || InTree(path);

        private bool InTree(string path)
        {
            foreach (var tree in _trees)
                if (IsAtOrBelow(path, tree)) return true;
            return false;
        }

        private static bool IsAtOrBelow(string path, string dir) =>
            path.StartsWith(dir, PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
            && (path.Length == dir.Length || path[dir.Length] == '/' || path[dir.Length] == '\\' || dir.EndsWith('/'));
    }

    private sealed class Subscription : IDisposable
    {
        private readonly FileWatcher _owner;
        private readonly string _root;
        private readonly ExcludeMatcher _excludes;
        private readonly FileWatchWants? _wantsSource;
        private readonly Action<IReadOnlyList<FileChange>> _callback;
        private readonly ChangeCoalescer _pending = new();
        private readonly object _gate = new();
        private readonly Timer _timer;
        private WantSet? _wants;
        private long _firstTick;      // when the current burst began (0 = idle)
        private bool _delivering;
        private bool _flushAgain;
        private volatile bool _disposed;

        public Subscription(FileWatcher owner, string root, ExcludeMatcher excludes, FileWatchWants? wants, Action<IReadOnlyList<FileChange>> callback)
        {
            _owner = owner;
            _root = root;
            _excludes = excludes;
            _wantsSource = wants;
            _wants = WantSet.From(root, wants);
            _callback = callback;
            _timer = new Timer(static s => ((Subscription)s!).OnTimer(), this, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>The source it is one of (set when it joins). Its cache directories are left out of every subscription's events.</summary>
        public Source? Source;

        /// <summary>Its subscription to <see cref="FileWatchWants"/> changes, ended with it.</summary>
        public IDisposable? WantsSubscription;

        /// <summary>It was given <see cref="FileWatchWants"/>: when it goes, the OS watch looks at what only it wanted.</summary>
        public bool HasWants => _wantsSource is not null;

        /// <summary>Takes the current <see cref="FileWatchWants"/> (on the setter's thread, before the OS watch is told).</summary>
        public void RefreshWants() => Volatile.Write(ref _wants, WantSet.From(_root, _wantsSource));

        /// <summary>Its excludes leave out what is in <paramref name="dir"/> (wants aside).</summary>
        public bool ExcludesContentsOf(string dir) => _excludes.IsExcluded(dir);

        /// <summary>It wants a path at or below <paramref name="dir"/>, or <paramref name="dir"/> lies in a tree it wants.</summary>
        public bool KeepsForWant(string dir) => Volatile.Read(ref _wants)?.Keeps(dir) == true;

        // An event it does not get: excluded by its own path, or in a cache directory — unless it asked for that path.
        private bool Drops(string path) =>
            (_excludes.IsExcludedEntry(path) || Source?.InsideKnownCache(path) == true) && Volatile.Read(ref _wants)?.Covers(path) != true;

        public void Add(RawKind kind, string path, string? oldPath)
        {
            if (_disposed) return;
            // Excluded paths are dropped here, before they cost a dictionary slot. For a rename only the excluded half is dropped.
            if (kind == RawKind.Renamed)
            {
                var newExcluded = Drops(path);
                var oldExcluded = oldPath is not null && Drops(oldPath);
                if (newExcluded && oldExcluded) return;
                if (newExcluded) { kind = RawKind.Deleted; path = oldPath!; oldPath = null; }
                else if (oldExcluded) oldPath = null;
            }
            else if (Drops(path)) return;

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
            WantsSubscription?.Dispose();
            _timer.Dispose();
            lock (_gate) _pending.Drain();
        }
    }
}
