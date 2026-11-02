using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Platform;
using CsCode.Core.Projects;
using CsCode.ProjectTemplates.Engine;

namespace CsCode.Panels.NewProject.ViewModels;

/// <summary>What the page remembers between uses (<see cref="NewProjectViewModel.StateKey"/>): the last generator, each generator's options, the Git box.</summary>
public sealed class RememberedChoices
{
    public string? Generator { get; set; }
    public bool? CreateGitRepository { get; set; }
    public Dictionary<string, Dictionary<string, string>> Options { get; set; } = [];
}

/// <summary>
/// The New Project page: the generators (each checked for its tool off the UI thread; a missing tool leaves it listed but disabled with the
/// install hint), the selected one's options, Name and Location with the "Project will be created in …" line, "Create Git repository", and
/// Create / Cancel. Validation: the generator's name rules (on the UI thread: a regex), and the target folder missing or empty (off it).
/// Create runs the generator through <see cref="ProjectCreator"/> on the pool, its steps on the status line and every line in the
/// "New Project" Output channel; then the project opens (this window when it has no folder, else the user chooses) with its main file.
/// Called on the UI thread; background results come back through the dispatcher and are dropped once the page is disposed or out of date.
/// </summary>
public sealed class NewProjectViewModel : ObservableObject, IDisposable
{
    public const string StateKey = "cscode.newProject.last";
    public const string OutputChannel = "New Project";
    /// <summary>File ▸ New Window, which session processes register; it takes the folder and file to open as arguments.</summary>
    public const string NewWindowCommand = "workbench.action.newWindow";
    public const string OpenHere = "Open in This Window";
    public const string OpenInNewWindow = "Open in New Window";
    public const string DoNotOpen = "Don't Open";

    private static readonly TimeSpan ToolCheckTimeout = TimeSpan.FromSeconds(30);

    private readonly NewProjectServices _s;
    // Cancelled at Dispose and never disposed itself: background work still running may read its token after the page is gone.
    private readonly CancellationTokenSource _life = new();
    private readonly RememberedChoices _remembered;
    private CancellationTokenSource? _run;
    private GeneratorItem? _selected;
    private string _name = "";
    private string _location = "";
    private string? _nameError;
    private string? _locationError;
    private bool _targetPending;
    private bool _optionsLoading;
    private bool _createGit;
    private bool _isBusy;
    private string? _status;
    private bool _statusIsError;
    private int _optionsVersion;
    private int _targetVersion;
    private bool _syncingVcs;
    private bool _disposed;

    public NewProjectViewModel(NewProjectServices services)
    {
        _s = services;
        _remembered = Read(services.State);
        _createGit = _remembered.CreateGitRepository ?? true;
        Generators = [.. services.Generators.Select(g => new GeneratorItem(g))];
        _selected = Generators.FirstOrDefault(g => g.Id == _remembered.Generator) ?? Generators.FirstOrDefault();
        if (_selected is not null) LoadOptions(_selected);
        Initialized = InitializeAsync();
    }

    public ObservableCollection<GeneratorItem> Generators { get; }
    public ObservableCollection<OptionItem> Options { get; } = [];

    /// <summary>Completes once the default location and name are set and every tool check has answered (tests).</summary>
    public Task Initialized { get; }

    /// <summary>Completes once the selected generator's options are shown (tests).</summary>
    public Task OptionsLoaded { get; private set; } = Task.CompletedTask;

    /// <summary>Completes once the latest target check has answered (tests).</summary>
    public Task TargetChecked { get; private set; } = Task.CompletedTask;

    public GeneratorItem? SelectedGenerator
    {
        get => _selected;
        set
        {
            if (value is null || !SetProperty(ref _selected, value)) return;
            LoadOptions(value);
            ValidateName();
            Notify();
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (!SetProperty(ref _name, value ?? "")) return;
            ValidateName();
            CheckTarget();
        }
    }

    public string Location
    {
        get => _location;
        set
        {
            if (!SetProperty(ref _location, value ?? "")) return;
            CheckTarget();
        }
    }

    /// <summary>The project folder: Location/Name, <c>~</c> expanded; empty while either is.</summary>
    public string TargetPath
    {
        get
        {
            var location = ExpandHome(_location.Trim());
            var name = _name.Trim();
            if (location.Length == 0 || name.Length == 0) return "";
            try { return Path.Combine(location, name); }
            catch (ArgumentException) { return ""; }
        }
    }

    public string Preview => TargetPath.Length > 0 ? $"Project will be created in {TargetPath}" : "";

    public string? NameError { get => _nameError; private set { if (SetProperty(ref _nameError, value)) Notify(); } }
    public string? LocationError { get => _locationError; private set { if (SetProperty(ref _locationError, value)) Notify(); } }

    public bool CreateGitRepository
    {
        get => _createGit;
        set
        {
            if (!SetProperty(ref _createGit, value) || _syncingVcs) return;
            // A generator whose own tool sets up version control (cargo --vcs) follows the box.
            if (Options.OfType<ChoiceOptionItem>().FirstOrDefault(o => o.Id == ProjectOption.VcsOptionId) is { } vcs)
                Syncing(() => vcs.Value = value ? "git" : "none");
        }
    }

    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) Notify(); } }

    public string? Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }

    /// <summary>The selected generator's tool line ("cargo 1.96.1 (…)").</summary>
    public string? ToolSummary => _selected?.ToolSummary;

    public bool HasOptions => Options.Count > 0;

    public bool CanCreate =>
        !_isBusy && !_disposed && _selected is { IsAvailable: true } && !_optionsLoading && !_targetPending
        && _nameError is null && _locationError is null && TargetPath.Length > 0;

    private void Notify()
    {
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(ToolSummary));
    }

    private void Syncing(Action change)
    {
        _syncingVcs = true;
        try { change(); }
        finally { _syncingVcs = false; }
    }

    // ---- start-up -------------------------------------------------------------------------------------------------------------------------

    private async Task InitializeAsync()
    {
        var ct = _life.Token;
        var checks = Generators.Select(item => CheckToolAsync(item, ct)).ToArray();
        var configured = _s.Configuration?.Get<string>(NewProjectHost.DefaultLocationSetting);
        var recent = _s.Workspace?.RecentFolders.Where(u => u.IsFile).Select(u => u.LocalPath).ToArray() ?? [];
        var (location, name) = await Task.Run(() =>
        {
            var location = ProjectLocations.DefaultLocation(configured, recent, _s.Home);
            return (location, ProjectLocations.UniqueName(location, "untitled"));
        }, ct).ConfigureAwait(true);
        await _s.Ui.InvokeAsync(() =>
        {
            if (_disposed) return;
            if (_location.Length == 0) Location = location;
            if (_name.Length == 0) Name = name;
        }).ConfigureAwait(true);
        await Task.WhenAll(checks).ConfigureAwait(true);
    }

    private async Task CheckToolAsync(GeneratorItem item, CancellationToken ct)
    {
        ProjectToolCheck check;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ToolCheckTimeout);
        try { check = await Task.Run(() => item.Generator.CheckToolsAsync(timeout.Token).AsTask(), timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { check = new ProjectToolCheck(false, "The tool check did not answer in time"); }
        catch (OperationCanceledException) { return; }
        await _s.Ui.InvokeAsync(() =>
        {
            if (_disposed) return;
            item.Apply(check);
            // The remembered generator's tool has gone: the first one that works is chosen instead.
            if (item == _selected && !check.IsAvailable && Generators.FirstOrDefault(g => g.IsAvailable) is { } other) SelectedGenerator = other;
            else if (item == _selected) Notify();
        }).ConfigureAwait(false);
    }

    private string ExpandHome(string path) => ProjectLocations.ExpandHome(path, _s.Home);

    // ---- options and validation -----------------------------------------------------------------------------------------------------------

    private void LoadOptions(GeneratorItem item)
    {
        var version = ++_optionsVersion;
        foreach (var old in Options) old.PropertyChanged -= OnOptionChanged;
        Options.Clear();
        OnPropertyChanged(nameof(HasOptions));
        _optionsLoading = true;
        Notify();
        var ct = _life.Token;
        OptionsLoaded = Task.Run(async () =>
        {
            IReadOnlyList<ProjectOption> options;
            try { options = await item.Generator.GetOptionsAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await _s.Ui.InvokeAsync(() => ShowOptions(item, options, version)).ConfigureAwait(false);
        }, ct);
    }

    private void ShowOptions(GeneratorItem item, IReadOnlyList<ProjectOption> options, int version)
    {
        if (_disposed || version != _optionsVersion) return; // another generator was chosen meanwhile
        _remembered.Options.TryGetValue(item.Id, out var remembered);
        foreach (var option in options)
        {
            OptionItem row = option.Kind == ProjectOptionKind.Toggle ? new ToggleOptionItem(option) : new ChoiceOptionItem(option);
            if (remembered?.TryGetValue(option.Id, out var value) == true) row.Value = value;
            row.PropertyChanged += OnOptionChanged;
            Options.Add(row);
        }
        // The Git box and a generator's own VCS choice agree from the start: the box decides.
        if (Options.OfType<ChoiceOptionItem>().FirstOrDefault(o => o.Id == ProjectOption.VcsOptionId) is { } vcs)
        {
            if (_createGit) Syncing(() => vcs.Value = "git");
            else if (vcs.Value == "git") Syncing(() => vcs.Value = "none");
        }
        _optionsLoading = false;
        OnPropertyChanged(nameof(HasOptions));
        Notify();
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingVcs || e.PropertyName != nameof(OptionItem.Value) || sender is not ChoiceOptionItem { Id: ProjectOption.VcsOptionId } vcs) return;
        Syncing(() => CreateGitRepository = vcs.Value == "git");
    }

    private void ValidateName()
    {
        var name = _name.Trim();
        NameError = name.Length == 0 ? "Enter a project name." : _selected?.Generator.ValidateName(name);
    }

    /// <summary>Checks the target folder off the UI thread; the latest check wins.</summary>
    private void CheckTarget()
    {
        OnPropertyChanged(nameof(TargetPath));
        OnPropertyChanged(nameof(Preview));
        var version = ++_targetVersion;
        var location = ExpandHome(_location.Trim());
        var target = TargetPath;
        if (location.Length == 0)
        {
            LocationError = "Choose a location.";
            return;
        }
        if (!Path.IsPathFullyQualified(location))
        {
            LocationError = "The location must be an absolute path.";
            return;
        }
        if (target.Length == 0)
        {
            LocationError = null;
            return;
        }
        _targetPending = true;
        Notify();
        TargetChecked = Task.Run(async () =>
        {
            var error = ProjectCreator.ValidateTarget(target);
            await _s.Ui.InvokeAsync(() =>
            {
                if (_disposed || version != _targetVersion) return;
                _targetPending = false;
                LocationError = error;
                Notify();
            }).ConfigureAwait(false);
        }, _life.Token);
    }

    // ---- commands ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>The folder browser for Location.</summary>
    public async Task BrowseAsync()
    {
        if (_s.Window is null || _isBusy) return;
        var start = ExpandHome(_location.Trim());
        Uri? defaultUri = Path.IsPathFullyQualified(start) ? new Uri(Path.TrimEndingDirectorySeparator(start) + "/") : null;
        var picked = await _s.Window.ShowOpenDialogAsync(new OpenDialogOptions(CanSelectFiles: false, CanSelectFolders: true, Title: "Project Location", DefaultUri: defaultUri)).ConfigureAwait(true);
        if (!_disposed && picked is [{ IsFile: true } folder, ..]) Location = Path.TrimEndingDirectorySeparator(folder.LocalPath);
    }

    /// <summary>Cancel: stops a running creation (what it made is removed); otherwise closes the page.</summary>
    public void Cancel()
    {
        if (_run is { } run)
        {
            Status = "Cancelling...";
            run.Cancel();
            return;
        }
        _ = _s.Views?.CloseViewAsync(NewProjectHost.ViewId).AsTask();
    }

    /// <summary>Creates the project, then opens it. Does nothing unless <see cref="CanCreate"/>.</summary>
    public async Task CreateAsync()
    {
        if (!CanCreate || _selected is not { } item) return;
        var name = _name.Trim();
        var target = TargetPath;
        var options = Options.ToDictionary(o => o.Id, o => o.Value, StringComparer.Ordinal);
        var git = _createGit;
        Remember(item.Id, options, git);

        var run = _run = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        IsBusy = true;
        StatusIsError = false;
        Status = $"Creating {name}...";
        var channel = _s.Output?.GetOrCreate(OutputChannel);
        channel?.AppendLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} New {item.DisplayName} project '{name}' in {target}");
        ProjectCreation outcome;
        try
        {
            var progress = new Progress(this, channel);
            outcome = await Task.Run(() => _s.Creator.CreateAsync(item.Generator, new ProjectRequest(name, target, options, git), progress, run.Token), CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            _run = null;
            run.Dispose();
        }
        await _s.Ui.InvokeAsync(() => IsBusy = false).ConfigureAwait(true);
        if (_disposed) return;

        switch (outcome.Status)
        {
            case ProjectCreationStatus.Cancelled:
                await _s.Ui.InvokeAsync(() => Status = "Cancelled.").ConfigureAwait(true);
                CheckTarget();
                return;
            case ProjectCreationStatus.Failed:
                await _s.Ui.InvokeAsync(() =>
                {
                    StatusIsError = true;
                    Status = $"{outcome.Error} (the {OutputChannel} output has the details)";
                }).ConfigureAwait(true);
                if (_s.Views is { } views) await views.ShowViewAsync("output").ConfigureAwait(true);
                CheckTarget();
                return;
        }

        var result = outcome.Result!;
        var warnings = result.Warnings.Count > 0 ? " " + string.Join(" ", result.Warnings) : "";
        await _s.Ui.InvokeAsync(() => Status = $"Created {result.Directory}.{warnings}").ConfigureAwait(true);
        await OpenAsync(name, result).ConfigureAwait(true);
    }

    /// <summary>
    /// Opens the new project: in this window when it has no folder; otherwise the user chooses this window or a new window (a new session, where
    /// File ▸ New Window exists), or not to open it. The page closes once the project is opening, then the main file opens.
    /// </summary>
    private async Task OpenAsync(string name, ProjectResult result)
    {
        if (_s.Workspace is not { } workspace) return;
        var main = result.MainFile is { } m ? Path.Combine(result.Directory, m.Replace('/', Path.DirectorySeparatorChar)) : null;
        var choice = OpenHere;
        if (workspace.Folders.Count > 0 && _s.Window is { } window)
        {
            var newWindow = _s.Commands?.Has(NewWindowCommand) == true;
            MessageAction[] actions = newWindow
                ? [new(OpenHere), new(OpenInNewWindow), new(DoNotOpen, IsCloseAffordance: true)]
                : [new(OpenHere), new(DoNotOpen, IsCloseAffordance: true)];
            choice = await window.ShowMessageAsync(MessageSeverity.Info, $"'{name}' was created in {result.Directory}. Where do you want to open it?", actions).ConfigureAwait(true) ?? DoNotOpen;
        }
        if (choice == DoNotOpen) return;

        var views = _s.Views;
        if (views is not null) await views.CloseViewAsync(NewProjectHost.ViewId).ConfigureAwait(true); // disposes this page
        if (choice == OpenInNewWindow && _s.Commands is { } commands)
        {
            await commands.ExecuteAsync(NewWindowCommand, main is null ? [result.Directory] : [result.Directory, main]).ConfigureAwait(true);
            return;
        }
        await workspace.OpenFolderAsync(new Uri(Path.TrimEndingDirectorySeparator(result.Directory) + Path.DirectorySeparatorChar)).ConfigureAwait(true);
        if (main is not null && _s.Navigation is { } navigation) await navigation.OpenAsync(new Uri(main)).ConfigureAwait(true);
    }

    // ---- remembering ------------------------------------------------------------------------------------------------------------------------

    private static RememberedChoices Read(IStateStorage? state)
    {
        try { return state?.Get<RememberedChoices>(StateKey) ?? new(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NotSupportedException) { return new(); }
    }

    private void Remember(string generator, Dictionary<string, string> options, bool git)
    {
        _remembered.Generator = generator;
        _remembered.CreateGitRepository = git;
        _remembered.Options[generator] = options;
        _s.State?.Set(StateKey, _remembered);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var option in Options) option.PropertyChanged -= OnOptionChanged;
        _run?.Cancel(); // a creation still running stops, and removes what it made
        _life.Cancel();
        OnPropertyChanged(nameof(CanCreate));
    }

    /// <summary>A generator's progress: steps on the status line (latest wins), every line in the Output channel.</summary>
    private sealed class Progress(NewProjectViewModel owner, IOutputChannel? channel) : IProjectGenerationProgress
    {
        public void Step(string message)
        {
            channel?.AppendLine("== " + message);
            owner._s.Ui.PostCoalesced("newProject.status", () => { if (!owner._disposed && owner._isBusy) owner.Status = message + "..."; });
        }

        public void Output(string line) => channel?.AppendLine(line);
    }
}
