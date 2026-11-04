using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CsCode.Core.Contributions;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Panels.Git.Engine;
using CsCode.Panels.Git.ViewModels;
using CsCode.Panels.Git.Views;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Git.Tests;

/// <summary>Merge conflicts in the Source Control window over a real merge in a real repository: the merge group, staging with VS Code's check.</summary>
public sealed class MergeConflictTests
{
    internal sealed class Harness : IDisposable
    {
        public required TestRepo Repo { get; init; }
        public required TestWorkspace Workspace { get; init; }
        public required ServiceProvider Services { get; init; }
        public required GitService Git { get; init; }
        public required GitHost Host { get; init; }
        public required RecordingWindowService Window { get; init; }
        public required TestCommandService Commands { get; init; }
        public required DocumentManager Documents { get; init; }
        public GitRepository Repository => Git.Repositories[0];

        public void Dispose()
        {
            Services.Dispose();
            Workspace.Dispose();
            Repo.Dispose();
        }
    }

    /// <summary>A repository where merging <c>topic</c> into <c>main</c> conflicted in <c>c.txt</c> (and merged <c>other.txt</c> cleanly).</summary>
    internal static async Task<Harness> CreateConflictAsync(Action<IServiceCollection>? configure = null)
    {
        Assert.SkipUnless(TestRepo.GitAvailable, "git is not installed");
        var repo = new TestRepo();
        MakeConflict(repo);
        var ws = new TestWorkspace(false, repo.Uri);
        var window = new RecordingWindowService();
        var commands = new TestCommandService();
        var documents = new DocumentManager();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceService>(ws);
        services.AddSingleton<IConfigurationService>(new TestConfigurationService());
        services.AddSingleton<IWindowService>(window);
        services.AddSingleton<ICommandService>(commands);
        services.AddSingleton<IContextKeyService>(new TestContextKeyService());
        services.AddSingleton<IDocumentManager>(documents);
        services.AddSingleton<INavigationService>(new RecordingNavigationService(documents));
        services.AddSingleton<IContributionRegistry>(new ContributionRegistry());
        services.AddSingleton<IOutputService>(new InMemoryOutputService());
        configure?.Invoke(services);
        services.AddCsCodeGit();
        var sp = services.BuildServiceProvider();
        var git = sp.GetRequiredService<GitService>();
        var host = sp.GetRequiredService<GitHost>();
        await git.Initialization;
        await git.Repositories[0].RefreshAsync();
        return new Harness { Repo = repo, Workspace = ws, Services = sp, Git = git, Host = host, Window = window, Commands = commands, Documents = documents };
    }

    internal const string Base = "line 1\nbase\nline 3\n";
    internal const string Ours = "line 1\nours\nline 3\n";
    internal const string Theirs = "line 1\ntheirs\nline 3\n";

    internal static void MakeConflict(TestRepo repo)
    {
        repo.Write("c.txt", Base);
        repo.Write("other.txt", "a\nb\nc\n");
        repo.Commit("base");
        repo.Git("checkout", "-q", "-b", "topic");
        repo.Write("c.txt", Theirs);
        repo.Write("other.txt", "a\nb\nc\nfrom topic\n");
        repo.Commit("theirs");
        repo.Git("checkout", "-q", "main");
        repo.Write("c.txt", Ours);
        repo.Commit("ours");
        try { repo.Git("merge", "topic"); }
        catch (InvalidOperationException) { } // CONFLICT (content): git exits 1
        Assert.Contains("<<<<<<<", repo.Read("c.txt"), StringComparison.Ordinal);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    internal static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Timed out waiting for: " + what);
            Pump();
            await Task.Delay(15);
        }
    }

    private static (SourceControlView View, SourceControlViewModel Vm, Window Window) Show(Harness h)
    {
        var tool = h.Services.GetServices<CsCode.Ui.IToolWindow>().OfType<SourceControlToolWindow>().Single();
        var view = (SourceControlView)tool.CreateView(h.Services);
        var window = new Window { Width = 420, Height = 700, Content = view };
        window.Show();
        Pump();
        return (view, (SourceControlViewModel)view.DataContext!, window);
    }

    private static string Unmerged(Harness h) => h.Repo.Git("ls-files", "-u").Trim();

    [AvaloniaFact]
    public async Task A_conflicted_file_is_listed_under_merge_changes_and_staging_it_with_markers_asks_first()
    {
        using var h = await CreateConflictAsync();
        var (_, vm, window) = Show(h);
        await WaitFor(() => vm.Rows.OfType<ScmFileRow>().Any(r => r.Group == ScmGroup.Merge), "merge row");
        Assert.Equal(new ScmGroupRow(ScmGroup.Merge, 1), vm.Rows[0]);
        var row = vm.Rows.OfType<ScmFileRow>().Single(r => r.Group == ScmGroup.Merge);
        Assert.Equal(("c.txt", '!'), (row.Entry.Path, row.Letter));

        // markers still there: asked, and Cancel leaves it unmerged
        h.Window.MessageActionResponses.Enqueue("Cancel");
        await vm.StageAsync([row]);
        Assert.Contains(h.Window.Messages, m => m.Message == "Are you sure you want to stage c.txt with merge conflicts?");
        Assert.NotEmpty(Unmerged(h));
        Assert.True(h.Repository.Status.Entries.Single(e => e.Path == "c.txt").IsConflict);

        // the same through the command (git.stage with the file's URI)
        h.Window.MessageActionResponses.Enqueue(null); // dismissed
        await h.Commands.ExecuteAsync(GitHost.Stage, h.Repo.FileUri("c.txt"));
        Assert.NotEmpty(Unmerged(h));
        Assert.Equal(2, h.Window.Messages.Count(m => m.Message.Contains("with merge conflicts", StringComparison.Ordinal)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Staging_a_resolved_file_marks_it_resolved_without_asking()
    {
        using var h = await CreateConflictAsync();
        var (_, vm, window) = Show(h);
        await WaitFor(() => vm.Rows.OfType<ScmFileRow>().Any(r => r.Group == ScmGroup.Merge), "merge row");
        h.Repo.Write("c.txt", "line 1\nours and theirs\nline 3\n");
        await vm.StageAsync([vm.Rows.OfType<ScmFileRow>().Single(r => r.Group == ScmGroup.Merge)]);
        Assert.DoesNotContain(h.Window.Messages, m => m.Message.Contains("merge conflicts", StringComparison.Ordinal));
        Assert.Empty(Unmerged(h));
        await WaitFor(() => !vm.Rows.OfType<ScmFileRow>().Any(r => r.Group == ScmGroup.Merge), "merge group gone");
        Assert.Contains(vm.Rows.OfType<ScmFileRow>(), r => r.IsStagedRow && r.Entry.Path == "c.txt" && r.Letter == 'M');
        Assert.Equal("line 1\nours and theirs\nline 3\n", h.Repo.Git("show", ":c.txt"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Answering_yes_stages_the_markers_and_stage_all_asks_too()
    {
        using var h = await CreateConflictAsync();
        var (_, vm, window) = Show(h);
        await WaitFor(() => vm.Rows.OfType<ScmFileRow>().Any(r => r.Group == ScmGroup.Merge), "merge row");
        h.Window.MessageActionResponses.Enqueue("Cancel");
        await vm.StageAllAsync();
        Assert.NotEmpty(Unmerged(h));
        h.Window.MessageActionResponses.Enqueue("Yes");
        await vm.StageAllAsync();
        Assert.Empty(Unmerged(h));
        Assert.Contains("<<<<<<<", h.Repo.Git("show", ":c.txt"), StringComparison.Ordinal); // staged as it was, markers and all
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_merge_row_offers_accept_all_current_and_incoming_when_the_editor_registered_them()
    {
        using var h = await CreateConflictAsync();
        var calls = new List<object?[]>();
        h.Commands.Register(GitHost.AcceptAllCurrent, inv => { calls.Add([.. inv.Args]); return ValueTask.FromResult<object?>(null); });
        var (view, vm, window) = Show(h);
        await WaitFor(() => vm.Rows.OfType<ScmFileRow>().Any(r => r.Group == ScmGroup.Merge), "merge row");
        var row = vm.Rows.OfType<ScmFileRow>().Single(r => r.Group == ScmGroup.Merge);
        var menu = view.MenuFor(row);
        var current = Assert.Single(menu, e => e.Header == "Accept All C_urrent");
        Assert.DoesNotContain(menu, e => e.Header == "Accept All _Incoming"); // not registered
        await current.Run!();
        Assert.Equal(h.Repo.FileUri("c.txt"), Assert.Single(Assert.Single(calls)));
        window.Close();
    }
}
