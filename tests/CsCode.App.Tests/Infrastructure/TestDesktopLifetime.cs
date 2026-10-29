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

    public void TakeOverExit() => ExitTakenOver = true;

    public void Shutdown(int exitCode)
    {
        ShutdownExitCode = exitCode;
        Exited?.Invoke(this, EventArgs.Empty);
    }
}
