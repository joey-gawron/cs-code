using System.Runtime;
using CsCode.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CsCode.App.Startup;

/// <summary>Process start-up steps that must happen before (or beside) the first window.</summary>
public static class AppStartup
{
    /// <summary>
    /// Runtime settings for the whole process. <see cref="GCLatencyMode.SustainedLowLatency"/>: no blocking gen-2 collections while the user types
    /// (the editor's latency figures were measured in this mode; docs/EDITOR.md).
    /// </summary>
    public static void ConfigureRuntime() => GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

    /// <summary>
    /// Builds the container on a pool thread and resolves there the services whose constructors read files synchronously (settings, state and
    /// keybindings read their small files once, when constructed; docs/PLATFORM.md), so the UI thread never waits for the disk. The caller creates
    /// the window on the UI thread once this completes. A resolution failure faults the task (the app then shows its crash window).
    /// </summary>
    public static Task<ServiceProvider> BuildServicesAsync(IReadOnlyList<string> args, Action<IServiceCollection>? configure = null) => Task.Run(() =>
    {
        var services = AppServices.Build(args, configure);
        try
        {
            _ = services.GetService<ILoggerFactory>(); // the file logger: creates its directory and writer thread
            _ = services.GetService<IConfigurationService>();
            _ = services.GetService<IStateStorage>();
            _ = services.GetService<IKeybindingService>();
            _ = services.GetService<IWorkspaceService>();
            // The colour theme service parses the 21 embedded theme files when constructed, and the shell's completion of the current theme
            // (VS 2022 chrome mapping, contrast check) runs here too, so neither happens on the UI thread before the first frame.
            if (services.GetService<CsCode.Core.Theming.IColorThemeService>() is { } themes) ShellController.PrepareThemes(themes);
            return services;
        }
        catch
        {
            services.Dispose(); // nothing UI-bound was created yet
            throw;
        }
    });
}
