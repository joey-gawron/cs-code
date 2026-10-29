namespace CsCode.Core.Lifecycle;

/// <summary>
/// Leak-safe event: subscribers hold an <see cref="IDisposable"/> that unsubscribes. Prefer this over
/// C# <c>event</c> for any cross-service notification (a forgotten <c>-=</c> on a long-lived publisher is the
/// classic managed memory leak; here the subscription token is explicit and DisposableStore-friendly).
/// </summary>
public interface IEventSource<out T>
{
    IDisposable Subscribe(Action<T> handler);
}

/// <summary>Publisher side of <see cref="IEventSource{T}"/>. Thread-safe; handler exceptions are isolated.</summary>
public sealed class Emitter<T> : IEventSource<T>
{
    private readonly object _gate = new();
    private Action<T>[] _handlers = [];

    /// <summary>Raised when a handler throws. Default: swallowed (a bad subscriber must not break the publisher).</summary>
    public Action<Exception>? OnHandlerError { get; set; }

    public int SubscriberCount => Volatile.Read(ref _handlers).Length;

    public IEventSource<T> Event => this;

    public IDisposable Subscribe(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            var next = new Action<T>[_handlers.Length + 1];
            _handlers.CopyTo(next, 0);
            next[^1] = handler;
            _handlers = next;
        }
        return Disposable.Create(() =>
        {
            lock (_gate)
            {
                var idx = Array.IndexOf(_handlers, handler);
                if (idx < 0) return;
                var next = new Action<T>[_handlers.Length - 1];
                Array.Copy(_handlers, 0, next, 0, idx);
                Array.Copy(_handlers, idx + 1, next, idx, _handlers.Length - idx - 1);
                _handlers = next;
            }
        });
    }

    public void Fire(T value)
    {
        foreach (var h in Volatile.Read(ref _handlers))
        {
            try { h(value); }
            catch (Exception ex) { OnHandlerError?.Invoke(ex); }
        }
    }

    public void Clear()
    {
        lock (_gate) _handlers = [];
    }
}
