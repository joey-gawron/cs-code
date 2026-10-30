namespace CsCode.Editor.TreeSitter;

/// <summary>
/// Read-only facts about tree-sitter support per language, for documentation (docs/LANGUAGES.md is generated from them) and diagnostics:
/// which bundled grammar can colour a language, whether the default (<c>editor.syntaxEngine</c> = <c>auto</c>) uses it, and whether the grammar
/// folds by the tree. User settings are not taken into account.
/// </summary>
public static class TreeSitterLanguageSupport
{
    /// <summary>
    /// Grammars whose pinned nvim-treesitter queries include <c>folds.scm</c> (the others fold by indentation even on tree-sitter). Kept in step
    /// with the bundle by a test (TreeSitterBundleTests).
    /// </summary>
    public static readonly IReadOnlySet<string> GrammarsWithFolds = new HashSet<string>(StringComparer.Ordinal)
    {
        "bash", "c", "c_sharp", "cmake", "cpp", "css", "elixir", "erlang", "go", "haskell", "hcl", "html", "java", "javascript", "json", "kotlin",
        "lua", "markdown", "nix", "ocaml", "ocaml_interface", "proto", "python", "rust", "scala", "scss", "svelte", "terraform", "toml", "tsx",
        "typescript", "vue", "yaml", "zig",
    };

    /// <summary>The tree-sitter grammar for <paramref name="languageId"/>, or null when CsCode has none.</summary>
    public static string? GrammarFor(string languageId) => TreeSitterLanguages.GrammarFor(languageId);

    /// <summary>
    /// True when the default setting colours <paramref name="languageId"/> with tree-sitter: it has a grammar and TextMate has no built-in one.
    /// </summary>
    public static bool IsDefaultFor(string languageId) =>
        GrammarFor(languageId) is not null && Languages.BuiltinLanguages.Catalog.ScopeForLanguage(languageId) is null;
}
