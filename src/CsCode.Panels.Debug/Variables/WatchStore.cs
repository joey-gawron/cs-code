using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.Panels.Debug.Variables;

/// <summary>
/// The user's watch list. The expressions themselves live in <see cref="IDebugService.Watches"/> (so they survive the Watch window being
/// hidden and recreated); this adds the two things the service contract lacks: persistence per workspace in <see cref="IStateStorage"/>
/// and a change notification so every open Watch window (and the <c>debug.addToWatch</c> command) stays in sync.
/// </summary>
public sealed class WatchStore
{
    public const string StateKey = "debug.watches";

    private readonly IDebugService _debug;
    private readonly IStateStorage? _state;
    private readonly Emitter<object?> _changed = new();
    private int _loaded;

    public WatchStore(IDebugService debug, IStateStorage? state)
    {
        _debug = debug;
        _state = state;
    }

    public IEventSource<object?> Changed => _changed;

    public IReadOnlyList<WatchExpression> Items => _debug.Watches;

    /// <summary>Restores the persisted list into the debug service once, and only if the service has none yet (it outlives panels).</summary>
    public void EnsureLoaded()
    {
        if (Interlocked.Exchange(ref _loaded, 1) != 0) return;
        if (_debug.Watches.Count > 0 || _state is null) return;
        List<string>? saved;
        try { saved = _state.Get<List<string>>(StateKey, StorageScope.Workspace); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or NotSupportedException) { return; }
        if (saved is null) return;
        foreach (var expression in saved.Where(e => !string.IsNullOrWhiteSpace(e)).Take(500)) _debug.AddWatch(expression);
    }

    public WatchExpression Add(string expression)
    {
        var watch = _debug.AddWatch(expression.Trim());
        Save();
        _changed.Fire(null);
        return watch;
    }

    public void Remove(WatchExpression watch)
    {
        _debug.RemoveWatch(watch);
        Save();
        _changed.Fire(null);
    }

    public void Update(WatchExpression watch, string expression)
    {
        watch.Expression = expression.Trim();
        Save();
        _changed.Fire(null);
    }

    public void RemoveAll()
    {
        foreach (var w in _debug.Watches.ToArray()) _debug.RemoveWatch(w);
        Save();
        _changed.Fire(null);
    }

    /// <summary>
    /// Moves a watch to <paramref name="newIndex"/>. The service has no reorder operation, so the list is rebuilt in the new order
    /// (new <see cref="WatchExpression"/> instances; the expressions, and therefore the persisted list, are what matter).
    /// </summary>
    public void Move(WatchExpression watch, int newIndex)
    {
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
        _changed.Fire(null);
    }

    private void Save()
    {
        if (_state is null) return;
        try { _state.Set(StateKey, _debug.Watches.Select(w => w.Expression).ToList(), StorageScope.Workspace); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { /* persistence is best effort; the list itself is intact */ }
    }
}
