using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Vim;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the Vim engine factory, settings and commands. Owned by the Vim module (fill in; keep this signature).</summary>
    public static IServiceCollection AddCsCodeVim(this IServiceCollection services)
    {
        return services;
    }
}
