using CsCode.Core.Contributions;
using CsCode.Core.Languages;
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
        return services;
    }
}
