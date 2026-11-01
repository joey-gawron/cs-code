using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Core.Theming;

namespace CsCode.Editor.View;

/// <summary>
/// The settings of the editor's rendering of language-server results that are drawn into the text: code lens (<c>editor.codeLens*</c>) and
/// semantic highlighting (<c>editor.semanticHighlighting.enabled</c>), with VS Code's names, values and defaults. Registered by the view factory so
/// the Settings panel lists them.
/// </summary>
internal static class LanguageRenderSettings
{
    public const string CodeLensKey = "editor.codeLens";
    public const string CodeLensFontSizeKey = "editor.codeLensFontSize";
    public const string CodeLensFontFamilyKey = "editor.codeLensFontFamily";
    public const string SemanticHighlightingKey = "editor.semanticHighlighting.enabled";
    public const string ConfiguredByTheme = "configuredByTheme";

    private static ConfigurationProperty P(string key, object value, string description, string schema) =>
        new(key, JsonSerializer.SerializeToElement(value), description, JsonDocument.Parse(schema).RootElement.Clone());

    public static IReadOnlyList<ConfigurationProperty> Properties { get; } =
    [
        P(CodeLensKey, true, "Controls whether the editor shows CodeLens: the commands a language server (or the Test Explorer) puts above a line, such as \"3 references\" or \"Run Test | Debug Test\".",
            """{"type":"boolean"}"""),
        P(CodeLensFontSizeKey, 0, "Controls the font size in pixels for CodeLens. When set to 0, 90% of `editor.fontSize` is used.",
            """{"type":"integer","minimum":0,"maximum":100}"""),
        P(CodeLensFontFamilyKey, "", "Controls the font family for CodeLens. Empty uses the editor's font.", """{"type":"string"}"""),
        P(SemanticHighlightingKey, ConfiguredByTheme,
            "Controls whether semantic highlighting (colours from the language server's semantic tokens, on top of the grammar's) is shown: true, false, or configuredByTheme (on when the colour theme enables it, as VS Code's Dark+ and the Visual Studio themes do).",
            """{"enum":[true,false,"configuredByTheme"]}"""),
    ];

    /// <summary>The setting as "true", "false" or "configuredByTheme" (anything else is the default).</summary>
    public static string ReadSemanticHighlighting(JsonElement? raw) => raw switch
    {
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        { ValueKind: JsonValueKind.String } s when s.GetString() is "true" or "false" => s.GetString()!,
        _ => ConfiguredByTheme,
    };

    /// <summary>Whether semantic tokens are drawn for <paramref name="setting"/> under <paramref name="theme"/>.</summary>
    public static bool SemanticHighlightingOn(string setting, ColorTheme theme) => setting switch
    {
        "true" => true,
        "false" => false,
        _ => theme.SemanticHighlighting,
    };
}
