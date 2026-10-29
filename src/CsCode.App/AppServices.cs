using Avalonia.Controls;
using CsCode.App.Docking;
using CsCode.App.Notifications;
using CsCode.App.QuickInput;
using CsCode.App.Services;
using CsCode.App.Startup;
using CsCode.App.ToolWindows;
using CsCode.App.ToolWindows.FindResults;
using CsCode.Core;
using CsCode.Core.Platform;
using CsCode.Dap;
using CsCode.Editor;
using CsCode.Lsp;
using CsCode.Platform;
using CsCode.Profiling;
using CsCode.Ui;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App;

/// <summary>Holds the main window so services that need a TopLevel (dialogs, clipboard) can reach it without referencing the window type.</summary>
public sealed class WindowHolder
{
    public TopLevel? Window { get; set; }
}

/// <summary>
/// The composition root. <see cref="Build"/> registers the real modules (<c>AddCsCodeCore/Platform/Lsp/Dap/Editor/Profiling</c>) and the shell
/// services, then lets the caller replace any registration (dev host, tests) through <c>configure</c>. There are deliberately no silent
/// fallbacks here: a missing module registration surfaces as a resolution failure when the shell needs it.
/// </summary>
public static class AppServices
{
    public static ServiceProvider Build(IReadOnlyList<string> args, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCsCodeCore();
        services.AddCsCodePlatform();
        services.AddCsCodeLsp();
        services.AddCsCodeDap();
        services.AddCsCodeEditor();
        services.AddCsCodeProfiling();
        services.AddCsCodeShell(args);
        configure?.Invoke(services); // dev/test host: replace anything above
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Registers the shell's own services (all singletons; the shell has one window).</summary>
    public static IServiceCollection AddCsCodeShell(this IServiceCollection services, IReadOnlyList<string> args)
    {
        services.AddSingleton(CommandLineOptions.Parse(args));
        services.AddSingleton<IUiDispatcher>(_ => AvaloniaUiDispatcher.Instance);
        services.AddSingleton<WindowHolder>();
        services.AddSingleton<IShellHost>(sp => new AvaloniaShellHost(() => sp.GetRequiredService<WindowHolder>().Window));

        services.AddSingleton<NotificationCenter>();
        services.AddSingleton<QuickInputViewModel>();
        services.AddSingleton<DialogHostViewModel>();
        services.AddSingleton<IModalDialogs>(sp => sp.GetRequiredService<DialogHostViewModel>());

        services.AddSingleton<StatusBarService>();
        services.AddSingleton<IStatusBarService>(sp => sp.GetRequiredService<StatusBarService>());
        services.AddSingleton<OutputService>();
        services.AddSingleton<IOutputService>(sp => sp.GetRequiredService<OutputService>());
        services.AddSingleton<IWindowService, WindowService>();

        services.AddSingleton<ShellDockFactory>();
        services.AddSingleton(sp => new DocumentWell(sp.GetRequiredService<ShellDockFactory>(), sp.GetRequiredService<IEditorViewFactory>(), sp.GetRequiredService<IModalDialogs>(),
            sp.GetService<IWindowService>(), sp.GetService<IConfigurationService>(), sp.GetRequiredService<IUiDispatcher>()));
        services.AddSingleton(sp => new NavigationService(sp.GetRequiredService<DocumentWell>(), sp.GetRequiredService<IUiDispatcher>(), sp.GetRequiredService<IModalDialogs>(),
            sp.GetService<IWorkspaceService>()?.FileSystem, sp.GetService<Core.Text.IDocumentManager>(), sp.GetService<IWindowService>(), sp.GetService<IConfigurationService>()));
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());

        services.AddSingleton<IToolWindow, ExplorerToolWindow>();
        services.AddSingleton<IToolWindow, OutputToolWindow>();
        services.AddSingleton<IToolWindow, ErrorListToolWindow>();
        services.AddSingleton<IToolWindow, FindResultsToolWindow>();
        services.AddSingleton(sp => new ToolWindowManager(sp.GetRequiredService<ShellDockFactory>(), sp.GetServices<IToolWindow>(), sp));
        services.AddSingleton<FindResultsViewModel>(sp => new FindResultsViewModel(sp.GetService<INavigationService>(), sp.GetService<IWorkspaceService>()?.FileSystem, sp.GetService<Core.Text.IDocumentManager>()));

        services.AddSingleton<ShellController>();
        return services;
    }
}
