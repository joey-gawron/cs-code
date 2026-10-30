using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
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

    /// <summary>
    /// macOS: the system hands the app documents (Finder's Open With, <c>open file</c>, a drop on the Dock icon) or asks it to come back (a click
    /// on its Dock icon). Not raised on other platforms.
    /// </summary>
    event EventHandler<DesktopActivation>? Activated;

    /// <summary>
    /// macOS: the user or the system asks the app to quit (⌘Q, Quit in the Dock menu, log out, restart, shut down). Setting
    /// <see cref="System.ComponentModel.CancelEventArgs.Cancel"/> keeps it running. Not raised on other platforms, nor by <see cref="Shutdown"/>.
    /// </summary>
    event EventHandler<DesktopQuitRequest>? QuitRequested;

    void Shutdown(int exitCode);
}

/// <summary>What the system asked of a running app (<see cref="IDesktopHost.Activated"/>): open <see cref="Paths"/>, or (none) come to the front.</summary>
public sealed record DesktopActivation(IReadOnlyList<string> Paths)
{
    /// <summary>The Dock icon was clicked (macOS's reopen): show the window.</summary>
    public static DesktopActivation Reopen { get; } = new([]);
}

/// <summary>A quit asked of the app from outside it (<see cref="IDesktopHost.QuitRequested"/>).</summary>
public sealed class DesktopQuitRequest : System.ComponentModel.CancelEventArgs
{
    /// <summary>The desktop session ends (log out, restart, shut down): the system waits for the answer, and a cancel stops the log out.</summary>
    public bool IsOSShutdown { get; init; }
}

/// <summary><see cref="IDesktopHost"/> over Avalonia's classic desktop lifetime (one per process, like the lifetime itself).</summary>
public sealed class AvaloniaDesktopHost : IDesktopHost
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;

    /// <param name="activatable">The application's <see cref="IActivatableLifetime"/> feature (file activation and reopen; macOS only here).</param>
    public AvaloniaDesktopHost(IClassicDesktopStyleApplicationLifetime desktop, IActivatableLifetime? activatable = null)
    {
        _desktop = desktop;
        desktop.Exit += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
        // Only macOS: elsewhere Avalonia's own handling of these stays as it was (an X11 session end closes the window, prompts and all).
        if (!OperatingSystem.IsMacOS()) return;
        desktop.ShutdownRequested += OnShutdownRequested;
        if (activatable is not null) activatable.Activated += OnActivated;
    }

    public IReadOnlyList<string> Args => _desktop.Args ?? [];
    public Window? MainWindow { get => _desktop.MainWindow; set => _desktop.MainWindow = value; }
    public event EventHandler? Exited;
    public event EventHandler<DesktopActivation>? Activated;
    public event EventHandler<DesktopQuitRequest>? QuitRequested;
    public void TakeOverExit() => _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    public void Shutdown(int exitCode) => _desktop.Shutdown(exitCode);

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        var request = new DesktopQuitRequest { IsOSShutdown = MacQuitReason.IsSessionEnding(), Cancel = e.Cancel };
        QuitRequested?.Invoke(this, request);
        e.Cancel = request.Cancel;
    }

    private void OnActivated(object? sender, ActivatedEventArgs e)
    {
        if (ToActivation(e) is { } activation) Activated?.Invoke(this, activation);
    }

    /// <summary>The local paths of a file activation (items without one, such as a file promise not written yet, are left out), or a reopen.</summary>
    internal static DesktopActivation? ToActivation(ActivatedEventArgs e) => e switch
    {
        FileActivatedEventArgs files => new DesktopActivation(files.Files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray()),
        { Kind: ActivationKind.Reopen } => DesktopActivation.Reopen,
        _ => null,
    };
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
    /// <summary>Paths the system handed over before the main window existed (macOS file activation at launch): it opens them.</summary>
    private readonly List<string> _pendingPaths = new();
    /// <summary>A quit asked from outside (⌘Q, the Dock) is under way: its Save prompts are up, and asking again answers nothing.</summary>
    private bool _quitAsked;
    /// <summary>The desktop session ends and the window closed without prompts: its close ends the run synchronously (<see cref="ExitForSystemShutdown"/>).</summary>
    private bool _systemShutdown;

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
        _desktop.Activated += OnActivated;
        _desktop.QuitRequested += OnQuitRequested;
        CsCode.Sessions.SessionTrace.Mark("startup: avalonia initialized");
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
            CsCode.Sessions.SessionTrace.Mark("startup: services ready on the UI thread");
            if (_background is { } background)
            {
                var main = new MainWindow(_container);
                CsCode.Sessions.SessionTrace.Mark("startup: main window constructed");
                Present(main, show: false);
                _crash.Reported += OnCrashReported;
                await background.AttachAsync(main, _container).ConfigureAwait(true); // shows it, unless it is the spare or started hidden
                return;
            }
            var window = new MainWindow(_container);
            Present(window);
            _crash.Reported += OnCrashReported; // from now on failures are shown in the main window
            if (_pendingPaths.Count > 0) window.OpenTargets(ToTargets(_pendingPaths)); // after the command line's, once the layout is restored
            _pendingPaths.Clear();
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
        window.Closed += (_, _) =>
        {
            if (_systemShutdown) ExitForSystemShutdown();
            else _ = ShutdownAsync();
        };
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
            Unsubscribe();
            _desktop.Shutdown(_exitCode);
            _ended.TrySetResult(report);
        }
    }

    /// <summary>The lifetime ended without the orderly path (someone called <c>Shutdown</c> directly): kill tracked children, write state, leave.</summary>
    private void OnLifetimeExit(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0) return;
        _crash.Reported -= OnCrashReported;
        Unsubscribe();
        if (_container is { } container) AppShutdown.RunAbrupt(container);
        _ended.TrySetResult(null);
    }

    /// <summary>
    /// The desktop session ends (log out, restart, shut down) and the window closed without anything unsaved: the system ends the process as soon as
    /// the quit is answered, so there is no time for the orderly exit, only for its synchronous part (tracked children killed, state written). The
    /// layout was saved as the window closed, so the next start restores its tabs.
    /// </summary>
    private void ExitForSystemShutdown()
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0) return;
        _crash.Reported -= OnCrashReported;
        Unsubscribe();
        if (_container is { } container) AppShutdown.RunAbrupt(container);
        _ended.TrySetResult(null);
        // Normally the process is gone before this runs. Should the log out be cancelled after all (another app refused it), nothing would bring the
        // closed window back: the run ends here.
        Dispatcher.UIThread.Post(() => _desktop.Shutdown(_exitCode));
    }

    private void Unsubscribe()
    {
        _desktop.Exited -= OnLifetimeExit;
        _desktop.Activated -= OnActivated;
        _desktop.QuitRequested -= OnQuitRequested;
    }

    // ------------------------------------------------------------------ the system's requests (macOS)

    /// <summary>
    /// Documents handed over by the system (Finder's Open With, <c>open file</c>, a drop on the Dock icon) open as command-line paths do (a folder as
    /// the workspace, files as editors), and the window comes to the front; a click on the Dock icon (no paths) only brings it back. Before the
    /// main window exists the paths wait for it; it opens them once the layout is restored. Background mode shows the session's window through
    /// the launcher's own request (<see cref="Sessions.Ipc.IpcVerbs.Show"/>), which also restores a hidden session's views.
    /// </summary>
    private void OnActivated(object? sender, DesktopActivation activation)
    {
        if (Volatile.Read(ref _shuttingDown) != 0) return;
        if (_background is { } background)
        {
            _ = ShowInSessionAsync(background, activation);
            return;
        }
        switch (Window)
        {
            case null:
                _pendingPaths.AddRange(activation.Paths); // the main window shows itself when it opens
                return;
            case MainWindow main:
                main.BringToFront();
                if (activation.Paths.Count > 0) main.OpenTargets(ToTargets(activation.Paths));
                return;
            case var crash:
                crash.Activate();
                return;
        }
    }

    private async Task ShowInSessionAsync(Background.BackgroundHost background, DesktopActivation activation)
    {
        var request = new Sessions.Ipc.IpcMessage
        {
            Type = Sessions.Ipc.IpcVerbs.Show,
            Paths = activation.Paths.Select(p => new Sessions.Ipc.IpcPath(p)).ToArray(),
        };
        // From the pool, as an IPC request arrives: it checks the paths there and waits (off the UI thread) for the session to be restored.
        try { await Task.Run(() => background.HandleAsync(request, CancellationToken.None)).ConfigureAwait(true); }
        catch (Exception ex) { _crash.Report(ex, fatal: false, source: "Activation"); }
    }

    private static StartupTarget[] ToTargets(IEnumerable<string> paths) => paths.Select(p => new StartupTarget(p)).ToArray();

    /// <summary>
    /// A quit asked from outside the window (⌘Q, Quit in the Dock menu, log out) goes the way File &gt; Exit goes (<see cref="KnownCommands.Quit"/>:
    /// the Save prompts and the orderly exit; in background mode the session's quit with hot exit), never the lifetime's own way, which ends the
    /// process without the orderly exit when nothing holds a window open. The quit is cancelled here and the command ends the run when it closes
    /// the window. A log out with nothing unsaved closes at once (the system waits for no orderly exit: <see cref="ExitForSystemShutdown"/>); one with
    /// unsaved changes is paused (cancelled) with the window at the front, so its Save prompts are seen, and the user is told.
    /// Before the main window exists (and with the crash window) Avalonia's own handling stays.
    /// </summary>
    private void OnQuitRequested(object? sender, DesktopQuitRequest e)
    {
        if (Volatile.Read(ref _shuttingDown) != 0 || Window is not MainWindow main || _container is not { } services) return;
        if (e.IsOSShutdown)
        {
            if (_background is not null) return; // background mode: the session controller handles a system shutdown (OnCloseRequested)
            if (!_quitAsked && !main.HasUnsavedChanges)
            {
                _systemShutdown = true;
                if (main.CloseForSystemShutdown() == ShellController.CloseEnd.Closed) return; // the log out goes on
                // A module holds the close with a question of its own (the terminal's confirmOnExit) and closes again when the user agrees.
                _systemShutdown = false;
                e.Cancel = true;
                main.BringToFront();
                return;
            }
            e.Cancel = true;
            main.BringToFront();
            _ = services.GetService<IWindowService>()?.ShowMessageAsync(MessageSeverity.Warning,
                "CsCode paused the log out: there are unsaved changes. Save or discard them, then log out again.").AsTask();
        }
        else e.Cancel = true;
        if (_quitAsked) return;
        _ = QuitAsync(services);
    }

    private async Task QuitAsync(IServiceProvider services)
    {
        _quitAsked = true;
        try { await services.GetRequiredService<ICommandService>().ExecuteAsync(KnownCommands.Quit).ConfigureAwait(true); }
        catch (Exception ex) { _crash.Report(ex, fatal: false, source: "Quit"); }
        finally { _quitAsked = false; }
    }

    private void OnCrashReported(string summary, string path, bool fatal) => Dispatcher.UIThread.Post(async () =>
    {
        if (Volatile.Read(ref _shuttingDown) != 0 || _container?.GetService<IWindowService>() is not { } window) return;
        var choice = await window.ShowMessageAsync(MessageSeverity.Error, $"An unexpected error occurred: {summary}\nDetails were written to {path}",
            [new MessageAction("Copy Log Path"), new MessageAction("Dismiss", IsCloseAffordance: true)]).ConfigureAwait(true);
        if (choice == "Copy Log Path") await window.CopyToClipboardAsync(path).ConfigureAwait(true);
    });
}
