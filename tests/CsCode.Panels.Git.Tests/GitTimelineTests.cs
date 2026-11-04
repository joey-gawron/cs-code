using System.Text;
using Avalonia.Headless.XUnit;
using CsCode.Core.History;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Core.Timeline;
using CsCode.Panels.Git.Engine;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Git.Tests;

/// <summary>Git History in the Timeline (git log --follow, paged) and Discard Changes keeping the discarded files in local history.</summary>
public sealed class GitTimelineTests
{
    [Fact]
    public void The_log_parser_reads_renames_and_moves_older_commits_to_the_old_name()
    {
        static string C(string sha, string parents, string subject, string status) =>
            $"\u001e{sha}\u001f{parents}\u001fJane\u001fjane@example.com\u001f1793835329\u001f{subject}\0" + (status.Length > 0 ? "\n" + status : "");
        var output = C("4b3e3c85ef", "a910317d4a", "fourth: tweak", "M\0b.txt\0")
                     + C("a910317d4a", "c89b483419", "rename it", "R100\0a.txt\0b.txt\0")
                     + C("cafe00000a", "c89b483419 1234567890", "merge", "") // a merge: no status line, the path of the commit after it
                     + C("c89b483419", "e487d50621", "second", "M\0a.txt\0")
                     + C("e487d50621", "", "first one", "A\0a.txt\0");
        var commits = GitLogParser.Parse(Encoding.UTF8.GetBytes(output), "b.txt");
        Assert.Equal(["fourth: tweak", "rename it", "merge", "second", "first one"], commits.Select(c => c.Subject));
        Assert.Equal(["b.txt", "b.txt", "a.txt", "a.txt", "a.txt"], commits.Select(c => c.Path));
        Assert.Equal("a.txt", commits[1].OldPath);
        Assert.Null(commits[0].OldPath);
        Assert.Empty(commits[4].Parents);
        Assert.Equal(["c89b483419", "1234567890"], commits[2].Parents);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793835329), commits[0].Committed);
        Assert.Equal(("Jane", "jane@example.com"), (commits[0].Author, commits[0].Email));
    }

    private static async Task<(TestRepo Repo, TestWorkspace Workspace, GitService Git)> RepositoryAsync()
    {
        Assert.SkipUnless(TestRepo.GitAvailable, "git is not installed");
        var repo = new TestRepo();
        repo.Write("a.txt", "a\nb\n");
        repo.Commit("first one");
        repo.Write("a.txt", "a\nb\nc\n");
        repo.Commit("second");
        repo.Git("mv", "a.txt", "b.txt");
        repo.Git("commit", "-q", "-m", "rename it");
        repo.Write("b.txt", "x\nb\nc\n");
        repo.Commit("fourth: tweak");
        repo.Write("other.txt", "o\n");
        repo.Commit("unrelated");
        var ws = new TestWorkspace(false, repo.Uri);
        var git = new GitService(ws, new TestConfigurationService());
        await git.Initialization;
        Assert.Single(git.Repositories);
        return (repo, ws, git);
    }

    [Fact]
    public async Task A_files_commits_follow_its_renames_page_by_page_and_give_each_version()
    {
        var (repo, ws, git) = await RepositoryAsync();
        using var _r = repo;
        using var _w = ws;
        using var _g = git;
        using var provider = new GitTimelineProvider(git);
        var b = repo.FileUri("b.txt");
        Assert.True(provider.Handles(b));
        Assert.False(provider.Handles(new Uri(Path.Combine(Path.GetTempPath(), "elsewhere.txt"))));

        // two per page: git's own --skip gets this wrong once the skipped commits pass the rename
        var first = await provider.GetTimelineAsync(b, null, 2);
        Assert.Equal(["fourth: tweak", "rename it"], first.Items.Select(i => i.Label));
        Assert.Equal("2", first.Cursor);
        var second = await provider.GetTimelineAsync(b, first.Cursor, 2);
        Assert.Equal(["second", "first one"], second.Items.Select(i => i.Label));
        Assert.Null(second.Cursor);
        Assert.All(first.Items.Concat(second.Items), i =>
        {
            Assert.Equal(GitTimelineProvider.ProviderId, i.ProviderId);
            Assert.Equal("Test User", i.Description);
            Assert.Equal(40, i.Id.Length);
            Assert.Equal(b, i.Resource);
        });
        Assert.True(first.Items[0].Timestamp >= second.Items[^1].Timestamp);

        // each version, by the path the file had then
        var oldest = second.Items[^1];
        Assert.Equal("a\nb\n", (await provider.GetContentAsync(oldest))!.Text);
        Assert.Null(await provider.GetPreviousContentAsync(oldest)); // the first commit: nothing before it
        var rename = first.Items[1];
        Assert.Equal("a\nb\nc\n", (await provider.GetContentAsync(rename))!.Text);
        var beforeRename = await provider.GetPreviousContentAsync(rename);
        Assert.Equal("a\nb\nc\n", beforeRename!.Text);
        Assert.Equal($"a.txt ({rename.Id[..8]}^)", beforeRename.Label);
        var tweak = await provider.GetContentAsync(first.Items[0]);
        Assert.Equal("x\nb\nc\n", tweak!.Text);
        Assert.Equal($"b.txt ({first.Items[0].Id[..8]})", tweak.Label);

        // all at once, and an untracked file has none
        Assert.Equal(4, (await provider.GetTimelineAsync(b, null, 50)).Items.Count);
        repo.Write("new.txt", "n\n");
        Assert.Empty((await provider.GetTimelineAsync(repo.FileUri("new.txt"), null, 50)).Items);

        // cancellable
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await provider.GetTimelineAsync(b, null, 50, cts.Token));
    }

    [Fact]
    public async Task A_commit_tells_the_Timeline_its_items_changed()
    {
        var (repo, ws, git) = await RepositoryAsync();
        using var _r = repo;
        using var _w = ws;
        using var _g = git;
        using var provider = new GitTimelineProvider(git);
        var fired = 0;
        using var sub = provider.Changed.Subscribe(_ => Interlocked.Increment(ref fired));
        repo.Write("b.txt", "changed again\n");
        await git.Repositories[0].CommitAsync("fifth", all: true); // a mutation refreshes with history changed
        await Wait.UntilAsync(() => Volatile.Read(ref fired) > 0, because: "Changed after the commit");
    }

    /// <summary>Records what local history was asked to keep, with the file's content at that moment.</summary>
    private sealed class RecordingHistory : ILocalHistoryService
    {
        public List<(Uri Uri, string? Source, string? Content)> Added { get; } = [];
        public bool IsEnabled => true;
        public bool CanTrack(Uri resource) => true;

        public ValueTask<LocalHistoryEntry?> AddEntryAsync(Uri resource, string? source = null, CancellationToken ct = default)
        {
            lock (Added) Added.Add((resource, source, File.Exists(resource.LocalPath) ? File.ReadAllText(resource.LocalPath) : null));
            return ValueTask.FromResult<LocalHistoryEntry?>(null);
        }

        public ValueTask<IReadOnlyList<LocalHistoryEntry>> GetEntriesAsync(Uri resource, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<LocalHistoryEntry>>([]);
        public ValueTask<IReadOnlyList<Uri>> GetResourcesAsync(CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Uri>>([]);
        public ValueTask<byte[]?> ReadAsync(LocalHistoryEntry entry, CancellationToken ct = default) => ValueTask.FromResult<byte[]?>(null);
        public ValueTask<LocalHistoryEntry?> RenameEntryAsync(LocalHistoryEntry entry, string source, CancellationToken ct = default) => ValueTask.FromResult<LocalHistoryEntry?>(null);
        public ValueTask<bool> RemoveEntryAsync(LocalHistoryEntry entry, CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask RemoveAllAsync(Uri? resource, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask MoveEntriesAsync(Uri source, Uri target, CancellationToken ct = default) => ValueTask.CompletedTask;
        public void SetNextSaveSource(Uri resource, string source) { }
        public IEventSource<Uri?> Changed => EventSource.Never<Uri?>();
    }

    [AvaloniaFact]
    public async Task Discard_changes_keeps_each_file_in_local_history_before_overwriting_or_deleting_it()
    {
        Assert.SkipUnless(TestRepo.GitAvailable, "git is not installed");
        using var repo = new TestRepo();
        repo.Write("a.txt", "committed\n");
        repo.Commit("initial");
        repo.Write("a.txt", "my unsaved work\n");
        repo.Write("new.txt", "untracked work\n");
        using var ws = new TestWorkspace(false, repo.Uri);
        var history = new RecordingHistory();
        var window = new RecordingWindowService();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceService>(ws);
        services.AddSingleton<IConfigurationService>(new TestConfigurationService());
        services.AddSingleton<IWindowService>(window);
        services.AddSingleton<ICommandService>(new TestCommandService());
        services.AddSingleton<IDocumentManager>(new DocumentManager());
        services.AddSingleton<IOutputService>(new InMemoryOutputService());
        services.AddSingleton<ILocalHistoryService>(history);
        services.AddCsCodeGit();
        using var sp = services.BuildServiceProvider();
        var git = sp.GetRequiredService<GitService>();
        var host = sp.GetRequiredService<GitHost>();
        await git.Initialization;
        var r = git.Repositories[0];
        await r.RefreshAsync();
        Assert.Equal(2, r.Status.Unstaged.Count());

        window.MessageActionResponses.Enqueue("Cancel");
        await host.DiscardAsync(r, [.. r.Status.Unstaged]);
        Assert.Empty(history.Added); // nothing discarded, nothing kept

        window.MessageActionResponses.Enqueue("Discard Changes");
        await host.DiscardAsync(r, [.. r.Status.Unstaged]);
        Assert.Equal("committed\n", repo.Read("a.txt"));
        Assert.False(File.Exists(repo.Path("new.txt")));
        Assert.Equal(
            [("a.txt", LocalHistorySources.UndoDiscard, "my unsaved work\n"), ("new.txt", LocalHistorySources.UndoDiscard, "untracked work\n")],
            history.Added.Select(a => (Path.GetFileName(a.Uri.LocalPath), a.Source, a.Content)).OrderBy(a => a.Item1));
    }
}
