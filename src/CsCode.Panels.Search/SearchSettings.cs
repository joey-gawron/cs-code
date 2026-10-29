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
    ];
}
