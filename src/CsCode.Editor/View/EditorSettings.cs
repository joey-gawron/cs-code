using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Editor.Documents;

namespace CsCode.Editor.View;

/// <summary>
/// The rendering/behaviour settings of one editor view, read from <see cref="IConfigurationService"/> with per-language overrides
/// (<c>"[csharp]": { "editor.tabSize": 2 }</c>) and <c>.editorconfig</c> on top (indent style/size win over settings, as in VS Code with the
/// EditorConfig extension). Immutable; a settings change builds a new one and the view diffs it.
/// </summary>
public sealed record EditorSettings
{
    public string FontFamily { get; init; } = "Cascadia Code, Cascadia Mono, Consolas, DejaVu Sans Mono, Liberation Mono, monospace";
    public double FontSize { get; init; } = 14;
    public double LineHeightFactor { get; init; } = 1.0;
    public int TabSize { get; init; } = 4;
    public bool InsertSpaces { get; init; } = true;
    public bool WordWrap { get; init; }
    public string RenderWhitespace { get; init; } = "selection";
    public string LineNumbers { get; init; } = "on";
    public IReadOnlyList<int> Rulers { get; init; } = [];
    public string CursorStyle { get; init; } = "line";
    public bool MouseWheelZoom { get; init; }
    public bool BracketPairColorization { get; init; } = true;
    public bool IndentGuides { get; init; } = true;
    public bool HighlightActiveLine { get; init; } = true;
    public bool RenderLineHighlight { get; init; } = true;
    public string QuickSuggestions { get; init; } = "on";
    public bool SuggestOnTriggerCharacters { get; init; } = true;
    public bool FormatOnType { get; init; }
    public bool FormatOnPaste { get; init; }
    public bool AutoClosingBrackets { get; init; } = true;
    public bool AutoClosingQuotes { get; init; } = true;
    public bool AutoSurround { get; init; } = true;
    public bool OccurrencesHighlight { get; init; } = true;
    public bool Folding { get; init; } = true;
    public bool BracketMatching { get; init; } = true;
    public bool Minimap { get; init; }
    public bool StickyScroll { get; init; }
    public bool InlayHints { get; init; } = true;
    public bool CodeLens { get; init; } = true;
    public bool SemanticHighlighting { get; init; } = true;
    public int QuickSuggestionsDelayMs { get; init; } = 10;
    public int HoverDelayMs { get; init; } = 300;
    public int UndoLimit { get; init; } = 1000;
    /// <summary><c>editor.emptySelectionClipboard</c>: Ctrl+C / Ctrl+X on a caret copy / cut the whole line (Visual Studio, VS Code).</summary>
    public bool EmptySelectionClipboard { get; init; } = true;
    /// <summary><c>editor.selectionClipboard</c>: selecting sets the PRIMARY selection and middle-click pastes it (Linux only, as in VS Code).</summary>
    public bool SelectionClipboard { get; init; } = OperatingSystem.IsLinux();
    /// <summary><c>editor.autoIndentOnPaste</c> (and <c>editor.autoIndent</c> not "none"): a multi-line paste is re-indented to where it lands.</summary>
    public bool AutoIndentOnPaste { get; init; } = true;

    public bool ShowLineNumbers => LineNumbers != "off";

    /// <summary>Reads every key; unset ones keep the documented defaults above. Never throws on a wrongly typed value.</summary>
    public static EditorSettings Read(IConfigurationService cfg, string? languageId, EditorConfigProperties? editorConfig = null)
    {
        T G<T>(string key, T fallback) => cfg.GetOr(key, languageId, fallback);
        var defaults = new EditorSettings();
        var tabSize = editorConfig?.EffectiveIndentSize ?? G(SettingKeys.EditorTabSize, defaults.TabSize);
        var insertSpaces = editorConfig?.IndentStyle is { } style ? style == "space" : G(SettingKeys.EditorInsertSpaces, defaults.InsertSpaces);
        var wrap = G(SettingKeys.EditorWordWrap, "off");
        return new EditorSettings
        {
            FontFamily = G(SettingKeys.EditorFontFamily, defaults.FontFamily),
            FontSize = Math.Clamp(G(SettingKeys.EditorFontSize, defaults.FontSize), 6, 72),
            LineHeightFactor = Math.Clamp(G("editor.lineHeightFactor", defaults.LineHeightFactor), 0.8, 3),
            TabSize = Math.Clamp(tabSize, 1, 16),
            InsertSpaces = insertSpaces,
            WordWrap = wrap is "on" or "wordWrapColumn" or "bounded",
            RenderWhitespace = G(SettingKeys.EditorRenderWhitespace, defaults.RenderWhitespace),
            LineNumbers = G("editor.lineNumbers", defaults.LineNumbers),
            Rulers = ReadRulers(cfg.GetRaw("editor.rulers")),
            CursorStyle = G("editor.cursorStyle", defaults.CursorStyle),
            MouseWheelZoom = G("editor.mouseWheelZoom", defaults.MouseWheelZoom),
            BracketPairColorization = G(SettingKeys.EditorBracketPairColorization, defaults.BracketPairColorization),
            IndentGuides = G("editor.guides.indentation", defaults.IndentGuides),
            HighlightActiveLine = G("editor.renderLineHighlight", "line") is "line" or "all",
            QuickSuggestions = ReadQuickSuggestions(cfg.GetRaw(SettingKeys.EditorQuickSuggestions)),
            SuggestOnTriggerCharacters = G(SettingKeys.EditorSuggestOnTriggerCharacters, defaults.SuggestOnTriggerCharacters),
            FormatOnType = G(EditorSettingKeys.FormatOnType, defaults.FormatOnType),
            FormatOnPaste = G(EditorSettingKeys.FormatOnPaste, defaults.FormatOnPaste),
            AutoClosingBrackets = G("editor.autoClosingBrackets", "languageDefined") != "never",
            AutoClosingQuotes = G("editor.autoClosingQuotes", "languageDefined") != "never",
            AutoSurround = G("editor.autoSurround", "languageDefined") != "never",
            OccurrencesHighlight = G("editor.occurrencesHighlight", defaults.OccurrencesHighlight),
            Folding = G("editor.folding", defaults.Folding),
            BracketMatching = G("editor.matchBrackets", "always") != "never",
            Minimap = G(SettingKeys.EditorMinimapEnabled, defaults.Minimap),
            StickyScroll = G("editor.stickyScroll.enabled", defaults.StickyScroll),
            InlayHints = G("editor.inlayHints.enabled", "on") != "off",
            CodeLens = G("editor.codeLens", defaults.CodeLens),
            SemanticHighlighting = G("editor.semanticHighlighting.enabled", true),
            QuickSuggestionsDelayMs = Math.Clamp(G("editor.quickSuggestionsDelay", defaults.QuickSuggestionsDelayMs), 0, 2000),
            HoverDelayMs = Math.Clamp(G("editor.hover.delay", defaults.HoverDelayMs), 0, 5000),
            UndoLimit = Math.Max(0, G(EditorSettingKeys.UndoLimit, defaults.UndoLimit)),
            EmptySelectionClipboard = G("editor.emptySelectionClipboard", defaults.EmptySelectionClipboard),
            SelectionClipboard = OperatingSystem.IsLinux() && G("editor.selectionClipboard", defaults.SelectionClipboard),
            AutoIndentOnPaste = G("editor.autoIndentOnPaste", defaults.AutoIndentOnPaste) && G("editor.autoIndent", "full") != "none",
        };
    }

    /// <summary><c>editor.rulers</c>: an array of columns, or objects <c>{ "column": 80 }</c>.</summary>
    private static IReadOnlyList<int> ReadRulers(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Array } arr) return [];
        var list = new List<int>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n > 0) list.Add(n);
            else if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("column", out var c) && c.TryGetInt32(out var cn) && cn > 0) list.Add(cn);
        }
        return list;
    }

    /// <summary><c>editor.quickSuggestions</c> is a bool, "on"/"off"/"inline", or <c>{ other, comments, strings }</c>; we only need "are identifiers triggering it".</summary>
    private static string ReadQuickSuggestions(JsonElement? raw) => raw switch
    {
        { ValueKind: JsonValueKind.False } => "off",
        { ValueKind: JsonValueKind.String } s => s.GetString() is "off" ? "off" : "on",
        { ValueKind: JsonValueKind.Object } o => o.TryGetProperty("other", out var other) && other.ValueKind is JsonValueKind.False || (other.ValueKind == JsonValueKind.String && other.GetString() == "off") ? "off" : "on",
        _ => "on",
    };
}
