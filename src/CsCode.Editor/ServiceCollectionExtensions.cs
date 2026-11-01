using CsCode.Core.Languages;
using CsCode.Core.Theming;
using CsCode.Editor.Languages;
using CsCode.Editor.Snippets;
using CsCode.Editor.TextMate;
using CsCode.Editor.Theming;
using CsCode.Editor.Threading;
using CsCode.Editor.View;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Editor;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the editor module: <see cref="IEditorViewFactory"/> (+ the concrete <see cref="EditorViewFactory"/>), <see cref="ILanguageService"/>,
    /// <see cref="IColorThemeService"/>, the TextMate pipeline, the snippet engine and the editor worker thread. Every Core service the editor
    /// consumes (file system, watcher, configuration, window service…) is optional: when one is not registered a small local fallback is used.
    /// Resolve <see cref="IEditorViewFactory"/> once on the UI thread (it captures the UI thread for the document layer).
    /// </summary>
    public static IServiceCollection AddCsCodeEditor(this IServiceCollection services)
    {
        services.AddSingleton(sp => new EditorServices(sp, AvaloniaUiThread.Instance));
        services.AddSingleton(_ => new EditorWorker());
        services.AddSingleton(sp => new LanguageService(sp.GetRequiredService<EditorServices>()));
        services.AddSingleton<ILanguageService>(sp => sp.GetRequiredService<LanguageService>());
        services.AddSingleton(sp => new ColorThemeService(sp.GetRequiredService<EditorServices>()));
        services.AddSingleton<IColorThemeService>(sp => sp.GetRequiredService<ColorThemeService>());
        services.AddSingleton(sp => new SnippetService(sp.GetRequiredService<EditorServices>()));
        services.AddSingleton(sp => new TokenizationManager(sp.GetRequiredService<EditorServices>(), sp.GetRequiredService<EditorWorker>()));
        services.AddSingleton(sp => new EditorViewFactory(
            sp.GetRequiredService<EditorServices>(),
            sp.GetRequiredService<LanguageService>(),
            sp.GetRequiredService<ColorThemeService>(),
            sp.GetRequiredService<TokenizationManager>(),
            sp.GetRequiredService<SnippetService>(),
            sp.GetRequiredService<EditorWorker>()));
        services.AddSingleton<IEditorViewFactory>(sp => sp.GetRequiredService<EditorViewFactory>());
        // Markdown preview (docs/MARKDOWN.md): views the shell hosts in document tabs.
        services.AddSingleton(sp => new Markdown.Preview.MarkdownPreviewFactory(
            sp.GetRequiredService<EditorServices>(),
            sp.GetRequiredService<EditorWorker>(),
            sp.GetRequiredService<ColorThemeService>(),
            sp.GetRequiredService<TokenizationManager>(),
            sp.GetRequiredService<LanguageService>()));
        return services;
    }
}
