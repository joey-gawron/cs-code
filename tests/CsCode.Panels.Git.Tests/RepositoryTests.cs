using CsCode.Core.Scm;
using CsCode.Panels.Git.Engine;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Git.Tests;

/// <summary>The engine against real git repositories created in temp directories.</summary>
public sealed class RepositoryTests : IDisposable
{
    private readonly List<IDisposable> _owned = new();

    private T Own<T>(T d) where T : IDisposable { _owned.Add(d); return d; }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    }

    private async Task<(TestRepo Repo, GitService Git, TestConfigurationService Config)> OpenAsync(Action<TestRepo>? arrange = null, bool watcher = false)
    {
        Assert.SkipUnless(TestRepo.GitAvailable, "git is not installed");
        var repo = Own(new TestRepo());
        arrange?.Invoke(repo);
        var config = new TestConfigurationService();
        var ws = Own(new TestWorkspace(watcher, repo.Uri));
        var git = Own(new GitService(ws, config));
        await git.Initialization;
        return (repo, git, config);
    }

    private static void Seed(TestRepo r)
    {
        r.Write("a.txt", "one\ntwo\nthree\n");
        r.Write("src/b.cs", "class B {}\n");
        r.Write(".gitignore", "bin/\n");
        r.Commit("initial");
    }

    [Fact]
    public async Task Finds_the_repository_of_a_workspace_folder_spelled_as_the_workspace_spells_it()
    {
        var (repo, git, _) = await OpenAsync(Seed);
        var r = Assert.Single(git.Repositories);
        // the temp directory is under /var on macOS; git reports /private/var: the root keeps the workspace's spelling
        Assert.Equal(repo.Root.TrimEnd('/'), r.Root);
        Assert.Same(r, git.RepositoryFor(repo.FileUri("src/b.cs")));
        Assert.Equal("src/b.cs", r.RelativePath(repo.FileUri("src/b.cs")));
        Assert.Null(git.Unavailable);
        Assert.Equal("main", r.Status.Branch.Head);
    }

    [Fact]
    public async Task A_subfolder_opened_as_the_workspace_finds_the_enclosing_repository()
    {
        Assert.SkipUnless(TestRepo.GitAvailable, "git is not installed");
        var repo = Own(new TestRepo());
        Seed(repo);
        using var ws = new TestWorkspace(false, new Uri(repo.Path("src") + "/"));
        using var git = new GitService(ws, new TestConfigurationService());
        await git.Initialization;
        Assert.Equal(repo.Root.TrimEnd('/'), Assert.Single(git.Repositories).Root);
    }

    [Fact]
    public async Task Repositories_directly_inside_the_folder_are_found_too()
    {
        Assert.SkipUnless(TestRepo.GitAvailable, "git is not installed");
        using var parent = new TempDirectory("cscode-git-parent-");
        foreach (var name in new[] { "one", "two" })
        {
            var dir = Path.Combine(parent.Path, name);
            Directory.CreateDirectory(dir);
            TestRepo.RunGit(dir, "init", "-q", "-b", "main");
        }
        Directory.CreateDirectory(Path.Combine(parent.Path, "plain"));
        using var ws = new TestWorkspace(false, parent.Uri);
        using var git = new GitService(ws, new TestConfigurationService());
        await git.Initialization;
        Assert.Equal(["one", "two"], git.Repositories.Select(r => r.Name));
    }

    [Fact]
    public async Task Status_lists_modified_untracked_staged_deleted_and_ignored_files()
    {
        var (repo, git, _) = await OpenAsync(Seed);
        var r = git.Repositories[0];
        repo.Write("a.txt", "one\nTWO\nthree\n");
        repo.Write("new.txt", "new\n");
        repo.Write("staged.txt", "s\n");
        repo.Git("add", "staged.txt");
        File.Delete(repo.Path("src/b.cs"));
        repo.Write("bin/out.dll", "x");
        await r.RefreshAsync();

        var s = r.Status;
        Assert.Equal('M', s.Entries.Single(e => e.Path == "a.txt").WorkTreeLetter);
        Assert.True(s.Entries.Single(e => e.Path == "new.txt").IsUntracked);
        Assert.True(s.Entries.Single(e => e.Path == "staged.txt").IsStaged);
        Assert.Equal('D', s.Entries.Single(e => e.Path == "src/b.cs").WorkTreeLetter);
        Assert.Contains(s.Entries, e => e.IsIgnored && e.Path == "bin/");
        Assert.Equal(["staged.txt"], s.Staged.Select(e => e.Path));
        Assert.Equal(["a.txt", "new.txt", "src/b.cs"], s.Unstaged.Select(e => e.Path).Order());
    }

    [Fact]
    public async Task Explorer_decorations_mark_files_and_colour_their_folders_and_ignored_contents()
    {
        var (repo, git, _) = await OpenAsync(Seed);
        repo.Write("src/b.cs", "class B { int x; }\n");
        repo.Write("src/deep/new.cs", "x");
        repo.Write("bin/out.dll", "x");
        var changed = 0;
        using var sub = git.DecorationsChanged.Subscribe(_ => Interlocked.Increment(ref changed));
        await git.Repositories[0].RefreshAsync();

        Assert.True(changed > 0);
        Assert.Equal("M", git.GetDecoration(repo.FileUri("src/b.cs"), false)!.Badge);
        Assert.Equal("U", git.GetDecoration(repo.FileUri("src/deep/new.cs"), false)!.Badge);
        var folder = git.GetDecoration(new Uri(repo.Path("src")), true)!;
        Assert.Equal("gitDecoration.modifiedResourceForeground", folder.ColorKey); // the most important change inside wins over untracked
        Assert.Equal("●", folder.Badge);
        Assert.Equal("gitDecoration.untrackedResourceForeground", git.GetDecoration(new Uri(repo.Path("src/deep")), true)!.ColorKey);
        Assert.True(git.GetDecoration(repo.FileUri("bin/out.dll"), false)!.Faded);
        Assert.True(git.GetDecoration(new Uri(repo.Path("bin")), true)!.Faded);
        Assert.Null(git.GetDecoration(repo.FileUri("a.txt"), false));
        Assert.Null(git.GetDecoration(new Uri("ssh://host/x"), false));
    }

    [Fact]
    public async Task Turning_decorations_off_clears_them()
    {
        var (repo, git, config) = await OpenAsync(Seed);
        repo.Write("a.txt", "changed\n");
        await git.Repositories[0].RefreshAsync();
        Assert.NotNull(git.GetDecoration(repo.FileUri("a.txt"), false));
        config.Set(GitService.DecorationsSetting, false);
        Assert.Null(git.GetDecoration(repo.FileUri("a.txt"), false));
    }

    [Fact]
    public async Task With_two_repositories_a_slow_rebuild_never_publishes_its_older_decorations_over_a_newer_one()
    {
        Assert.SkipUnless(TestRepo.GitAvailable, "git is not installed");
        using var parent = new TempDirectory("cscode-git-parent-");
        var one = Own(new TestRepo(path: Path.Combine(parent.Path, "one")));
        var two = Own(new TestRepo(path: Path.Combine(parent.Path, "two")));
        Seed(one);
        Seed(two);
        using var ws = new TestWorkspace(false, parent.Uri);
        using var git = new GitService(ws, new TestConfigurationService());
        await git.Initialization;
        var r1 = git.Repositories.Single(r => r.Name == "one");
        var r2 = git.Repositories.Single(r => r.Name == "two");

        // the first rebuild (after repository one's refresh) is held after building its snapshot, before publishing it
        using var hold = new ManualResetEventSlim();
        using var held = new ManualResetEventSlim();
        var first = 1;
        git.DecorationsBuilt = () =>
        {
            if (Interlocked.Exchange(ref first, 0) == 0) return;
            held.Set();
            hold.Wait(TimeSpan.FromSeconds(20));
        };
        one.Write("a.txt", "changed in one\n");
        var refresh1 = Task.Run(() => r1.RefreshAsync());
        Assert.True(held.Wait(TimeSpan.FromSeconds(15)), "the first rebuild did not run");

        // meanwhile repository two changes and its refresh rebuilds (on another lane thread)
        two.Write("a.txt", "changed in two\n");
        var refresh2 = Task.Run(() => r2.RefreshAsync());
        var deadline = Environment.TickCount64 + 15_000;
        while (!r2.Status.Entries.Any(e => e.Path == "a.txt") && Environment.TickCount64 < deadline) await Task.Delay(20);
        Assert.Contains(r2.Status.Entries, e => e.Path == "a.txt");
        await Task.WhenAny(refresh2, Task.Delay(300)); // without serialization the second snapshot is published here
        hold.Set();
        await Task.WhenAll(refresh1, refresh2).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("M", git.GetDecoration(one.FileUri("a.txt"), false)?.Badge);
        Assert.Equal("M", git.GetDecoration(two.FileUri("a.txt"), false)?.Badge); // the held, older snapshot did not overwrite it
    }

    [Fact]
    public async Task Stage_unstage_discard_and_commit()
    {
        var (repo, git, _) = await OpenAsync(Seed);
        var r = git.Repositories[0];
        repo.Write("a.txt", "changed\n");
        repo.Write("new.txt", "new\n");
        await r.RefreshAsync();

        await r.StageAsync(["a.txt", "new.txt"]);
        Assert.Equal(["a.txt", "new.txt"], r.Status.Staged.Select(e => e.Path).Order());
        await r.UnstageAsync(["new.txt"]);
        Assert.True(r.Status.Entries.Single(e => e.Path == "new.txt").IsUntracked);

        await r.CommitAsync("Change a");
        Assert.DoesNotContain(r.Status.Entries, e => e.Path == "a.txt");
        Assert.Equal("Change a", (await r.LastCommitMessageAsync())!);

        await r.DiscardAsync([r.Status.Entries.Single(e => e.Path == "new.txt")]);
        Assert.False(File.Exists(repo.Path("new.txt")));

        repo.Write("src/b.cs", "broken");
        await r.RefreshAsync();
        await r.DiscardAsync([r.Status.Entries.Single(e => e.Path == "src/b.cs")]);
        Assert.Equal("class B {}\n", repo.Read("src/b.cs"));

        await r.CommitAsync("", amend: true);
        Assert.Equal("Change a", await r.LastCommitMessageAsync());
        await r.CommitAsync("Change a, amended", amend: true);
        Assert.Equal("Change a, amended", await r.LastCommitMessageAsync());
        Assert.Equal("2", repo.Git("rev-list", "--count", "HEAD").Trim());
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.CommitAsync("  "));
    }

    [Fact]
    public async Task Commit_all_stages_everything_first_and_a_failing_commit_reports_gits_message()
    {
        var (repo, git, _) = await OpenAsync(Seed);
        var r = git.Repositories[0];
        repo.Write("x.txt", "x");
        await r.RefreshAsync();
        await r.CommitAsync("Add x", all: true);
        Assert.DoesNotContain(r.Status.Entries, e => !e.IsIgnored);
        var ex = await Assert.ThrowsAsync<GitException>(() => r.CommitAsync("Nothing"));
        Assert.Contains("nothing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Initial_repository_unstages_with_rm_cached()
    {
        var (repo, git, _) = await OpenAsync();
        var r = git.Repositories[0];
        repo.Write("first.txt", "1");
        await r.RefreshAsync();
        Assert.True(r.Status.Branch.IsInitial);
        await r.StageAsync(["first.txt"]);
        Assert.Single(r.Status.Staged);
        await r.UnstageAllAsync();
        Assert.True(r.Status.Entries.Single().IsUntracked);
    }

    [Fact]
    public async Task Branches_create_switch_and_list()
    {
        var (repo, git, _) = await OpenAsync(Seed);
        var r = git.Repositories[0];
        Assert.True(await r.IsValidBranchNameAsync("feature/one"));
        Assert.False(await r.IsValidBranchNameAsync("bad..name"));
        await r.CreateBranchAsync("feature/one");
        Assert.Equal("feature/one", r.Status.Branch.Head);
        var branches = await r.BranchesAsync();
        Assert.Contains(branches, b => b.Name == "main" && !b.IsCurrent && !b.IsRemote);
        Assert.Contains(branches, b => b.Name == "feature/one" && b.IsCurrent);
        await r.CheckoutAsync(branches.Single(b => b.Name == "main"));
        Assert.Equal("main", r.Status.Branch.Head);
    }

    [Fact]
    public async Task Push_pull_and_fetch_against_a_local_remote_track_ahead_and_behind()
    {
        var (repo, git, _) = await OpenAsync(Seed);
        var r = git.Repositories[0];
        using var remoteDir = new TempDirectory("cscode-git-remote-");
        TestRepo.RunGit(remoteDir.Path, "init", "-q", "--bare", "-b", "main");
        repo.Git("remote", "add", "origin", remoteDir.Path);

        await r.PushAsync(); // no upstream yet: published with -u
        Assert.Equal("origin/main", r.Status.Branch.Upstream);
        Assert.Equal((0, 0), (r.Status.Branch.Ahead, r.Status.Branch.Behind));

        repo.Write("a.txt", "local\n");
        await r.CommitAsync("Local change", all: true);
        Assert.Equal(1, r.Status.Branch.Ahead);
        await r.PushAsync();
        Assert.Equal(0, r.Status.Branch.Ahead);

        // someone else pushes
        using var otherDir = new TempDirectory("cscode-git-other-");
        TestRepo.RunGit(otherDir.Path, "clone", "-q", remoteDir.Path, ".");
        TestRepo.Configure(otherDir.Path);
        File.WriteAllText(Path.Combine(otherDir.Path, "theirs.txt"), "t");
        TestRepo.RunGit(otherDir.Path, "add", "-A");
        TestRepo.RunGit(otherDir.Path, "commit", "-q", "-m", "Theirs");
        TestRepo.RunGit(otherDir.Path, "push", "-q");

        await r.FetchAsync();
        Assert.Equal(1, r.Status.Branch.Behind);
        await r.PullAsync();
        Assert.Equal(0, r.Status.Branch.Behind);
        Assert.True(File.Exists(repo.Path("theirs.txt")));
    }

    [Fact]
    public async Task Network_errors_surface_gits_message()
    {
        var (repo, git, _) = await OpenAsync(Seed);
        repo.Git("remote", "add", "origin", Path.Combine(repo.Dir, "does-not-exist"));
        var ex = await Assert.ThrowsAsync<GitException>(() => git.Repositories[0].PushAsync());
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Equal("push", ex.Command);
    }

    [Fact]
    public async Task Quick_diff_original_is_the_index_and_changes_when_staged()
    {
        var (repo, git, _) = await OpenAsync(Seed);
        var r = git.Repositories[0];
        var uri = repo.FileUri("a.txt");
        Assert.Equal("one\ntwo\nthree\n", (await git.GetOriginalAsync(uri))!.Text);

        var fired = 0;
        using var sub = git.OriginalChanged.Subscribe(_ => Interlocked.Increment(ref fired));
        repo.Write("a.txt", "one\n2\nthree\n");
        await r.StageAsync(["a.txt"]);
        Assert.True(fired > 0, "staging moves the original");
        Assert.Equal("one\n2\nthree\n", (await git.GetOriginalAsync(uri))!.Text);

        repo.Write("new.txt", "n");
        await r.RefreshAsync();
        Assert.Null(await git.GetOriginalAsync(repo.FileUri("new.txt"))); // untracked: no markers
        Assert.Null(await git.GetOriginalAsync(new Uri("untitled:Untitled-1")));
    }

    [Fact]
    public async Task Staging_a_range_puts_only_that_hunk_in_the_index()
    {
        var (repo, git, _) = await OpenAsync(r =>
        {
            r.Write("f.txt", string.Join('\n', Enumerable.Range(1, 20).Select(i => "line " + i)) + "\n");
            r.Commit();
        });
        var r = git.Repositories[0];
        var lines = Enumerable.Range(1, 20).Select(i => "line " + i).ToArray();
        lines[2] = "line 3 changed";
        lines[15] = "line 16 changed";
        repo.Write("f.txt", string.Join('\n', lines) + "\n");
        await r.RefreshAsync();

        Assert.True(await git.StageRangeAsync(repo.FileUri("f.txt"), 15, 15));
        var cached = repo.Git("diff", "--cached", "-U0");
        Assert.Contains("+line 16 changed", cached);
        Assert.DoesNotContain("line 3 changed", cached);
        var unstaged = repo.Git("diff", "-U0");
        Assert.Contains("+line 3 changed", unstaged);
        Assert.DoesNotContain("line 16 changed", unstaged);

        // and back out of the index
        Assert.True(await r.UnstageRangeAsync("f.txt", 15, 15));
        Assert.Equal("", repo.Git("diff", "--cached"));
    }

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private static byte[] Latin1(string s) => System.Text.Encoding.Latin1.GetBytes(s);

    [Fact]
    public async Task Staging_a_range_of_a_file_with_a_bom_keeps_the_bom_and_the_lines_it_did_not_pick()
    {
        var (repo, git, _) = await OpenAsync(r =>
        {
            r.WriteBytes("f.txt", [.. Bom, .. Latin1("one\ntwo\nthree\n")]);
            r.Commit();
        });
        var r = git.Repositories[0];
        repo.WriteBytes("f.txt", [.. Bom, .. Latin1("one\nTWO\nthree\nfour\n")]);
        await r.RefreshAsync();

        // the editor's text (no BOM: the editor keeps it apart)
        Assert.True(await r.StageRangeAsync("f.txt", "one\nTWO\nthree\nfour\n", 1, 1));
        Assert.Equal([.. Bom, .. Latin1("one\nTWO\nthree\n")], repo.GitBytes("cat-file", "-p", ":f.txt"));
        Assert.True(await r.UnstageRangeAsync("f.txt", 1, 1));
        Assert.Equal("", repo.Git("diff", "--cached"));

        // the file on disk (no open document): its BOM is not a change of line 0
        Assert.True(await git.StageRangeAsync(repo.FileUri("f.txt"), 3, 3));
        Assert.Equal([.. Bom, .. Latin1("one\ntwo\nthree\nfour\n")], repo.GitBytes("cat-file", "-p", ":f.txt"));
    }

    [Fact]
    public async Task Staging_a_range_of_a_non_utf8_file_keeps_the_bytes_of_the_lines_it_did_not_pick()
    {
        var head = Latin1("caf\u00e9\ntwo\nna\u00efve\nend\n"); // Windows-1252 / Latin-1 bytes E9 and EF
        var (repo, git, _) = await OpenAsync(r =>
        {
            r.WriteBytes("f.txt", head);
            r.WriteBytes("sjis.txt", [0x93, 0xFA, 0x96, 0x7B, 0x0A, .. Latin1("two\n")]); // Shift-JIS: a line of two characters, then ASCII
            r.Commit();
        });
        var r = git.Repositories[0];
        repo.WriteBytes("f.txt", Latin1("caf\u00e9\nTWO\nna\u00efve\nEND\n"));
        repo.WriteBytes("sjis.txt", [0x93, 0xFA, 0x96, 0x7B, 0x0A, .. Latin1("TWO\n")]);
        await r.RefreshAsync();

        // the editor's text of a Windows-1252 document
        Assert.True(await r.StageRangeAsync("f.txt", "caf\u00e9\nTWO\nna\u00efve\nEND\n", 1, 1, GitRepository.EncodingFor("windows1252")));
        Assert.Equal(Latin1("caf\u00e9\nTWO\nna\u00efve\nend\n"), repo.GitBytes("cat-file", "-p", ":f.txt"));
        Assert.True(await r.UnstageRangeAsync("f.txt", 1, 1));
        Assert.Equal(head, repo.GitBytes("cat-file", "-p", ":f.txt"));

        // the bytes on disk, in an encoding the editor does not know
        Assert.True(await git.StageRangeAsync(repo.FileUri("sjis.txt"), 1, 1));
        Assert.Equal([0x93, 0xFA, 0x96, 0x7B, 0x0A, .. Latin1("TWO\n")], repo.GitBytes("cat-file", "-p", ":sjis.txt"));
    }

    [Fact]
    public async Task Blame_of_a_line_names_the_author_and_unsaved_lines_are_uncommitted()
    {
        var (repo, git, config) = await OpenAsync(Seed);
        ILineBlameProvider blame = git;
        Assert.False(blame.IsEnabled); // off by default
        config.Set(GitService.BlameSetting, true);
        Assert.True(blame.IsEnabled);

        var b = await blame.GetLineBlameAsync(repo.FileUri("a.txt"), 1, null);
        Assert.NotNull(b);
        Assert.StartsWith("Test User, just now • initial", b.Text);

        var unsaved = await blame.GetLineBlameAsync(repo.FileUri("a.txt"), 1, "one\nedited in the editor\nthree\n");
        Assert.Equal("You, Uncommitted changes", unsaved!.Text);

        var whole = await git.Repositories[0].BlameAsync("a.txt");
        Assert.Equal(3, whole.Count);
        Assert.All(whole, l => Assert.Equal("Test User", l.Author));
    }

    [Fact]
    public async Task The_file_watcher_refreshes_the_status_after_a_change_on_disk()
    {
        var (repo, git, _) = await OpenAsync(Seed, watcher: true);
        var r = git.Repositories[0];
        var changed = new TaskCompletionSource();
        using var sub = r.StatusChanged.Subscribe(s => { if (s.Entries.Any(e => e.Path == "a.txt")) changed.TrySetResult(); });
        await Task.Delay(300); // let the OS watch start
        repo.Write("a.txt", "edited outside\n");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var history = new TaskCompletionSource();
        using var sub2 = r.HistoryChanged.Subscribe(_ => history.TrySetResult());
        repo.Git("add", "a.txt"); // the index changes on disk: the originals are stale
        await history.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Status_refreshes_are_coalesced()
    {
        var (repo, git, _) = await OpenAsync(Seed);
        var r = git.Repositories[0];
        repo.Write("a.txt", "x\n");
        var tasks = Enumerable.Range(0, 20).Select(_ => r.RefreshAsync()).ToArray();
        await Task.WhenAll(tasks);
        Assert.Contains(r.Status.Entries, e => e.Path == "a.txt");
    }

    [Fact]
    public async Task Remote_folders_get_no_repository_and_say_why()
    {
        Assert.SkipUnless(TestRepo.GitAvailable, "git is not installed");
        using var ws = new TestWorkspace(false, new Uri("ssh://me@host/home/me/src/"));
        using var git = new GitService(ws, new TestConfigurationService());
        await git.Initialization;
        Assert.Empty(git.Repositories);
        Assert.Equal(GitService.RemoteMessage, git.Unavailable);
    }

    [Fact]
    public async Task A_missing_git_is_reported_with_how_to_fix_it()
    {
        using var repo = new TestRepo(init: false);
        var config = new TestConfigurationService();
        config.Set(GitService.PathSetting, "/nonexistent/bin/git");
        using var ws = new TestWorkspace(false, repo.Uri);
        using var git = new GitService(ws, config);
        await git.Initialization;
        Assert.Empty(git.Repositories);
        Assert.Contains("/nonexistent/bin/git", git.Unavailable);
        Assert.Contains("git.path", git.Unavailable);
    }

    [Fact]
    public async Task Disabling_git_drops_the_repositories()
    {
        var (_, git, config) = await OpenAsync(Seed);
        Assert.Single(git.Repositories);
        config.Set(GitService.EnabledSetting, false);
        await git.Initialization;
        Assert.Empty(git.Repositories);
        Assert.Contains("git.enabled", git.Unavailable);
    }

    [Fact]
    public async Task Git_runs_on_the_lane_threads_and_a_cancelled_command_is_killed()
    {
        var (repo, git, _) = await OpenAsync(Seed);
        var runner = git.Runner;
        Assert.False(runner.IsLaneThread);
        using var cts = new CancellationTokenSource();
        // `git hash-object --stdin` with an open stdin would wait; a long `git log` on a fresh repo is too fast, so cancel up front
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(repo.Root, ["status"], ct: cts.Token));
        var r = await runner.RunAsync(repo.Root, ["rev-parse", "--abbrev-ref", "HEAD"]);
        Assert.Equal("main", r.Text.Trim());
    }
}
