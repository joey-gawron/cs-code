using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.Vim;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Vim module: the shared <see cref="VimSession"/> and <see cref="IVimService"/> (engine factory, on/off switch).
    /// Resolve <see cref="IVimService"/> once at startup: that registers the <c>vim.*</c> settings and commands, loads the vimrc and
    /// restores the saved registers. Requires <c>IConfigurationService</c> and <c>ICommandService</c> in the container;
    /// <c>IStateStorage</c>, <c>IAppPaths</c> and <c>IOutputService</c> are used when present.
    /// </summary>
    public static IServiceCollection AddCsCodeVim(this IServiceCollection services)
    {
        services.TryAddSingleton<VimSession>();
        services.TryAddSingleton<IVimService, VimService>();
        return services;
    }
}
