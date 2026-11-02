using CsCode.Core.Platform;
using CsCode.Core.Projects;
using CsCode.ProjectTemplates.Engine;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.NewProject;

/// <summary>
/// What the page uses. Only the generators, the creator and the dispatcher are required; without a workspace the new project is not opened,
/// without a window service there is no folder browser and nothing is asked, without state nothing is remembered, without follow-ups nothing
/// runs after the project opened.
/// </summary>
public sealed record NewProjectServices(
    IReadOnlyList<IProjectGenerator> Generators,
    ProjectCreator Creator,
    IUiDispatcher Ui,
    IConfigurationService? Configuration = null,
    IStateStorage? State = null,
    IWorkspaceService? Workspace = null,
    IWindowService? Window = null,
    ICommandService? Commands = null,
    INavigationService? Navigation = null,
    IOutputService? Output = null,
    IViewActivator? Views = null,
    ProjectFollowUps? FollowUps = null)
{
    /// <summary>The user's home folder (tests point it at a temp folder).</summary>
    public string Home { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static NewProjectServices From(IServiceProvider sp) => new(
        [.. sp.GetServices<IProjectGenerator>().OrderBy(g => g.Order).ThenBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)],
        sp.GetRequiredService<ProjectCreator>(),
        sp.GetRequiredService<IUiDispatcher>(),
        sp.GetService<IConfigurationService>(),
        sp.GetService<IStateStorage>(),
        sp.GetService<IWorkspaceService>(),
        sp.GetService<IWindowService>(),
        sp.GetService<ICommandService>(),
        sp.GetService<INavigationService>(),
        sp.GetService<IOutputService>(),
        sp.GetService<IViewActivator>(),
        sp.GetService<ProjectFollowUps>());
}
