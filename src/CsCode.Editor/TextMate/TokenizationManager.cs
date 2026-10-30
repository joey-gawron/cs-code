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
        TreeSitter = new TreeSitterService(services, Interner, HasTextMateGrammar) { LaneReplaced = OnTreeSitterReset };
        _subscriptions.Add(TreeSitter);
        _subscriptions.Add(services.Contributions.Of<GrammarContribution>().Changed.Subscribe(_ => OnGrammarsChanged()));
        // editor.syntaxEngine (global or "[language]", and the old editor.experimental.treeSitter alias) switches documents between the engines live
        _subscriptions.Add(services.Config.RegisterProperties([TreeSitterService.Property]));
        _subscriptions.Add(services.Config.Changed.Subscribe(e =>
        {
            if (e.Affects(TreeSitterService.SettingKey) || e.Affects(TreeSitterService.LegacySettingKey) || e.AffectedKeys.Any(k => k.StartsWith('[')))
                OnEngineSettingChanged();
        }));
        // Expand Selection, folding and the outline from the tree, for documents tree-sitter colours (a language server's providers rank first)
        Structure = new TreeSitterLanguageFeatures(this, services.Features);
        _subscriptions.Add(services.Features.Register(Core.Text.DocumentSelector.All, Structure, "tree-sitter"));
        if (services.Commands is { } commands && !commands.Has(ClearQuarantineCommand))
            _subscriptions.Add(commands.Register(ClearQuarantineCommand, _ =>
            {
                TreeSitter.ClearQuarantine();
                return ValueTask.FromResult<object?>(null);
            }, new Core.Platform.CommandMetadata(ClearQuarantineCommand, "Clear Quarantined Tree-sitter Grammars", "Editor")));
        if (services.Memory is { } monitor) _monitorRegistration = monitor.Register(this);
    }

    /// <summary>Tries the grammars the crash/hang isolation quarantined again (docs/TREE-SITTER.md, "Isolation").</summary>
    public const string ClearQuarantineCommand = "editor.treeSitter.clearQuarantine";

    internal EditorServices Services { get; }
    internal EditorWorker Worker { get; }
    internal GrammarService Grammars { get; }
    public ScopeInterner Interner { get; }
    internal ILogger Log { get; }
    internal IUiThread Ui { get; }

    /// <summary>The tree-sitter engine's shared state (<c>editor.syntaxEngine</c>, its syntax lane, the isolation).</summary>
    internal TreeSitterService TreeSitter { get; }

    /// <summary>The structural features from the tree (registered as providers) and the modal layer's syntax text objects.</summary>
    internal TreeSitterLanguageFeatures Structure { get; }

    public int SessionCount
    {
        get
        {
            lock (_gate) return _sessions.Count;
        }
    }

    /// <summary>True when a grammar exists for the language (TextMate, or tree-sitter when it colours the language): otherwise there is nothing to tokenise with.</summary>
    internal bool HasGrammar(string languageId) => HasTextMateGrammar(languageId) || TreeSitter.IsEnabledFor(languageId);

    /// <summary>True when TextMate can colour the language (a user contribution or a built-in grammar): what <c>editor.syntaxEngine</c> = <c>auto</c> asks.</summary>
    internal bool HasTextMateGrammar(string languageId) =>
        Services.Contributions.Of<GrammarContribution>().Items.Any(c => c.Value.LanguageId == languageId)
        || Languages.BuiltinLanguages.Catalog.ScopeForLanguage(languageId) is not null;

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

    /// <summary>The session of <paramref name="document"/> (the editor's document, or another with its URI), or null when it has none.</summary>
    internal TokenizationSession? SessionFor(Core.Text.ITextDocument document)
    {
        lock (_gate)
        {
            foreach (var s in _sessions)
                if (ReferenceEquals(s.Document, document)) return s;
            foreach (var s in _sessions)
                if (s.Document.Uri == document.Uri) return s;
            return null;
        }
    }

    private void OnEngineSettingChanged()
    {
        TokenizationSession[] all;
        lock (_gate) all = _sessions.ToArray();
        Ui.Post(() =>
        {
            foreach (var s in all)
                if (s.TreeSitterWantedChanged()) s.RestartFromManager();
            _availability.Fire(true); // views without a session (no grammar before) may have one now
        });
    }

    /// <summary>
    /// Tree-sitter moved to a new lane (a grammar hung) or the quarantine was cleared: every document on tree-sitter restarts its engine (an
    /// engine of the old lane is dropped, never touched), and every document whose engine choice changed switches.
    /// </summary>
    private void OnTreeSitterReset()
    {
        TokenizationSession[] all;
        lock (_gate) all = _sessions.ToArray();
        Ui.Post(() =>
        {
            foreach (var s in all)
                if (s.TreeSitterWanted || s.TreeSitterWantedChanged()) s.RestartFromManager();
            _availability.Fire(true);
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
            _availability.Fire(true);
        });
    }

    private readonly Emitter<bool> _availability = new();

    /// <summary>
    /// Fires on the UI thread after something may have given a language a grammar it had none of (the engine setting, a user grammar, a cleared
    /// quarantine): a view without a tokenisation session asks again (<c>CodeEditorControl</c>).
    /// </summary>
    internal IEventSource<bool> GrammarAvailabilityChanged => _availability;

    private readonly Emitter<EditorDocument> _treeArrived = new();

    /// <summary>
    /// Fires on the UI thread when a document's syntax tree arrived after a structural request (folding, the outline, Expand Selection) gave up
    /// waiting for it, e.g. the first parse of a big file: those features ask again instead of keeping their fallback until the next edit.
    /// </summary>
    internal IEventSource<EditorDocument> SyntaxTreeArrived => _treeArrived;

    /// <summary>Any thread (the syntax lane): see <see cref="SyntaxTreeArrived"/>.</summary>
    internal void NotifySyntaxTreeArrived(EditorDocument document) => Ui.Post(() =>
    {
        if (!_disposed) _treeArrived.Fire(document);
    });

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
        // the sessions first: their tree-sitter engines are freed by jobs on the syntax lane, which the service's own disposal (in the
        // subscriptions) stops after the jobs already queued
        TokenizationSession[] all;
        lock (_gate) all = _sessions.ToArray();
        foreach (var s in all) s.Dispose();
        _subscriptions.Dispose();
    }
}
