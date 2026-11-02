using CsCode.Core.Contributions;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers the default in-process implementations of the Core registries (all singletons).</summary>
    public static IServiceCollection AddCsCodeCore(this IServiceCollection services)
    {
        services.AddSingleton<IDocumentManager, DocumentManager>();
        services.AddSingleton<ILanguageFeatureRegistry, LanguageFeatureRegistry>();
        services.AddSingleton<IDiagnosticService, DiagnosticService>();
        services.AddSingleton<IContributionRegistry, ContributionRegistry>();
        // read-only library and generated code (cscode-virtual:), served to the editor as a file system of its own scheme
        services.AddSingleton<VirtualDocumentService>();
        services.AddSingleton<IVirtualDocumentService>(sp => sp.GetRequiredService<VirtualDocumentService>());
        services.AddSingleton<IFileSystemProvider>(sp => sp.GetRequiredService<VirtualDocumentService>());
        return services;
    }
}
