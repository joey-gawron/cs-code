using CsCode.Macros;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CsCode.App.Macros;

/// <summary>The UI thread as the macro player needs it: slices are posted at Background priority, so input and rendering go first.</summary>
public sealed class UiMacroScheduler(IUiDispatcher ui) : IMacroScheduler
{
    public bool IsUiThread => ui.CheckAccess();
    public void Post(Action action) => ui.Post(action, UiPriority.Background);
}

public static class MacroAppServices
{
    /// <summary>The macro module plus its UI scheduler (<see cref="MacroIntegration"/> connects it when the shell starts).</summary>
    public static IServiceCollection AddCsCodeMacrosShell(this IServiceCollection services)
    {
        services.TryAddSingleton<IMacroScheduler>(sp => new UiMacroScheduler(sp.GetRequiredService<IUiDispatcher>()));
        services.AddCsCodeMacros();
        return services;
    }
}
