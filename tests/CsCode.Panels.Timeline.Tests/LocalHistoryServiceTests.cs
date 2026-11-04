using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CsCode.Core.History;
using CsCode.Core.Platform;
using CsCode.Panels.Timeline.LocalHistory;
using Xunit;

namespace CsCode.Panels.Timeline.Tests;

/// <summary>The local history engine over the real file system: saves, VS Code's layout, mergeWindow, limits, exclusions, renames, restarts.</summary>
public class LocalHistoryServiceTests
{
    [Fact]
    public void The_folder_name_is_VS_Codes_hash_of_the_resource()
    {
        // Values computed with VS Code's hash() (base/common/hash.ts) in node.
        Assert.Equal("4849c989", HistoryFormat.Hash("file:///Users/me/project/a.cs"));
        Assert.Equal("341de848", HistoryFormat.Hash("file:///tmp/x.txt"));
        Assert.Equal("-12ac7d2b", HistoryFormat.Hash("file:///tmp/f10.txt"));
        Assert.Equal("3186634c", HistoryFormat.Hash("ssh://host/home/u/é.py"));
    }

    [Fact]
    public async Task A_save_stores_the_saved_file_in_VS_Codes_layout()
    {
        using var rig = new HistoryRig();
        await rig.SaveAsync("src/a.cs", "class A {}\r\n");

        var entries = await rig.History.GetEntriesAsync(rig.UriOf("src/a.cs"));
        var entry = Assert.Single(entries);
        Assert.Equal(LocalHistorySources.FileSaved, entry.Source);
        Assert.Equal("File Saved", entry.SourceLabel);
        Assert.Equal(rig.Time.GetUtcNow(), entry.Timestamp);
        Assert.Equal(["class A {}\r\n"], await rig.ContentsAsync("src/a.cs")); // the bytes on disk, line endings and all

        var key = new Uri(rig.PathOf("src/a.cs")).AbsoluteUri;
        var folder = Path.Combine(rig.Home, HistoryFormat.Hash(key));
        Assert.True(File.Exists(Path.Combine(folder, entry.Id)));
        Assert.EndsWith(".cs", entry.Id);
        Assert.Equal(7, entry.Id.Length); // four random characters + the extension
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "entries.json")));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(key, json.RootElement.GetProperty("resource").GetString());
        var stored = Assert.Single(json.RootElement.GetProperty("entries").EnumerateArray().ToList());
        Assert.Equal(entry.Id, stored.GetProperty("id").GetString());
        Assert.Equal(entry.Timestamp.ToUnixTimeMilliseconds(), stored.GetProperty("timestamp").GetInt64());
        Assert.False(stored.TryGetProperty("source", out _)); // VS Code leaves the default source out
    }

    [Fact]
    public async Task Saves_within_the_merge_window_replace_the_newest_entry_of_the_same_source()
    {
        using var rig = new HistoryRig();
        await rig.SaveAsync("a.txt", "one");
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        await rig.SaveAsync("a.txt", "two"); // within 10 s: replaces "one"
        var entries = await rig.History.GetEntriesAsync(rig.UriOf("a.txt"));
        Assert.Equal(["two"], await rig.ContentsAsync("a.txt"));
        Assert.Equal(rig.Time.GetUtcNow(), Assert.Single(entries).Timestamp);

        rig.Time.Advance(TimeSpan.FromSeconds(11));
        await rig.SaveAsync("a.txt", "three"); // outside it: a new entry
        Assert.Equal(["two", "three"], await rig.ContentsAsync("a.txt"));

        // another source never merges
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Write("a.txt", "four");
        Assert.NotNull(await rig.History.AddEntryAsync(rig.UriOf("a.txt"), "checkpoint"));
        Assert.Equal(["two", "three", "four"], await rig.ContentsAsync("a.txt"));

        // the window is the setting's
        rig.Config.Set(LocalHistorySettings.MergeWindow, 60);
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        await rig.SaveAsync("a.txt", "five");
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        await rig.SaveAsync("a.txt", "six");
        Assert.Equal(["two", "three", "four", "six"], await rig.ContentsAsync("a.txt"));
    }

    [Fact]
    public async Task A_save_that_changed_nothing_adds_no_entry()
    {
        using var rig = new HistoryRig();
        await rig.SaveAsync("a.txt", "same");
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.SaveAsync("a.txt", "same");
        Assert.Single(await rig.History.GetEntriesAsync(rig.UriOf("a.txt")));

        // also after a restart (the newest entry's content is read back to compare)
        rig.Restart();
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.SaveAsync("a.txt", "same");
        Assert.Single(await rig.History.GetEntriesAsync(rig.UriOf("a.txt")));
    }

    [Fact]
    public async Task Entries_past_maxFileEntries_are_pruned_oldest_first_and_their_copies_deleted()
    {
        using var rig = new HistoryRig();
        rig.Config.Set(LocalHistorySettings.MaxFileEntries, 3);
        for (var i = 1; i <= 5; i++)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(20));
            await rig.SaveAsync("a.txt", "v" + i);
        }
        Assert.Equal(["v3", "v4", "v5"], await rig.ContentsAsync("a.txt"));
        var folder = Path.GetDirectoryName((await rig.History.GetEntriesAsync(rig.UriOf("a.txt")))[0].Location.LocalPath)!;
        Assert.Equal(4, Directory.GetFiles(folder).Length); // three copies + entries.json

        rig.Config.Set(LocalHistorySettings.MaxFileEntries, 0);
        rig.Time.Advance(TimeSpan.FromSeconds(20));
        await rig.SaveAsync("a.txt", "v6");
        Assert.Empty(await rig.History.GetEntriesAsync(rig.UriOf("a.txt")));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task Excluded_files_too_large_files_and_a_disabled_history_store_nothing()
    {
        using var rig = new HistoryRig();
        rig.Config.Set(LocalHistorySettings.Exclude, new Dictionary<string, bool> { ["**/*.log"] = true, ["secret/**"] = true, ["*.tmp"] = false });
        await rig.SaveAsync("app.log", "log");
        await rig.SaveAsync("secret/key.txt", "k");
        await rig.SaveAsync("notes.tmp", "t"); // a pattern set to false excludes nothing
        Assert.Empty(await rig.History.GetEntriesAsync(rig.UriOf("app.log")));
        Assert.Empty(await rig.History.GetEntriesAsync(rig.UriOf("secret/key.txt")));
        Assert.Single(await rig.History.GetEntriesAsync(rig.UriOf("notes.tmp")));

        // an absolute pattern is matched against the full path
        rig.Config.Set(LocalHistorySettings.Exclude, new Dictionary<string, bool> { [rig.PathOf("abs").Replace('\\', '/') + "/**"] = true });
        await rig.SaveAsync("abs/x.txt", "x");
        Assert.Empty(await rig.History.GetEntriesAsync(rig.UriOf("abs/x.txt")));

        rig.Config.Set(LocalHistorySettings.MaxFileSize, 1); // KB
        await rig.SaveAsync("big.txt", new string('x', 2048));
        await rig.SaveAsync("small.txt", new string('x', 500));
        Assert.Empty(await rig.History.GetEntriesAsync(rig.UriOf("big.txt")));
        Assert.Single(await rig.History.GetEntriesAsync(rig.UriOf("small.txt")));

        rig.Config.Set(LocalHistorySettings.Enabled, false);
        await rig.SaveAsync("off.txt", "o");
        Assert.Empty(await rig.History.GetEntriesAsync(rig.UriOf("off.txt")));
        Assert.Null(await rig.History.AddEntryAsync(rig.UriOf("off.txt"), "named"));

        // untitled buffers and files inside the history folder itself have none
        Assert.False(rig.History.CanTrack(new Uri("untitled:Untitled-1")));
        Assert.False(rig.History.CanTrack(new Uri(Path.Combine(rig.Home, "123", "abcd.txt"))));
    }

    [Fact]
    public async Task Saves_never_wait_for_history_IO()
    {
        using var rig = new HistoryRig();
        rig.Write("a.txt", "content");
        var doc = rig.Document("a.txt");
        rig.Fs.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); // the disk "hangs"

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++) rig.Documents.NotifySaved(doc);
        clock.Stop();
        Assert.True(clock.ElapsedMilliseconds < 200, $"200 saves took {clock.ElapsedMilliseconds} ms on the caller's thread");

        // the public API only enqueues too: its task is still running while the disk is held
        var add = rig.History.AddEntryAsync(rig.UriOf("a.txt"), "named").AsTask();
        await Task.Delay(100);
        Assert.False(add.IsCompleted);
        Assert.False(Directory.Exists(rig.Home)); // nothing reached the disk

        rig.Fs.Hold.SetResult();
        await add.WaitAsync(TimeSpan.FromSeconds(30));
        await rig.History.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var entries = await rig.History.GetEntriesAsync(rig.UriOf("a.txt"));
        Assert.Equal(2, entries.Count); // the saves merged into one (same content), then the named one
        Assert.Equal(0, rig.History.DroppedCount);
    }

    [Fact]
    public async Task Entries_and_their_names_survive_a_restart()
    {
        using var rig = new HistoryRig();
        await rig.SaveAsync("a.txt", "one");
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.SaveAsync("a.txt", "two");
        var before = await rig.History.GetEntriesAsync(rig.UriOf("a.txt"));
        await rig.History.RenameEntryAsync(before[0], "before refactoring");

        rig.Restart();
        var after = await rig.History.GetEntriesAsync(rig.UriOf("a.txt"));
        Assert.Equal(["before refactoring", "File Saved"], after.Select(e => e.SourceLabel));
        Assert.Equal(before.Select(e => (e.Id, e.Timestamp)), after.Select(e => (e.Id, e.Timestamp)));
        Assert.Equal(["one", "two"], await rig.ContentsAsync("a.txt"));
        Assert.Equal([rig.UriOf("a.txt")], (await rig.History.GetResourcesAsync()).Select(u => new Uri(u.LocalPath)));

        // and keep working: a save within the window of the restored newest entry merges into it
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        await rig.SaveAsync("a.txt", "three");
        Assert.Equal(["one", "three"], await rig.ContentsAsync("a.txt"));
    }

    [Fact]
    public async Task Two_processes_sharing_the_folder_keep_each_others_entries()
    {
        using var rig = new HistoryRig();
        // a second CsCode process (another session): its own service and caches over the same History folder
        var otherDocuments = new Core.Text.DocumentManager();
        using var other = new LocalHistoryService(rig.Fs, rig.Home, rig.Config, otherDocuments, rig.WorkspaceService, rig.Time);
        var uri = rig.UriOf("a.txt");
        using var _ = otherDocuments.Register(new Core.Text.InMemoryTextDocument(uri, "plaintext", ""));

        await rig.SaveAsync("a.txt", "from one");
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        rig.Write("a.txt", "from two");
        otherDocuments.NotifySaved(otherDocuments.Get(uri)!);
        await other.WhenIdleAsync();
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.SaveAsync("a.txt", "one again");
        Assert.Equal(["from one", "from two", "one again"], await rig.ContentsAsync("a.txt"));
        Assert.Equal(3, (await other.GetEntriesAsync(uri)).Count);

        // a save merging into the other process's newest entry compares with that entry's content, not a cached one
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        rig.Write("a.txt", "two again");
        otherDocuments.NotifySaved(otherDocuments.Get(uri)!);
        await other.WhenIdleAsync();
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.SaveAsync("a.txt", "two again"); // the same as the newest (written by the other process): nothing added
        Assert.Equal(["from one", "from two", "two again"], await rig.ContentsAsync("a.txt"));

        // files the other process gave history to are listed
        rig.Write("b.txt", "b");
        Assert.NotNull(await other.AddEntryAsync(rig.UriOf("b.txt"), "named"));
        Assert.Equal(2, (await rig.History.GetResourcesAsync()).Count);
    }

    [Fact]
    public async Task A_damaged_entries_file_is_rebuilt_from_the_copies()
    {
        using var rig = new HistoryRig();
        await rig.SaveAsync("a.txt", "one");
        var entry = Assert.Single(await rig.History.GetEntriesAsync(rig.UriOf("a.txt")));
        var folder = Path.GetDirectoryName(entry.Location.LocalPath)!;
        File.WriteAllText(Path.Combine(folder, "entries.json"), "{ not json");
        rig.Restart();
        var rebuilt = Assert.Single(await rig.History.GetEntriesAsync(rig.UriOf("a.txt")));
        Assert.Equal(entry.Id, rebuilt.Id);
        Assert.Equal(["one"], await rig.ContentsAsync("a.txt"));
    }

    [Fact]
    public async Task A_folder_name_taken_by_another_file_with_the_same_hash_is_not_shared()
    {
        using var rig = new HistoryRig();
        var key = new Uri(rig.PathOf("a.txt")).AbsoluteUri;
        var taken = Path.Combine(rig.Home, HistoryFormat.Hash(key));
        Directory.CreateDirectory(taken);
        File.WriteAllText(Path.Combine(taken, "entries.json"), """{"version":1,"resource":"file:///somewhere/else.txt","entries":[{"id":"zzzz.txt","timestamp":1}]}""");
        File.WriteAllText(Path.Combine(taken, "zzzz.txt"), "other");

        await rig.SaveAsync("a.txt", "mine");
        var entry = Assert.Single(await rig.History.GetEntriesAsync(rig.UriOf("a.txt")));
        Assert.Equal(taken + "_1", Path.GetDirectoryName(entry.Location.LocalPath));
        Assert.Equal("other", File.ReadAllText(Path.Combine(taken, "zzzz.txt")));
        Assert.Equal(2, (await rig.History.GetResourcesAsync()).Count);
    }

    [Fact]
    public async Task Entries_follow_a_file_renamed_or_moved_and_every_file_of_a_folder_renamed()
    {
        using var rig = new HistoryRig();
        await rig.SaveAsync("a.txt", "a1");
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.SaveAsync("a.txt", "a2");

        File.Move(rig.PathOf("a.txt"), rig.PathOf("b.txt"));
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.History.DidRenameFilesAsync([(rig.UriOf("a.txt"), rig.UriOf("b.txt"))], default);
        await rig.History.WhenIdleAsync();
        Assert.Empty(await rig.History.GetEntriesAsync(rig.UriOf("a.txt")));
        var renamed = await rig.History.GetEntriesAsync(rig.UriOf("b.txt"));
        Assert.Equal(["File Saved", "File Saved", "File Renamed"], renamed.Select(e => e.SourceLabel));
        Assert.Equal(["a1", "a2", "a2"], await rig.ContentsAsync("b.txt"));

        Directory.CreateDirectory(rig.PathOf("sub"));
        File.Move(rig.PathOf("b.txt"), rig.PathOf("sub/b.txt"));
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.History.DidRenameFilesAsync([(rig.UriOf("b.txt"), rig.UriOf("sub/b.txt"))], default);
        await rig.History.WhenIdleAsync();
        Assert.Equal(["File Saved", "File Saved", "File Renamed", "File Moved"], (await rig.History.GetEntriesAsync(rig.UriOf("sub/b.txt"))).Select(e => e.SourceLabel));

        // a folder: every file under it with history follows (the Explorer passes folder URIs with a trailing slash)
        await rig.SaveAsync("sub/c.txt", "c");
        rig.Time.Advance(TimeSpan.FromMinutes(1)); // (a second move within mergeWindow would replace the first "File Moved", as in VS Code)
        Directory.Move(rig.PathOf("sub"), rig.PathOf("moved"));
        await rig.History.DidRenameFilesAsync([(new Uri(rig.PathOf("sub") + "/"), new Uri(rig.PathOf("moved") + "/"))], default);
        await rig.History.WhenIdleAsync();
        Assert.Empty(await rig.History.GetEntriesAsync(rig.UriOf("sub/b.txt")));
        Assert.Empty(await rig.History.GetEntriesAsync(rig.UriOf("sub/c.txt")));
        Assert.Equal(5, (await rig.History.GetEntriesAsync(rig.UriOf("moved/b.txt"))).Count);
        Assert.Equal(["c", "c"], await rig.ContentsAsync("moved/c.txt"));

        // and that is what a restart finds
        rig.Restart();
        Assert.Equal(5, (await rig.History.GetEntriesAsync(rig.UriOf("moved/b.txt"))).Count);
        Assert.Equal(2, (await rig.History.GetResourcesAsync()).Count);
    }

    [Fact]
    public async Task A_rename_onto_a_file_with_history_merges_both_by_time()
    {
        using var rig = new HistoryRig();
        await rig.SaveAsync("a.txt", "a");
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.SaveAsync("b.txt", "b");
        File.Delete(rig.PathOf("b.txt"));
        File.Move(rig.PathOf("a.txt"), rig.PathOf("b.txt"));
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.History.MoveEntriesAsync(rig.UriOf("a.txt"), rig.UriOf("b.txt"));
        Assert.Equal(["a", "b", "a"], await rig.ContentsAsync("b.txt"));
    }

    [Fact]
    public async Task Entries_can_be_renamed_deleted_and_all_deleted()
    {
        using var rig = new HistoryRig();
        var changes = new List<Uri?>();
        using var sub = rig.History.Changed.Subscribe(u => { lock (changes) changes.Add(u); });
        for (var i = 1; i <= 3; i++)
        {
            rig.Time.Advance(TimeSpan.FromMinutes(1));
            await rig.SaveAsync("a.txt", "v" + i);
        }
        await rig.SaveAsync("b.txt", "b");
        var entries = await rig.History.GetEntriesAsync(rig.UriOf("a.txt"));
        Assert.True(await rig.History.RemoveEntryAsync(entries[1]));
        Assert.False(await rig.History.RemoveEntryAsync(entries[1]));
        Assert.False(File.Exists(entries[1].Location.LocalPath));
        Assert.Equal(["v1", "v3"], await rig.ContentsAsync("a.txt"));
        var renamed = await rig.History.RenameEntryAsync(entries[0], "first");
        Assert.Equal("first", renamed!.SourceLabel);

        await rig.History.RemoveAllAsync(rig.UriOf("a.txt"));
        Assert.Empty(await rig.History.GetEntriesAsync(rig.UriOf("a.txt")));
        Assert.Single(await rig.History.GetEntriesAsync(rig.UriOf("b.txt")));

        await rig.History.RemoveAllAsync(null);
        Assert.Empty(await rig.History.GetEntriesAsync(rig.UriOf("b.txt")));
        Assert.Empty(await rig.History.GetResourcesAsync());
        Assert.False(Directory.Exists(rig.Home));
        lock (changes) Assert.Contains(null, changes);
    }

    [Fact]
    public async Task The_next_save_can_be_given_a_source()
    {
        using var rig = new HistoryRig();
        rig.History.SetNextSaveSource(rig.UriOf("a.txt"), LocalHistorySources.FileRestored);
        await rig.SaveAsync("a.txt", "restored");
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.SaveAsync("a.txt", "edited");
        Assert.Equal(["File Restored", "File Saved"], (await rig.History.GetEntriesAsync(rig.UriOf("a.txt"))).Select(e => e.SourceLabel));
    }

    [Fact]
    public async Task Resources_are_listed_most_recently_changed_first()
    {
        using var rig = new HistoryRig();
        await rig.SaveAsync("old.txt", "o");
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.SaveAsync("new.txt", "n");
        Assert.Equal(["new.txt", "old.txt"], (await rig.History.GetResourcesAsync()).Select(u => Path.GetFileName(u.LocalPath)));
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.SaveAsync("old.txt", "o2");
        Assert.Equal(["old.txt", "new.txt"], (await rig.History.GetResourcesAsync()).Select(u => Path.GetFileName(u.LocalPath)));
    }

    /// <summary>
    /// A file system serving <c>ssh://host/...</c> from a local folder (the remote module's provider stands behind the same router in the app),
    /// counting the reads made over "the connection".
    /// </summary>
    private sealed class RemoteRouter(IFileSystem local, string remoteRoot) : IFileSystem
    {
        public int RemoteReads;
        private Uri Map(Uri uri) => uri.Scheme == "ssh" ? new Uri(remoteRoot + Uri.UnescapeDataString(uri.AbsolutePath)) : uri;
        public ValueTask<FileStat?> StatAsync(Uri uri, CancellationToken ct = default) => local.StatAsync(Map(uri), ct);
        public ValueTask<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(Uri uri, CancellationToken ct = default) => local.ReadDirectoryAsync(Map(uri), ct);
        public ValueTask<byte[]> ReadFileAsync(Uri uri, CancellationToken ct = default)
        {
            if (uri.Scheme == "ssh") Interlocked.Increment(ref RemoteReads);
            return local.ReadFileAsync(Map(uri), ct);
        }
        public ValueTask WriteFileAsync(Uri uri, ReadOnlyMemory<byte> content, bool createParents = true, CancellationToken ct = default) => local.WriteFileAsync(Map(uri), content, createParents, ct);
        public ValueTask CreateDirectoryAsync(Uri uri, CancellationToken ct = default) => local.CreateDirectoryAsync(Map(uri), ct);
        public ValueTask DeleteAsync(Uri uri, bool recursive = false, bool useTrash = true, CancellationToken ct = default) => local.DeleteAsync(Map(uri), recursive, useTrash, ct);
        public ValueTask RenameAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => local.RenameAsync(Map(source), Map(target), overwrite, ct);
        public ValueTask CopyAsync(Uri source, Uri target, bool overwrite = false, CancellationToken ct = default) => local.CopyAsync(Map(source), Map(target), overwrite, ct);
    }

    [Fact]
    public async Task A_remote_files_history_is_kept_on_this_machine_and_follows_its_renames()
    {
        using var rig = new HistoryRig(start: false);
        var remoteRoot = Path.Combine(rig.Workspace, "remote-host");
        Directory.CreateDirectory(Path.Combine(remoteRoot, "home", "u"));
        var router = new RemoteRouter(rig.Local, remoteRoot);
        using var history = new LocalHistoryService(router, rig.Home, rig.Config, rig.Documents, new FakeWorkspace(router, new Uri("ssh://host/home/u/")), rig.Time);
        var remote = new Uri("ssh://host/home/u/main.py");
        Assert.True(history.CanTrack(remote));
        File.WriteAllText(Path.Combine(remoteRoot, "home", "u", "main.py"), "print(1)\n");
        using var _ = rig.Documents.Register(new Core.Text.InMemoryTextDocument(remote, "python", "print(1)\n"));
        rig.Documents.NotifySaved(rig.Documents.Get(remote)!);
        await history.WhenIdleAsync();

        var entry = Assert.Single(await history.GetEntriesAsync(remote));
        Assert.True(entry.Location.IsFile); // the copy is a local file
        Assert.StartsWith(rig.Home, entry.Location.LocalPath);
        Assert.Equal(HistoryFormat.Hash("ssh://host/home/u/main.py"), Path.GetFileName(Path.GetDirectoryName(entry.Location.LocalPath)));
        Assert.Equal("print(1)\n", Encoding.UTF8.GetString((await history.ReadAsync(entry))!));
        Assert.Equal(1, router.RemoteReads); // the saved file was read once over the connection; the stat said its size first

        // a workspace-relative exclude pattern applies to remote folders too
        rig.Config.Set(LocalHistorySettings.Exclude, new Dictionary<string, bool> { ["**/*.log"] = true });
        Assert.True(history.IsExcluded(new Uri("ssh://host/home/u/out.log")));

        File.Move(Path.Combine(remoteRoot, "home", "u", "main.py"), Path.Combine(remoteRoot, "home", "u", "app.py"));
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        await history.DidRenameFilesAsync([(remote, new Uri("ssh://host/home/u/app.py"))], default);
        await history.WhenIdleAsync();
        Assert.Equal(["File Saved", "File Renamed"], (await history.GetEntriesAsync(new Uri("ssh://host/home/u/app.py"))).Select(e => e.SourceLabel));
    }

    [Fact]
    public async Task Trimming_drops_the_caches_and_the_entries_are_read_again()
    {
        using var rig = new HistoryRig();
        await rig.SaveAsync("a.txt", "one");
        await rig.SaveAsync("b.txt", "two");
        Assert.Equal(2, (await rig.History.GetResourcesAsync()).Count);
        var trimmable = (CsCode.Core.Diagnostics.IMemoryTrimmable)rig.History;
        Assert.True(trimmable.EstimatedBytes > 0);
        Assert.Equal(0, trimmable.Trim(CsCode.Core.Diagnostics.MemoryTrimLevel.Idle));
        Assert.True(trimmable.Trim(CsCode.Core.Diagnostics.MemoryTrimLevel.Moderate) > 0);
        await rig.History.WhenIdleAsync();
        Assert.Equal(0, trimmable.EstimatedBytes);
        Assert.Equal(["one"], await rig.ContentsAsync("a.txt"));
        Assert.Equal(2, (await rig.History.GetResourcesAsync()).Count);
    }

    [Fact]
    public void Entries_json_ids_that_would_leave_their_folder_are_dropped()
    {
        var parsed = HistoryFormat.Parse(Encoding.UTF8.GetBytes("""{"resource":"file:///a","entries":[{"id":"../../x","timestamp":2},{"id":"ok.txt","timestamp":1},{"id":"entries.json","timestamp":3}]}"""));
        Assert.Equal(["ok.txt"], parsed!.Entries.Select(e => e.Id));
        Assert.Null(HistoryFormat.Parse(Encoding.UTF8.GetBytes("[]")));
    }
}
