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

    /// <summary>
    /// Language-scoped override (<c>"[csharp]": { "editor.tabSize": 2 }</c>) falling back to the global value. A wrongly typed override is
    /// ignored (the global value applies). The section is read in every shape it comes in (see <see cref="LanguageOverride"/>).
    /// </summary>
    public static T GetOr<T>(this IConfigurationService cfg, string key, string? languageId, T fallback)
    {
        if (LanguageOverride(cfg, key, languageId) is { } scoped)
        {
            try
            {
                if (scoped.Deserialize<T>(CoreJson.Options) is { } value) return value;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                // wrong type in the language section: ignore it and use the global value
            }
        }
        return cfg.GetOr(key, fallback);
    }

    /// <summary>The raw value of <paramref name="key"/> for <paramref name="languageId"/>: its language override if there is one, else the global value.</summary>
    public static JsonElement? GetRawFor(this IConfigurationService cfg, string key, string? languageId) =>
        LanguageOverride(cfg, key, languageId) ?? cfg.GetRaw(key);

    /// <summary>
    /// The value of <paramref name="key"/> inside the <c>"[languageId]"</c> section, or null. The settings service hands sections back with their
    /// dotted keys expanded (<c>{"editor": {"tabSize": 2}}</c>), other configuration services (and settings written by hand) keep them as written
    /// (<c>{"editor.tabSize": 2}</c>), and the two can be mixed (<c>{"editor": {"suggest.insertMode": …}}</c>): at each level the longest member
    /// that matches wins, as Core's <c>ConfigurationLanguageExtensions</c> reads the first two shapes (T30: the editor read only the flat one, so
    /// per-language settings were ignored in the real app).
    /// </summary>
    internal static JsonElement? LanguageOverride(IConfigurationService cfg, string key, string? languageId)
    {
        if (string.IsNullOrEmpty(languageId) || cfg.GetRaw("[" + languageId + "]") is not { ValueKind: JsonValueKind.Object } section) return null;
        return Find(section, key) is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value ? value : null;
    }

    private static JsonElement? Find(JsonElement obj, string key)
    {
        if (obj.TryGetProperty(key, out var whole)) return whole;
        for (var dot = key.IndexOf('.'); dot > 0 && dot < key.Length - 1; dot = key.IndexOf('.', dot + 1))
        {
            if (obj.TryGetProperty(key.AsSpan(0, dot), out var child) && child.ValueKind == JsonValueKind.Object &&
                Find(child, key[(dot + 1)..]) is { } found)
                return found;
        }
        return null;
    }
}
