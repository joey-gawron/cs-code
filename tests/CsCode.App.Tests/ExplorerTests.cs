using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using CsCode.App.Notifications;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.ToolWindows.Explorer;
using CsCode.Core.Platform;
using CsCode.Platform.Io;
using CsCode.Platform.Watching;
using CsCode.TestDoubles;
using CsCode.Ui.Threading;
using Xunit;

namespace CsCode.App.Tests;

public class ExplorerTests
{
    private sealed class Rig : IDisposable
    {
        public TempDirectory Dir { get; } = new("cscode-explorer-");
        public FakeFileWatcher Watcher { get; } = new();
        public FakeWorkspaceService Workspace { get; }
        public FakeModalDialogs Dialogs { get; } = new();
        public InMemoryStateStorage State { get; } = new();
        public TestConfigurationService Config { get; } = new();
        public ExplorerViewModel Vm { get; }

        public Rig()
        {
            Workspace = new FakeWorkspaceService(new DiskFileSystem(), Watcher);
            Vm = new ExplorerViewModel(Workspace, AvaloniaUiDispatcher.Instance, null, Config, State, Dialogs);
        }

        public async Task OpenAsync()
        {
            await Workspace.OpenFolderAsync(Dir.Uri); // the view model reacts to FoldersChanged
            await Vm.RefreshCompleted;
        }

        public string[] Names => Vm.Rows.Select(r => new string(' ', r.Depth) + r.Name).ToArray();
        public string P(string rel) => Dir.Combine(rel.Split('/'));
        public void Dispose() { Vm.Dispose(); Dir.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Opening_a_folder_shows_the_root_expanded_with_folders_first_then_files_sorted()
    {
        using var rig = new Rig();
        rig.Dir.WriteFile("zeta.txt", "");
        rig.Dir.WriteFile("Alpha.cs", "");
        rig.Dir.WriteFile("src/inner.cs", "");
        Directory.CreateDirectory(rig.P("docs"));
        await rig.OpenAsync();
        var root = Path.GetFileName(rig.Dir.Path);
        Assert.Equal([root, " docs", " src", " Alpha.cs", " zeta.txt"], rig.Names);
        Assert.True(rig.Vm.HasFolder);
    }

    [AvaloniaFact]
    public async Task Folders_load_lazily_on_expand_and_collapse_removes_exactly_their_rows()
    {
        using var rig = new Rig();
        rig.Dir.WriteFile("a/b/deep.txt", "");
        rig.Dir.WriteFile("a/x.txt", "");
        rig.Dir.WriteFile("top.txt", "");
        await rig.OpenAsync();
        var reads = rig.Vm.DirectoryReads;
        Assert.Equal(1, reads); // only the root so far

        var a = rig.Vm.Rows.Single(r => r.Name == "a");
        await rig.Vm.ToggleAsync(a);
        Assert.Equal(reads + 1, rig.Vm.DirectoryReads);
        Assert.Contains("  x.txt", rig.Names);
        var b = rig.Vm.Rows.Single(r => r.Name == "b");
        await rig.Vm.ToggleAsync(b);
        Assert.Contains("   deep.txt", rig.Names);

        rig.Vm.Collapse(a);
        Assert.DoesNotContain(rig.Names, n => n.Contains("deep.txt") || n.Contains("x.txt"));
        Assert.Contains(" top.txt", rig.Names);

        await rig.Vm.ToggleAsync(a); // re-expand: nested expansion state is remembered, no re-read needed
        Assert.Contains("   deep.txt", rig.Names);
        Assert.Equal(reads + 2, rig.Vm.DirectoryReads);
    }

    [AvaloniaFact]
    public async Task Expansion_state_is_persisted_and_restored_for_the_next_session()
    {
        using var rig = new Rig();
        rig.Dir.WriteFile("a/b/c.txt", "");
        await rig.OpenAsync();
        await rig.Vm.ToggleAsync(rig.Vm.Rows.Single(r => r.Name == "a"));
        await rig.Vm.ToggleAsync(rig.Vm.Rows.Single(r => r.Name == "b"));
        Assert.NotEmpty(rig.State.Get<List<string>>(ExplorerViewModel.ExpandedStateKey, StorageScope.Workspace)!);

        using var second = new ExplorerViewModel(rig.Workspace, AvaloniaUiDispatcher.Instance, null, rig.Config, rig.State, rig.Dialogs);
        await second.RefreshRootsAsync();
        Assert.Contains(second.Rows, r => r.Name == "c.txt");
    }

    [AvaloniaFact]
    public async Task Create_rename_duplicate_and_delete_operate_on_disk_and_update_rows()
    {
        using var rig = new Rig();
        rig.Dir.WriteFile("keep.txt", "k");
        await rig.OpenAsync();
        var root = rig.Vm.Roots[0];

        var created = await rig.Vm.CreateFileAsync(root, "new.cs");
        Assert.NotNull(created);
        Assert.True(File.Exists(rig.P("new.cs")));
        Assert.Contains(" new.cs", rig.Names);
        await rig.Vm.CreateFolderAsync(root, "sub");
        Assert.True(Directory.Exists(rig.P("sub")));

        var node = rig.Vm.Rows.Single(r => r.Name == "new.cs");
        rig.Vm.BeginRename(node);
        node.EditText = "renamed.cs";
        Assert.True(await rig.Vm.CommitRenameAsync(node));
        Assert.True(File.Exists(rig.P("renamed.cs")));
        Assert.False(File.Exists(rig.P("new.cs")));

        var keep = rig.Vm.Rows.Single(r => r.Name == "keep.txt");
        await rig.Vm.DuplicateAsync(keep);
        Assert.True(File.Exists(rig.P("keep copy.txt")));
        await rig.Vm.DuplicateAsync(keep);
        Assert.True(File.Exists(rig.P("keep copy 2.txt")));

        rig.Dialogs.Answers.Enqueue("Move to Trash");
        Assert.True(await rig.Vm.DeleteAsync(rig.Vm.Rows.Single(r => r.Name == "renamed.cs")));
        Assert.False(File.Exists(rig.P("renamed.cs")));
        Assert.DoesNotContain(" renamed.cs", rig.Names);
    }

    [AvaloniaFact]
    public async Task Delete_is_cancelled_when_the_confirmation_is_declined_and_roots_cannot_be_deleted()
    {
        using var rig = new Rig();
        rig.Dir.WriteFile("x.txt", "");
        await rig.OpenAsync();
        rig.Dialogs.Answers.Enqueue("Cancel");
        Assert.False(await rig.Vm.DeleteAsync(rig.Vm.Rows.Single(r => r.Name == "x.txt")));
        Assert.True(File.Exists(rig.P("x.txt")));
        Assert.False(await rig.Vm.DeleteAsync(rig.Vm.Roots[0]));
    }

    [AvaloniaFact]
    public async Task Invalid_and_colliding_names_are_rejected_with_a_message()
    {
        using var rig = new Rig();
        rig.Dir.WriteFile("exists.txt", "");
        await rig.OpenAsync();
        var root = rig.Vm.Roots[0];
        Assert.NotNull(rig.Vm.ValidateName("a/b", root, null));
        Assert.NotNull(rig.Vm.ValidateName("", root, null));
        Assert.NotNull(rig.Vm.ValidateName("..", root, null));
        Assert.NotNull(rig.Vm.ValidateName("EXISTS.txt", root, null));
        Assert.Null(rig.Vm.ValidateName("fresh.txt", root, null));
        Assert.Null(await rig.Vm.CreateFileAsync(root, "exists.txt"));
        Assert.Single(rig.Dialogs.Shown);
    }

    [AvaloniaFact]
    public async Task Cut_paste_and_drag_drop_move_files_and_copy_paste_duplicates()
    {
        using var rig = new Rig();
        rig.Dir.WriteFile("one.txt", "1");
        rig.Dir.WriteFile("two.txt", "2");
        rig.Dir.WriteFile("three.txt", "3");
        Directory.CreateDirectory(rig.P("dest"));
        await rig.OpenAsync();
        var dest = rig.Vm.Rows.Single(r => r.Name == "dest");

        rig.Vm.Cut([rig.Vm.Rows.Single(r => r.Name == "one.txt")]);
        Assert.True(rig.Vm.CanPaste);
        await rig.Vm.PasteAsync(dest);
        Assert.True(File.Exists(rig.P("dest/one.txt")));
        Assert.False(File.Exists(rig.P("one.txt")));

        rig.Vm.Copy([rig.Vm.Rows.Single(r => r.Name == "two.txt")]);
        await rig.Vm.PasteAsync(dest);
        Assert.True(File.Exists(rig.P("two.txt")));
        Assert.True(File.Exists(rig.P("dest/two.txt")));

        await rig.Vm.MoveAsync([rig.Vm.Rows.Single(r => r.Name == "three.txt")], dest); // drag and drop
        Assert.False(File.Exists(rig.P("three.txt")));
        Assert.True(File.Exists(rig.P("dest/three.txt")));
        // A name collision in the target is skipped rather than overwritten.
        rig.Dir.WriteFile("two.txt", "again");
        await rig.Vm.ReloadDirectoryAsync(rig.Vm.Roots[0]);
        await rig.Vm.MoveAsync([rig.Vm.Rows.Single(r => r.Name == "two.txt" && r.Parent == rig.Vm.Roots[0])], dest);
        Assert.True(File.Exists(rig.P("two.txt")));
    }

    [AvaloniaFact]
    public async Task A_folder_cannot_be_moved_into_itself_or_its_descendants()
    {
        using var rig = new Rig();
        rig.Dir.WriteFile("a/b/f.txt", "");
        await rig.OpenAsync();
        var a = rig.Vm.Rows.Single(r => r.Name == "a");
        await rig.Vm.ToggleAsync(a);
        var b = rig.Vm.Rows.Single(r => r.Name == "b");
        await rig.Vm.MoveAsync([a], b);
        Assert.True(Directory.Exists(rig.P("a/b")));
    }

    [AvaloniaFact]
    public async Task Watcher_batches_update_only_the_affected_directory_without_rereading_the_tree()
    {
        using var rig = new Rig();
        rig.Dir.WriteFile("src/a.cs", "");
        rig.Dir.WriteFile("other/o.cs", "");
        await rig.OpenAsync();
        await rig.Vm.ToggleAsync(rig.Vm.Rows.Single(r => r.Name == "src"));
        await rig.Vm.ToggleAsync(rig.Vm.Rows.Single(r => r.Name == "other"));
        var reads = rig.Vm.DirectoryReads;

        rig.Dir.WriteFile("src/b.cs", "");
        File.Delete(rig.P("src/a.cs"));
        rig.Watcher.Raise(new FileChange(new Uri(rig.P("src/b.cs")), FileChangeKind.Created), new FileChange(new Uri(rig.P("src/a.cs")), FileChangeKind.Deleted));
        await rig.Vm.FlushWatcherAsync();
        Assert.Equal(reads + 1, rig.Vm.DirectoryReads); // only 'src' was re-read, 'other' and the root were not
        Assert.Contains("  b.cs", rig.Names);
        Assert.DoesNotContain("  a.cs", rig.Names);
        Assert.Contains("  o.cs", rig.Names);
    }

    [AvaloniaFact]
    public async Task Watcher_updates_keep_expanded_subfolders_expanded()
    {
        using var rig = new Rig();
        rig.Dir.WriteFile("a/sub/deep.txt", "");
        await rig.OpenAsync();
        await rig.Vm.ToggleAsync(rig.Vm.Rows.Single(r => r.Name == "a"));
        await rig.Vm.ToggleAsync(rig.Vm.Rows.Single(r => r.Name == "sub"));
        rig.Dir.WriteFile("a/new.txt", "");
        rig.Watcher.Raise(new FileChange(new Uri(rig.P("a/new.txt")), FileChangeKind.Created));
        await rig.Vm.FlushWatcherAsync();
        Assert.Contains("  new.txt", rig.Names);
        Assert.Contains("   deep.txt", rig.Names);
    }

    [AvaloniaFact]
    public async Task Reveal_expands_ancestors_and_selects_the_file()
    {
        using var rig = new Rig();
        rig.Dir.WriteFile("a/b/c/target.cs", "");
        await rig.OpenAsync();
        var node = await rig.Vm.RevealAsync(new Uri(rig.P("a/b/c/target.cs")));
        Assert.NotNull(node);
        Assert.Same(node, rig.Vm.Selected);
        Assert.Contains(rig.Vm.Rows, r => r == node);
        Assert.Null(await rig.Vm.RevealAsync(new Uri("file:///elsewhere/x.cs")));
    }

    [AvaloniaFact]
    public async Task Type_ahead_selects_the_next_row_with_that_prefix_and_wraps()
    {
        using var rig = new Rig();
        foreach (var n in new[] { "apple.txt", "avocado.txt", "banana.txt" }) rig.Dir.WriteFile(n, "");
        await rig.OpenAsync();
        Assert.Equal("apple.txt", rig.Vm.TypeAhead("a")!.Name);
        Assert.Equal("avocado.txt", rig.Vm.TypeAhead("a")!.Name);
        Assert.Equal("apple.txt", rig.Vm.TypeAhead("a")!.Name); // wraps
        Assert.Equal("banana.txt", rig.Vm.TypeAhead("b")!.Name);
        Assert.Null(rig.Vm.TypeAhead("zzz"));
    }

    [AvaloniaFact]
    public async Task Files_exclude_hides_matching_entries()
    {
        using var rig = new Rig();
        rig.Dir.WriteFile("keep.cs", "");
        rig.Dir.WriteFile("node_modules/x.js", "");
        rig.Dir.WriteFile("out.log", "");
        rig.Config.Set(SettingKeys.FilesExclude, new Dictionary<string, bool> { ["**/node_modules"] = true, ["*.log"] = true });
        await rig.OpenAsync();
        Assert.Contains(" keep.cs", rig.Names);
        Assert.DoesNotContain(rig.Names, n => n.Contains("node_modules") || n.Contains("out.log"));
    }

    [AvaloniaFact]
    public async Task The_folder_watch_excludes_watcher_excluded_and_explorer_excluded_folders_but_still_shows_the_former()
    {
        // The Explorer's subscription shares the folder's OS watch with the language servers and EditorConfig (files.watcherExclude); a folder
        // every subscription excludes costs no OS watch. With files.exclude alone it kept node_modules/, bin/ and obj/ watched for everyone.
        using var rig = new Rig();
        rig.Dir.WriteFile("target/debug/app", "");
        rig.Dir.WriteFile("src/main.rs", "");
        rig.Config.Set(SettingKeys.FilesExclude, new Dictionary<string, bool> { ["**/.git"] = true, ["*.log"] = true, ["**/off"] = false });
        rig.Config.Set(SettingKeys.FilesWatcherExclude, new Dictionary<string, bool> { ["**/target/**"] = true, ["**/node_modules/**"] = false });
        await rig.OpenAsync();

        var excludes = Assert.Single(rig.Watcher.ActiveExcludes);
        Assert.NotNull(excludes);
        Assert.Equal(["**/.git", "**/target/**", "*.log"], excludes.Order(StringComparer.Ordinal));

        // What is shown is still files.exclude alone: a watcher-excluded folder is listed, and expanding it reads the disk.
        var target = rig.Vm.Rows.Single(r => r.Name == "target");
        await rig.Vm.ToggleAsync(target);
        Assert.Contains("  debug", rig.Names);
    }

    [AvaloniaFact]
    public async Task Without_settings_the_live_refresh_excludes_the_built_in_watcher_excludes_and_the_explorer_defaults()
    {
        using var rig = new Rig();
        await rig.OpenAsync();
        var excludes = Assert.Single(rig.Watcher.ActiveExcludes);
        Assert.NotNull(excludes);
        Assert.Contains("**/bin/**", excludes);
        Assert.Contains("**/node_modules/**", excludes);
        Assert.Contains("**/.git", excludes);
        Assert.DoesNotContain("**/target/**", excludes); // left out by its cache tag instead: a source folder may be called target/
    }

    /// <summary>The Explorer over a real folder watch (the OS backend), for what refreshes live.</summary>
    private sealed class LiveRig : IDisposable
    {
        public TempDirectory Dir { get; } = new("cscode-explorer-live-");
        public IoLane Lane { get; } = new("test.explorer.watch", 2);
        public FileWatcher Watcher { get; }
        public FakeWorkspaceService Workspace { get; }
        public TestConfigurationService Config { get; } = new();
        public ExplorerViewModel Vm { get; }

        public LiveRig()
        {
            Watcher = new FileWatcher(Lane);
            Workspace = new FakeWorkspaceService(new DiskFileSystem(), Watcher);
            Vm = new ExplorerViewModel(Workspace, AvaloniaUiDispatcher.Instance, null, Config, new InMemoryStateStorage(), new FakeModalDialogs());
        }

        public async Task OpenAsync()
        {
            await Workspace.OpenFolderAsync(Dir.Uri);
            await Vm.RefreshCompleted;
        }

        public string[] Names => Vm.Rows.Select(r => new string(' ', r.Depth) + r.Name).ToArray();
        public string P(string rel) => Dir.Combine(rel.Split('/'));
        public ExplorerNode Row(string name) => Vm.Rows.Single(r => r.Name == name);

        public async Task Until(Func<string[], bool> condition, string what)
        {
            var sw = Stopwatch.StartNew();
            while (!condition(Names))
            {
                if (sw.ElapsedMilliseconds > 10_000) throw new TimeoutException($"Timed out waiting for {what}. Rows: {string.Join(" | ", Names)}");
                await Task.Delay(20);
            }
        }

        public void Dispose() { Vm.Dispose(); Watcher.Dispose(); Lane.Dispose(); Dir.Dispose(); }
    }

    [AvaloniaFact]
    public async Task Build_output_folders_appearing_and_going_under_an_expanded_folder_show_up_and_leave_live()
    {
        // `dotnet build` creates bin/ and obj/ in an expanded project folder, `git clean -xdf` removes them: the folders themselves are reported
        // (an event is excluded by its own path only), so neither is missing after a build nor left behind as a ghost after a clean.
        using var rig = new LiveRig();
        rig.Dir.WriteFile("src/App/Program.cs", "");
        rig.Dir.WriteFile("src/Lib/obj/project.assets.json", "");                                // present when the folder opens
        await rig.OpenAsync();
        await rig.Vm.ToggleAsync(rig.Row("src"));
        await rig.Vm.ToggleAsync(rig.Row("App"));
        await rig.Vm.ToggleAsync(rig.Row("Lib"));
        await rig.Vm.WantsApplied;

        Directory.CreateDirectory(rig.P("src/App/bin/Debug"));
        File.WriteAllText(rig.P("src/App/bin/Debug/App.dll"), "x");
        await rig.Until(n => n.Contains("   bin"), "bin/ to show up");

        Directory.Delete(rig.P("src/App/bin"), recursive: true);
        Directory.Delete(rig.P("src/Lib/obj"), recursive: true);
        await rig.Until(n => !n.Contains("   bin") && !n.Contains("   obj"), "bin/ and obj/ to leave");
    }

    [AvaloniaFact]
    public async Task An_expanded_folder_inside_a_watcher_excluded_one_refreshes_live_and_is_read_again_when_expanded_after_a_collapse()
    {
        // bin/ is excluded from the folder's watch (files.watcherExclude), so it costs no OS watch; but a folder the user expands there is asked
        // for, its entries refresh live — Rust's src/bin/ and a Node CLI's bin/ are source. Collapsed it is not watched: expanding it again reads
        // the disk instead of showing what was cached.
        using var rig = new LiveRig();
        rig.Dir.WriteFile("src/bin/tool.rs", "");
        await rig.OpenAsync();
        await rig.Vm.ToggleAsync(rig.Row("src"));
        await rig.Vm.ToggleAsync(rig.Row("bin"));
        await rig.Vm.WantsApplied;
        Assert.Contains("   tool.rs", rig.Names);

        File.WriteAllText(rig.P("src/bin/other.rs"), "");
        await rig.Until(n => n.Contains("   other.rs"), "the new file in the expanded bin/ to show up");

        rig.Vm.Collapse(rig.Row("bin"));
        await rig.Vm.WantsApplied;
        File.WriteAllText(rig.P("src/bin/while-collapsed.rs"), "");
        File.Delete(rig.P("src/bin/tool.rs"));
        await Task.Delay(400);
        await rig.Vm.ToggleAsync(rig.Row("bin"));
        Assert.Contains("   while-collapsed.rs", rig.Names);
        Assert.DoesNotContain("   tool.rs", rig.Names);
    }

    [AvaloniaFact]
    public async Task The_filter_box_narrows_to_matching_loaded_nodes_and_clearing_restores_the_tree()
    {
        using var rig = new Rig();
        rig.Dir.WriteFile("a.cs", "");
        rig.Dir.WriteFile("b.txt", "");
        await rig.OpenAsync();
        rig.Vm.Filter = "cs";
        for (var i = 0; i < 100 && rig.Vm.Rows.Count != 1; i++) await Task.Delay(10);
        Assert.Equal(["a.cs"], rig.Vm.Rows.Select(r => r.Name));
        rig.Vm.Filter = "";
        Assert.Equal(3, rig.Vm.Rows.Count);
    }

    [AvaloniaFact]
    public async Task A_20k_file_folder_expands_within_a_time_budget_and_one_collection_change()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(rig.P("big"));
        for (var i = 0; i < 20_000; i++) File.WriteAllBytes(Path.Combine(rig.P("big"), $"file{i:D5}.txt"), []);
        await rig.OpenAsync();
        var big = rig.Vm.Rows.Single(r => r.Name == "big");
        var changes = 0;
        rig.Vm.Rows.CollectionChanged += (_, _) => changes++;
        var sw = Stopwatch.StartNew();
        await rig.Vm.ToggleAsync(big);
        sw.Stop();
        Assert.Equal(20_001 + 1, rig.Vm.Rows.Count);
        Assert.Equal(1, changes);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"expanding 20k files took {sw.ElapsedMilliseconds} ms");
        // The UI-thread part (splicing the prepared nodes into the row list) is tiny; measure it separately from the worker's read+sort.
        rig.Vm.Collapse(big);
        var sw2 = Stopwatch.StartNew();
        await rig.Vm.ToggleAsync(big);
        Assert.True(sw2.ElapsedMilliseconds < 500, $"re-expanding an already loaded 20k folder took {sw2.ElapsedMilliseconds} ms");
    }

    [AvaloniaFact]
    public async Task Disposing_the_view_model_stops_watching_and_releases_nodes()
    {
        var rig = new Rig();
        rig.Dir.WriteFile("a/b.txt", "");
        await rig.OpenAsync();
        await rig.Vm.ToggleAsync(rig.Vm.Rows.Single(r => r.Name == "a"));
        Assert.Equal(1, rig.Watcher.ActiveWatches);
        rig.Vm.Dispose();
        Assert.Equal(0, rig.Watcher.ActiveWatches);
        Assert.Empty(rig.Vm.Rows);
        rig.Dir.Dispose();
    }

    [AvaloniaFact]
    public async Task Tree_nodes_are_not_retained_after_the_folder_is_closed()
    {
        var rig = new Rig();
        rig.Dir.WriteFile("a/b.txt", "");
        await LeakAssert.NotRetainedAsync(async () =>
        {
            await rig.OpenAsync();
            await rig.Vm.ToggleAsync(rig.Vm.Rows.Single(r => r.Name == "a"));
            var nodes = rig.Vm.Rows.Cast<object>().ToArray();
            await rig.Workspace.CloseFolderAsync();
            await rig.Vm.RefreshCompleted;
            Assert.Empty(rig.Vm.Rows);
            return nodes;
        });
        rig.Dispose();
    }
}
