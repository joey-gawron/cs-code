using System.Runtime.InteropServices;
using CsCode.Editor.TreeSitter.Highlighting;
using CsCode.Editor.TreeSitter.Native;
using CsCode.Editor.TreeSitter.Queries;

namespace CsCode.Editor.TreeSitter.Grammars;

/// <summary>
/// Everything needed to highlight one grammar: the language, its compiled highlight and injection queries, and the capture → scope table.
/// Immutable and process-wide (tree-sitter languages and queries are thread-safe once built), never disposed: a grammar is loaded once.
/// </summary>
public sealed class LanguageBundle
{
    /// <summary>Highlight capture that is not painted.</summary>
    internal const int SkipKey = -1;

    /// <summary>Highlight capture that resets to the layer base (<c>@none</c>).</summary>
    internal const int NoneKey = -2;

    private readonly string?[] _symbolNames;

    internal LanguageBundle(string name, TsLanguage language, TsQuery? highlights, TsQuery? injections, string rootScope, string suffix,
        IReadOnlyList<string> queryFiles, IReadOnlyList<string> diagnostics)
    {
        Name = name;
        Language = language;
        Highlights = highlights;
        Injections = injections;
        RootScope = rootScope;
        RootScopeKey = ScopeNames.KeyOf(rootScope);
        ScopeSuffix = suffix;
        QueryFiles = queryFiles;
        Diagnostics = diagnostics;
        _symbolNames = new string?[Math.Max(1, TsApi.ts_language_symbol_count(language.Handle))];
        var names = highlights?.CaptureNames ?? [];
        CaptureKeys = new int[names.Count];
        var scopes = new string?[names.Count];
        CaptureScopeNames = scopes;
        for (var i = 0; i < names.Count; i++)
        {
            var scope = CaptureScopes.ScopeFor(names[i], name, suffix);
            scopes[i] = scope;
            CaptureKeys[i] = scope switch
            {
                null => SkipKey, // unknown capture name: left unstyled (listed by the coverage test)
                CaptureScopes.Skip => SkipKey,
                CaptureScopes.None => NoneKey,
                _ => ScopeNames.KeyOf(scope),
            };
        }
    }

    public string Name { get; }
    public TsLanguage Language { get; }
    public TsQuery? Highlights { get; }
    public TsQuery? Injections { get; }
    public string RootScope { get; }
    internal int RootScopeKey { get; }
    public string ScopeSuffix { get; }
    public IReadOnlyList<string> QueryFiles { get; }

    /// <summary>Load-time notes: query compile errors that were worked around, unsupported predicates, unknown capture names.</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>Scope per highlight capture index (<see cref="SkipKey"/>, <see cref="NoneKey"/> or a <see cref="ScopeNames"/> key).</summary>
    internal int[] CaptureKeys { get; }

    /// <summary>The TextMate scope each highlight capture maps to (for tests and the evaluation document).</summary>
    public IReadOnlyList<string?> CaptureScopeNames { get; }

    /// <summary>Node type name for a symbol id, cached (predicates compare node types without allocating).</summary>
    internal unsafe string SymbolName(ushort symbol, Native.TSNode node)
    {
        if (symbol < _symbolNames.Length && _symbolNames[symbol] is { } cached) return cached;
        var name = TsApi.Utf8(TsApi.ts_node_type(node)) ?? string.Empty;
        if (symbol < _symbolNames.Length) _symbolNames[symbol] = name;
        return name;
    }
}

/// <summary>
/// Loads grammars by name through a <see cref="GrammarLocator"/> and caches them (bundles process-wide, failures per registry so a fixed
/// installation is picked up by a new registry). Also owns one parser per injected language for injection parses. Confined to the editor
/// worker (the injection parsers are not thread-safe); bundles it returns may be used anywhere.
/// </summary>
public sealed class GrammarRegistry : IDisposable
{
    private static readonly object SharedGate = new();
    private static readonly Dictionary<string, LanguageBundle> Shared = new(StringComparer.Ordinal);

    private readonly GrammarLocator _locator;
    private readonly Func<string, string?> _rootScopeFor;
    private readonly Dictionary<string, (LanguageBundle? Bundle, string? Error)> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IntPtr> _injectionParsers = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <param name="rootScopeFor">The TextMate root scope for a grammar name (<c>c_sharp</c> → <c>source.cs</c>), so tree-sitter tokens carry the same root as TextMate ones; null = <c>source.&lt;name&gt;</c>.</param>
    public GrammarRegistry(GrammarLocator locator, Func<string, string?> rootScopeFor)
    {
        _locator = locator;
        _rootScopeFor = rootScopeFor;
    }

    public GrammarLocator Locator => _locator;

    /// <summary>The bundle for grammar <paramref name="grammar"/>, or null with a reason (no runtime, not installed, ABI mismatch, query does not compile).</summary>
    public LanguageBundle? Get(string grammar, out string? error)
    {
        if (_byName.TryGetValue(grammar, out var known))
        {
            error = known.Error;
            return known.Bundle;
        }
        var bundle = LoadUncached(grammar, out error);
        _byName[grammar] = (bundle, error);
        return bundle;
    }

    private LanguageBundle? LoadUncached(string grammar, out string? error)
    {
        error = null;
        if (!TsRuntime.Load())
        {
            error = TsRuntime.FailureReason;
            return null;
        }
        var files = _locator.Find(grammar);
        if (files is null)
        {
            error = $"no compiled tree-sitter grammar '{grammar}' was found";
            return null;
        }
        var root = _rootScopeFor(grammar) ?? "source." + grammar.Replace('_', '-');
        var key = files.ParserPath + "|" + grammar + "|" + root;
        lock (SharedGate)
        {
            if (Shared.TryGetValue(key, out var shared)) return shared;
        }
        var language = TsLanguage.Load(grammar, files.ParserPath, out error);
        if (language is null) return null;

        var diagnostics = new List<string>();
        var highlightSource = QueryLoader.Load(_locator, grammar, "highlights", out var highlightFiles);
        TsQuery? highlights = null;
        if (highlightSource is not null)
        {
            highlights = TsQuery.Compile(language, highlightSource, out var queryError);
            if (highlights is null)
            {
                error = $"{grammar}: highlights query does not compile ({queryError}; {string.Join(", ", highlightFiles)})";
                return null;
            }
            diagnostics.AddRange(highlights.Warnings.Distinct().Select(w => "highlights: " + w));
        }
        else
        {
            error = $"{grammar}: no highlights.scm was found";
            return null;
        }
        TsQuery? injections = null;
        var injectionSource = QueryLoader.Load(_locator, grammar, "injections", out var injectionFiles);
        if (injectionSource is not null)
        {
            injections = TsQuery.Compile(language, injectionSource, out var injectionError);
            if (injections is null) diagnostics.Add($"injections query does not compile, injections disabled ({injectionError})");
            else diagnostics.AddRange(injections.Warnings.Distinct().Select(w => "injections: " + w));
        }
        var suffix = root[(root.LastIndexOf('.') + 1)..];
        var bundle = new LanguageBundle(grammar, language, highlights, injections, root, suffix,
            highlightFiles.Concat(injectionFiles).ToArray(), diagnostics);
        foreach (var (name, i) in highlights.CaptureNames.Select((n, i) => (n, i)))
            if (bundle.CaptureScopeNames[i] is null) diagnostics.Add($"capture @{name} has no scope mapping (left unstyled)");
        lock (SharedGate)
        {
            if (Shared.TryGetValue(key, out var raced)) return raced;
            Shared[key] = bundle;
        }
        return bundle;
    }

    /// <summary>A parser for injection parses of <paramref name="bundle"/>'s language (one per language per registry; worker only).</summary>
    internal IntPtr InjectionParser(LanguageBundle bundle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_injectionParsers.TryGetValue(bundle.Name, out var p)) return p;
        p = TsApi.ts_parser_new();
        if (TsApi.ts_parser_set_language(p, bundle.Language.Handle) == 0)
        {
            TsApi.ts_parser_delete(p);
            return IntPtr.Zero;
        }
        _injectionParsers[bundle.Name] = p;
        return p;
    }

    /// <summary>Deletes the injection parsers (call on the worker).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var p in _injectionParsers.Values) TsApi.ts_parser_delete(p);
        _injectionParsers.Clear();
    }
}

/// <summary>Names a code block or an <c>@injection.language</c> capture may use, mapped to grammar names (<c>c#</c> → <c>c_sharp</c>).</summary>
public static class InjectionLanguages
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = "c_sharp", ["c#"] = "c_sharp", ["csharp"] = "c_sharp", ["c_sharp"] = "c_sharp",
        ["js"] = "javascript", ["javascript"] = "javascript", ["jsx"] = "javascript", ["mjs"] = "javascript", ["cjs"] = "javascript", ["node"] = "javascript",
        ["ts"] = "typescript", ["typescript"] = "typescript", ["mts"] = "typescript", ["cts"] = "typescript", ["tsx"] = "tsx",
        ["py"] = "python", ["python"] = "python", ["python3"] = "python", ["py3"] = "python",
        ["rs"] = "rust", ["rust"] = "rust",
        ["sh"] = "bash", ["bash"] = "bash", ["shell"] = "bash", ["zsh"] = "bash", ["shellscript"] = "bash", ["console"] = "bash",
        ["c++"] = "cpp", ["cpp"] = "cpp", ["cc"] = "cpp", ["cxx"] = "cpp", ["hpp"] = "cpp", ["c"] = "c", ["h"] = "c",
        ["md"] = "markdown", ["markdown"] = "markdown", ["markdown_inline"] = "markdown_inline",
        ["yml"] = "yaml", ["yaml"] = "yaml", ["toml"] = "toml", ["json"] = "json", ["jsonc"] = "json", ["json5"] = "json",
        ["html"] = "html", ["htm"] = "html", ["xhtml"] = "html", ["css"] = "css", ["scss"] = "scss",
        ["go"] = "go", ["golang"] = "go", ["lua"] = "lua", ["sql"] = "sql", ["diff"] = "diff", ["patch"] = "diff",
        ["dockerfile"] = "dockerfile", ["docker"] = "dockerfile", ["java"] = "java", ["kotlin"] = "kotlin", ["kt"] = "kotlin",
        ["regex"] = "regex", ["jsdoc"] = "jsdoc", ["nix"] = "nix", ["xml"] = "xml", ["make"] = "make", ["makefile"] = "make",
    };

    /// <summary>Injection targets that are deliberately not parsed: Neovim's "comment" grammar (TODO/FIXME tags inside every comment) costs a parse per comment for a highlight VS Code does not have.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal) { "comment", "luap", "printf" };

    /// <summary>The grammar name for <paramref name="name"/>, or null when it is ignored or empty.</summary>
    public static string? Resolve(string name)
    {
        var n = name.Trim();
        if (n.Length == 0) return null;
        var grammar = Aliases.TryGetValue(n, out var g) ? g : n.ToLowerInvariant();
        return Ignored.Contains(grammar) ? null : grammar;
    }
}
