namespace CsCode.Core.Lifecycle;

/// <summary>Cheap IDisposable wrappers. Every subscription/registration in CsCode returns one of these.</summary>
public static class Disposable
{
    public static readonly IDisposable Empty = new EmptyDisposable();

    public static IDisposable Create(Action dispose) => new ActionDisposable(dispose);

    public static IDisposable Combine(params IDisposable[] items) => new ActionDisposable(() =>
    {
        foreach (var d in items) d.Dispose();
    });

    private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>
/// Owns a set of disposables and disposes them all (reverse order) exactly once.
/// Anything added after disposal is disposed immediately, so late registrations can never leak.
/// </summary>
public sealed class DisposableStore : IDisposable
{
    private readonly object _gate = new();
    private List<IDisposable>? _items = new();

    public bool IsDisposed { get { lock (_gate) return _items is null; } }

    public T Add<T>(T item) where T : IDisposable
    {
        lock (_gate)
        {
            if (_items is not null)
            {
                _items.Add(item);
                return item;
            }
        }
        item.Dispose();
        return item;
    }

    public void Dispose()
    {
        List<IDisposable>? items;
        lock (_gate)
        {
            items = _items;
            _items = null;
        }
        if (items is null) return;
        List<Exception>? errors = null;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            try { items[i].Dispose(); }
            catch (Exception ex) { (errors ??= new()).Add(ex); }
        }
        if (errors is { Count: > 0 }) throw new AggregateException(errors);
    }
}
