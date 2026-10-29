using Avalonia.Controls;
using Avalonia.Threading;
using CsCode.App.Docking;
using CsCode.App.Notifications;
using CsCode.App.ToolWindows.ErrorList;
using CsCode.App.ToolWindows.Explorer;
using CsCode.App.ToolWindows.FindResults;
using CsCode.App.ToolWindows.Output;
using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Ui;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.ToolWindows;

/// <summary>The shell's own tool windows. Each creates a fresh view-model per view; the view disposes it when the tool window is hidden.</summary>
public sealed class ExplorerToolWindow : IToolWindow
{
    public string Id => "explorer";
    public string Title => "Explorer";
    public string? IconKey => "files";
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Right; // VS docks Solution Explorer on the right
    public string? ToggleCommandId => "workbench.view.explorer";
    public string? AutoShowWhen => null;

    public Control CreateView(IServiceProvider services)
    {
        var vm = new ExplorerViewModel(
            services.GetRequiredService<IWorkspaceService>(), services.GetRequiredService<IUiDispatcher>(), services.GetService<INavigationService>(),
            services.GetService<IConfigurationService>(), services.GetService<IStateStorage>(), services.GetService<IModalDialogs>(), services.GetService<DocumentWell>(),
            services.GetService<Services.FileParticipants>());
        var view = new ExplorerView(vm, services.GetRequiredService<IWindowService>(), services.GetService<ICommandService>(), services.GetService<IContextKeyService>(),
            services.GetService<IContributionRegistry>());
        // Load after the first frame so showing the window never waits for the directory read.
        Dispatcher.UIThread.Post(() => { _ = vm.RefreshRootsAsync(); }, DispatcherPriority.Background);
        return view;
    }
}

public sealed class OutputToolWindow : IToolWindow
{
    public string Id => "output";
    public string Title => "Output";
    public string? IconKey => "output";
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Bottom;
    public string? ToggleCommandId => "workbench.action.output.toggleOutput";
    public string? AutoShowWhen => null;
    public Control CreateView(IServiceProvider services) => new OutputView(new OutputViewModel(services.GetRequiredService<IOutputService>()));
}

public sealed class ErrorListToolWindow : IToolWindow
{
    public string Id => "errorlist";
    public string Title => "Error List";
    public string? IconKey => "error";
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Bottom;
    public string? ToggleCommandId => "workbench.actions.view.problems";
    public string? AutoShowWhen => null;
    public Control CreateView(IServiceProvider services) =>
        new ErrorListView(new ErrorListViewModel(services.GetRequiredService<IDiagnosticService>(), services.GetRequiredService<IUiDispatcher>(), services.GetService<INavigationService>()));
}

public sealed class FindResultsToolWindow : IToolWindow
{
    public string Id => "findresults";
    public string Title => "Find Results";
    public string? IconKey => "search";
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Bottom;
    public string? ToggleCommandId => "workbench.action.findResults.toggle";
    public string? AutoShowWhen => null;
    /// <summary>The results live in a singleton model (the command can fill them while the window is hidden); only the view comes and goes.</summary>
    public Control CreateView(IServiceProvider services) => new FindResultsView(services.GetRequiredService<FindResultsViewModel>());
}
