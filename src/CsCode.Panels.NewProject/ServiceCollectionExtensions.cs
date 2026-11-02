using CsCode.ProjectTemplates;
using CsCode.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.Panels.NewProject;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the New Project page (an <see cref="IToolWindow"/> document view), the <see cref="NewProjectHost"/> that registers its command
    /// (<c>cscode.file.newProject</c>) and setting (<c>cscode.newProject.defaultLocation</c>), and the built-in generators
    /// (<see cref="CsCode.ProjectTemplates.ServiceCollectionExtensions.AddCsCodeProjectTemplates"/>). Required: <c>IUiDispatcher</c>; the
    /// workspace, window, command, navigation, output, state, configuration and view-activator services are optional.
    /// </summary>
    public static IServiceCollection AddCsCodeNewProject(this IServiceCollection services)
    {
        services.AddCsCodeProjectTemplates();
        services.TryAddSingleton(sp => new NewProjectHost(sp));
        services.AddSingleton<IToolWindow>(sp => new NewProjectToolWindow(sp.GetRequiredService<NewProjectHost>()));
        return services;
    }
}
