using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CsCode.App.Notifications;
using CsCode.App.Startup;
using CsCode.Core.Platform;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App;

public sealed class App : Application
{
    /// <summary>Dev host / test hook: customises the service collection after the real modules registered (replace any service here).</summary>
    public static Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>The crash handler for this process (set by <c>Program</c>; created lazily for hosts that do not use <c>Program</c>).</summary>
    public static CrashHandler Crash { get; set; } = new(CrashHandler.DefaultLogDirectory());

    public IServiceProvider? Services { get; private set; }

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
            try
            {
                Services = AppServices.Build(desktop.Args ?? [], ConfigureServices);
                Crash.Reported += (summary, path, fatal) => NotifyCrash(summary, path);
                desktop.MainWindow = new MainWindow(Services);
            }
            catch (Exception ex)
            {
                // Startup failure (e.g. a module that the shell needs is not registered): never a silent exit.
                var log = Crash.Report(ex, fatal: true, source: "Startup");
                desktop.MainWindow = new CrashWindow(CrashHandler.Summarize(ex), log);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void NotifyCrash(string summary, string path) => Dispatcher.UIThread.Post(async () =>
    {
        if (Services?.GetService<IWindowService>() is not { } window) return;
        var choice = await window.ShowMessageAsync(MessageSeverity.Error, $"An unexpected error occurred: {summary}\nDetails were written to {path}",
            [new MessageAction("Copy Log Path"), new MessageAction("Dismiss", IsCloseAffordance: true)]);
        if (choice == "Copy Log Path") await window.CopyToClipboardAsync(path);
    });
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
