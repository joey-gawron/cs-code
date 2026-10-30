using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Core.Theming;

namespace CsCode.Platform.Configuration;

/// <summary>
/// Defaults for every key in <see cref="SettingKeys"/> plus the few Platform-owned keys (keymap, file excludes). VS Code-like values:
/// modules that own other settings (LSP, DAP, Profiling…) register their own through <see cref="IConfigurationService.RegisterProperties"/>.
/// </summary>
public static class BuiltInSettings
{
    public const string KeymapKey = "cscode.keymap";

    /// <summary>
    /// The default <c>editor.fontFamily</c>: Fira Code, which CsCode ships (assets/fonts/fira-code), on every OS, then the OS's own faces should
    /// it ever be missing. A stock Mac has none of Cascadia Code or Consolas (and its "monospace" is Helvetica), so there Menlo comes next, as
    /// in VS Code. Elsewhere 'Courier New' is left out: the editor now strips the quotes that kept it from ever matching, and a Linux machine
    /// with Microsoft's core fonts would otherwise switch from fontconfig's monospace to Courier New.
    /// </summary>
    public static string DefaultFontFamily { get; } = OperatingSystem.IsMacOS() ? FontFamilies.MacCode : FontFamilies.Bundled + ", Cascadia Code, Consolas, monospace";

    /// <summary>The folders macOS keeps at a volume's root (Spotlight's index, the FSEvents log, the Trash of a removable volume, versions).</summary>
    private static readonly string[] MacVolumeFolders = [".Spotlight-V100", ".fseventsd", ".Trashes", ".TemporaryItems", ".DocumentRevisions-V100"];

    /// <summary><paramref name="patterns"/> plus, on macOS, the Finder's clutter (AppleDouble <c>._*</c> files on non-HFS volumes and the volume folders).</summary>
    private static Dictionary<string, bool> WithMacClutter(Dictionary<string, bool> patterns, string[] mac)
    {
        if (OperatingSystem.IsMacOS()) foreach (var p in mac) patterns[p] = true;
        return patterns;
    }

    private static ConfigurationProperty P(string key, object? value, string description, string? schemaJson = null) =>
        new(key, JsonSerializer.SerializeToElement(value), description, schemaJson is null ? null : JsonDocument.Parse(schemaJson).RootElement.Clone());

    public static IReadOnlyList<ConfigurationProperty> All { get; } =
    [
        P(SettingKeys.EditorFontFamily, DefaultFontFamily, "Controls the font family.", """{"type":"string"}"""),
        P(SettingKeys.EditorFontLigatures, false, "Font ligatures and contextual alternates (Fira Code's != and =>): true for the font's own, or a CSS font-feature-settings list such as \"'ss01', 'zero'\".",
            """{"type":["boolean","string"]}"""),
        P(SettingKeys.EditorFontSize, 14, "Controls the font size in pixels.", """{"type":"number","minimum":6,"maximum":100}"""),
        P(SettingKeys.EditorTabSize, 4, "The number of spaces a tab is equal to.", """{"type":"integer","minimum":1}"""),
        P(SettingKeys.EditorInsertSpaces, true, "Insert spaces when pressing Tab.", """{"type":"boolean"}"""),
        P(SettingKeys.EditorWordWrap, "off", "Controls how lines should wrap.", """{"type":"string","enum":["off","on","wordWrapColumn","bounded"]}"""),
        P(SettingKeys.EditorMinimapEnabled, true, "Controls whether the minimap is shown.", """{"type":"boolean"}"""),
        P(SettingKeys.EditorFormatOnSave, false, "Format a file on save.", """{"type":"boolean"}"""),
        P(SettingKeys.EditorBracketPairColorization, true, "Controls whether bracket pair colorization is enabled.", """{"type":"boolean"}"""),
        P(SettingKeys.EditorRenderWhitespace, "selection", "Controls how the editor should render whitespace characters.", """{"type":"string","enum":["none","boundary","selection","trailing","all"]}"""),
        P(SettingKeys.EditorSuggestOnTriggerCharacters, true, "Controls whether suggestions should automatically show up when typing trigger characters.", """{"type":"boolean"}"""),
        P(SettingKeys.EditorQuickSuggestions, new Dictionary<string, string> { ["other"] = "on", ["comments"] = "off", ["strings"] = "off" },
            "Controls whether suggestions should automatically show up while typing.", """{"type":"object"}"""),
        P(SettingKeys.EditorLargeFileOptimizationsLimit, 5L * 1024 * 1024, "Files larger than this many bytes open in large-file mode (no tokenisation, folding or squiggles).", """{"type":"integer","minimum":0}"""),
        P(SettingKeys.FilesAutoSave, "off", "Controls auto save of dirty editors.", """{"type":"string","enum":["off","afterDelay","onFocusChange","onWindowChange"]}"""),
        P(SettingKeys.FilesExclude, WithMacClutter(new Dictionary<string, bool>
        {
            ["**/.git"] = true, ["**/.svn"] = true, ["**/.hg"] = true, ["**/CVS"] = true, ["**/.DS_Store"] = true, ["**/Thumbs.db"] = true,
        }, ["**/._*", .. MacVolumeFolders.Select(f => "**/" + f)]), "Glob patterns for files and folders to exclude from the explorer, quick open and search.", """{"type":"object"}"""),
        P(SettingKeys.FilesWatcherExclude, WithMacClutter(new Dictionary<string, bool>
        {
            ["**/.git/objects/**"] = true, ["**/.git/subtree-cache/**"] = true, ["**/.hg/store/**"] = true,
            ["**/node_modules/**"] = true, ["**/bin/**"] = true, ["**/obj/**"] = true,
        }, [.. MacVolumeFolders.Select(f => "**/" + f + "/**")]), "Glob patterns of paths to exclude from file watching.", """{"type":"object"}"""),
        P(SettingKeys.WorkbenchColorTheme, "Visual Studio Dark", "Specifies the color theme used in the workbench.", """{"type":"string"}"""),
        P(SettingKeys.WorkbenchStartupEditor, "welcomePage", "Controls which editor is shown at startup.", """{"type":"string","enum":["none","welcomePage","newUntitledFile"]}"""),
        P(SettingKeys.MemoryBudgetMb, 0, "Soft memory budget in MB (0 = unlimited). Above it caches are trimmed.", """{"type":"integer","minimum":0}"""),
        P(SettingKeys.MemoryTrimOnIdle, true, "Trim re-creatable caches when the application is idle.", """{"type":"boolean"}"""),
        P(KeymapKey, "visualstudio", "The default keybinding set.", """{"type":"string","enum":["visualstudio","vscode"]}"""),
        P("search.useIgnoreFiles", true, "Honour .gitignore files when searching files in the workspace.", """{"type":"boolean"}"""),
        P("search.followSymlinks", true, "Follow symbolic links to directories while searching (link cycles are detected).", """{"type":"boolean"}"""),
    ];
}
