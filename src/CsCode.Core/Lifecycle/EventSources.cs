namespace CsCode.Core.Lifecycle;

/// <summary>Ready-made <see cref="IEventSource{T}"/> values.</summary>
public static class EventSource
{
    /// <summary>
    /// A source that never fires. Subscribing stores nothing (the token is <see cref="Disposable.Empty"/>), so it is the safe default for an
    /// optional event of an interface (a default interface member) that an implementation does not support.
    /// </summary>
    public static IEventSource<T> Never<T>() => NeverSource<T>.Instance;

    private sealed class NeverSource<T> : IEventSource<T>
    {
        public static readonly NeverSource<T> Instance = new();
        public IDisposable Subscribe(Action<T> handler) => Disposable.Empty;
    }
}
