using CsCode.Editor.Languages;

namespace CsCode.Editor.TreeSitter;

/// <summary>
/// Which CsCode languages the EXPERIMENTAL tree-sitter engine may take over, and under which grammar name. The prototype set is the
/// evaluation's (docs/TREE-SITTER-EVALUATION.md): C#, TypeScript/TSX (and JavaScript, which the TypeScript queries build on), Rust, Python,
/// Markdown (with injections), plus C/C++, HTML and CSS for the quality comparison. Every other language stays on TextMate whatever the
/// setting says. Grammars that only appear as injections (a ```yaml block, Markdown's inline grammar) are found by name when needed.
/// </summary>
internal static class TreeSitterLanguages
{
    private static readonly Dictionary<string, string> GrammarByLanguage = new(StringComparer.Ordinal)
    {
        ["csharp"] = "c_sharp",
        ["typescript"] = "typescript",
        ["typescriptreact"] = "tsx",
        ["javascript"] = "javascript",
        ["javascriptreact"] = "javascript",
        ["rust"] = "rust",
        ["python"] = "python",
        ["markdown"] = "markdown",
        ["cpp"] = "cpp",
        ["c"] = "c",
        ["html"] = "html",
        ["css"] = "css",
    };

    /// <summary>Grammar name → the CsCode language id whose TextMate root scope the grammar's tokens carry (for grammars not in the table above).</summary>
    private static readonly Dictionary<string, string> LanguageByGrammar = new(StringComparer.Ordinal)
    {
        ["markdown_inline"] = "markdown",
        ["bash"] = "shellscript",
        ["yaml"] = "yaml",
        ["toml"] = "toml",
        ["json"] = "json",
        ["lua"] = "lua",
        ["go"] = "go",
        ["java"] = "java",
        ["sql"] = "sql",
        ["diff"] = "diff",
        ["dockerfile"] = "dockerfile",
        ["xml"] = "xml",
        ["scss"] = "scss",
        ["make"] = "makefile",
    };

    /// <summary>Root scopes for injection-only grammars that have no TextMate language of their own.</summary>
    private static readonly Dictionary<string, string> RootByGrammar = new(StringComparer.Ordinal)
    {
        ["jsdoc"] = "comment.block.documentation.jsdoc",
        ["regex"] = "string.regexp.regex",
    };

    public static IReadOnlyCollection<string> Languages => GrammarByLanguage.Keys;

    public static string? GrammarFor(string languageId) => GrammarByLanguage.GetValueOrDefault(languageId);

    /// <summary>The TextMate root scope tree-sitter tokens of <paramref name="grammar"/> carry (<c>c_sharp</c> → <c>source.cs</c>), so theme rules keyed on it apply.</summary>
    public static string? RootScopeFor(string grammar)
    {
        if (RootByGrammar.TryGetValue(grammar, out var root)) return root;
        var language = GrammarByLanguage.FirstOrDefault(kv => kv.Value == grammar).Key ?? LanguageByGrammar.GetValueOrDefault(grammar);
        return language is null ? null : BuiltinLanguages.Catalog.ScopeForLanguage(language);
    }
}
