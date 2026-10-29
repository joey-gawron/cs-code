using Avalonia;
using CsCode.App.Startup;

namespace CsCode.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = CommandLineOptions.Parse(args);
        if (options.ShowHelp) { Console.WriteLine(CommandLineOptions.HelpText); return 0; }
        if (options.ShowVersion) { Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "dev"); return 0; }

        var crash = new CrashHandler(CrashHandler.DefaultLogDirectory());
        crash.Install();
        App.Crash = crash;
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            var log = crash.Report(ex, fatal: true, source: "Main");
            Console.Error.WriteLine($"CsCode crashed: {CrashHandler.Summarize(ex)}\nLog: {log}");
            return 1;
        }
    }

    // Also used by the headless test harness and the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
