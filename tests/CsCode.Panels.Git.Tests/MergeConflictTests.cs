using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CsCode.Core.Contributions;
using CsCode.Core.Languages;
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

    private sealed class RecordingMergeEditors : IMergeEditorService
    {
        public List<MergeEditorRequest> Requests { get; } = new();
        public ValueTask OpenMergeEditorAsync(MergeEditorRequest request, CancellationToken ct = default) { lock (Requests) Requests.Add(request); return ValueTask.CompletedTask; }
    }

    [AvaloniaFact]
    public async Task Resolve_in_merge_editor_reads_the_three_stages_and_completing_the_merge_model_leaves_the_file_and_index_resolved()
    {
        var editors = new RecordingMergeEditors();
        using var h = await CreateConflictAsync(s => s.AddSingleton<IMergeEditorService>(editors));
        await h.Commands.ExecuteAsync(GitHost.OpenMergeEditor, h.Repo.FileUri("c.txt"));
        var request = Assert.Single(editors.Requests);
        Assert.Equal((Base, Ours, Theirs), (request.Base, request.Current, request.Incoming));
        Assert.Equal(("HEAD", "topic"), (request.CurrentLabel, request.IncomingLabel)); // the names on git's markers
        Assert.Equal("Merging: c.txt", request.Title);
        Assert.Equal(h.Repo.FileUri("c.txt"), request.Result);

        // what the merge editor does: the 3-way model, a side taken per conflict, the result saved, then Complete Merge's completion
        var merge = ThreeWayMerge.Compute(request.Base, request.Current, request.Incoming);
        var conflict = Assert.Single(merge.Conflicts);
        Assert.Single(MergeConflictParser.Parse(h.Repo.Read("c.txt"))); // as many as git found
        var resolution = new MergeResolution(merge);
        resolution.Set(conflict.ConflictIndex, MergeSides.Both, incomingFirst: true);
        h.Repo.Write("c.txt", resolution.ResultText());
        await request.Complete!(CancellationToken.None);

        Assert.Equal("line 1\ntheirs\nours\nline 3\n", h.Repo.Read("c.txt"));
        Assert.Empty(Unmerged(h));
        Assert.Equal("line 1\ntheirs\nours\nline 3\n", h.Repo.Git("show", ":c.txt"));
        await WaitFor(() => !h.Repository.Status.Entries.Any(e => e.IsConflict), "no conflicts left");
        h.Repo.Git("commit", "-q", "--no-edit");
        Assert.Equal("", h.Repo.Git("status", "--porcelain").Trim());

        // a file without conflicts says so
        await h.Commands.ExecuteAsync(GitHost.OpenMergeEditor, h.Repo.FileUri("other.txt"));
        Assert.Single(editors.Requests);
        Assert.Contains(h.Window.Messages, m => m.Message.Contains("no merge conflicts", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task The_merge_model_agrees_with_git_merge_file_on_files_with_clean_and_conflicting_changes()
    {
        Assert.SkipUnless(TestRepo.GitAvailable, "git is not installed");
        using var dir = new TempDirectory("cscode-merge-file-");
        string Run(string flag, string ours, string b, string theirs)
        {
            var paths = new[] { dir.WriteFile("current", ours), dir.WriteFile("base", b), dir.WriteFile("incoming", theirs) };
            var psi = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "merge-file", "-p", flag, paths[0], paths[1], paths[2] }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output;
        }
        static string All(ThreeWayMerge merge, MergeSides sides)
        {
            var r = new MergeResolution(merge);
            for (var k = 0; k < merge.Conflicts.Count; k++) r.Set(k, sides);
            return r.ResultText();
        }

        // conflicts far apart, clean changes on both sides: every mode agrees, and so does the number of conflicts
        const string b = "a\nb\nc\nd\ne\nf\ng\nh\ni\nj\nk\nl\nm\n";
        const string ours = "a\nB ours\nc\nd\ne\nf\ng\nH ours\ni\nj\nk\nl\nm\nadded at the end\n";
        const string theirs = "a\nB theirs\nB2 theirs\nc\nd\nE theirs\nf\ng\nH theirs\ni\nj\nk\nl\nM theirs\n";
        var merge = ThreeWayMerge.Compute(b, ours, theirs);
        Assert.Equal(Run("--ours", ours, b, theirs), All(merge, MergeSides.Current));
        Assert.Equal(Run("--theirs", ours, b, theirs), All(merge, MergeSides.Incoming));
        Assert.Equal(Run("--union", ours, b, theirs), All(merge, MergeSides.Both));
        Assert.Equal(MergeConflictParser.Parse(Run("--diff3", ours, b, theirs)).Count, merge.Conflicts.Count);

        // conflicts three lines apart: git's default (merge) style joins them into one block (the lines between go into both sides), its diff3
        // style and the model keep two; taking one side everywhere still gives git's text (a union does not: docs/SOURCE-CONTROL.md)
        const string near = "a\nb\nc\nd\ne\nf\ng\n";
        const string nearOurs = "a\nB ours\nc\nd\ne\nF ours\ng\n";
        const string nearTheirs = "A theirs\nb\nc\nd\ne\nF theirs\ng\n";
        var joined = ThreeWayMerge.Compute(near, nearOurs, nearTheirs);
        Assert.Equal(2, joined.Conflicts.Count);
        Assert.Equal(2, MergeConflictParser.Parse(Run("--diff3", nearOurs, near, nearTheirs)).Count);
        Assert.Single(MergeConflictParser.Parse(Run("-q", nearOurs, near, nearTheirs)));
        Assert.Equal(Run("--ours", nearOurs, near, nearTheirs), All(joined, MergeSides.Current));
        Assert.Equal(Run("--theirs", nearOurs, near, nearTheirs), All(joined, MergeSides.Incoming));
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task A_conflicted_file_gets_a_resolve_in_merge_editor_lens_and_its_merge_row_the_menu_item()
    {
        var registry = new LanguageFeatureRegistry();
        using var h = await CreateConflictAsync(s =>
        {
            s.AddSingleton<IMergeEditorService>(new RecordingMergeEditors());
            s.AddSingleton<ILanguageFeatureRegistry>(registry);
        });
        var conflicted = new InMemoryTextDocument(h.Repo.FileUri("c.txt"), "plaintext", h.Repo.Read("c.txt"));
        var lenses = new List<CodeLens>();
        foreach (var p in registry.GetProviders<ICodeLensProvider>(conflicted))
            if (await p.ProvideCodeLensesAsync(conflicted, CancellationToken.None) is { } found) lenses.AddRange(found);
        var lens = Assert.Single(lenses);
        Assert.Equal(("Resolve in Merge Editor", GitHost.OpenMergeEditor, 1), (lens.Command!.Title, lens.Command.CommandId, lens.Range.Start.Line));
        var clean = new InMemoryTextDocument(h.Repo.FileUri("other.txt"), "plaintext", h.Repo.Read("other.txt"));
        foreach (var p in registry.GetProviders<ICodeLensProvider>(clean)) Assert.Null(await p.ProvideCodeLensesAsync(clean, CancellationToken.None));

        var (view, vm, window) = Show(h);
        await WaitFor(() => vm.Rows.OfType<ScmFileRow>().Any(r => r.Group == ScmGroup.Merge), "merge row");
        Assert.Contains(view.MenuFor(vm.Rows.OfType<ScmFileRow>().Single(r => r.Group == ScmGroup.Merge)), e => e.Header == "Resolve in _Merge Editor");
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
