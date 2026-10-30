using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CsCode.Core.Platform;
using CsCode.Platform.Io;
using CsCode.Platform.Watching;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Platform.Tests;

/// <summary>
/// The Linux backend on its own (synthetic events, watch counts read from the kernel, fd and thread release, the walk running ahead of the
/// event queue, overflows) and through the <see cref="FileWatcher"/> (what a directory every subscription excludes costs, cache tags, wanted
/// paths, subscriptions that join with fewer excludes, roots that go). Runs with the other watcher tests, alone: it counts process-wide inotify
/// watches and fds. A backend made without its reader thread is driven by hand (<see cref="InotifyBackend.Pump"/>), so a test can let the
/// disk run ahead of the queue, or lose events, on purpose.
/// </summary>
[Collection("Watchers")]
public class InotifyBackendTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RawCollector
    {
        private readonly List<(RawKind Kind, string Path, string? Old)> _all = new();
        public void Add(RawKind kind, string path, string? old) { lock (_all) _all.Add((kind, path, old)); }
        public (RawKind Kind, string Path, string? Old)[] Snapshot() { lock (_all) return _all.ToArray(); }
        public void Clear() { lock (_all) _all.Clear(); }

        public async Task WaitFor(Func<(RawKind Kind, string Path, string? Old)[], bool> predicate, string what, int timeoutMs = 10_000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (predicate(Snapshot())) return;
                await Task.Delay(10, Ct);
            }
            throw new TimeoutException($"Timed out waiting for {what}. Seen: " + string.Join(", ", Snapshot().Select(e => $"{e.Kind}:{e.Path}<-{e.Old}")));
        }

        public Task WaitFor(RawKind kind, string path) => WaitFor(s => s.Any(e => e.Kind == kind && e.Path == path), $"{kind} {path}");

        public int RootLost;
        public int Fatal;
    }

    private static InotifyBackend Backend(string root, RawCollector raw, Func<string, bool>? isPruned = null, bool recursive = true, bool startReader = true,
        RawSink? sink = null) =>
        new(root, recursive, d => isPruned?.Invoke(d) == true ? WatchVerdict.Prune : WatchVerdict.Watch, sink ?? raw.Add,
            ex => { Interlocked.Increment(ref raw.Fatal); raw.Add(RawKind.Changed, "FATAL: " + ex.Message, null); },
            () => Interlocked.Increment(ref raw.RootLost), startReader);

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(10, Ct);
        }
    }

    // ---- what the kernel says ----------------------------------------------------------------------------------------------------

    private static bool IsInotify(string fdLink)
    {
        try { return new FileInfo(fdLink).LinkTarget?.Contains("inotify", StringComparison.Ordinal) == true; }
        catch (IOException) { return false; }
    }

    /// <summary>Watches held by one inotify fd: the kernel lists each as an "inotify wd:" line in /proc/self/fdinfo.</summary>
    private static int KernelWatches(int fd)
    {
        try { return File.ReadLines($"/proc/self/fdinfo/{fd}").Count(l => l.StartsWith("inotify wd:", StringComparison.Ordinal)); }
        catch (IOException) { return 0; }
    }

    /// <summary>Watches held by every inotify fd of this process.</summary>
    private static int ProcessWatches() =>
        Directory.GetFiles("/proc/self/fd").Where(IsInotify).Sum(fd => KernelWatches(int.Parse(Path.GetFileName(fd), System.Globalization.CultureInfo.InvariantCulture)));

    private static int EventFds() =>
        Directory.GetFiles("/proc/self/fd").Count(fd =>
        {
            try { return new FileInfo(fd).LinkTarget?.Contains("eventfd", StringComparison.Ordinal) == true; }
            catch (IOException) { return false; }
        });

    // ---- synthetic events ----------------------------------------------------------------------------------------------------------

    /// <summary>One inotify_event as the kernel lays it out: a 16-byte header, then the name NUL-terminated and padded to 16 bytes.</summary>
    private static byte[] Ev(int wd, uint mask, uint cookie = 0, string name = "")
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var len = name.Length == 0 ? 0 : (nameBytes.Length + 1 + 15) / 16 * 16;
        var bytes = new byte[16 + len];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), wd);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), mask);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), cookie);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), (uint)len);
        nameBytes.CopyTo(bytes, 16);
        return bytes;
    }

    private static byte[] Bytes(params byte[][] events) => events.SelectMany(e => e).ToArray();

    [Fact]
    public void Parse_reads_each_record_with_its_padded_name_and_leaves_out_a_truncated_one()
    {
        var buffer = Bytes(Ev(3, InotifyBackend.InCreate, 0, "a.txt"), Ev(-1, InotifyBackend.InQOverflow), Ev(4, InotifyBackend.InMovedTo, 77, "sixteen-chars-ab"),
            Ev(5, InotifyBackend.InDelete, 0, "Ünï 100% #1"));
        var events = new List<InotifyBackend.RawEvent>();
        Assert.Equal(buffer.Length, InotifyBackend.Parse(buffer, events));
        static InotifyBackend.RawEvent E(int wd, uint mask, uint cookie, string name) => new(wd, mask, cookie, name);
        Assert.Equal(
            [E(3, InotifyBackend.InCreate, 0, "a.txt"), E(-1, InotifyBackend.InQOverflow, 0, ""), E(4, InotifyBackend.InMovedTo, 77, "sixteen-chars-ab"),
             E(5, InotifyBackend.InDelete, 0, "Ünï 100% #1")],
            events);

        events.Clear();
        var cut = Bytes(Ev(3, InotifyBackend.InModify, 0, "whole"), Ev(3, InotifyBackend.InModify, 0, "cut-off"))[..^5];
        Assert.Equal(32, InotifyBackend.Parse(cut, events));
        Assert.Equal("whole", Assert.Single(events).Name);
    }

    [Fact]
    public void Synthetic_events_pair_renames_by_cookie_report_unpaired_moves_and_overflow_and_ignored_drops_the_watch()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        var raw = new RawCollector();
        using var b = Backend(t.Path, raw, startReader: false); // no reader to flush a pending FROM between the calls below
        var wd = b.RootWd;
        string P(string name) => Path.Join(t.Path, name);

        b.Process(Bytes(
            Ev(wd, InotifyBackend.InMovedFrom, 5, "old.txt"), Ev(wd, InotifyBackend.InMovedTo, 5, "new.txt"),   // a rename
            Ev(wd, InotifyBackend.InMovedFrom, 6, "gone.txt"), Ev(wd, InotifyBackend.InCreate, 0, "x.txt"),      // moved out, then something else
            Ev(wd, InotifyBackend.InMovedTo, 7, "came.txt"),                                                    // moved in
            Ev(wd, InotifyBackend.InModify, 0, "x.txt"), Ev(wd, InotifyBackend.InAttrib, 0, "touched.txt"), Ev(wd, InotifyBackend.InDelete, 0, "x.txt"),
            Ev(wd, InotifyBackend.InAttrib),                                                                     // the root itself: its parent's business
            Ev(wd, InotifyBackend.InMovedFrom, 8, "last.txt")), endOfBurst: false);                             // its TO may still come
        Assert.Equal(
            [(RawKind.Renamed, P("new.txt"), P("old.txt")), (RawKind.Deleted, P("gone.txt"), null), (RawKind.Created, P("x.txt"), null),
             (RawKind.Created, P("came.txt"), null), (RawKind.Changed, P("x.txt"), null), (RawKind.Changed, P("touched.txt"), null),
             (RawKind.Deleted, P("x.txt"), null)],
            raw.Snapshot());

        raw.Clear();
        b.Process(Bytes(Ev(wd, InotifyBackend.InMovedTo, 8, "last-renamed.txt")), endOfBurst: false);          // the TO arrives in the next read
        Assert.Equal([(RawKind.Renamed, P("last-renamed.txt"), P("last.txt"))], raw.Snapshot());

        raw.Clear();
        b.Process(Bytes(Ev(wd, InotifyBackend.InMovedFrom, 9, "out.txt")), endOfBurst: false);
        b.Process([], endOfBurst: true);                                                                      // … and here it does not
        Assert.Equal([(RawKind.Deleted, P("out.txt"), null)], raw.Snapshot());

        raw.Clear();
        b.Process(Bytes(Ev(-1, InotifyBackend.InQOverflow)), endOfBurst: false);
        Assert.Equal([(RawKind.Changed, t.Path, null)], raw.Snapshot());                                    // consumers rescan

        raw.Clear();
        b.Process(Bytes(Ev(wd, InotifyBackend.InIgnored), Ev(wd, InotifyBackend.InCreate, 0, "after.txt")), endOfBurst: true);
        Assert.Equal(0, b.WatchCount);                                                                       // the descriptor is forgotten
        Assert.Empty(raw.Snapshot());                                                                        // and its late events dropped

        b.Process(Bytes(Ev(-1, InotifyBackend.InQOverflow)), endOfBurst: false);
        Assert.Equal(1, b.WatchCount);                                                                       // the walk after an overflow re-adds it
    }

    // ---- what a watch costs ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_excluded_subtree_costs_no_watch_when_armed_nor_when_directories_appear_in_it_later()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        foreach (var d in new[] { "src/a", "target/debug/deps", "node_modules/pkg/lib" }) Directory.CreateDirectory(t.Combine(d.Split('/')));
        var raw = new RawCollector();
        var matcher = new ExcludeMatcher(t.Path, ["**/target/**", "**/node_modules/**"]);
        using var b = Backend(t.Path, raw, matcher.IsExcluded);

        Assert.Equal(3, b.WatchCount);                                                                       // the root, src, src/a
        Assert.Equal(3, KernelWatches(b.Fd));
        Assert.Equal([t.Combine("node_modules"), t.Combine("target")], b.PrunedDirectories);

        Directory.CreateDirectory(t.Combine("target", "release", "build", "x"));                           // cargo build
        Directory.CreateDirectory(t.Combine("crates", "core", "target", "debug"));                         // a nested crate's output
        await WaitUntil(() => b.PrunedDirectories.Contains(t.Combine("crates", "core", "target")), "the nested target/ to be skipped");
        File.WriteAllText(t.Combine("crates", "core", "lib.rs"), "");
        await raw.WaitFor(RawKind.Created, t.Combine("crates", "core", "lib.rs"));
        Assert.Equal(5, b.WatchCount);                                                                       // + crates, crates/core
        Assert.Equal(5, KernelWatches(b.Fd));

        File.WriteAllText(t.Combine("target", "release", "build", "x", "out.o"), "");
        File.WriteAllText(t.Combine("src", "a", "main.rs"), "");
        await raw.WaitFor(RawKind.Created, t.Combine("src", "a", "main.rs"));
        Assert.DoesNotContain(raw.Snapshot(), e => e.Path.Contains("out.o", StringComparison.Ordinal));
    }

    private const string CacheTag = "Signature: 8a477f597d28d172789f06886806bc55\n# This file is a cache directory tag created by cargo.\n";

    [Fact]
    public async Task A_tree_with_a_big_cache_tagged_target_directory_costs_only_its_wanted_directories_in_watches()
    {
        // The bug: one window on a Rust + JS monorepo held 75,892 inotify watches, 56,489 of them in target/. Through the FileWatcher with
        // the default excludes, the excluded and cache-tagged directories must cost nothing (a FileSystemWatcher put a watch on each of the 213
        // here) — a cache directory one watch on itself, for its tag — while a source folder that is merely called target/ is watched.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fdinfo");
        using var f = new WatchFixture();
        var wanted = new[] { "src/a", "src/b", "docs", ".git/refs", "src/target/sub" };                    // + the root, src, .git, src/target: 9
        foreach (var d in wanted) Directory.CreateDirectory(f.P(d));
        for (var i = 0; i < 200; i++) Directory.CreateDirectory(f.P($"target/debug/build/d{i:D3}"));     // 203 directories
        File.WriteAllText(f.P("target/CACHEDIR.TAG"), CacheTag);
        Directory.CreateDirectory(f.P("node_modules/pkg/lib"));                                             // 3
        Directory.CreateDirectory(f.P(".git/objects/ab"));                                                  // 2
        Directory.CreateDirectory(f.P("crates/x/target/debug/incremental"));                               // 2 wanted (crates, crates/x), 3 not
        File.WriteAllText(f.P("crates/x/target/CACHEDIR.TAG"), CacheTag);
        File.WriteAllText(f.P("target/debug/build/d000/out.o"), "");

        var baseline = ProcessWatches();
        var (events, h) = f.Start();                                                                         // null: files.watcherExclude, else the built-ins
        await h.Ready;
        var cost = ProcessWatches() - baseline;
        Assert.True(cost <= 13, $"{cost} inotify watches for a tree with 11 wanted directories and 2 cache directories");
        Assert.Equal(13, cost);

        File.WriteAllText(f.P("target/debug/build/d000/out.o"), "rebuilt");
        File.WriteAllText(f.P("crates/x/target/debug/incremental/q.bin"), "");
        File.WriteAllText(f.P("src/target/sub/kept.rs"), "");                                               // a source folder called target
        File.WriteAllText(f.P("src/b/lib.rs"), "");
        await events.WaitFor(s => s.Any(c => Collector.Name(c) == "lib.rs") && s.Any(c => Collector.Name(c) == "kept.rs"), "lib.rs and kept.rs", Ct);
        await Task.Delay(150, Ct);
        Assert.DoesNotContain(events.Snapshot(), c => Collector.Name(c) is "out.o" or "q.bin");

        Directory.Delete(f.P("target"), recursive: true);                                                   // cargo clean: the folder itself is reported
        await events.WaitForKind("target", FileChangeKind.Deleted, Ct);
        h.Dispose();
    }

    [Fact]
    public async Task Symlinked_directories_are_neither_watched_nor_entered()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        using var outside = new TempDirectory("cscode-outside-");
        Directory.CreateDirectory(outside.Combine("deep", "er"));
        Directory.CreateSymbolicLink(t.Combine("link"), outside.Path);
        var raw = new RawCollector();
        using var b = Backend(t.Path, raw);
        Assert.Equal(1, b.WatchCount);

        Directory.CreateSymbolicLink(t.Combine("link2"), outside.Path);                                     // one appearing later neither
        await raw.WaitFor(RawKind.Created, t.Combine("link2"));
        Assert.Equal(1, b.WatchCount);
    }

    // ---- directories that appear, move and go ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_directory_created_after_arming_is_watched_and_what_is_created_inside_it_is_reported()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        var raw = new RawCollector();
        // A new directory is reported only once it is watched (the FileWatcher's listing of it relies on that): a file written the instant
        // `fresh` is reported — on the reader thread, inside the sink — must be reported too.
        var fresh = t.Combine("fresh");
        using var b = Backend(t.Path, raw, sink: (kind, path, old) =>
        {
            raw.Add(kind, path, old);
            if (kind == RawKind.Created && path == fresh) File.WriteAllText(Path.Join(fresh, "inner.txt"), "x");
        });

        Directory.CreateDirectory(fresh);
        await raw.WaitFor(RawKind.Created, Path.Join(fresh, "inner.txt"));

        Directory.CreateDirectory(t.Combine("a", "b", "c"));                                                // mkdir -p: one event, three directories
        await WaitUntil(() => b.WatchCount == 5, "a, a/b and a/b/c to be watched");
        File.WriteAllText(t.Combine("a", "b", "c", "x.txt"), "x");
        await raw.WaitFor(RawKind.Created, t.Combine("a", "b", "c", "x.txt"));
        Assert.Equal(5, KernelWatches(b.Fd));

        Directory.Delete(t.Combine("a"), recursive: true);
        await raw.WaitFor(RawKind.Deleted, t.Combine("a"));
        await WaitUntil(() => b.WatchCount == 2, "the deleted directories' watches to go");
    }

    [Fact]
    public async Task A_rename_is_one_paired_event_and_a_renamed_directory_keeps_reporting_under_its_new_path()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        t.WriteFile("old.txt", "x");
        Directory.CreateDirectory(t.Combine("d1", "sub"));
        var raw = new RawCollector();
        using var b = Backend(t.Path, raw);

        File.Move(t.Combine("old.txt"), t.Combine("new.txt"));
        await raw.WaitFor(s => s.Any(e => e.Kind == RawKind.Renamed), "the rename");
        Assert.Equal([(RawKind.Renamed, t.Combine("new.txt"), t.Combine("old.txt"))], raw.Snapshot());

        raw.Clear();
        Directory.Move(t.Combine("d1"), t.Combine("d2"));
        await raw.WaitFor(s => s.Any(e => e.Kind == RawKind.Renamed), "the directory rename");
        Assert.Equal([(RawKind.Renamed, t.Combine("d2"), t.Combine("d1"))], raw.Snapshot());
        File.WriteAllText(t.Combine("d2", "sub", "f.txt"), "x");
        await raw.WaitFor(RawKind.Created, t.Combine("d2", "sub", "f.txt"));                               // the same watch, its new path
        Assert.Equal(3, b.WatchCount);
    }

    [Fact]
    public async Task A_directory_moved_in_is_watched_with_its_subtree_and_one_moved_out_stops_reporting_and_costing()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        using var outside = new TempDirectory("cscode-outside-");
        outside.WriteFile("tree/u/v/early.txt", "x");
        Directory.CreateDirectory(outside.Combine("tree", "target", "debug"));
        var raw = new RawCollector();
        var matcher = new ExcludeMatcher(t.Path, ["**/target/**"]);
        using var b = Backend(t.Path, raw, matcher.IsExcluded);

        Directory.Move(outside.Combine("tree"), t.Combine("tree"));
        await raw.WaitFor(RawKind.Created, t.Combine("tree"));                                              // an unpaired TO: created
        Assert.Equal(4, b.WatchCount);                                                                       // the root, tree, tree/u, tree/u/v
        Assert.Contains(t.Combine("tree", "target"), b.PrunedDirectories);
        File.WriteAllText(t.Combine("tree", "u", "v", "late.txt"), "x");
        await raw.WaitFor(RawKind.Created, t.Combine("tree", "u", "v", "late.txt"));

        Directory.Move(t.Combine("tree"), outside.Combine("back"));
        await raw.WaitFor(RawKind.Deleted, t.Combine("tree"));                                              // an unpaired FROM: deleted
        Assert.Equal(1, b.WatchCount);
        Assert.Empty(b.PrunedDirectories);
        raw.Clear();
        File.WriteAllText(outside.Combine("back", "u", "v", "elsewhere.txt"), "x");
        File.WriteAllText(t.Combine("marker.txt"), "x");
        await raw.WaitFor(RawKind.Created, t.Combine("marker.txt"));
        Assert.DoesNotContain(raw.Snapshot(), e => e.Path.Contains("elsewhere", StringComparison.Ordinal)); // not under the old path either
    }

    [Fact]
    public async Task A_root_moved_away_stops_reporting_what_happens_in_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        var root = t.Combine("root");
        Directory.CreateDirectory(Path.Join(root, "sub"));
        var raw = new RawCollector();
        using var b = Backend(root, raw);

        Directory.Move(root, t.Combine("moved"));
        await WaitUntil(() => b.WatchCount == 0, "the moved root's watches to be dropped");
        File.WriteAllText(t.Combine("moved", "sub", "x.txt"), "x");
        await Task.Delay(150, Ct);
        Assert.Empty(raw.Snapshot());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task After_a_burst_of_directory_churn_exactly_the_wanted_directories_are_watched_under_their_current_paths(int seed)
    {
        // Directories created, renamed, moved around, into and out of excluded ones, out of the tree and in again, and deleted — faster than
        // the reader follows, so its walks see the disk ahead of its event queue. Once it has caught up, the watches (ours and the kernel's)
        // are exactly the wanted directories, each under the path it has now.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        using var outside = new TempDirectory("cscode-outside-");
        var raw = new RawCollector();
        var matcher = new ExcludeMatcher(t.Path, ["**/target/**"]);
        using var b = Backend(t.Path, raw, matcher.IsExcluded);
        var rnd = new Random(seed);

        string[] Dirs() => Directory.GetDirectories(t.Path, "*", SearchOption.AllDirectories);
        string Pick(bool rootToo)
        {
            var all = Dirs();
            return rootToo && (all.Length == 0 || rnd.Next(4) == 0) ? t.Path : all.Length == 0 ? t.Path : all[rnd.Next(all.Length)];
        }

        for (var i = 0; i < 300; i++)
        {
            try
            {
                switch (rnd.Next(7))
                {
                    case 0 or 1:
                        {
                            var dir = Path.Join(Pick(rootToo: true), rnd.Next(8) == 0 ? "target" : $"d{i}");
                            Directory.CreateDirectory(rnd.Next(3) == 0 ? Path.Join(dir, $"e{i}") : dir);
                            break;
                        }
                    case 2:
                        {
                            var dir = Pick(rootToo: false);
                            if (dir != t.Path) Directory.Move(dir, Path.Join(Path.GetDirectoryName(dir)!, $"r{i}"));
                            break;
                        }
                    case 3:
                        {
                            var dir = Pick(rootToo: false);
                            var into = Pick(rootToo: true);
                            if (dir != t.Path && !into.StartsWith(dir + "/", StringComparison.Ordinal) && into != dir) Directory.Move(dir, Path.Join(into, $"m{i}"));
                            break;
                        }
                    case 4:
                        {
                            var dir = Pick(rootToo: false);
                            if (dir != t.Path) Directory.Delete(dir, recursive: true);
                            break;
                        }
                    case 5:
                        {
                            var dir = Pick(rootToo: false);
                            if (dir != t.Path) Directory.Move(dir, outside.Combine($"out{i}"));
                            break;
                        }
                    default:
                        Directory.CreateDirectory(outside.Combine($"in{i}", "x", rnd.Next(2) == 0 ? "target" : "y", "z"));
                        Directory.Move(outside.Combine($"in{i}"), Path.Join(Pick(rootToo: true), $"in{i}"));
                        break;
                }
            }
            catch (IOException) { } // a directory picked twice, or an existing name: the churn goes on
        }

        string[] Wanted() => [t.Path, .. Dirs().Where(d => !matcher.IsExcluded(d))];
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10_000 && !(b.WatchedDirectories.Order(StringComparer.Ordinal).SequenceEqual(Wanted().Order(StringComparer.Ordinal)) && KernelWatches(b.Fd) == b.WatchCount))
            await Task.Delay(20, Ct);
        Assert.Equal(Wanted().Order(StringComparer.Ordinal), b.WatchedDirectories.Order(StringComparer.Ordinal));
        Assert.Equal(b.WatchCount, KernelWatches(b.Fd));
        Assert.DoesNotContain(raw.Snapshot(), e => e.Path.StartsWith("FATAL", StringComparison.Ordinal));
    }

    // ---- release -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Dispose_closes_the_inotify_fd_and_the_eventfd_and_ends_the_reader_thread()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fd");
        using var t = new TempDirectory("cscode-inotify-");
        Directory.CreateDirectory(t.Combine("a", "b"));
        var inotifyBefore = WatchFixture.InotifyFds();
        var eventFdsBefore = EventFds();
        var b = Backend(t.Path, new RawCollector());
        Assert.Equal(inotifyBefore + 1, WatchFixture.InotifyFds());
        Assert.Equal(eventFdsBefore + 1, EventFds());
        Assert.True(b.ReaderThread!.IsAlive);

        var sw = Stopwatch.StartNew();
        b.Dispose();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Dispose took {sw.ElapsedMilliseconds} ms");
        Assert.False(b.ReaderThread.IsAlive);                                                                // it closed both fds on its way out
        Assert.True(WatchFixture.InotifyFds() <= inotifyBefore, "the inotify fd is still open");
        Assert.True(EventFds() <= eventFdsBefore, "the eventfd is still open");
        b.Dispose();                                                                                         // twice is harmless
    }

    [Fact]
    public async Task A_backend_whose_root_was_deleted_drops_its_watches_and_still_releases_its_fds_and_thread()
    {
        // A FileSystemWatcher in this state can never be released: its reader waits forever on an instance with no watch left.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fd");
        using var t = new TempDirectory("cscode-inotify-");
        var root = t.Combine("root");
        Directory.CreateDirectory(Path.Join(root, "a", "b"));
        var inotifyBefore = WatchFixture.InotifyFds();
        var eventFdsBefore = EventFds();
        var raw = new RawCollector();
        var b = Backend(root, raw);
        Assert.Equal(3, b.WatchCount);

        Directory.Delete(root, recursive: true);
        await WaitUntil(() => b.WatchCount == 0, "every watch to end with its directory");
        Assert.Equal(0, KernelWatches(b.Fd));
        Assert.True(b.ReaderThread!.IsAlive);

        b.Dispose();
        Assert.False(b.ReaderThread.IsAlive);
        Assert.True(WatchFixture.InotifyFds() <= inotifyBefore, "the inotify fd leaked");
        Assert.True(EventFds() <= eventFdsBefore, "the eventfd leaked");
        Assert.DoesNotContain(raw.Snapshot(), e => e.Path.StartsWith("FATAL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_watch_disposed_after_its_directory_was_deleted_releases_its_inotify_instance()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fd");
        using var f = new WatchFixture();
        Directory.CreateDirectory(f.P("empty"));
        var baseline = WatchFixture.InotifyFds();
        var h = (FileWatchHandle)f.Watcher.Watch(FileUri.FromDirectoryPath(f.P("empty")), false, [], _ => { });
        await h.Ready;
        Assert.Equal(baseline + 1, WatchFixture.InotifyFds());
        Directory.Delete(f.P("empty"));                                                                     // nothing inside, nothing watches the parent: unnoticed
        await Task.Delay(100, Ct);
        h.Dispose();
        Assert.True(await WatchFixture.SettledInotifyFds(baseline, Ct) <= baseline, "the inotify instance of a deleted directory leaked");
    }

    // ---- subscriptions with different excludes ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_subscription_that_joins_with_fewer_excludes_gets_the_directories_the_others_left_unwatched()
    {
        // The Explorer excludes .git (files.exclude) on top of files.watcherExclude; EditorConfig or a language server joining later does not.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fdinfo");
        using var f = new WatchFixture();
        Directory.CreateDirectory(f.P("gen/sub"));
        Directory.CreateDirectory(f.P("src"));
        var baseline = ProcessWatches();
        var (narrow, h1) = f.Start(excludes: ["**/gen/**"]);
        await h1.Ready;
        Assert.Equal(2, ProcessWatches() - baseline);                                                        // the root and src

        var (wide, h2) = f.Start(excludes: []);
        await h2.Ready;
        Assert.Equal(4, ProcessWatches() - baseline);                                                        // + gen, gen/sub: one OS watch, re-armed in place
        Assert.Equal(1, f.Watcher.ActiveBackends);

        File.WriteAllText(f.P("gen/sub/made.txt"), "x");
        await wide.WaitForKind("made.txt", FileChangeKind.Created, Ct);
        Directory.CreateDirectory(f.P("gen/sub/newer"));                                                    // and what appears there later
        await wide.WaitForKind("newer", FileChangeKind.Created, Ct);
        await Task.Delay(100, Ct);
        File.WriteAllText(f.P("gen/sub/newer/inner.txt"), "x");
        await wide.WaitForKind("inner.txt", FileChangeKind.Created, Ct);
        Assert.DoesNotContain(narrow.Snapshot(), c => FileUri.ToPath(c.Uri).Contains("/gen/", StringComparison.Ordinal));
        h1.Dispose();
        h2.Dispose();
    }

    [Fact]
    public async Task A_subscription_that_joins_while_the_tree_is_being_walked_still_gets_what_the_walk_skipped()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var walked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = 1;
        using var f = new WatchFixture(factory: (root, recursive, verdict, sink, fatal, rootLost) =>
        {
            var backend = FileWatcher.DefaultBackend(root, recursive, verdict, sink, fatal, rootLost);
            if (Interlocked.Exchange(ref first, 0) == 1)
            {
                walked.Set();                                                                                // walked (gen skipped), not yet in place
                release.Wait(TimeSpan.FromSeconds(10));
            }
            return backend;
        });
        Directory.CreateDirectory(f.P("gen/sub"));
        var (_, h1) = f.Start(excludes: ["**/gen/**"]);
        Assert.True(walked.Wait(TimeSpan.FromSeconds(10), Ct));
        var (wide, h2) = f.Start(excludes: []);                                                              // its re-check finds no backend yet …
        await Task.Delay(200, Ct);
        release.Set();                                                                                       // … so arming must revisit what it skipped
        await Task.WhenAll(h1.Ready, h2.Ready);

        File.WriteAllText(f.P("gen/sub/x.txt"), "x");
        await wide.WaitForKind("x.txt", FileChangeKind.Created, Ct);
        h1.Dispose();
        h2.Dispose();
    }

    // ---- the walk ahead of the queue, lost events ----------------------------------------------------------------------------------

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int olddirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldpath, int newdirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string newpath, uint flags);

    [Fact]
    public void A_name_reused_before_its_events_are_read_leaves_each_watch_under_the_path_of_its_own_directory()
    {
        // mkdir X; mv X Y; mkdir X — all before the backend reads one event, so its walk of the new X sees the second directory while the
        // queue still describes the first: the rename must not move the second directory's watch to Y.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        var raw = new RawCollector();
        using var b = Backend(t.Path, raw, startReader: false);
        Directory.CreateDirectory(t.Combine("X"));
        Directory.Move(t.Combine("X"), t.Combine("Y"));
        Directory.CreateDirectory(t.Combine("X"));
        b.Pump();
        Assert.Equal([t.Path, t.Combine("X"), t.Combine("Y")], b.WatchedDirectories.Order(StringComparer.Ordinal));
        Assert.Equal(b.WatchCount, KernelWatches(b.Fd));

        raw.Clear();
        File.WriteAllText(t.Combine("X", "in-x.txt"), "x");
        File.WriteAllText(t.Combine("Y", "in-y.txt"), "y");
        b.Pump();
        Assert.Contains(raw.Snapshot(), e => e.Kind == RawKind.Created && e.Path == t.Combine("X", "in-x.txt"));
        Assert.Contains(raw.Snapshot(), e => e.Kind == RawKind.Created && e.Path == t.Combine("Y", "in-y.txt"));
    }

    [Fact]
    public void Two_watched_directories_exchanged_keep_reporting_each_under_its_new_path()
    {
        // `mv --exchange src src-alt` (renameat2 RENAME_EXCHANGE): two renames whose targets are both watched.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        Directory.CreateDirectory(t.Combine("src", "inner-a"));
        Directory.CreateDirectory(t.Combine("src-alt", "inner-b"));
        var raw = new RawCollector();
        using var b = Backend(t.Path, raw, startReader: false);
        const int AtFdCwd = -100;
        const uint RenameExchange = 2;
        Assert.SkipWhen(RenameAt2(AtFdCwd, t.Combine("src"), AtFdCwd, t.Combine("src-alt"), RenameExchange) != 0, "the file system cannot exchange directories");
        b.Pump();
        Assert.Equal(new[] { t.Path, t.Combine("src"), t.Combine("src", "inner-b"), t.Combine("src-alt"), t.Combine("src-alt", "inner-a") }.Order(StringComparer.Ordinal),
            b.WatchedDirectories.Order(StringComparer.Ordinal));
        Assert.Equal(b.WatchCount, KernelWatches(b.Fd));

        raw.Clear();
        File.WriteAllText(t.Combine("src", "inner-b", "f.txt"), "x");
        File.WriteAllText(t.Combine("src-alt", "inner-a", "g.txt"), "x");
        b.Pump();
        Assert.Contains(raw.Snapshot(), e => e.Kind == RawKind.Created && e.Path == t.Combine("src", "inner-b", "f.txt"));
        Assert.Contains(raw.Snapshot(), e => e.Kind == RawKind.Created && e.Path == t.Combine("src-alt", "inner-a", "g.txt"));
    }

    [Fact]
    public void An_overflow_resync_removes_the_watches_of_directories_that_left_the_tree_while_events_were_lost()
    {
        // During a burst that overflows the queue, one directory is moved out of the tree (its watches stay alive in the kernel) and one deleted
        // (the kernel frees its watches; their IN_IGNORED is lost). The walk after the overflow must leave neither mapped under its old path.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        using var outside = new TempDirectory("cscode-outside-");
        Directory.CreateDirectory(t.Combine("a", "sub"));
        Directory.CreateDirectory(t.Combine("b", "sub"));
        Directory.CreateDirectory(t.Combine("c"));
        var raw = new RawCollector();
        using var b = Backend(t.Path, raw, startReader: false);
        Assert.Equal(6, b.WatchCount);
        Directory.Move(t.Combine("a"), outside.Combine("a"));
        Directory.Delete(t.Combine("b"), recursive: true);

        b.Process(Bytes(Ev(-1, InotifyBackend.InQOverflow)), endOfBurst: true); // all that arrives of the burst
        Assert.Equal([t.Path, t.Combine("c")], b.WatchedDirectories.Order(StringComparer.Ordinal));
        Assert.Equal(b.WatchCount, KernelWatches(b.Fd));
        Assert.Contains(raw.Snapshot(), e => e.Kind == RawKind.Changed && e.Path == t.Path);

        raw.Clear();
        File.WriteAllText(outside.Combine("a", "sub", "elsewhere.txt"), "x");
        File.WriteAllText(t.Combine("c", "marker.txt"), "x");
        b.Pump(); // (the events queued before the overflow come too: harmless)
        Assert.Contains(raw.Snapshot(), e => e.Path == t.Combine("c", "marker.txt"));
        Assert.DoesNotContain(raw.Snapshot(), e => e.Path.Contains("elsewhere", StringComparison.Ordinal));
    }

    [Fact]
    public void Running_out_of_watches_on_a_new_directory_still_reports_it_and_the_rest_of_the_read_then_a_rescan()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        var raw = new RawCollector();
        using var b = Backend(t.Path, raw, startReader: false);
        b.AddWatchFault = path => path.EndsWith("/boom", StringComparison.Ordinal) ? InotifyBackend.Enospc : 0;
        Directory.CreateDirectory(t.Combine("boom"));
        File.WriteAllText(t.Combine("after.txt"), "x");
        Directory.CreateDirectory(t.Combine("later"));
        b.Pump();

        var seen = raw.Snapshot().Where(e => !e.Path.StartsWith("FATAL", StringComparison.Ordinal)).ToArray();
        Assert.Contains((RawKind.Created, t.Combine("boom"), (string?)null), seen);
        Assert.Contains((RawKind.Created, t.Combine("after.txt"), (string?)null), seen);
        Assert.Contains((RawKind.Created, t.Combine("later"), (string?)null), seen);
        Assert.Equal((RawKind.Changed, t.Path, (string?)null), seen[^1]);                                  // what the missing watches would have said: rescan
        Assert.Equal(1, raw.Fatal);                                                                          // the FileWatcher replaces the backend
        Assert.Equal(1, b.WatchCount);                                                                       // and no watch is added after the failure
    }

    // ---- roots that go ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_root_moved_away_or_deleted_is_reported_lost()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        var moved = new RawCollector();
        Directory.CreateDirectory(t.Combine("one", "sub"));
        using var b1 = Backend(t.Combine("one"), moved);
        Directory.Move(t.Combine("one"), t.Combine("one-moved"));
        await WaitUntil(() => Volatile.Read(ref moved.RootLost) == 1, "the moved root to be reported lost");
        Assert.Equal(0, b1.WatchCount);

        var deleted = new RawCollector();
        Directory.CreateDirectory(t.Combine("two"));                                                        // empty: no event below it
        using var b2 = Backend(t.Combine("two"), deleted);
        Directory.Delete(t.Combine("two"));
        await WaitUntil(() => Volatile.Read(ref deleted.RootLost) == 1, "the deleted root to be reported lost");
    }

    [Fact]
    public async Task A_watched_folder_inside_an_unwatched_one_that_is_moved_away_and_replaced_is_watched_again()
    {
        // Go to definition opened node_modules/@types/node/fs.d.ts, so the editor watches that folder. `npm install` renames the package folder
        // away and extracts the new version in its place. Nothing watches node_modules (every subscription excludes it), so no parent reports
        // the swap: the folder's own watch must.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var f = new WatchFixture();
        Directory.CreateDirectory(f.P("node_modules/@types/node"));
        File.WriteAllText(f.P("node_modules/@types/node/fs.d.ts"), "old");
        var (_, workspace) = f.Start();
        var events = new Collector();
        var doc = (FileWatchHandle)f.Watcher.Watch(FileUri.FromDirectoryPath(f.P("node_modules/@types/node")), false, null, events.Add);
        await Task.WhenAll(workspace.Ready, doc.Ready);

        Directory.Move(f.P("node_modules/@types/node"), f.P("node_modules/@types/.node-old"));
        Directory.CreateDirectory(f.P("node_modules/@types/node"));
        File.WriteAllText(f.P("node_modules/@types/node/fs.d.ts"), "new");
        await events.WaitForPresent("fs.d.ts", Ct);                                                          // the new folder's content, announced
        await Task.Delay(150, Ct);
        File.WriteAllText(f.P("node_modules/@types/node/later.d.ts"), "x");                                // and watched from then on
        await events.WaitForKind("later.d.ts", FileChangeKind.Created, Ct);
        doc.Dispose();
        workspace.Dispose();
    }

    [Fact]
    public void A_backend_without_a_reader_closes_its_fds_on_dispose()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fd");
        using var t = new TempDirectory("cscode-inotify-");
        var inotifyBefore = WatchFixture.InotifyFds();
        var b = Backend(t.Path, new RawCollector(), startReader: false);
        Assert.Null(b.ReaderThread);
        b.Dispose();
        Assert.True(b.IsClosed);
        Assert.True(WatchFixture.InotifyFds() <= inotifyBefore, "the inotify fd is still open");
    }

    // ---- cache directories and wanted paths ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_cache_tag_drops_the_watches_in_its_directory_whether_it_comes_first_like_cargo_or_later()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fdinfo");
        using var f = new WatchFixture();
        var baseline = ProcessWatches();
        var (events, h) = f.Start();
        await h.Ready;
        Assert.Equal(1, ProcessWatches() - baseline);

        // Cargo: a temporary directory gets the tag, then is renamed into place; the build fills it afterwards.
        Directory.CreateDirectory(f.P(".tmpQx3"));
        File.WriteAllText(f.P(".tmpQx3/CACHEDIR.TAG"), CacheTag);
        Directory.Move(f.P(".tmpQx3"), f.P("target"));
        Directory.CreateDirectory(f.P("target/debug/deps"));
        File.WriteAllText(f.P("target/debug/deps/libx.rlib"), "x");
        await events.WaitForKind("target", FileChangeKind.Created, Ct);                                    // the folder itself is reported
        await WaitUntil(() => ProcessWatches() - baseline == 2, "target/ watched at its top only");

        // Another tool: the directory first, its tag later.
        Directory.CreateDirectory(f.P("cache/a/b"));
        await WaitUntil(() => ProcessWatches() - baseline == 5, "cache, cache/a and cache/a/b to be watched");
        File.WriteAllText(f.P("cache/CACHEDIR.TAG"), CacheTag);
        await WaitUntil(() => ProcessWatches() - baseline == 3, "the watches below cache/ to be dropped");
        File.WriteAllText(f.P("cache/a/b/late.bin"), "x");
        File.WriteAllText(f.P("marker.txt"), "x");
        await events.WaitForKind("marker.txt", FileChangeKind.Created, Ct);
        await Task.Delay(150, Ct);
        Assert.DoesNotContain(events.Snapshot(), c => Collector.Name(c) is "libx.rlib" or "deps" or "late.bin");
        h.Dispose();
    }

    [Fact]
    public async Task A_wanted_tree_inside_a_cache_directory_is_watched_alone_reported_and_let_go_when_no_longer_wanted()
    {
        // rust-analyzer watches a build script's OUT_DIR, target/debug/build/<crate>/out: the language client asks for it as a wanted tree.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fdinfo");
        using var f = new WatchFixture();
        Directory.CreateDirectory(f.P("target/debug/build/foo-1/out/nested"));
        Directory.CreateDirectory(f.P("target/debug/deps"));
        for (var i = 0; i < 50; i++) Directory.CreateDirectory(f.P($"target/debug/build/other-{i}/out"));
        File.WriteAllText(f.P("target/CACHEDIR.TAG"), CacheTag);
        var baseline = ProcessWatches();
        var outDir = f.P("target/debug/build/foo-1/out");
        var wants = new FileWatchWants([], [FileUri.FromDirectoryPath(outDir)]);
        var events = new Collector();
        var h = (FileWatchHandle)f.Watcher.Watch(f.Root, true, null, events.Add, wants);
        await h.Ready;
        Assert.Equal(7, ProcessWatches() - baseline);                                                        // the root, target, debug, build, foo-1, out, out/nested

        File.WriteAllText(f.P("target/debug/build/foo-1/out/gen.rs"), "x");
        File.WriteAllText(f.P("target/debug/build/foo-1/out/nested/more.rs"), "x");
        File.WriteAllText(f.P("target/debug/foo.d"), "x");                                                 // in a directory watched only on the way
        File.WriteAllText(f.P("target/debug/build/other-0/out/no.rs"), "x");
        await events.WaitFor(s => s.Any(c => Collector.Name(c) == "gen.rs") && s.Any(c => Collector.Name(c) == "more.rs"), "the OUT_DIR files", Ct);
        await Task.Delay(150, Ct);
        Assert.DoesNotContain(events.Snapshot(), c => Collector.Name(c) is "foo.d" or "no.rs");

        await wants.SetAsync([], []);                                                                        // no longer wanted: let go
        Assert.Equal(2, ProcessWatches() - baseline);                                                        // the root, and target/ at its top
        h.Dispose();
    }

    // ---- labels the queue no longer describes ------------------------------------------------------------------------------------

    [Fact]
    public async Task An_overflow_after_names_were_reused_and_directories_exchanged_relabels_every_watch_and_ends()
    {
        // While events were lost: `mv X X.moved; mkdir X` for several names (so the walk meets both orders) and `mv --exchange ex1 ex2`.
        // Reconciling the old labels path by path could relabel two watches back and forth for ever, inside the backend's lock.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        string[] names = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta"];
        foreach (var n in names) Directory.CreateDirectory(t.Combine(n, "sub"));
        Directory.CreateDirectory(t.Combine("ex1", "in1"));
        Directory.CreateDirectory(t.Combine("ex2", "in2"));
        var raw = new RawCollector();
        var b = Backend(t.Path, raw, startReader: false);
        foreach (var n in names)
        {
            Directory.Move(t.Combine(n), t.Combine(n + ".moved"));
            Directory.CreateDirectory(t.Combine(n, "fresh"));
        }
        const int AtFdCwd = -100;
        const uint RenameExchange = 2;
        _ = RenameAt2(AtFdCwd, t.Combine("ex1"), AtFdCwd, t.Combine("ex2"), RenameExchange); // (a file system that cannot exchange: the rest still holds)

        var resync = Task.Run(() => b.Process(Bytes(Ev(-1, InotifyBackend.InQOverflow)), endOfBurst: true), Ct);
        if (await Task.WhenAny(resync, Task.Delay(TimeSpan.FromSeconds(10), Ct)) != resync)
            Assert.Fail("the walk after the overflow did not end (the backend is left undisposed: its reader holds the lock)");
        try
        {
            string[] Tree() => [t.Path, .. Directory.GetDirectories(t.Path, "*", SearchOption.AllDirectories)];
            Assert.Equal(Tree().Order(StringComparer.Ordinal), b.WatchedDirectories.Order(StringComparer.Ordinal));
            Assert.Equal(b.WatchCount, KernelWatches(b.Fd));

            b.Pump(); // the events queued before the overflow marker's walk come now: they describe the past
            Assert.Equal(Tree().Order(StringComparer.Ordinal), b.WatchedDirectories.Order(StringComparer.Ordinal));
            Assert.Equal(b.WatchCount, KernelWatches(b.Fd));
            raw.Clear();
            foreach (var dir in Tree()) File.WriteAllText(Path.Join(dir, "probe.txt"), "x");
            b.Pump();
            foreach (var dir in Tree())
                Assert.Contains(raw.Snapshot(), e => e.Kind == RawKind.Created && e.Path == Path.Join(dir, "probe.txt")); // each under its own path
        }
        finally { b.Dispose(); }
    }

    [Fact]
    public void A_directory_renamed_then_moved_out_before_its_events_are_read_reports_nothing_under_its_old_path()
    {
        // mv A B; mv B <outside>; mkdir B — all before the backend reads: the rename's probe finds the new B, and the moved directory must not
        // keep its watch under the label A while it lives outside the tree.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        using var outside = new TempDirectory("cscode-outside-");
        Directory.CreateDirectory(t.Combine("A"));
        var raw = new RawCollector();
        using var b = Backend(t.Path, raw, startReader: false);
        Directory.Move(t.Combine("A"), t.Combine("B"));
        Directory.Move(t.Combine("B"), outside.Combine("X"));
        Directory.CreateDirectory(t.Combine("B"));
        b.Pump();
        Assert.Equal([t.Path, t.Combine("B")], b.WatchedDirectories.Order(StringComparer.Ordinal));
        Assert.Equal(b.WatchCount, KernelWatches(b.Fd));

        raw.Clear();
        File.WriteAllText(outside.Combine("X", "notes.rs"), "x");
        File.WriteAllText(t.Combine("B", "marker.txt"), "x");
        b.Pump();
        Assert.Contains(raw.Snapshot(), e => e.Path == t.Combine("B", "marker.txt"));
        Assert.DoesNotContain(raw.Snapshot(), e => e.Path.StartsWith(t.Combine("A"), StringComparison.Ordinal));
    }

    [Fact]
    public void A_directory_created_or_moved_in_just_before_its_parent_is_renamed_is_watched_under_the_new_name()
    {
        // mkdir X/new and mv <outside>/T X/T, then mv X Y — read late: X/new and X/T are not there any more when their events are handled.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var t = new TempDirectory("cscode-inotify-");
        using var outside = new TempDirectory("cscode-outside-");
        Directory.CreateDirectory(t.Combine("X"));
        Directory.CreateDirectory(outside.Combine("T", "deep"));
        var raw = new RawCollector();
        using var b = Backend(t.Path, raw, startReader: false);
        Directory.CreateDirectory(t.Combine("X", "new"));
        Directory.Move(outside.Combine("T"), t.Combine("X", "T"));
        Directory.Move(t.Combine("X"), t.Combine("Y"));
        b.Pump();
        Assert.Equal(new[] { t.Path, t.Combine("Y"), t.Combine("Y", "new"), t.Combine("Y", "T"), t.Combine("Y", "T", "deep") }.Order(StringComparer.Ordinal),
            b.WatchedDirectories.Order(StringComparer.Ordinal));

        raw.Clear();
        File.WriteAllText(t.Combine("Y", "new", "f.txt"), "x");
        File.WriteAllText(t.Combine("Y", "T", "deep", "g.txt"), "x");
        b.Pump();
        Assert.Contains(raw.Snapshot(), e => e.Kind == RawKind.Created && e.Path == t.Combine("Y", "new", "f.txt"));
        Assert.Contains(raw.Snapshot(), e => e.Kind == RawKind.Created && e.Path == t.Combine("Y", "T", "deep", "g.txt"));
    }

    // ---- what a subscription takes with it, virtual environments --------------------------------------------------------------------

    [Fact]
    public async Task A_subscription_that_goes_takes_the_watches_only_its_wants_kept()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fdinfo");
        using var f = new WatchFixture();
        Directory.CreateDirectory(f.P("node_modules/pkg/lib"));
        var baseline = ProcessWatches();
        var (_, plain) = f.Start();                                                                          // EditorConfig, say: no wants
        var wants = new FileWatchWants([FileUri.FromDirectoryPath(f.P("node_modules/pkg"))], []);
        var explorer = (FileWatchHandle)f.Watcher.Watch(f.Root, true, null, _ => { }, wants);
        await Task.WhenAll(plain.Ready, explorer.Ready);
        Assert.Equal(3, ProcessWatches() - baseline);                                                        // the root, node_modules, pkg

        explorer.Dispose();                                                                                  // the Explorer closes; the folder stays watched
        await WaitUntil(() => ProcessWatches() - baseline == 1, "the wanted folder's watches to go");
        plain.Dispose();
    }

    [Fact]
    public async Task A_tagged_python_virtual_environment_is_watched_and_reported_even_when_uv_writes_its_tag_first()
    {
        // uv tags the .venv it creates (CACHEDIR.TAG first, pyvenv.cfg last); Pyright resolves imports from it, so a package installed there
        // must reach the language server.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify");
        using var f = new WatchFixture();
        Directory.CreateDirectory(f.P(".venv/lib/site-packages"));
        File.WriteAllText(f.P(".venv/CACHEDIR.TAG"), CacheTag);
        File.WriteAllText(f.P(".venv/pyvenv.cfg"), "home = /usr/bin\n");
        var (events, h) = f.Start(excludes: []);
        await h.Ready;
        File.WriteAllText(f.P(".venv/lib/site-packages/httpx.py"), "x");
        await events.WaitForKind("httpx.py", FileChangeKind.Created, Ct);

        // `uv venv` with the folder open: the directory, its tag, the packages, pyvenv.cfg last.
        Directory.CreateDirectory(f.P("env2"));
        File.WriteAllText(f.P("env2/CACHEDIR.TAG"), CacheTag);                                             // (in the cache: not reported)
        await Task.Delay(300, Ct);
        Directory.CreateDirectory(f.P("env2/lib/site-packages"));
        File.WriteAllText(f.P("env2/lib/site-packages/early.py"), "x");
        File.WriteAllText(f.P("env2/pyvenv.cfg"), "home = /usr/bin\n");
        await events.WaitForPresent("early.py", Ct);                                                         // announced once it is an environment
        await Task.Delay(150, Ct);
        File.WriteAllText(f.P("env2/lib/site-packages/later.py"), "x");                                    // and watched from then on
        await events.WaitForKind("later.py", FileChangeKind.Created, Ct);
        h.Dispose();
    }
}
