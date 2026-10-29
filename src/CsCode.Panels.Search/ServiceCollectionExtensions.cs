using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Search;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers this panel set's <c>IToolWindow</c>s and services. Owned by the Panels.Search module (fill in; keep this signature).</summary>
    public static IServiceCollection AddCsCodeSearchPanels(this IServiceCollection services)
    {
        return services;
    }
}
