using System.ComponentModel;
using System.Runtime.CompilerServices;
using CsCode.Core.Lifecycle;
using CsCode.Panels.Git.Engine;
using CsCode.Ui.Collections;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Git.ViewModels;

/// <summary>A row of the Source Control list: a group header (Staged Changes, Changes, Merge Changes) or a file of a group.</summary>
public abstract class ScmRow
{
    public abstract string Key { get; }
}

public enum ScmGroup { Merge, Staged, Changes }

public sealed class ScmGroupRow(ScmGroup group, int count) : ScmRow
{
    public ScmGroup Group { get; } = group;
    public int Count { get; } = count;
    public string Title => Group switch { ScmGroup.Merge => "Merge Changes", ScmGroup.Staged => "Staged Changes", _ => "Changes" };
    public override string Key => "group:" + Group;
    public override bool Equals(object? obj) => obj is ScmGroupRow g && g.Group == Group && g.Count == Count;
    public override int GetHashCode() => HashCode.Combine(Group, Count);
}

public sealed class ScmFileRow(GitStatusEntry entry, ScmGroup group) : ScmRow
{
    public GitStatusEntry Entry { get; } = entry;
    public ScmGroup Group { get; } = group;
    public bool IsStagedRow => Group == ScmGroup.Staged;
    public string FileName => Path.GetFileName(Entry.Path);
    public string Folder => Path.GetDirectoryName(Entry.Path)?.Replace('\\', '/') ?? "";
    /// <summary>The letter shown at the end of the row (VS Code's): the index side for a staged row, the working-tree side otherwise.</summary>
    public char Letter => IsStagedRow ? Entry.IndexLetter : Entry.WorkTreeLetter;
    public string ColorKey => Letter switch
    {
        'M' or 'T' => "gitDecoration.modifiedResourceForeground",
        'A' or 'C' => "gitDecoration.addedResourceForeground",
        'D' => "gitDecoration.deletedResourceForeground",
        'R' or 'U' => "gitDecoration.untrackedResourceForeground",
        '!' => "gitDecoration.conflictingResourceForeground",
        _ => "foreground",
    };
    public string Tooltip => Entry.OriginalPath is { } from ? $"{from} → {Entry.Path}" : Entry.Path;
    public override string Key => $"{Group}:{Entry.Path}";
    public override bool Equals(object? obj) => obj is ScmFileRow f && f.Group == Group && f.Entry == Entry;
    public override int GetHashCode() => HashCode.Combine(Group, Entry);
}

/// <summary>
/// The Source Control window's state (UI thread): the repository shown, the commit message and Amend, the rows of its staged and unstaged
/// changes (rebuilt from each status snapshot, in one bulk update), the branch line and the busy/unavailable message. Operations go through
/// <see cref="GitHost"/>. Disposed with the view.
/// </summary>
public sealed class SourceControlViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly GitHost _host;
    private readonly IUiDispatcher _ui;
    private readonly DisposableStore _store = new();
    private IDisposable? _repoSub;
    private GitRepository? _repository;
    private string _commitMessage = "";
    private bool _amend;
    private string? _busy;
    private bool _disposed;
    private string? _messageBeforeAmend;

    public SourceControlViewModel(GitHost host, IUiDispatcher? ui = null)
    {
        _host = host;
        _ui = ui ?? AvaloniaUiDispatcher.Instance;
        _store.Add(host.Git.RepositoriesChanged.Subscribe(_ => _ui.Post(OnRepositoriesChanged)));
        if (host.Bindings is not null)
            _store.Add(host.Bindings.Changed.Subscribe(_ => _ui.Post(() => { OnChanged(nameof(Watermark)); OnChanged(nameof(CommitToolTip)); })));
        if (host.Navigation is not null)
            _store.Add(host.Navigation.ActiveDocumentChanged.Subscribe(doc => _ui.Post(() =>
            {
                // follow the active editor's repository, like VS Code's "always show repository of the active editor"
                if (doc?.Uri is { } uri && host.Git.RepositoryFor(uri) is { } r && !ReferenceEquals(r, _repository)) Repository = r;
            })));
        OnRepositoriesChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public BulkObservableCollection<ScmRow> Rows { get; } = new();

    public IReadOnlyList<GitRepository> Repositories => _host.Git.Repositories;

    public bool HasManyRepositories => Repositories.Count > 1;

    public GitRepository? Repository
    {
        get => _repository;
        set
        {
            if (ReferenceEquals(_repository, value)) return;
            _repository = value;
            _repoSub?.Dispose();
            _repoSub = value?.StatusChanged.Subscribe(_ => _ui.Post(Rebuild));
            OnChanged();
            Rebuild();
            _host.UpdateStatusBar();
        }
    }

    public string CommitMessage { get => _commitMessage; set { if (_commitMessage != value) { _commitMessage = value ?? ""; OnChanged(); OnChanged(nameof(CanCommit)); } } }

    /// <summary>Amend the last commit: the box is filled with its message (and emptied again when Amend is turned off untouched).</summary>
    public bool Amend
    {
        get => _amend;
        set
        {
            if (_amend == value) return;
            _amend = value;
            OnChanged();
            OnChanged(nameof(CommitLabel));
            OnChanged(nameof(CanCommit));
            if (value && _repository is { } repo && string.IsNullOrWhiteSpace(_commitMessage))
            {
                _messageBeforeAmend = _commitMessage;
                _ = FillLastMessageAsync(repo);
            }
            else if (!value && _messageBeforeAmend is not null) { CommitMessage = _messageBeforeAmend; _messageBeforeAmend = null; }
        }
    }

    private async Task FillLastMessageAsync(GitRepository repo)
    {
        var last = await repo.LastCommitMessageAsync().ConfigureAwait(true);
        _ui.Post(() => { if (_amend && string.IsNullOrWhiteSpace(_commitMessage) && last is not null) { CommitMessage = last; } });
    }

    public string CommitLabel => _amend ? "Amend" : (_repository?.Status.Staged.Any() == false && _repository.Status.Unstaged.Any() ? "Commit All" : "Commit");

    public bool CanCommit => _repository is not null && _busy is null && (_amend || !string.IsNullOrWhiteSpace(_commitMessage));

    public string Watermark => _repository is { } r ? $"Message ({_host.CommitKeyLabel} to commit on '{r.Status.Branch.DisplayName}')" : "Message";

    public string CommitToolTip => $"Commit ({_host.CommitKeyLabel})";

    public string BranchText => _repository is { } r ? r.Status.Branch.DisplayName : "";

    public string SyncText
    {
        get
        {
            if (_repository is not { } r) return "";
            var b = r.Status.Branch;
            if (b.Upstream is null) return b.Head is null ? "detached" : "not published";
            return b.Ahead + b.Behind == 0 ? $"up to date with {b.Upstream}" : $"{b.Behind}↓ {b.Ahead}↑ {b.Upstream}";
        }
    }

    /// <summary>The line shown instead of the lists when there is nothing to show: git missing, a remote folder, no repository, or busy.</summary>
    public string? Message => _busy ?? (_repository is not null ? null : _host.Git.Unavailable ?? (_host.Git.Repositories.Count == 0 ? "The folder currently open doesn't have a git repository." : null));

    public bool IsBusy => _busy is not null;

    public int StagedCount { get; private set; }
    public int ChangesCount { get; private set; }

    internal void SetBusy(string? text) => _ui.Post(() =>
    {
        _busy = text;
        OnChanged(nameof(IsBusy));
        OnChanged(nameof(Message));
        OnChanged(nameof(CanCommit));
    });

    internal void OnCommitted() => _ui.Post(() =>
    {
        _messageBeforeAmend = null;
        _amend = false;
        OnChanged(nameof(Amend));
        CommitMessage = "";
        OnChanged(nameof(CommitLabel));
    });

    private void OnRepositoriesChanged()
    {
        if (_disposed) return;
        var repos = _host.Git.Repositories;
        if (_repository is null || !repos.Contains(_repository)) Repository = _host.ActiveRepository() ?? repos.FirstOrDefault();
        OnChanged(nameof(Repositories));
        OnChanged(nameof(HasManyRepositories));
        OnChanged(nameof(Message));
    }

    /// <summary>Rebuilds the rows from the repository's current status (one bulk replacement; rows that did not change compare equal, so the selection survives).</summary>
    internal void Rebuild()
    {
        if (_disposed) return;
        var rows = new List<ScmRow>();
        var status = _repository?.Status ?? GitStatusSnapshot.Empty;
        var merge = status.Entries.Where(e => e.IsConflict).OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        var staged = status.Entries.Where(e => e.IsStaged).OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        var changes = status.Entries.Where(e => e.IsUnstaged && !e.IsConflict).OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        if (merge.Count > 0) { rows.Add(new ScmGroupRow(ScmGroup.Merge, merge.Count)); rows.AddRange(merge.Select(e => new ScmFileRow(e, ScmGroup.Merge))); }
        if (staged.Count > 0) { rows.Add(new ScmGroupRow(ScmGroup.Staged, staged.Count)); rows.AddRange(staged.Select(e => new ScmFileRow(e, ScmGroup.Staged))); }
        rows.Add(new ScmGroupRow(ScmGroup.Changes, changes.Count));
        rows.AddRange(changes.Select(e => new ScmFileRow(e, ScmGroup.Changes)));
        StagedCount = staged.Count;
        ChangesCount = changes.Count + merge.Count;
        if (!Rows.SequenceEqual(rows)) Rows.ReplaceAll(rows);
        OnChanged(nameof(BranchText));
        OnChanged(nameof(SyncText));
        OnChanged(nameof(Watermark));
        OnChanged(nameof(CommitLabel));
        OnChanged(nameof(CanCommit));
        OnChanged(nameof(Message));
        OnChanged(nameof(StagedCount));
        OnChanged(nameof(ChangesCount));
    }

    // ------------------------------------------------------------------------------------- actions

    public Task CommitAsync() => _host.CommitFromBoxAsync(amend: false);

    public Task StageAsync(IReadOnlyList<ScmFileRow> rows) => Run(r => _host.StageAsync(r, [.. rows.Select(x => x.Entry.Path)]));

    public Task UnstageAsync(IReadOnlyList<ScmFileRow> rows) => Run(r => r.UnstageAsync([.. rows.Select(x => x.Entry.Path)]));

    public Task DiscardAsync(IReadOnlyList<ScmFileRow> rows) => _repository is { } r ? _host.DiscardAsync(r, [.. rows.Select(x => x.Entry)]) : Task.CompletedTask;

    public Task StageAllAsync() => Run(_host.StageAllAsync);

    public Task UnstageAllAsync() => Run(r => r.UnstageAllAsync());

    public Task DiscardAllAsync() => _repository is { } r ? _host.DiscardAsync(r, [.. r.Status.Unstaged.Where(e => !e.IsConflict)]) : Task.CompletedTask;

    public Task OpenChangeAsync(ScmFileRow row) => _repository is { } r ? _host.Guard(() => _host.OpenChangeAsync(r, row.Entry, row.IsStagedRow)) : Task.CompletedTask;

    public async Task OpenFileAsync(ScmFileRow row)
    {
        if (_repository is { } r && _host.Navigation is { } nav && row.Entry.WorkTree != 'D') await nav.OpenAsync(r.ToUri(row.Entry.Path)).ConfigureAwait(true);
    }

    public Task RefreshAsync() => _repository?.RefreshAsync(historyChanged: true) ?? Task.CompletedTask;

    /// <summary>
    /// Runs one of the module's commands (pull, push, fetch, sync, checkout, create branch) on this window's repository: its root goes as the
    /// command's argument, so they do not act on the active document's repository while the window shows another one.
    /// </summary>
    public async Task RunCommandAsync(string id)
    {
        if (_host.Commands is not { } commands) return;
        if (_repository is { } r) await commands.ExecuteAsync(id, r.Root).ConfigureAwait(true);
        else await commands.ExecuteAsync(id).ConfigureAwait(true);
    }

    /// <summary>Whether a command (the editor's merge-conflict commands, the merge editor) is registered: its menu items show only then.</summary>
    public bool HasCommand(string id) => _host.Commands?.Has(id) == true;

    /// <summary>Runs a command on a row's file (its URI as the argument): Accept All Current / Incoming of a merge row.</summary>
    public async Task RunFileCommandAsync(string id, ScmFileRow row)
    {
        if (_host.Commands is { } commands && _repository is { } r) await commands.ExecuteAsync(id, r.ToUri(row.Entry.Path)).ConfigureAwait(true);
    }

    private Task Run(Func<GitRepository, Task> op) => _repository is { } r ? _host.Guard(() => op(r)) : Task.CompletedTask;

    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Dispose();
        _repoSub?.Dispose();
        PropertyChanged = null;
    }
}
