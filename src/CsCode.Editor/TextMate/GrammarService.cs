using CsCode.Core.Contributions;
using CsCode.Editor.Languages;
using Microsoft.Extensions.Logging;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace CsCode.Editor.TextMate;

/// <summary>
/// Owns the TextMateSharp <see cref="Registry"/> and the compiled grammars. <b>Every member must be called on the editor worker
/// thread</b>: the registry and its rule caches are not thread-safe, and confining them to the one worker thread is what makes that safe
/// without a single lock — and keeps grammar compilation (Oniguruma) off the UI thread.
/// Grammars come from the user's <see cref="GrammarContribution"/>s first, then from the VS Code built-ins embedded in TextMateSharp.Grammars.
/// </summary>
internal sealed class GrammarService
{
    private readonly EditorServices _svc;
    private readonly ILogger _log;
    private Registry? _registry;
    private readonly Dictionary<string, IGrammar?> _byScope = new(StringComparer.Ordinal);

    public GrammarService(EditorServices svc)
    {
        _svc = svc;
        _log = svc.LoggerFactory.CreateLogger<GrammarService>();
    }

    /// <summary>The compiled grammar for a language id, or null when there is none (plain text, unknown language, grammar failed to load).</summary>
    public IGrammar? GrammarForLanguage(string languageId)
    {
        var scope = ScopeForLanguage(languageId);
        return scope is null ? null : GrammarForScope(scope);
    }

    public string? ScopeForLanguage(string languageId)
    {
        // the user's grammar wins over the built-in one for the same language
        foreach (var c in _svc.Contributions.Of<GrammarContribution>().Items.Reverse())
            if (c.Value.LanguageId == languageId) return c.Value.ScopeName;
        return BuiltinLanguages.Catalog.ScopeForLanguage(languageId);
    }

    public IGrammar? GrammarForScope(string scope)
    {
        if (_byScope.TryGetValue(scope, out var cached)) return cached;
        IGrammar? grammar = null;
        try
        {
            _registry ??= new Registry(new Options(this));
            grammar = _registry.LoadGrammar(scope);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not load TextMate grammar {Scope}", scope);
        }
        _byScope[scope] = grammar;
        return grammar;
    }

    /// <summary>Drops every compiled grammar (grammar contributions changed); the next lookup recompiles from the current sources.</summary>
    public void Reset()
    {
        _registry = null;
        _byScope.Clear();
    }

    private sealed class Options(GrammarService owner) : IRegistryOptions
    {
        public IRawGrammar GetGrammar(string scopeName)
        {
            foreach (var c in owner._svc.Contributions.Of<GrammarContribution>().Items.Reverse())
            {
                if (c.Value.ScopeName != scopeName) continue;
                // A small grammar file read on the worker thread: this is the IO the worker is allowed (never the UI thread).
                using var fs = new FileStream(c.Value.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                return GrammarReader.ReadGrammarSync(reader);
            }
            if (BuiltinLanguages.Catalog.GrammarForScope(scopeName) is { } builtin && BuiltinLanguages.Open(builtin.ResourceName) is { } stream)
            {
                using (stream)
                using (var reader = new StreamReader(stream)) return GrammarReader.ReadGrammarSync(reader);
            }
            return null!;
        }

        public ICollection<string> GetInjections(string scopeName) => null!;
        public IRawTheme GetTheme(string scopeName) => null!;
        // The registry insists on a theme; colours are resolved by TokenColorResolver, so this one is deliberately empty.
        public IRawTheme GetDefaultTheme() => EmptyTheme.Instance;
    }

    private sealed class EmptyTheme : IRawTheme
    {
        public static readonly EmptyTheme Instance = new();
        public string GetName() => "empty";
        public string GetInclude() => null!;
        public ICollection<IRawThemeSetting> GetSettings() => [];
        public ICollection<IRawThemeSetting> GetTokenColors() => [];
        public ICollection<KeyValuePair<string, object>> GetGuiColors() => [];
    }
}
