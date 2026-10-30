using CsCode.Core.Diagnostics;
using CsCode.Core.Platform;
using CsCode.Core.Terminal;
using CsCode.Terminal.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.Terminal;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TerminalService"/> as <see cref="ITerminalService"/> (singleton). Its optional dependencies (configuration, workspace,
    /// <see cref="IViewActivator"/>, memory monitor) are resolved lazily on first use, so composing it never builds the shell early.
    /// The Terminal tool window (<c>CsCode.Panels.Terminal</c>, <c>AddCsCodeTerminal</c>) calls this.
    /// </summary>
    public static IServiceCollection AddCsCodeTerminalService(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new TerminalService(
            sp.GetService<IConfigurationService>(),
            sp.GetService<IWorkspaceService>(),
            new LazyViewActivator(sp),
            sp.GetService<IMemoryMonitor>()));
        services.TryAddSingleton<ITerminalService>(sp => sp.GetRequiredService<TerminalService>());
        return services;
    }

    /// <summary>The shell's view activator depends on the tool windows (which include the terminal's): resolved when first needed.</summary>
    private sealed class LazyViewActivator(IServiceProvider services) : IViewActivator
    {
        public ValueTask<bool> ShowViewAsync(string viewId, CancellationToken ct = default) =>
            services.GetService<IViewActivator>() is { } views ? views.ShowViewAsync(viewId, ct) : ValueTask.FromResult(false);
    }
}
