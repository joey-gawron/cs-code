using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Editor.Languages;
using CsCode.Editor.Snippets;
using CsCode.Editor.TextMate;
using CsCode.Editor.Theming;
using CsCode.Editor.Threading;
using CsCode.Editor.View;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        // editor.codeActionsOnSave. Registered as an IWillSaveParticipant by the shell (AddCodeActionsOnSave), not here: the remote module redirects
        // the will-save participants registered before it to the out-of-process language host.
        services.AddSingleton(sp => new Features.CodeActionsOnSaveParticipant(sp.GetRequiredService<EditorServices>()));
        // Bookmarks (docs/EDITOR.md §7g): the workspace's bookmarks, kept on their lines through edits and persisted per workspace. Registered as an
        // IFileOperationParticipant (renames and deletes in CsCode) by the shell, AddBookmarkFileTracking: the remote module redirects the participants
        // registered before it to the out-of-process language host.
        services.AddSingleton(sp => new Bookmarks.BookmarkService(sp.GetRequiredService<EditorServices>().Documents, sp.GetService<IStateStorage>(), sp.GetService<IWorkspaceService>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger("CsCode.Editor.Bookmarks")));
        services.AddSingleton<IBookmarkService>(sp => sp.GetRequiredService<Bookmarks.BookmarkService>());
        // Markdown preview (docs/MARKDOWN.md): views the shell hosts in document tabs.
        services.AddSingleton(sp => new Markdown.Preview.MarkdownPreviewFactory(
            sp.GetRequiredService<EditorServices>(),
            sp.GetRequiredService<EditorWorker>(),
            sp.GetRequiredService<ColorThemeService>(),
            sp.GetRequiredService<TokenizationManager>(),
            sp.GetRequiredService<LanguageService>()));
        return services;
    }

    /// <summary>
    /// Makes bookmarks follow a file that CsCode renames, moves or deletes (<see cref="Bookmarks.BookmarkService"/> as an <see cref="IFileOperationParticipant"/>).
    /// Call after <c>AddCsCodeRemote</c>, which redirects the file-operation participants registered before it to the out-of-process language host.
    /// </summary>
    public static IServiceCollection AddBookmarkFileTracking(this IServiceCollection services)
    {
        services.AddSingleton<IFileOperationParticipant>(sp => sp.GetRequiredService<Bookmarks.BookmarkService>());
        return services;
    }

    /// <summary>
    /// Makes <c>editor.codeActionsOnSave</c> (<see cref="Features.CodeActionsOnSaveParticipant"/>) the FIRST will-save participant, ahead of the language
    /// servers' <c>willSaveWaitUntil</c>. Call after every other module that registers will-save participants (after <c>AddCsCodeRemote</c>, which
    /// redirects the ones registered before it to the out-of-process language host).
    /// </summary>
    public static IServiceCollection AddCodeActionsOnSave(this IServiceCollection services)
    {
        var first = services.TakeWhile(d => d.ServiceType != typeof(IWillSaveParticipant)).Count();
        services.Insert(first, ServiceDescriptor.Singleton<IWillSaveParticipant>(sp => sp.GetRequiredService<Features.CodeActionsOnSaveParticipant>()));
        return services;
    }
}
