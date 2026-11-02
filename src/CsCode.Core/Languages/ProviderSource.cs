namespace CsCode.Core.Languages;

/// <summary>
/// Implemented by providers that know where they come from: a language server's definition (<see cref="SourceId"/> is its id, the
/// <c>cscode.languageServers</c> key such as "ruff" or "pyright"; <see cref="SourceName"/> its display name). Settings that choose one provider of a
/// feature among several (<c>editor.defaultFormatter</c>) name it by that id. Additive (language stage 1): a provider without it has no id, is never
/// chosen by one, and is still used when nothing is chosen.
/// </summary>
public interface ILanguageProviderSource
{
    string SourceId { get; }
    string SourceName { get; }
}

public static class LanguageProviderSource
{
    /// <summary>The provider's source id, or null when it does not say.</summary>
    public static string? IdOf(ILanguageProvider provider) => (provider as ILanguageProviderSource)?.SourceId;

    /// <summary>The provider's display name: its source's name, else its id, else <paramref name="fallback"/>.</summary>
    public static string NameOf(ILanguageProvider provider, string fallback) =>
        provider is ILanguageProviderSource s ? (string.IsNullOrEmpty(s.SourceName) ? s.SourceId : s.SourceName) : fallback;
}
