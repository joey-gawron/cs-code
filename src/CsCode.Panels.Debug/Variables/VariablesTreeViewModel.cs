using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Infrastructure;
using CsCode.Ui.Threading;

namespace CsCode.Panels.Debug.Variables;

/// <summary>Identifies one load of a tree: results that arrive for an older generation (the debuggee resumed, another frame was picked) are dropped.</summary>
public readonly record struct LoadToken(long Generation, CancellationToken Cancellation);

/// <summary>
/// The engine behind Locals, Autos and Watch (and reusable for editor hover pins): a lazily expanded, paged, virtualised variables tree.
///
/// <list type="bullet">
/// <item><b>Virtualised by flattening.</b> Visible nodes live in one <see cref="Rows"/> list bound to a virtualising list control, so a
/// million-element array costs one page of rows (100) plus a pager row, never a million visuals.</item>
/// <item><b>Lazy and paged.</b> Children are fetched with <c>GetVariablesAsync(reference, start, 100)</c> when a node is expanded; a "… N more"
/// row loads the next page. Per node at most <see cref="MaxLoadedPerNode"/> children are ever held.</item>
/// <item><b>Handle lifetime.</b> <c>variablesReference</c>s die when the debuggee resumes. <see cref="Invalidate"/> cancels in-flight loads,
/// drops every node (nothing keeps a stale handle), but remembers which paths were expanded and the values shown; the next stop re-expands
/// those paths lazily and highlights values that differ from the previous stop.</item>
/// <item><b>Never blocks the UI thread.</b> Every adapter call is awaited through <see cref="UiAsync.OnUi{T}"/>; large pages are turned into
/// nodes in ≤ 4 ms slices (<see cref="IUiDispatcher.PostBatched{T}"/>).</item>
/// </list>
/// </summary>
public sealed class VariablesTreeViewModel : ObservableObject, IDisposable
{
    public const int PageSize = 100;
    public const int MaxLoadedPerNode = 10_000;
    private const int MaxTrackedValues = 20_000;
    private const int MaxExpandedPaths = 2_000;
    private const int InlineCreateLimit = 64;

    private readonly DebugPanelServices _s;
    private readonly IUiDispatcher _ui;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    // Change highlighting. A frame's "shape" (its first root names) keys the values it showed: _epochSnapshots holds what each shape showed
    // earlier in THIS stop (the user may switch frames), _baselines what each shape showed at the most recent EARLIER stop. Only
    // _baselines is ever compared with, so switching frames back and forth never turns a stop's own values into the baseline.
    private const int MaxBaselineShapes = 8;
    private const int MaxTrackedValueChars = 1_024;
    private Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private Dictionary<string, string>? _previous;
    private readonly Dictionary<string, Dictionary<string, string>> _epochSnapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _baselines = new(StringComparer.Ordinal);
    private readonly Queue<string> _baselineOrder = new();
    private string? _currentSignature;
    private bool _compare;
    private long _loadedEpoch = -1;
    private CancellationTokenSource _cts = new();
    private long _generation;
    private IDebugSession? _session;
    private VariableNodeViewModel? _holder;
    private NumberFormat _format;
    private string? _status;
    private VariableNodeViewModel? _selected;
    private int _disposed;

    public VariablesTreeViewModel(DebugPanelServices services, string? initialStatus = null)
    {
        _s = services;
        _ui = services.Ui;
        _status = initialStatus;
        Rows.CollectionChanged += OnRowsChanged;
    }

    /// <summary>The flattened, currently visible rows (bind a virtualising list to this).</summary>
    public RangeObservableCollection<VariableNodeViewModel> Rows { get; } = new();

    /// <summary>Shared Name/Value column widths of the header and every row.</summary>
    public ColumnSizes Columns { get; } = new();

    public IDebugSession? Session => _session;
    public long Generation => Volatile.Read(ref _generation);
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Text shown when there are no rows ("Not debugging", "Running…", "No variables").</summary>
    public string? StatusText { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(ShowStatus)); } }
    public bool ShowStatus => Rows.Count == 0 && !string.IsNullOrEmpty(_status);

    public VariableNodeViewModel? SelectedRow { get => _selected; set => SetProperty(ref _selected, value); }

    /// <summary>Hex/decimal display of integer values for the whole tree (a node can override it).</summary>
    public NumberFormat NumberFormat
    {
        get => _format;
        set
        {
            if (!SetProperty(ref _format, value)) return;
            foreach (var row in Rows) row.Refresh();
        }
    }

    /// <summary>The panel model reloads the frame (a value was edited and its dependents may have changed).</summary>
    public Action? RefreshRequested { get; set; }

    /// <summary>The last load/edit failure, shown in a strip under the rows until the next load (null = none).</summary>
    public string? ErrorText { get => _errorText; private set { if (SetProperty(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _errorText is not null;
    private string? _errorText;

    /// <summary>Delete key / "Delete Watch" on a row (the Watch window wires it; Locals and Autos leave it unset).</summary>
    public Action<VariableNodeViewModel>? DeleteRequested { get; set; }

    /// <summary>Alt+Up/Down and "Move Up/Down" on a row: moves it by the given number of positions (Watch only).</summary>
    public Action<VariableNodeViewModel, int>? MoveRequested { get; set; }

    /// <summary>A watch row was dropped on another row (drag and drop): move the first onto the position of the second (Watch only).</summary>
    public Action<VariableNodeViewModel, VariableNodeViewModel>? ReorderRequested { get; set; }

    /// <summary>"Delete All Watches" (Watch only).</summary>
    public Action? DeleteAllRequested { get; set; }

    /// <summary>Called when the user commits an expression edit on a Watch/NewWatch row.</summary>
    public Func<VariableNodeViewModel, string, Task>? ExpressionCommitted { get; set; }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(ShowStatus));

    private bool IsLive(long generation) => generation == Volatile.Read(ref _generation) && Volatile.Read(ref _disposed) == 0;

    // ------------------------------------------------------------------ loading / invalidation

    /// <summary>Starts a new load for <paramref name="session"/>: cancels whatever is in flight and clears the rows.</summary>
    /// <param name="epoch">The stop this load belongs to; a new epoch turns the values currently shown into the baseline for change highlighting.</param>
    public LoadToken BeginLoad(IDebugSession session, long epoch)
    {
        CancelPending();
        if (_loadedEpoch != epoch) PromoteEpoch();
        else StashCurrent();
        _loadedEpoch = epoch;
        _currentSignature = null;
        _previous = null;
        _compare = false;
        _session = session;
        _holder = null;
        ErrorText = null;
        ClearRows();
        return new LoadToken(Generation, _cts.Token);
    }

    /// <summary>
    /// The "shape" of a frame is its first root names: values are only compared with the previous stop's when the shapes match, so stepping
    /// into another function does not paint its (unrelated) locals red.
    /// </summary>
    private void ApplySignature(IEnumerable<string> rootNames)
    {
        UseSignature(string.Join('\n', rootNames.Take(40)));
    }

    private void UseSignature(string signature)
    {
        _currentSignature = signature;
        _previous = _baselines.GetValueOrDefault(signature);
        _compare = _previous is { Count: > 0 };
    }

    /// <summary>The frame being left (another one was picked at the same stop) keeps what it showed, under its shape.</summary>
    private void StashCurrent()
    {
        if (_currentSignature is not null && _current.Count > 0)
        {
            _epochSnapshots[_currentSignature] = _current;
            _current = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        else _current.Clear();
    }

    /// <summary>The stop is over (resume, or another stop began): what each frame shape showed becomes that shape's baseline.</summary>
    private void PromoteEpoch()
    {
        StashCurrent();
        foreach (var (signature, values) in _epochSnapshots)
        {
            if (!_baselines.ContainsKey(signature)) _baselineOrder.Enqueue(signature);
            _baselines[signature] = values;
        }
        _epochSnapshots.Clear();
        while (_baselineOrder.Count > MaxBaselineShapes) _baselines.Remove(_baselineOrder.Dequeue());
    }

    /// <summary>Like <see cref="BeginLoad"/> but keeps the root rows (Watch): the caller re-evaluates them under the returned token.</summary>
    public LoadToken BeginReload(IDebugSession session, long epoch, string signature)
    {
        CancelPending();
        if (_loadedEpoch != epoch)
        {
            PromoteEpoch();
            CaptureBaselines();
        }
        else StashCurrent();
        _loadedEpoch = epoch;
        UseSignature(signature);
        _session = session;
        ErrorText = null;
        RemoveNonRootRows();
        foreach (var root in Rows.Where(r => r.Depth == 0)) root.DropHandles();
        return new LoadToken(Generation, _cts.Token);
    }

    /// <summary>
    /// The debuggee resumed (or the session ended): every handle is dead. Cancels in-flight loads, drops all nodes, remembers expansion
    /// paths and the values shown (the baseline for change highlighting after the next stop).
    /// </summary>
    /// <param name="keepRoots">Keep the top-level rows (Watch expressions) and merely dim their values; everything below them is dropped.</param>
    public void Invalidate(string? status, bool keepRoots = false)
    {
        CancelPending();
        PromoteEpoch();
        _loadedEpoch = -1;
        _session = null;
        _holder = null;
        if (keepRoots)
        {
            CaptureBaselines();
            RemoveNonRootRows();
            foreach (var root in Rows)
            {
                root.DropHandles();
                if (root.IsDataRow && !root.IsError) root.IsStale = true;
            }
        }
        else ClearRows();
        StatusText = status;
    }

    /// <summary>Root rows that outlive stops (Watch) keep the value they showed as the baseline the next stop is compared with.</summary>
    private void CaptureBaselines()
    {
        foreach (var root in Rows)
            if (root.Depth == 0 && root.Kind == VariableRowKind.Watch)
                root.BaselineValue = root.HasValue && !root.IsError ? root.FullValue : null;
    }

    private void CancelPending()
    {
        Interlocked.Increment(ref _generation);
        var old = _cts;
        _cts = new CancellationTokenSource();
        try { old.Cancel(); }
        catch (AggregateException) { /* a cancellation callback threw: nothing to do, the loads are abandoned anyway */ }
        old.Dispose();
    }

    private void ClearRows()
    {
        SelectedRow = null;
        Rows.Reset();
    }

    private void RemoveNonRootRows()
    {
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (Rows[i].Depth == 0) continue;
            var end = i;
            while (i > 0 && Rows[i - 1].Depth > 0) i--;
            Rows.RemoveRange(i, end - i + 1);
        }
        if (SelectedRow is not null && !Rows.Contains(SelectedRow)) SelectedRow = null;
    }

    public void SetStatus(string? status) => StatusText = status;

    // ------------------------------------------------------------------ node creation

    internal VariableNodeViewModel NewNode(VariableRowKind kind, VariableNodeViewModel? parent, string name, string path, long generation, WatchExpression? watch = null) =>
        new(this, kind, parent, name, path) { Generation = generation, Watch = watch };

    /// <summary>Builds the node of one adapter variable and applies expansion memory + change detection by path.</summary>
    internal VariableNodeViewModel CreateVariableNode(DebugVariable v, VariableNodeViewModel parent, int parentReference, long generation)
    {
        var path = (parent.Path.Length == 0 ? "" : parent.Path + "/") + v.Name;
        var node = NewNode(VariableRowKind.Variable, parent, v.Name, path, generation);
        node.ParentReference = parentReference;
        node.SetFrom(v);
        TrackValue(node, v.Value);
        return node;
    }

    private void TrackValue(VariableNodeViewModel node, string value)
    {
        var tracked = value.Length > MaxTrackedValueChars ? value[..MaxTrackedValueChars] : value;
        if (_current.Count < MaxTrackedValues) _current[node.Path] = tracked;
        if (_compare && _previous!.TryGetValue(node.Path, out var old) && !string.Equals(old, tracked, StringComparison.Ordinal)) node.IsChanged = true;
    }

    /// <summary>Records a (re-)evaluated child value for change detection (Watch children use it).</summary>
    internal void NoteValue(VariableNodeViewModel node) => TrackValue(node, node.FullValue);

    private void RememberExpanded(string path, bool expanded)
    {
        if (!expanded) { _expanded.Remove(path); return; }
        if (_expanded.Count >= MaxExpandedPaths) _expanded.Clear(); // bounded: pathological expansion churn starts over rather than growing
        _expanded.Add(path);
    }

    // ------------------------------------------------------------------ roots

    /// <summary>
    /// Shows the variables of <paramref name="scopes"/>: a single scope's variables directly (paged), several scopes as expandable groups.
    /// </summary>
    public async Task ShowScopesAsync(LoadToken token, IReadOnlyList<DebugScope> scopes)
    {
        if (!IsLive(token.Generation)) return;
        if (scopes.Count == 0) { StatusText = "No variables"; return; }
        StatusText = null;
        var holder = NewNode(VariableRowKind.Scope, null, "", "", token.Generation);
        holder.Depth = -1;
        _holder = holder;
        if (scopes.Count == 1)
        {
            var scope = scopes[0];
            holder.SetValue("", null, scope.VariablesReference, scope.NamedVariables, scope.IndexedVariables);
            await LoadChildrenAsync(holder, loadMore: false);
            if (IsLive(token.Generation) && Rows.Count == 0) StatusText = "No variables";
            return;
        }
        ApplySignature(scopes.Select(sc => sc.Name));
        var groups = new List<VariableNodeViewModel>(scopes.Count);
        foreach (var scope in scopes)
        {
            var group = NewNode(VariableRowKind.Scope, holder, scope.Name, "scope:" + scope.Name, token.Generation);
            group.SetValue("", null, scope.VariablesReference, scope.NamedVariables, scope.IndexedVariables);
            if (!_expanded.Contains(group.Path) && !_hasSeenScopes) RememberExpanded(group.Path, true);
            groups.Add(group);
        }
        _hasSeenScopes = true;
        holder.AddChildren(groups);
        holder.ChildrenLoaded = true;
        Rows.AddRange(groups);
        await RestoreExpansionAsync(groups, token.Generation);
    }

    private bool _hasSeenScopes;

    /// <summary>Shows an already known flat list of variables as the roots (Autos: filtered locals).</summary>
    public async Task ShowRootsAsync(LoadToken token, IReadOnlyList<(DebugVariable Variable, int ParentReference)> roots)
    {
        if (!IsLive(token.Generation)) return;
        var holder = NewNode(VariableRowKind.Scope, null, "", "", token.Generation);
        holder.Depth = -1;
        _holder = holder;
        ApplySignature(roots.Select(r => r.Variable.Name));
        var nodes = new List<VariableNodeViewModel>(roots.Count);
        foreach (var (variable, parentRef) in roots) nodes.Add(CreateVariableNode(variable, holder, parentRef, token.Generation));
        holder.AddChildren(nodes);
        holder.ChildrenLoaded = true;
        StatusText = nodes.Count == 0 ? "No variables" : null;
        Rows.AddRange(nodes);
        await RestoreExpansionAsync(nodes, token.Generation);
    }

    // ------------------------------------------------------------------ expand / collapse / paging

    public Task ToggleAsync(VariableNodeViewModel node)
    {
        if (!node.IsExpanded) return ExpandAsync(node);
        Collapse(node);
        return Task.CompletedTask;
    }

    public async Task ExpandAsync(VariableNodeViewModel node)
    {
        if (IsDisposed || !node.HasChildren || node.Kind == VariableRowKind.More) return;
        if (node.Generation != Generation && node.Kind != VariableRowKind.Watch) return; // a node of a previous stop: its handle is dead
        if (node.IsExpanded && (node.ChildrenLoaded || node.IsLoading)) return;
        node.IsExpanded = true;
        RememberExpanded(node.Path, true);
        if (node.ChildrenLoaded)
        {
            await InsertVisibleChildrenAsync(node, Generation);
            return;
        }
        await LoadChildrenAsync(node, loadMore: false);
    }

    public void Collapse(VariableNodeViewModel node)
    {
        if (!node.IsExpanded) return;
        node.IsExpanded = false;
        node.RowsVersion++; // a page of this node still waiting to be inserted must not appear under it now
        RememberExpanded(node.Path, false);
        var index = Rows.IndexOf(node);
        if (index < 0) return;
        var count = 0;
        while (index + 1 + count < Rows.Count && Rows[index + 1 + count].Depth > node.Depth) count++;
        if (count > 0)
        {
            if (SelectedRow is { } sel && sel != node && IsInRange(sel, index + 1, count)) SelectedRow = node;
            // Likewise for expanded descendants: re-expanding this node re-inserts their loaded rows itself.
            for (var i = index + 1; i <= index + count; i++) Rows[i].RowsVersion++;
            Rows.RemoveRange(index + 1, count);
        }
        // Error/message rows are one-shot: re-expanding retries the load.
        if (node.Children.Count > 0 && node.Children[0].Kind == VariableRowKind.Message)
        {
            node.ClearChildren();
            node.ChildrenLoaded = false;
        }
    }

    private bool IsInRange(VariableNodeViewModel row, int start, int count)
    {
        var i = Rows.IndexOf(row);
        return i >= start && i < start + count;
    }

    /// <summary>Loads the next page of a node's children (the "… more" row was activated).</summary>
    public Task LoadMoreAsync(VariableNodeViewModel node) => LoadChildrenAsync(node.Kind == VariableRowKind.More ? node.Parent! : node, loadMore: true);

    private async Task LoadChildrenAsync(VariableNodeViewModel node, bool loadMore)
    {
        var generation = Generation;
        var session = _session;
        if (session is null || IsDisposed || node.IsLoading) return;
        if (node.Generation != generation && node.Kind != VariableRowKind.Watch) return;
        var ct = _cts.Token;
        var start = loadMore ? node.NextStart : 0;
        node.IsLoading = true;
        try
        {
            var page = await _ui.OnUi(session.GetVariablesAsync(node.VariablesReference, start, PageSize, ct), ct);
            if (!IsLive(generation)) return;

            if (node.Depth < 0 && start == 0 && _currentSignature is null) ApplySignature(page.Take(40).Select(v => v.Name));

            // An adapter that ignores paging answers "start = N" with page 0 again: recognise it and stop instead of duplicating rows.
            var ignoredPaging = loadMore && page.Count > 0 && node.FirstChildName is not null && page[0].Name == node.FirstChildName && start > 0;
            var room = Math.Max(0, MaxLoadedPerNode - node.LoadedCount);
            var take = ignoredPaging ? 0 : Math.Min(page.Count, room);

            var created = new List<VariableNodeViewModel>(take);
            if (take <= InlineCreateLimit)
            {
                for (var i = 0; i < take; i++) created.Add(CreateVariableNode(page[i], node, node.VariablesReference, generation));
            }
            else
            {
                var slice = new DebugVariable[take];
                for (var i = 0; i < take; i++) slice[i] = page[i];
                await _ui.PostBatched(slice, v => created.Add(CreateVariableNode(v, node, node.VariablesReference, generation)), 4, UiPriority.Background);
                if (!IsLive(generation)) return;
            }

            if (node.FirstChildName is null && page.Count > 0) node.FirstChildName = page[0].Name;
            node.NextStart = start + (ignoredPaging ? 0 : page.Count);
            var oldMore = node.MoreRow;
            node.AddChildren(created);
            node.ChildrenLoaded = true;

            var more = BuildMoreRow(node, page.Count, ignoredPaging, take < page.Count && !ignoredPaging, generation);
            node.MoreRow = more;
            await ApplyRowsAsync(node, created, oldMore, more, generation);
            await RestoreExpansionAsync(created, generation);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsLive(generation)) ShowLoadError(node, ex);
        }
        finally
        {
            node.IsLoading = false;
        }
    }

    private VariableNodeViewModel? BuildMoreRow(VariableNodeViewModel node, int pageCount, bool ignoredPaging, bool capped, long generation)
    {
        if (ignoredPaging) return null;
        var total = node.KnownChildCount;
        // A short page is the end, whatever the adapter announced (named + indexed counts are not always exact), so the pager never loops.
        var loadedAll = pageCount < PageSize || (total is { } t && node.NextStart >= t);
        if (loadedAll && !capped) return null;
        if (node.LoadedCount >= MaxLoadedPerNode)
        {
            var notice = NewNode(VariableRowKind.Message, node, total is { } n
                ? $"Showing the first {node.LoadedCount:N0} of {n:N0} items"
                : $"Showing the first {node.LoadedCount:N0} items", node.Path + "/…cap", generation);
            return notice;
        }
        var remaining = total is { } all ? all - node.NextStart : (int?)null;
        var text = remaining is { } r && r > 0 ? $"… {r:N0} more" : "… more";
        return NewNode(VariableRowKind.More, node, text, node.Path + "/…more", generation);
    }

    private async Task ApplyRowsAsync(VariableNodeViewModel node, IReadOnlyList<VariableNodeViewModel> created, VariableNodeViewModel? oldMore, VariableNodeViewModel? newMore, long generation)
    {
        var rows = new List<VariableNodeViewModel>(created.Count + 1);
        foreach (var child in created) FlattenInto(rows, child);
        if (newMore is not null) rows.Add(newMore);

        // The pager row is always the last row of the node's block, so the next page goes where it was.
        if (oldMore is not null && Rows.IndexOf(oldMore) is var moreIndex and >= 0) Rows.RemoveAt(moreIndex);
        await InsertChildRowsAsync(node, rows, generation);
    }

    private Task InsertVisibleChildrenAsync(VariableNodeViewModel node, long generation)
    {
        var rows = new List<VariableNodeViewModel>();
        FlattenInto(rows, node, includeSelf: false);
        return InsertChildRowsAsync(node, rows, generation);
    }

    /// <summary>
    /// Where the visible rows below <paramref name="parent"/> end right now (where more of its rows go), or -1 when its rows must not be shown:
    /// the node was collapsed, or is not in the list because an ancestor is collapsed (its rows appear when that ancestor expands).
    /// </summary>
    private int ChildRowsEnd(VariableNodeViewModel parent)
    {
        if (parent.Depth < 0) return Rows.Count; // the hidden root's children are the top level
        if (!parent.IsExpanded) return -1;
        var index = Rows.IndexOf(parent);
        if (index < 0) return -1;
        var end = index + 1;
        while (end < Rows.Count && Rows[end].Depth > parent.Depth) end++;
        return end;
    }

    /// <summary>Rows per list notification. A virtualising list handles an Add of a few hundred rows in well under a frame; ten thousand is not.</summary>
    private const int RowChunk = 100;

    /// <summary>
    /// Inserts <paramref name="rows"/> (descendants of <paramref name="parent"/>) at the end of the parent's block. Small lists go in at once
    /// with one notification; a bigger one (a full page plus its pager row, or an adapter that ignored paging) is inserted in chunks of
    /// <see cref="RowChunk"/> across UI turns (≤ 4 ms per turn) so the list control never has to absorb thousands of rows in one frame.
    /// The position is resolved when a chunk actually goes in, never before the yield: in between, other loads (Watch re-expands every root at
    /// once after a stop) can shift the parent and the user can collapse it. A collapse (or any newer insert for the same parent) drops the rest.
    /// </summary>
    private async Task InsertChildRowsAsync(VariableNodeViewModel parent, List<VariableNodeViewModel> rows, long generation)
    {
        var version = ++parent.RowsVersion;
        if (rows.Count <= RowChunk)
        {
            if (ChildRowsEnd(parent) is var at and >= 0) Rows.InsertRange(at, rows);
            return;
        }
        VariableNodeViewModel? last = null;
        var dropped = false;
        await _ui.PostBatched(rows.Chunk(RowChunk).ToList(), chunk =>
        {
            if (dropped || !IsLive(generation) || parent.RowsVersion != version) { dropped = true; return; }
            // Each chunk follows the previous one; the first goes to the end of the parent's (current) block.
            var index = last is null ? ChildRowsEnd(parent) : Rows.IndexOf(last) is var lastIndex and >= 0 ? lastIndex + 1 : -1;
            if (index < 0) { dropped = true; return; } // the parent (or what was inserted so far) left the list
            Rows.InsertRange(index, chunk);
            last = chunk[^1];
        }, 4, UiPriority.Background);
    }

    /// <summary>Appends <paramref name="node"/> and, when expanded and loaded, its descendants (depth first) plus its pager row.</summary>
    internal static void FlattenInto(List<VariableNodeViewModel> output, VariableNodeViewModel node, bool includeSelf = true)
    {
        if (includeSelf) output.Add(node);
        if (!node.IsExpanded || !node.ChildrenLoaded) return;
        foreach (var child in node.Children) FlattenInto(output, child);
        if (node.MoreRow is not null) output.Add(node.MoreRow);
    }

    private void ShowLoadError(VariableNodeViewModel node, Exception ex)
    {
        var message = ex is InvalidOperationException ? "The debuggee resumed; this value is no longer available." : ex.Message;
        var row = NewNode(VariableRowKind.Message, node, "Cannot load children: " + message, node.Path + "/…error", Generation);
        node.ClearChildren();
        node.AddChildren([row]);
        node.ChildrenLoaded = false;
        node.MoreRow = null;
        var index = node.Depth < 0 ? -1 : Rows.IndexOf(node);
        if (index >= 0 && node.IsExpanded)
        {
            var count = 0;
            while (index + 1 + count < Rows.Count && Rows[index + 1 + count].Depth > node.Depth) count++;
            Rows.RemoveRange(index + 1, count);
            Rows.InsertRange(index + 1, [row]);
        }
        else if (node.Depth < 0) Rows.AddRange([row]);
        ErrorText = message;
    }

    internal async Task RestoreExpansionAsync(IReadOnlyList<VariableNodeViewModel> nodes, long generation)
    {
        // Sequential on purpose: one request in flight per tree keeps a deep restore from flooding the adapter.
        foreach (var node in nodes)
        {
            if (!IsLive(generation)) return;
            if (node.HasChildren && node.IsDataRow && !node.IsExpanded && _expanded.Contains(node.Path))
                await ExpandAsync(node);
        }
    }

    // ------------------------------------------------------------------ activation, keyboard helpers

    /// <summary>Enter / double-click: pager rows load more, placeholder rows start their editor, expandable rows toggle.</summary>
    public async Task ActivateAsync(VariableNodeViewModel node)
    {
        switch (node.Kind)
        {
            case VariableRowKind.More: await LoadMoreAsync(node); break;
            case VariableRowKind.NewWatch: BeginEditExpression(node); break;
            case VariableRowKind.Message: break;
            default:
                if (node.HasChildren) await ToggleAsync(node);
                break;
        }
    }

    /// <summary>The row that holds <paramref name="node"/> in the list, or its nearest visible ancestor (for Left-arrow navigation).</summary>
    public VariableNodeViewModel? ParentRow(VariableNodeViewModel node) => node.Parent is { Depth: >= 0 } p && Rows.Contains(p) ? p : null;

    // ------------------------------------------------------------------ editing

    public bool CanEditValue(VariableNodeViewModel node) =>
        node.Kind == VariableRowKind.Variable && !node.IsError && node.ParentReference > 0 && node.Generation == Generation
        && _session is { State: DebugSessionState.Stopped, Capabilities.SupportsSetVariable: true };

    public void BeginEditValue(VariableNodeViewModel node)
    {
        if (!CanEditValue(node)) return;
        node.BeginEdit(EditField.Value, node.FullValue);
    }

    public void BeginEditExpression(VariableNodeViewModel node)
    {
        if (node.Kind is not (VariableRowKind.Watch or VariableRowKind.NewWatch)) return;
        node.BeginEdit(EditField.Expression, node.Kind == VariableRowKind.NewWatch ? "" : node.Name);
    }

    public void CancelEdit(VariableNodeViewModel node) => node.EndEdit();

    /// <summary>Commits the inline editor of <paramref name="node"/>. A value edit calls <c>setVariable</c>; a failure stays inline and the editor stays open.</summary>
    public async Task CommitEditAsync(VariableNodeViewModel node)
    {
        if (!node.IsEditing) return;
        var text = node.EditText;
        if (node.IsEditingExpression)
        {
            node.EndEdit();
            if (ExpressionCommitted is { } commit) await commit(node, text);
            return;
        }
        var session = _session;
        if (session is null || !CanEditValue(node)) { node.EditError = "The value can no longer be edited."; return; }
        var generation = Generation;
        var ct = _cts.Token;
        node.EditError = null;
        try
        {
            var result = await _ui.OnUi(session.SetVariableAsync(node.ParentReference, node.Name, text, ct), ct);
            if (!IsLive(generation)) return;
            ApplyEditedValue(node, result);
            node.EndEdit();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsLive(generation)) node.EditError = ex.Message; // shown inline, under the editor
        }
    }

    private void ApplyEditedValue(VariableNodeViewModel node, DebugVariable result)
    {
        var wasExpanded = node.IsExpanded;
        if (wasExpanded) Collapse(node);
        node.ClearChildren();
        node.ChildrenLoaded = false;
        node.MoreRow = null;
        node.NextStart = 0;
        node.FirstChildName = null;
        node.SetValue(result.Value, result.Type ?? node.Type, result.VariablesReference, result.NamedVariables, result.IndexedVariables);
        node.IsChanged = true;
        node.IsStale = false;
        RememberExpanded(node.Path, false);
        if (node.Parent is not null) TrackValue(node, result.Value);
    }

    // ------------------------------------------------------------------ copy / watch / radix

    public async Task CopyValueAsync(VariableNodeViewModel node)
    {
        if (_s.Window is { } window && node.IsDataRow) await window.CopyToClipboardAsync(node.FullValue);
    }

    public async Task CopyExpressionAsync(VariableNodeViewModel node)
    {
        if (_s.Window is { } window && node.IsDataRow) await window.CopyToClipboardAsync(ExpressionOf(node));
    }

    /// <summary>The expression that re-creates this value in a watch: the adapter's <c>evaluateName</c> when it gave one, else the name.</summary>
    public static string ExpressionOf(VariableNodeViewModel node) => node.Kind == VariableRowKind.Watch ? node.Name : node.EvaluateName ?? node.Name;

    public void AddToWatch(VariableNodeViewModel node)
    {
        if (node.Kind == VariableRowKind.Variable) _s.Watches.Add(ExpressionOf(node));
    }

    public void ViewAs(VariableNodeViewModel node, NumberFormat? format) => node.FormatOverride = format;

    // ------------------------------------------------------------------ lifetime

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Rows.CollectionChanged -= OnRowsChanged;
        try { _cts.Cancel(); }
        catch (AggregateException) { }
        _cts.Dispose();
        _session = null;
        _holder = null;
        _selected = null;
        Rows.Reset();
        _expanded.Clear();
        _previous = null;
        _current.Clear();
        _epochSnapshots.Clear();
        _baselines.Clear();
        _baselineOrder.Clear();
        RefreshRequested = null;
        ExpressionCommitted = null;
        ReorderRequested = null;
        DeleteRequested = null;
        MoveRequested = null;
        DeleteAllRequested = null;
    }
}
