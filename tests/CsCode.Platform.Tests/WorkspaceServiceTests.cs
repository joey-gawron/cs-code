using System.Diagnostics;
using CsCode.Core.Platform;
using CsCode.Platform.Configuration;
using CsCode.Platform.FileSystem;
using CsCode.Platform.Io;
using CsCode.Platform.State;
using CsCode.Platform.Watching;
using CsCode.Platform.Workspace;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Platform.Tests;

public sealed class WorkspaceFixture : IDisposable
{
    public TempDirectory Temp { get; } = new("cscode-ws-");
    public IoLane Lane { get; } = new("test.ws", 4);
    public AppPaths Paths { get; }
    public ConfigurationService Config { get; }
    public StateStorage State { get; }
    public LocalFileSystem Fs { get; }
    public FileWatcher Watcher { get; }
    public WorkspaceService Workspace { get; }
    public PlatformOptions Options { get; } = new() { IoThreads = 4, ConfigReloadDebounce = TimeSpan.FromMilliseconds(20), StateFlushDelay = TimeSpan.FromMilliseconds(50) };

    public WorkspaceFixture()
    {
        Paths = new AppPaths(k => k == "CSCODE_HOME" ? Temp.Combine("home") : null, HostOs.Linux, "/home/test");
        Watcher = new FileWatcher(Lane, null, Options);
        Config = new ConfigurationService(Paths, Lane, Watcher, Options);
        State = new StateStorage(Paths, Lane, Options);
        Fs = new LocalFileSystem(Lane, new FreedesktopTrash(Temp.Combine("trash")));
        Workspace = new WorkspaceService(Fs, Watcher, State, Config, Lane, [Config, State], Options);
    }

    public string Root => Temp.Combine("project");
    public string P(string rel) => Path.Combine(Root, Path.Combine(rel.Split('/')));
    public Uri RootUri => FileUri.FromDirectoryPath(Root);

    public string Write(string rel, string content = "x")
    {
        var full = P(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public async Task<string[]> Find(string? glob = null, int max = 0, CancellationToken ct = default)
    {
        var list = new List<string>();
        await foreach (var u in Workspace.FindFilesAsync(glob, max, ct)) list.Add(Rel(u));
        list.Sort(StringComparer.Ordinal);
        return list.ToArray();
    }

    public string Rel(Uri uri) => Path.GetRelativePath(Root, FileUri.ToPath(uri)).Replace('\\', '/');

    public void Dispose() { Workspace.Dispose(); Config.Dispose(); State.Dispose(); Watcher.Dispose(); Lane.Dispose(); Temp.Dispose(); }
}

public class WorkspaceFoldersTests : IDisposable
{
    private readonly WorkspaceFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Opening_a_folder_sets_Folders_PrimaryFolder_Name_and_raises_one_event()
    {
        Directory.CreateDirectory(_f.Root);
        var events = new List<WorkspaceFoldersChangedEventArgs>();
        using var sub = _f.Workspace.FoldersChanged.Subscribe(events.Add);
        Assert.Empty(_f.Workspace.Folders);
        Assert.Null(_f.Workspace.Name);
        Assert.Null(_f.Workspace.PrimaryFolder);

        await _f.Workspace.OpenFolderAsync(_f.RootUri, Ct);

        Assert.Equal([_f.RootUri], _f.Workspace.Folders);
        Assert.Equal(_f.RootUri, _f.Workspace.PrimaryFolder);
        Assert.Equal("project", _f.Workspace.Name);
        var e = Assert.Single(events);
        Assert.Equal([_f.RootUri], e.Added);
        Assert.Empty(e.Removed);
    }

    [Fact]
    public async Task A_folder_uri_without_a_trailing_slash_is_the_same_folder()
    {
        Directory.CreateDirectory(_f.Root);
        var events = 0;
        using var sub = _f.Workspace.FoldersChanged.Subscribe(_ => events++);
        await _f.Workspace.OpenFolderAsync(FileUri.FromPath(_f.Root), Ct);       // no trailing slash
        await _f.Workspace.OpenFolderAsync(_f.RootUri, Ct);                       // same folder again: nothing changes
        Assert.Equal(1, events);
        Assert.Single(_f.Workspace.Folders);
    }

    [Fact]
    public async Task Opening_another_folder_reports_the_old_one_as_removed()
    {
        Directory.CreateDirectory(_f.Root);
        var other = FileUri.FromDirectoryPath(_f.Temp.Combine("other"));
        Directory.CreateDirectory(_f.Temp.Combine("other"));
        await _f.Workspace.OpenFolderAsync(_f.RootUri, Ct);
        var events = new List<WorkspaceFoldersChangedEventArgs>();
        using var sub = _f.Workspace.FoldersChanged.Subscribe(events.Add);

        await _f.Workspace.OpenFolderAsync(other, Ct);

        var e = Assert.Single(events);
        Assert.Equal([other], e.Added);
        Assert.Equal([_f.RootUri], e.Removed);
        Assert.Equal("other", _f.Workspace.Name);
    }

    [Fact]
    public async Task Closing_clears_the_workspace_and_reports_the_removal()
    {
        Directory.CreateDirectory(_f.Root);
        await _f.Workspace.OpenFolderAsync(_f.RootUri, Ct);
        var events = new List<WorkspaceFoldersChangedEventArgs>();
        using var sub = _f.Workspace.FoldersChanged.Subscribe(events.Add);
        await _f.Workspace.CloseFolderAsync(Ct);
        Assert.Empty(_f.Workspace.Folders);
        Assert.Null(_f.Workspace.Name);
        Assert.Equal([_f.RootUri], Assert.Single(events).Removed);
        await _f.Workspace.CloseFolderAsync(Ct);        // closing nothing is quiet
        Assert.Single(events);
    }

    [Fact]
    public async Task A_missing_folder_or_a_file_cannot_be_opened_and_changes_nothing()
    {
        Directory.CreateDirectory(_f.Root);
        await _f.Workspace.OpenFolderAsync(_f.RootUri, Ct);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () => await _f.Workspace.OpenFolderAsync(FileUri.FromDirectoryPath(_f.Temp.Combine("nope")), Ct));
        var file = _f.Write("a.txt");
        await Assert.ThrowsAsync<IOException>(async () => await _f.Workspace.OpenFolderAsync(FileUri.FromPath(file), Ct));
        Assert.Equal([_f.RootUri], _f.Workspace.Folders);
        Assert.DoesNotContain(_f.Workspace.RecentFolders, r => r.AbsoluteUri.Contains("nope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Multi_root_workspaces_add_remove_and_name_all_folders()
    {
        Directory.CreateDirectory(_f.Root);
        var second = FileUri.FromDirectoryPath(_f.Temp.Combine("second"));
        Directory.CreateDirectory(_f.Temp.Combine("second"));
        await _f.Workspace.OpenFolderAsync(_f.RootUri, Ct);
        await _f.Workspace.AddFolderAsync(second, Ct);
        await _f.Workspace.AddFolderAsync(second, Ct);   // already there

        Assert.Equal([_f.RootUri, second], _f.Workspace.Folders);
        Assert.Equal(_f.RootUri, _f.Workspace.PrimaryFolder);
        Assert.Equal("project, second", _f.Workspace.Name);

        await _f.Workspace.RemoveFolderAsync(_f.RootUri, Ct);
        Assert.Equal([second], _f.Workspace.Folders);
        Assert.Equal(second, _f.Workspace.PrimaryFolder);
    }

    [Fact]
    public async Task Concurrent_folder_changes_are_serialised_and_end_in_a_consistent_state()
    {
        var dirs = Enumerable.Range(0, 8).Select(i => { var d = _f.Temp.Combine("d" + i); Directory.CreateDirectory(d); return FileUri.FromDirectoryPath(d); }).ToArray();
        var events = new List<WorkspaceFoldersChangedEventArgs>();
        using var sub = _f.Workspace.FoldersChanged.Subscribe(e => { lock (events) events.Add(e); });

        await Task.WhenAll(dirs.Select(d => _f.Workspace.OpenFolderAsync(d, Ct).AsTask())).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var current = Assert.Single(_f.Workspace.Folders);
        Assert.Contains(current, dirs);
        // every transition was announced once and the Added/Removed pairs chain correctly
        Uri? last = null;
        foreach (var e in events)
        {
            if (last is not null) Assert.Equal([last], e.Removed);
            last = Assert.Single(e.Added);
        }
        Assert.Equal(current, last);
    }

    [Fact]
    public async Task Dependents_see_the_new_workspace_BEFORE_FoldersChanged_subscribers_run()
    {
        Directory.CreateDirectory(_f.Root);
        _f.Write(".cscode/settings.json", """{ "editor.tabSize": 7 }""");
        var seenInHandler = -1;
        using var sub = _f.Workspace.FoldersChanged.Subscribe(_ => seenInHandler = _f.Config.Get<int>("editor.tabSize"));

        await _f.Workspace.OpenFolderAsync(_f.RootUri, Ct);

        Assert.Equal(7, seenInHandler);                       // settings were already switched when the event fired
        await _f.Workspace.CloseFolderAsync(Ct);
        Assert.Equal(4, seenInHandler);
    }

    [Fact]
    public async Task Workspace_scoped_state_follows_the_open_folder()
    {
        Directory.CreateDirectory(_f.Root);
        var other = _f.Temp.Combine("other");
        Directory.CreateDirectory(other);
        await _f.Workspace.OpenFolderAsync(_f.RootUri, Ct);
        _f.State.Set("tabs", new[] { "a.cs" }, StorageScope.Workspace);

        await _f.Workspace.OpenFolderAsync(FileUri.FromDirectoryPath(other), Ct);
        Assert.Null(_f.State.Get<string[]>("tabs", StorageScope.Workspace));

        await _f.Workspace.OpenFolderAsync(_f.RootUri, Ct);
        Assert.Equal(["a.cs"], _f.State.Get<string[]>("tabs", StorageScope.Workspace)!);
    }

    [Fact]
    public async Task OpenFolder_does_not_block_the_caller_while_the_io_lane_is_saturated()
    {
        Directory.CreateDirectory(_f.Root);
        using var gate = new ManualResetEventSlim();
        using var started = new CountdownEvent(4);
        var blockers = Enumerable.Range(0, 4).Select(_ => _f.Lane.RunAsync(_ => { started.Signal(); gate.Wait(); }, Ct)).ToArray();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), Ct));

        var sw = Stopwatch.StartNew();
        var open = _f.Workspace.OpenFolderAsync(_f.RootUri, Ct);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(100), $"call took {sw.Elapsed.TotalMilliseconds:F1} ms on the caller thread");
        Assert.False(open.IsCompleted);

        gate.Set();
        await open.AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Task.WhenAll(blockers);
        Assert.Single(_f.Workspace.Folders);
    }

    // ---- recents -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Recents_are_most_recent_first_deduplicated_and_capped_at_thirty()
    {
        for (var i = 0; i < 35; i++)
        {
            var d = _f.Temp.Combine("r" + i);
            Directory.CreateDirectory(d);
            await _f.Workspace.OpenFolderAsync(FileUri.FromDirectoryPath(d), Ct);
        }
        var recents = _f.Workspace.RecentFolders;
        Assert.Equal(30, recents.Count);
        Assert.EndsWith("/r34/", recents[0].AbsoluteUri);
        Assert.EndsWith("/r5/", recents[^1].AbsoluteUri);          // r0..r4 fell off the end

        // re-opening an old one moves it to the front without duplicating it
        await _f.Workspace.OpenFolderAsync(FileUri.FromDirectoryPath(_f.Temp.Combine("r10")), Ct);
        Assert.EndsWith("/r10/", _f.Workspace.RecentFolders[0].AbsoluteUri);
        Assert.Equal(30, _f.Workspace.RecentFolders.Count);
        Assert.Single(_f.Workspace.RecentFolders, r => r.AbsoluteUri.EndsWith("/r10/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recents_survive_a_restart_and_can_be_cleared()
    {
        Directory.CreateDirectory(_f.Root);
        await _f.Workspace.OpenFolderAsync(_f.RootUri, Ct);
        _f.Workspace.NoteRecent(FileUri.FromPath(_f.Write("notes.txt")));          // files can be recent too
        await _f.State.FlushAsync();

        using var state2 = new StateStorage(_f.Paths, _f.Lane, _f.Options);
        using var ws2 = new WorkspaceService(_f.Fs, _f.Watcher, state2, _f.Config, _f.Lane, [], _f.Options);
        Assert.Equal(2, ws2.RecentFolders.Count);
        Assert.EndsWith("/notes.txt", ws2.RecentFolders[0].AbsoluteUri);
        Assert.Equal(_f.RootUri, ws2.RecentFolders[1]);

        ws2.ClearRecents();
        Assert.Empty(ws2.RecentFolders);
    }

    [Fact]
    public void Exposes_the_file_system_and_watcher_it_was_built_with()
    {
        Assert.Same(_f.Fs, _f.Workspace.FileSystem);
        Assert.Same(_f.Watcher, _f.Workspace.Watcher);
    }
}

public class FindFilesTests : IDisposable
{
    private readonly WorkspaceFixture _f = new();
    public void Dispose() => _f.Dispose();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task Open()
    {
        Directory.CreateDirectory(_f.Root);
        await _f.Workspace.OpenFolderAsync(_f.RootUri, Ct);
    }

    // ---- basics ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Finds_every_file_in_nested_directories_including_hidden_ones()
    {
        _f.Write("a.txt"); _f.Write("src/b.cs"); _f.Write("src/deep/er/c.cs"); _f.Write(".hidden/d.txt"); _f.Write(".dotfile");
        await Open();
        Assert.Equal([".dotfile", ".hidden/d.txt", "a.txt", "src/b.cs", "src/deep/er/c.cs"], await _f.Find(ct: Ct));
    }

    [Fact]
    public async Task With_no_folder_open_it_yields_nothing()
    {
        _f.Write("a.txt");
        Assert.Empty(await _f.Find(ct: Ct));
    }

    [Fact]
    public async Task Version_control_directories_are_never_entered()
    {
        _f.Write("code.cs"); _f.Write(".git/HEAD"); _f.Write(".git/objects/ab/cdef"); _f.Write(".hg/store/x"); _f.Write(".svn/entries"); _f.Write("sub/.git/config");
        await Open();
        Assert.Equal(["code.cs"], await _f.Find(ct: Ct));
    }

    [Fact]
    public async Task Paths_with_spaces_hashes_percents_and_unicode_come_back_as_valid_uris()
    {
        var names = new[] { "with space/Ünï 100%.txt", "hash#dir/a#b.txt", "q?/x.txt", "日本語/ファイル.md" };
        foreach (var n in names) _f.Write(n);
        await Open();
        var found = new List<Uri>();
        await foreach (var u in _f.Workspace.FindFilesAsync(null, 0, Ct)) found.Add(u);
        Assert.Equal(names.Length, found.Count);
        foreach (var u in found) Assert.True(File.Exists(FileUri.ToPath(u)), u.ToString());
    }

    // ---- includeGlob & maxResults ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_include_glob_filters_by_folder_relative_path()
    {
        _f.Write("a.cs"); _f.Write("b.md"); _f.Write("src/c.cs"); _f.Write("src/deep/d.cs"); _f.Write("tests/e.cs"); _f.Write("README.md");
        await Open();
        Assert.Equal(["a.cs", "src/c.cs", "src/deep/d.cs", "tests/e.cs"], await _f.Find("**/*.cs", ct: Ct));
        Assert.Equal(["README.md", "b.md"], await _f.Find("*.md", ct: Ct));                 // no slash: file name only
        Assert.Equal(["src/c.cs", "src/deep/d.cs"], await _f.Find("src/**/*.cs", ct: Ct));
        Assert.Equal(["a.cs"], await _f.Find("a.cs", ct: Ct));
        Assert.Empty(await _f.Find("*.nothing", ct: Ct));
    }

    [Fact]
    public async Task maxResults_caps_the_stream_and_stops_the_walk_early()
    {
        for (var d = 0; d < 20; d++) for (var f = 0; f < 50; f++) _f.Write($"d{d}/f{f}.txt");
        await Open();
        Assert.Equal(7, (await _f.Find(max: 7, ct: Ct)).Length);
        Assert.Single(await _f.Find(max: 1, ct: Ct));
        Assert.Equal(1000, (await _f.Find(max: 0, ct: Ct)).Length);     // 0 = unlimited
        Assert.Equal(1000, (await _f.Find(max: 5000, ct: Ct)).Length);

        // after a capped search the lane is idle again: the walk really stopped
        await Task.Delay(100, Ct);
        Assert.Equal(0, _f.Lane.Pending);
    }

    // ---- files.exclude -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Default_files_exclude_hides_ds_store_and_thumbs_db()
    {
        _f.Write("real.txt"); _f.Write(".DS_Store"); _f.Write("sub/Thumbs.db"); _f.Write("sub/ok.txt");
        await Open();
        Assert.Equal(["real.txt", "sub/ok.txt"], await _f.Find(ct: Ct));
    }

    [Fact]
    public async Task The_files_exclude_setting_prunes_directories_and_files()
    {
        _f.Write("keep.cs"); _f.Write("obj/a.json"); _f.Write("src/obj/b.json"); _f.Write("src/x.tmp"); _f.Write("src/y.cs");
        await Open();
        await _f.Config.UpdateAsync("files.exclude", new Dictionary<string, bool> { ["**/obj"] = true, ["**/*.tmp"] = true, ["**/.git"] = false }, ct: Ct);
        Assert.Equal(["keep.cs", "src/y.cs"], await _f.Find(ct: Ct));
    }

    [Fact]
    public async Task Workspace_settings_can_override_the_exclusions()
    {
        _f.Write("a.cs"); _f.Write("gen/b.cs");
        _f.Write(".cscode/settings.json", """{ "files.exclude": { "**/gen": true } }""");
        await Open();
        Assert.Equal([".cscode/settings.json", "a.cs"], await _f.Find(ct: Ct));
    }

    // ---- .gitignore --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Gitignore_rules_hide_files_and_directories()
    {
        _f.Write(".gitignore", "bin/\n*.log\n!keep.log\n/rootonly.txt\n");
        _f.Write("src/a.cs"); _f.Write("bin/out.dll"); _f.Write("src/bin/x.dll"); _f.Write("noise.log"); _f.Write("keep.log"); _f.Write("rootonly.txt"); _f.Write("src/rootonly.txt");
        await Open();
        Assert.Equal([".gitignore", "keep.log", "src/a.cs", "src/rootonly.txt"], await _f.Find(ct: Ct));
    }

    [Fact]
    public async Task Nested_gitignore_files_apply_to_their_subtree_with_negations()
    {
        _f.Write(".gitignore", "*.log\n");
        _f.Write("sub/.gitignore", "!important.log\n/local.txt\n");
        _f.Write("a.log"); _f.Write("important.log"); _f.Write("sub/important.log"); _f.Write("sub/other.log"); _f.Write("sub/local.txt"); _f.Write("sub/x/local.txt"); _f.Write("local.txt");
        await Open();
        Assert.Equal([".gitignore", "local.txt", "sub/.gitignore", "sub/important.log", "sub/x/local.txt"], await _f.Find(ct: Ct));
    }

    [Fact]
    public async Task A_file_cannot_be_reincluded_when_its_directory_is_ignored()
    {
        _f.Write(".gitignore", "build/\n!build/keep.txt\n");
        _f.Write("build/keep.txt"); _f.Write("build/other.txt"); _f.Write("src/a.cs");
        await Open();
        Assert.Equal([".gitignore", "src/a.cs"], await _f.Find(ct: Ct));      // git's own rule: the directory was excluded first
    }

    [Fact]
    public async Task Git_info_exclude_is_honoured()
    {
        _f.Write(".git/info/exclude", "*.secret\n");
        _f.Write("a.cs"); _f.Write("x.secret");
        await Open();
        Assert.Equal(["a.cs"], await _f.Find(ct: Ct));
    }

    [Fact]
    public async Task search_useIgnoreFiles_false_turns_gitignore_off()
    {
        _f.Write(".gitignore", "*.log\n");
        _f.Write("a.log"); _f.Write("b.txt");
        await Open();
        await _f.Config.UpdateAsync("search.useIgnoreFiles", false, ct: Ct);
        Assert.Equal([".gitignore", "a.log", "b.txt"], await _f.Find(ct: Ct));
    }

    [Fact]
    public async Task A_huge_or_binary_looking_ignore_file_is_not_a_problem()
    {
        Directory.CreateDirectory(_f.Root);
        File.WriteAllBytes(_f.P(".gitignore"), Enumerable.Repeat((byte)'x', 3_000_000).ToArray());
        _f.Write("a.txt");
        await Open();
        Assert.Equal([".gitignore", "a.txt"], await _f.Find(ct: Ct));      // > 1 MB: skipped, not parsed
    }

    // ---- symlinks ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_symlink_cycle_terminates_and_each_file_is_found_once()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        _f.Write("a/file.txt"); _f.Write("a/b/deep.txt");
        Directory.CreateSymbolicLink(_f.P("a/b/loop"), _f.P("a"));          // a/b/loop -> a  (cycle)
        Directory.CreateSymbolicLink(_f.P("a/up"), _f.Root);                 // a/up -> project (a parent)
        await Open();
        var found = await _f.Find(ct: Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.Equal(["a/b/deep.txt", "a/file.txt"], found);
    }

    [Fact]
    public async Task A_link_to_a_directory_elsewhere_is_followed_unless_disabled_and_dangling_links_are_skipped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlink creation needs privileges on Windows");
        using var outside = new TempDirectory("cscode-outside-");
        outside.WriteFile("lib/util.cs", "x");
        Directory.CreateDirectory(_f.Root);
        Directory.CreateSymbolicLink(_f.P("vendor"), outside.Combine("lib"));
        File.CreateSymbolicLink(_f.P("broken"), _f.P("nowhere"));
        File.CreateSymbolicLink(_f.P("alias.txt"), _f.Write("real.txt"));
        await Open();

        Assert.Equal(["alias.txt", "real.txt", "vendor/util.cs"], await _f.Find(ct: Ct));

        await _f.Config.UpdateAsync("search.followSymlinks", false, ct: Ct);
        Assert.Equal(["alias.txt", "real.txt"], await _f.Find(ct: Ct));
    }

    // ---- multi-root & schemes ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_workspace_folder_is_searched()
    {
        _f.Write("one.txt");
        var second = _f.Temp.Combine("second");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "two.txt"), "x");
        await Open();
        await _f.Workspace.AddFolderAsync(FileUri.FromDirectoryPath(second), Ct);

        var all = new List<string>();
        await foreach (var u in _f.Workspace.FindFilesAsync(null, 0, Ct)) all.Add(Path.GetFileName(FileUri.ToPath(u)));
        Assert.Equal(["one.txt", "two.txt"], all.Order().ToArray());
    }

    // ---- scale, back-pressure, cancellation --------------------------------------------------------------------------------------

    private void BuildTree(int dirs, int filesPerDir)
    {
        for (var d = 0; d < dirs; d++)
        {
            var dir = _f.P($"d{d / 100}/e{d % 100}");
            Directory.CreateDirectory(dir);
            for (var f = 0; f < filesPerDir; f++) File.WriteAllBytes(Path.Combine(dir, $"f{f}.txt"), []);
        }
    }

    [Fact]
    public async Task A_big_tree_is_found_completely_and_the_pending_directory_set_stays_tiny()
    {
        // 10 x 10 x 10 = 1,000 leaf directories (1,110 in all) with 20 files each: 20,000 files. Fan-out 10, depth 3.
        for (var a = 0; a < 10; a++)
            for (var b = 0; b < 10; b++)
                for (var c = 0; c < 10; c++)
                {
                    var dir = _f.P($"a{a}/b{b}/c{c}");
                    Directory.CreateDirectory(dir);
                    for (var f = 0; f < 20; f++) File.WriteAllBytes(Path.Combine(dir, $"f{f}.txt"), []);
                }
        Directory.CreateDirectory(_f.Root);

        using var walker = new FileWalker(_f.Lane, [_f.Root], new WalkOptions(null, [], true, true, Degree: 3, MaxResults: int.MaxValue), Ct);
        var count = 0;
        var reader = walker.Reader;
        while (await reader.WaitToReadAsync(Ct))
        {
            while (reader.TryRead(out _)) count++;
            walker.Resume();
        }
        Assert.Equal(20_000, count);
        // Depth-first with a LIFO stack: at most ~ degree x depth x fan-out directories wait at once (3 x 3 x 10 = 90 here, plus the pumps' current
        // directories), nowhere near the 1,110 directories or the 20,000 files. A breadth-first walk would have queued ~1,000.
        Assert.True(walker.PeakPendingDirectories <= 150, $"peak pending directories was {walker.PeakPendingDirectories}");
    }

    [Fact]
    public async Task A_slow_consumer_gets_every_file_the_walk_parks_instead_of_buffering_or_blocking()
    {
        BuildTree(dirs: 100, filesPerDir: 30);           // 3,000 files, far more than the 1,024-slot channel
        await Open();
        var count = 0;
        await foreach (var _ in _f.Workspace.FindFilesAsync(null, 0, Ct))
        {
            if (++count % 500 == 0) await Task.Delay(30, Ct);           // slow consumer: the producer must wait, not block a thread
        }
        Assert.Equal(3000, count);
        Assert.Equal(0, _f.Lane.Pending);
    }

    [Fact]
    public async Task A_parked_walk_does_not_hold_any_lane_thread_other_work_proceeds()
    {
        BuildTree(dirs: 100, filesPerDir: 30);
        await Open();
        await using var e = _f.Workspace.FindFilesAsync(null, 0, Ct).GetAsyncEnumerator(Ct);
        Assert.True(await e.MoveNextAsync());                            // the walk starts, fills the channel and parks
        await Task.Delay(300, Ct);

        var sw = Stopwatch.StartNew();
        var stat = await _f.Fs.StatAsync(_f.RootUri, Ct);                // unrelated IO still runs immediately on the same lane
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250), $"unrelated stat took {sw.Elapsed.TotalMilliseconds:F0} ms while a search was parked");
        Assert.NotNull(stat);
        Assert.Equal(0, _f.Lane.Pending);                                // parked = nothing queued either
    }

    [Fact]
    public async Task Cancelling_stops_the_search_within_milliseconds_and_the_lane_goes_idle()
    {
        BuildTree(dirs: 2000, filesPerDir: 10);
        await Open();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var seen = 0;
        var cancelledAt = 0L;
        Exception? thrown = null;
        var stopped = 0L;
        try
        {
            await foreach (var _ in _f.Workspace.FindFilesAsync(null, 0, cts.Token))
            {
                if (++seen == 50) { cancelledAt = Stopwatch.GetTimestamp(); cts.Cancel(); }
            }
        }
        catch (OperationCanceledException ex) { thrown = ex; stopped = Stopwatch.GetTimestamp(); }

        Assert.NotNull(thrown);
        var latency = Stopwatch.GetElapsedTime(cancelledAt, stopped).TotalMilliseconds;
        Assert.True(latency < 100, $"cancellation took {latency:F1} ms to surface");
        Assert.True(seen < 20_000, "the walk kept going");

        for (var i = 0; i < 100 && _f.Lane.Pending > 0; i++) await Task.Delay(10, Ct);
        Assert.Equal(0, _f.Lane.Pending);
    }

    [Fact]
    public async Task Abandoning_the_enumeration_stops_the_walk()
    {
        BuildTree(dirs: 1000, filesPerDir: 10);
        await Open();
        var n = 0;
        await foreach (var _ in _f.Workspace.FindFilesAsync(null, 0, Ct)) { if (++n == 10) break; }
        for (var i = 0; i < 100 && _f.Lane.Pending > 0; i++) await Task.Delay(10, Ct);
        Assert.Equal(0, _f.Lane.Pending);
        // the lane is fully usable afterwards
        Assert.Equal(1, await _f.Lane.RunAsync(_ => 1, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task A_pre_cancelled_token_throws_without_walking()
    {
        _f.Write("a.txt");
        await Open();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await _f.Find(ct: cts.Token));
    }

    [Fact]
    public async Task The_search_does_not_block_its_caller_while_the_io_lane_is_saturated()
    {
        _f.Write("a.txt");
        await Open();
        using var gate = new ManualResetEventSlim();
        using var started = new CountdownEvent(4);
        var blockers = Enumerable.Range(0, 4).Select(_ => _f.Lane.RunAsync(_ => { started.Signal(); gate.Wait(); }, Ct)).ToArray();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), Ct));

        await using var e = _f.Workspace.FindFilesAsync(null, 0, Ct).GetAsyncEnumerator(Ct);
        var sw = Stopwatch.StartNew();
        var first = e.MoveNextAsync();
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(100), $"MoveNextAsync blocked for {sw.Elapsed.TotalMilliseconds:F1} ms");
        Assert.False(first.IsCompleted);

        gate.Set();
        Assert.True(await first);
        await Task.WhenAll(blockers);
    }

    [Fact]
    public async Task Unreadable_directories_are_skipped_not_fatal()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "permission bits are not enforced for root / differ on Windows");
        _f.Write("ok.txt"); _f.Write("locked/secret.txt");
        await Open();
        Chmod(_f.P("locked"), 0);
        try { Assert.Equal(["ok.txt"], await _f.Find(ct: Ct)); }
        finally { Chmod(_f.P("locked"), 0x1C0); }
    }

    private static void Chmod(string path, int mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)mode);
    }
}
