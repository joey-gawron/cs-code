using CsCode.Core.Contributions;
using CsCode.Core.Platform;
using CsCode.Platform.Commands;
using CsCode.Platform.Configuration;
using CsCode.Platform.Context;
using CsCode.Platform.EditorConfig;
using CsCode.Platform.FileSystem;
using CsCode.Platform.Io;
using CsCode.Platform.Keybindings;
using CsCode.Platform.Logging;
using CsCode.Platform.State;
using CsCode.Platform.Watching;
using CsCode.Platform.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CsCode.Platform;

public static class ServiceCollectionExtensions
{
    private sealed class PlatformRegisteredMarker;

    /// <summary>Registers the Platform services (all singletons) and the rolling file logger. Calling it twice is harmless.</summary>
    public static IServiceCollection AddCsCodePlatform(this IServiceCollection services) => services.AddCsCodePlatform(null);

    /// <summary>Same, with the chance to tune <see cref="PlatformOptions"/> (timings, IO-lane size).</summary>
    public static IServiceCollection AddCsCodePlatform(this IServiceCollection services, Action<PlatformOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(d => d.ServiceType == typeof(PlatformRegisteredMarker))) return services;
        services.AddSingleton<PlatformRegisteredMarker>();

        var options = new PlatformOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        // Shared infrastructure. Created first (as a dependency of everything below), so the container disposes it LAST.
        services.TryAddSingleton(sp => new IoLane("CsCode.IO", sp.GetRequiredService<PlatformOptions>().IoThreads));
        services.TryAddSingleton<AppPaths>(_ => new AppPaths());
        services.TryAddSingleton<IAppPaths>(sp => sp.GetRequiredService<AppPaths>());

        // File system, watcher
        services.TryAddSingleton<ITrashService>(_ => PlatformTrash.Create());
        services.TryAddSingleton(sp => new LocalFileSystem(sp.GetRequiredService<IoLane>(), sp.GetRequiredService<ITrashService>(), sp.GetService<ILogger<LocalFileSystem>>()));
        // IFileSystem / IFileWatcher route by URI scheme: file: is local, other schemes (remote ssh:) go to the module that registered an
        // IFileSystemProvider / IFileWatcherProvider / IFileSearchProvider for them. Providers are resolved lazily (on the first non-file call).
        services.TryAddSingleton(sp => new FileSystemRouter(sp.GetRequiredService<LocalFileSystem>(), () => sp.GetServices<IFileSystemProvider>()));
        services.TryAddSingleton<IFileSystem>(sp => sp.GetRequiredService<FileSystemRouter>());
        // The watcher reads files.watcherExclude from configuration, and configuration uses the watcher: the lazy accessor breaks the cycle
        // (configuration always passes explicit excludes for its own watches, so the accessor is never touched during its construction).
        services.TryAddSingleton(sp => new FileWatcher(sp.GetRequiredService<IoLane>(), new LazyWatcherConfig(sp), sp.GetRequiredService<PlatformOptions>(),
            sp.GetService<ILogger<FileWatcher>>(), backendFactory: null));
        services.TryAddSingleton(sp => new FileWatcherRouter(sp.GetRequiredService<FileWatcher>(), () => sp.GetServices<IFileWatcherProvider>()));
        services.TryAddSingleton<IFileWatcher>(sp => sp.GetRequiredService<FileWatcherRouter>());

        // Configuration, state (both follow the open workspace folder)
        // The window service is resolved only when a workspace file sets a user-only setting (a notice), long after both exist.
        services.TryAddSingleton(sp => new ConfigurationService(sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<IoLane>(), sp.GetRequiredService<IFileWatcher>(),
            sp.GetRequiredService<PlatformOptions>(), sp.GetService<ILogger<ConfigurationService>>(), () => sp.GetService<IWindowService>()));
        services.TryAddSingleton<IConfigurationService>(sp => sp.GetRequiredService<ConfigurationService>());
        services.TryAddSingleton(sp => new StateStorage(sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<IoLane>(), sp.GetRequiredService<PlatformOptions>(),
            sp.GetService<ILogger<StateStorage>>()));
        services.TryAddSingleton<IStateStorage>(sp => sp.GetRequiredService<StateStorage>());

        // Commands, context keys, keybindings
        services.TryAddSingleton(sp => new CommandService(sp.GetService<ILogger<CommandService>>()));
        services.TryAddSingleton<ICommandService>(sp => sp.GetRequiredService<CommandService>());
        services.TryAddSingleton(sp => new ContextKeyService(sp.GetRequiredService<IConfigurationService>(), sp.GetService<ILogger<ContextKeyService>>()));
        services.TryAddSingleton<IContextKeyService>(sp => sp.GetRequiredService<ContextKeyService>());
        services.TryAddSingleton(sp => new KeybindingService(
            sp.GetRequiredService<IContextKeyService>(), sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<IoLane>(),
            sp.GetService<IContributionRegistry>(), sp.GetRequiredService<IConfigurationService>(), sp.GetRequiredService<IStateStorage>(),
            sp.GetRequiredService<IFileWatcher>(), sp.GetRequiredService<PlatformOptions>(), os: null, sp.GetService<ILogger<KeybindingService>>()));
        services.TryAddSingleton<IKeybindingService>(sp => sp.GetRequiredService<KeybindingService>());

        // EditorConfig (follows the workspace folders for invalidation)
        services.TryAddSingleton(sp => new EditorConfigService(sp.GetRequiredService<IoLane>(), sp.GetRequiredService<IFileWatcher>(), sp.GetService<ILogger<EditorConfigService>>()));
        services.TryAddSingleton<IEditorConfigService>(sp => sp.GetRequiredService<EditorConfigService>());

        // Workspace-dependent services are told about folder changes by WorkspaceService (so none of them needs a reference to it).
        services.AddSingleton<IWorkspaceBound>(sp => sp.GetRequiredService<ConfigurationService>());
        services.AddSingleton<IWorkspaceBound>(sp => sp.GetRequiredService<StateStorage>());
        services.AddSingleton<IWorkspaceBound>(sp => sp.GetRequiredService<EditorConfigService>());

        services.TryAddSingleton(sp => new WorkspaceService(
            sp.GetRequiredService<IFileSystem>(), sp.GetRequiredService<IFileWatcher>(), sp.GetRequiredService<IStateStorage>(), sp.GetRequiredService<IConfigurationService>(),
            sp.GetRequiredService<IoLane>(), sp.GetServices<IWorkspaceBound>(), sp.GetRequiredService<PlatformOptions>(), sp.GetService<ILogger<WorkspaceService>>(),
            () => sp.GetServices<IFileSearchProvider>()));
        services.TryAddSingleton<IWorkspaceService>(sp => sp.GetRequiredService<WorkspaceService>());

        return services.AddCsCodeLogging();
    }

    /// <summary>Adds <c>Microsoft.Extensions.Logging</c> with the rolling file logger (<c>{LogsDir}/cscode.log</c>, 5 files × 5 MB) as a provider.</summary>
    public static IServiceCollection AddCsCodeLogging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        services.TryAddSingleton(new FileLoggerOptions());
        // Type-based (not a factory) so TryAddEnumerable can tell it apart from other providers; constructor: (IAppPaths, FileLoggerOptions?, TimeProvider?).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, FileLoggerProvider>());
        return services;
    }

    private sealed class LazyWatcherConfig(IServiceProvider services) : FileWatcher.IConfigurationServiceRead
    {
        public IReadOnlyList<string>? WatcherExcludes() =>
            services.GetService<IConfigurationService>()?.Get<Dictionary<string, bool>>(SettingKeys.FilesWatcherExclude)?
                .Where(kv => kv.Value).Select(kv => kv.Key).ToArray();
    }
}
