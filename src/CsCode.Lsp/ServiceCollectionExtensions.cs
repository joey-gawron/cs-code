using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Lsp;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers this module's services. Owned by the CsCode.Lsp module (fill in; keep this signature).</summary>
    public static IServiceCollection AddCsCodeLsp(this IServiceCollection services)
    {
        return services;
    }
}
