using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CsCode.SpellCheck;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the spell checker (<see cref="SpellCheckService"/>) and its workbench glue (<see cref="SpellCheckHost"/>: quick fixes, commands,
    /// status bar). Nothing runs until the shell calls <see cref="SpellCheckHost.Start"/>; the dictionaries load on the first check. Needs
    /// <see cref="IDocumentManager"/>, <see cref="IDiagnosticService"/> and <see cref="IConfigurationService"/>; uses the editor's
    /// <see cref="ISyntaxClassificationService"/> (without it only prose languages are checked) and every other service when registered.
    /// </summary>
    public static IServiceCollection AddCsCodeSpellCheck(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new SpellCheckService(
            sp.GetRequiredService<IDocumentManager>(),
            sp.GetRequiredService<IDiagnosticService>(),
            sp.GetRequiredService<IConfigurationService>(),
            sp.GetService<IWorkspaceService>(),
            sp.GetService<IAppPaths>(),
            sp.GetService<ISyntaxClassificationService>(),
            sp.GetService<IFileWatcher>(),
            sp.GetService<IMemoryMonitor>(),
            sp.GetService<ILoggerFactory>()));
        services.TryAddSingleton(sp => new SpellCheckHost(
            sp.GetRequiredService<SpellCheckService>(),
            sp.GetRequiredService<IConfigurationService>(),
            sp.GetService<ICommandService>(),
            sp.GetService<ILanguageFeatureRegistry>(),
            sp.GetService<IStatusBarService>(),
            sp.GetService<IWindowService>(),
            sp.GetService<IWorkspaceService>(),
            sp.GetService<ILanguageService>(),
            // the navigation service depends on the document well, which is built after this: resolved when first used
            () => sp.GetService<INavigationService>(),
            sp.GetService<ILoggerFactory>()));
        return services;
    }
}
