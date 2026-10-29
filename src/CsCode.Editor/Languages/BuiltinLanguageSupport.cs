namespace CsCode.Editor.Languages;

/// <summary>Where a built-in language's <c>language-configuration.json</c> comes from.</summary>
public enum LanguageConfigurationOrigin
{
    /// <summary>None: the editor's default bracket and quote pairs apply, there are no comment tokens.</summary>
    None,
    /// <summary>VS Code's built-in extension, embedded by TextMateSharp.Grammars.</summary>
    VsCode,
    /// <summary>CsCode's own file (<c>src/CsCode.Editor/Languages/Configurations</c>) for a language VS Code ships none for.</summary>
    CsCode,
}

/// <summary>What the editor itself provides for one built-in language (the facts the support matrix in docs/LANGUAGES.md is generated from).</summary>
/// <param name="GrammarScope">Root scope of the built-in TextMate grammar, or null when the language is not highlighted.</param>
/// <param name="GrammarSource">The TextMateSharp.Grammars package the grammar comes from (a VS Code built-in extension), or null.</param>
public sealed record BuiltinLanguageSupportInfo(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> Filenames,
    IReadOnlyList<string> FilenamePatterns,
    string? FirstLine,
    string? GrammarScope,
    string? GrammarSource,
    LanguageConfigurationOrigin Configuration,
    bool HasBuiltinSnippets);

/// <summary>Read-only view of the built-in language catalog for documentation and diagnostics. User contributions are not included.</summary>
public static class BuiltinLanguageSupport
{
    private static readonly Lazy<IReadOnlyList<BuiltinLanguageSupportInfo>> LanguagesLazy = new(Build);

    /// <summary>Every user-visible built-in language (one with a display name; embedded helper languages are left out), ordered by id.</summary>
    public static IReadOnlyList<BuiltinLanguageSupportInfo> Languages => LanguagesLazy.Value;

    private static IReadOnlyList<BuiltinLanguageSupportInfo> Build()
    {
        var catalog = BuiltinLanguages.Catalog;
        return catalog.Languages
            .Where(l => l.Aliases.Count > 0)
            .OrderBy(l => l.Id, StringComparer.Ordinal)
            .Select(l =>
            {
                var scope = catalog.ScopeForLanguage(l.Id);
                var grammar = scope is null ? null : catalog.GrammarForScope(scope);
                var config = l.ConfigurationPath switch
                {
                    null => LanguageConfigurationOrigin.None,
                    var p when p.StartsWith(BuiltinLanguages.BuiltinPathPrefix + BuiltinLanguages.OwnConfigurationPrefix, StringComparison.Ordinal) => LanguageConfigurationOrigin.CsCode,
                    _ => LanguageConfigurationOrigin.VsCode,
                };
                return new BuiltinLanguageSupportInfo(
                    l.Id, l.Aliases[0], l.Extensions, l.Filenames, l.FilenamePatterns, l.FirstLine,
                    scope, grammar is null ? null : PackageOf(grammar.ResourceName), config,
                    catalog.Snippets.Any(s => s.LanguageId == l.Id));
            })
            .ToArray();
    }

    // "TextMateSharp.Grammars.Resources.Grammars.csharp.syntaxes.csharp.tmLanguage.json" -> "csharp"
    private static string PackageOf(string resourceName)
    {
        var rest = resourceName[BuiltinLanguages.ResourcePrefix.Length..];
        var dot = rest.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? rest : rest[..dot];
    }
}
