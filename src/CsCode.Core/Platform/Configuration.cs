using System.Text.Json;
using CsCode.Core.Lifecycle;

namespace CsCode.Core.Platform;

public enum ConfigurationTarget { User = 1, Workspace = 2, WorkspaceFolder = 3 }

/// <summary>
/// Where a setting may be set: VS Code's <c>ConfigurationScope</c>, same names and values. <see cref="Application"/> and <see cref="Machine"/>
/// settings are read from the registered defaults and the USER settings.json only; a value in a workspace's or folder's .vscode/.cscode
/// settings.json is ignored (the configuration service logs it and shows a notice once), and writing one there is refused. That is what keeps a
/// cloned repository from choosing the programs CsCode starts: every setting that names an executable, its arguments, its environment or its
/// search path (language servers, debug adapters, shells, ssh, docker) is <see cref="Machine"/>. Everything else defaults to <see cref="Window"/>.
/// </summary>
public enum ConfigurationScope
{
    /// <summary>One value for the whole application (user settings only).</summary>
    Application = 1,
    /// <summary>
    /// A value of this machine: programs and paths CsCode runs (user settings only). In a remote window the remote host's own machine settings file
    /// sets them instead, as in VS Code: the client's values name programs on the client's machine.
    /// </summary>
    Machine = 2,
    /// <summary>The default: user, workspace and folder settings.</summary>
    Window = 3,
    Resource = 4,
    LanguageOverridable = 5,
    /// <summary>Like <see cref="Window"/> (a workspace may set it); kept for VS Code parity.</summary>
    MachineOverridable = 6,
}

/// <summary>Declared by the module that owns the setting through <see cref="IConfigurationService.RegisterProperties"/> (default + JSON schema fragment).</summary>
public sealed record ConfigurationProperty(string Key, JsonElement? Default, string? Description = null, JsonElement? Schema = null)
{
    /// <summary>Where the setting may be set (additive, lsp-core-1); <see cref="ConfigurationScope.Window"/> unless the owner says otherwise.</summary>
    public ConfigurationScope Scope { get; init; } = ConfigurationScope.Window;

    /// <summary>
    /// Member paths below an object-valued setting that are <see cref="ConfigurationScope.Machine"/> although the setting itself is not, relative
    /// to <see cref="Key"/>, dot-separated, <c>*</c> = any one member name. <c>cscode.languageServers</c> declares <c>*.command</c>, <c>*.args</c>,
    /// <c>*.env</c> and <c>*.workingDirectory</c>: a workspace may still tune a server's <c>settings</c> or <c>initializationOptions</c>, but never
    /// what is started. A workspace value at such a path is replaced by the user's (or removed). Additive (lsp-core-1).
    /// </summary>
    public IReadOnlyList<string>? MachineScopedMembers { get; init; }

    /// <summary>True for <see cref="ConfigurationScope.Application"/> and <see cref="ConfigurationScope.Machine"/>: only user settings can set it.</summary>
    public bool IsUserSettingOnly => Scope is ConfigurationScope.Application or ConfigurationScope.Machine;
}

public sealed record ConfigurationChangedEventArgs(IReadOnlyCollection<string> AffectedKeys)
{
    /// <summary>True if <paramref name="section"/> (e.g. "editor" or "editor.fontSize") or any key under it changed.</summary>
    public bool Affects(string section) =>
        AffectedKeys.Any(k => k == section || k.StartsWith(section + ".", StringComparison.Ordinal) || section.StartsWith(k + ".", StringComparison.Ordinal));
}

/// <summary>
/// Layered settings: schema defaults &lt; user settings.json &lt; workspace .cscode/settings.json (also honours .vscode/settings.json)
/// &lt; folder. JSONC files, live-reloaded. Dotted keys and nested objects are equivalent ("editor.fontSize" == {"editor":{"fontSize":..}}).
/// Only the top-level keys of a file (and of a "[lang]" block) are split on dots; member names inside a value (globs, URLs, server ids) are kept
/// as written. Settings registered with an <see cref="ConfigurationProperty.IsUserSettingOnly"/> scope ignore the workspace and folder layers.
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
    public const string EditorFontLigatures = "editor.fontLigatures";
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
    /// <summary>Milliseconds after the last edit before <c>files.autoSave: "afterDelay"</c> saves (VS Code's default: 1000).</summary>
    public const string FilesAutoSaveDelay = "files.autoSaveDelay";
    public const string FilesExclude = "files.exclude";
    public const string FilesWatcherExclude = "files.watcherExclude";
    public const string WorkbenchColorTheme = "workbench.colorTheme";
    public const string WorkbenchStartupEditor = "workbench.startupEditor";
    public const string MemoryBudgetMb = "cscode.memory.budgetMb";
    public const string MemoryTrimOnIdle = "cscode.memory.trimOnIdle";
}
