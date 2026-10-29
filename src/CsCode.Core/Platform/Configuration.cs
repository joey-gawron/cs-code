using System.Text.Json;
using CsCode.Core.Lifecycle;

namespace CsCode.Core.Platform;

public enum ConfigurationTarget { User = 1, Workspace = 2, WorkspaceFolder = 3 }

/// <summary>Declared by `contributes.configuration` or by built-in modules (defaults + JSON schema fragment).</summary>
public sealed record ConfigurationProperty(string Key, JsonElement? Default, string? Description = null, JsonElement? Schema = null, string? ExtensionId = null);

public sealed record ConfigurationChangedEventArgs(IReadOnlyCollection<string> AffectedKeys)
{
    /// <summary>True if <paramref name="section"/> (e.g. "editor" or "editor.fontSize") or any key under it changed.</summary>
    public bool Affects(string section) =>
        AffectedKeys.Any(k => k == section || k.StartsWith(section + ".", StringComparison.Ordinal) || section.StartsWith(k + ".", StringComparison.Ordinal));
}

/// <summary>
/// Layered settings: schema defaults &lt; user settings.json &lt; workspace .cscode/settings.json (also honours .vscode/settings.json)
/// &lt; folder. JSONC files, live-reloaded. Dotted keys and nested objects are equivalent ("editor.fontSize" == {"editor":{"fontSize":..}}).
/// </summary>
public interface IConfigurationService
{
    T Get<T>(string key, T defaultValue);
    T? Get<T>(string key);
    /// <summary>Raw merged value for a key or section (object for sections), or null.</summary>
    JsonElement? GetRaw(string key);
    ValueTask UpdateAsync(string key, object? value, ConfigurationTarget target = ConfigurationTarget.User, CancellationToken ct = default);
    IDisposable RegisterProperties(IEnumerable<ConfigurationProperty> properties);
    IReadOnlyList<ConfigurationProperty> Properties { get; }
    IEventSource<ConfigurationChangedEventArgs> Changed { get; }
    string UserSettingsPath { get; }
}

/// <summary>Well-known setting keys (VS Code–compatible names). Defaults are registered by the module that owns them.</summary>
public static class SettingKeys
{
    public const string EditorFontFamily = "editor.fontFamily";
    public const string EditorFontSize = "editor.fontSize";
    public const string EditorTabSize = "editor.tabSize";
    public const string EditorInsertSpaces = "editor.insertSpaces";
    public const string EditorWordWrap = "editor.wordWrap";
    public const string EditorMinimapEnabled = "editor.minimap.enabled";
    public const string EditorFormatOnSave = "editor.formatOnSave";
    public const string EditorBracketPairColorization = "editor.bracketPairColorization.enabled";
    public const string EditorRenderWhitespace = "editor.renderWhitespace";
    public const string EditorSuggestOnTriggerCharacters = "editor.suggestOnTriggerCharacters";
    public const string EditorQuickSuggestions = "editor.quickSuggestions";
    public const string EditorLargeFileOptimizationsLimit = "cscode.editor.largeFileThresholdBytes";
    public const string FilesAutoSave = "files.autoSave";
    public const string FilesExclude = "files.exclude";
    public const string FilesWatcherExclude = "files.watcherExclude";
    public const string WorkbenchColorTheme = "workbench.colorTheme";
    public const string WorkbenchStartupEditor = "workbench.startupEditor";
    public const string ExtensionsNodePath = "cscode.extensionHost.nodePath";
    public const string ExtensionsAutoUpdate = "extensions.autoUpdate";
    public const string MemoryBudgetMb = "cscode.memory.budgetMb";
    public const string MemoryTrimOnIdle = "cscode.memory.trimOnIdle";
}
