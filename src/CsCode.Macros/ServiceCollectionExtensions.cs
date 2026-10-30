using CsCode.Core.Contributions;
using CsCode.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CsCode.Macros;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the macro module: <see cref="MacroStore"/> (macros.json under <see cref="IAppPaths.UserDataDir"/>) and <see cref="MacroService"/>.
    /// Requires <see cref="ICommandService"/>, <see cref="IAppPaths"/>, <see cref="IFileSystem"/> and an <see cref="IMacroScheduler"/> (the shell
    /// registers one over its UI dispatcher); uses the configuration, window, status bar, context keys, state, navigation, file watcher and
    /// contribution services when present. Resolving <see cref="MacroService"/> registers the <c>macro.*</c> commands; call
    /// <see cref="MacroService.StartAsync"/> to load the saved and the last macro.
    /// </summary>
    public static IServiceCollection AddCsCodeMacros(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new MacroStore(sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<IFileSystem>(), sp.GetService<IFileWatcher>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<MacroStore>(), sp.GetService<TimeProvider>()));
        services.TryAddSingleton(sp => new MacroService(sp.GetRequiredService<ICommandService>(), sp.GetRequiredService<IMacroScheduler>(), sp.GetRequiredService<MacroStore>(),
            sp.GetService<IConfigurationService>(), sp.GetService<IWindowService>(), sp.GetService<IStatusBarService>(), sp.GetService<IContextKeyService>(),
            sp.GetService<IStateStorage>(), sp.GetService<INavigationService>(), sp.GetService<IContributionRegistry>(), sp.GetService<ILogger<MacroService>>(),
            sp.GetService<TimeProvider>()));
        return services;
    }
}
