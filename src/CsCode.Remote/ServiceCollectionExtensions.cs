using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Remote;
using CsCode.Remote.Bridge;
using CsCode.Remote.Client;
using CsCode.Remote.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.Remote;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers remote editing: <see cref="IRemoteConnectionManager"/>, the <c>ssh:</c> <see cref="IFileSystemProvider"/> / <see cref="IFileWatcherProvider"/> /
    /// <see cref="IFileSearchProvider"/> (picked up by Platform's routing file system, watcher and workspace search), will-save/file-operation
    /// participants for remote documents, and <see cref="RemoteShellContributions"/> (call its <c>Start()</c> once from the shell).
    /// <para>Call AFTER <c>AddCsCodeLsp()</c>: with <c>cscode.languageHost = process</c> the language-server manager registrations made so far (and the
    /// will-save/file-operation participants that resolve it) are redirected to the out-of-process <see cref="ProcessLanguageHost"/>, so the in-process
    /// language client is never constructed. The setting is read when the manager is first resolved (it applies on restart).</para>
    /// </summary>
    public static IServiceCollection AddCsCodeRemote(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(d => d.ServiceType == typeof(RemoteConnectionManager))) return services;

        // Capture the language-host registrations made so far (Lsp's) before adding our own participants.
        RedirectToProcessHost<ILanguageServerManager>(services);
        RedirectToProcessHost<IWillSaveParticipant>(services);
        RedirectToProcessHost<IFileOperationParticipant>(services);

        services.TryAddSingleton(new RemoteOptions());
        services.TryAddSingleton(sp => new LanguageHostMode(sp));
        services.TryAddSingleton(sp => new ProcessLanguageHost(sp, sp.GetRequiredService<RemoteOptions>()));
        services.TryAddSingleton(sp => new RemoteConnectionManager(sp, sp.GetRequiredService<RemoteOptions>()));
        services.TryAddSingleton<IRemoteConnectionManager>(sp => sp.GetRequiredService<RemoteConnectionManager>());
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<RemoteConnectionManager>().FileSystem);
        services.AddSingleton<IFileWatcherProvider>(sp => sp.GetRequiredService<RemoteConnectionManager>().Watcher);
        services.AddSingleton<IFileSearchProvider>(sp => sp.GetRequiredService<RemoteConnectionManager>().Search);
        services.AddSingleton<IWillSaveParticipant>(sp => sp.GetRequiredService<RemoteConnectionManager>().SaveParticipant);
        services.AddSingleton<IFileOperationParticipant>(sp => sp.GetRequiredService<RemoteConnectionManager>().SaveParticipant);
        services.TryAddSingleton(sp => new RemoteShellContributions(sp));
        return services;
    }

    /// <summary>Wraps every existing <typeparamref name="T"/> registration: in process mode it resolves to the <see cref="ProcessLanguageHost"/> instead.</summary>
    private static void RedirectToProcessHost<T>(IServiceCollection services) where T : class
    {
        var existing = services.Where(d => d.ServiceType == typeof(T) && !d.IsKeyedService && d.Lifetime == ServiceLifetime.Singleton).ToArray();
        foreach (var d in existing)
        {
            services.Remove(d);
            var original = d;
            services.AddSingleton<T>(sp => sp.GetRequiredService<LanguageHostMode>().IsProcess
                ? (sp.GetRequiredService<ProcessLanguageHost>() as T ?? throw new InvalidOperationException($"{typeof(T).Name} is not served by the process language host"))
                : Create<T>(sp, original));
        }
    }

    private static T Create<T>(IServiceProvider sp, ServiceDescriptor d) where T : class => d switch
    {
        { ImplementationInstance: T instance } => instance,
        { ImplementationFactory: { } factory } => (T)factory(sp),
        { ImplementationType: { } type } => (T)ActivatorUtilities.CreateInstance(sp, type),
        _ => throw new InvalidOperationException($"Unsupported registration of {typeof(T).Name}"),
    };
}

/// <summary>Reads <c>cscode.languageHost</c> once (or <see cref="RemoteOptions.LanguageHostMode"/>): the choice holds for the process lifetime.</summary>
public sealed class LanguageHostMode(IServiceProvider sp)
{
    private readonly Lazy<bool> _process = new(() =>
        string.Equals(sp.GetService<RemoteOptions>()?.LanguageHostMode ?? sp.GetService<IConfigurationService>()?.Get(RemoteSettings.LanguageHost, "inProcess"),
            "process", StringComparison.OrdinalIgnoreCase));

    public bool IsProcess => _process.Value;
}
