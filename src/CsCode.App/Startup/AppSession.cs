using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CsCode.Core.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.Startup;

/// <summary>
/// What a session needs from the desktop lifetime. Avalonia's lifetime interfaces cannot be implemented outside Avalonia, so the app adapts its
/// lifetime (<see cref="AvaloniaDesktopHost"/>) and tests drive the same session with their own host.
/// </summary>
public interface IDesktopHost
{
    IReadOnlyList<string> Args { get; }
    Window? MainWindow { get; set; }

    /// <summary>From now on the process ends only through <see cref="Shutdown"/> (closing the last window no longer ends it by itself).</summary>
    void TakeOverExit();

    /// <summary>Raised when the lifetime ends (also when something else ended it).</summary>
    event EventHandler? Exited;

    void Shutdown(int exitCode);
}

/// <summary><see cref="IDesktopHost"/> over Avalonia's classic desktop lifetime (one per process, like the lifetime itself).</summary>
public sealed class AvaloniaDesktopHost : IDesktopHost
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;

    public AvaloniaDesktopHost(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        desktop.Exit += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<string> Args => _desktop.Args ?? [];
    public Window? MainWindow { get => _desktop.MainWindow; set => _desktop.MainWindow = value; }
    public event EventHandler? Exited;
    public void TakeOverExit() => _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    public void Shutdown(int exitCode) => _desktop.Shutdown(exitCode);
}

/// <summary>
/// One run of the desktop app, from the first frame to the exit. The container is built off the UI thread while Avalonia finishes starting
/// (<see cref="AppStartup.BuildServicesAsync"/>), the main window opens when it is ready, and the exit is ours
/// (<see cref="ShutdownMode.OnExplicitShutdown"/>): when the main window has closed, services shut down gracefully and bounded
/// (<see cref="AppShutdown"/>) before the lifetime is ended. UI-thread object.
/// </summary>
public sealed class AppSession
{
    private readonly IDesktopHost _desktop;
    private readonly CrashHandler _crash;
    private readonly Action<IServiceCollection>? _configure;
    private readonly TimeSpan _deadline;
    private readonly Background.BackgroundHost? _background;
    private readonly TaskCompletionSource<ShutdownReport?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ServiceProvider? _container;
    private int _exitCode;
    private int _shuttingDown;

    /// <param name="background">Background mode (a session process or the warm spare): the host decides when the window is shown; closing it may
    /// only hide it (<see cref="Background.SessionWindowController"/>). Null: the window shows at once and closing it ends the run.</param>
    public AppSession(IDesktopHost desktop, CrashHandler crash, Action<IServiceCollection>? configure, TimeSpan deadline, Background.BackgroundHost? background = null)
    {
        _desktop = desktop;
        _crash = crash;
        _configure = configure;
        _deadline = deadline;
        _background = background;
    }

    public IServiceProvider? Services => _container;

    /// <summary>The main window, or the crash window when startup failed; null until the services are ready.</summary>
    public Window? Window { get; private set; }

    /// <summary>Completes once the lifetime was ended, with the orderly shutdown's report (null when there was nothing to shut down).</summary>
    public Task<ShutdownReport?> Ended => _ended.Task;

    public void Start()
    {
        _desktop.TakeOverExit();
        _desktop.Exited += OnLifetimeExit;
        var services = AppStartup.BuildServicesAsync(_desktop.Args, _configure);
        Dispatcher.UIThread.Post(() => _ = OpenWindowAsync(services));
    }

    /// <summary>
    /// Never faults (its task is discarded): whatever fails on the way to the first window, the user gets the crash window, or, when not even that can
    /// be shown, a fatal crash log, a line on stderr and exit code 1. With <see cref="ShutdownMode.OnExplicitShutdown"/> nothing else would end the
    /// process, so a lost exception here would leave it running without a window.
    /// </summary>
    private async Task OpenWindowAsync(Task<ServiceProvider> pending)
    {
        Exception failure;
        try
        {
            _container = await pending.ConfigureAwait(true);
            if (_background is { } background)
            {
                var main = new MainWindow(_container);
                Present(main, show: false);
                _crash.Reported += OnCrashReported;
                await background.AttachAsync(main, _container).ConfigureAwait(true); // shows it, unless it is the spare or started hidden
                return;
            }
            Present(new MainWindow(_container));
            _crash.Reported += OnCrashReported; // from now on failures are shown in the main window
            return;
        }
        catch (Exception ex) { failure = ex; }

        // Startup failure (a module the shell needs is not registered, the windowing system refused the window, ...): never a silent exit, never a hang.
        _exitCode = 1;
        var log = _crash.Report(failure, fatal: true, source: "Startup");
        // A session process (background mode): the launcher waiting for its window gets the reason now, and `cscode --quit` can end it.
        _background?.StartupFailed(CrashHandler.Summarize(failure), log, () => Dispatcher.UIThread.Post(() => _ = ShutdownAsync()));
        DiscardFailedMainWindow();
        try
        {
            Present(new CrashWindow(CrashHandler.Summarize(failure), log));
            return; // the run ends when the user closes it
        }
        catch (Exception ex)
        {
            _crash.Report(ex, fatal: true, source: "Startup");
            Console.Error.WriteLine($"CsCode could not start: {CrashHandler.Summarize(failure)}\nLog: {log ?? "(not written)"}");
        }
        await ShutdownAsync().ConfigureAwait(true);
    }

    /// <summary>Makes <paramref name="window"/> the session's window; the run ends when it closes (only once it was shown).</summary>
    private void Present(Window window, bool show = true)
    {
        Window = window;
        _desktop.MainWindow = window;
        if (show) window.Show();
        window.Closed += (_, _) => _ = ShutdownAsync();
    }

    /// <summary>A main window that failed while opening is closed (no prompts: nothing was edited yet) so only the crash window remains.</summary>
    private void DiscardFailedMainWindow()
    {
        if (Window is not MainWindow failed) return;
        try { failed.CloseWithoutPrompt(); }
        catch (Exception ex) { _crash.Report(ex, fatal: false, source: "Startup"); }
    }

    private async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0) return;
        _crash.Reported -= OnCrashReported;
        ShutdownReport? report = null;
        try
        {
            if (_container is { } container) report = await AppShutdown.RunAsync(container, _deadline).ConfigureAwait(true);
        }
        catch (Exception ex) { _crash.Report(ex, fatal: false, source: "Shutdown"); }
        finally
        {
            _container = null;
            _desktop.Exited -= OnLifetimeExit;
            _desktop.Shutdown(_exitCode);
            _ended.TrySetResult(report);
        }
    }

    /// <summary>The lifetime ended without the orderly path (someone called <c>Shutdown</c> directly): kill tracked children, write state, leave.</summary>
    private void OnLifetimeExit(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0) return;
        _crash.Reported -= OnCrashReported;
        if (_container is { } container) AppShutdown.RunAbrupt(container);
        _ended.TrySetResult(null);
    }

    private void OnCrashReported(string summary, string path, bool fatal) => Dispatcher.UIThread.Post(async () =>
    {
        if (Volatile.Read(ref _shuttingDown) != 0 || _container?.GetService<IWindowService>() is not { } window) return;
        var choice = await window.ShowMessageAsync(MessageSeverity.Error, $"An unexpected error occurred: {summary}\nDetails were written to {path}",
            [new MessageAction("Copy Log Path"), new MessageAction("Dismiss", IsCloseAffordance: true)]).ConfigureAwait(true);
        if (choice == "Copy Log Path") await window.CopyToClipboardAsync(path).ConfigureAwait(true);
    });
}
