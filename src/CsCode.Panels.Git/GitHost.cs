using System.Text;
using Avalonia.Controls;
using CsCode.Core.Contributions;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Panels.Git.Engine;
using CsCode.Panels.Git.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Git;

/// <summary>
/// The module's composition point: registers the git and compare commands (VS Code's ids), the status bar items (branch with ahead/behind, sync),
/// the Explorer's Select for Compare / Compare with Selected items and a window-focus refresh, and performs the operations the Source Control
/// window and the commands share, surfacing git's errors as messages (and in the "Git" output channel). Resolved at startup through the tool window.
/// </summary>
public sealed partial class GitHost : IDisposable
{
    public const string ViewId = "scm";
    public const string ShowScm = "workbench.view.scm";
    public const string Refresh = "git.refresh";
    public const string Commit = "git.commit";
    public const string CommitAmend = "git.commitAmend";
    public const string StageAll = "git.stageAll";
    public const string UnstageAll = "git.unstageAll";
    public const string CleanAll = "git.cleanAll";
    public const string Stage = "git.stage";
    public const string Unstage = "git.unstage";
    public const string Clean = "git.clean";
    public const string OpenChange = "git.openChange";
    public const string OpenFile = "git.openFile";
    public const string Checkout = "git.checkout";
    public const string Branch = "git.branch";
    public const string Pull = "git.pull";
    public const string Push = "git.push";
    public const string Fetch = "git.fetch";
    public const string Sync = "git.sync";
    public const string Blame = "git.blame";
    public const string ToggleBlame = "git.toggleBlame";
    public const string ShowOutput = "git.showOutput";
    public const string CompareWithSaved = "workbench.files.action.compareWithSaved";
    public const string CompareWithClipboard = "workbench.files.action.compareWithClipboard";
    public const string SelectForCompare = "selectForCompare";
    public const string CompareSelected = "compareFiles";

    private readonly IServiceProvider _sp;
    private readonly DisposableStore _store = new();
    private readonly IDisposable[] _status = new IDisposable[2];
    private readonly object _gate = new();
    private IDisposable? _repoStatusSubs;
    private Uri? _selectedForCompare;

    public GitHost(IServiceProvider services, GitService git)
    {
        _sp = services;
        Git = git;
        Windows = services.GetService<IWindowService>();
        Navigation = services.GetService<INavigationService>();
        Diff = services.GetService<IDiffEditorService>();
        Views = services.GetService<IViewActivator>();
        Keys = services.GetService<IContextKeyService>();
        StatusBar = services.GetService<IStatusBarService>();
        Config = services.GetService<IConfigurationService>();
        Commands = services.GetService<ICommandService>();
        Bindings = services.GetService<IKeybindingService>();
        if (Commands is { } commands) { RegisterCommands(commands); RegisterMergeCommands(commands); }
        if (services.GetService<IContributionRegistry>() is { } contributions) _store.Add(contributions.Contribute(ContributionSources.Builtin, AppContext.BaseDirectory, Menus));
        _store.Add(git.RepositoriesChanged.Subscribe(_ => OnRepositoriesChanged()));
        if (Navigation is not null) _store.Add(Navigation.ActiveDocumentChanged.Subscribe(_ => UpdateStatusBar()));
        // window focus: read the status again (VS Code does), changes made in a terminal or another tool show at once
        _store.Add(Avalonia.Controls.Window.IsActiveProperty.Changed.Subscribe(new FocusObserver(this)));
        OnRepositoriesChanged();
    }

    public GitService Git { get; }
    public IWindowService? Windows { get; }
    public INavigationService? Navigation { get; }
    public IDiffEditorService? Diff { get; }
    public IViewActivator? Views { get; }
    public IContextKeyService? Keys { get; }
    public IStatusBarService? StatusBar { get; }
    public IConfigurationService? Config { get; }
    public ICommandService? Commands { get; }
    public IKeybindingService? Bindings { get; }

    /// <summary>
    /// The key that commits from the message box, as the active keymap labels it on this OS (<c>⌘↩</c> on a Mac, <c>Ctrl+Enter</c> elsewhere, or the
    /// user's own binding of <c>git.commit</c>); the platform's default without a keybinding service.
    /// </summary>
    public string CommitKeyLabel => Bindings?.GetLabel(Commit) ?? (OperatingSystem.IsMacOS() ? "⌘↩" : "Ctrl+Enter");

    /// <summary>The live Source Control view-model (null while the window is closed): the commit box lives there.</summary>
    public SourceControlViewModel? LiveView { get; private set; }

    internal SourceControlViewModel CreateViewModel()
    {
        var vm = new SourceControlViewModel(this);
        LiveView = vm;
        return vm;
    }

    internal void ReleaseViewModel(SourceControlViewModel vm)
    {
        if (ReferenceEquals(LiveView, vm)) LiveView = null;
        vm.Dispose();
    }

    private sealed class FocusObserver(GitHost host) : IObserver<Avalonia.AvaloniaPropertyChangedEventArgs<bool>>
    {
        private long _last;
        public void OnNext(Avalonia.AvaloniaPropertyChangedEventArgs<bool> e)
        {
            if (!e.NewValue.GetValueOrDefault() || host.Config?.Get(GitService.AutoRefreshSetting, true) == false) return;
            var now = Environment.TickCount64;
            if (now - _last < 2000) return; // flicking between windows reads the status once
            _last = now;
            foreach (var r in host.Git.Repositories) r.ScheduleRefresh();
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    // ------------------------------------------------------------------------------------- menus

    /// <summary>The Explorer's compare items (VS Code's), shown on files.</summary>
    public static IReadOnlyList<MenuContribution> Menus { get; } =
    [
        new("explorer/context", SelectForCompare, Group: "3_compare@1", When: "!explorerResourceIsFolder") { Title = "Select for Compare" },
        new("explorer/context", CompareSelected, Group: "3_compare@2", When: "!explorerResourceIsFolder && " + ScmContextKeys.ResourceSelectedForCompare) { Title = "Compare with Selected" },
    ];

    // ------------------------------------------------------------------------------------- repositories, status bar

    /// <summary>The repository the commands act on: the active document's, else the Source Control window's, else the first.</summary>
    public GitRepository? ActiveRepository()
    {
        if (Navigation?.ActiveDocument?.Uri is { } uri && Git.RepositoryFor(uri) is { } r) return r;
        if (LiveView?.Repository is { } live) return live;
        return Git.Repositories.FirstOrDefault();
    }

    private void OnRepositoriesChanged()
    {
        lock (_gate)
        {
            _repoStatusSubs?.Dispose();
            _repoStatusSubs = Disposable.Combine([.. Git.Repositories.Select(r => r.StatusChanged.Subscribe(_ => UpdateStatusBar()))]);
        }
        Keys?.Set(ScmContextKeys.GitOpenRepositoryCount, Git.Repositories.Count);
        UpdateStatusBar();
    }

    /// <summary>The branch item (<c>$(source-control) main*</c>, click: switch branch) and the sync item (<c>$(sync) 1↓ 2↑</c>, or Publish Branch).</summary>
    internal void UpdateStatusBar()
    {
        if (StatusBar is null) return;
        var repo = ActiveRepository();
        lock (_gate)
        {
            foreach (var d in _status) d?.Dispose();
            if (repo is null) { Array.Clear(_status); return; }
            var b = repo.Status.Branch;
            var dirty = repo.Status.Entries.Any(e => e.IsStaged || e.IsUnstaged) ? "*" : "";
            var tip = $"{repo.Name} ({b.DisplayName}), Checkout Branch/Tag...";
            _status[0] = StatusBar.Set(new StatusBarItemModel("git.branch", $"$(source-control) {b.DisplayName}{dirty}", tip, Checkout, StatusBarAlignment.Left, 100));
            _status[1] = b.Upstream is null
                ? StatusBar.Set(new StatusBarItemModel("git.sync", b.Head is null ? "" : "$(arrow-up) Publish", "Publish Branch", Push, StatusBarAlignment.Left, 99, IsVisible: b.Head is not null))
                : StatusBar.Set(new StatusBarItemModel("git.sync", b.Ahead + b.Behind == 0 ? "$(sync)" : $"$(sync) {b.Behind}↓ {b.Ahead}↑",
                    $"{b.Upstream}: {b.Behind} to pull, {b.Ahead} to push. Synchronize Changes", Sync, StatusBarAlignment.Left, 99));
        }
    }

    // ------------------------------------------------------------------------------------- commands

    private void RegisterCommands(ICommandService commands)
    {
        void Reg(string id, string title, Func<CommandInvocation, Task> run, string category = "Git")
        {
            if (commands.Has(id)) return;
            _store.Add(commands.Register(id, async inv => { await run(inv).ConfigureAwait(false); return null; }, new CommandMetadata(id, title, category)));
        }
        Reg(Refresh, "Refresh", _ => Git.RefreshAllAsync());
        Reg(Commit, "Commit", _ => CommitFromBoxAsync(amend: false));
        Reg(CommitAmend, "Commit (Amend)", _ => CommitFromBoxAsync(amend: true));
        Reg(StageAll, "Stage All Changes", inv => WithRepo(inv, StageAllAsync));
        Reg(UnstageAll, "Unstage All Changes", inv => WithRepo(inv, r => r.UnstageAllAsync()));
        Reg(CleanAll, "Discard All Changes", inv => WithRepo(inv, r => DiscardAsync(r, [.. r.Status.Unstaged])));
        Reg(Stage, "Stage Changes", inv => OnFiles(inv, StageAsync));
        Reg(Unstage, "Unstage Changes", inv => OnFiles(inv, (r, paths) => r.UnstageAsync(paths)));
        Reg(Clean, "Discard Changes", inv => OnFiles(inv, (r, paths) => DiscardAsync(r, [.. r.Status.Entries.Where(e => paths.Contains(e.Path) && e.IsUnstaged)])));
        Reg(OpenChange, "Open Changes", inv => OnFiles(inv, async (r, paths) =>
        {
            foreach (var p in paths.Take(10))
                if (r.Status.Entries.FirstOrDefault(e => e.Path == p) is { } e) await OpenChangeAsync(r, e, staged: !e.IsUnstaged).ConfigureAwait(false);
        }));
        Reg(OpenFile, "Open File", inv => OnFiles(inv, async (r, paths) =>
        {
            foreach (var p in paths.Take(10)) if (Navigation is not null) await Navigation.OpenAsync(r.ToUri(p)).ConfigureAwait(false);
        }));
        Reg(Checkout, "Checkout to...", inv => WithRepo(inv, CheckoutAsync));
        Reg(Branch, "Create Branch...", inv => WithRepo(inv, CreateBranchAsync));
        Reg(Pull, "Pull", inv => WithRepo(inv, r => NetworkAsync(r, "Pulling", ct => r.PullAsync(ct))));
        Reg(Push, "Push", inv => WithRepo(inv, PushAsync));
        Reg(Fetch, "Fetch", inv => WithRepo(inv, r => NetworkAsync(r, "Fetching", ct => r.FetchAsync(ct))));
        Reg(Sync, "Sync", inv => WithRepo(inv, SyncAsync));
        Reg(Blame, "Blame (Show File Annotations)", _ => BlameActiveFileAsync());
        Reg(ToggleBlame, "Toggle Inline Blame", async _ =>
        {
            if (Config is not null) await Config.UpdateAsync(GitService.BlameSetting, !Config.Get(GitService.BlameSetting, false)).ConfigureAwait(false);
        });
        Reg(ShowOutput, "Show Git Output", async _ => { Output(); if (Views is not null) await Views.ShowViewAsync("output").ConfigureAwait(false); });
        Reg(CompareWithSaved, "Compare Active File with Saved", _ => CompareWithSavedAsync(), "File");
        Reg(CompareWithClipboard, "Compare Active File with Clipboard", _ => CompareWithClipboardAsync(), "File");
        Reg(SelectForCompare, "Select for Compare", inv => { SelectFileForCompare(UriArg(inv) ?? Navigation?.ActiveDocument?.Uri); return Task.CompletedTask; }, "File");
        Reg(CompareSelected, "Compare with Selected", inv => CompareWithSelectedAsync(UriArg(inv) ?? Navigation?.ActiveDocument?.Uri), "File");
    }

    private static Uri? UriArg(CommandInvocation inv)
    {
        if (inv.Args.Count == 0 || inv.Args[0] is null) return null;
        if (inv.Args[0] is Uri u) return u;
        var s = inv.Arg<string>(0);
        return s is not null && Uri.TryCreate(s, UriKind.Absolute, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Runs a repository command on the repository named by its first argument (a repository root, as the Source Control window passes its
    /// own), else on <see cref="ActiveRepository"/>.
    /// </summary>
    private async Task WithRepo(CommandInvocation inv, Func<GitRepository, Task> run)
    {
        await Git.Initialization.ConfigureAwait(false);
        var root = inv.Args.Count > 0 ? inv.Args[0] as string : null;
        var repo = (root is null ? null : Git.Repositories.FirstOrDefault(r => string.Equals(r.Root, root, StringComparison.Ordinal))) ?? ActiveRepository();
        if (repo is null)
        {
            await Info(Git.Unavailable ?? "There is no git repository in the workspace.").ConfigureAwait(false);
            return;
        }
        await Guard(() => run(repo)).ConfigureAwait(false);
    }

    /// <summary>Runs a command on the files given as arguments (URIs or URI strings), else on the active document's file.</summary>
    private async Task OnFiles(CommandInvocation inv, Func<GitRepository, IReadOnlyCollection<string>, Task> run)
    {
        var uris = inv.Args.Select(a => a switch { Uri u => u, string s when Uri.TryCreate(s, UriKind.Absolute, out var p) => p, _ => null }).OfType<Uri>().ToList();
        if (uris.Count == 0 && Navigation?.ActiveDocument?.Uri is { } active) uris.Add(active);
        foreach (var group in uris.GroupBy(u => Git.RepositoryFor(u)))
        {
            if (group.Key is not { } repo) continue;
            var paths = group.Select(u => repo.RelativePath(u)).OfType<string>().Where(p => p.Length > 0).ToList();
            if (paths.Count > 0) await Guard(() => run(repo, paths)).ConfigureAwait(false);
        }
    }

    /// <summary>Runs an operation, turning git's failures into an error message (with its text) instead of an unobserved exception.</summary>
    public async Task<bool> Guard(Func<Task> run)
    {
        try
        {
            await run().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Output().AppendLine($"[{DateTime.Now:HH:mm:ss}] {(ex is GitException g ? "git " + g.Command + ": " : "")}{ex.Message}");
            if (Windows is not null) _ = Windows.ShowMessageAsync(MessageSeverity.Error, "Git: " + FirstLines(ex.Message), [new MessageAction("Show Output")]).AsTask().ContinueWith(async t =>
            {
                if (t.IsCompletedSuccessfully && await t == "Show Output" && Views is not null) await Views.ShowViewAsync("output").ConfigureAwait(false);
            }, TaskScheduler.Default);
            return false;
        }
    }

    private static string FirstLines(string message)
    {
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var text = string.Join(" ", lines.Take(3));
        return text.Length > 400 ? text[..400] + "…" : text;
    }

    private IOutputChannel Output() => _sp.GetService<IOutputService>()?.GetOrCreate("Git") ?? NullChannel.Instance;

    private sealed class NullChannel : IOutputChannel
    {
        public static readonly NullChannel Instance = new();
        public string Name => "Git";
        public void Append(string text) { }
        public void AppendLine(string line) { }
        public void Clear() { }
        public string GetText() => "";
        public IEventSource<string> Appended => EventSource.Never<string>();
        public void Dispose() { }
    }

    private Task Info(string message) => Windows?.ShowMessageAsync(MessageSeverity.Info, message).AsTask() ?? Task.CompletedTask;

    // ------------------------------------------------------------------------------------- operations

    /// <summary>
    /// Commits the Source Control box's message (or asks for one when the window is closed). With nothing staged it offers to commit all changes
    /// (VS Code's smart commit; <c>git.enableSmartCommit</c> skips the question). Clears the box after a successful commit.
    /// </summary>
    public async Task CommitFromBoxAsync(bool amend)
    {
        await Git.Initialization.ConfigureAwait(false);
        var live = LiveView;
        var repo = live?.Repository ?? ActiveRepository();
        if (repo is null) { await Info(Git.Unavailable ?? "There is no git repository in the workspace.").ConfigureAwait(false); return; }
        var message = live?.CommitMessage;
        amend |= live?.Amend == true;
        if (string.IsNullOrWhiteSpace(message) && !amend)
        {
            if (Windows is null) return;
            message = await Windows.ShowInputBoxAsync(new InputBoxOptions("Commit", $"Commit message for '{repo.Status.Branch.DisplayName}'", "Message")).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(message)) return;
        }
        var all = false;
        if (!amend && !repo.Status.Staged.Any())
        {
            if (!repo.Status.Unstaged.Any()) { await Info("There are no changes to commit.").ConfigureAwait(false); return; }
            all = Config?.Get(GitSettings.EnableSmartCommit, false) == true;
            if (!all && Windows is not null)
            {
                var answer = await Windows.ShowMessageAsync(MessageSeverity.Warning, "There are no staged changes to commit. Stage all your changes and commit them directly?",
                    [new MessageAction("Yes"), new MessageAction("Always"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(false);
                if (answer is not ("Yes" or "Always")) return;
                if (answer == "Always" && Config is not null) await Config.UpdateAsync(GitSettings.EnableSmartCommit, true).ConfigureAwait(false);
                all = true;
            }
        }
        if (await Guard(() => repo.CommitAsync(message ?? "", amend, all)).ConfigureAwait(false)) live?.OnCommitted();
    }

    public async Task DiscardAsync(GitRepository repo, IReadOnlyList<GitStatusEntry> entries)
    {
        if (entries.Count == 0) return;
        var untracked = entries.Count(e => e.IsUntracked);
        var what = entries.Count == 1
            ? (entries[0].IsUntracked ? $"Are you sure you want to DELETE '{entries[0].Path}'? This is IRREVERSIBLE: the file is not in git." : $"Are you sure you want to discard the changes in '{entries[0].Path}'?")
            : $"Are you sure you want to discard the changes in {entries.Count} files?" + (untracked > 0 ? $" {untracked} untracked file(s) will be DELETED (IRREVERSIBLE)." : "");
        if (Windows is not null)
        {
            var action = untracked > 0 && entries.Count == 1 ? "Delete File" : "Discard Changes";
            var answer = await Windows.ShowMessageAsync(MessageSeverity.Warning, what, [new MessageAction(action), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(false);
            if (answer != action) return;
        }
        await Guard(() => repo.DiscardAsync(entries)).ConfigureAwait(false);
    }

    /// <summary>
    /// The diff of one status entry: a staged change is HEAD ↔ index, an unstaged one index ↔ the working file (followed live), an untracked
    /// file empty ↔ the file, a file deleted on disk index ↔ empty. A conflicted file opens as a file (its conflict markers are the change).
    /// </summary>
    public async Task OpenChangeAsync(GitRepository repo, GitStatusEntry e, bool staged)
    {
        var uri = repo.ToUri(e.Path);
        var name = Path.GetFileName(e.Path);
        if (e.IsConflict || Diff is null)
        {
            if (Navigation is not null) await Navigation.OpenAsync(uri).ConfigureAwait(false);
            return;
        }
        DiffEditorRequest request;
        if (staged)
        {
            var head = e.Index == 'A' || e.Kind == GitEntryKind.Renamed && e.OriginalPath is null ? "" : await repo.ShowAsync("HEAD", e.OriginalPath ?? e.Path).ConfigureAwait(false) ?? "";
            var index = e.Index == 'D' ? "" : await repo.ShowAsync("", e.Path).ConfigureAwait(false) ?? "";
            request = new DiffEditorRequest(new DiffSide($"{name} (HEAD)", Text: head), new DiffSide($"{name} (Index)", Text: index), $"{name} (Index)", e.Path);
        }
        else if (e.IsUntracked)
            request = new DiffEditorRequest(new DiffSide($"{name} (empty)", Text: ""), new DiffSide($"{name} (Untracked)", Uri: uri), $"{name} (Untracked)", e.Path);
        else
        {
            var index = await repo.ShowAsync("", e.Path).ConfigureAwait(false) ?? "";
            var modified = e.WorkTree == 'D' ? new DiffSide($"{name} (Deleted)", Text: "") : new DiffSide($"{name} (Working Tree)", Uri: uri);
            request = new DiffEditorRequest(new DiffSide($"{name} (Index)", Text: index), modified, $"{name} (Working Tree)", e.Path);
        }
        await Diff.OpenDiffAsync(request).ConfigureAwait(false);
    }

    private async Task CheckoutAsync(GitRepository repo)
    {
        if (Windows is null) return;
        var branches = await repo.BranchesAsync().ConfigureAwait(false);
        var local = branches.Where(b => !b.IsRemote).ToList();
        var items = new List<QuickPickItem> { new("$(add) Create new branch...", Tag: "new") };
        items.AddRange(local.Select(b => new QuickPickItem(b.Name, b.IsCurrent ? $"{b.ShortOid} (current)" : b.ShortOid, Tag: b)));
        var remote = branches.Where(b => b.IsRemote && !local.Any(l => l.Upstream == b.Name)).ToList();
        if (remote.Count > 0) items.Add(new QuickPickItem("remote branches", IsSeparator: true));
        items.AddRange(remote.Select(b => new QuickPickItem(b.Name, $"remote branch at {b.ShortOid}", Tag: b)));
        var pick = await Windows.ShowQuickPickAsync(items, new QuickPickOptions("Checkout", "Select a branch to checkout")).ConfigureAwait(false);
        if (pick?.Tag is "new") { await CreateBranchAsync(repo).ConfigureAwait(false); return; }
        if (pick?.Tag is GitBranch b && !b.IsCurrent) await Guard(() => repo.CheckoutAsync(b)).ConfigureAwait(false);
    }

    private async Task CreateBranchAsync(GitRepository repo)
    {
        if (Windows is null) return;
        var name = await Windows.ShowInputBoxAsync(new InputBoxOptions("Create Branch", "Please provide a new branch name", "Branch name",
            Validate: s => string.IsNullOrWhiteSpace(s) ? "A branch name is required" : s.Any(char.IsWhiteSpace) ? "Branch names cannot contain spaces" : null)).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (!await repo.IsValidBranchNameAsync(name).ConfigureAwait(false)) { await Info($"'{name}' is not a valid branch name.").ConfigureAwait(false); return; }
        await Guard(() => repo.CreateBranchAsync(name)).ConfigureAwait(false);
    }

    private Task PushAsync(GitRepository repo)
    {
        if (repo.Status.Branch.Head is null) return Info("Cannot push a detached HEAD: check out a branch first.");
        return NetworkAsync(repo, repo.Status.Branch.Upstream is null ? "Publishing branch" : "Pushing", async ct =>
        {
            string? remote = null;
            if (repo.Status.Branch.Upstream is null)
            {
                var remotes = await repo.RemotesAsync().ConfigureAwait(false);
                if (remotes.Count == 0) throw new InvalidOperationException("This repository has no remotes configured to publish to.");
                remote = remotes.Contains("origin") ? "origin" : remotes[0];
            }
            await repo.PushAsync(ct, remote).ConfigureAwait(false);
        });
    }

    private async Task SyncAsync(GitRepository repo)
    {
        var b = repo.Status.Branch;
        if (b.Upstream is null) { await PushAsync(repo).ConfigureAwait(false); return; }
        if (Config?.Get(GitSettings.ConfirmSync, true) != false && Windows is not null)
        {
            var answer = await Windows.ShowMessageAsync(MessageSeverity.Info, $"This action will pull and push commits from and to '{b.Upstream}'.",
                [new MessageAction("OK"), new MessageAction("OK, Don't Show Again"), new MessageAction("Cancel", IsCloseAffordance: true)]).ConfigureAwait(false);
            if (answer is not ("OK" or "OK, Don't Show Again")) return;
            if (answer == "OK, Don't Show Again" && Config is not null) await Config.UpdateAsync(GitSettings.ConfirmSync, false).ConfigureAwait(false);
        }
        await NetworkAsync(repo, "Synchronizing", async ct =>
        {
            await repo.PullAsync(ct).ConfigureAwait(false);
            await repo.PushAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Runs a network operation under a cancellable progress notification; the Source Control window shows it as busy meanwhile.</summary>
    public async Task NetworkAsync(GitRepository repo, string title, Func<CancellationToken, Task> run)
    {
        LiveView?.SetBusy(title + "…");
        try
        {
            if (Windows is null) { await Guard(() => run(CancellationToken.None)).ConfigureAwait(false); return; }
            await Windows.WithProgressAsync($"Git: {title} ({repo.Name})", cancellable: true, async (progress, ct) =>
            {
                progress.Report(title + "…");
                return await Guard(() => run(ct)).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        finally { LiveView?.SetBusy(null); }
    }

    /// <summary>Git: Blame: the active file annotated line by line (commit, author, date) in a read-only document beside it.</summary>
    private async Task BlameActiveFileAsync()
    {
        var doc = Navigation?.ActiveDocument;
        if (doc is null || Git.RepositoryFor(doc.Uri) is not { } repo || repo.RelativePath(doc.Uri) is not { Length: > 0 } rel)
        {
            await Info("Blame needs a file of a git repository in the active editor.").ConfigureAwait(false);
            return;
        }
        IReadOnlyList<BlameLine> lines = [];
        if (!await Guard(async () => lines = await repo.BlameAsync(rel, contents: doc.IsDirty ? doc.GetText() : null).ConfigureAwait(false)).ConfigureAwait(false)) return;
        if (lines.Count == 0) { await Info($"No blame for '{rel}' (not committed yet?).").ConfigureAwait(false); return; }
        var now = DateTimeOffset.Now;
        var authorWidth = Math.Min(20, lines.Max(l => l.Author.Length));
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            var who = l.IsUncommitted ? "Not committed yet" : l.Author;
            if (who.Length > authorWidth) who = who[..(authorWidth - 1)] + "…";
            sb.Append(l.IsUncommitted ? "        " : l.ShortSha).Append(' ').Append(who.PadRight(authorWidth)).Append(' ')
              .Append(l.IsUncommitted ? "          " : l.AuthorTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
              .Append(" │ ").Append(l.Content).Append('\n');
        }
        if (Diff is not null)
        {
            var name = Path.GetFileName(rel);
            // an original with neither text nor file: the diff editor shows the one side as a read-only document
            await Diff.OpenDiffAsync(new DiffEditorRequest(new DiffSide(""), new DiffSide($"Blame: {rel}", Text: sb.ToString()), $"Blame: {name}", "blame.txt")
            { RevealLine = Navigation?.ActiveCaret?.Line }).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------------------------- compare

    private async Task CompareWithSavedAsync()
    {
        var doc = Navigation?.ActiveDocument;
        if (doc is null || doc.IsUntitled || Diff is null) { await Info("Compare with Saved needs a saved file in the active editor.").ConfigureAwait(false); return; }
        string saved;
        try
        {
            var fs = _sp.GetService<IWorkspaceService>()?.FileSystem ?? throw new InvalidOperationException("No file system.");
            saved = GitRepository.Decode(await fs.ReadFileAsync(doc.Uri).ConfigureAwait(false)) ?? "(binary file)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { await Info("Could not read the saved file: " + ex.Message).ConfigureAwait(false); return; }
        var name = Path.GetFileName(doc.Uri.LocalPath);
        await Diff.OpenDiffAsync(new DiffEditorRequest(new DiffSide($"{name} (on disk)", Text: saved), new DiffSide($"{name} (in editor)", Uri: doc.Uri),
            $"{name} (on disk ↔ in editor)", doc.Uri.ToString())).ConfigureAwait(false);
    }

    private async Task CompareWithClipboardAsync()
    {
        var doc = Navigation?.ActiveDocument;
        var clipboard = _sp.GetService<IClipboardService>();
        if (doc is null || Diff is null || clipboard is null) { await Info("Compare with Clipboard needs a file in the active editor.").ConfigureAwait(false); return; }
        var text = (await clipboard.ReadAsync().ConfigureAwait(false))?.Text ?? "";
        var name = doc.IsUntitled ? doc.Uri.AbsolutePath : Path.GetFileName(doc.Uri.LocalPath);
        await Diff.OpenDiffAsync(new DiffEditorRequest(new DiffSide("Clipboard", Text: text), new DiffSide(name, Uri: doc.Uri), $"Clipboard ↔ {name}", doc.Uri.ToString())).ConfigureAwait(false);
    }

    /// <summary>Remembers <paramref name="uri"/> as the left side of the next Compare with Selected.</summary>
    public void SelectFileForCompare(Uri? uri)
    {
        _selectedForCompare = uri;
        Keys?.Set(ScmContextKeys.ResourceSelectedForCompare, uri is not null);
    }

    public Uri? SelectedForCompare => _selectedForCompare;

    private async Task CompareWithSelectedAsync(Uri? right)
    {
        if (_selectedForCompare is not { } left || right is null || Diff is null)
        {
            await Info("Select a file with 'Select for Compare' first.").ConfigureAwait(false);
            return;
        }
        var l = Path.GetFileName(left.IsFile ? left.LocalPath : left.AbsolutePath);
        var r = Path.GetFileName(right.IsFile ? right.LocalPath : right.AbsolutePath);
        await Diff.OpenDiffAsync(new DiffEditorRequest(new DiffSide(l, Uri: left), new DiffSide(r, Uri: right), $"{l} ↔ {r}", right.ToString())).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _store.Dispose();
        lock (_gate)
        {
            _repoStatusSubs?.Dispose();
            foreach (var d in _status) d?.Dispose();
        }
    }
}
