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
        services.TryAddSingleton(sp => CreateService(sp));
        services.TryAddSingleton<ITerminalService>(sp => sp.GetRequiredService<TerminalService>());
        return services;
    }

    /// <summary>The service as the container builds it; a host (tests, the remote server later) may pass its own PTY factory.</summary>
    public static TerminalService CreateService(IServiceProvider sp, Func<Pty.IPtyHost>? hostFactory = null) => new(
        sp.GetService<IConfigurationService>(),
        sp.GetService<IWorkspaceService>(),
        new LazyViewActivator(sp),
        sp.GetService<IMemoryMonitor>(),
        hostFactory)
    {
        // A remote workspace's terminals run on its server (the Remote module registers the provider); resolved on the first terminal.
        Backends = () => sp.GetService<ITerminalBackendProvider>(),
    };

    /// <summary>The shell's view activator depends on the tool windows (which include the terminal's): resolved when first needed.</summary>
    private sealed class LazyViewActivator(IServiceProvider services) : IViewActivator
    {
        public ValueTask<bool> ShowViewAsync(string viewId, CancellationToken ct = default) =>
            services.GetService<IViewActivator>() is { } views ? views.ShowViewAsync(viewId, ct) : ValueTask.FromResult(false);
    }
}
