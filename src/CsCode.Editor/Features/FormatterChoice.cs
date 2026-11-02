using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Platform;

namespace CsCode.Editor.Features;

/// <summary>
/// <c>editor.defaultFormatter</c>, global or per language (<c>"[python]": { "editor.defaultFormatter": "ruff" }</c>): the language server whose
/// formatter Format Document / Selection, format on save, on paste and on type use. The value is a server id (the <c>cscode.languageServers</c>
/// key); a VS Code extension id whose last segment is that id (<c>charliermarsh.ruff</c>) names it too, so settings copied from VS Code keep
/// working. Unset: every formatter of the document, first answer wins (the behaviour before the setting existed).
/// <para>
/// A provider that does not say which server it comes from (<see cref="ILanguageProviderSource"/>: a remote workspace's bridged providers) cannot be
/// matched; when the chosen server is not among the identified providers, those unidentified ones are used as before rather than nothing. Only
/// when every provider is identified and none is the chosen one does formatting do nothing (and say so), as VS Code does.
/// </para>
/// </summary>
internal static class FormatterChoice
{
    public const string DefaultFormatterKey = "editor.defaultFormatter";

    public static IReadOnlyList<ConfigurationProperty> Properties { get; } =
    [
        new(DefaultFormatterKey, null,
            "The language server whose formatter is used (a cscode.languageServers id such as \"ruff\"; a VS Code extension id ending in it works too). " +
            "Set it per language: \"[python]\": { \"editor.defaultFormatter\": \"ruff\" }. Unset: the first formatter that answers.",
            JsonDocument.Parse("""{"type":["string","null"]}""").RootElement.Clone()),
    ];

    /// <summary>The configured server id for <paramref name="languageId"/>, or null.</summary>
    public static string? Configured(IConfigurationService config, string? languageId) =>
        config.GetOr<string?>(DefaultFormatterKey, languageId, null) is { Length: > 0 } id ? id.Trim() : null;

    /// <summary>Whether <paramref name="configured"/> names the server <paramref name="sourceId"/>.</summary>
    public static bool Names(string configured, string? sourceId) =>
        !string.IsNullOrEmpty(sourceId) &&
        (string.Equals(configured, sourceId, StringComparison.OrdinalIgnoreCase) ||
         configured.EndsWith("." + sourceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The providers a format request may use. <c>Missing</c> is the configured id when it matches none of them (and every provider is
    /// identified): then <c>Providers</c> is empty and the caller tells the user instead of formatting.
    /// </summary>
    public static (IReadOnlyList<IDocumentFormattingProvider> Providers, string? Missing) Select(IReadOnlyList<IDocumentFormattingProvider> all, string? configured)
    {
        if (configured is null || all.Count == 0) return (all, null);
        var chosen = all.Where(p => Names(configured, LanguageProviderSource.IdOf(p))).ToList();
        if (chosen.Count > 0) return (chosen, null);
        var unidentified = all.Where(p => LanguageProviderSource.IdOf(p) is null).ToList();
        return unidentified.Count > 0 ? (unidentified, null) : ([], configured);
    }

    public static string MissingMessage(string id, string? languageId) =>
        $"The default formatter '{id}' is not running for this {(string.IsNullOrEmpty(languageId) ? "file" : languageId + " file")} (editor.defaultFormatter)";
}
