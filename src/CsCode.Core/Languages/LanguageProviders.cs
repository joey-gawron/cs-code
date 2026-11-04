using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Core.Languages;

// One interface per language feature (the vscode.languages.register*Provider shape). A provider is registered
// against a DocumentSelector in ILanguageFeatureRegistry; providers are produced by:
//   - CsCode.Lsp         (native language clients: one provider instance per server per feature)
//   - CsCode.Remote      (bridge providers for a remote workspace, whose language clients run in CsCode.Server)
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

    /// <summary>
    /// The hint with what its provider computes lazily (LSP <c>inlayHint/resolve</c>: tooltips, label parts' locations and commands), for a hint
    /// with <see cref="InlayHint.CanResolve"/>; the editor asks when the pointer rests on it or before following a link. Additive (lang-2): the
    /// default returns the hint as it is.
    /// </summary>
    ValueTask<InlayHint> ResolveInlayHintAsync(InlayHint hint, CancellationToken ct) => ValueTask.FromResult(hint);
}

public interface ICodeLensProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<CodeLens>?> ProvideCodeLensesAsync(ITextDocument document, CancellationToken ct);
    ValueTask<CodeLens> ResolveCodeLensAsync(CodeLens lens, CancellationToken ct) => ValueTask.FromResult(lens);

    /// <summary>
    /// Fires when the lenses of a document may have changed without an edit (the Test Explorer discovered tests): the document's URI, or null
    /// for every document. Additive: language servers re-register on <c>workspace/codeLens/refresh</c> instead, so the default never fires.
    /// </summary>
    IEventSource<Uri?> CodeLensesChanged => EventSource.Never<Uri?>();
}

public interface IDocumentColorProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<ColorInformation>?> ProvideDocumentColorsAsync(ITextDocument document, CancellationToken ct);

    /// <summary>
    /// The ways <paramref name="color"/> (a colour this provider reported, with the picker's new components and the range its text now covers)
    /// can be written in the document (LSP <c>textDocument/colorPresentation</c>): the editor's colour picker offers them and applies the chosen
    /// one's edit. Additive (colour picker): the default has none, so the picker shows the colour without editing it.
    /// </summary>
    ValueTask<IReadOnlyList<ColorPresentation>?> ProvideColorPresentationsAsync(ITextDocument document, ColorInformation color, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<ColorPresentation>?>(null);
}
