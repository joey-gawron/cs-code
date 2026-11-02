using CsCode.Core.Languages;
using CsCode.Lsp.Manager;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.Lsp;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the native LSP client: <see cref="ILanguageServerManager"/> (singleton, plus the shell-facing
    /// <see cref="CsCode.Core.Languages.IWillSaveParticipant"/> and <see cref="CsCode.Core.Languages.IFileOperationParticipant"/>) and the built-in
    /// server definitions, and server extensions' read-only results (<see cref="Extensions.ReadOnlyDocuments"/>, Core virtual documents). Requires <c>AddCsCodeCore()</c> (documents, registry, diagnostics); everything else
    /// (configuration, window, output, navigation, watcher, commands, memory monitor, logging) is optional and picked up when registered.
    ///
    /// The manager starts observing documents when it is first RESOLVED: resolve <see cref="ILanguageServerManager"/> once at startup.
    /// Disposing the service provider disposes it (all servers are shut down and their process trees killed).
    /// </summary>
    public static IServiceCollection AddCsCodeLsp(this IServiceCollection services)
    {
        // server extensions' results, served as Core virtual documents (the cscode-virtual: scheme's "results" provider)
        services.TryAddSingleton(sp => new Extensions.ReadOnlyDocuments(sp.GetService<CsCode.Core.Text.IVirtualDocumentService>()));
        services.TryAddSingleton(sp => new LanguageServerManager(LspServices.FromProvider(sp)));
        services.TryAddSingleton<ILanguageServerManager>(sp => sp.GetRequiredService<LanguageServerManager>());
        services.AddSingleton<IWillSaveParticipant>(sp => sp.GetRequiredService<LanguageServerManager>());
        services.AddSingleton<IFileOperationParticipant>(sp => sp.GetRequiredService<LanguageServerManager>());
        // the selected Python interpreter as Pyright's / basedpyright's python.pythonPath (nothing when no interpreter service is registered)
        services.AddSingleton<ILanguageServerSettingsContributor>(sp => new PythonServerSettings(sp.GetService<CsCode.Core.Python.IPythonEnvironmentService>()));
        return services;
    }
}
