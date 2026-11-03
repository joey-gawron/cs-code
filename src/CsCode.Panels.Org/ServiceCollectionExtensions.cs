using Avalonia.Controls;
using CsCode.Panels.Org.Views;
using CsCode.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.Panels.Org;

/// <summary>The Org Agenda tool window (id <c>org.agenda</c>; docks on the right, next to Source Control).</summary>
public sealed class OrgAgendaToolWindow(OrgHost host) : IToolWindow
{
    public string Id => OrgHost.ViewId;
    public string Title => "Org Agenda";
    public string? IconKey => "checklist";
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Right;
    public string? ToggleCommandId => OrgHost.ShowAgenda;
    public string? AutoShowWhen => null;

    public Control CreateView(IServiceProvider services) => new OrgAgendaView(host.CreateViewModel(), host.ReleaseViewModel, host);
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Org mode: the <see cref="OrgHost"/> (the org language, the <c>org.*</c> commands, keys and settings, the agenda's files) and the Org Agenda
    /// tool window. The tool window resolves the host, so the shell enumerating <c>IEnumerable&lt;IToolWindow&gt;</c> at startup starts it. Every
    /// service it uses is optional.
    /// </summary>
    public static IServiceCollection AddCsCodeOrg(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new OrgHost(sp));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolWindow, OrgAgendaToolWindow>());
        return services;
    }
}
