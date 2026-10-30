using Avalonia.Controls;
using CsCode.App.Docking;
using CsCode.App.Notifications;
using CsCode.App.QuickInput;
using CsCode.App.Services;
using CsCode.App.Startup;
using CsCode.App.ToolWindows;
using CsCode.App.ToolWindows.FindResults;
using CsCode.Core;
using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Dap;
using CsCode.Editor;
using CsCode.Lsp;
using CsCode.Panels.Debug;
using CsCode.Panels.Search;
using CsCode.Platform;
using CsCode.Profiling;
using CsCode.Remote;
using CsCode.Ui;
using CsCode.Vim;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CsCode.App;

/// <summary>Holds the main window so services that need a TopLevel (dialogs, clipboard) can reach it without referencing the window type.</summary>
public sealed class WindowHolder
{
    public TopLevel? Window { get; set; }
}

/// <summary>
/// The composition root. <see cref="Build"/> registers the real modules (<c>AddCsCodeCore/Platform/Lsp/Dap/Editor/Vim/Profiling</c>), the shell
/// services and the Debug and Search/Settings panels, then lets the caller replace any registration (dev host, tests) through <c>configure</c>. There are deliberately no silent
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
        services.AddCsCodeVim(); // engine + session; the shell's adapter (Vim/VimIntegration) connects it to the editor views
        services.AddCsCodeProfiling();
        services.AddCsCodeShell(args);
        services.AddCsCodeDebugPanels();
        services.AddCsCodeSearchPanels();
        services.AddCsCodeRemote(); // after AddCsCodeLsp: with cscode.languageHost = process it redirects the language-server manager to a child process
        configure?.Invoke(services); // dev/test host: replace anything above
        AddChildProcessTracking(services); // last: wraps whichever memory monitor ended up registered
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>
    /// Modules report their child processes through <see cref="IMemoryMonitor.TrackChildProcess"/>; the shell wraps the registered monitor (or stands in
    /// for it while CsCode.Profiling registers none) so the exit path knows which processes to kill if their owner does not stop them in time.
    /// </summary>
    private static void AddChildProcessTracking(IServiceCollection services)
    {
        services.TryAddSingleton<ChildProcessTracker>();
        var registered = services.LastOrDefault(d => d.ServiceType == typeof(IMemoryMonitor) && !d.IsKeyedService);
        if (registered is not null) services.Remove(registered);
        services.AddSingleton<IMemoryMonitor>(sp =>
        {
            var (inner, owned) = registered switch
            {
                null => (null, false),
                { ImplementationInstance: IMemoryMonitor instance } => (instance, false), // the container never disposes instances either
                { ImplementationFactory: { } factory } => ((IMemoryMonitor)factory(sp), true),
                { ImplementationType: { } type } => ((IMemoryMonitor)ActivatorUtilities.CreateInstance(sp, type), true),
                _ => ((IMemoryMonitor?)null, false),
            };
            return new ShellMemoryMonitor(inner, owned, sp.GetRequiredService<ChildProcessTracker>());
        });
    }

    /// <summary>Registers the shell's own services (all singletons; the shell has one window).</summary>
    public static IServiceCollection AddCsCodeShell(this IServiceCollection services, IReadOnlyList<string> args)
    {
        services.AddSingleton(CommandLineOptions.Parse(args));
        services.TryAddSingleton(_ => App.Crash); // the process-wide handler (installed before the container exists): the window reports late failures through it
        services.AddSingleton<IUiDispatcher>(_ => AvaloniaUiDispatcher.Instance);
        services.AddSingleton<WindowHolder>();
        services.AddSingleton<IShellHost>(sp => new AvaloniaShellHost(() => sp.GetRequiredService<WindowHolder>().Window));
        // The system clipboard (+ PRIMARY on X11) and the in-memory clipboard ring, for the editor, the panels and the shell.
        services.TryAddSingleton(sp => new ClipboardService(sp.GetRequiredService<IUiDispatcher>(), AvaloniaPlatformClipboard.Of(() => sp.GetRequiredService<WindowHolder>().Window)));
        services.TryAddSingleton<IClipboardService>(sp => sp.GetRequiredService<ClipboardService>());

        services.AddSingleton<NotificationCenter>();
        services.AddSingleton<QuickInputViewModel>();
        services.AddSingleton<DialogHostViewModel>();
        services.AddSingleton<IModalDialogs>(sp => sp.GetRequiredService<DialogHostViewModel>());

        services.AddSingleton<StatusBarService>();
        services.AddSingleton<IStatusBarService>(sp => sp.GetRequiredService<StatusBarService>());
        services.AddSingleton<OutputService>();
        services.AddSingleton<IOutputService>(sp => sp.GetRequiredService<OutputService>());
        services.AddSingleton<IWindowService, WindowService>();

        // Language servers take part in saves and file operations; resolved lazily because the language client itself depends on navigation.
        services.AddSingleton(sp => new FileParticipants(() => sp.GetServices<IWillSaveParticipant>(), () => sp.GetServices<IFileOperationParticipant>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("CsCode.Shell.FileParticipants")));
        services.AddSingleton<ShellDockFactory>();
        services.AddSingleton(sp => new DocumentWell(sp.GetRequiredService<ShellDockFactory>(), sp.GetRequiredService<IEditorViewFactory>(), sp.GetRequiredService<IModalDialogs>(),
            sp.GetService<IWindowService>(), sp.GetService<IConfigurationService>(), sp.GetRequiredService<IUiDispatcher>(), sp.GetService<FileParticipants>()));
        services.AddSingleton(sp => new NavigationService(sp.GetRequiredService<DocumentWell>(), sp.GetRequiredService<IUiDispatcher>(), sp.GetRequiredService<IModalDialogs>(),
            sp.GetService<IWorkspaceService>()?.FileSystem, sp.GetService<Core.Text.IDocumentManager>(), sp.GetService<IWindowService>(), sp.GetService<IConfigurationService>(),
            sp.GetService<FileParticipants>()));
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());

        services.AddSingleton<IToolWindow, ExplorerToolWindow>();
        services.AddSingleton<IToolWindow, OutputToolWindow>();
        services.AddSingleton<IToolWindow, ErrorListToolWindow>();
        services.AddSingleton<IToolWindow, FindResultsToolWindow>();
        services.AddSingleton(sp => new ToolWindowManager(sp.GetRequiredService<ShellDockFactory>(), sp.GetServices<IToolWindow>(), sp));
        // Panels bring their views to the front through this (Find in Files, Settings, Keyboard Shortcuts); document-location windows open as tabs.
        services.AddSingleton<ShellViewActivator>();
        services.AddSingleton<IViewActivator>(sp => sp.GetRequiredService<ShellViewActivator>());
        // ${selectedText} (Dap) and the Find in Files seed; ShellController attaches it to the document well.
        services.AddSingleton<ActiveSelectionProvider>();
        services.AddSingleton<Core.Debugging.IActiveSelectionProvider>(sp => sp.GetRequiredService<ActiveSelectionProvider>());
        services.AddSingleton<FindResultsViewModel>(sp => new FindResultsViewModel(sp.GetService<INavigationService>(), sp.GetService<IWorkspaceService>()?.FileSystem, sp.GetService<Core.Text.IDocumentManager>()));

        services.AddSingleton<ShellController>();
        return services;
    }
}
