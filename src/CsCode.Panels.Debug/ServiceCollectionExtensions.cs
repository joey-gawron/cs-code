using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Debug;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers this panel set's <c>IToolWindow</c>s and services. Owned by the Panels.Debug module (fill in; keep this signature).</summary>
    public static IServiceCollection AddCsCodeDebugPanels(this IServiceCollection services)
    {
        return services;
    }
}
