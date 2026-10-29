using System.Text.Json;

namespace CsCode.Core.Platform;

/// <summary>
/// Language-scoped settings: <c>"[csharp]": { "editor.tabSize": 2 }</c> in settings.json overrides the plain setting for documents whose language
/// id is <c>csharp</c>. Works with any <see cref="IConfigurationService"/> (it only needs <see cref="IConfigurationService.GetRaw"/>).
/// </summary>
public static class ConfigurationLanguageExtensions
{
    /// <summary>The raw value of <paramref name="key"/> for <paramref name="languageId"/>: the language override if there is one, else the plain value.</summary>
    public static JsonElement? GetRaw(this IConfigurationService config, string key, string languageId) =>
        GetLanguageOverride(config, key, languageId) ?? config.GetRaw(key);

    public static T Get<T>(this IConfigurationService config, string key, string languageId, T defaultValue)
    {
        var raw = config.GetRaw(key, languageId);
        if (raw is null) return defaultValue;
        try { return raw.Value.Deserialize<T>(CoreJson.Options) ?? defaultValue; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException) { return defaultValue; }
    }

    public static T? Get<T>(this IConfigurationService config, string key, string languageId) =>
        config.Get<T>(key, languageId, default!);

    /// <summary>True if the user configured <paramref name="key"/> specifically for <paramref name="languageId"/>.</summary>
    public static bool HasLanguageOverride(this IConfigurationService config, string key, string languageId) =>
        GetLanguageOverride(config, key, languageId) is not null;

    /// <summary>
    /// True if the change affects <paramref name="section"/> for <paramref name="languageId"/>: the plain setting changed, or the language's own override did.
    /// (A plain <see cref="ConfigurationChangedEventArgs.Affects"/> check does not see keys such as <c>[csharp].editor.tabSize</c>.)
    /// </summary>
    public static bool Affects(this ConfigurationChangedEventArgs e, string section, string languageId)
    {
        if (e.Affects(section)) return true;
        var prefix = "[" + languageId + "].";
        foreach (var key in e.AffectedKeys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var inner = key[prefix.Length..]; // e.g. "editor.tabSize" for the affected key "[csharp].editor.tabSize"
            if (inner == section
                || inner.StartsWith(section + ".", StringComparison.Ordinal)   // a key below the section changed
                || section.StartsWith(inner + ".", StringComparison.Ordinal))  // the whole parent object changed
                return true;
        }
        return false;
    }

    private static JsonElement? GetLanguageOverride(IConfigurationService config, string key, string languageId)
    {
        var block = config.GetRaw("[" + languageId + "]");
        if (block is not { ValueKind: JsonValueKind.Object } section) return null;

        // normalised form first: {"editor": {"tabSize": 2}}; then a literal dotted member: {"editor.tabSize": 2}
        var current = section;
        var found = true;
        foreach (var segment in key.Split('.'))
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var next)) current = next;
            else { found = false; break; }
        }
        if (found) return current.ValueKind == JsonValueKind.Null ? null : current;
        return section.TryGetProperty(key, out var flat) && flat.ValueKind != JsonValueKind.Null ? flat : null;
    }
}
