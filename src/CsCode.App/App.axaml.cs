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

    /// <summary>Background mode: the session process's host (<c>--host</c>, set by <c>Program</c>); null for an in-process run and for tests.</summary>
    internal static Background.BackgroundHost? Background { get; set; }

    /// <summary>Overall bound of the graceful part of the exit (language servers, debug sessions, state flush); see <see cref="AppShutdown"/>.</summary>
    public static TimeSpan ShutdownDeadline { get; set; } = AppShutdown.DefaultDeadline;

    private AppSession? _session;

    public IServiceProvider? Services => _session?.Services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (!OperatingSystem.IsMacOS()) return;
        // macOS names the application menu and its Hide and Quit items after this (the executable's name otherwise), and reads the menu's own
        // items as the app starts (Avalonia's "About Avalonia" otherwise): they run the commands of the run the main window connects.
        Name = "CsCode";
        ApplicationMenu = Menus.NativeMenuExport.BuildApplicationMenu();
        ApplicationMenu.NewWindow = () => _session?.NewWindow();
        NativeMenu.SetMenu(this, ApplicationMenu.Menu);
        // The Dock icon's menu: New Window, also after the last window closed (the app runs on: AppSession, audit ui-6).
        NativeDock.SetMenu(this, Menus.NativeMenuExport.BuildDockMenu(() => _session?.NewWindow()));
    }

    /// <summary>macOS: the application menu's own items (About CsCode, Settings...); null elsewhere (tests stand one in on every OS).</summary>
    internal static Menus.NativeMenuExport? ApplicationMenu { get; set; }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Crash.Report(e.Exception, fatal: false, source: "UI");
                e.Handled = true; // keep running: the user is told and the log is written
            };
            Start(new AvaloniaDesktopHost(desktop, this.TryGetFeature<IActivatableLifetime>()));
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Starts a run of the app on <paramref name="desktop"/> (see <see cref="AppSession"/>). Internal so tests can drive it with their own host.</summary>
    internal AppSession Start(IDesktopHost desktop)
    {
        var session = new AppSession(desktop, Crash, ConfigureServices, ShutdownDeadline, Background);
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
