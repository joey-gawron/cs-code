using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.Panels.Search;

/// <summary>The settings this module owns (registered with <see cref="IConfigurationService.RegisterProperties"/> so they show up in the Settings UI).</summary>
public static class SearchSettings
{
    private static ConfigurationProperty P(string key, object? value, string description, string schemaJson) =>
        new(key, JsonSerializer.SerializeToElement(value), description, JsonDocument.Parse(schemaJson).RootElement.Clone());

    public static IReadOnlyList<ConfigurationProperty> Properties() =>
    [
        P(Engine.SearchLimits.MaxResultsKey, 20_000, "Maximum number of results Find in Files collects before it stops and reports that the results were truncated.", """{"type":"integer","minimum":1,"maximum":1000000}"""),
        P(Engine.SearchLimits.MaxFileSizeKey, 5L * 1024 * 1024, "Files larger than this many bytes are skipped by Find in Files.", """{"type":"integer","minimum":0}"""),
        P(Engine.SearchLimits.RegexTimeoutKey, 1000, "Milliseconds a regular expression may spend on one match before the file is abandoned (only expressions that need the backtracking engine can time out).", """{"type":"integer","minimum":10,"maximum":60000}"""),
        P(Engine.SearchLimits.ExcludeKey, new Dictionary<string, bool>
        {
            ["**/node_modules"] = true, ["**/bower_components"] = true, ["**/*.code-search"] = true,
        }, "Glob patterns excluded from Find in Files, in addition to files.exclude.", """{"type":"object"}"""),
        P(Engine.SearchLimits.UseIgnoreFilesKey, true, "Whether Find in Files honours .gitignore files by default.", """{"type":"boolean"}"""),
        P(ViewModels.FindInFilesViewModel.CollapseResultsKey, "auto", "Controls whether the Find in Files results are shown expanded or collapsed.",
            """{"type":"string","enum":["auto","alwaysCollapse","alwaysExpand"],"enumDescriptions":["Files with few results are expanded.","All files are collapsed.","All files are expanded."]}"""),
        // ---- the search editor (VS Code's keys and defaults)
        P(SearchEditor.SearchEditorService.ContextLinesKey, 1, "The default number of surrounding context lines to use when creating new Search Editors (and opening Find in Files results in one).",
            """{"type":"integer","minimum":0,"maximum":100}"""),
        P(SearchEditor.SearchEditorService.DoubleClickKey, "goToLocation", "Configure effect of double-clicking a result in a search editor.",
            """{"type":"string","enum":["selectWord","goToLocation","openLocationToSide"],"enumDescriptions":["Double-clicking selects the word under the cursor.","Double-clicking opens the result in the active editor group.","Double-clicking opens the result in the editor group to the side, creating one if it does not yet exist."]}"""),
        P(SearchEditor.SearchEditorService.ReuseConfigurationKey, false, "When enabled, new Search Editors will reuse the includes, excludes, and flags of the previously opened Search Editor.", """{"type":"boolean"}"""),
        P(SearchEditor.SearchEditorService.SearchOnTypeKey, true, "Search as you type in a search editor's header (the query, flags and globs). The Find in Files window searches on Enter.", """{"type":"boolean"}"""),
        P(SearchEditor.SearchEditorService.SearchOnTypeDebounceKey, 300, "When `search.searchOnType` is enabled, controls the timeout in milliseconds between a character being typed and the search starting. Has no effect when `search.searchOnType` is disabled.",
            """{"type":"number","minimum":0,"maximum":10000}"""),
    ];
}
