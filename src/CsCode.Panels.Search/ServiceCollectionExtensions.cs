using CsCode.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.Panels.Search;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Find in Files tool window and the Settings / Keyboard Shortcuts document views as <see cref="IToolWindow"/>s, and the
    /// <see cref="SearchPanelsHost"/> that registers their commands (<c>workbench.action.findInFiles</c>, <c>replaceInFiles</c>,
    /// <c>search.action.*</c>, <c>workbench.action.openSettings</c>, <c>openSettingsJson</c>, <c>openGlobalKeybindings</c>) and the
    /// <c>search.*</c> settings. The host is resolved by the tool windows, so the shell enumerating <c>IEnumerable&lt;IToolWindow&gt;</c> at
    /// startup is what registers the commands. Required services: <c>IWorkspaceService</c>; everything else (documents, navigation, window,
    /// configuration, state, commands, keybindings, <c>IViewActivator</c>) is optional.
    /// </summary>
    public static IServiceCollection AddCsCodeSearchPanels(this IServiceCollection services)
    {
        services.TryAddSingleton<SearchPanelsHost>(sp => new SearchPanelsHost(sp));
        services.AddSingleton<IToolWindow>(sp => new FindInFilesToolWindow(sp.GetRequiredService<SearchPanelsHost>()));
        services.AddSingleton<IToolWindow>(sp => new SettingsToolWindow(sp.GetRequiredService<SearchPanelsHost>()));
        services.AddSingleton<IToolWindow>(sp => new KeyboardShortcutsToolWindow(sp.GetRequiredService<SearchPanelsHost>()));
        return services;
    }
}
