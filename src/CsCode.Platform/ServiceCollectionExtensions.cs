using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Platform;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers this module's services. Owned by the CsCode.Platform module (fill in; keep this signature).</summary>
    public static IServiceCollection AddCsCodePlatform(this IServiceCollection services)
    {
        return services;
    }
}
