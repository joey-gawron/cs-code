using CsCode.Core.Text;

namespace CsCode.Core.Languages;

// One interface per language feature (the vscode.languages.register*Provider shape). A provider is registered
// against a DocumentSelector in ILanguageFeatureRegistry; providers are produced by:
//   - CsCode.Lsp         (native language clients: one provider instance per server per feature)
//   - CsCode.Extensions  (providers registered by JS extensions via the extension host, proxied over RPC)
// and consumed by CsCode.Editor / CsCode.App. All calls are async and cancellable; return null for "no result".

public interface ILanguageProvider { }

public interface ICompletionProvider : ILanguageProvider
{
    IReadOnlyList<string> TriggerCharacters { get; }
    ValueTask<CompletionList?> ProvideCompletionAsync(ITextDocument document, TextPosition position, CompletionContext context, CancellationToken ct);
    ValueTask<CompletionItem> ResolveCompletionAsync(CompletionItem item, CancellationToken ct) => ValueTask.FromResult(item);
}

public interface IHoverProvider : ILanguageProvider
{
    ValueTask<Hover?> ProvideHoverAsync(ITextDocument document, TextPosition position, CancellationToken ct);
}

public enum DefinitionKind { Definition, Declaration, TypeDefinition, Implementation }

public interface IDefinitionProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<LocationLink>?> ProvideDefinitionAsync(ITextDocument document, TextPosition position, DefinitionKind kind, CancellationToken ct);
}

public interface IReferenceProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<Location>?> ProvideReferencesAsync(ITextDocument document, TextPosition position, bool includeDeclaration, CancellationToken ct);
}

public interface IDocumentHighlightProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<DocumentHighlight>?> ProvideDocumentHighlightsAsync(ITextDocument document, TextPosition position, CancellationToken ct);
}

public interface IRenameProvider : ILanguageProvider
{
    ValueTask<PrepareRenameResult?> PrepareRenameAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
        ValueTask.FromResult<PrepareRenameResult?>(null);
    ValueTask<WorkspaceEdit?> ProvideRenameAsync(ITextDocument document, TextPosition position, string newName, CancellationToken ct);
}

public interface IDocumentFormattingProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<TextEdit>?> FormatDocumentAsync(ITextDocument document, FormattingOptions options, CancellationToken ct);
    ValueTask<IReadOnlyList<TextEdit>?> FormatRangeAsync(ITextDocument document, TextRange range, FormattingOptions options, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<TextEdit>?>(null);
    /// <summary>Characters that trigger on-type formatting (first char + more). Empty = none.</summary>
    IReadOnlyList<string> OnTypeTriggerCharacters => [];
    ValueTask<IReadOnlyList<TextEdit>?> FormatOnTypeAsync(ITextDocument document, TextPosition position, string typedCharacter, FormattingOptions options, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<TextEdit>?>(null);
}

public interface ISignatureHelpProvider : ILanguageProvider
{
    IReadOnlyList<string> TriggerCharacters { get; }
    IReadOnlyList<string> RetriggerCharacters => [];
    ValueTask<SignatureHelp?> ProvideSignatureHelpAsync(ITextDocument document, TextPosition position, SignatureHelpContext context, CancellationToken ct);
}

public interface ICodeActionProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<CodeAction>?> ProvideCodeActionsAsync(ITextDocument document, TextRange range, CodeActionContext context, CancellationToken ct);
    ValueTask<CodeAction> ResolveCodeActionAsync(CodeAction action, CancellationToken ct) => ValueTask.FromResult(action);
}

public interface IDocumentSymbolProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<DocumentSymbol>?> ProvideDocumentSymbolsAsync(ITextDocument document, CancellationToken ct);
}

/// <summary>Workspace-wide (not per-document) symbol search; registered with DocumentSelector.All.</summary>
public interface IWorkspaceSymbolProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<WorkspaceSymbol>?> ProvideWorkspaceSymbolsAsync(string query, CancellationToken ct);
}

public interface IFoldingRangeProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<FoldingRange>?> ProvideFoldingRangesAsync(ITextDocument document, CancellationToken ct);
}

public interface ISelectionRangeProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<SelectionRange>?> ProvideSelectionRangesAsync(ITextDocument document, IReadOnlyList<TextPosition> positions, CancellationToken ct);
}

public interface ISemanticTokensProvider : ILanguageProvider
{
    SemanticTokensLegend Legend { get; }
    ValueTask<SemanticTokens?> ProvideSemanticTokensAsync(ITextDocument document, CancellationToken ct);
}

public interface IInlayHintsProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<InlayHint>?> ProvideInlayHintsAsync(ITextDocument document, TextRange range, CancellationToken ct);
}

public interface ICodeLensProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<CodeLens>?> ProvideCodeLensesAsync(ITextDocument document, CancellationToken ct);
    ValueTask<CodeLens> ResolveCodeLensAsync(CodeLens lens, CancellationToken ct) => ValueTask.FromResult(lens);
}

public interface IDocumentColorProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<ColorInformation>?> ProvideDocumentColorsAsync(ITextDocument document, CancellationToken ct);
}
