using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Profiling;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers this module's services. Owned by the CsCode.Profiling module (fill in; keep this signature).</summary>
    public static IServiceCollection AddCsCodeProfiling(this IServiceCollection services)
    {
        return services;
    }
}
