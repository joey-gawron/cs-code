using CsCode.Editor.Languages;

namespace CsCode.Editor.TreeSitter;

/// <summary>
/// Which CsCode languages tree-sitter can colour, and with which grammar (docs/TREE-SITTER.md, "Languages"). Two groups:
/// <list type="bullet">
/// <item>languages TextMate has no grammar for (TOML, Kotlin, Scala, Zig, Protobuf, GraphQL, Haskell, Elixir, Erlang, OCaml, Vue, Svelte,
/// Terraform, Nix, reStructuredText, assembly, CMake, CSV, JSON Lines, Julia Markdown): the <c>auto</c> default colours them with tree-sitter;</item>
/// <item>languages TextMate already colours (C#, TypeScript/TSX, JavaScript, Rust, Python, Markdown, C/C++, HTML, CSS/SCSS, shell, YAML, JSON,
/// Lua, Go, Java): tree-sitter only when the user opts in (<c>editor.syntaxEngine</c> = <c>treesitter</c>, globally or per language).</item>
/// </list>
/// Every grammar named here is in the pinned bundle (tools/treesitter/manifest.json; a test keeps the two in step). Grammars that only appear as
/// injections (Markdown's inline grammar, regex, JSDoc, HCL) are found by name when needed.
/// </summary>
internal static class TreeSitterLanguages
{
    private static readonly Dictionary<string, string> GrammarByLanguage = new(StringComparer.Ordinal)
    {
        // TextMate colours these by default; tree-sitter is opt-in
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
        ["scss"] = "scss",
        ["shellscript"] = "bash",
        ["yaml"] = "yaml",
        ["json"] = "json",
        ["jsonc"] = "json",
        ["lua"] = "lua",
        ["go"] = "go",
        ["java"] = "java",
        // no TextMate grammar: tree-sitter by default (auto)
        ["toml"] = "toml",
        ["cmake"] = "cmake",
        ["kotlin"] = "kotlin",
        ["scala"] = "scala",
        ["zig"] = "zig",
        ["protobuf"] = "proto",
        ["graphql"] = "graphql",
        ["haskell"] = "haskell",
        ["elixir"] = "elixir",
        ["erlang"] = "erlang",
        ["ocaml"] = "ocaml",
        ["vue"] = "vue",
        ["svelte"] = "svelte",
        ["terraform"] = "terraform",
        ["terraform-vars"] = "terraform",
        ["nix"] = "nix",
        ["restructuredtext"] = "rst",
        ["assembly"] = "nasm",
        ["gas"] = "asm",
        ["csv"] = "csv",
        ["jsonl"] = "json",
        ["juliamarkdown"] = "markdown",
    };

    /// <summary>Grammar name → the CsCode language id whose TextMate root scope the grammar's tokens carry (for grammars not in the table above).</summary>
    private static readonly Dictionary<string, string> LanguageByGrammar = new(StringComparer.Ordinal)
    {
        ["markdown_inline"] = "markdown",
        ["ocaml_interface"] = "ocaml",
        ["sql"] = "sql",
        ["diff"] = "diff",
        ["dockerfile"] = "dockerfile",
        ["xml"] = "xml",
        ["make"] = "makefile",
    };

    /// <summary>Root scopes for grammars without a TextMate language of their own (their tokens still need a root theme rules can key on).</summary>
    private static readonly Dictionary<string, string> RootByGrammar = new(StringComparer.Ordinal)
    {
        ["jsdoc"] = "comment.block.documentation.jsdoc",
        ["regex"] = "string.regexp.regex",
        ["hcl"] = "source.hcl",
    };

    public static IReadOnlyCollection<string> Languages => GrammarByLanguage.Keys;

    /// <summary>The grammar for a language (null: tree-sitter cannot colour it).</summary>
    public static string? GrammarFor(string languageId) => GrammarByLanguage.GetValueOrDefault(languageId);

    /// <summary>
    /// The grammar for one document: like <see cref="GrammarFor(string)"/>, except where a language has two grammars by file kind (OCaml
    /// interfaces, <c>.mli</c>, have a grammar of their own).
    /// </summary>
    public static string? GrammarFor(string languageId, Uri? uri)
    {
        var grammar = GrammarFor(languageId);
        if (grammar == "ocaml" && uri is not null && uri.AbsolutePath.EndsWith(".mli", StringComparison.OrdinalIgnoreCase)) return "ocaml_interface";
        return grammar;
    }

    /// <summary>The TextMate root scope tree-sitter tokens of <paramref name="grammar"/> carry (<c>c_sharp</c> → <c>source.cs</c>), so theme rules keyed on it apply.</summary>
    public static string? RootScopeFor(string grammar)
    {
        if (RootByGrammar.TryGetValue(grammar, out var root)) return root;
        var language = GrammarByLanguage.FirstOrDefault(kv => kv.Value == grammar).Key ?? LanguageByGrammar.GetValueOrDefault(grammar);
        return language is null ? null : BuiltinLanguages.Catalog.ScopeForLanguage(language);
    }
}
