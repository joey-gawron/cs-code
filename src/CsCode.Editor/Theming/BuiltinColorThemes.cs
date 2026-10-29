using CsCode.Core.Theming;
using CsCode.Editor.Languages;

namespace CsCode.Editor.Theming;

/// <summary>Where an embedded theme comes from (for the third-party notices and the docs).</summary>
public enum ThemeOrigin
{
    /// <summary>A theme of Visual Studio Code's built-in extensions (MIT, Copyright Microsoft), as embedded by TextMateSharp.Grammars.</summary>
    VsCode,
    /// <summary>A theme that TextMateSharp.Grammars ships beside VS Code's (not one of VS Code's built-in extensions; the package is MIT).</summary>
    TextMateSharp,
}

/// <param name="File">Resource file name inside TextMateSharp.Grammars (<c>Resources/Themes/</c>).</param>
/// <param name="Id">Theme id (stable; <c>workbench.colorTheme</c> stores the label, ids are accepted too).</param>
/// <param name="Label">Display name: VS Code's label for its themes (the files' own <c>name</c> differs for some, e.g. "Solarized (dark)").</param>
/// <param name="UiTheme">The contribution's <c>uiTheme</c> (<c>vs</c>, <c>vs-dark</c>, <c>hc-black</c>, <c>hc-light</c>): several files carry no <c>type</c>.</param>
public sealed record BuiltinThemeDescriptor(string File, string Id, string Label, string UiTheme, ThemeOrigin Origin);

/// <summary>
/// The 21 colour themes embedded in <c>TextMateSharp.Grammars</c> 2.0.3, all of which CsCode offers. Loading is synchronous and in-memory (the
/// files and their <c>include</c>s are manifest resources), but it parses JSON, so it belongs off the UI thread; the theme service does it once,
/// when it is constructed (the shell resolves it on the start-up pool thread).
/// </summary>
public static class BuiltinColorThemes
{
    public const string PathPrefix = "builtin-theme:";
    private const string ResourcePrefix = "TextMateSharp.Grammars.Resources.Themes.";

    public static IReadOnlyList<BuiltinThemeDescriptor> All { get; } =
    [
        new("dark_plus.json", "vscode.dark-plus", "Dark+ (default dark)", "vs-dark", ThemeOrigin.VsCode),
        new("light_plus.json", "vscode.light-plus", "Light+ (default light)", "vs", ThemeOrigin.VsCode),
        new("dark_vs.json", "vscode.dark-vs", "Dark (Visual Studio)", "vs-dark", ThemeOrigin.VsCode),
        new("light_vs.json", "vscode.light-vs", "Light (Visual Studio)", "vs", ThemeOrigin.VsCode),
        new("hc_black.json", "vscode.hc-black", "Dark High Contrast", "hc-black", ThemeOrigin.VsCode),
        new("hc_light.json", "vscode.hc-light", "Light High Contrast", "hc-light", ThemeOrigin.VsCode),
        new("abyss-color-theme.json", "vscode.abyss", "Abyss", "vs-dark", ThemeOrigin.VsCode),
        new("kimbie-dark-color-theme.json", "vscode.kimbie-dark", "Kimbie Dark", "vs-dark", ThemeOrigin.VsCode),
        new("monokai-color-theme.json", "vscode.monokai", "Monokai", "vs-dark", ThemeOrigin.VsCode),
        new("dimmed-monokai-color-theme.json", "vscode.monokai-dimmed", "Monokai Dimmed", "vs-dark", ThemeOrigin.VsCode),
        new("quietlight-color-theme.json", "vscode.quiet-light", "Quiet Light", "vs", ThemeOrigin.VsCode),
        new("Red-color-theme.json", "vscode.red", "Red", "vs-dark", ThemeOrigin.VsCode),
        new("solarized-dark-color-theme.json", "vscode.solarized-dark", "Solarized Dark", "vs-dark", ThemeOrigin.VsCode),
        new("solarized-light-color-theme.json", "vscode.solarized-light", "Solarized Light", "vs", ThemeOrigin.VsCode),
        new("tomorrow-night-blue-color-theme.json", "vscode.tomorrow-night-blue", "Tomorrow Night Blue", "vs-dark", ThemeOrigin.VsCode),
        new("atom-one-dark-color-theme.json", "textmatesharp.atom-one-dark", "Atom One Dark", "vs-dark", ThemeOrigin.TextMateSharp),
        new("atom-one-light-color-theme.json", "textmatesharp.atom-one-light", "Atom One Light", "vs", ThemeOrigin.TextMateSharp),
        new("onedark-color-theme.json", "textmatesharp.one-dark", "One Dark", "vs-dark", ThemeOrigin.TextMateSharp),
        new("dracula-color-theme.json", "textmatesharp.dracula", "Dracula", "vs-dark", ThemeOrigin.TextMateSharp),
        // Named "Visual Studio Dark/Light" in their files: renamed so they cannot be confused with the shell's own Visual Studio 2022 themes.
        new("visual-studio-dark-theme.json", "textmatesharp.visual-studio-dark", "Visual Studio Dark (TextMateSharp)", "vs-dark", ThemeOrigin.TextMateSharp),
        new("visual-studio-light-theme.json", "textmatesharp.visual-studio-light", "Visual Studio Light (TextMateSharp)", "vs", ThemeOrigin.TextMateSharp),
    ];

    /// <summary>Whether <paramref name="themeId"/> is one of <see cref="All"/>.</summary>
    public static bool IsBuiltin(string themeId) => All.Any(d => string.Equals(d.Id, themeId, StringComparison.Ordinal));

    /// <summary>Loads one embedded theme with its <c>include</c> chain resolved (the including theme overrides key by key).</summary>
    public static ColorTheme Load(BuiltinThemeDescriptor d)
    {
        var text = ReadResource(d.File) ?? throw new InvalidOperationException($"Built-in theme {d.File} is missing from TextMateSharp.Grammars");
        // Built-ins only include other built-ins, all embedded: a fully synchronous, in-memory load (no I/O, nothing to await).
        return VsCodeThemeLoader.LoadSync(d.Id, d.Label, d.UiTheme, text, PathPrefix + d.File, (_, rel) =>
        {
            var f = rel.TrimStart('.', '/');
            return ReadResource(f) is { } t ? (PathPrefix + f, t) : null;
        });
    }

    /// <summary>Every embedded theme, in catalogue order.</summary>
    public static IReadOnlyList<ColorTheme> LoadAll() => All.Select(Load).ToArray();

    /// <summary>The text of an embedded theme file (<c>dark_plus.json</c>), or null when the package has no such file.</summary>
    public static string? ReadResource(string file)
    {
        using var s = BuiltinLanguages.Open(ResourcePrefix + file);
        if (s is null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
