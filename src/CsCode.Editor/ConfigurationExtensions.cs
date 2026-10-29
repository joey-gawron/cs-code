using System.Text.Json;
using CsCode.Core.Platform;

namespace CsCode.Editor;

internal static class ConfigurationExtensions
{
    /// <summary>
    /// Typed read with a default that is honoured for unset keys. <c>IConfigurationService.Get&lt;T&gt;(key, default)</c> cannot be relied on
    /// for value types (an unset key may come back as <c>default(T)</c>, e.g. 0 or false), so the editor reads the raw merged value.
    /// A value of the wrong JSON type also falls back to the default instead of throwing.
    /// </summary>
    public static T GetOr<T>(this IConfigurationService cfg, string key, T fallback)
    {
        var raw = cfg.GetRaw(key);
        if (raw is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return fallback;
        try
        {
            return v.Deserialize<T>(CoreJson.Options) ?? fallback;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return fallback;
        }
    }

    /// <summary>Language-scoped override (<c>"[csharp]": { "editor.tabSize": 2 }</c>) falling back to the global value.</summary>
    public static T GetOr<T>(this IConfigurationService cfg, string key, string? languageId, T fallback)
    {
        if (!string.IsNullOrEmpty(languageId) && cfg.GetRaw($"[{languageId}]") is { ValueKind: JsonValueKind.Object } section &&
            section.TryGetProperty(key, out var scoped) && scoped.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            try
            {
                return scoped.Deserialize<T>(CoreJson.Options) ?? fallback;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                // wrong type in the language section: ignore it and use the global value
            }
        }
        return cfg.GetOr(key, fallback);
    }
}
