using System.Reflection;
using CsCode.Core.Platform;
using CsCode.Platform.Io;
using CsCode.Platform.Watching;
using CsCode.TestDoubles;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CsCode.Platform.Tests;

/// <summary>
/// What the macOS backends (MacWatchBackends.cs) must get right: FSEvents' sticky flags classified from the disk, rescan requests and the
/// root's own deletion, and non-recursive watches served by kqueue. The first ones hold on every platform and run everywhere.
/// </summary>
[Collection("Watchers")]
public class MacFileWatcherTests : IDisposable
{
    private readonly WatchFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- every platform ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_atomic_save_over_an_existing_file_reports_the_file_and_never_its_temporary()
    {
        File.WriteAllText(_f.P("doc.txt"), "1");
        var (events, h) = _f.Start();
        await h.Ready;
        File.WriteAllText(_f.P("doc.txt.tmp"), "2");
        File.Move(_f.P("doc.txt.tmp"), _f.P("doc.txt"), overwrite: true);
        File.WriteAllText(_f.P("marker.txt"), "x");
        await events.WaitForKind("marker.txt", FileChangeKind.Created, Ct);
        await events.WaitFor(s => s.Any(c => Collector.Name(c) == "doc.txt" && c.Kind != FileChangeKind.Deleted), "doc.txt", Ct);
        await Task.Delay(200, Ct);
        Assert.DoesNotContain(events.Snapshot(), c => Collector.Name(c) == "doc.txt.tmp");
        h.Dispose();
    }

    [Fact]
    public async Task A_recursively_watched_directory_that_is_deleted_reports_its_entries_and_keeps_reporting_once_made_again()
    {
        Directory.CreateDirectory(_f.P("cfg/sub"));
        File.WriteAllText(_f.P("cfg/a.json"), "x");
        File.WriteAllText(_f.P("cfg/sub/s.json"), "x");
        var events = new Collector();
        var h = (FileWatchHandle)_f.Watcher.Watch(FileUri.FromDirectoryPath(_f.P("cfg")), true, [], events.Add);
        await h.Ready;

        Directory.Delete(_f.P("cfg"), recursive: true);
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
    public async Task A_recursively_watched_directory_replaced_in_an_instant_by_a_tree_moved_there_reports_the_trees_content()
    {
        // Moved away and, in the same instant, a tree built elsewhere moved to its path, while nothing watches the parent. (Linux cannot see
        // this: an inotify watch follows the moved directory and says nothing; see FileWatcher's "Not detected".)
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "the FSEvents root watch");
        Directory.CreateDirectory(_f.P("cfg"));
        File.WriteAllText(_f.P("cfg/c.json"), "x");
        using var outside = new TempDirectory("cscode-outside-");
        outside.WriteFile("built/deep/moved.json", "x");
        var events = new Collector();
        var h = (FileWatchHandle)_f.Watcher.Watch(FileUri.FromDirectoryPath(_f.P("cfg")), true, [], events.Add);
        await h.Ready;

        Directory.Move(_f.P("cfg"), _f.P("cfg.bak"));
        Directory.Move(outside.Combine("built"), _f.P("cfg"));
        await events.WaitFor(s => s.Any(c => c.Kind == FileChangeKind.Deleted && Collector.Name(c) == "c.json"), "the moved directory's entries deleted", Ct);
        File.WriteAllText(_f.P("cfg/after-move.json"), "x");
        await events.WaitForKind("moved.json", FileChangeKind.Created, Ct);
        await events.WaitForKind("after-move.json", FileChangeKind.Created, Ct);
        h.Dispose();
    }

    // ---- FSEvents ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Sticky_fsevents_flags_are_classified_from_the_disk()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "FSEvents and the macOS lstat layout");
        Directory.CreateDirectory(_f.P("old"));
        File.WriteAllText(_f.P("old/before.txt"), "x");
        var seen = new List<(RawKind Kind, string Name)>();
        var reconciler = new FsEventsReconciler(_f.Temp.Path, (k, p, _) => seen.Add((k, Path.GetFileName(p))), _ => false);

        // FileSystemWatcher raises Changed then Created for one FSEvents event of a new file (both flags are set), and the same pair for a write
        // to a file that existed before the stream: the first is a creation, the second a change, each reported once.
        File.WriteAllText(_f.P("new.txt"), "x");
        reconciler.Handle(RawKind.Changed, _f.P("new.txt"), null);
        reconciler.Handle(RawKind.Created, _f.P("new.txt"), null);
        File.AppendAllText(_f.P("old/before.txt"), "y");
        reconciler.Handle(RawKind.Changed, _f.P("old/before.txt"), null);
        reconciler.Handle(RawKind.Created, _f.P("old/before.txt"), null);
        Assert.Equal([(RawKind.Created, "new.txt"), (RawKind.Changed, "before.txt")], seen);

        // a later write to the new file is a change; created and removed before the event arrived: nothing; removed: deleted
        seen.Clear();
        File.AppendAllText(_f.P("new.txt"), "y");
        reconciler.Handle(RawKind.Changed, _f.P("new.txt"), null);
        reconciler.Handle(RawKind.Changed, _f.P("came-and-went.lock"), null);
        reconciler.Handle(RawKind.Created, _f.P("came-and-went.lock"), null);
        reconciler.Handle(RawKind.Deleted, _f.P("came-and-went.lock"), null);
        File.Delete(_f.P("old/before.txt"));
        reconciler.Handle(RawKind.Changed, _f.P("old/before.txt"), null);
        reconciler.Handle(RawKind.Created, _f.P("old/before.txt"), null);
        reconciler.Handle(RawKind.Deleted, _f.P("old/before.txt"), null);
        Assert.Equal([(RawKind.Changed, "new.txt"), (RawKind.Deleted, "before.txt")], seen);
    }

    [Fact]
    public async Task An_fsevents_rescan_request_keeps_the_stream_and_asks_subscribers_to_rescan()
    {
        // files-4: FileSystemWatcher raises an IOException for MustScanSubDirs and for events dropped by the kernel or fseventsd. That is an
        // overflow, not a dead watch: polling the root for the rest of the session would lose the real-time watch for good.
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "FSEvents rescan requests");
        FswBackend? backend = null;
        using var f = new WatchFixture(factory: (root, recursive, sink, fatal) => backend = new FswBackend(root, recursive, sink, fatal));
        var (events, h) = f.Start();
        await h.Ready;

        var onError = typeof(FileSystemWatcher).GetMethod("OnError", BindingFlags.Instance | BindingFlags.NonPublic)!;
        onError.Invoke(backend!.Watcher, [new ErrorEventArgs(new IOException("FSEvents asked for a rescan"))]);
        await events.WaitFor(s => s.Any(c => c.Kind == FileChangeKind.Changed && FileUri.ToPath(c.Uri).TrimEnd('/') == f.Temp.Path.TrimEnd('/')), "the root changed", Ct);

        File.WriteAllText(f.P("after.txt"), "x");
        await events.WaitForKind("after.txt", FileChangeKind.Created, Ct);
        Assert.DoesNotContain(f.Log.Entries, e => e.Level >= LogLevel.Warning);
        h.Dispose();
    }

    // ---- kqueue (non-recursive watches) ------------------------------------------------------------------------------------------

    [Fact]
    public void A_non_recursive_watch_on_macOS_is_not_an_fsevents_stream_of_the_whole_subtree()
    {
        // files-9: a stream is always recursive, so watching ~ for ~/.zshrc would bring every event under ~/Library into the process.
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "kqueue backend");
        using var nonRecursive = OsWatchBackend.Create(_f.Temp.Path, recursive: false, (_, _, _) => { }, _ => { }, TimeSpan.FromSeconds(1));
        using var recursive = OsWatchBackend.Create(_f.Temp.Path, recursive: true, (_, _, _) => { }, _ => { }, TimeSpan.FromSeconds(1));
        Assert.IsType<KqueueDirectoryBackend>(nonRecursive);
        Assert.IsType<FswBackend>(recursive);
    }

    [Fact]
    public async Task A_kqueue_watch_reports_entries_and_writes_in_place_but_nothing_below_the_directory()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "kqueue backend");
        Directory.CreateDirectory(_f.P("sub"));
        File.WriteAllText(_f.P("rc"), "1");
        File.WriteAllText(_f.P("old"), "1");
        var (events, h) = _f.Start(recursive: false);
        await h.Ready;

        File.AppendAllText(_f.P("rc"), "2");                               // in place: the directory itself does not change
        await events.WaitForKind("rc", FileChangeKind.Changed, Ct);
        File.WriteAllText(_f.P("sub/deep.txt"), "x");
        File.WriteAllText(_f.P("new"), "x");
        File.Move(_f.P("old"), _f.P("renamed"));
        await events.WaitFor(s => s.Any(c => c.Kind == FileChangeKind.Created && Collector.Name(c) == "new")
            && s.Any(c => c.Kind == FileChangeKind.Deleted && Collector.Name(c) == "old")
            && s.Any(c => c.Kind == FileChangeKind.Created && Collector.Name(c) == "renamed"), "create and rename", Ct);
        await Task.Delay(150, Ct);
        events.Clear();
        File.AppendAllText(_f.P("new"), "y");                              // a file created after the watch is watched too
        await events.WaitForKind("new", FileChangeKind.Changed, Ct);
        File.Delete(_f.P("rc"));
        await events.WaitForKind("rc", FileChangeKind.Deleted, Ct);
        await Task.Delay(150, Ct);
        Assert.DoesNotContain(events.Snapshot(), c => Collector.Name(c) == "deep.txt");
        Assert.DoesNotContain(events.Snapshot(), c => Collector.Name(c) == "sub");
        h.Dispose();
    }

    [Fact]
    public async Task Files_beyond_the_kqueue_budget_are_polled()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "kqueue backend");
        for (var i = 0; i < KqueueDirectoryBackend.MaxWatchedFiles + 20; i++) File.WriteAllText(_f.P($"f{i:D3}"), "1");
        var seen = new List<string>();
        using var backend = new KqueueDirectoryBackend(_f.Temp.Path, (k, p, _) => { if (k == RawKind.Changed) lock (seen) seen.Add(Path.GetFileName(p)); }, TimeSpan.FromMilliseconds(50));
        Assert.Equal(KqueueDirectoryBackend.MaxWatchedFiles, backend.WatchedFiles);

        for (var i = 0; i < KqueueDirectoryBackend.MaxWatchedFiles + 20; i++) File.AppendAllText(_f.P($"f{i:D3}"), "2");
        for (var i = 0; i < 500; i++)
        {
            lock (seen) if (seen.Distinct().Count() == KqueueDirectoryBackend.MaxWatchedFiles + 20) break;
            await Task.Delay(20, Ct);
        }
        lock (seen) Assert.Equal(KqueueDirectoryBackend.MaxWatchedFiles + 20, seen.Distinct().Count());
    }

    [Fact]
    public async Task An_empty_directory_deleted_and_made_again_with_nothing_watching_its_parent_keeps_reporting()
    {
        // Nothing reports the deletion of an empty directory to its own watch's owner (there are no entries to report): the kqueue backend
        // looks at the path itself until a directory is back. (Linux cannot see this: see FileWatcher's "Not detected".)
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "kqueue backend");
        Directory.CreateDirectory(_f.P("cfg"));
        var events = new Collector();
        var h = (FileWatchHandle)_f.Watcher.Watch(FileUri.FromDirectoryPath(_f.P("cfg")), false, [], events.Add);
        await h.Ready;

        Directory.Delete(_f.P("cfg"));
        await Task.Delay(300, Ct);
        Directory.CreateDirectory(_f.P("cfg"));
        File.WriteAllText(_f.P("cfg/back.json"), "x");
        await events.WaitForKind("back.json", FileChangeKind.Created, Ct);
        await Task.Delay(150, Ct);
        File.WriteAllText(_f.P("cfg/later.json"), "x");
        await events.WaitForKind("later.json", FileChangeKind.Created, Ct);
        h.Dispose();
    }
}
