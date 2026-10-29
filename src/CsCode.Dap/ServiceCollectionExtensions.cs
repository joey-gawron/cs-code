using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Dap;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers this module's services. Owned by the CsCode.Dap module (fill in; keep this signature).</summary>
    public static IServiceCollection AddCsCodeDap(this IServiceCollection services)
    {
        return services;
    }
}
