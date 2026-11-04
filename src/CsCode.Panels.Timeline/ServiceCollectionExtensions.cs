using Avalonia.Controls;
using CsCode.Core.History;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Core.Timeline;
using CsCode.Panels.Timeline.LocalHistory;
using CsCode.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CsCode.Panels.Timeline;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Local history and the Timeline: <see cref="LocalHistoryService"/> (also <see cref="ILocalHistoryService"/> and an
    /// <see cref="IFileOperationParticipant"/>, so entries follow renames), its <see cref="ITimelineProvider"/>, the <see cref="TimelineHost"/>
    /// (commands) and the Timeline tool window. The tool window resolves the host, which resolves the service: the shell enumerating the tool
    /// windows at startup is what arms the save hook and registers the commands.
    /// <para>Register it after <c>AddCsCodeRemote()</c>: that call redirects the file-operation participants registered before it to the
    /// out-of-process language host when <c>cscode.languageHost = process</c>.</para>
    /// </summary>
    public static IServiceCollection AddCsCodeTimeline(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new LocalHistoryService(
            sp.GetService<IFileSystem>() ?? sp.GetRequiredService<IWorkspaceService>().FileSystem,
            Path.Combine(sp.GetRequiredService<IAppPaths>().UserDataDir, LocalHistoryService.HistoryFolderName),
            sp.GetService<IConfigurationService>(),
            sp.GetService<IDocumentManager>(),
            sp.GetService<IWorkspaceService>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("CsCode.LocalHistory")));
        services.TryAddSingleton<ILocalHistoryService>(sp => sp.GetRequiredService<LocalHistoryService>());
        services.AddSingleton<IFileOperationParticipant>(sp => sp.GetRequiredService<LocalHistoryService>());
        services.AddSingleton<ITimelineProvider>(sp => new LocalHistoryTimelineProvider(sp.GetRequiredService<LocalHistoryService>()));
        services.TryAddSingleton(sp => new TimelineHost(sp, sp.GetRequiredService<LocalHistoryService>()));
        services.AddSingleton<IToolWindow>(sp => new TimelineToolWindow(sp.GetRequiredService<TimelineHost>()));
        return services;
    }
}

/// <summary>
/// "Timeline" (<c>timeline</c>): VS Code's Timeline view, docked on the right with the Explorer and Source Control. The shell creates the view
/// when it is shown and disposes it (and its view-model) when it is hidden.
/// </summary>
public sealed class TimelineToolWindow(TimelineHost host) : IToolWindow
{
    public string Id => TimelineHost.ViewId;
    public string Title => "Timeline";
    public string? IconKey => "history";
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Right;
    public string? ToggleCommandId => TimelineHost.Focus;
    public string? AutoShowWhen => null;

    public Control CreateView(IServiceProvider services) => new TimelineView(host, host.CreateViewModel());
}
