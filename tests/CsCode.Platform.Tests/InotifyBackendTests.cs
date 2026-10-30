using System.Diagnostics;
using System.Text;
using CsCode.Core.Platform;
using CsCode.Platform.Io;
using CsCode.Platform.Watching;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Platform.Tests;

/// <summary>
/// The Linux backend on its own (synthetic events, watch counts read from the kernel, fd and thread release) and through the
/// <see cref="FileWatcher"/> (what a directory every subscription excludes costs; subscriptions that join with fewer excludes).
/// Runs with the other watcher tests, alone: it counts process-wide inotify watches and fds.
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
    }

    private static InotifyBackend Backend(string root, RawCollector raw, Func<string, bool>? isPruned = null, bool recursive = true) =>
        new(root, recursive, isPruned ?? (_ => false), raw.Add, ex => raw.Add(RawKind.Changed, "FATAL: " + ex.Message, null));

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
        using var b = Backend(t.Path, raw);
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

    [Fact]
    public async Task A_tree_with_a_big_excluded_target_directory_costs_only_its_wanted_directories_in_watches()
    {
        // The bug: one window on a Rust + JS monorepo held 75,892 inotify watches, 56,489 of them in target/. Through the FileWatcher with
        // the default excludes, the excluded directories must cost nothing (a FileSystemWatcher put a watch on each of the 213 here).
        Assert.SkipUnless(OperatingSystem.IsLinux(), "counts /proc/self/fdinfo");
        using var f = new WatchFixture();
        var wanted = new[] { "src/a", "src/b", "docs", ".git/refs" };                                       // + the root, src, .git: 7 directories
        foreach (var d in wanted) Directory.CreateDirectory(f.P(d));
        for (var i = 0; i < 200; i++) Directory.CreateDirectory(f.P($"target/debug/build/d{i:D3}"));     // 203 directories
        Directory.CreateDirectory(f.P("node_modules/pkg/lib"));                                             // 3
        Directory.CreateDirectory(f.P(".git/objects/ab"));                                                  // 2
        Directory.CreateDirectory(f.P("crates/x/target/debug/incremental"));                               // 2 wanted (crates, crates/x), 3 not
        File.WriteAllText(f.P("target/debug/build/d000/out.o"), "");

        var baseline = ProcessWatches();
        var (events, h) = f.Start();                                                                         // null: files.watcherExclude, else the built-ins
        await h.Ready;
        var cost = ProcessWatches() - baseline;
        Assert.True(cost <= 9, $"{cost} inotify watches for a tree with 9 wanted directories");
        Assert.Equal(9, cost);

        File.WriteAllText(f.P("target/debug/build/d000/out.o"), "rebuilt");
        File.WriteAllText(f.P("crates/x/target/debug/incremental/q.bin"), "");
        File.WriteAllText(f.P("src/b/lib.rs"), "");
        await events.WaitForKind("lib.rs", FileChangeKind.Created, Ct);
        await Task.Delay(150, Ct);
        Assert.DoesNotContain(events.Snapshot(), c => Collector.Name(c) is "out.o" or "q.bin");
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
        using var b = Backend(t.Path, raw);

        Directory.CreateDirectory(t.Combine("fresh"));
        await raw.WaitFor(RawKind.Created, t.Combine("fresh"));                                             // reported only once it is watched
        File.WriteAllText(t.Combine("fresh", "inner.txt"), "x");
        await raw.WaitFor(RawKind.Created, t.Combine("fresh", "inner.txt"));

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
        using var f = new WatchFixture(factory: (root, recursive, isPruned, sink, fatal) =>
        {
            var backend = FileWatcher.DefaultBackend(root, recursive, isPruned, sink, fatal);
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
}
