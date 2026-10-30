using Avalonia;
using Avalonia.Headless;
using CsCode.App.Background;
using CsCode.App.Startup;
using CsCode.Ui.Theming;

namespace CsCode.App;

/// <summary>
/// One executable, three roles (docs/SESSIONS.md): the <b>launcher</b> (default: talks to the running CsCode processes over IPC and exits, before
/// Avalonia is touched, so it stays fast), a <b>CsCode process</b> (<c>--host</c>: a session's window, or the warm spare) and the classic
/// <b>in-process</b> run (<c>--foreground</c>, or when background mode is unavailable).
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // ssh runs `cscode --askpass "<prompt>"` (SSH_ASKPASS) during a remote connection: answer it through the running GUI and exit.
        if (args is ["--askpass", ..]) return CsCode.Remote.Askpass.AskpassClient.Run(string.Join(' ', args.Skip(1)), Console.Out, Console.Error);
        AppStartup.NormalizeEnvironment(); // first: every path below derives from CSCODE_HOME, and the processes started inherit it
        AppStartup.ConfigureRuntime();
        var options = CommandLineOptions.Parse(args);
        if (options.ShowHelp) { Console.WriteLine(CommandLineOptions.HelpText); return 0; }
        if (options.ShowVersion) { Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "dev"); return 0; }

        if (options.IsLauncher)
        {
            var code = LauncherEntry.Run(options);
            if (code != LauncherEntry.RunInProcess) return code;
            // Background mode is unavailable (no private runtime directory): run the window in this process instead.
        }
        return RunApp(args, options);
    }

    private static int RunApp(string[] args, CommandLineOptions options)
    {
        // macOS, started from Finder/the Dock (lsp-1): the login shell's PATH and the rest, in the background; children wait for it
        if (!options.HeadlessTest) CsCode.Core.Platform.LoginShellEnvironment.Start();
        var crash = new CrashHandler(CrashHandler.DefaultLogDirectory());
        crash.Install();
        App.Crash = crash;
        BackgroundHost? host = null;
        try
        {
            if (options.Host)
            {
                host = BackgroundHost.Start(options, crash, out var refused);
                if (refused is { } exit) return exit;
            }
            App.Background = host;
            return BuildAvaloniaApp(options.HeadlessTest).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            var log = crash.Report(ex, fatal: true, source: "Main");
            Console.Error.WriteLine($"CsCode crashed: {CrashHandler.Summarize(ex)}\nLog: {log}");
            return 1;
        }
        finally
        {
            host?.Dispose(); // releases the session lock and removes the IPC endpoint
        }
    }

    // Also used by the headless test harness and the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().WithCodeFonts().LogToTrace();

    /// <summary><paramref name="headless"/>: the Avalonia headless platform with Skia rendering (<c>--headless-test</c>: integration tests of real processes, no display).</summary>
    public static AppBuilder BuildAvaloniaApp(bool headless) => headless
        ? AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().WithCodeFonts().LogToTrace()
        : BuildAvaloniaApp();
}
