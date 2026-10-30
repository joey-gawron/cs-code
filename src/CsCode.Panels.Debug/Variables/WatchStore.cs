using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Panels.Debug.Variables;

/// <summary>
/// The user's watch list. The expressions themselves live in <see cref="IDebugService.Watches"/> (so they survive the Watch window being
/// hidden and recreated); this adds the two things the service contract lacks: persistence per workspace in <see cref="IStateStorage"/>
/// and a change notification so every open Watch window (and the <c>debug.addToWatch</c> command) stays in sync.
/// <para>
/// The service's list is app-wide but the saved list is per workspace, so when another folder is opened in the same window the service's
/// list is replaced by that folder's saved one (the storage has already switched its workspace scope by then, so nothing is saved in
/// between: saving would write the old folder's list into the new folder). The storage swaps its scope before the workspace service
/// announces the change (and the other workspace dependents may take a while), so an edit in that window is saved only through
/// <see cref="IWorkspaceStateScope"/>, which refuses once the scope belongs to another folder: the edit then stays in the old folder's list
/// on screen until the switch replaces it, and is not saved anywhere (the old folder's scope is gone).
/// </para>
/// </summary>
public sealed class WatchStore : IDisposable
{
    public const string StateKey = "debug.watches";
    private const int MaxRestored = 500;

    private readonly IDebugService _debug;
    private readonly IStateStorage? _state;
    private readonly IWorkspaceService? _workspace;
    private readonly Emitter<object?> _changed = new();
    private readonly DisposableStore _subs = new();
    // Serialises edits (UI thread) with a folder switch (whatever thread the workspace service fires on): an edit never saves the old
    // folder's list after the switch replaced it.
    private readonly object _gate = new();
    private bool _loaded;
    private Uri? _loadedFolder;

    public WatchStore(IDebugService debug, IStateStorage? state, IWorkspaceService? workspace = null)
    {
        _debug = debug;
        _state = state;
        _workspace = workspace;
        if (workspace is not null) _subs.Add(workspace.FoldersChanged.Subscribe(_ => OnFoldersChanged()));
    }

    public IEventSource<object?> Changed => _changed;

    public IReadOnlyList<WatchExpression> Items => _debug.Watches;

    private Uri? PrimaryFolder => _workspace?.Folders is { Count: > 0 } folders ? folders[0] : null;

    /// <summary>Restores the persisted list into the debug service once, and only if the service has none yet (it outlives panels).</summary>
    public void EnsureLoaded()
    {
        lock (_gate) LoadOnce();
    }

    /// <summary>
    /// Under <see cref="_gate"/>. Every edit calls it first, wherever it comes from (Locals' "Add Watch", the command): saving before the
    /// load would write the service's list over the folder's saved one, and the load would then find a non-empty service and skip it.
    /// </summary>
    private void LoadOnce()
    {
        if (_loaded) return;
        _loaded = true;
        _loadedFolder = PrimaryFolder;
        if (_debug.Watches.Count > 0) return;
        foreach (var expression in ReadSaved()) _debug.AddWatch(expression);
    }

    /// <summary>Another folder was opened: show its watches instead of carrying the previous folder's over (and later saving them into it).</summary>
    private void OnFoldersChanged()
    {
        lock (_gate)
        {
            if (!_loaded) return; // nothing was taken from the previous folder yet: the first EnsureLoaded reads the new one
            var folder = PrimaryFolder;
            if (folder == _loadedFolder) return; // a root was added or removed: same workspace, same saved list
            _loadedFolder = folder;
            foreach (var w in _debug.Watches.ToArray()) _debug.RemoveWatch(w);
            foreach (var expression in ReadSaved()) _debug.AddWatch(expression);
        }
        _changed.Fire(null);
    }

    private List<string> ReadSaved()
    {
        if (_state is null) return [];
        List<string>? saved;
        try { saved = _state.Get<List<string>>(StateKey, StorageScope.Workspace); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or NotSupportedException) { return []; }
        return saved is null ? [] : [.. saved.Where(e => !string.IsNullOrWhiteSpace(e)).Take(MaxRestored)];
    }

    public WatchExpression Add(string expression)
    {
        WatchExpression watch;
        lock (_gate)
        {
            LoadOnce();
            watch = _debug.AddWatch(expression.Trim());
            Save();
        }
        _changed.Fire(null);
        return watch;
    }

    public void Remove(WatchExpression watch)
    {
        lock (_gate)
        {
            LoadOnce();
            _debug.RemoveWatch(watch);
            Save();
        }
        _changed.Fire(null);
    }

    public void Update(WatchExpression watch, string expression)
    {
        lock (_gate)
        {
            LoadOnce();
            watch.Expression = expression.Trim();
            Save();
        }
        _changed.Fire(null);
    }

    public void RemoveAll()
    {
        lock (_gate)
        {
            LoadOnce();
            foreach (var w in _debug.Watches.ToArray()) _debug.RemoveWatch(w);
            Save();
        }
        _changed.Fire(null);
    }

    /// <summary>
    /// Moves a watch to <paramref name="newIndex"/>. The service has no reorder operation, so the list is rebuilt in the new order
    /// (new <see cref="WatchExpression"/> instances; the expressions, and therefore the persisted list, are what matter).
    /// </summary>
    public void Move(WatchExpression watch, int newIndex)
    {
        lock (_gate)
        {
            LoadOnce();
            var order = _debug.Watches.Select(w => w.Expression).ToList();
            var from = _debug.Watches.ToList().FindIndex(w => ReferenceEquals(w, watch));
            if (from < 0) return;
            newIndex = Math.Clamp(newIndex, 0, order.Count - 1);
            if (newIndex == from) return;
            var moved = order[from];
            order.RemoveAt(from);
            order.Insert(newIndex, moved);
            foreach (var w in _debug.Watches.ToArray()) _debug.RemoveWatch(w);
            foreach (var e in order) _debug.AddWatch(e);
            Save();
        }
        _changed.Fire(null);
    }

    private void Save()
    {
        if (_state is null) return;
        var expressions = _debug.Watches.Select(w => w.Expression).ToList();
        try
        {
            if (_workspace is not null && _state is IWorkspaceStateScope scoped) scoped.TrySetForFolder(_loadedFolder, StateKey, expressions); // refused mid-switch
            else _state.Set(StateKey, expressions, StorageScope.Workspace);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { /* persistence is best effort; the list itself is intact */ }
    }

    public void Dispose() => _subs.Dispose();
}
