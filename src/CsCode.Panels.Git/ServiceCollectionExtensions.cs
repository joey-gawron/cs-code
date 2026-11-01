using Avalonia.Controls;
using CsCode.Core.Diagnostics;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Panels.Git.Engine;
using CsCode.Panels.Git.Views;
using CsCode.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.Panels.Git;

/// <summary>The Source Control tool window (id <c>scm</c>; docks on the right, next to the Explorer, as Visual Studio's Git Changes does).</summary>
public sealed class SourceControlToolWindow(GitHost host) : IToolWindow
{
    public string Id => GitHost.ViewId;
    public string Title => "Source Control";
    public string? IconKey => "source-control";
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Right;
    public string? ToggleCommandId => GitHost.ShowScm;
    public string? AutoShowWhen => null;

    public Control CreateView(IServiceProvider services) => new SourceControlView(host.CreateViewModel(), host.ReleaseViewModel, services.GetService<IContextKeyService>());
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Source control: <see cref="GitService"/> (also the <see cref="IFileDecorationProvider"/>, <see cref="IQuickDiffProvider"/> and
    /// <see cref="ILineBlameProvider"/> the Explorer and the editor read), the host (commands, status bar, menus) and the Source Control tool window.
    /// </summary>
    public static IServiceCollection AddCsCodeGit(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new GitService(sp.GetRequiredService<IWorkspaceService>(), sp.GetService<IConfigurationService>(), sp.GetService<IMemoryMonitor>(),
            sp.GetService<IDocumentManager>(), askpass: sp.GetService<IAskpassService>()));
        services.TryAddSingleton<IFileDecorationProvider>(sp => sp.GetRequiredService<GitService>());
        services.TryAddSingleton<IQuickDiffProvider>(sp => sp.GetRequiredService<GitService>());
        services.TryAddSingleton<ILineBlameProvider>(sp => sp.GetRequiredService<GitService>());
        services.TryAddSingleton(sp => new GitHost(sp, sp.GetRequiredService<GitService>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolWindow, SourceControlToolWindow>());
        return services;
    }
}
