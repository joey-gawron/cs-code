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

/// <summary>
/// Posts to the UI dispatcher and, when a test (or a diagnostic session) has installed <see cref="Observer"/>, reports how long each of the
/// editor's own UI jobs ran — the evidence behind "no UI step longer than a few milliseconds". Free when nobody observes.
/// </summary>
internal static class UiJobs
{
    /// <summary>Receives (the job's delegate, its duration) after each posted job. Null in production.</summary>
    public static Action<Delegate, TimeSpan>? Observer { get; set; }

    public static void Post(Action action, DispatcherPriority priority)
    {
        var observer = Observer;
        if (observer is null)
        {
            Dispatcher.UIThread.Post(action, priority);
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                action();
            }
            finally
            {
                observer(action, System.Diagnostics.Stopwatch.GetElapsedTime(start));
            }
        }, priority);
    }
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
            // Only resolvable from the UI thread itself: refresh the cached thread whenever the UI thread asks (a headless test
            // session may recreate it), and serve the cache to everybody else.
            if (CheckAccess()) return _owner = Thread.CurrentThread;
            return _owner ?? throw new InvalidOperationException("UI thread not captured yet; first use must be on the UI thread.");
        }
    }

    public void Post(Action action) => UiJobs.Post(action, DispatcherPriority.Normal);

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
