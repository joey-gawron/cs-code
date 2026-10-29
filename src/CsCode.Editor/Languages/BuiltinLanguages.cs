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

internal sealed record BuiltinCatalog(IReadOnlyList<LanguageContribution> Languages, IReadOnlyList<BuiltinGrammar> Grammars)
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
/// languages VS Code ships no grammar for (Kotlin, Zig, TOML, …) so detection still yields the id language servers expect.
/// </summary>
internal static class BuiltinLanguages
{
    internal const string ResourcePrefix = "TextMateSharp.Grammars.Resources.Grammars.";
    internal const string BuiltinPathPrefix = "builtin:";

    private static readonly Assembly Asm = typeof(RegistryOptions).Assembly;
    private static readonly Lazy<BuiltinCatalog> CatalogLazy = new(Build);

    public static BuiltinCatalog Catalog => CatalogLazy.Value;

    public static Stream? Open(string resourceName) => Asm.GetManifestResourceStream(resourceName);

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

    // Languages VS Code has no built-in grammar for. Detection only (plus whatever language server the user configures).
    private static readonly (string Id, string Name, string[] Ext, string[] Files)[] Supplements =
    [
        ("plaintext", "Plain Text", [".txt"], []),
        ("toml", "TOML", [".toml"], ["Cargo.lock", "Pipfile", "poetry.lock"]),
        ("cmake", "CMake", [".cmake"], ["CMakeLists.txt"]),
        ("kotlin", "Kotlin", [".kt", ".kts"], []),
        ("scala", "Scala", [".scala", ".sc", ".sbt"], []),
        ("zig", "Zig", [".zig"], []),
        ("protobuf", "Protocol Buffers", [".proto"], []),
        ("graphql", "GraphQL", [".graphql", ".gql"], []),
        ("haskell", "Haskell", [".hs", ".lhs"], []),
        ("elixir", "Elixir", [".ex", ".exs"], []),
        ("erlang", "Erlang", [".erl", ".hrl"], []),
        ("ocaml", "OCaml", [".ml", ".mli"], []),
        ("vue", "Vue", [".vue"], []),
        ("svelte", "Svelte", [".svelte"], []),
        ("terraform", "Terraform", [".tf", ".tfvars"], []),
        ("nix", "Nix", [".nix"], []),
        ("restructuredtext", "reStructuredText", [".rst"], []),
        ("assembly", "Assembly", [".asm", ".s"], []),
        ("csv", "CSV", [".csv"], []),
        ("jsonl", "JSON Lines", [".jsonl", ".ndjson"], []),
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
        }

        foreach (var (id, name, ext, files) in Supplements)
        {
            if (!langs.TryGetValue(id, out var m)) langs[id] = m = new Mutable(id);
            Mutable.AddAll(m.Aliases, [name]);
            Mutable.AddAll(m.Extensions, ext);
            Mutable.AddAll(m.Filenames, files);
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
        return new BuiltinCatalog(result, grammars);
    }

    private static IEnumerable<string> Strings(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
            : [];
}
