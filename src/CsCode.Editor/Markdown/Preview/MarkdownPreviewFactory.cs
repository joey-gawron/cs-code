using CsCode.Core.Lifecycle;
using CsCode.Editor.Features;
using CsCode.Editor.Languages;
using CsCode.Editor.TextMate;
using CsCode.Editor.Theming;
using CsCode.Editor.Threading;

namespace CsCode.Editor.Markdown.Preview;

/// <summary>
/// Creates Markdown previews (<see cref="MarkdownPreviewView"/>) over the editor module's services: the worker lane for parsing, the TextMate
/// grammars and the colour theme for code blocks, the file system for images and closed files. Registered by <c>AddCsCodeEditor</c>; the shell
/// hosts the views in document tabs (docs/MARKDOWN.md). Registers the <c>markdown.preview.*</c> settings once.
/// </summary>
public sealed class MarkdownPreviewFactory : IDisposable
{
    private readonly IDisposable _settings;

    internal MarkdownPreviewFactory(EditorServices services, EditorWorker worker, ColorThemeService themes, TokenizationManager? tokens, LanguageService? languages)
    {
        Services = services;
        Worker = worker;
        Themes = themes;
        Tokens = tokens;
        Languages = languages;
        _settings = services.Config.RegisterProperties(MarkdownPreviewSettings.Properties);
    }

    internal EditorServices Services { get; }
    internal EditorWorker Worker { get; }
    internal ColorThemeService Themes { get; }
    internal TokenizationManager? Tokens { get; }
    internal LanguageService? Languages { get; }

    /// <summary>Whether <paramref name="languageId"/> is previewable (Markdown).</summary>
    public static bool IsMarkdown(string? languageId) => string.Equals(languageId, "markdown", StringComparison.OrdinalIgnoreCase);

    /// <summary>A new preview of <paramref name="source"/> (the document if it is open, else the file). The caller owns and disposes it. UI thread.</summary>
    public MarkdownPreviewView Create(Uri source) => new(this, source);

    /// <summary>The code colouring of a parse, bound to the current theme (worker thread: the grammars live there).</summary>
    internal CodeColorizer? CreateColorizer()
    {
        if (Tokens is not { } tokens || Languages is not { } languages) return null;
        var theme = Themes.Current;
        var fg = Themes.GetColor("editor.foreground");
        return (info, code, ct) =>
        {
            try { return CodeHighlighter.HighlightCode(info, code, tokens, languages, theme, fg, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; } // an odd grammar must not lose the preview
        };
    }

    public void Dispose() => _settings.Dispose();
}
