using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Editor;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the editor module: IEditorViewFactory, ILanguageService, IColorThemeService, TextMate registry, snippet engine.
    /// Owned by the Editor module (fill in; keep this signature).
    /// </summary>
    public static IServiceCollection AddCsCodeEditor(this IServiceCollection services)
    {
        services.AddSingleton<IEditorViewFactory, PlainEditorViewFactory>();
        return services;
    }
}
