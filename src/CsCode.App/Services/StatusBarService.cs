using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Threading;

namespace CsCode.App.Services;

/// <summary>
/// Status bar registry. Thread-safe; <see cref="Changed"/> is raised on the UI thread and coalesced (a burst of updates from
/// a language server produces one notification), so the bar view re-renders at most once per dispatcher turn.
/// </summary>
public sealed class StatusBarService(IUiDispatcher ui) : IStatusBarService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StatusBarItemModel> _items = new(StringComparer.Ordinal);
    private readonly Emitter<IReadOnlyList<StatusBarItemModel>> _changed = new();
    private IReadOnlyList<StatusBarItemModel>? _snapshot;

    public IEventSource<IReadOnlyList<StatusBarItemModel>> Changed => _changed;

    public IReadOnlyList<StatusBarItemModel> Items
    {
        get
        {
            lock (_gate)
            {
                // Left items by descending priority (first = leftmost), right items likewise (first = rightmost), like VS Code.
                return _snapshot ??= _items.Values
                    .OrderBy(i => i.Alignment)
                    .ThenByDescending(i => i.Priority)
                    .ThenBy(i => i.Id, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public IDisposable Set(StatusBarItemModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            _items[item.Id] = item;
            _snapshot = null;
        }
        Notify();
        return Disposable.Create(() =>
        {
            bool removed;
            lock (_gate)
            {
                // Only remove if nobody replaced the item since (a newer Set owns the id now).
                removed = _items.TryGetValue(item.Id, out var cur) && ReferenceEquals(cur, item) && _items.Remove(item.Id);
                if (removed) _snapshot = null;
            }
            if (removed) Notify();
        });
    }

    private void Notify() => ui.PostCoalesced("statusbar.changed", () => _changed.Fire(Items));
}
