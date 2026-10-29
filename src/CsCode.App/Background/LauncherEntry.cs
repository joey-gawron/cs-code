using CsCode.App.Startup;
using CsCode.Platform;
using CsCode.Sessions;
using CsCode.Sessions.Ipc;
using CsCode.Sessions.Launcher;

namespace CsCode.App.Background;

/// <summary>
/// Client mode of the <c>cscode</c> executable (no Avalonia, no services): maps the command line to a <see cref="LaunchRequest"/> and runs the
/// <see cref="SessionLauncher"/>. Paths come from Platform's <see cref="AppPaths"/> (pure, honours <c>CSCODE_HOME</c>), the IPC directory from
/// <see cref="RuntimeDirectory.Resolve"/> (derived from <c>CSCODE_HOME</c> too, so a test never reaches the user's running CsCode).
/// </summary>
internal static class LauncherEntry
{
    /// <summary>Returned when background mode cannot work here: the caller runs the window in its own process.</summary>
    public const int RunInProcess = -1;

    public static int Run(CommandLineOptions options)
    {
        var paths = new AppPaths();
        var runtime = RuntimeDirectory.Resolve();
        // Processes the launcher starts inherit the test platform switch; nothing else of the launcher's command line is passed on.
        var spawner = ProcessHostSpawner.ForCurrentProcess(options.HeadlessTest ? ["--headless-test"] : null);
        var launcher = new SessionLauncher(runtime, new SessionStore(paths.UserDataDir), LaunchSettings.Read(paths.ConfigDir), spawner, Console.Out, Console.Error);
        foreach (var unknown in options.UnknownSwitches) Console.Error.WriteLine($"cscode: ignoring unknown option {unknown}");
        var outcome = launcher.Run(ToRequest(options));
        return outcome.ExitCode == 2 ? RunInProcess : outcome.ExitCode;
    }

    internal static LaunchRequest ToRequest(CommandLineOptions o)
    {
        if (o.ListSessions) return new LaunchRequest(LaunchKind.ListSessions);
        if (o.Quit) return new LaunchRequest(LaunchKind.Quit) { Session = o.Session, All = o.All };
        if (o.Background) return new LaunchRequest(LaunchKind.Background);
        return new LaunchRequest(LaunchKind.Open)
        {
            Session = o.Session,
            NewWindow = o.NewWindow,
            Wait = o.Wait,
            Targets = o.Targets.Select(t => new LaunchTarget(t.Path, Directory.Exists(t.Path), t.Line, t.Column)).ToArray(),
        };
    }
}
