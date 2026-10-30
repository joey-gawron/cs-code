using System.Text.Json;
using CsCode.Core.Platform;
using CsCode.Editor.TextMate;
using CsCode.Editor.Threading;
using CsCode.Editor.TreeSitter.Grammars;
using CsCode.Editor.TreeSitter.Highlighting;
using CsCode.Editor.TreeSitter.Native;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.TreeSitter;

/// <summary>
/// The EXPERIMENTAL tree-sitter engine's shared state, one per <see cref="TokenizationManager"/>: the setting, the grammar registry, and the
/// scope-stack adapter onto the manager's <see cref="ScopeInterner"/>. Off unless <c>editor.experimental.treeSitter</c> is true (globally or
/// in a <c>"[language]"</c> section); TextMate stays the default and the fallback for every language and every failure (no runtime, no
/// grammar, ABI mismatch, query that does not compile) — a failure is logged once per grammar, the document is coloured by TextMate.
///
/// Threading: <see cref="IsEnabledFor"/> reads configuration (any thread). Everything else (<see cref="Registry"/>, <see cref="Stacks"/>,
/// loading) runs on the editor worker, where all tree-sitter objects are confined.
/// </summary>
internal sealed class TreeSitterService : IDisposable
{
    public const string SettingKey = "editor.experimental.treeSitter";

    private readonly EditorServices _services;
    private readonly EditorWorker _worker;
    private readonly ILogger _log;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private GrammarRegistry? _registry;
    private InternerScopeStacks? _stacks;
    private bool _disposed;

    public TreeSitterService(EditorServices services, EditorWorker worker, ScopeInterner interner)
    {
        _services = services;
        _worker = worker;
        Interner = interner;
        _log = services.LoggerFactory.CreateLogger<TreeSitterService>();
    }

    public ScopeInterner Interner { get; }

    /// <summary>The setting's declaration (registered by the manager so the Settings panel lists it).</summary>
    public static ConfigurationProperty Property => new(SettingKey, JsonSerializer.SerializeToElement(false),
        "EXPERIMENTAL: colour C#, TypeScript/TSX, JavaScript, Rust, Python, Markdown, C/C++, HTML and CSS with tree-sitter instead of TextMate " +
        "(needs libtree-sitter 0.25+ and compiled grammars on this machine; see docs/TREE-SITTER-EVALUATION.md). Can be set per language.");

    /// <summary>True when the setting asks for tree-sitter for <paramref name="languageId"/> and the prototype covers that language.</summary>
    public bool IsEnabledFor(string languageId) =>
        TreeSitterLanguages.GrammarFor(languageId) is not null && _services.Config.GetOr(SettingKey, languageId, false);

    /// <summary>Tests: where grammars are looked up (set before the first document uses tree-sitter). Default: the user's folder, the environment, the Nix store.</summary>
    internal GrammarLocator? LocatorOverride { get; set; }

    /// <summary>Worker only. The registry (created, and the runtime loaded, on first use).</summary>
    public GrammarRegistry Registry
    {
        get
        {
            if (_registry is not null) return _registry;
            var userDir = _services.AppPaths?.UserDataDir is { } d ? Path.Combine(d, "tree-sitter") : null;
            TsRuntime.Load(userDir is null ? null : [userDir, Path.Combine(userDir, "lib")]);
            var locator = LocatorOverride ?? new GrammarLocator(GrammarLocator.DefaultRoots(userDir is null ? null : Path.Combine(userDir, "grammars")));
            return _registry = new GrammarRegistry(locator, TreeSitterLanguages.RootScopeFor);
        }
    }

    /// <summary>Worker only. Interns tree-sitter scope stacks into the same ids TextMate tokens use.</summary>
    public IScopeStacks Stacks => _stacks ??= new InternerScopeStacks(Interner);

    /// <summary>Worker only. The grammar bundle for a document language, or null (logged once) — the caller falls back to TextMate.</summary>
    public LanguageBundle? BundleFor(string languageId)
    {
        if (TreeSitterLanguages.GrammarFor(languageId) is not { } grammar) return null;
        var bundle = Registry.Get(grammar, out var error);
        if (bundle is null)
        {
            if (_reported.Add(grammar)) _log.LogWarning("tree-sitter is enabled but not usable for {Language}: {Reason}. Using TextMate.", languageId, error);
        }
        else if (bundle.Diagnostics.Count > 0 && _reported.Add(grammar))
            _log.LogInformation("tree-sitter grammar {Grammar}: {Notes}", grammar, string.Join("; ", bundle.Diagnostics));
        return bundle;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // the injection parsers belong to the worker thread
        var registry = _registry;
        if (registry is not null) _worker.Post(WorkPriority.Interactive, null, _ => registry.Dispose());
    }

    /// <summary>
    /// <see cref="IScopeStacks"/> over the editor's <see cref="ScopeInterner"/>: tree-sitter stacks become ordinary interned scope stacks, so the
    /// colorizer, the theme resolver, <c>KindAt</c> (auto-closing) and the bracket feature treat them exactly like TextMate's. Worker only;
    /// (parent, scope) → id is cached so the hot path is one dictionary lookup.
    /// </summary>
    private sealed class InternerScopeStacks(ScopeInterner interner) : IScopeStacks
    {
        private readonly Dictionary<long, int> _push = new();
        private readonly Dictionary<int, int> _roots = new();

        public int Root(int scopeKey)
        {
            if (_roots.TryGetValue(scopeKey, out var id)) return id;
            id = interner.Intern([ScopeNames.NameOf(scopeKey)]);
            _roots[scopeKey] = id;
            return id;
        }

        public int Push(int parentId, int scopeKey)
        {
            var key = ((long)parentId << 32) | (uint)scopeKey;
            if (_push.TryGetValue(key, out var id)) return id;
            var parent = interner.Scopes(parentId);
            var stack = new string[parent.Length + 1];
            parent.CopyTo(stack, 0);
            stack[^1] = ScopeNames.NameOf(scopeKey);
            id = interner.Intern(stack);
            _push[key] = id;
            return id;
        }
    }
}
