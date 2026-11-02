using CsCode.Lsp.Protocol;

namespace CsCode.Lsp.Client;

/// <summary>
/// The capabilities CsCode announces. HONEST by construction: every flag below corresponds to something the client really handles
/// (see docs/LSP.md capability matrix); anything not implemented is simply absent.
/// </summary>
internal static class ClientCapabilitiesFactory
{
    public static ClientCapabilities Create(bool offerAlternativePositionEncodings, bool offerPullDiagnostics = true)
    {
        var symbolKinds = Enumerable.Range((int)SymbolKind.File, (int)SymbolKind.TypeParameter).Select(i => (SymbolKind)i).ToArray();
        var completionKinds = Enumerable.Range((int)CompletionItemKind.Text, (int)CompletionItemKind.TypeParameter).Select(i => (CompletionItemKind)i).ToArray();
        MarkupKind[] markup = [MarkupKind.Markdown, MarkupKind.PlainText];

        return new ClientCapabilities
        {
            Workspace = new WorkspaceClientCapabilities
            {
                ApplyEdit = true,
                WorkspaceEdit = new WorkspaceEditClientCapabilities
                {
                    DocumentChanges = true,
                    ResourceOperations = [ResourceOperationKind.Create, ResourceOperationKind.Rename, ResourceOperationKind.Delete],
                    FailureHandling = FailureHandlingKind.Abort,
                },
                DidChangeConfiguration = new DidChangeConfigurationClientCapabilities { DynamicRegistration = true },
                DidChangeWatchedFiles = new DidChangeWatchedFilesClientCapabilities { DynamicRegistration = true, RelativePatternSupport = true },
                Symbol = new WorkspaceSymbolClientCapabilities
                {
                    DynamicRegistration = true,
                    SymbolKind = new WorkspaceSymbolClientCapabilitiesSymbolKind { ValueSet = symbolKinds },
                },
                ExecuteCommand = new ExecuteCommandClientCapabilities { DynamicRegistration = true },
                WorkspaceFolders = true,
                Configuration = true,
                SemanticTokens = new SemanticTokensWorkspaceClientCapabilities { RefreshSupport = true },
                CodeLens = new CodeLensWorkspaceClientCapabilities { RefreshSupport = true },
                InlayHint = new InlayHintWorkspaceClientCapabilities { RefreshSupport = true },
                Diagnostics = offerPullDiagnostics ? new DiagnosticWorkspaceClientCapabilities { RefreshSupport = true } : null,
                FileOperations = new FileOperationClientCapabilities
                {
                    DynamicRegistration = true, DidCreate = true, DidRename = true, DidDelete = true, WillRename = true,
                },
            },
            TextDocument = new TextDocumentClientCapabilities
            {
                Synchronization = new TextDocumentSyncClientCapabilities { DynamicRegistration = true, WillSave = true, WillSaveWaitUntil = true, DidSave = true },
                Completion = new CompletionClientCapabilities
                {
                    DynamicRegistration = true,
                    ContextSupport = true,
                    CompletionItem = new CompletionClientCapabilitiesCompletionItem
                    {
                        SnippetSupport = true,
                        CommitCharactersSupport = true,
                        DocumentationFormat = markup,
                        DeprecatedSupport = true,
                        PreselectSupport = true,
                        InsertReplaceSupport = true,
                        LabelDetailsSupport = true,
                        TagSupport = new CompletionClientCapabilitiesCompletionItemTagSupport { ValueSet = [CompletionItemTag.Deprecated] },
                        ResolveSupport = new CompletionClientCapabilitiesCompletionItemResolveSupport { Properties = ["documentation", "detail", "additionalTextEdits"] },
                    },
                    CompletionItemKind = new CompletionClientCapabilitiesCompletionItemKind { ValueSet = completionKinds },
                    CompletionList = new CompletionClientCapabilitiesCompletionList { ItemDefaults = ["commitCharacters", "editRange", "insertTextFormat", "data"] },
                },
                Hover = new HoverClientCapabilities { DynamicRegistration = true, ContentFormat = markup },
                SignatureHelp = new SignatureHelpClientCapabilities
                {
                    DynamicRegistration = true,
                    ContextSupport = true,
                    SignatureInformation = new SignatureHelpClientCapabilitiesSignatureInformation
                    {
                        DocumentationFormat = markup,
                        ParameterInformation = new SignatureHelpClientCapabilitiesSignatureInformationParameterInformation { LabelOffsetSupport = true },
                        ActiveParameterSupport = true,
                    },
                },
                Declaration = new DeclarationClientCapabilities { DynamicRegistration = true, LinkSupport = true },
                Definition = new DefinitionClientCapabilities { DynamicRegistration = true, LinkSupport = true },
                TypeDefinition = new TypeDefinitionClientCapabilities { DynamicRegistration = true, LinkSupport = true },
                Implementation = new ImplementationClientCapabilities { DynamicRegistration = true, LinkSupport = true },
                References = new ReferenceClientCapabilities { DynamicRegistration = true },
                DocumentHighlight = new DocumentHighlightClientCapabilities { DynamicRegistration = true },
                DocumentSymbol = new DocumentSymbolClientCapabilities
                {
                    DynamicRegistration = true,
                    HierarchicalDocumentSymbolSupport = true,
                    SymbolKind = new DocumentSymbolClientCapabilitiesSymbolKind { ValueSet = symbolKinds },
                    TagSupport = new DocumentSymbolClientCapabilitiesTagSupport { ValueSet = [SymbolTag.Deprecated] },
                },
                CodeAction = new CodeActionClientCapabilities
                {
                    DynamicRegistration = true,
                    IsPreferredSupport = true,
                    DisabledSupport = true,
                    DataSupport = true,
                    ResolveSupport = new CodeActionClientCapabilitiesResolveSupport { Properties = ["edit"] },
                    CodeActionLiteralSupport = new CodeActionClientCapabilitiesCodeActionLiteralSupport
                    {
                        CodeActionKind = new CodeActionClientCapabilitiesCodeActionLiteralSupportCodeActionKind
                        {
                            ValueSet = new[] { "", "quickfix", "refactor", "refactor.extract", "refactor.inline", "refactor.rewrite", "source", "source.organizeImports", "source.fixAll" }
                                .Select(s => new CodeActionKind(s)).ToArray(),
                        },
                    },
                },
                CodeLens = new CodeLensClientCapabilities { DynamicRegistration = true },
                ColorProvider = new DocumentColorClientCapabilities { DynamicRegistration = true },
                Formatting = new DocumentFormattingClientCapabilities { DynamicRegistration = true },
                RangeFormatting = new DocumentRangeFormattingClientCapabilities { DynamicRegistration = true },
                OnTypeFormatting = new DocumentOnTypeFormattingClientCapabilities { DynamicRegistration = true },
                Rename = new RenameClientCapabilities
                {
                    DynamicRegistration = true,
                    PrepareSupport = true,
                    PrepareSupportDefaultBehavior = PrepareSupportDefaultBehavior.Identifier,
                },
                FoldingRange = new FoldingRangeClientCapabilities { DynamicRegistration = true, LineFoldingOnly = true },
                SelectionRange = new SelectionRangeClientCapabilities { DynamicRegistration = true },
                PublishDiagnostics = new PublishDiagnosticsClientCapabilities
                {
                    RelatedInformation = true,
                    VersionSupport = true,
                    CodeDescriptionSupport = true,
                    DataSupport = true,
                    TagSupport = new PublishDiagnosticsClientCapabilitiesTagSupport { ValueSet = [DiagnosticTag.Unnecessary, DiagnosticTag.Deprecated] },
                },
                Diagnostic = offerPullDiagnostics ? new DiagnosticClientCapabilities { DynamicRegistration = true, RelatedDocumentSupport = true } : null,
                SemanticTokens = new SemanticTokensClientCapabilities
                {
                    DynamicRegistration = true,
                    Requests = new SemanticTokensClientCapabilitiesRequests
                    {
                        Range = true,
                        Full = new SemanticTokensClientCapabilitiesRequestsFullOption2 { Delta = true },
                    },
                    TokenTypes = SemanticTokenTypesList,
                    TokenModifiers = SemanticTokenModifiersList,
                    Formats = [TokenFormat.Relative],
                    ServerCancelSupport = true,
                    OverlappingTokenSupport = false,
                    MultilineTokenSupport = false,
                },
                // lang-2: tooltips and label parts' tooltips, locations and commands may come later, by inlayHint/resolve (not text edits: CsCode
                // does not insert hints)
                InlayHint = new InlayHintClientCapabilities
                {
                    DynamicRegistration = true,
                    ResolveSupport = new InlayHintClientCapabilitiesResolveSupport { Properties = ["tooltip", "label.tooltip", "label.location", "label.command"] },
                },
                LinkedEditingRange = new LinkedEditingRangeClientCapabilities { DynamicRegistration = true },
                CallHierarchy = new CallHierarchyClientCapabilities { DynamicRegistration = true },
                TypeHierarchy = new TypeHierarchyClientCapabilities { DynamicRegistration = true },
            },
            Window = new WindowClientCapabilities
            {
                WorkDoneProgress = true,
                ShowMessage = new ShowMessageRequestClientCapabilities { MessageActionItem = new ShowMessageRequestClientCapabilitiesMessageActionItem { AdditionalPropertiesSupport = false } },
                ShowDocument = new ShowDocumentClientCapabilities { Support = true },
            },
            General = new GeneralClientCapabilities
            {
                PositionEncodings = offerAlternativePositionEncodings
                    ? [PositionEncodingKind.UTF8, PositionEncodingKind.UTF16, PositionEncodingKind.UTF32]
                    : [PositionEncodingKind.UTF16],
                StaleRequestSupport = new GeneralClientCapabilitiesStaleRequestSupport
                {
                    Cancel = true,
                    RetryOnContentModified = [LspMethods.TextDocumentSemanticTokensFull, LspMethods.TextDocumentSemanticTokensFullDelta, LspMethods.TextDocumentSemanticTokensRange, LspMethods.TextDocumentInlayHint, LspMethods.TextDocumentCodeLens, LspMethods.TextDocumentDiagnostic],
                },
            },
        };
    }

    // The legend the editor can colour. Servers pick the subset they support; unknown types still arrive and are ignored by the theme.
    private static readonly string[] SemanticTokenTypesList =
    [
        "namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property", "enumMember", "event",
        "function", "method", "macro", "keyword", "modifier", "comment", "string", "number", "regexp", "operator", "decorator",
    ];

    private static readonly string[] SemanticTokenModifiersList =
    [
        "declaration", "definition", "readonly", "static", "deprecated", "abstract", "async", "modification", "documentation", "defaultLibrary",
    ];
}
