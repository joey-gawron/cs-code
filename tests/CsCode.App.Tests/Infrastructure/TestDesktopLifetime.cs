using Avalonia.Controls;
using CsCode.App.Startup;

namespace CsCode.App.Tests.Infrastructure;

/// <summary>
/// A desktop host for driving the app's real start/exit wiring (<see cref="App.Start"/> / <see cref="AppSession"/>) on the headless platform, which has
/// no desktop lifetime of its own. Records the shutdown instead of stopping the dispatcher (the test process keeps running).
/// </summary>
public sealed class TestDesktopLifetime(params string[] args) : IDesktopHost
{
    public IReadOnlyList<string> Args { get; } = args;
    public Window? MainWindow { get; set; }
    public bool ExitTakenOver { get; private set; }
    public int? ShutdownExitCode { get; private set; }
    public event EventHandler? Exited;
    public event EventHandler<DesktopActivation>? Activated;
    public event EventHandler<DesktopQuitRequest>? QuitRequested;

    public void TakeOverExit() => ExitTakenOver = true;

    /// <summary>As macOS hands documents to the app or the Dock icon is clicked (no paths).</summary>
    public void Activate(params string[] paths) => Activated?.Invoke(this, new DesktopActivation(paths));

    /// <summary>As ⌘Q (or, with <paramref name="osShutdown"/>, a log out) asks the app to quit: whether the quit was cancelled.</summary>
    public bool RequestQuit(bool osShutdown = false)
    {
        var request = new DesktopQuitRequest { IsOSShutdown = osShutdown };
        QuitRequested?.Invoke(this, request);
        return request.Cancel;
    }

    public void Shutdown(int exitCode)
    {
        ShutdownExitCode = exitCode;
        Exited?.Invoke(this, EventArgs.Empty);
    }
}
