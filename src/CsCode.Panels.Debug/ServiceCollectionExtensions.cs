using CsCode.Panels.Debug.ToolWindows;
using CsCode.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.Panels.Debug;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the debug tool windows (<c>debug.callstack</c>, <c>debug.locals</c>, <c>debug.autos</c>, <c>debug.watch</c>,
    /// <c>debug.breakpoints</c>, <c>debug.threads</c>, <c>debug.console</c>, <c>debug.exception</c>, <c>debug.modules</c>) as <see cref="IToolWindow"/> singletons, plus the shared
    /// <see cref="DebugPanelsRuntime"/> (which registers the <c>debug.addToWatch</c> command). The panels consume
    /// <c>IDebugService</c>, <c>IDebugFrameFocus</c>, <c>IDebugOutputHistory</c>, <c>INavigationService</c>, <c>IWindowService</c>,
    /// <c>ICommandService</c>, <c>IStateStorage</c>, <c>IDocumentManager</c> and <c>IUiDispatcher</c> from DI when a view is created; only
    /// <c>IDebugService</c> is required. The debug commands (<c>workbench.action.debug.*</c>) belong to the DAP module and the toggle
    /// commands (<c>workbench.debug.action.toggle*</c>) to the shell, which binds each <see cref="IToolWindow.ToggleCommandId"/>.
    /// </summary>
    public static IServiceCollection AddCsCodeDebugPanels(this IServiceCollection services)
    {
        services.TryAddSingleton<DebugPanelsRuntime>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolWindow, CallStackToolWindow>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolWindow, LocalsToolWindow>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolWindow, AutosToolWindow>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolWindow, WatchToolWindow>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolWindow, BreakpointsToolWindow>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolWindow, ThreadsToolWindow>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolWindow, DebugConsoleToolWindow>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolWindow, ExceptionToolWindow>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolWindow, ModulesToolWindow>());
        return services;
    }
}
