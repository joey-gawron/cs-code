using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers this module's services. Owned by the CsCode.Extensions module (fill in; keep this signature).</summary>
    public static IServiceCollection AddCsCodeExtensions(this IServiceCollection services)
    {
        return services;
    }
}
