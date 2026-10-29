using Avalonia.Threading;

namespace CsCode.Editor.Threading;

/// <summary>
/// The editor's view of "the UI thread". The AvaloniaEdit <c>TextDocument</c> and every control are owned by it; everything
/// else in the module reaches the UI only through this seam, which is what makes the document layer testable without a
/// running Avalonia application (the test project supplies a thread-affinity implementation).
/// </summary>
public interface IUiThread
{
    /// <summary>True when the caller is on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>The OS thread that owns the document/UI objects (for <c>TextDocument.SetOwnerThread</c>).</summary>
    Thread OwnerThread { get; }

    /// <summary>Fire-and-forget: runs <paramref name="action"/> on the UI thread (queued, never inline).</summary>
    void Post(Action action);

    /// <summary>Runs on the UI thread. Inline when already there. The returned task completes when the action has run.</summary>
    Task InvokeAsync(Action action);

    /// <summary>
    /// Blocking variant for NON-UI callers that implement a synchronous contract (e.g. <c>ITextDocument.ApplyEdits</c> called
    /// from a language-server thread). On the UI thread it simply runs inline, so the UI thread itself never waits.
    /// </summary>
    void Invoke(Action action);
}

/// <summary>Production implementation over <see cref="Dispatcher.UIThread"/>.</summary>
public sealed class AvaloniaUiThread : IUiThread
{
    public static readonly AvaloniaUiThread Instance = new();

    private Thread? _owner;

    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public Thread OwnerThread
    {
        get
        {
            if (_owner is { } t) return t;
            // Only resolvable from the UI thread itself; capture lazily the first time anyone on it asks.
            if (!CheckAccess()) throw new InvalidOperationException("UI thread not captured yet; first use must be on the UI thread.");
            return _owner = Thread.CurrentThread;
        }
    }

    public void Post(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Normal);

    public Task InvokeAsync(Action action)
    {
        if (CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    public void Invoke(Action action)
    {
        if (CheckAccess()) action();
        else Dispatcher.UIThread.Invoke(action);
    }
}
