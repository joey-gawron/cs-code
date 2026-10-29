using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CsCode.App.Startup;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App;

public sealed class App : Application
{
    /// <summary>Dev host / test hook: customises the service collection after the real modules registered (replace any service here).</summary>
    public static Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>The crash handler for this process (set by <c>Program</c>; created lazily for hosts that do not use <c>Program</c>).</summary>
    public static CrashHandler Crash { get; set; } = new(CrashHandler.DefaultLogDirectory());

    /// <summary>Overall bound of the graceful part of the exit (language servers, debug sessions, state flush); see <see cref="AppShutdown"/>.</summary>
    public static TimeSpan ShutdownDeadline { get; set; } = AppShutdown.DefaultDeadline;

    private AppSession? _session;

    public IServiceProvider? Services => _session?.Services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Crash.Report(e.Exception, fatal: false, source: "UI");
                e.Handled = true; // keep running: the user is told and the log is written
            };
            Start(new AvaloniaDesktopHost(desktop));
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Starts a run of the app on <paramref name="desktop"/> (see <see cref="AppSession"/>). Internal so tests can drive it with their own host.</summary>
    internal AppSession Start(IDesktopHost desktop)
    {
        var session = new AppSession(desktop, Crash, ConfigureServices, ShutdownDeadline);
        _session = session;
        session.Start();
        return session;
    }
}

/// <summary>Minimal window shown when startup itself failed, so the user always learns what happened and where the log is.</summary>
public sealed class CrashWindow : Window
{
    public CrashWindow(string summary, string? logPath)
    {
        Title = "CsCode could not start";
        Width = 640;
        Height = 260;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(18, 4) };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 12,
            Children =
            {
                new TextBlock { Text = "CsCode could not start", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = summary, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = logPath is null ? "The crash log could not be written." : "A crash report was written to:\n" + logPath, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.8 },
                close,
            },
        };
    }
}
