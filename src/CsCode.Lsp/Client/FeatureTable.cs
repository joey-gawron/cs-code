using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;
using CsCode.Lsp.Features;
using P = CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Client;

/// <summary>The provider interfaces CsCode registers on behalf of a server (one provider object per kind per session).</summary>
internal enum ProviderKind
{
    Completion, Hover, SignatureHelp, Definition, References, DocumentHighlight, DocumentSymbol, WorkspaceSymbol, CodeAction, CodeLens,
    DocumentColor, Formatting, Rename, FoldingRange, SelectionRange, SemanticTokens, SemanticTokensRange, InlayHint, LinkedEditing,
    CallHierarchy, TypeHierarchy,
}

/// <summary>One static capability or dynamic registration contributing to a provider kind.</summary>
internal sealed record FeatureEntry(string Id, string Method, DocumentSelector? Selector, JsonElement? Options)
{
    public T? Opt<T>() where T : class => Options is { ValueKind: JsonValueKind.Object } o ? P.LspJson.Deserialize<T>(o) : null;
}

/// <summary>
/// Turns server capabilities (static at initialize, dynamic via client/registerCapability) into registry registrations. A provider
/// kind exists in the <see cref="ILanguageFeatureRegistry"/> exactly while at least one entry backs it; when the aggregate options
/// change (trigger characters, legend, ...) the provider is rebuilt and re-registered so consumers see ProvidersChanged.
/// Per-registration document selectors are honoured by <see cref="Allows"/> (the registry registration itself uses the definition's selector).
/// </summary>
internal sealed class FeatureTable(LanguageClient client)
{
    private sealed class State
    {
        public FeatureEntry[] Entries = [];
        public IDisposable? Token;
    }

    private readonly object _gate = new();
    private readonly Dictionary<ProviderKind, State> _states = new();
    private bool _disposed;

    public const string StaticPrefix = "static:";

    public static ProviderKind? KindOf(string method) => method switch
    {
        P.LspMethods.TextDocumentCompletion => ProviderKind.Completion,
        P.LspMethods.TextDocumentHover => ProviderKind.Hover,
        P.LspMethods.TextDocumentSignatureHelp => ProviderKind.SignatureHelp,
        P.LspMethods.TextDocumentDefinition or P.LspMethods.TextDocumentDeclaration or P.LspMethods.TextDocumentTypeDefinition or P.LspMethods.TextDocumentImplementation => ProviderKind.Definition,
        P.LspMethods.TextDocumentReferences => ProviderKind.References,
        P.LspMethods.TextDocumentDocumentHighlight => ProviderKind.DocumentHighlight,
        P.LspMethods.TextDocumentDocumentSymbol => ProviderKind.DocumentSymbol,
        P.LspMethods.WorkspaceSymbol => ProviderKind.WorkspaceSymbol,
        P.LspMethods.TextDocumentCodeAction => ProviderKind.CodeAction,
        P.LspMethods.TextDocumentCodeLens => ProviderKind.CodeLens,
        P.LspMethods.TextDocumentDocumentColor => ProviderKind.DocumentColor,
        P.LspMethods.TextDocumentFormatting or P.LspMethods.TextDocumentRangeFormatting or P.LspMethods.TextDocumentOnTypeFormatting => ProviderKind.Formatting,
        P.LspMethods.TextDocumentRename => ProviderKind.Rename,
        P.LspMethods.TextDocumentFoldingRange => ProviderKind.FoldingRange,
        P.LspMethods.TextDocumentSelectionRange => ProviderKind.SelectionRange,
        "textDocument/semanticTokens" or P.LspMethods.TextDocumentSemanticTokensFull or P.LspMethods.TextDocumentSemanticTokensFullDelta => ProviderKind.SemanticTokens,
        P.LspMethods.TextDocumentSemanticTokensRange => ProviderKind.SemanticTokensRange,
        P.LspMethods.TextDocumentInlayHint => ProviderKind.InlayHint,
        P.LspMethods.TextDocumentLinkedEditingRange => ProviderKind.LinkedEditing,
        P.LspMethods.TextDocumentPrepareCallHierarchy => ProviderKind.CallHierarchy,
        P.LspMethods.TextDocumentPrepareTypeHierarchy => ProviderKind.TypeHierarchy,
        _ => null,
    };

    /// <summary>Kinds currently registered in the registry (for tests / diagnostics).</summary>
    public IReadOnlyList<ProviderKind> ActiveKinds
    {
        get { lock (_gate) return _states.Where(kv => kv.Value.Token is not null).Select(kv => kv.Key).ToArray(); }
    }

    public bool Allows(ProviderKind kind, ITextDocument doc)
    {
        FeatureEntry[] entries;
        lock (_gate) entries = _states.TryGetValue(kind, out var s) ? s.Entries : [];
        foreach (var e in entries)
            if (e.Selector is null || e.Selector.Score(doc) > 0) return true;
        return false;
    }

    public IReadOnlyList<FeatureEntry> EntriesOf(ProviderKind kind)
    {
        lock (_gate) return _states.TryGetValue(kind, out var s) ? s.Entries : [];
    }

    public void Add(ProviderKind kind, FeatureEntry entry)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var state = GetState(kind);
            state.Entries = [.. state.Entries.Where(e => !(e.Id == entry.Id && e.Method == entry.Method)), entry];
            Rebuild(kind, state);
        }
    }

    public void Remove(string id, string? method)
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var (kind, state) in _states.ToArray())
            {
                var kept = state.Entries.Where(e => !(e.Id == id && (method is null || e.Method == method))).ToArray();
                if (kept.Length == state.Entries.Length) continue;
                state.Entries = kept;
                Rebuild(kind, state);
            }
        }
    }

    public void Refresh(RefreshKind kind)
    {
        ProviderKind[] kinds = kind switch
        {
            RefreshKind.SemanticTokens => [ProviderKind.SemanticTokens, ProviderKind.SemanticTokensRange],
            RefreshKind.InlayHints => [ProviderKind.InlayHint],
            RefreshKind.CodeLenses => [ProviderKind.CodeLens],
            RefreshKind.Folding => [ProviderKind.FoldingRange],
            _ => [],
        };
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var k in kinds)
                if (_states.TryGetValue(k, out var s) && s.Entries.Length > 0) Rebuild(k, s);
        }
    }

    public void DisposeAll()
    {
        List<IDisposable> tokens = [];
        lock (_gate)
        {
            _disposed = true;
            foreach (var s in _states.Values)
            {
                if (s.Token is not null) tokens.Add(s.Token);
                s.Token = null;
                s.Entries = [];
            }
            _states.Clear();
        }
        foreach (var t in tokens) t.Dispose();
    }

    private State GetState(ProviderKind kind)
    {
        if (!_states.TryGetValue(kind, out var s)) _states[kind] = s = new State();
        return s;
    }

    private void Rebuild(ProviderKind kind, State state)
    {
        state.Token?.Dispose();
        state.Token = null;
        if (state.Entries.Length == 0) return;
        state.Token = Register(kind, state.Entries);
    }

    private IDisposable Register(ProviderKind kind, FeatureEntry[] entries)
    {
        var sel = client.Definition.Selector;
        var owner = client.OwnerId;
        var reg = client.Services.Registry;
        return kind switch
        {
            ProviderKind.Completion => reg.Register<ICompletionProvider>(sel, new CompletionProvider(client, entries), owner),
            ProviderKind.Hover => reg.Register<IHoverProvider>(sel, new HoverProvider(client), owner),
            ProviderKind.SignatureHelp => reg.Register<ISignatureHelpProvider>(sel, new SignatureHelpProvider(client, entries), owner),
            ProviderKind.Definition => reg.Register<IDefinitionProvider>(sel, new DefinitionProvider(client, entries), owner),
            ProviderKind.References => reg.Register<IReferenceProvider>(sel, new ReferencesProvider(client), owner),
            ProviderKind.DocumentHighlight => reg.Register<IDocumentHighlightProvider>(sel, new DocumentHighlightProvider(client), owner),
            ProviderKind.DocumentSymbol => reg.Register<IDocumentSymbolProvider>(sel, new DocumentSymbolProvider(client), owner),
            ProviderKind.WorkspaceSymbol => reg.Register<IWorkspaceSymbolProvider>(DocumentSelector.All, new WorkspaceSymbolProvider(client), owner),
            ProviderKind.CodeAction => reg.Register<ICodeActionProvider>(sel, new CodeActionProvider(client), owner),
            ProviderKind.CodeLens => reg.Register<ICodeLensProvider>(sel, new CodeLensProvider(client), owner),
            ProviderKind.DocumentColor => reg.Register<IDocumentColorProvider>(sel, new DocumentColorProvider(client), owner),
            ProviderKind.Formatting => reg.Register<IDocumentFormattingProvider>(sel, new FormattingProvider(client, entries), owner),
            ProviderKind.Rename => reg.Register<IRenameProvider>(sel, new RenameProvider(client, entries), owner),
            ProviderKind.FoldingRange => reg.Register<IFoldingRangeProvider>(sel, new FoldingRangeProvider(client), owner),
            ProviderKind.SelectionRange => reg.Register<ISelectionRangeProvider>(sel, new SelectionRangeProvider(client), owner),
            ProviderKind.SemanticTokens => reg.Register<ISemanticTokensProvider>(sel, new SemanticTokensProvider(client, entries), owner),
            ProviderKind.SemanticTokensRange => reg.Register<ISemanticTokensRangeProvider>(sel, new SemanticTokensRangeProvider(client, entries), owner),
            ProviderKind.InlayHint => reg.Register<IInlayHintsProvider>(sel, new InlayHintsProvider(client, entries), owner),
            ProviderKind.LinkedEditing => reg.Register<ILinkedEditingRangeProvider>(sel, new LinkedEditingProvider(client), owner),
            ProviderKind.CallHierarchy => reg.Register<ICallHierarchyProvider>(sel, new CallHierarchyProvider(client), owner),
            ProviderKind.TypeHierarchy => reg.Register<ITypeHierarchyProvider>(sel, new TypeHierarchyProvider(client), owner),
            _ => Disposable.Empty,
        };
    }

    // =================================================================================================================
    // Static capabilities
    // =================================================================================================================

    public void RegisterStatic(P.ServerCapabilities c)
    {
        Static(ProviderKind.Completion, P.LspMethods.TextDocumentCompletion, c.CompletionProvider);
        Static(ProviderKind.Hover, P.LspMethods.TextDocumentHover, c.HoverProvider);
        Static(ProviderKind.SignatureHelp, P.LspMethods.TextDocumentSignatureHelp, c.SignatureHelpProvider);
        Static(ProviderKind.Definition, P.LspMethods.TextDocumentDefinition, c.DefinitionProvider);
        Static(ProviderKind.Definition, P.LspMethods.TextDocumentDeclaration, c.DeclarationProvider);
        Static(ProviderKind.Definition, P.LspMethods.TextDocumentTypeDefinition, c.TypeDefinitionProvider);
        Static(ProviderKind.Definition, P.LspMethods.TextDocumentImplementation, c.ImplementationProvider);
        Static(ProviderKind.References, P.LspMethods.TextDocumentReferences, c.ReferencesProvider);
        Static(ProviderKind.DocumentHighlight, P.LspMethods.TextDocumentDocumentHighlight, c.DocumentHighlightProvider);
        Static(ProviderKind.DocumentSymbol, P.LspMethods.TextDocumentDocumentSymbol, c.DocumentSymbolProvider);
        Static(ProviderKind.WorkspaceSymbol, P.LspMethods.WorkspaceSymbol, c.WorkspaceSymbolProvider);
        Static(ProviderKind.CodeAction, P.LspMethods.TextDocumentCodeAction, c.CodeActionProvider);
        Static(ProviderKind.CodeLens, P.LspMethods.TextDocumentCodeLens, c.CodeLensProvider);
        Static(ProviderKind.DocumentColor, P.LspMethods.TextDocumentDocumentColor, c.ColorProvider);
        Static(ProviderKind.Formatting, P.LspMethods.TextDocumentFormatting, c.DocumentFormattingProvider);
        Static(ProviderKind.Formatting, P.LspMethods.TextDocumentRangeFormatting, c.DocumentRangeFormattingProvider);
        Static(ProviderKind.Formatting, P.LspMethods.TextDocumentOnTypeFormatting, c.DocumentOnTypeFormattingProvider);
        Static(ProviderKind.Rename, P.LspMethods.TextDocumentRename, c.RenameProvider);
        Static(ProviderKind.FoldingRange, P.LspMethods.TextDocumentFoldingRange, c.FoldingRangeProvider);
        Static(ProviderKind.SelectionRange, P.LspMethods.TextDocumentSelectionRange, c.SelectionRangeProvider);
        Static(ProviderKind.InlayHint, P.LspMethods.TextDocumentInlayHint, c.InlayHintProvider);
        Static(ProviderKind.LinkedEditing, P.LspMethods.TextDocumentLinkedEditingRange, c.LinkedEditingRangeProvider);
        Static(ProviderKind.CallHierarchy, P.LspMethods.TextDocumentPrepareCallHierarchy, c.CallHierarchyProvider);
        Static(ProviderKind.TypeHierarchy, P.LspMethods.TextDocumentPrepareTypeHierarchy, c.TypeHierarchyProvider);

        if (c.SemanticTokensProvider is { } st)
        {
            var element = P.LspJson.ToElement(st.Value);
            var o = element.ValueKind == JsonValueKind.Object ? element : (JsonElement?)null;
            var full = o is not null && o.Value.TryGetProperty("full", out var f) && f.ValueKind is not (JsonValueKind.False or JsonValueKind.Null);
            var range = o is not null && o.Value.TryGetProperty("range", out var r) && r.ValueKind is not (JsonValueKind.False or JsonValueKind.Null);
            if (full) Add(ProviderKind.SemanticTokens, new FeatureEntry(StaticPrefix + "semanticTokens", "textDocument/semanticTokens", null, o));
            if (range) Add(ProviderKind.SemanticTokensRange, new FeatureEntry(StaticPrefix + "semanticTokensRange", P.LspMethods.TextDocumentSemanticTokensRange, null, o));
        }

        if (c.DiagnosticProvider is { } dp)
        {
            var d = dp.Match(o => o, r => new P.DiagnosticOptions { Identifier = r.Identifier, InterFileDependencies = r.InterFileDependencies, WorkspaceDiagnostics = r.WorkspaceDiagnostics });
            client.EnablePullDiagnostics(d.Identifier, null, d.WorkspaceDiagnostics);
        }
        if (c.ExecuteCommandProvider is { Commands: { Length: > 0 } cmds }) client.RegisterServerCommands(cmds, StaticPrefix + "executeCommand");
        if (c.Workspace?.FileOperations is { } fo) client.SetFileOperationOptions(fo);
    }

    private void Static(ProviderKind kind, string method, object? capability)
    {
        if (capability is P.IOneOf one) capability = one.Value;
        if (capability is null or false) return;
        JsonElement? options = capability is true ? null : P.LspJson.ToElement(capability);
        Add(kind, new FeatureEntry(StaticPrefix + method, method, null, options));
    }
}
