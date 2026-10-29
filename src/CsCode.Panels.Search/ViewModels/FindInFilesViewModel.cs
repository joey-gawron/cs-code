using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using CsCode.Ui.Collections;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Search.ViewModels;

public enum FindFocusTarget { Query, Replace, Results }

/// <summary>What a command like <c>workbench.action.findInFiles</c> asks the panel to do (VS Code argument names).</summary>
public sealed record FindRequest
{
    public string? Query { get; init; }
    public string? Replace { get; init; }
    public string? Include { get; init; }
    public string? Exclude { get; init; }
    public bool? IsRegex { get; init; }
    public bool? MatchCase { get; init; }
    public bool? WholeWord { get; init; }
    public bool? PreserveCase { get; init; }
    public bool? UseExcludeSettingsAndIgnoreFiles { get; init; }
    public bool ShowReplace { get; init; }
    public bool TriggerSearch { get; init; }
    /// <summary>Search only inside this selection.</summary>
    public SelectionScope? InSelection { get; init; }
}

/// <summary>
/// "Find in Files". UI-thread object: it owns the query form, the flattened result tree and the commands, and talks to the engine only
/// through <see cref="SearchSession"/> events. Everything heavy (scanning, regex, decoding, the final sort) happens on engine threads; the
/// UI thread appends small immutable batches in slices of at most 4 ms and updates counts coalesced.
/// </summary>
public sealed class FindInFilesViewModel : ObservableObject, IDisposable
{
    public const string CollapseResultsKey = "search.collapseResults";
    private const double SliceMs = 4;

    private readonly SearchServices _s;
    private readonly DisposableStore _subs = new();
    private readonly SearchHistory _history;
    private readonly ReplacePreviewState _previews = new();
    private readonly Dictionary<Uri, FileRow> _files = new();
    private readonly List<FileRow> _order = new();
    private readonly ConcurrentQueue<(int Run, FileMatches File)> _incoming = new();
    private readonly Emitter<FindFocusTarget> _focus = new();
    private readonly string _countsKey = "search.counts." + Guid.NewGuid().ToString("N");
    private SearchSession? _session;
    private DisposableStore? _sessionSubs;
    private TaskCompletionSource _searchDone = NewDone(completed: true);
    private SearchQuery? _lastQuery;
    private ReplacePreviewPattern? _previewPattern;
    private SelectionScope? _selectionScope;
    private RootSet _roots;
    private int _run, _drainScheduled, _totalMatches;
    private bool _disposed;

    private string _query = "", _replaceText = "", _include = "", _exclude = "";
    private bool _matchCase, _wholeWord, _isRegex, _preserveCase, _openEditorsOnly, _useExcludeSettings = true, _useIgnoreFiles = true;
    private bool _replaceVisible, _detailsVisible, _isSearching, _isStale, _inSelection;
    private string _summary = "", _errorMessage = "", _progressText = "";
    private ResultRow? _selected;
    private bool _truncated;

    public FindInFilesViewModel(SearchServices services)
    {
        _s = services;
        _history = new SearchHistory(services.State);
        _roots = new RootSet(services.Workspace.Folders);
        _useIgnoreFiles = SearchLimits.ReadBool(services.Configuration, SearchLimits.UseIgnoreFilesKey, true);

        SearchCommand = new RelayCommand(Start, () => CanSearch);
        CancelCommand = new RelayCommand(Cancel, () => IsSearching);
        ReplaceAllCommand = new AsyncRelayCommand(ReplaceAllAsync, () => CanReplace);
        ClearCommand = new RelayCommand(Clear);
        ToggleCollapseCommand = new RelayCommand(ToggleCollapseAll);
        NextCommand = new RelayCommand(() => _ = NextResultAsync());
        PreviousCommand = new RelayCommand(() => _ = PreviousResultAsync());
        ToggleReplaceCommand = new RelayCommand(() => ReplaceVisible = !ReplaceVisible);
        ToggleDetailsCommand = new RelayCommand(() => DetailsVisible = !DetailsVisible);

        _subs.Add(services.Workspace.FoldersChanged.Subscribe(_ =>
            services.Ui.Post(() =>
            {
                _roots = new RootSet(services.Workspace.Folders);
                if (_files.Count > 0) IsStale = true; // results of the previous folder set are out of date
            })));
    }

    // -------------------------------------------------------------------------------------- form state

    public string Query
    {
        get => _query;
        set
        {
            if (!SetProperty(ref _query, value)) return;
            OnPropertyChanged(nameof(CanSearch));
            (SearchCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }
    }

    public string ReplaceText
    {
        get => _replaceText;
        set { if (SetProperty(ref _replaceText, value)) RefreshReplacePreview(); }
    }

    public string Include { get => _include; set => SetProperty(ref _include, value); }
    public string Exclude { get => _exclude; set => SetProperty(ref _exclude, value); }
    public bool MatchCase { get => _matchCase; set { if (SetProperty(ref _matchCase, value)) RefreshReplacePreview(); } }
    public bool WholeWord { get => _wholeWord; set { if (SetProperty(ref _wholeWord, value)) RefreshReplacePreview(); } }
    public bool IsRegex { get => _isRegex; set { if (SetProperty(ref _isRegex, value)) RefreshReplacePreview(); } }
    public bool PreserveCase { get => _preserveCase; set { if (SetProperty(ref _preserveCase, value)) RefreshReplacePreview(); } }
    public bool OpenEditorsOnly { get => _openEditorsOnly; set => SetProperty(ref _openEditorsOnly, value); }
    public bool UseExcludeSettings { get => _useExcludeSettings; set => SetProperty(ref _useExcludeSettings, value); }
    public bool UseIgnoreFiles { get => _useIgnoreFiles; set => SetProperty(ref _useIgnoreFiles, value); }
    public bool InSelection { get => _inSelection; private set => SetProperty(ref _inSelection, value); }

    public bool ReplaceVisible
    {
        get => _replaceVisible;
        set { if (SetProperty(ref _replaceVisible, value)) { RefreshReplacePreview(); OnPropertyChanged(nameof(CanReplace)); (ReplaceAllCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged(); } }
    }

    public bool DetailsVisible { get => _detailsVisible; set => SetProperty(ref _detailsVisible, value); }

    public bool CanSearch => !string.IsNullOrEmpty(Query);
    public bool CanReplace => ReplaceVisible && _totalMatches > 0 && !IsReplacing && !IsSearching;
    public bool IsReplacing { get; private set; }

    // ---------------------------------------------------------------------------------------- results

    public BulkObservableCollection<ResultRow> Rows { get; } = new();
    public ReplacePreviewState Previews => _previews;
    public SearchHistory History => _history;

    public ResultRow? SelectedRow { get => _selected; set => SetProperty(ref _selected, value); }
    public bool IsSearching { get => _isSearching; private set { if (SetProperty(ref _isSearching, value)) { (CancelCommand as RelayCommand)?.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanReplace)); } } }
    /// <summary>The summary line: "12 results in 4 files — 38 ms", "Searching…", an error, "truncated at 20,000".</summary>
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public bool IsStale { get => _isStale; private set => SetProperty(ref _isStale, value); }
    public bool Truncated { get => _truncated; private set => SetProperty(ref _truncated, value); }
    public int TotalMatches => _totalMatches;
    public int TotalFiles => _order.Count;
    public bool HasResults => _totalMatches > 0;
    public SearchCompletion? LastCompletion { get; private set; }

    public ICommand SearchCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ReplaceAllCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ToggleCollapseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand ToggleReplaceCommand { get; }
    public ICommand ToggleDetailsCommand { get; }

    /// <summary>Focus requested before a view was attached; the view consumes (and clears) it on attach.</summary>
    public FindFocusTarget? PendingFocus { get; set; }

    /// <summary>Asks the view to move keyboard focus (raised by commands such as Find in Files).</summary>
    public IEventSource<FindFocusTarget> FocusRequested => _focus;

    /// <summary>Completes when the current search is over and every result has been applied to <see cref="Rows"/> (tests, scripting).</summary>
    public Task WaitForSearchAsync() => _searchDone.Task;

    private static TaskCompletionSource NewDone(bool completed)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) tcs.SetResult();
        return tcs;
    }

    // ------------------------------------------------------------------------------------- requests

    /// <summary>Applies a request from a command: fills the form, optionally shows replace, optionally runs the search.</summary>
    public void Apply(FindRequest r)
    {
        if (r.Query is { } q) Query = q;
        if (r.Replace is { } rep) { ReplaceText = rep; ReplaceVisible = true; }
        if (r.ShowReplace) ReplaceVisible = true;
        if (r.Include is { } inc) { Include = inc; DetailsVisible = true; }
        if (r.Exclude is { } exc) { Exclude = exc; DetailsVisible = true; }
        if (r.IsRegex is { } re) IsRegex = re;
        if (r.MatchCase is { } mc) MatchCase = mc;
        if (r.WholeWord is { } ww) WholeWord = ww;
        if (r.PreserveCase is { } pc) PreserveCase = pc;
        if (r.UseExcludeSettingsAndIgnoreFiles is { } ue) { UseExcludeSettings = ue; UseIgnoreFiles = ue; }
        _selectionScope = r.InSelection;
        InSelection = r.InSelection is not null;
        var target = ReplaceVisible && r.ShowReplace ? FindFocusTarget.Replace : FindFocusTarget.Query;
        PendingFocus = target; // a view that is not attached yet picks it up when it appears
        _focus.Fire(target);
        if (r.TriggerSearch && CanSearch) Start();
    }

    // ---------------------------------------------------------------------------------------- search

    public SearchQuery BuildQuery() => new(Query, IsRegex, MatchCase, WholeWord, MultiLine: false,
        Include: Include, Exclude: Exclude, UseExcludeSettings: UseExcludeSettings, UseIgnoreFiles: UseIgnoreFiles,
        OpenEditorsOnly: OpenEditorsOnly, Roots: null, Selection: _selectionScope);

    /// <summary>Starts (or restarts) the search with the current form. Returns immediately; results stream in.</summary>
    public void Start()
    {
        if (_disposed || !CanSearch) return;
        StopSession();
        var query = BuildQuery();
        _lastQuery = query;
        _history.Add(Query);
        ResetResults();
        ErrorMessage = "";
        IsStale = false;

        if (_s.Workspace.Folders.Count == 0 && !OpenEditorsOnly && query.Selection is null)
        {
            Summary = "No folder is open. Open a folder to search it.";
            return;
        }

        var run = ++_run;
        IsSearching = true;
        Summary = "Searching…";
        ProgressText = "";
        _searchDone = NewDone(completed: false);
        var subs = new DisposableStore();
        _sessionSubs = subs;
        // Subscribed before the session runs (an invalid regex completes on the coordinator at once, before Start could return, and a late
        // subscriber would never hear of it: the panel would stay "Searching…"). Engine threads: only enqueue and post here.
        _session = _s.Engine.Start(query, session =>
        {
            subs.Add(session.Batches.Subscribe(b => OnBatch(run, b)));
            subs.Add(session.Progress.Subscribe(p => _s.Ui.PostCoalesced(_countsKey + ".p", () => { if (run == _run) ShowProgress(p); }, UiPriority.Background)));
            subs.Add(session.Completed.Subscribe(c => _s.Ui.Post(() => OnCompleted(run, c), UiPriority.Background)));
        });
    }

    public void Cancel()
    {
        _session?.Cancel();
    }

    private void StopSession()
    {
        _sessionSubs?.Dispose();
        _sessionSubs = null;
        _session?.Cancel();
        _session = null;
        _run++; // anything still in flight for the old run is dropped by the drain
        if (!_searchDone.Task.IsCompleted) _searchDone.TrySetResult();
    }

    private void OnBatch(int run, SearchBatch batch)
    {
        foreach (var f in batch.Files) _incoming.Enqueue((run, f));
        ScheduleDrain();
    }

    private void ScheduleDrain()
    {
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0) _s.Ui.Post(Drain, UiPriority.Background);
    }

    /// <summary>Appends queued files for at most about 4 ms, then yields to the dispatcher (input and rendering get their turn).</summary>
    private void Drain()
    {
        var start = Stopwatch.GetTimestamp();
        var budget = (long)(SliceMs * Stopwatch.Frequency / 1000.0);
        var applied = false;
        while (_incoming.TryDequeue(out var item))
        {
            if (item.Run != _run || _disposed) continue; // a cancelled or superseded search
            AppendFile(item.File);
            applied = true;
            if (Stopwatch.GetTimestamp() - start >= budget) break;
        }
        Volatile.Write(ref _drainScheduled, 0);
        if (applied) _s.Ui.PostCoalesced(_countsKey, PublishCounts, UiPriority.Background);
        if (!_incoming.IsEmpty) ScheduleDrain();
        else TryFinish();
    }

    private void AppendFile(FileMatches f)
    {
        var collapsed = CollapseResultsMode() == "alwaysCollapse";
        if (!_files.TryGetValue(f.Uri, out var row))
        {
            row = NewFileRow(f);
            row.IsExpanded = !collapsed;
            _files[f.Uri] = row;
            _order.Add(row);
            Rows.Add(row);
        }
        var added = new List<MatchRow>(f.Matches.Count);
        foreach (var m in f.Matches) added.Add(new MatchRow(row, m, _previews));
        var insertAt = row.IsExpanded ? Rows.IndexOf(row) + 1 + row.Matches.Count : -1;
        row.Matches.AddRange(added);
        row.MatchCount = row.Matches.Count;
        _totalMatches += added.Count;
        if (row.IsExpanded)
        {
            if (insertAt >= Rows.Count) Rows.AddRange(added);
            else Rows.InsertRange(insertAt, added);
        }
    }

    private FileRow NewFileRow(FileMatches f)
    {
        var rel = _roots.ToRelative(f.Uri);
        var slash = rel.LastIndexOf('/');
        var name = slash >= 0 ? rel[(slash + 1)..] : rel;
        var dir = slash > 0 ? rel[1..slash] : "";
        if (!f.Uri.IsFile) { name = f.Uri.Scheme == "untitled" ? f.Uri.AbsolutePath : Path.GetFileName(Uri.UnescapeDataString(f.Uri.AbsolutePath)); dir = f.Uri.IsFile ? dir : f.Uri.Scheme; }
        return new FileRow(f.Uri, string.IsNullOrEmpty(name) ? f.Uri.ToString() : name, dir) { FromMemory = f.FromMemory, DocumentVersion = f.DocumentVersion };
    }

    private string CollapseResultsMode() =>
        _s.Configuration?.GetRaw(CollapseResultsKey) is { ValueKind: System.Text.Json.JsonValueKind.String } e ? e.GetString() ?? "auto" : "auto";

    private void PublishCounts()
    {
        OnPropertyChanged(nameof(TotalMatches));
        OnPropertyChanged(nameof(TotalFiles));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(CanReplace));
        (ReplaceAllCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        if (IsSearching && _session is not null)
            Summary = $"Searching… {_totalMatches:N0} result{(_totalMatches == 1 ? "" : "s")} in {_order.Count:N0} file{(_order.Count == 1 ? "" : "s")}";
    }

    private void ShowProgress(SearchProgress p) => ProgressText = $"{p.FilesScanned:N0} files searched";

    private SearchCompletion? _pendingCompletion;

    private void OnCompleted(int run, SearchCompletion c)
    {
        if (run != _run || _disposed) return;
        _pendingCompletion = c;
        TryFinish();
    }

    /// <summary>Finishes once the engine is done AND every queued result has been applied.</summary>
    private void TryFinish()
    {
        if (_pendingCompletion is not { } c || !_incoming.IsEmpty) return;
        _pendingCompletion = null;
        var run = _run;
        LastCompletion = c;
        _session = null;
        _sessionSubs?.Dispose();
        _sessionSubs = null;
        IsSearching = false;
        Truncated = c.Stats.Truncated;
        PublishCounts();
        ProgressText = "";
        Summary = SummaryText(c);
        ErrorMessage = c.Outcome == SearchOutcome.Failed ? c.Error ?? "The search failed." : "";

        // Sorting up to 20k files is not UI work: do it on a worker and apply the new order in one step.
        _ = SortAsync(_order.ToArray(), run);
    }

    private async Task SortAsync(FileRow[] snapshot, int run)
    {
        var sorted = await Task.Run(() => snapshot.OrderBy(f => f.Directory, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray()).ConfigureAwait(false);
        _s.Ui.Post(() =>
        {
            if (run == _run && !_disposed) ApplySortedOrder(sorted);
            _searchDone.TrySetResult();
        });
    }

    private void ApplySortedOrder(FileRow[] sorted)
    {
        _order.Clear();
        _order.AddRange(sorted);
        var keep = SelectedRow;
        Rows.ReplaceAll(Flatten(_order));
        if (keep is not null && Rows.Contains(keep)) SelectedRow = keep;
    }

    private static IEnumerable<ResultRow> Flatten(IEnumerable<FileRow> files)
    {
        foreach (var f in files)
        {
            yield return f;
            if (!f.IsExpanded) continue;
            foreach (var m in f.Matches) yield return m;
        }
    }

    private string SummaryText(SearchCompletion c)
    {
        var s = c.Stats;
        var time = s.Elapsed.TotalSeconds >= 1 ? $"{s.Elapsed.TotalSeconds:F1} s" : $"{Math.Max(1, (int)s.Elapsed.TotalMilliseconds)} ms";
        var results = $"{_totalMatches:N0} result{(_totalMatches == 1 ? "" : "s")} in {_order.Count:N0} file{(_order.Count == 1 ? "" : "s")}";
        return c.Outcome switch
        {
            SearchOutcome.Cancelled => $"Search cancelled — {results} so far",
            SearchOutcome.Truncated => $"{results} — truncated at {_totalMatches:N0}, refine the search — {time}",
            SearchOutcome.Failed => c.Error ?? "The search failed.",
            _ when _totalMatches == 0 => $"No results found for '{Query}' — {s.FilesScanned:N0} files searched — {time}",
            _ => $"{results} — {time}",
        };
    }

    private void ResetResults()
    {
        while (_incoming.TryDequeue(out _)) { }
        _files.Clear();
        _order.Clear();
        Rows.Clear();
        _totalMatches = 0;
        SelectedRow = null;
        Truncated = false;
        LastCompletion = null;
        _pendingCompletion = null;
        PublishCounts();
        RefreshReplacePreview();
    }

    // --------------------------------------------------------------------------- tree manipulation

    public void Clear()
    {
        StopSession();
        _lastQuery = null; // no results left: the replace preview follows the form again
        ResetResults();
        IsSearching = false;
        Summary = "";
        ErrorMessage = "";
        ProgressText = "";
        IsStale = false;
    }

    public void SetExpanded(FileRow file, bool expanded)
    {
        if (file.IsExpanded == expanded) return;
        file.IsExpanded = expanded;
        var at = Rows.IndexOf(file);
        if (at < 0) return;
        if (expanded) Rows.InsertRange(at + 1, file.Matches);
        else Rows.RemoveRange(at + 1, file.Matches.Count);
    }

    public void CollapseAll()
    {
        foreach (var f in _order) f.IsExpanded = false;
        Rows.ReplaceAll(Flatten(_order));
    }

    public void ExpandAll()
    {
        foreach (var f in _order) f.IsExpanded = true;
        Rows.ReplaceAll(Flatten(_order));
    }

    public bool AllCollapsed => _order.Count > 0 && _order.All(f => !f.IsExpanded);

    public void ToggleCollapseAll()
    {
        if (AllCollapsed) ExpandAll(); else CollapseAll();
    }

    /// <summary>Removes a file (with all its matches) or a single match from the results (Del).</summary>
    public void Dismiss(ResultRow row)
    {
        switch (row)
        {
            case FileRow f:
                RemoveFile(f);
                break;
            case MatchRow m:
                RemoveMatches(m.File, [m]);
                break;
        }
        PublishCounts();
        UpdateSummaryAfterEdit();
    }

    private void RemoveFile(FileRow f)
    {
        var at = Rows.IndexOf(f);
        if (at >= 0) Rows.RemoveRange(at, 1 + (f.IsExpanded ? f.Matches.Count : 0));
        _totalMatches -= f.Matches.Count;
        _files.Remove(f.Uri);
        _order.Remove(f);
        if (SelectedRow is not null && (SelectedRow == f || (SelectedRow is MatchRow sm && sm.File == f))) SelectedRow = null;
    }

    private void RemoveMatches(FileRow file, IReadOnlyCollection<MatchRow> matches)
    {
        if (matches.Count >= file.Matches.Count) { RemoveFile(file); return; }
        foreach (var m in matches)
        {
            if (file.IsExpanded) { var i = Rows.IndexOf(m); if (i >= 0) Rows.RemoveAt(i); }
            file.Matches.Remove(m);
            if (SelectedRow == m) SelectedRow = null;
        }
        file.MatchCount = file.Matches.Count;
        _totalMatches -= matches.Count;
    }

    /// <summary>
    /// Removes many files and matches with ONE rebuild of the flat list. Removing them one by one (as <see cref="Dismiss"/> does for a single
    /// row) costs an IndexOf over every row, a list shift and a reset notification per file: for thousands of files (Exclude This Folder on a
    /// big folder, Replace All) that is far beyond the 4 ms budget of a UI callback. A file all of whose matches go is removed with them.
    /// </summary>
    private void RemoveInBulk(IReadOnlyCollection<FileRow> files, IReadOnlyDictionary<FileRow, HashSet<MatchRow>>? matches)
    {
        if (files.Count == 0 && (matches is null || matches.Count == 0)) return;
        var removed = new HashSet<FileRow>(files);
        foreach (var (file, gone) in matches ?? new Dictionary<FileRow, HashSet<MatchRow>>())
        {
            if (gone.Count == 0 || removed.Contains(file)) continue;
            if (gone.Count >= file.Matches.Count) { removed.Add(file); continue; }
            _totalMatches -= file.Matches.RemoveAll(gone.Contains);
            file.MatchCount = file.Matches.Count;
        }
        foreach (var f in removed)
        {
            _totalMatches -= f.Matches.Count;
            _files.Remove(f.Uri);
        }
        if (removed.Count > 0) _order.RemoveAll(removed.Contains);
        var keep = SelectedRow;
        Rows.ReplaceAll(Flatten(_order));
        SelectedRow = keep is not null && Rows.Contains(keep) ? keep : null;
    }

    private void UpdateSummaryAfterEdit()
    {
        if (IsSearching) return;
        Summary = _totalMatches == 0 ? "All results dismissed" : $"{_totalMatches:N0} result{(_totalMatches == 1 ? "" : "s")} in {_order.Count:N0} file{(_order.Count == 1 ? "" : "s")}";
    }

    // --------------------------------------------------------------------------------- navigation

    public async Task OpenAsync(ResultRow row, bool preview = false, bool preserveFocus = false)
    {
        if (_s.Navigation is null) return;
        switch (row)
        {
            case MatchRow m: await _s.Navigation.OpenAsync(m.File.Uri, m.Match.Range, preview, preserveFocus); break;
            case FileRow f: await _s.Navigation.OpenAsync(f.Uri, null, preview, preserveFocus); break;
        }
    }

    public Task NextResultAsync() => StepAsync(+1);
    public Task PreviousResultAsync() => StepAsync(-1);

    /// <summary>F4 / Shift+F4: selects the next/previous match (expanding a collapsed file if needed), wrapping around, and opens it.</summary>
    private async Task StepAsync(int direction)
    {
        var target = FindStep(direction);
        if (target is null) return;
        if (target.File is { IsExpanded: false } f) SetExpanded(f, true);
        SelectedRow = target;
        await OpenAsync(target, preview: true, preserveFocus: true);
    }

    private MatchRow? FindStep(int direction)
    {
        var matches = _order.SelectMany(f => f.Matches).ToList();
        if (matches.Count == 0) return null;
        // Order is the displayed order (files in tree order, matches in document order).
        var current = SelectedRow switch
        {
            MatchRow m => matches.IndexOf(m),
            FileRow f => direction > 0 ? matches.IndexOf(f.Matches.FirstOrDefault()!) - 1 : matches.IndexOf(f.Matches.FirstOrDefault()!),
            _ => direction > 0 ? -1 : matches.Count,
        };
        var next = ((current + direction) % matches.Count + matches.Count) % matches.Count;
        return matches[next];
    }

    // ----------------------------------------------------------------------------------- history

    public void HistoryUp()
    {
        if (_history.Previous(Query) is { } older) Query = older;
    }

    public void HistoryDown()
    {
        if (_history.Next() is { } newer) Query = newer;
    }

    // ------------------------------------------------------------------------------------ replace

    private void RefreshReplacePreview()
    {
        // The preview shows what Replace will write, so it follows the query that produced the results (Replace uses that one), not the
        // form, whose toggles or pattern may have been edited since without searching again.
        var query = _lastQuery ?? BuildQuery();
        if (!ReplaceVisible || string.IsNullOrEmpty(query.Pattern))
        {
            _previews.Set(null);
            return;
        }
        // Compiled once per query: a keystroke in the replace box only builds a new closure.
        if (_previewPattern is null || _previewPattern.Query != query) _previewPattern = ReplaceService.CreatePreviewPattern(query);
        // An empty replacement is a valid "delete the matches", so the preview is active as soon as the replace box is open.
        _previews.Set(ReplaceService.CreatePreviewer(_previewPattern, new ReplaceOptions(ReplaceText, PreserveCase)));
    }

    public Task ReplaceAllAsync() => ReplaceAsync(_order.Select(f => new ReplaceTarget(f.Uri, f.Matches.Select(m => m.Match.Range).ToArray())).ToList());

    public Task ReplaceFileAsync(FileRow file) =>
        ReplaceAsync([new ReplaceTarget(file.Uri, file.Matches.Select(m => m.Match.Range).ToArray())]);

    public Task ReplaceMatchAsync(MatchRow match) =>
        ReplaceAsync([new ReplaceTarget(match.File.Uri, [match.Match.Range])]);

    private async Task ReplaceAsync(IReadOnlyList<ReplaceTarget> targets)
    {
        // Replacing needs the replace UI: in plain Find the replacement is "" (hidden), so a stray click on a row's icon or Ctrl+Alt+Enter
        // would delete every match of a file without a prompt, on disk and without undo for a closed file. The view hides those actions too.
        if (!ReplaceVisible || IsReplacing || _s.Navigation is null || targets.Count == 0 || (_lastQuery ?? BuildQuery()) is not { } query) return;
        SetReplacing(true);
        (MessageSeverity Severity, string Text)? report;
        try
        {
            report = await ReplaceCoreAsync(query, targets, _s.Navigation);
        }
        finally
        {
            SetReplacing(false);
        }
        // Shown, never awaited: a notification without actions resolves only when the shell demotes it (8-20 s later) or never (pushed off the
        // toast stack), and Replace must not stay locked that long.
        if (report is { } r && _s.Windows is { } windows) _ = windows.ShowMessageAsync(r.Severity, r.Text).AsTask();
    }

    /// <summary>Plans, confirms (multi-file only) and applies; returns the message to show afterwards, if any.</summary>
    private async Task<(MessageSeverity Severity, string Text)?> ReplaceCoreAsync(SearchQuery query, IReadOnlyList<ReplaceTarget> targets, INavigationService navigation)
    {
        var options = new ReplaceOptions(ReplaceText, PreserveCase);
        var plan = await _s.Replace.PlanAsync(query, targets, options);
        if (plan.ReplacementCount == 0)
        {
            return (MessageSeverity.Info, plan.Skipped.Count > 0
                ? "Nothing was replaced: " + string.Join("; ", plan.Skipped.Select(x => x.Reason).Distinct())
                : "There is nothing to replace.");
        }

        // One confirmation for a multi-file replace (the files are then applied one by one, so the shell does not ask again). It is a toast
        // that may be answered much later; ApplyAsync therefore re-checks every file against the text it was planned from right before
        // editing it, and skips (and reports) a file that changed meanwhile.
        if (plan.FileCount > 1 && _s.Windows is not null)
        {
            var text = ReplaceText.Length == 0 ? "nothing (delete them)" : $"'{ReplaceText}'";
            var choice = await _s.Windows.ShowMessageAsync(MessageSeverity.Warning,
                $"Replace {plan.ReplacementCount:N0} occurrences across {plan.FileCount:N0} files with {text}?",
                [new MessageAction("Replace"), new MessageAction("Cancel", true)]);
            if (choice != "Replace") return null;
        }

        var result = await _s.Replace.ApplyAsync(plan, navigation, "Replace in Files");
        ApplyReplaceResult(plan, result);
        var msg = $"Replaced {result.Replacements:N0} occurrence{(result.Replacements == 1 ? "" : "s")} across {result.FilesChanged:N0} file{(result.FilesChanged == 1 ? "" : "s")}";
        _s.Windows?.SetStatusBarMessage(msg, TimeSpan.FromSeconds(6));
        Summary = msg;
        return result.Problems.Count == 0 ? null
            : (MessageSeverity.Warning, "Some replacements were skipped:\n" + string.Join("\n", result.Problems.Take(10)) + (result.Problems.Count > 10 ? $"\n…and {result.Problems.Count - 10} more" : ""));
    }

    private void SetReplacing(bool value)
    {
        IsReplacing = value;
        OnPropertyChanged(nameof(IsReplacing));
        OnPropertyChanged(nameof(CanReplace));
        (ReplaceAllCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
    }

    /// <summary>Takes the replaced matches out of the tree (in one rebuild); files that still have matches shift under the edit, so those are re-searched.</summary>
    private void ApplyReplaceResult(ReplacePlan plan, ReplaceResult result)
    {
        var planned = new Dictionary<Uri, FileReplacePlan>(plan.Files.Count);
        foreach (var p in plan.Files) planned.TryAdd(p.Uri, p);
        var gone = new Dictionary<FileRow, HashSet<MatchRow>>();
        var rerun = false;
        foreach (var uri in result.ChangedFiles ?? [])
        {
            if (!_files.TryGetValue(uri, out var row) || !planned.TryGetValue(uri, out var filePlan)) continue;
            var replaced = filePlan.Items.Select(i => i.Range).ToHashSet();
            var matches = row.Matches.Where(m => replaced.Contains(m.Match.Range)).ToHashSet();
            if (matches.Count < row.Matches.Count) rerun = true;
            gone[row] = matches;
        }
        RemoveInBulk([], gone);
        PublishCounts();
        if (rerun && CanSearch) Start();
    }

    // ----------------------------------------------------------------------------------- misc

    public string? CopyPathOf(ResultRow row) => row switch { FileRow f => f.FullPath, MatchRow m => m.File.FullPath, _ => null };

    public async Task CopyPathAsync(ResultRow row)
    {
        if (CopyPathOf(row) is { } p && _s.Windows is not null) await _s.Windows.CopyToClipboardAsync(p);
    }

    /// <summary>Adds the file's folder (relative to its workspace folder) to the exclude field, removes its results ("exclude this folder").</summary>
    public void ExcludeFolderOf(FileRow file)
    {
        if (string.IsNullOrEmpty(file.Directory)) return;
        var glob = "./" + file.Directory;
        Exclude = string.IsNullOrWhiteSpace(Exclude) ? glob : Exclude.TrimEnd().TrimEnd(',') + ", " + glob;
        RemoveInBulk(_order.Where(f => f.Directory == file.Directory || f.Directory.StartsWith(file.Directory + "/", StringComparison.Ordinal)).ToList(), null);
        PublishCounts();
        UpdateSummaryAfterEdit();
    }

    public bool HasCommand(string id) => _s.Commands?.Has(id) == true;

    public async Task RevealInExplorerAsync(ResultRow row)
    {
        var uri = row switch { FileRow f => f.Uri, MatchRow m => m.File.Uri, _ => null };
        if (uri is null || _s.Commands is null || !_s.Commands.Has("revealInExplorer")) return;
        await _s.Commands.ExecuteAsync("revealInExplorer", uri.ToString());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopSession();
        _subs.Dispose();
        while (_incoming.TryDequeue(out _)) { }
        Rows.Clear();
        _files.Clear();
        _order.Clear();
        _totalMatches = 0;
        _focus.Clear();
        _previews.Set(null);
        SelectedRow = null;
        if (!_searchDone.Task.IsCompleted) _searchDone.TrySetResult();
    }
}
