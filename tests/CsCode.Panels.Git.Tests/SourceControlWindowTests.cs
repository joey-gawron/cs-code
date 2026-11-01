using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

/// <summary>The Source Control window, the host's commands and the status bar, headless, over a real repository.</summary>
public sealed class SourceControlWindowTests
{
    private sealed class RecordingDiffs : IDiffEditorService
    {
        public List<DiffEditorRequest> Requests { get; } = new();
        public ValueTask OpenDiffAsync(DiffEditorRequest request, CancellationToken ct = default) { lock (Requests) Requests.Add(request); return ValueTask.CompletedTask; }
    }

    private sealed class Harness : IDisposable
    {
        public required TestRepo Repo { get; init; }
        public required TestWorkspace Workspace { get; init; }
        public required ServiceProvider Services { get; init; }
        public required GitService Git { get; init; }
        public required GitHost Host { get; init; }
        public required RecordingWindowService Window { get; init; }
        public required RecordingDiffs Diffs { get; init; }
        public required InMemoryStatusBarService StatusBar { get; init; }
        public required TestCommandService Commands { get; init; }
        public required TestContextKeyService Keys { get; init; }
        public required RecordingNavigationService Navigation { get; init; }
        public required ContributionRegistry Contributions { get; init; }
        public GitRepository Repository => Git.Repositories[0];

        public void Dispose()
        {
            Services.Dispose();
            Workspace.Dispose();
            Repo.Dispose();
        }
    }

    private static async Task<Harness> CreateAsync()
    {
        Assert.SkipUnless(TestRepo.GitAvailable, "git is not installed");
        var repo = new TestRepo();
        repo.Write("a.txt", "one\ntwo\n");
        repo.Write("b.txt", "b\n");
        repo.Commit("initial");
        var ws = new TestWorkspace(false, repo.Uri);
        var window = new RecordingWindowService();
        var diffs = new RecordingDiffs();
        var status = new InMemoryStatusBarService();
        var commands = new TestCommandService();
        var keys = new TestContextKeyService();
        var documents = new DocumentManager();
        var nav = new RecordingNavigationService(documents);
        var contributions = new ContributionRegistry();
        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceService>(ws);
        services.AddSingleton<IConfigurationService>(new TestConfigurationService());
        services.AddSingleton<IWindowService>(window);
        services.AddSingleton<IDiffEditorService>(diffs);
        services.AddSingleton<IStatusBarService>(status);
        services.AddSingleton<ICommandService>(commands);
        services.AddSingleton<IContextKeyService>(keys);
        services.AddSingleton<IDocumentManager>(documents);
        services.AddSingleton<INavigationService>(nav);
        services.AddSingleton<IContributionRegistry>(contributions);
        services.AddSingleton<IOutputService>(new InMemoryOutputService());
        services.AddCsCodeGit();
        var sp = services.BuildServiceProvider();
        var git = sp.GetRequiredService<GitService>();
        var host = sp.GetRequiredService<GitHost>();
        await git.Initialization;
        return new Harness
        {
            Repo = repo, Workspace = ws, Services = sp, Git = git, Host = host, Window = window, Diffs = diffs, StatusBar = status, Commands = commands, Keys = keys,
            Navigation = nav, Contributions = contributions,
        };
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Timed out waiting for: " + what);
            Pump();
            await Task.Delay(15);
        }
    }

    private static (SourceControlView View, Window Window) Show(Harness h)
    {
        var tool = h.Services.GetServices<CsCode.Ui.IToolWindow>().OfType<SourceControlToolWindow>().Single();
        var view = (SourceControlView)tool.CreateView(h.Services);
        var window = new Window { Width = 420, Height = 700, Content = view };
        window.Show();
        Pump();
        return (view, window);
    }

    private static string[] RowText(SourceControlView view) => [.. view.List.Items.Select(r => r switch
    {
        ScmGroupRow g => $"[{g.Title} {g.Count}]",
        ScmFileRow f => $"{f.Entry.Path} {f.Letter}",
        _ => "?",
    })];

    [AvaloniaFact]
    public async Task Rows_show_staged_and_unstaged_changes_and_the_buttons_stage_and_unstage()
    {
        using var h = await CreateAsync();
        h.Repo.Write("a.txt", "one\nTWO\n");
        h.Repo.Write("new.txt", "n");
        await h.Repository.RefreshAsync();
        var (view, window) = Show(h);
        await WaitFor(() => RowText(view).Length == 3, "rows");
        Assert.Equal(["[Changes 2]", "a.txt M", "new.txt U"], RowText(view));

        var vm = (SourceControlViewModel)view.DataContext!;
        await vm.StageAsync([vm.Rows.OfType<ScmFileRow>().Single(r => r.Entry.Path == "a.txt")]);
        await WaitFor(() => RowText(view).Length == 4, "staged group");
        Assert.Equal(["[Staged Changes 1]", "a.txt M", "[Changes 1]", "new.txt U"], RowText(view).Take(4));

        await vm.UnstageAsync([vm.Rows.OfType<ScmFileRow>().Single(r => r.IsStagedRow)]);
        await WaitFor(() => !vm.Rows.OfType<ScmFileRow>().Any(r => r.IsStagedRow), "unstaged");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Ctrl_Enter_in_the_message_box_commits_the_staged_changes_and_clears_the_box()
    {
        using var h = await CreateAsync();
        h.Repo.Write("a.txt", "changed\n");
        h.Repo.Git("add", "a.txt");
        await h.Repository.RefreshAsync();
        var (view, window) = Show(h);
        var vm = (SourceControlViewModel)view.DataContext!;
        await WaitFor(() => vm.StagedCount == 1, "staged row");
        view.MessageBox.Focus();
        Assert.Equal(true, h.Keys.Get(ScmContextKeys.ScmInputFocus));
        view.MessageBox.Text = "Change a";
        Pump();
        Assert.True(view.CommitButton.IsEnabled);
        view.MessageBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control });
        await WaitFor(() => h.Repo.Git("log", "-1", "--format=%s").Trim() == "Change a", "committed");
        await WaitFor(() => vm.CommitMessage == "" && vm.StagedCount == 0, "box cleared");
        Assert.False(view.CommitButton.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Committing_with_nothing_staged_offers_to_commit_everything()
    {
        using var h = await CreateAsync();
        h.Repo.Write("b.txt", "changed\n");
        await h.Repository.RefreshAsync();
        var (view, window) = Show(h);
        var vm = (SourceControlViewModel)view.DataContext!;
        await WaitFor(() => vm.ChangesCount == 1, "row");
        Assert.Equal("Commit All", vm.CommitLabel);
        vm.CommitMessage = "All";
        h.Window.MessageActionResponses.Enqueue("Cancel");
        await vm.CommitAsync();
        Assert.Equal("initial", h.Repo.Git("log", "-1", "--format=%s").Trim());
        h.Window.MessageActionResponses.Enqueue("Yes");
        await vm.CommitAsync();
        Assert.Equal("All", h.Repo.Git("log", "-1", "--format=%s").Trim());
        Assert.Contains(h.Window.Messages, m => m.Message.Contains("no staged changes", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Discard_asks_first_and_deletes_untracked_files_only_when_confirmed()
    {
        using var h = await CreateAsync();
        h.Repo.Write("a.txt", "broken\n");
        h.Repo.Write("tmp.txt", "t");
        await h.Repository.RefreshAsync();
        var (view, window) = Show(h);
        var vm = (SourceControlViewModel)view.DataContext!;
        await WaitFor(() => vm.ChangesCount == 2, "rows");
        var rows = vm.Rows.OfType<ScmFileRow>().ToList();

        h.Window.MessageActionResponses.Enqueue("Cancel");
        await vm.DiscardAsync(rows);
        Assert.Equal("broken\n", h.Repo.Read("a.txt"));
        Assert.True(File.Exists(h.Repo.Path("tmp.txt")));
        Assert.Contains(h.Window.Messages, m => m.Message.Contains("DELETED", StringComparison.Ordinal));

        h.Window.MessageActionResponses.Enqueue("Discard Changes");
        await vm.DiscardAsync(rows);
        Assert.Equal("one\ntwo\n", h.Repo.Read("a.txt"));
        Assert.False(File.Exists(h.Repo.Path("tmp.txt")));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Opening_a_change_compares_the_right_versions()
    {
        using var h = await CreateAsync();
        h.Repo.Write("a.txt", "one\nstaged\n");
        h.Repo.Git("add", "a.txt");
        h.Repo.Write("a.txt", "one\nstaged\nworking\n");
        h.Repo.Write("u.txt", "untracked\n");
        await h.Repository.RefreshAsync();
        var (view, window) = Show(h);
        var vm = (SourceControlViewModel)view.DataContext!;
        await WaitFor(() => vm.Rows.OfType<ScmFileRow>().Count() == 3, "rows");

        await vm.OpenChangeAsync(vm.Rows.OfType<ScmFileRow>().Single(r => r.IsStagedRow));
        var staged = h.Diffs.Requests[^1];
        Assert.Equal(("one\ntwo\n", "one\nstaged\n"), (staged.Original.Text, staged.Modified.Text));
        Assert.Equal("a.txt (HEAD)", staged.Original.Label);

        await vm.OpenChangeAsync(vm.Rows.OfType<ScmFileRow>().Single(r => !r.IsStagedRow && r.Entry.Path == "a.txt"));
        var unstaged = h.Diffs.Requests[^1];
        Assert.Equal("one\nstaged\n", unstaged.Original.Text); // the index
        Assert.Equal(h.Repo.FileUri("a.txt"), unstaged.Modified.Uri); // the live working file
        Assert.Equal("a.txt", unstaged.LanguageHint);

        await vm.OpenChangeAsync(vm.Rows.OfType<ScmFileRow>().Single(r => r.Entry.Path == "u.txt"));
        var untracked = h.Diffs.Requests[^1];
        Assert.Equal("", untracked.Original.Text);
        Assert.Equal(h.Repo.FileUri("u.txt"), untracked.Modified.Uri);

        // keyboard: Enter opens the selected row's change, Space stages it
        var row = vm.Rows.OfType<ScmFileRow>().Single(r => r.Entry.Path == "u.txt");
        view.List.SelectedItem = row;
        Pump();
        var count = h.Diffs.Requests.Count;
        view.List.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await WaitFor(() => h.Diffs.Requests.Count == count + 1, "enter opens");
        view.List.SelectedItem = row;
        view.List.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
        await WaitFor(() => h.Repository.Status.Entries.Any(e => e.Path == "u.txt" && e.IsStaged), "space stages");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Status_bar_shows_the_branch_with_a_dirty_mark_and_the_sync_state()
    {
        using var h = await CreateAsync();
        await WaitFor(() => h.StatusBar.Items.Any(i => i.Id == "git.branch"), "branch item");
        Assert.Equal("$(source-control) main", h.StatusBar.Items.Single(i => i.Id == "git.branch").Text);
        Assert.Equal(GitHost.Checkout, h.StatusBar.Items.Single(i => i.Id == "git.branch").CommandId);
        Assert.Equal("$(arrow-up) Publish", h.StatusBar.Items.Single(i => i.Id == "git.sync").Text);

        h.Repo.Write("a.txt", "dirty\n");
        await h.Repository.RefreshAsync();
        await WaitFor(() => h.StatusBar.Items.Single(i => i.Id == "git.branch").Text == "$(source-control) main*", "dirty mark");

        using var remote = new TempDirectory("cscode-git-remote-");
        TestRepo.RunGit(remote.Path, "init", "-q", "--bare", "-b", "main");
        h.Repo.Git("remote", "add", "origin", remote.Path);
        await h.Commands.ExecuteAsync(GitHost.Push);
        await WaitFor(() => h.StatusBar.Items.Single(i => i.Id == "git.sync").Text == "$(sync)", "published");
        await h.Repository.CommitAsync("local", all: true);
        await WaitFor(() => h.StatusBar.Items.Single(i => i.Id == "git.sync").Text == "$(sync) 0↓ 1↑", "ahead");
        Assert.Equal(1, h.Keys.Get(ScmContextKeys.GitOpenRepositoryCount));
    }

    [AvaloniaFact]
    public async Task Commands_are_registered_and_errors_are_shown_with_gits_message()
    {
        using var h = await CreateAsync();
        foreach (var id in new[] { GitHost.Refresh, GitHost.Commit, GitHost.CommitAmend, GitHost.StageAll, GitHost.UnstageAll, GitHost.CleanAll, GitHost.Stage,
                     GitHost.Unstage, GitHost.Clean, GitHost.OpenChange, GitHost.OpenFile, GitHost.Checkout, GitHost.Branch, GitHost.Pull, GitHost.Push,
                     GitHost.Fetch, GitHost.Sync, GitHost.Blame, GitHost.ToggleBlame, GitHost.CompareWithSaved, GitHost.CompareWithClipboard,
                     GitHost.SelectForCompare, GitHost.CompareSelected })
            Assert.True(h.Commands.Has(id), id);
        Assert.Contains(h.Commands.All, c => c.Id == GitHost.Commit && c.Title == "Commit" && c.Category == "Git");

        // pull without a remote: git's own explanation reaches the user and the Git output channel
        await h.Commands.ExecuteAsync(GitHost.Pull);
        Assert.Contains(h.Window.Messages, m => m.Severity == MessageSeverity.Error && m.Message.StartsWith("Git: ", StringComparison.Ordinal));
        Assert.NotEqual("", ((InMemoryOutputService)h.Services.GetRequiredService<IOutputService>()).Text("Git"));
    }

    [AvaloniaFact]
    public async Task Branch_commands_create_and_switch_through_the_quick_pick()
    {
        using var h = await CreateAsync();
        h.Window.InputResponses.Enqueue("feature/x");
        await h.Commands.ExecuteAsync(GitHost.Branch);
        Assert.Equal("feature/x", h.Repository.Status.Branch.Head);
        // Checkout: item 0 is "create new branch", then the local branches in for-each-ref order (feature/x, main)
        h.Window.QuickPickResponses.Enqueue(2);
        await h.Commands.ExecuteAsync(GitHost.Checkout);
        Assert.Equal("main", h.Repository.Status.Branch.Head);
    }

    [AvaloniaFact]
    public async Task Select_for_compare_then_compare_with_selected_opens_both_files()
    {
        using var h = await CreateAsync();
        Assert.Contains(h.Contributions.Of<MenuContribution>().Items, m => m.Value.MenuId == "explorer/context" && m.Value.Command == GitHost.CompareSelected
            && m.Value.When == "!explorerResourceIsFolder && " + ScmContextKeys.ResourceSelectedForCompare);
        await h.Commands.ExecuteAsync(GitHost.SelectForCompare, h.Repo.FileUri("a.txt").ToString());
        Assert.Equal(true, h.Keys.Get(ScmContextKeys.ResourceSelectedForCompare));
        await h.Commands.ExecuteAsync(GitHost.CompareSelected, h.Repo.FileUri("b.txt").ToString());
        var r = Assert.Single(h.Diffs.Requests);
        Assert.Equal((h.Repo.FileUri("a.txt"), h.Repo.FileUri("b.txt")), (r.Original.Uri, r.Modified.Uri));
        Assert.Equal("a.txt ↔ b.txt", r.Title);
    }

    [AvaloniaFact]
    public async Task Compare_with_saved_puts_the_disk_copy_beside_the_open_document()
    {
        using var h = await CreateAsync();
        var doc = new InMemoryTextDocument(h.Repo.FileUri("a.txt"), "plaintext", "edited in memory\n");
        h.Navigation.ActiveDocument = new EditorDocumentAdapter(doc);
        await h.Commands.ExecuteAsync(GitHost.CompareWithSaved);
        var r = Assert.Single(h.Diffs.Requests);
        Assert.Equal("one\ntwo\n", r.Original.Text);
        Assert.Equal(h.Repo.FileUri("a.txt"), r.Modified.Uri);
    }

    [AvaloniaFact]
    public async Task Blame_command_opens_the_annotated_file_alone()
    {
        using var h = await CreateAsync();
        h.Navigation.ActiveDocument = new EditorDocumentAdapter(new InMemoryTextDocument(h.Repo.FileUri("a.txt"), "plaintext", "one\ntwo\n"));
        await h.Commands.ExecuteAsync(GitHost.Blame);
        var r = Assert.Single(h.Diffs.Requests);
        Assert.Null(r.Original.Text);
        Assert.Null(r.Original.Uri);
        Assert.Contains("Test User", r.Modified.Text);
        Assert.Contains("│ two", r.Modified.Text);
    }

    [AvaloniaFact]
    public async Task Without_a_repository_the_window_says_why()
    {
        Assert.SkipUnless(TestRepo.GitAvailable, "git is not installed");
        using var ws = new TestWorkspace(false, new Uri("ssh://me@host/src/"));
        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceService>(ws);
        services.AddCsCodeGit();
        using var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<GitService>().Initialization;
        var view = (SourceControlView)sp.GetServices<CsCode.Ui.IToolWindow>().OfType<SourceControlToolWindow>().Single().CreateView(sp);
        var window = new Window { Content = view, Width = 400, Height = 400 };
        window.Show();
        await WaitFor(() => ((SourceControlViewModel)view.DataContext!).Message == GitService.RemoteMessage, "message");
        Assert.False(view.List.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Closing_the_window_disposes_its_view_model()
    {
        using var h = await CreateAsync();
        var (view, window) = Show(h);
        Assert.NotNull(h.Host.LiveView);
        window.Content = null;
        view.Dispose();
        Assert.Null(h.Host.LiveView);
        window.Close();
    }

    /// <summary>Wraps a plain text document as the editor document the navigation service hands out.</summary>
    private sealed class EditorDocumentAdapter(InMemoryTextDocument inner) : IEditorDocument
    {
        public Uri Uri => inner.Uri;
        public string LanguageId => inner.LanguageId;
        public int Version => inner.Version;
        public bool IsDirty => true;
        public int Length => inner.Length;
        public int LineCount => inner.LineCount;
        public string GetText() => inner.GetText();
        public string GetText(TextRange range) => inner.GetText(range);
        public string GetLineText(int line) => inner.GetLineText(line);
        public int OffsetAt(TextPosition position) => inner.OffsetAt(position);
        public TextPosition PositionAt(int offset) => inner.PositionAt(offset);
        public TextPosition Validate(TextPosition position) => inner.Validate(position);
        public bool ApplyEdits(IReadOnlyList<TextEdit> edits) => inner.ApplyEdits(edits);
        public Core.Lifecycle.IEventSource<TextDocumentChangedEventArgs> Changed => inner.Changed;
        public Core.Lifecycle.IEventSource<ITextDocument> LanguageIdChanged => inner.LanguageIdChanged;
        public bool IsReadOnly => false;
        public bool IsUntitled => false;
        public string EncodingName => "utf-8";
        public string EndOfLine => "\n";
        public void SetLanguageId(string languageId) { }
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsAsync(Uri target, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Core.Lifecycle.IEventSource<IEditorDocument> Saved => Core.Lifecycle.EventSource.Never<IEditorDocument>();
        public Core.Lifecycle.IEventSource<IEditorDocument> DirtyChanged => Core.Lifecycle.EventSource.Never<IEditorDocument>();
    }
}
