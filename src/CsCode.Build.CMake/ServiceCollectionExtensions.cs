using CsCode.Core.Build;
using CsCode.Core.Diagnostics;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CsCode.Build.CMake;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CMakeService"/> (also as <see cref="ICMakeService"/>) and the <see cref="CMakeHost"/> the shell starts once its
    /// window exists (<see cref="CMakeHost.Start"/>: commands, status bar, configure on open, clangd's compile-commands directory). Every service
    /// they use is optional.
    /// </summary>
    public static IServiceCollection AddCsCodeCMake(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new CMakeService(sp.GetService<IWorkspaceService>(), sp.GetService<IConfigurationService>(), sp.GetService<IStateStorage>(),
            sp.GetService<IDiagnosticService>(), sp.GetService<IOutputService>(), sp.GetService<IDocumentManager>(), sp.GetService<IMemoryMonitor>(),
            sp.GetService<ILoggerFactory>()));
        services.TryAddSingleton<ICMakeService>(sp => sp.GetRequiredService<CMakeService>());
        services.TryAddSingleton(sp => new CMakeHost(sp, sp.GetRequiredService<CMakeService>()));
        return services;
    }
}
