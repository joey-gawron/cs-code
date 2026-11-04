using System.ComponentModel;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Timeline;
using CsCode.Panels.Timeline.LocalHistory;
using CsCode.Ui.Collections;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Timeline;

/// <summary>
/// The Timeline window's state while it is shown (created with its view, disposed when it is hidden): the file it shows (the active editor's,
/// unless pinned), and its items from every included provider, merged newest first. Providers page: a source that has more contributes items
/// only down to its oldest loaded item (anything older from another source could interleave with its next page), and a "Load more" row asks
/// each such source for its next page. Mutated on the UI thread only; provider calls run on their own lanes.
/// </summary>
public sealed class TimelineViewModel : INotifyPropertyChanged, IDisposable
{
    internal const int PageSize = 50;
    private static readonly TimeSpan TimeRefresh = TimeSpan.FromMinutes(1);

    private readonly TimelineHost _host;
    private readonly IUiDispatcher _ui;
    private readonly DisposableStore _store = new();
    private readonly List<SourceState> _sources = [];
    private readonly ITimer? _timer;
    private CancellationTokenSource? _loading;
    private int _generation;
    private Uri? _resource;
    private TimelineRow? _selected;
    private bool _isLoading;
    private bool _disposed;

    private sealed class SourceState(ITimelineProvider provider, int order)
    {
        public ITimelineProvider Provider { get; } = provider;
        public int Order { get; } = order;
        public List<TimelineItem> Items { get; } = [];
        public string? Cursor { get; set; }
        public bool Loaded { get; set; }
        public bool Loading { get; set; }
    }

    internal TimelineViewModel(TimelineHost host, IUiDispatcher ui)
    {
        _host = host;
        _ui = ui;
        foreach (var provider in host.Providers) _store.Add(provider.Changed.Subscribe(OnProviderChanged));
        if (host.Navigation is { } navigation)
            _store.Add(navigation.ActiveDocumentChanged.Subscribe(doc => _ui.Post(() => OnActiveDocument(doc?.Uri))));
        if (host.Config is { } config)
            _store.Add(config.Changed.Subscribe(e => { if (e.Affects(LocalHistorySettings.Enabled)) _ui.PostCoalesced(CoalesceKey, Reload); }));
        _timer = host.Time.CreateTimer(_ => _ui.Post(RefreshTimes), null, TimeRefresh, TimeRefresh);
        _resource = host.Navigation?.ActiveDocument?.Uri is { } active && Trackable(active) ? active : host.LastResource;
        host.LastResource = _resource;
        Reload();
    }

    private string CoalesceKey => "timeline.reload." + GetHashCode();

    public BulkObservableCollection<TimelineRow> Rows { get; } = [];

    /// <summary>The file shown; null when none.</summary>
    public Uri? Resource => _resource;

    public string Title => _resource is null ? "Timeline" : LocalHistoryTimelineProvider.FileName(_resource);

    /// <summary>VS Code's "follow active editor": off pins the Timeline to its file.</summary>
    public bool FollowActiveEditor => _host.FollowActiveEditor;

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (_isLoading != value) { _isLoading = value; Changed(nameof(IsLoading)); Changed(nameof(EmptyText)); Changed(nameof(IsEmpty)); } }
    }

    public bool IsEmpty => Rows.Count == 0;

    public string EmptyText => _resource is null
        ? "Open a file to see its timeline: its local history and the commits that changed it."
        : IsLoading ? $"Loading the timeline of '{Title}'..."
        : _sources.Count == 0 ? $"No source of the timeline is turned on for '{Title}' (Filter)."
        : $"No timeline information for '{Title}'." + (_host.History?.IsEnabled == false ? " Local history is turned off (workbench.localHistory.enabled)." : "");

    /// <summary>The selected row (the palette's per-entry commands act on its item).</summary>
    public TimelineRow? Selected
    {
        get => _selected;
        set { if (!ReferenceEquals(_selected, value)) { _selected = value; Changed(nameof(Selected)); } }
    }

    /// <summary>Raised after the rows were replaced (the view restores its selection).</summary>
    public event Action? RowsRebuilt;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Trackable(Uri uri) => _host.Providers.Any(p => SafeHandles(p, uri));

    private void OnActiveDocument(Uri? uri)
    {
        if (_disposed || !_host.FollowActiveEditor || uri is null || !Trackable(uri) || uri == _resource) return; // no editor (a diff tab): keep the file
        Show(uri);
    }

    /// <summary>Shows the timeline of <paramref name="resource"/> (UI thread).</summary>
    public void Show(Uri resource)
    {
        if (_disposed) return;
        _resource = resource;
        _host.LastResource = resource;
        Selected = null;
        Changed(nameof(Resource));
        Changed(nameof(Title));
        Reload();
    }

    /// <summary>Re-reads the rows when the follow mode changes (the host owns it: it outlives the view).</summary>
    internal void OnFollowChanged(Uri? active)
    {
        Changed(nameof(FollowActiveEditor));
        if (_host.FollowActiveEditor && active is not null && Trackable(active) && active != _resource) Show(active);
    }

    private void OnProviderChanged(Uri? uri)
    {
        if (_disposed) return;
        var current = _resource;
        if (uri is not null && (current is null || !SameResource(uri, current))) return;
        _ui.PostCoalesced(CoalesceKey, Reload, UiPriority.Background);
    }

    private static bool SameResource(Uri a, Uri b) => a == b || string.Equals(HistoryFormat.Key(a), HistoryFormat.Key(b), StringComparison.Ordinal);

    /// <summary>Reads every included source again from its first page (UI thread). The returned task completes when all have answered.</summary>
    public Task ReloadAsync()
    {
        if (_disposed) return Task.CompletedTask;
        _loading?.Cancel();
        _loading?.Dispose();
        var cts = _loading = new CancellationTokenSource();
        var generation = ++_generation;
        _sources.Clear();
        var resource = _resource;
        if (resource is not null)
        {
            var order = 0;
            foreach (var provider in _host.Providers)
            {
                if (_host.IsSourceIncluded(provider.Id) && SafeHandles(provider, resource)) _sources.Add(new SourceState(provider, order));
                order++;
            }
        }
        Rebuild();
        if (_sources.Count == 0) { IsLoading = false; return Task.CompletedTask; }
        IsLoading = true;
        return Task.WhenAll(_sources.Select(s => LoadPageAsync(generation, s, resource!, cts.Token)).ToArray());
    }

    private void Reload() => _ = ReloadAsync();

    /// <summary>The next page of every source that has one (the "Load more" row).</summary>
    public Task LoadMoreAsync()
    {
        if (_disposed || _resource is not { } resource || _loading is not { } cts) return Task.CompletedTask;
        var generation = _generation;
        var more = _sources.Where(s => s.Cursor is not null && !s.Loading).ToArray();
        if (more.Length == 0) return Task.CompletedTask;
        IsLoading = true;
        return Task.WhenAll(more.Select(s => LoadPageAsync(generation, s, resource, cts.Token)).ToArray());
    }

    public bool HasMore => _sources.Any(s => s.Cursor is not null);

    private async Task LoadPageAsync(int generation, SourceState s, Uri resource, CancellationToken ct)
    {
        s.Loading = true;
        TimelinePage page;
        try
        {
            page = await s.Provider.GetTimelineAsync(resource, s.Cursor, PageSize, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log?.Invoke($"Timeline: {s.Provider.Label} failed for {resource}: {ex.Message}");
            page = TimelinePage.Empty;
        }
        finally
        {
            s.Loading = false;
        }
        if (_disposed || generation != _generation) return;
        s.Items.AddRange(page.Items);
        s.Cursor = page.Items.Count == 0 ? null : page.Cursor; // an empty page with a cursor would ask for ever
        s.Loaded = true;
        Rebuild();
        if (_sources.All(x => !x.Loading)) IsLoading = false;
    }

    private void Rebuild()
    {
        if (_disposed) return;
        var now = _host.Time.GetUtcNow();
        // While a source has more pages, nothing older than its oldest loaded item is shown: the next page may hold items in between.
        DateTimeOffset? cutoff = null;
        foreach (var s in _sources)
            if (s.Loaded && s.Cursor is not null && s.Items.Count > 0 && (cutoff is null || s.Items[^1].Timestamp > cutoff)) cutoff = s.Items[^1].Timestamp;
        var items = _sources.Where(s => s.Loaded)
            .SelectMany(s => s.Items.Select(i => (Item: i, s.Order)))
            .Where(x => cutoff is null || x.Item.Timestamp >= cutoff)
            .OrderByDescending(x => x.Item.Timestamp).ThenBy(x => x.Order)
            .Select(x => TimelineRow.For(x.Item, now))
            .ToList();
        if (HasMore) items.Add(TimelineRow.LoadMore());
        var keep = _selected?.Item;
        Rows.ReplaceAll(items);
        Selected = keep is null ? null : items.FirstOrDefault(r => r.Item is { } i && i.ProviderId == keep.ProviderId && i.Id == keep.Id);
        Changed(nameof(IsEmpty));
        Changed(nameof(EmptyText));
        Changed(nameof(HasMore));
        RowsRebuilt?.Invoke();
    }

    private void RefreshTimes()
    {
        if (_disposed) return;
        var now = _host.Time.GetUtcNow();
        foreach (var row in Rows) row.UpdateTime(now);
    }

    private static bool SafeHandles(ITimelineProvider provider, Uri resource)
    {
        try { return provider.Handles(resource); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Dispose();
        _timer?.Dispose();
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
        _sources.Clear();
        Rows.Clear();
        RowsRebuilt = null;
        PropertyChanged = null;
    }
}
