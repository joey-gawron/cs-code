using CsCode.Core.Debugging;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Panels.Search.Engine;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Search.ViewModels;

/// <summary>
/// Everything the panels consume, bundled so view-models take one argument and tests can build it from fakes. Only the workspace
/// is required: every other Core service is optional and the panel degrades (no history persistence, no navigation...) without it.
/// </summary>
public sealed class SearchServices
{
    public required IWorkspaceService Workspace { get; init; }
    public required IUiDispatcher Ui { get; init; }
    public required SearchEngine Engine { get; init; }
    public required ReplaceService Replace { get; init; }
    public IConfigurationService? Configuration { get; init; }
    public IDocumentManager? Documents { get; init; }
    public INavigationService? Navigation { get; init; }
    public IWindowService? Windows { get; init; }
    public IStateStorage? State { get; init; }
    public ICommandService? Commands { get; init; }
    public IKeybindingService? Keybindings { get; init; }
    public IContextKeyService? ContextKeys { get; init; }
    public IViewActivator? Views { get; init; }
    public IActiveSelectionProvider? Selection { get; init; }
    public IFileSystem? FileSystem { get; init; }
    /// <summary>The colour themes (the Settings page's Color Theme dropdown); optional.</summary>
    public IColorThemeService? Themes { get; init; }
    /// <summary>The connected remote host's machine settings file (the Settings page's Remote tab); optional (null without remote editing).</summary>
    public IRemoteMachineSettings? RemoteSettings { get; init; }

    public static SearchServices From(IServiceProvider sp)
    {
        var workspace = sp.GetRequiredService<IWorkspaceService>();
        var documents = sp.GetService<IDocumentManager>();
        var config = sp.GetService<IConfigurationService>();
        return new SearchServices
        {
            Workspace = workspace,
            Ui = sp.GetService<IUiDispatcher>() ?? AvaloniaUiDispatcher.Instance,
            Engine = new SearchEngine(workspace, documents, config, textSearch: sp.GetServices<ITextSearchProvider>().ToArray()),
            Replace = new ReplaceService(workspace, documents, limits: null, configuration: config), // same size limit / regex timeout as the search
            Configuration = config,
            Documents = documents,
            Navigation = sp.GetService<INavigationService>(),
            Windows = sp.GetService<IWindowService>(),
            State = sp.GetService<IStateStorage>(),
            Commands = sp.GetService<ICommandService>(),
            Keybindings = sp.GetService<IKeybindingService>(),
            ContextKeys = sp.GetService<IContextKeyService>(),
            Views = sp.GetService<IViewActivator>(),
            Selection = sp.GetService<IActiveSelectionProvider>(),
            FileSystem = workspace.FileSystem,
            Themes = sp.GetService<IColorThemeService>(),
            RemoteSettings = sp.GetService<IRemoteMachineSettings>(),
        };
    }
}
