using CsCode.Core.Contributions;
using CsCode.Core.Diagnostics;
using CsCode.Core.Lifecycle;
using CsCode.Editor.Documents;
using CsCode.Editor.Threading;
using CsCode.Editor.TreeSitter;
using Microsoft.Extensions.Logging;

namespace CsCode.Editor.TextMate;

/// <summary>
/// Owns what all tokenisation sessions share: the editor worker, the grammar service (confined to that worker), the scope interner,
/// and the <see cref="IMemoryTrimmable"/> face of the whole TextMate subsystem — token arrays and checkpoints are re-creatable, so under
/// memory pressure they are released (outside the visible window first, everything at the aggressive level) and rebuilt on demand.
/// </summary>
public sealed class TokenizationManager : IMemoryTrimmable, IDisposable
{
    private readonly object _gate = new();
    private readonly List<TokenizationSession> _sessions = new();
    private readonly DisposableStore _subscriptions = new();
    private IDisposable? _monitorRegistration;
    private bool _disposed;

    internal TokenizationManager(EditorServices services, EditorWorker worker)
    {
        Services = services;
        Worker = worker;
        Grammars = new GrammarService(services);
        Interner = new ScopeInterner();
        Log = services.LoggerFactory.CreateLogger<TokenizationManager>();
        Ui = services.Ui;
        TreeSitter = new TreeSitterService(services, worker, Interner);
        _subscriptions.Add(TreeSitter);
        _subscriptions.Add(services.Contributions.Of<GrammarContribution>().Changed.Subscribe(_ => OnGrammarsChanged()));
        // EXPERIMENTAL: editor.experimental.treeSitter (global or "[language]") switches documents between the engines live
        _subscriptions.Add(services.Config.RegisterProperties([TreeSitterService.Property]));
        _subscriptions.Add(services.Config.Changed.Subscribe(e =>
        {
            if (e.Affects(TreeSitterService.SettingKey) || e.AffectedKeys.Any(k => k.StartsWith('['))) OnEngineSettingChanged();
        }));
        if (services.Memory is { } monitor) _monitorRegistration = monitor.Register(this);
    }

    internal EditorServices Services { get; }
    internal EditorWorker Worker { get; }
    internal GrammarService Grammars { get; }
    public ScopeInterner Interner { get; }
    internal ILogger Log { get; }
    internal IUiThread Ui { get; }

    /// <summary>The EXPERIMENTAL tree-sitter engine's shared state (off unless <c>editor.experimental.treeSitter</c> is set).</summary>
    internal TreeSitterService TreeSitter { get; }

    public int SessionCount
    {
        get
        {
            lock (_gate) return _sessions.Count;
        }
    }

    /// <summary>True when a grammar exists for the language (a user contribution or a built-in): otherwise there is nothing to tokenise with.</summary>
    internal bool HasGrammar(string languageId) =>
        Services.Contributions.Of<GrammarContribution>().Items.Any(c => c.Value.LanguageId == languageId)
        || Languages.BuiltinLanguages.Catalog.ScopeForLanguage(languageId) is not null
        || TreeSitter.IsEnabledFor(languageId);

    internal TokenizationSession CreateSession(EditorDocument document)
    {
        var session = new TokenizationSession(this, document);
        lock (_gate) _sessions.Add(session);
        return session;
    }

    internal void Remove(TokenizationSession session)
    {
        lock (_gate) _sessions.Remove(session);
    }

    private void OnEngineSettingChanged()
    {
        TokenizationSession[] all;
        lock (_gate) all = _sessions.ToArray();
        Ui.Post(() =>
        {
            foreach (var s in all)
                if (s.TreeSitterWantedChanged()) s.RestartFromManager();
        });
    }

    private void OnGrammarsChanged()
    {
        // Grammars (and the registry holding them) are confined to the worker, so the reset is a worker job; sessions restart behind it.
        Worker.Post(WorkPriority.Interactive, "grammar-reset", _ => Grammars.Reset());
        TokenizationSession[] all;
        lock (_gate) all = _sessions.ToArray();
        Ui.Post(() =>
        {
            foreach (var s in all) s.RestartFromManager();
        });
    }

    // ---- IMemoryTrimmable ----

    public string Name => "Editor syntax tokens";

    public long EstimatedBytes
    {
        get
        {
            TokenizationSession[] all;
            lock (_gate) all = _sessions.ToArray();
            // + the tree-sitter syntax trees (native memory; estimated, see SyntaxDocument.EstimatedTreeBytes)
            return all.Sum(s => s.Store.EstimatedBytes + s.TreeSitterBytes) + Interner.EstimatedBytes;
        }
    }

    public long Trim(MemoryTrimLevel level)
    {
        TokenizationSession[] all;
        lock (_gate) all = _sessions.ToArray();
        return all.Sum(s => s.Trim(level));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitorRegistration?.Dispose();
        _subscriptions.Dispose();
        TokenizationSession[] all;
        lock (_gate) all = _sessions.ToArray();
        foreach (var s in all) s.Dispose();
    }
}
