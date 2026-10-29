using System.Diagnostics;
using CsCode.Core.Platform;
using CsCode.Platform.Io;
using CsCode.Platform.Tests.Support;
using CsCode.Platform.Watching;
using CsCode.TestDoubles;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CsCode.Platform.Tests;

public class ChangeCoalescerTests
{
    private static Dictionary<string, FileChangeKind> Run(params (RawKind Kind, string Path, string? Old)[] events)
    {
        var c = new ChangeCoalescer();
        foreach (var (k, p, o) in events) c.Add(k, p, o);
        return c.Drain().Changes.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    [Theory]
    [InlineData("Created", "Changed", FileChangeKind.Created)]
    [InlineData("Created", "Created", FileChangeKind.Created)]
    [InlineData("Changed", "Changed", FileChangeKind.Changed)]
    [InlineData("Changed", "Created", FileChangeKind.Changed)]
    [InlineData("Changed", "Deleted", FileChangeKind.Deleted)]
    [InlineData("Deleted", "Created", FileChangeKind.Changed)]   // replaced
    [InlineData("Deleted", "Changed", FileChangeKind.Changed)]
    [InlineData("Deleted", "Deleted", FileChangeKind.Deleted)]
    public void Pairs_merge_per_the_documented_rules(string first, string second, FileChangeKind expected)
    {
        var r = Run((Enum.Parse<RawKind>(first), "/w/f", null), (Enum.Parse<RawKind>(second), "/w/f", null));
        Assert.Equal(expected, Assert.Single(r).Value);
    }

    [Fact]
    public void Created_then_deleted_within_the_window_vanishes()
    {
        Assert.Empty(Run((RawKind.Created, "/w/tmp", null), (RawKind.Changed, "/w/tmp", null), (RawKind.Deleted, "/w/tmp", null)));
    }

    [Fact]
    public void Delete_create_delete_is_a_delete_and_a_long_save_dance_is_one_entry()
    {
        var r = Run((RawKind.Deleted, "/w/f", null), (RawKind.Created, "/w/f", null), (RawKind.Deleted, "/w/f", null));
        Assert.Equal(FileChangeKind.Deleted, Assert.Single(r).Value);

        var many = Enumerable.Range(0, 500).Select(_ => (RawKind.Changed, "/w/log.txt", (string?)null)).ToArray();
        Assert.Single(Run(many));
    }

    [Fact]
    public void A_rename_is_a_delete_of_the_old_path_and_a_create_of_the_new_one()
    {
        var r = Run((RawKind.Renamed, "/w/new.txt", "/w/old.txt"));
        Assert.Equal(FileChangeKind.Deleted, r["/w/old.txt"]);
        Assert.Equal(FileChangeKind.Created, r["/w/new.txt"]);
    }

    [Fact]
    public void A_rename_chain_collapses()
    {
        // a -> b -> c within one window: a deleted, c created, b never existed to the consumer
        var r = Run((RawKind.Renamed, "/w/b", "/w/a"), (RawKind.Renamed, "/w/c", "/w/b"));
        Assert.Equal(FileChangeKind.Deleted, r["/w/a"]);
        Assert.Equal(FileChangeKind.Created, r["/w/c"]);
        Assert.DoesNotContain("/w/b", r.Keys);
    }

    [Fact]
    public void Rename_over_an_existing_file_reports_it_as_changed()
    {
        var r = Run((RawKind.Deleted, "/w/target", null), (RawKind.Renamed, "/w/target", "/w/tmp"));
        Assert.Equal(FileChangeKind.Changed, r["/w/target"]);
        Assert.Equal(FileChangeKind.Deleted, r["/w/tmp"]);
    }

    [Fact]
    public void Deleting_a_directory_drops_events_for_everything_below_it()
    {
        var r = Run(
            (RawKind.Deleted, "/w/dir/a.txt", null), (RawKind.Deleted, "/w/dir/sub/b.txt", null), (RawKind.Deleted, "/w/dir/sub", null),
            (RawKind.Deleted, "/w/dir", null), (RawKind.Changed, "/w/dir.txt", null), (RawKind.Changed, "/w/dir2/x", null));
        Assert.Equal(["/w/dir", "/w/dir.txt", "/w/dir2/x"], r.Keys.Order().ToArray());
    }

    [Fact]
    public void Drain_is_sorted_and_resets()
    {
        var c = new ChangeCoalescer();
        c.Add(RawKind.Created, "/w/b"); c.Add(RawKind.Created, "/w/a"); c.Add(RawKind.Created, "/w/c");
        Assert.Equal(["/w/a", "/w/b", "/w/c"], c.Drain().Changes.Select(k => k.Key).ToArray());
        Assert.True(c.IsEmpty);
        Assert.Empty(c.Drain().Changes);
    }

    [Fact]
    public void State_is_bounded_a_flood_of_distinct_paths_degrades_to_a_rescan_marker()
    {
        var c = new ChangeCoalescer();
        for (var i = 0; i <= ChangeCoalescer.MaxPaths + 10; i++) c.Add(RawKind.Created, "/w/f" + i);
        Assert.True(c.Overflowed);
        Assert.True(c.Count <= ChangeCoalescer.MaxPaths);
        var (changes, overflowed) = c.Drain();
        Assert.True(overflowed);
        Assert.Empty(changes);
        Assert.False(c.Overflowed);            // and it recovers
        c.Add(RawKind.Created, "/w/again");
        Assert.Single(c.Drain().Changes);
    }
}

public class ExcludeMatcherTests
{
    private static bool Ex(string root, string path, params string[] globs) => new ExcludeMatcher(root, globs).IsExcluded(path);

    [Theory]
    [InlineData("/w/node_modules/pkg/index.js", true)]
    [InlineData("/w/node_modules", true)]                    // the directory itself
    [InlineData("/w/src/node_modules/pkg/x.js", true)]       // nested
    [InlineData("/w/.git/objects/ab/cdef", true)]
    [InlineData("/w/.git/HEAD", false)]                      // only objects are excluded
    [InlineData("/w/.git/index.lock", false)]
    [InlineData("/w/bin/Debug/app.dll", true)]
    [InlineData("/w/src/obj/x.json", true)]
    [InlineData("/w/src/Program.cs", false)]
    [InlineData("/w/binary.txt", false)]                     // "bin" must not match by prefix
    [InlineData("/w/obj.txt", false)]
    public void Default_watcher_excludes(string path, bool excluded) =>
        Assert.Equal(excluded, new ExcludeMatcher("/w", ExcludeMatcher.BuiltInWatcherExcludes).IsExcluded(path));

    [Fact]
    public void Matching_is_relative_to_the_watch_root_so_a_root_inside_an_excluded_name_still_works()
    {
        // The root itself lives under .../bin/...: the exclude must apply to paths BELOW the root only.
        Assert.False(Ex("/home/u/bin/proj", "/home/u/bin/proj/src/a.cs", "**/bin/**"));
        Assert.True(Ex("/home/u/bin/proj", "/home/u/bin/proj/bin/a.dll", "**/bin/**"));
        Assert.False(Ex("/w", "/w", "**/*"));                // the root is never excluded
    }

    [Fact]
    public void Custom_patterns_and_empty_lists()
    {
        Assert.True(Ex("/w", "/w/a/b/c.tmp", "**/*.tmp"));
        Assert.False(Ex("/w", "/w/a/b/c.txt", "**/*.tmp"));
        Assert.True(Ex("/w", "/w/out/x", "out/**"));
        Assert.False(Ex("/w", "/w/anything"));
        Assert.True(new ExcludeMatcher("/w", []).IsEmpty);
        Assert.False(Ex("/w", "/w/x", "", "  "));            // blank patterns are ignored
    }
}

internal sealed class WatchFixture : IDisposable
{
    public TempDirectory Temp { get; } = new("cscode-watch-");
    public IoLane Lane { get; } = new("test.watch", 3);
    public CapturingLogger<FileWatcher> Log { get; } = new();
    public PlatformOptions Options { get; } = new()
    {
        WatchDebounce = TimeSpan.FromMilliseconds(40),
        WatchMaxDelay = TimeSpan.FromMilliseconds(400),
        PollInterval = TimeSpan.FromMilliseconds(50),
    };
    public FileWatcher Watcher { get; }

    public WatchFixture(BackendFactory? factory = null, IConfigurationService? config = null)
    {
        Watcher = factory is null
            ? new FileWatcher(Lane, config, Options, Log)
            : new FileWatcher(Lane, null, Options, Log, factory);
    }

    public string P(string rel) => Temp.Combine(rel.Split('/'));
    public Uri Root => FileUri.FromDirectoryPath(Temp.Path);

    public void Dispose() { Watcher.Dispose(); Lane.Dispose(); Temp.Dispose(); }

    public (Collector Events, FileWatchHandle Handle) Start(bool recursive = true, IReadOnlyList<string>? excludes = null)
    {
        var c = new Collector();
        var h = (FileWatchHandle)Watcher.Watch(Root, recursive, excludes, c.Add);
        return (c, h);
    }

    public static int InotifyFds()
    {
        if (!OperatingSystem.IsLinux()) return 0;
        var n = 0;
        foreach (var fd in Directory.GetFiles("/proc/self/fd"))
        {
            try { if (new FileInfo(fd).LinkTarget?.Contains("inotify", StringComparison.Ordinal) == true) n++; }
            catch (IOException) { }
        }
        return n;
    }

    public static async Task<int> SettledInotifyFds(int expectedAtMost, CancellationToken ct)
    {
        var n = InotifyFds();
        for (var i = 0; i < 250 && n > expectedAtMost; i++) { await Task.Delay(20, ct); n = InotifyFds(); }
        return n;
    }
}

public sealed class Collector
{
    private readonly List<FileChange> _all = new();
    private readonly List<(int Count, long Tick, bool OnLane, bool OnPool)> _batches = new();
    public Func<bool>? ThrowOnce;

    public void Add(IReadOnlyList<FileChange> batch)
    {
        lock (_all)
        {
            _all.AddRange(batch);
            _batches.Add((batch.Count, Environment.TickCount64, IoLane.IsLaneThread, Thread.CurrentThread.IsThreadPoolThread));
        }
        if (ThrowOnce is { } t && t()) throw new InvalidOperationException("callback failure (expected by the test)");
    }

    public FileChange[] Snapshot() { lock (_all) return _all.ToArray(); }
    public (int Count, long Tick, bool OnLane, bool OnPool)[] Batches() { lock (_all) return _batches.ToArray(); }
    public void Clear() { lock (_all) { _all.Clear(); _batches.Clear(); } }

    public static string Name(FileChange c) => Path.GetFileName(FileUri.ToPath(c.Uri));

    public async Task WaitFor(Func<FileChange[], bool> predicate, string what, CancellationToken ct, int timeoutMs = 15_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate(Snapshot())) return;
            await Task.Delay(15, ct);
        }
        throw new TimeoutException($"Timed out waiting for {what}. Seen: " + string.Join(", ", Snapshot().Select(c => $"{c.Kind}:{Name(c)}")));
    }

    public Task WaitForKind(string name, FileChangeKind kind, CancellationToken ct) =>
        WaitFor(s => s.Any(c => c.Kind == kind && Name(c) == name), $"{kind} {name}", ct);
}

/// <summary>Runs alone: the handle-accounting tests count process-wide inotify fds, and the timing tests are sensitive to CPU contention.</summary>
[CollectionDefinition("Watchers", DisableParallelization = true)]
public sealed class WatchersCollection;

[Collection("Watchers")]
public class FileWatcherTests : IDisposable
{
    private readonly WatchFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- basic events ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reports_create_change_and_delete()
    {
        var (events, h) = _f.Start();
        await h.Ready;

        File.WriteAllText(_f.P("a.txt"), "1");
        await events.WaitForKind("a.txt", FileChangeKind.Created, Ct);

        await Task.Delay(150, Ct); // let the burst settle so the modification is a separate batch
        events.Clear();
        File.AppendAllText(_f.P("a.txt"), "2");
        await events.WaitForKind("a.txt", FileChangeKind.Changed, Ct);

        await Task.Delay(150, Ct);
        events.Clear();
        File.Delete(_f.P("a.txt"));
        await events.WaitForKind("a.txt", FileChangeKind.Deleted, Ct);
        h.Dispose();
    }

    [Fact]
    public async Task A_rename_arrives_as_delete_plus_create()
    {
        File.WriteAllText(_f.P("old.txt"), "x");
        var (events, h) = _f.Start();
        await h.Ready;
        File.Move(_f.P("old.txt"), _f.P("new.txt"));
        await events.WaitFor(s => s.Any(c => c.Kind == FileChangeKind.Deleted && Collector.Name(c) == "old.txt") && s.Any(c => c.Kind == FileChangeKind.Created && Collector.Name(c) == "new.txt"), "rename", Ct);
        h.Dispose();
    }

    [Fact]
    public async Task A_file_created_and_removed_inside_one_window_is_never_reported()
    {
        _f.Options.WatchDebounce = TimeSpan.FromMilliseconds(400); // wide window: the OS may deliver the two events a little apart under load
        _f.Options.WatchMaxDelay = TimeSpan.FromSeconds(5);
        var (events, h) = _f.Start();
        await h.Ready;
        File.WriteAllText(_f.P("temp.lock"), "x");
        File.Delete(_f.P("temp.lock"));
        File.WriteAllText(_f.P("marker.txt"), "x");
        await events.WaitForKind("marker.txt", FileChangeKind.Created, Ct);
        await Task.Delay(200, Ct);
        Assert.DoesNotContain(events.Snapshot(), c => Collector.Name(c) == "temp.lock");
        h.Dispose();
    }

    [Fact]
    public async Task A_recursive_watch_sees_new_subdirectories_and_files_created_in_them()
    {
        var (events, h) = _f.Start(recursive: true);
        await h.Ready;
        Directory.CreateDirectory(_f.P("sub/deep"));
        await events.WaitForKind("deep", FileChangeKind.Created, Ct);
        await Task.Delay(200, Ct); // the new directories get their own OS watches
        File.WriteAllText(_f.P("sub/deep/inner.txt"), "x");
        await events.WaitForKind("inner.txt", FileChangeKind.Created, Ct);
        h.Dispose();
    }

    [Fact]
    public async Task Content_that_appears_with_a_new_directory_is_reported_even_though_the_os_watch_arms_late()
    {
        // `mkdir -p` + immediate writes, or a whole tree moved in: the OS reports only the top directory; the watcher must look inside.
        var (events, h) = _f.Start(recursive: true);
        await h.Ready;
        Directory.CreateDirectory(_f.P("staging/a/b"));
        File.WriteAllText(_f.P("staging/a/b/early.txt"), "x");
        File.WriteAllText(_f.P("staging/top.txt"), "x");
        await events.WaitFor(s => s.Any(c => Collector.Name(c) == "early.txt") && s.Any(c => Collector.Name(c) == "top.txt") && s.Any(c => Collector.Name(c) == "b"), "tree contents", Ct);

        // a whole pre-built tree renamed into the watched root (built OUTSIDE it, so nothing was reported while it was being made)
        using var outside = new TempDirectory("cscode-outside-");
        outside.WriteFile("t/u/moved.txt", "x");
        Directory.Move(outside.Combine("t"), _f.P("t-moved"));
        await events.WaitFor(s => s.Any(c => Collector.Name(c) == "moved.txt"), "moved tree contents", Ct);
        h.Dispose();
    }

    [Fact]
    public async Task New_directories_are_not_scanned_into_excluded_subtrees()
    {
        var (events, h) = _f.Start(recursive: true);
        await h.Ready;
        Directory.CreateDirectory(_f.P("fresh/node_modules/pkg"));
        File.WriteAllText(_f.P("fresh/node_modules/pkg/index.js"), "x");
        File.WriteAllText(_f.P("fresh/src.cs"), "x");
        await events.WaitForKind("src.cs", FileChangeKind.Created, Ct);
        await Task.Delay(250, Ct);
        Assert.DoesNotContain(events.Snapshot(), c => Collector.Name(c) == "index.js");
        h.Dispose();
    }

    [Fact]
    public async Task A_non_recursive_watch_ignores_what_happens_below_the_root()
    {
        Directory.CreateDirectory(_f.P("sub"));
        var (events, h) = _f.Start(recursive: false);
        await h.Ready;
        File.WriteAllText(_f.P("sub/hidden.txt"), "x");
        File.WriteAllText(_f.P("top.txt"), "x");
        await events.WaitForKind("top.txt", FileChangeKind.Created, Ct);
        await Task.Delay(200, Ct);
        Assert.DoesNotContain(events.Snapshot(), c => Collector.Name(c) == "hidden.txt");
        h.Dispose();
    }

    [Fact]
    public async Task Paths_with_spaces_and_unicode_arrive_as_correct_uris()
    {
        var (events, h) = _f.Start();
        await h.Ready;
        File.WriteAllText(_f.P("Ünï 100% #1.txt"), "x");
        await events.WaitForKind("Ünï 100% #1.txt", FileChangeKind.Created, Ct);
        Assert.Contains(events.Snapshot(), c => FileUri.ToPath(c.Uri) == _f.P("Ünï 100% #1.txt"));
        h.Dispose();
    }

    // ---- excludes ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Default_excludes_drop_node_modules_git_objects_bin_and_obj_but_not_ordinary_files()
    {
        Directory.CreateDirectory(_f.P("node_modules/pkg"));
        Directory.CreateDirectory(_f.P(".git/objects"));
        Directory.CreateDirectory(_f.P("bin"));
        Directory.CreateDirectory(_f.P("obj"));
        var (events, h) = _f.Start();
        await h.Ready;

        File.WriteAllText(_f.P("node_modules/pkg/index.js"), "x");
        File.WriteAllText(_f.P(".git/objects/blob"), "x");
        File.WriteAllText(_f.P("bin/app.dll"), "x");
        File.WriteAllText(_f.P("obj/cache.json"), "x");
        File.WriteAllText(_f.P(".git/HEAD"), "x");
        File.WriteAllText(_f.P("real.cs"), "x");

        await events.WaitFor(s => s.Any(c => Collector.Name(c) == "real.cs") && s.Any(c => Collector.Name(c) == "HEAD"), "ordinary files", Ct);
        await Task.Delay(250, Ct);
        var names = events.Snapshot().Select(Collector.Name).ToHashSet();
        Assert.DoesNotContain("index.js", names);
        Assert.DoesNotContain("blob", names);
        Assert.DoesNotContain("app.dll", names);
        Assert.DoesNotContain("cache.json", names);
        h.Dispose();
    }

    [Fact]
    public async Task An_explicit_exclude_list_replaces_the_defaults_and_an_empty_list_excludes_nothing()
    {
        Directory.CreateDirectory(_f.P("node_modules"));
        var (events, h) = _f.Start(excludes: ["**/*.tmp"]);
        await h.Ready;
        File.WriteAllText(_f.P("scratch.tmp"), "x");
        File.WriteAllText(_f.P("node_modules/m.js"), "x");   // not excluded now: the defaults are replaced
        await events.WaitForKind("m.js", FileChangeKind.Created, Ct);
        await Task.Delay(200, Ct);
        Assert.DoesNotContain(events.Snapshot(), c => Collector.Name(c) == "scratch.tmp");
        h.Dispose();

        var (all, h2) = _f.Start(excludes: []);
        await h2.Ready;
        File.WriteAllText(_f.P("x.tmp"), "x");
        await all.WaitForKind("x.tmp", FileChangeKind.Created, Ct);
        h2.Dispose();
    }

    [Fact]
    public async Task The_watcherExclude_setting_supplies_the_default_and_false_entries_do_not_exclude()
    {
        var cfg = new TestConfigurationService();
        cfg.Set(SettingKeys.FilesWatcherExclude, new Dictionary<string, bool> { ["**/skipme/**"] = true, ["**/node_modules/**"] = false });
        using var f = new WatchFixture(config: cfg);
        Directory.CreateDirectory(f.P("skipme"));
        Directory.CreateDirectory(f.P("node_modules"));
        var (events, h) = f.Start();
        await h.Ready;

        File.WriteAllText(f.P("skipme/a.txt"), "x");
        File.WriteAllText(f.P("node_modules/b.txt"), "x");
        await events.WaitForKind("b.txt", FileChangeKind.Created, Ct);
        await Task.Delay(200, Ct);
        Assert.DoesNotContain(events.Snapshot(), c => Collector.Name(c) == "a.txt");
        h.Dispose();
    }

    // ---- debounce / batching / threading -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_burst_is_coalesced_into_few_batches_delivered_on_io_lane_threads()
    {
        var (events, h) = _f.Start();
        await h.Ready;
        for (var i = 0; i < 200; i++) File.WriteAllText(_f.P($"f{i:D3}.txt"), "x");
        await events.WaitFor(s => s.Select(Collector.Name).Distinct().Count(n => n.StartsWith('f')) >= 200, "all 200 files", Ct);

        var batches = events.Batches();
        Assert.True(batches.Length <= 20, $"{batches.Length} batches for a 200-file burst");
        Assert.All(batches, b => { Assert.True(b.OnLane); Assert.False(b.OnPool); });
        // each file is reported as Created exactly once (the follow-up Changed events merged into it)
        Assert.All(events.Snapshot().GroupBy(Collector.Name), g => Assert.Equal(FileChangeKind.Created, g.First().Kind));
        h.Dispose();
    }

    [Fact]
    public async Task Batches_are_capped_at_one_thousand_changes()
    {
        var (events, h) = _f.Start();
        await h.Ready;
        Parallel.For(0, 2500, i => File.WriteAllBytes(_f.P($"g{i:D4}"), []));
        await events.WaitFor(s => s.Select(Collector.Name).Distinct().Count() >= 2500, "2500 files", Ct, timeoutMs: 60_000);
        Assert.All(events.Batches(), b => Assert.True(b.Count <= FileWatcher.MaxBatch, $"batch of {b.Count}"));
        h.Dispose();
    }

    [Fact]
    public async Task Continuous_activity_still_delivers_within_the_max_delay()
    {
        _f.Options.WatchDebounce = TimeSpan.FromMilliseconds(300);   // longer than the gaps between writes: debounce alone would never fire
        _f.Options.WatchMaxDelay = TimeSpan.FromMilliseconds(500);
        var (events, h) = _f.Start();
        await h.Ready;

        var startTick = Environment.TickCount64;
        var i = 0;
        while (Environment.TickCount64 - startTick < 2200)
        {
            File.WriteAllText(_f.P("busy.log"), "line " + i++);
            await Task.Delay(40, Ct);
        }
        // Writes came every ~40 ms, so the 300 ms quiet period never elapsed: only the max-delay rule can have produced these batches.
        var duringWrites = events.Batches().Count(b => b.Tick - startTick <= 2200);
        Assert.True(duringWrites >= 2, $"only {duringWrites} batch(es) while writes were still running");
        h.Dispose();
    }

    [Fact]
    public async Task A_throwing_callback_does_not_stop_later_deliveries()
    {
        var events = new Collector();
        var first = true;
        events.ThrowOnce = () => { var t = first; first = false; return t; };
        var h = (FileWatchHandle)_f.Watcher.Watch(_f.Root, true, null, events.Add);
        await h.Ready;

        File.WriteAllText(_f.P("one.txt"), "x");
        await events.WaitForKind("one.txt", FileChangeKind.Created, Ct);
        await Task.Delay(150, Ct);
        File.WriteAllText(_f.P("two.txt"), "x");
        await events.WaitForKind("two.txt", FileChangeKind.Created, Ct);
        Assert.Contains(_f.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("threw"));
        h.Dispose();
    }

    [Fact]
    public async Task Watch_and_dispose_return_immediately_even_for_a_big_tree()
    {
        for (var i = 0; i < 1500; i++) Directory.CreateDirectory(_f.P($"d{i}/inner"));

        var sw = Stopwatch.StartNew();
        var h = (FileWatchHandle)_f.Watcher.Watch(_f.Root, true, null, _ => { });
        var watchMs = sw.Elapsed.TotalMilliseconds;
        Assert.True(watchMs < 50, $"Watch() blocked the caller for {watchMs:F1} ms");

        await h.Ready;                       // arming (the inotify tree walk) happened on the lane
        sw.Restart();
        h.Dispose();
        Assert.True(sw.Elapsed.TotalMilliseconds < 50, $"Dispose() blocked the caller for {sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void Other_schemes_are_rejected()
    {
        Assert.Throws<NotSupportedException>(() => _f.Watcher.Watch(new Uri("ssh://host/x"), true, null, _ => { }));
    }

    // ---- roots that do not exist (yet), or stop existing -------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_directory_is_watched_for_its_creation_and_what_appears_in_it_is_reported()
    {
        var events = new Collector();
        var sw = Stopwatch.StartNew();
        var h = (FileWatchHandle)_f.Watcher.Watch(FileUri.FromDirectoryPath(_f.P("not/there")), true, null, events.Add);
        Assert.True(sw.ElapsedMilliseconds < 50, $"Watch() blocked the caller for {sw.ElapsedMilliseconds} ms");
        await h.Ready;                                                       // armed: its creation will be noticed
        Assert.DoesNotContain(_f.Log.Entries, e => e.Level >= LogLevel.Warning);

        // `mkdir -p` + an immediate write: both happen before any OS watch on the new directories can exist
        Directory.CreateDirectory(_f.P("not/there/sub"));
        File.WriteAllText(_f.P("not/there/sub/early.txt"), "x");
        await events.WaitForKind("early.txt", FileChangeKind.Created, Ct);

        await Task.Delay(150, Ct);
        File.WriteAllText(_f.P("not/there/later.txt"), "x");               // from then on it is an ordinary watch
        File.WriteAllText(_f.P("not/sibling.txt"), "x");                   // and nothing outside its root is reported
        await events.WaitForKind("later.txt", FileChangeKind.Created, Ct);
        await Task.Delay(150, Ct);
        Assert.All(events.Snapshot(), c => Assert.StartsWith(_f.P("not/there") + "/", FileUri.ToPath(c.Uri), StringComparison.Ordinal));
        h.Dispose();
    }

    [Fact]
    public async Task Every_subscriber_of_a_directory_created_after_the_first_watch_gets_its_events()
    {
        // Two modules watch the same directory before it exists (DAP's launch.json watch and the settings service both watch .cscode/):
        // the later one must not join a dead watch and the earlier one must not stay deaf.
        var dir = FileUri.FromDirectoryPath(_f.P(".cscode"));
        var first = new Collector();
        var h1 = (FileWatchHandle)_f.Watcher.Watch(dir, false, [], first.Add);
        await h1.Ready;
        Directory.CreateDirectory(_f.P(".cscode"));
        await Task.Delay(200, Ct);
        var second = new Collector();
        var h2 = (FileWatchHandle)_f.Watcher.Watch(dir, false, [], second.Add);
        await h2.Ready;

        File.WriteAllText(_f.P(".cscode/settings.json"), "{}");
        await first.WaitForKind("settings.json", FileChangeKind.Created, Ct);
        await second.WaitForKind("settings.json", FileChangeKind.Created, Ct);
        h1.Dispose();
        h2.Dispose();
    }

    [Fact]
    public async Task A_watched_directory_that_is_deleted_and_created_again_keeps_reporting()
    {
        Directory.CreateDirectory(_f.P("cfg"));
        File.WriteAllText(_f.P("cfg/a.json"), "x");
        var events = new Collector();
        var h = (FileWatchHandle)_f.Watcher.Watch(FileUri.FromDirectoryPath(_f.P("cfg")), false, [], events.Add);
        await h.Ready;

        Directory.Delete(_f.P("cfg"), recursive: true);                    // the OS watch dies with its directory, silently on Linux
        await events.WaitForKind("a.json", FileChangeKind.Deleted, Ct);
        await Task.Delay(200, Ct);
        Directory.CreateDirectory(_f.P("cfg"));
        File.WriteAllText(_f.P("cfg/b.json"), "x");
        await events.WaitForKind("b.json", FileChangeKind.Created, Ct);
        await Task.Delay(150, Ct);
        File.WriteAllText(_f.P("cfg/c.json"), "x");
        await events.WaitForKind("c.json", FileChangeKind.Created, Ct);
        h.Dispose();
    }

    [Fact]
    public async Task A_directory_replaced_in_an_instant_under_a_watched_parent_is_watched_again()
    {
        // `git checkout` / `rm -rf .vscode && mkdir .vscode`: too quick for any later existence check to see a gap. The parent's watch
        // (the settings service watches the workspace folder) reports the re-creation, which the directory's own dead watch cannot.
        Directory.CreateDirectory(_f.P("cfg"));
        File.WriteAllText(_f.P("cfg/a.json"), "x");
        var (_, parent) = _f.Start(recursive: false);
        var events = new Collector();
        var h = (FileWatchHandle)_f.Watcher.Watch(FileUri.FromDirectoryPath(_f.P("cfg")), false, [], events.Add);
        await Task.WhenAll(parent.Ready, h.Ready);

        Directory.Delete(_f.P("cfg"), recursive: true);
        Directory.CreateDirectory(_f.P("cfg"));
        File.WriteAllText(_f.P("cfg/b.json"), "x");
        await events.WaitForKind("b.json", FileChangeKind.Created, Ct);
        await Task.Delay(150, Ct);
        File.WriteAllText(_f.P("cfg/c.json"), "x");
        await events.WaitForKind("c.json", FileChangeKind.Created, Ct);
        h.Dispose();
        parent.Dispose();
    }

    [Fact]
    public async Task Rewatching_a_directory_that_was_moved_away_watches_whatever_is_at_that_path_now()
    {
        Directory.CreateDirectory(_f.P("cfg"));
        var first = new Collector();
        var h1 = (FileWatchHandle)_f.Watcher.Watch(FileUri.FromDirectoryPath(_f.P("cfg")), false, [], first.Add);
        await h1.Ready;
        Directory.Move(_f.P("cfg"), _f.P("cfg.bak"));                       // nothing reaches a watch on the directory itself
        var second = new Collector();                                       // a module re-watching after its parent watch reported the move
        var h2 = (FileWatchHandle)_f.Watcher.Watch(FileUri.FromDirectoryPath(_f.P("cfg")), false, [], second.Add);
        await h2.Ready;

        Directory.CreateDirectory(_f.P("cfg"));
        File.WriteAllText(_f.P("cfg/new.json"), "x");
        File.WriteAllText(_f.P("cfg.bak/moved.json"), "x");
        await second.WaitForKind("new.json", FileChangeKind.Created, Ct);
        await first.WaitForKind("new.json", FileChangeKind.Created, Ct);
        await Task.Delay(200, Ct);
        Assert.DoesNotContain(first.Snapshot(), c => Collector.Name(c) == "moved.json"); // the moved directory is not reported under the old path
        h1.Dispose();
        h2.Dispose();
    }

    // On Linux a FileSystemWatcher whose directory was deleted keeps its inotify instance (and a thread) until the process exits, disposed
    // or not. Re-arming a fresh one after every deletion would use up the per-user instance limit (often 128, shared with every other
    // program) over a day of rebuilds or branch switches: a path is polled once it has lost MaxOsWatchLosses OS watches.

    private static Task WaitForPresent(Collector events, string name, CancellationToken ct) =>
        events.WaitFor(s => s.Any(c => Collector.Name(c) == name && c.Kind != FileChangeKind.Deleted), $"{name} to be reported", ct);

    [Fact]
    public async Task A_build_output_directory_deleted_and_rebuilt_again_and_again_keeps_reporting_and_costs_at_most_two_inotify_instances()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fd");
        var baseline = WatchFixture.InotifyFds();
        Directory.CreateDirectory(_f.P("out/gen"));
        File.WriteAllText(_f.P("out/gen/G0.cs"), "x");
        var events = new Collector();                                       // the editor watching the folder of an open generated file
        var h = (FileWatchHandle)_f.Watcher.Watch(FileUri.FromDirectoryPath(_f.P("out/gen")), false, null, events.Add);
        await h.Ready;

        const int Rebuilds = 10;
        for (var i = 1; i <= Rebuilds; i++)
        {
            Directory.Delete(_f.P("out"), recursive: true);                 // clean …
            await events.WaitForKind($"G{i - 1}.cs", FileChangeKind.Deleted, Ct);
            await Task.Delay(100, Ct);
            Directory.CreateDirectory(_f.P("out/gen"));                     // … and build
            File.WriteAllText(_f.P($"out/gen/G{i}.cs"), "x");
            await WaitForPresent(events, $"G{i}.cs", Ct);
        }
        h.Dispose();

        var leaked = await WatchFixture.SettledInotifyFds(baseline + FileWatcher.MaxOsWatchLosses, Ct) - baseline;
        Assert.True(leaked <= FileWatcher.MaxOsWatchLosses, $"{leaked} inotify instances leaked over {Rebuilds} rebuilds");
        Assert.Single(_f.Log.Entries, e => e.Message.Contains("polled every", StringComparison.Ordinal));
        Assert.DoesNotContain(_f.Log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_settings_directory_replaced_again_and_again_under_a_watched_parent_keeps_reporting_and_costs_at_most_two_inotify_instances()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fd");
        var baseline = WatchFixture.InotifyFds();
        Directory.CreateDirectory(_f.P(".vscode"));
        var (_, parent) = _f.Start(recursive: false);                       // the settings service watches the folder …
        var events = new Collector();                                       // … and the settings directory
        var h = (FileWatchHandle)_f.Watcher.Watch(FileUri.FromDirectoryPath(_f.P(".vscode")), false, [], events.Add);
        await Task.WhenAll(parent.Ready, h.Ready);

        const int Switches = 10;
        for (var i = 1; i <= Switches; i++)
        {
            Directory.Delete(_f.P(".vscode"), recursive: true);             // `git checkout` of a branch without it, and back: in an instant
            Directory.CreateDirectory(_f.P(".vscode"));
            File.WriteAllText(_f.P($".vscode/settings{i}.json"), "{}");
            await WaitForPresent(events, $"settings{i}.json", Ct);
        }
        File.WriteAllText(_f.P(".vscode/edited.json"), "{}");               // and it is still watched afterwards
        await events.WaitForKind("edited.json", FileChangeKind.Created, Ct);
        h.Dispose();
        parent.Dispose();

        var leaked = await WatchFixture.SettledInotifyFds(baseline + FileWatcher.MaxOsWatchLosses, Ct) - baseline;
        Assert.True(leaked <= FileWatcher.MaxOsWatchLosses, $"{leaked} inotify instances leaked over {Switches} replacements");
        Assert.Equal(0, _f.Watcher.ActiveBackends);
    }

    [Fact]
    public async Task A_path_watched_again_after_its_watches_were_lost_is_polled_from_the_start()
    {
        // Modules drop their watch when the directory goes and watch the path again when it is back (the settings service does): the
        // losses count for the path, not for one watch, or each re-watch would cost a fresh inotify instance at the next deletion.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fd");
        var (folder, parent) = _f.Start(recursive: false);                  // the module's folder watch tells it when the directory goes
        await parent.Ready;
        for (var i = 0; i < FileWatcher.MaxOsWatchLosses; i++)
        {
            Directory.CreateDirectory(_f.P("cfg"));
            await folder.WaitForKind("cfg", FileChangeKind.Created, Ct);
            var h = (FileWatchHandle)_f.Watcher.Watch(FileUri.FromDirectoryPath(_f.P("cfg")), false, [], _ => { });
            await h.Ready;
            folder.Clear();
            Directory.Delete(_f.P("cfg"), recursive: true);
            await folder.WaitForKind("cfg", FileChangeKind.Deleted, Ct);
            h.Dispose();                                                    // the module drops the watch of a directory that went
            folder.Clear();
        }
        // (counted on the lane: by the re-check the parent's report triggered, or where the dropped OS watch is disposed)
        for (var i = 0; i < 250 && !_f.Log.Entries.Any(e => e.Message.Contains("polled every", StringComparison.Ordinal)); i++) await Task.Delay(20, Ct);
        Assert.Single(_f.Log.Entries, e => e.Message.Contains("polled every", StringComparison.Ordinal));

        var baseline = WatchFixture.InotifyFds();
        Directory.CreateDirectory(_f.P("cfg"));
        var events = new Collector();
        var again = (FileWatchHandle)_f.Watcher.Watch(FileUri.FromDirectoryPath(_f.P("cfg")), false, [], events.Add);
        await again.Ready;
        Assert.Equal(baseline, WatchFixture.InotifyFds());                 // no OS watch any more …
        File.WriteAllText(_f.P("cfg/right-after-ready.json"), "{}");       // … yet a change right after Ready is reported
        await events.WaitForKind("right-after-ready.json", FileChangeKind.Created, Ct);
        Directory.Delete(_f.P("cfg"), recursive: true);                    // and the path is followed through deletion and re-creation
        await events.WaitForKind("right-after-ready.json", FileChangeKind.Deleted, Ct);
        Directory.CreateDirectory(_f.P("cfg"));
        File.WriteAllText(_f.P("cfg/back.json"), "{}");
        await events.WaitForKind("back.json", FileChangeKind.Created, Ct);
        again.Dispose();
        parent.Dispose();
    }

    // ---- handle accounting -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Watches_waiting_for_their_directory_share_the_parents_os_watch_and_release_it_on_dispose()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fd");
        var baseline = WatchFixture.InotifyFds();
        var (_, hr) = _f.Start(recursive: false);                           // e.g. the settings service watching the workspace folder
        var a = (FileWatchHandle)_f.Watcher.Watch(FileUri.FromDirectoryPath(_f.P(".cscode")), false, null, _ => { });
        var b = (FileWatchHandle)_f.Watcher.Watch(FileUri.FromDirectoryPath(_f.P(".vscode")), false, null, _ => { });
        await Task.WhenAll(hr.Ready, a.Ready, b.Ready);
        Assert.Equal(baseline + 1, WatchFixture.InotifyFds());             // both wait through the folder's existing OS watch
        Assert.Equal(1, _f.Watcher.ActiveBackends);

        a.Dispose();
        b.Dispose();
        hr.Dispose();
        Assert.True(await WatchFixture.SettledInotifyFds(baseline, Ct) <= baseline, "inotify handle leaked");
        Assert.Equal(0, _f.Watcher.ActiveBackends);
    }

    [Fact]
    public void A_disposed_watch_on_a_missing_directory_and_its_callback_are_not_retained()
    {
        LeakAssert.NotRetained(() =>
        {
            var target = new object();
            var h = (FileWatchHandle)_f.Watcher.Watch(FileUri.FromDirectoryPath(_f.P("missing/dir")), true, null, _ => GC.KeepAlive(target));
            h.Ready.GetAwaiter().GetResult();
            h.Dispose();
            return [target, h];
        });
        Assert.Equal(0, _f.Watcher.ActiveBackends);
    }

    [Fact]
    public async Task Disposing_the_subscription_stops_callbacks_and_releases_the_inotify_handle()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fd");
        var baseline = WatchFixture.InotifyFds();
        var (events, h) = _f.Start();
        await h.Ready;
        Assert.Equal(baseline + 1, WatchFixture.InotifyFds());

        h.Dispose();
        Assert.True(await WatchFixture.SettledInotifyFds(baseline, Ct) <= baseline, "inotify handle leaked after Dispose");

        File.WriteAllText(_f.P("after.txt"), "x");
        await Task.Delay(300, Ct);
        Assert.Empty(events.Snapshot());
    }

    [Fact]
    public async Task Subscriptions_on_the_same_root_share_one_os_watcher()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fd");
        var baseline = WatchFixture.InotifyFds();
        var (a, ha) = _f.Start();
        var (b, hb) = _f.Start();
        var (c, hc) = _f.Start(excludes: []);
        await Task.WhenAll(ha.Ready, hb.Ready, hc.Ready);
        Assert.Equal(baseline + 1, WatchFixture.InotifyFds());
        Assert.Equal(1, _f.Watcher.ActiveBackends);

        File.WriteAllText(_f.P("shared.txt"), "x");
        await a.WaitForKind("shared.txt", FileChangeKind.Created, Ct);
        await b.WaitForKind("shared.txt", FileChangeKind.Created, Ct);
        await c.WaitForKind("shared.txt", FileChangeKind.Created, Ct);

        ha.Dispose();                                       // the others keep working
        await Task.Delay(150, Ct);
        b.Clear();
        File.WriteAllText(_f.P("still.txt"), "x");
        await b.WaitForKind("still.txt", FileChangeKind.Created, Ct);

        hb.Dispose();
        hc.Dispose();
        Assert.True(await WatchFixture.SettledInotifyFds(baseline, Ct) <= baseline);
        Assert.Equal(0, _f.Watcher.ActiveBackends);
    }

    [Fact]
    public async Task Repeated_watch_and_dispose_cycles_do_not_leak_handles()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fd");
        var baseline = WatchFixture.InotifyFds();
        for (var i = 0; i < 40; i++)
        {
            var h = (FileWatchHandle)_f.Watcher.Watch(_f.Root, true, null, _ => { });
            await h.Ready;
            File.WriteAllText(_f.P("c.txt"), i.ToString());
            h.Dispose();
        }
        Assert.True(await WatchFixture.SettledInotifyFds(baseline, Ct) <= baseline, "inotify handles leaked across cycles");
        // (Thread counts are deliberately not asserted: other test classes run in parallel in this process, so the total is noisy.)
    }

    [Fact]
    public async Task Disposing_the_watcher_itself_closes_every_subscription()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fd");
        var baseline = WatchFixture.InotifyFds();
        var (events, h) = _f.Start();
        await h.Ready;
        _f.Watcher.Dispose();
        Assert.True(await WatchFixture.SettledInotifyFds(baseline, Ct) <= baseline);
        File.WriteAllText(_f.P("late.txt"), "x");
        await Task.Delay(250, Ct);
        Assert.Empty(events.Snapshot());
    }

    [Fact]
    public void A_disposed_subscription_and_its_callback_are_not_retained()
    {
        LeakAssert.NotRetained(() =>
        {
            var target = new object();
            var h = (FileWatchHandle)_f.Watcher.Watch(_f.Root, true, null, _ => GC.KeepAlive(target));
            h.Ready.GetAwaiter().GetResult();
            h.Dispose();
            return [target, h];
        });
        Assert.Equal(0, _f.Watcher.ActiveBackends);
    }

    // ---- polling fallback --------------------------------------------------------------------------------------------------------

    private static IOException LimitReached() => new("The configured user limit (8192) on the number of inotify watches has been reached.");

    [Fact]
    public async Task When_the_os_watch_limit_is_exhausted_the_root_falls_back_to_polling_and_logs_once()
    {
        using var f = new WatchFixture(factory: (_, _, _, _) => throw LimitReached());
        Directory.CreateDirectory(f.P("node_modules"));
        File.WriteAllText(f.P("existing.txt"), "x");
        var (events, h) = f.Start();
        var (events2, h2) = f.Start(excludes: []);       // second subscriber on the SAME root: no second warning
        await h.Ready;
        await Task.Delay(300, Ct);                       // baseline scan

        File.WriteAllText(f.P("polled.txt"), "x");
        await events.WaitForKind("polled.txt", FileChangeKind.Created, Ct);
        await events2.WaitForKind("polled.txt", FileChangeKind.Created, Ct);

        File.WriteAllText(f.P("existing.txt"), "changed content that is longer");
        await events.WaitForKind("existing.txt", FileChangeKind.Changed, Ct);
        File.Delete(f.P("polled.txt"));
        await events.WaitForKind("polled.txt", FileChangeKind.Deleted, Ct);

        var warnings = f.Log.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("falling back to polling")).ToArray();
        Assert.Single(warnings);
        h.Dispose();
        h2.Dispose();
    }

    [Fact]
    public async Task Polling_prunes_the_default_excluded_directories()
    {
        using var f = new WatchFixture(factory: (_, _, _, _) => throw LimitReached());
        Directory.CreateDirectory(f.P("node_modules/pkg"));
        var (events, h) = f.Start(excludes: []);          // subscriber excludes nothing, but polling never walks node_modules at all
        await h.Ready;
        await Task.Delay(300, Ct);
        File.WriteAllText(f.P("node_modules/pkg/x.js"), "x");
        File.WriteAllText(f.P("visible.txt"), "x");
        await events.WaitForKind("visible.txt", FileChangeKind.Created, Ct);
        await Task.Delay(200, Ct);
        Assert.DoesNotContain(events.Snapshot(), c => Collector.Name(c) == "x.js");
        h.Dispose();
    }

    [Fact]
    public async Task Each_root_that_degrades_logs_its_own_single_warning()
    {
        using var f = new WatchFixture(factory: (_, _, _, _) => throw LimitReached());
        var other = f.P("other");
        Directory.CreateDirectory(other);
        var h1 = (FileWatchHandle)f.Watcher.Watch(f.Root, true, null, _ => { });
        var h2 = (FileWatchHandle)f.Watcher.Watch(FileUri.FromDirectoryPath(other), true, null, _ => { });
        await Task.WhenAll(h1.Ready, h2.Ready);
        Assert.Equal(2, f.Log.Entries.Count(e => e.Message.Contains("falling back to polling")));
        h1.Dispose();
        h2.Dispose();
    }

    private sealed class FakeBackend : IWatchBackend
    {
        public string? Root;
        public RawSink? Sink;
        public Action<Exception>? Fatal;
        public Action? OnDispose;
        public volatile bool Disposed;
        public void Dispose()
        {
            Disposed = true;
            OnDispose?.Invoke();
        }
    }

    /// <summary>A fixture whose "OS watches" are fakes, added to <paramref name="backends"/> as created; <paramref name="onCreate"/> runs inside the factory.</summary>
    private static WatchFixture FakeFixture(List<FakeBackend> backends, Action<FakeBackend>? onCreate = null) =>
        new(factory: (root, _, sink, fatal) =>
        {
            var b = new FakeBackend { Root = root, Sink = sink, Fatal = fatal };
            lock (backends) backends.Add(b);
            onCreate?.Invoke(b);
            return b;
        });

    private static FakeBackend[] On(List<FakeBackend> backends, string root)
    {
        lock (backends) return backends.Where(b => b.Root == root).ToArray();
    }

    [Fact]
    public async Task A_failure_reported_by_a_replaced_os_watch_leaves_its_successor_alone()
    {
        var backends = new List<FakeBackend>();
        using var f = FakeFixture(backends);
        Directory.CreateDirectory(f.P("cfg"));
        var (_, parent) = f.Start(recursive: false);
        var events = new Collector();
        var h = (FileWatchHandle)f.Watcher.Watch(FileUri.FromDirectoryPath(f.P("cfg")), false, [], events.Add);
        await Task.WhenAll(parent.Ready, h.Ready);
        var first = Assert.Single(On(backends, f.P("cfg")));

        // The parent reports the directory re-created: its watch is replaced …
        Assert.Single(On(backends, f.Temp.Path)).Sink!(RawKind.Created, f.P("cfg"), null);
        for (var i = 0; i < 250 && On(backends, f.P("cfg")).Length < 2; i++) await Task.Delay(20, Ct);
        var second = On(backends, f.P("cfg"))[1];
        Assert.True(first.Disposed);

        // … and only then does the old one report its failure: nothing may happen to the new one.
        first.Fatal!(new IOException("inotify limit hit while adding a subdirectory"));
        await Task.Delay(300, Ct);
        Assert.False(second.Disposed, "the stale failure report tore down the replacement watch");
        Assert.DoesNotContain(f.Log.Entries, e => e.Message.Contains("falling back to polling", StringComparison.Ordinal));
        second.Sink!(RawKind.Created, f.P("cfg/still-live.json"), null);
        await events.WaitForKind("still-live.json", FileChangeKind.Created, Ct);

        // The current one's failure still degrades the watch, once.
        second.Fatal!(new IOException("inotify limit hit"));
        for (var i = 0; i < 250 && !second.Disposed; i++) await Task.Delay(20, Ct);
        Assert.True(second.Disposed);
        for (var i = 0; i < 250 && f.Watcher.ActiveBackends < 2; i++) await Task.Delay(20, Ct); // the polling backend (with its baseline) is in place
        File.WriteAllText(f.P("cfg/polled.json"), "{}");
        await events.WaitForKind("polled.json", FileChangeKind.Created, Ct);
        Assert.Single(f.Log.Entries, e => e.Message.Contains("falling back to polling", StringComparison.Ordinal));
        h.Dispose();
        parent.Dispose();
    }

    [Fact]
    public async Task A_failure_reported_while_the_os_watch_is_being_armed_degrades_it_once_it_is_in_place()
    {
        // The failure is handled on another lane thread while the watch is still being put in place. It must neither be lost (the watch
        // would stay broken) nor install a polling backend that the arming then overwrites (that one would poll forever, unowned).
        var backends = new List<FakeBackend>();
        using var f = FakeFixture(backends, b =>
        {
            b.Fatal!(new IOException("inotify limit hit while adding a subdirectory"));
            Thread.Sleep(300);                                              // the failure report is processed before this watch is in place
        });
        var (events, h) = f.Start(recursive: true);
        await h.Ready;
        var fake = Assert.Single(backends);
        for (var i = 0; i < 250 && !fake.Disposed; i++) await Task.Delay(20, Ct);
        Assert.True(fake.Disposed, "the failed watch stayed in place");
        for (var i = 0; i < 250 && f.Watcher.ActiveBackends == 0; i++) await Task.Delay(20, Ct); // the polling backend (with its baseline) is in place

        File.WriteAllText(f.P("after.txt"), "x");
        await events.WaitForKind("after.txt", FileChangeKind.Created, Ct);
        Assert.Single(f.Log.Entries, e => e.Message.Contains("falling back to polling", StringComparison.Ordinal));
        Assert.Equal(1, f.Watcher.ActiveBackends);
        h.Dispose();
        Assert.Equal(0, f.Watcher.ActiveBackends);
    }

    [Fact]
    public async Task A_root_back_by_the_time_its_dead_watch_is_replaced_has_what_is_inside_reported()
    {
        // The re-check finds the root gone; before the new watch is armed it is back, with content nothing has reported yet.
        var created = 0;
        var backends = new List<FakeBackend>();
        using var f = FakeFixture(backends);
        Directory.CreateDirectory(f.P("cfg"));
        var events = new Collector();
        var h = (FileWatchHandle)f.Watcher.Watch(FileUri.FromDirectoryPath(f.P("cfg")), false, [], events.Add);
        await h.Ready;
        var dead = Assert.Single(backends);
        dead.OnDispose = () =>                                              // runs between the re-check's look and the new arming
        {
            if (Interlocked.Increment(ref created) != 1) return;
            Directory.CreateDirectory(f.P("cfg"));
            File.WriteAllText(f.P("cfg/inside.json"), "{}");
        };

        Directory.Delete(f.P("cfg"), recursive: true);
        dead.Sink!(RawKind.Deleted, f.P("cfg/old.json"), null);            // a deletion below the root: re-checked after the debounce
        await events.WaitForKind("inside.json", FileChangeKind.Created, Ct);
        Assert.Equal(2, backends.Count);
        h.Dispose();
    }

    [Fact]
    public async Task A_fatal_error_after_arming_degrades_to_polling()
    {
        var backends = new List<FakeBackend>();
        using var f = new WatchFixture(factory: (_, _, sink, fatal) => { var b = new FakeBackend { Sink = sink, Fatal = fatal }; lock (backends) backends.Add(b); return b; });
        var (events, h) = f.Start();
        await h.Ready;

        // The "OS watcher" works at first: raw events flow through the same coalescing/exclude pipeline
        backends[0].Sink!(RawKind.Created, f.P("raw.txt"), null);
        await events.WaitForKind("raw.txt", FileChangeKind.Created, Ct);

        backends[0].Fatal!(new IOException("inotify limit hit while adding a subdirectory"));
        for (var i = 0; i < 200 && !backends[0].Disposed; i++) await Task.Delay(20, Ct);
        Assert.True(backends[0].Disposed);

        await Task.Delay(300, Ct); // polling baseline
        File.WriteAllText(f.P("after-failure.txt"), "x");
        await events.WaitForKind("after-failure.txt", FileChangeKind.Created, Ct);
        Assert.Single(f.Log.Entries, e => e.Message.Contains("falling back to polling"));
        h.Dispose();
    }

    [Fact]
    public async Task A_raw_flood_beyond_the_bound_is_reported_as_one_root_changed_event()
    {
        FakeBackend? backend = null;
        using var f = new WatchFixture(factory: (_, _, sink, fatal) => backend = new FakeBackend { Sink = sink, Fatal = fatal });
        f.Options.WatchDebounce = TimeSpan.FromMilliseconds(200);
        f.Options.WatchMaxDelay = TimeSpan.FromSeconds(20);
        var (events, h) = f.Start(excludes: []);
        await h.Ready;

        for (var i = 0; i <= ChangeCoalescer.MaxPaths + 5; i++) backend!.Sink!(RawKind.Created, $"{f.Temp.Path}/f{i}", null);
        await events.WaitFor(s => s.Length >= 1, "overflow marker", Ct);
        await Task.Delay(300, Ct);

        var all = events.Snapshot();
        Assert.Single(all);
        Assert.Equal(FileChangeKind.Changed, all[0].Kind);
        Assert.Equal(f.Temp.Path, FileUri.ToPath(all[0].Uri).TrimEnd('/'));
        h.Dispose();
    }
}
