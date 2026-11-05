using System.Reflection;
using System.Text.Json;
using CsCode.Core.Contributions;
using TextMateSharp.Grammars;

namespace CsCode.Editor.Languages;

/// <summary>A grammar that ships inside TextMateSharp.Grammars (embedded VS Code built-in extension).</summary>
internal sealed record BuiltinGrammar(
    string? LanguageId,
    string ScopeName,
    string ResourceName,
    IReadOnlyDictionary<string, string>? EmbeddedLanguages);

/// <summary>A snippet file shipped with a built-in language (VS Code <c>.code-snippets</c>).</summary>
internal sealed record BuiltinSnippetFile(string LanguageId, string ResourceName);

internal sealed record BuiltinCatalog(IReadOnlyList<LanguageContribution> Languages, IReadOnlyList<BuiltinGrammar> Grammars, IReadOnlyList<BuiltinSnippetFile> Snippets)
{
    private readonly Dictionary<string, string> _scopeByLanguage = Grammars
        .Where(g => g.LanguageId is not null)
        .GroupBy(g => g.LanguageId!)
        .ToDictionary(g => g.Key, g => g.First().ScopeName);

    private readonly Dictionary<string, BuiltinGrammar> _byScope = Grammars
        .GroupBy(g => g.ScopeName)
        .ToDictionary(g => g.Key, g => g.First());

    public string? ScopeForLanguage(string languageId) => _scopeByLanguage.GetValueOrDefault(languageId);
    public BuiltinGrammar? GrammarForScope(string scope) => _byScope.GetValueOrDefault(scope);
}

/// <summary>
/// The built-in languages, grammars and language configurations. They are the real VS Code built-in extensions that
/// TextMateSharp.Grammars embeds (MIT); this reads their <c>package.json</c> manifests directly instead of going through
/// <c>RegistryOptions</c>, so it is cheap, synchronous and needs no TextMate registry. A short supplement table adds
/// languages VS Code ships no grammar for (Kotlin, Zig, TOML, …) so detection still yields the id language servers expect,
/// each with a language configuration of CsCode's own (<c>Languages/Configurations/*.language-configuration.json</c>, embedded
/// in this assembly, same VS Code format) so comments, brackets, auto-closing and indentation work before any highlighting does.
/// </summary>
internal static class BuiltinLanguages
{
    internal const string ResourcePrefix = "TextMateSharp.Grammars.Resources.Grammars.";
    internal const string BuiltinPathPrefix = "builtin:";
    /// <summary>Resource names of CsCode's own language configurations (this assembly, see CsCode.Editor.csproj).</summary>
    internal const string OwnConfigurationPrefix = "CsCode.Editor.Languages.Configurations.";
    /// <summary>
    /// Resource names of CsCode's own built-in snippets (<c>Snippets/Builtin/&lt;language id&gt;.json</c>): for languages whose VS Code extension ships
    /// none (Python's does not). Language stage 1; later stages add their languages' files there.
    /// </summary>
    internal const string OwnSnippetPrefix = "CsCode.Editor.Snippets.Builtin.";
    /// <summary>Resource names of CsCode's own TextMate grammars (<c>Languages/Grammars/*.tmLanguage.json</c>): for languages TextMateSharp.Grammars has none for.</summary>
    internal const string OwnGrammarPrefix = "CsCode.Editor.Languages.Grammars.";

    private static readonly Assembly Asm = typeof(RegistryOptions).Assembly;
    private static readonly Assembly OwnAsm = typeof(BuiltinLanguages).Assembly;
    private static readonly Lazy<BuiltinCatalog> CatalogLazy = new(Build);

    public static BuiltinCatalog Catalog => CatalogLazy.Value;

    public static Stream? Open(string resourceName) =>
        resourceName.StartsWith(OwnConfigurationPrefix, StringComparison.Ordinal) || resourceName.StartsWith(OwnSnippetPrefix, StringComparison.Ordinal)
        || resourceName.StartsWith(OwnGrammarPrefix, StringComparison.Ordinal)
            ? OwnAsm.GetManifestResourceStream(resourceName)
            : Asm.GetManifestResourceStream(resourceName);

    internal static string OwnConfigurationResource(string name) => OwnConfigurationPrefix + name + ".language-configuration.json";

    public static string? ReadText(string resourceName)
    {
        using var s = Open(resourceName);
        if (s is null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Resource name for a path relative to a package, e.g. ("csharp", "./syntaxes/csharp.tmLanguage.json").</summary>
    internal static string ResourceName(string package, string relativePath) =>
        ResourcePrefix + package.ToLowerInvariant() + "." + relativePath.TrimStart('.', '/').Replace('/', '.');

    private sealed class Mutable(string id)
    {
        public string Id { get; } = id;
        public List<string> Aliases { get; } = [];
        public List<string> Extensions { get; } = [];
        public List<string> Filenames { get; } = [];
        public List<string> Patterns { get; } = [];
        public List<string> MimeTypes { get; } = [];
        public string? FirstLine { get; set; }
        public string? ConfigResource { get; set; }

        public static void AddAll(List<string> target, IEnumerable<string> items)
        {
            foreach (var i in items)
                if (!target.Contains(i, StringComparer.OrdinalIgnoreCase)) target.Add(i);
        }
    }

    // Languages VS Code has no built-in grammar for: detection, a language configuration of our own (Config = the file name under
    // Languages/Configurations) and whatever built-in or user language server serves the id. No highlighting yet (docs/LANGUAGES.md).
    private static readonly (string Id, string Name, string[] Ext, string[] Files, string? Config)[] Supplements =
    [
        ("plaintext", "Plain Text", [".txt"], [], null),
        ("toml", "TOML", [".toml"], ["Cargo.lock", "Pipfile", "poetry.lock"], "toml"),
        ("cmake", "CMake", [".cmake"], ["CMakeLists.txt"], "cmake"),
        ("kotlin", "Kotlin", [".kt", ".kts"], [], "kotlin"),
        ("scala", "Scala", [".scala", ".sc", ".sbt"], [], "scala"),
        ("zig", "Zig", [".zig", ".zon"], [], "zig"),
        ("protobuf", "Protocol Buffers", [".proto"], [], "protobuf"),
        ("graphql", "GraphQL", [".graphql", ".gql", ".graphqls"], [], "graphql"),
        ("haskell", "Haskell", [".hs", ".lhs"], [], "haskell"),
        ("elixir", "Elixir", [".ex", ".exs"], [], "elixir"),
        ("erlang", "Erlang", [".erl", ".hrl"], ["rebar.config"], "erlang"),
        ("ocaml", "OCaml", [".ml", ".mli"], [], "ocaml"),
        ("vue", "Vue", [".vue"], [], "vue"),
        ("svelte", "Svelte", [".svelte"], [], "svelte"),
        // terraform-ls tells variable files apart by language id, as HashiCorp's VS Code extension does
        ("terraform", "Terraform", [".tf"], [], "terraform"),
        ("terraform-vars", "Terraform Variables", [".tfvars"], [], "terraform"),
        ("nix", "Nix", [".nix"], [], "nix"),
        ("restructuredtext", "reStructuredText", [".rst"], [], "restructuredtext"),
        // two comment syntaxes: ';' (NASM/MASM, .asm) is a statement separator in GNU as (.s), whose comment character is '#'
        ("assembly", "Assembly", [".asm", ".nasm"], [], "assembly"),
        ("gas", "GNU Assembler", [".s"], [], "gas"),
        ("csv", "CSV", [".csv"], [], "csv"),
        ("jsonl", "JSON Lines", [".jsonl", ".ndjson"], [], "jsonl"),
        // the search editor's results (VS Code's built-in search-result language; its grammar is CsCode's own, below)
        ("search-result", "Search Result", [".code-search"], [], null),
    ];

    // CsCode's own TextMate grammars (Languages/Grammars, embedded in this assembly) for languages TextMateSharp.Grammars embeds none for.
    private static readonly (string LanguageId, string ScopeName, string File)[] OwnGrammars =
    [
        ("search-result", "text.searchResult", "search-result.tmLanguage.json"),
    ];

    // Extensions the bundled (older) manifests omit.
    private static readonly (string Id, string[] Ext)[] ExtraExtensions =
    [
        ("typescript", [".mts", ".cts"]),
        ("javascript", [".mjs", ".cjs"]),
        ("shellscript", [".fish"]),
        ("json", [".json5"]),
        ("jsonc", [".code-workspace", ".code-snippets"]),
    ];

    private static BuiltinCatalog Build()
    {
        var langs = new Dictionary<string, Mutable>(StringComparer.Ordinal);
        var grammars = new List<BuiltinGrammar>();
        var snippets = new List<BuiltinSnippetFile>();

        foreach (var name in Asm.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".package.json", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            var package = name[ResourcePrefix.Length..^".package.json".Length];
            using var stream = Asm.GetManifestResourceStream(name)!;
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("contributes", out var contributes)) continue;

            if (contributes.TryGetProperty("languages", out var languages))
                foreach (var l in languages.EnumerateArray())
                {
                    var id = l.GetProperty("id").GetString()!;
                    if (!langs.TryGetValue(id, out var m)) langs[id] = m = new Mutable(id);
                    Mutable.AddAll(m.Aliases, Strings(l, "aliases"));
                    Mutable.AddAll(m.Extensions, Strings(l, "extensions"));
                    Mutable.AddAll(m.Filenames, Strings(l, "filenames"));
                    Mutable.AddAll(m.Patterns, Strings(l, "filenamePatterns"));
                    Mutable.AddAll(m.MimeTypes, Strings(l, "mimetypes"));
                    if (l.TryGetProperty("firstLine", out var fl) && fl.ValueKind == JsonValueKind.String) m.FirstLine = fl.GetString();
                    if (l.TryGetProperty("configuration", out var cfg) && cfg.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(cfg.GetString()))
                    {
                        var res = ResourceName(package, cfg.GetString()!);
                        if (Asm.GetManifestResourceInfo(res) is not null) m.ConfigResource ??= res;
                    }
                }

            if (contributes.TryGetProperty("grammars", out var gs))
                foreach (var g in gs.EnumerateArray())
                {
                    var path = g.GetProperty("path").GetString();
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var res = ResourceName(package, path);
                    if (Asm.GetManifestResourceInfo(res) is null) continue;
                    Dictionary<string, string>? embedded = null;
                    if (g.TryGetProperty("embeddedLanguages", out var e) && e.ValueKind == JsonValueKind.Object)
                        embedded = e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!);
                    grammars.Add(new BuiltinGrammar(g.TryGetProperty("language", out var gl) ? gl.GetString() : null, g.GetProperty("scopeName").GetString()!, res, embedded));
                }

            if (contributes.TryGetProperty("snippets", out var sn))
                foreach (var s in sn.EnumerateArray())
                {
                    if (!s.TryGetProperty("language", out var lang) || !s.TryGetProperty("path", out var spath)) continue;
                    var res = ResourceName(package, spath.GetString()!);
                    if (Asm.GetManifestResourceInfo(res) is not null) snippets.Add(new BuiltinSnippetFile(lang.GetString()!, res));
                }
        }

        foreach (var (languageId, scopeName, file) in OwnGrammars)
            if (OwnAsm.GetManifestResourceInfo(OwnGrammarPrefix + file) is not null && grammars.All(g => g.ScopeName != scopeName))
                grammars.Add(new BuiltinGrammar(languageId, scopeName, OwnGrammarPrefix + file, null));

        foreach (var res in OwnAsm.GetManifestResourceNames().Where(n => n.StartsWith(OwnSnippetPrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            snippets.Add(new BuiltinSnippetFile(res[OwnSnippetPrefix.Length..^".json".Length], res));

        foreach (var (id, name, ext, files, config) in Supplements)
        {
            if (!langs.TryGetValue(id, out var m)) langs[id] = m = new Mutable(id);
            Mutable.AddAll(m.Aliases, [name]);
            Mutable.AddAll(m.Extensions, ext);
            Mutable.AddAll(m.Filenames, files);
            if (config is not null && OwnAsm.GetManifestResourceInfo(OwnConfigurationResource(config)) is not null)
                m.ConfigResource ??= OwnConfigurationResource(config); // a VS Code configuration, should an upgrade bring one, wins
        }
        foreach (var (id, ext) in ExtraExtensions)
            if (langs.TryGetValue(id, out var m)) Mutable.AddAll(m.Extensions, ext);
        if (langs.TryGetValue("plaintext", out var plain)) Mutable.AddAll(plain.Aliases, ["text"]);

        var result = langs.Values
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => new LanguageContribution(
                m.Id,
                m.Aliases,
                m.Extensions,
                m.Filenames,
                m.Patterns,
                m.FirstLine,
                m.ConfigResource is null ? null : BuiltinPathPrefix + m.ConfigResource,
                m.MimeTypes))
            .ToArray();
        return new BuiltinCatalog(result, grammars, snippets);
    }

    private static IEnumerable<string> Strings(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
            : [];
}
