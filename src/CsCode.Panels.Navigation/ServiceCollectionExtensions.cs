using Avalonia.Controls;
using CsCode.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.Panels.Navigation;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Hierarchy tool window (call and type hierarchy) and the <see cref="HierarchyHost"/> behind it (commands, right-click
    /// items). The tool window resolves the host, so the shell enumerating <c>IEnumerable&lt;IToolWindow&gt;</c> at startup is what registers
    /// the commands. Every service it uses is optional (language features, navigation, commands, contributions, window, view activator).
    /// </summary>
    public static IServiceCollection AddCsCodeNavigationPanels(this IServiceCollection services)
    {
        services.TryAddSingleton<HierarchyHost>(sp => new HierarchyHost(sp));
        services.AddSingleton<IToolWindow>(sp => new HierarchyToolWindow(sp.GetRequiredService<HierarchyHost>()));
        return services;
    }
}

/// <summary>
/// "Hierarchy" (<c>hierarchy</c>): the call hierarchy (Visual Studio's Call Hierarchy window) and the type hierarchy, docked at the bottom
/// like Visual Studio's. The shell creates the view when it is shown and disposes it when hidden; the tree stays in the host's model.
/// </summary>
public sealed class HierarchyToolWindow(HierarchyHost host) : IToolWindow
{
    public string Id => HierarchyHost.ViewId;
    public string Title => "Hierarchy";
    public string? IconKey => "list-tree";
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Bottom;
    public string? ToggleCommandId => HierarchyHost.FocusView;
    public string? AutoShowWhen => null;

    public Control CreateView(IServiceProvider services) => new HierarchyView(host, services);
}
