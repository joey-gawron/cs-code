using CommunityToolkit.Mvvm.ComponentModel;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Panels.Debug.Infrastructure;

namespace CsCode.Panels.Debug.Variables;

/// <summary>
/// Watch window. Rows are the user's expressions (<see cref="WatchStore"/>, persisted per workspace) plus an empty last row for typing a new
/// one. While stopped every expression is evaluated in the focused frame (context <c>watch</c>) on each stop, step and frame switch;
/// results are expandable like any variable. Errors show in red with the adapter's message. While the debuggee runs the last values stay,
/// dimmed. The expression text is edited inline.
/// </summary>
public sealed class WatchViewModel : ObservableObject, IDisposable
{
    private readonly DebugPanelServices _s;
    private readonly DebugFocusTracker _tracker;
    private readonly DisposableStore _subs = new();
    private readonly string _syncKey = "debug.watch.sync." + Guid.NewGuid().ToString("N");
    private readonly VariableNodeViewModel _newRow;
    private DebugFocus _focus;
    private IDebugSession? _evaluatedSession;
    private long _evaluatedEpoch = -1;
    private int? _evaluatedFrame;
    private int _disposed;

    public WatchViewModel(DebugPanelServices services)
    {
        _s = services;
        Tree = new VariablesTreeViewModel(services);
        Tree.ExpressionCommitted = OnExpressionCommitted;
        Tree.RefreshRequested = RefreshAll;
        Tree.DeleteRequested = Remove;
        Tree.MoveRequested = Move;
        Tree.DeleteAllRequested = RemoveAll;
        _newRow = Tree.NewNode(VariableRowKind.NewWatch, null, "", "watch:", Tree.Generation);
        services.Watches.EnsureLoaded();
        SyncRows();
        _subs.Add(services.Watches.Changed.Subscribe(_ => services.Ui.PostCoalesced(_syncKey, SyncAndEvaluate)));
        _tracker = new DebugFocusTracker(services, OnFocus);
        _tracker.Start();
    }

    public VariablesTreeViewModel Tree { get; }

    /// <summary>The empty last row (the view focuses its editor for "Add item to watch").</summary>
    public VariableNodeViewModel NewRow => _newRow;

    // ------------------------------------------------------------------ rows

    /// <summary>
    /// Makes the root rows match the watch list: existing rows are reused by expression (so values, change baselines and expansion survive
    /// adding, removing or reordering others); new expressions get new rows. Returns whether the root rows changed.
    /// </summary>
    internal bool SyncRows()
    {
        var items = _s.Watches.Items;
        var pool = new Dictionary<string, Queue<VariableNodeViewModel>>(StringComparer.Ordinal);
        var currentRoots = new List<VariableNodeViewModel>();
        foreach (var row in Tree.Rows)
        {
            if (row.Depth != 0 || row.Kind != VariableRowKind.Watch) continue;
            currentRoots.Add(row);
            if (!pool.TryGetValue(row.Name, out var q)) pool[row.Name] = q = new Queue<VariableNodeViewModel>();
            q.Enqueue(row);
        }

        var desired = new List<VariableNodeViewModel>(items.Count);
        foreach (var w in items)
        {
            if (pool.TryGetValue(w.Expression, out var q) && q.TryDequeue(out var reused))
            {
                reused.Watch = w;
                desired.Add(reused);
            }
            else desired.Add(Tree.NewNode(VariableRowKind.Watch, null, w.Expression, "watch:" + w.Expression, Tree.Generation, w));
        }

        var hasNewRow = Tree.Rows.Count > 0 && ReferenceEquals(Tree.Rows[^1], _newRow);
        if (hasNewRow && currentRoots.SequenceEqual(desired)) return false;

        var rows = new List<VariableNodeViewModel>();
        foreach (var node in desired) VariablesTreeViewModel.FlattenInto(rows, node);
        rows.Add(_newRow);
        var selectedName = Tree.SelectedRow?.Kind == VariableRowKind.Watch ? Tree.SelectedRow.Name : null;
        Tree.Rows.ReplaceAll(rows);
        if (selectedName is not null) Tree.SelectedRow = desired.FirstOrDefault(n => n.Name == selectedName);
        return true;
    }

    private void SyncAndEvaluate()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        SyncRows();
        if (_focus.HasFrame) _ = EvaluateAllAsync(_focus);
    }

    // ------------------------------------------------------------------ following the debugger

    private void OnFocus(DebugFocus focus)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _focus = focus;
        SyncRows(); // someone may have used IDebugService.AddWatch directly
        if (!focus.HasFrame)
        {
            _evaluatedSession = null;
            _evaluatedEpoch = -1;
            _evaluatedFrame = null;
            Tree.Invalidate(null, keepRoots: true);
            return;
        }
        if (ReferenceEquals(focus.Session, _evaluatedSession) && focus.Epoch == _evaluatedEpoch && focus.FrameId == _evaluatedFrame) return;
        _ = EvaluateAllAsync(focus);
    }

    /// <summary>Re-evaluates every expression in the focused frame.</summary>
    public void RefreshAll()
    {
        if (!_focus.HasFrame) return;
        _evaluatedSession = null;
        _ = EvaluateAllAsync(_focus);
    }

    internal async Task EvaluateAllAsync(DebugFocus focus)
    {
        var session = focus.Session!;
        var frameId = focus.FrameId!.Value;
        _evaluatedSession = session;
        _evaluatedEpoch = focus.Epoch;
        _evaluatedFrame = frameId;
        var token = Tree.BeginReload(session, focus.Epoch, "watch");
        var roots = Tree.Rows.Where(r => r.Depth == 0 && r.Kind == VariableRowKind.Watch).ToList();
        await Task.WhenAll(roots.Select(r => EvaluateOneAsync(r, session, frameId, token)));
    }

    private async Task EvaluateOneAsync(VariableNodeViewModel node, IDebugSession session, int frameId, LoadToken token)
    {
        node.Generation = token.Generation;
        node.IsStale = false;
        try
        {
            var result = await _s.Ui.OnUi(session.EvaluateAsync(node.Name, frameId, "watch", token.Cancellation), token.Cancellation);
            if (token.Generation != Tree.Generation) return;
            node.SetFrom(result);
            node.IsChanged = node.BaselineValue is not null && !string.Equals(node.BaselineValue, result.Result, StringComparison.Ordinal);
            await Tree.RestoreExpansionAsync([node], token.Generation);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.Generation != Tree.Generation) return;
            node.SetError(ex is InvalidOperationException && ex.Message.Contains("no longer valid", StringComparison.OrdinalIgnoreCase) ? "The debuggee resumed." : ex.Message);
            node.IsChanged = false;
        }
    }

    // ------------------------------------------------------------------ editing

    private Task OnExpressionCommitted(VariableNodeViewModel node, string text)
    {
        text = text.Trim();
        if (node.Kind == VariableRowKind.NewWatch)
        {
            if (text.Length > 0) _s.Watches.Add(text);
        }
        else if (node.Watch is { } watch)
        {
            if (text.Length == 0) _s.Watches.Remove(watch);
            else if (!string.Equals(text, watch.Expression, StringComparison.Ordinal)) _s.Watches.Update(watch, text);
        }
        return Task.CompletedTask;
    }

    /// <summary>Starts typing a new expression (Ctrl+Alt+W while the window is open).</summary>
    public void BeginAdd()
    {
        Tree.SelectedRow = _newRow;
        Tree.BeginEditExpression(_newRow);
    }

    public void BeginRename(VariableNodeViewModel node) => Tree.BeginEditExpression(node);

    public void Remove(VariableNodeViewModel node)
    {
        if (node.Kind == VariableRowKind.Watch && node.Watch is { } w) _s.Watches.Remove(w);
    }

    public void RemoveSelected()
    {
        if (Tree.SelectedRow is { Kind: VariableRowKind.Watch } node) Remove(node);
    }

    public void RemoveAll() => _s.Watches.RemoveAll();

    /// <summary>Moves a watch row up (-1) or down (+1) within the list.</summary>
    public void Move(VariableNodeViewModel node, int delta)
    {
        if (node.Kind != VariableRowKind.Watch || node.Watch is not { } w) return;
        var index = _s.Watches.Items.ToList().FindIndex(x => ReferenceEquals(x, w));
        if (index < 0) return;
        _s.Watches.Move(w, index + delta);
    }

    /// <summary>Drag-and-drop reorder: puts <paramref name="node"/> where <paramref name="target"/> is.</summary>
    public void MoveTo(VariableNodeViewModel node, VariableNodeViewModel target)
    {
        if (node.Kind != VariableRowKind.Watch || node.Watch is not { } w || target.Kind != VariableRowKind.Watch || target.Watch is not { } t) return;
        var items = _s.Watches.Items.ToList();
        var to = items.FindIndex(x => ReferenceEquals(x, t));
        if (to >= 0) _s.Watches.Move(w, to);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _tracker.Dispose();
        _subs.Dispose();
        Tree.Dispose();
        _evaluatedSession = null;
        _focus = default;
    }
}
