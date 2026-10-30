using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Remote.Protocol;

namespace CsCode.Remote.Bridge;

// One small class per feature interface: each forwards to the server's provider of the same registration (LangOps names the method).
// Interactive features (what the user waits for while typing/pointing) use the short timeout; whole-document features the long one.

internal abstract class BridgeBase(LanguageBridge bridge, LangRegistration registration) : ILanguageProvider
{
    protected readonly LanguageBridge Bridge = bridge;
    protected readonly LangRegistration Registration = registration;

    protected ValueTask<T?> Call<T>(string op, ITextDocument? doc, LangArgs args, CancellationToken ct, bool interactive = true) where T : class =>
        Bridge.CallAsync<T>(Registration.Id, op, doc, args, interactive, ct);
}

internal sealed class CompletionBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), ICompletionProvider
{
    public IReadOnlyList<string> TriggerCharacters => Registration.Info?.TriggerCharacters ?? [];

    public async ValueTask<CompletionList?> ProvideCompletionAsync(ITextDocument document, TextPosition position, CompletionContext context, CancellationToken ct)
    {
        var r = await Call<ResolvableResult<CompletionList>>(LangOps.Completion, document, new LangArgs { Position = position, CompletionContext = context }, ct).ConfigureAwait(false);
        Bridge.Remember(r?.ResultId, r?.Value?.Items);
        return r?.Value;
    }

    public async ValueTask<CompletionItem> ResolveCompletionAsync(CompletionItem item, CancellationToken ct) =>
        await Call<CompletionItem>(LangOps.ResolveCompletion, null, new LangArgs { CompletionItem = item, Origin = Bridge.OriginOf(item) }, ct).ConfigureAwait(false) ?? item;
}

internal sealed class HoverBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), IHoverProvider
{
    public ValueTask<Hover?> ProvideHoverAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
        Call<Hover>(LangOps.Hover, document, new LangArgs { Position = position }, ct);
}

internal sealed class DefinitionBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), IDefinitionProvider
{
    public ValueTask<IReadOnlyList<LocationLink>?> ProvideDefinitionAsync(ITextDocument document, TextPosition position, DefinitionKind kind, CancellationToken ct) =>
        Call<IReadOnlyList<LocationLink>>(LangOps.Definition, document, new LangArgs { Position = position, DefinitionKind = kind }, ct);
}

internal sealed class ReferenceBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), IReferenceProvider
{
    public ValueTask<IReadOnlyList<Location>?> ProvideReferencesAsync(ITextDocument document, TextPosition position, bool includeDeclaration, CancellationToken ct) =>
        Call<IReadOnlyList<Location>>(LangOps.References, document, new LangArgs { Position = position, Flag = includeDeclaration }, ct, interactive: false);
}

internal sealed class HighlightBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), IDocumentHighlightProvider
{
    public ValueTask<IReadOnlyList<DocumentHighlight>?> ProvideDocumentHighlightsAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
        Call<IReadOnlyList<DocumentHighlight>>(LangOps.Highlights, document, new LangArgs { Position = position }, ct);
}

internal sealed class RenameBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), IRenameProvider
{
    public ValueTask<PrepareRenameResult?> PrepareRenameAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
        Call<PrepareRenameResult>(LangOps.PrepareRename, document, new LangArgs { Position = position }, ct);

    public ValueTask<WorkspaceEdit?> ProvideRenameAsync(ITextDocument document, TextPosition position, string newName, CancellationToken ct) =>
        Call<WorkspaceEdit>(LangOps.Rename, document, new LangArgs { Position = position, Text = newName }, ct, interactive: false);
}

internal sealed class FormattingBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), IDocumentFormattingProvider
{
    public IReadOnlyList<string> OnTypeTriggerCharacters => Registration.Info?.OnTypeTriggerCharacters ?? [];

    public ValueTask<IReadOnlyList<TextEdit>?> FormatDocumentAsync(ITextDocument document, FormattingOptions options, CancellationToken ct) =>
        Call<IReadOnlyList<TextEdit>>(LangOps.Format, document, new LangArgs { Formatting = options }, ct, interactive: false);

    public ValueTask<IReadOnlyList<TextEdit>?> FormatRangeAsync(ITextDocument document, TextRange range, FormattingOptions options, CancellationToken ct) =>
        Call<IReadOnlyList<TextEdit>>(LangOps.FormatRange, document, new LangArgs { Range = range, Formatting = options }, ct, interactive: false);

    public ValueTask<IReadOnlyList<TextEdit>?> FormatOnTypeAsync(ITextDocument document, TextPosition position, string typedCharacter, FormattingOptions options, CancellationToken ct) =>
        Call<IReadOnlyList<TextEdit>>(LangOps.FormatOnType, document, new LangArgs { Position = position, Text = typedCharacter, Formatting = options }, ct);
}

internal sealed class SignatureHelpBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), ISignatureHelpProvider
{
    public IReadOnlyList<string> TriggerCharacters => Registration.Info?.TriggerCharacters ?? [];
    public IReadOnlyList<string> RetriggerCharacters => Registration.Info?.RetriggerCharacters ?? [];

    public ValueTask<SignatureHelp?> ProvideSignatureHelpAsync(ITextDocument document, TextPosition position, SignatureHelpContext context, CancellationToken ct) =>
        Call<SignatureHelp>(LangOps.SignatureHelp, document, new LangArgs { Position = position, SignatureHelpContext = context }, ct);
}

internal sealed class CodeActionBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), ICodeActionProvider
{
    public async ValueTask<IReadOnlyList<CodeAction>?> ProvideCodeActionsAsync(ITextDocument document, TextRange range, CodeActionContext context, CancellationToken ct)
    {
        var res = await Call<ResolvableResult<IReadOnlyList<CodeAction>>>(LangOps.CodeActions, document, new LangArgs { Range = range, CodeActionContext = context }, ct).ConfigureAwait(false);
        Bridge.Remember(res?.ResultId, res?.Value);
        return res?.Value;
    }

    public async ValueTask<CodeAction> ResolveCodeActionAsync(CodeAction action, CancellationToken ct) =>
        await Call<CodeAction>(LangOps.ResolveCodeAction, null, new LangArgs { CodeAction = action, Origin = Bridge.OriginOf(action) }, ct).ConfigureAwait(false) ?? action;
}

internal sealed class DocumentSymbolBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), IDocumentSymbolProvider
{
    public ValueTask<IReadOnlyList<DocumentSymbol>?> ProvideDocumentSymbolsAsync(ITextDocument document, CancellationToken ct) =>
        Call<IReadOnlyList<DocumentSymbol>>(LangOps.DocumentSymbols, document, new LangArgs(), ct, interactive: false);
}

internal sealed class WorkspaceSymbolBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), IWorkspaceSymbolProvider
{
    public ValueTask<IReadOnlyList<WorkspaceSymbol>?> ProvideWorkspaceSymbolsAsync(string query, CancellationToken ct) =>
        Call<IReadOnlyList<WorkspaceSymbol>>(LangOps.WorkspaceSymbols, null, new LangArgs { Text = query }, ct);
}

internal sealed class FoldingBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), IFoldingRangeProvider
{
    public ValueTask<IReadOnlyList<FoldingRange>?> ProvideFoldingRangesAsync(ITextDocument document, CancellationToken ct) =>
        Call<IReadOnlyList<FoldingRange>>(LangOps.FoldingRanges, document, new LangArgs(), ct, interactive: false);
}

internal sealed class SelectionRangeBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), ISelectionRangeProvider
{
    public ValueTask<IReadOnlyList<SelectionRange>?> ProvideSelectionRangesAsync(ITextDocument document, IReadOnlyList<TextPosition> positions, CancellationToken ct) =>
        Call<IReadOnlyList<SelectionRange>>(LangOps.SelectionRanges, document, new LangArgs { Positions = positions }, ct);
}

internal sealed class SemanticTokensBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), ISemanticTokensProvider
{
    public SemanticTokensLegend Legend => Registration.Info?.Legend ?? new SemanticTokensLegend([], []);

    public ValueTask<SemanticTokens?> ProvideSemanticTokensAsync(ITextDocument document, CancellationToken ct) =>
        Call<SemanticTokens>(LangOps.SemanticTokens, document, new LangArgs(), ct, interactive: false);
}

internal sealed class SemanticTokensRangeBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), ISemanticTokensRangeProvider
{
    public SemanticTokensLegend Legend => Registration.Info?.Legend ?? new SemanticTokensLegend([], []);

    public ValueTask<SemanticTokens?> ProvideSemanticTokensRangeAsync(ITextDocument document, TextRange range, CancellationToken ct) =>
        Call<SemanticTokens>(LangOps.SemanticTokensRange, document, new LangArgs { Range = range }, ct, interactive: false);
}

internal sealed class InlayHintsBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), IInlayHintsProvider
{
    public ValueTask<IReadOnlyList<InlayHint>?> ProvideInlayHintsAsync(ITextDocument document, TextRange range, CancellationToken ct) =>
        Call<IReadOnlyList<InlayHint>>(LangOps.InlayHints, document, new LangArgs { Range = range }, ct, interactive: false);
}

internal sealed class CodeLensBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), ICodeLensProvider
{
    public async ValueTask<IReadOnlyList<CodeLens>?> ProvideCodeLensesAsync(ITextDocument document, CancellationToken ct)
    {
        var res = await Call<ResolvableResult<IReadOnlyList<CodeLens>>>(LangOps.CodeLens, document, new LangArgs(), ct, interactive: false).ConfigureAwait(false);
        Bridge.Remember(res?.ResultId, res?.Value);
        return res?.Value;
    }

    public async ValueTask<CodeLens> ResolveCodeLensAsync(CodeLens lens, CancellationToken ct) =>
        await Call<CodeLens>(LangOps.ResolveCodeLens, null, new LangArgs { CodeLens = lens, Origin = Bridge.OriginOf(lens) }, ct).ConfigureAwait(false) ?? lens;
}

internal sealed class ColorBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), IDocumentColorProvider
{
    public ValueTask<IReadOnlyList<ColorInformation>?> ProvideDocumentColorsAsync(ITextDocument document, CancellationToken ct) =>
        Call<IReadOnlyList<ColorInformation>>(LangOps.DocumentColors, document, new LangArgs(), ct, interactive: false);
}

internal sealed class LinkedEditingBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), ILinkedEditingRangeProvider
{
    public ValueTask<LinkedEditingRanges?> ProvideLinkedEditingRangesAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
        Call<LinkedEditingRanges>(LangOps.LinkedEditing, document, new LangArgs { Position = position }, ct);
}

internal sealed class CallHierarchyBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), ICallHierarchyProvider
{
    public ValueTask<IReadOnlyList<HierarchyItem>?> PrepareCallHierarchyAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
        Call<IReadOnlyList<HierarchyItem>>(LangOps.PrepareCallHierarchy, document, new LangArgs { Position = position }, ct);

    public ValueTask<IReadOnlyList<CallHierarchyIncomingCall>?> IncomingCallsAsync(HierarchyItem item, CancellationToken ct) =>
        Call<IReadOnlyList<CallHierarchyIncomingCall>>(LangOps.IncomingCalls, null, new LangArgs { HierarchyItem = item }, ct, interactive: false);

    public ValueTask<IReadOnlyList<CallHierarchyOutgoingCall>?> OutgoingCallsAsync(HierarchyItem item, CancellationToken ct) =>
        Call<IReadOnlyList<CallHierarchyOutgoingCall>>(LangOps.OutgoingCalls, null, new LangArgs { HierarchyItem = item }, ct, interactive: false);
}

internal sealed class TypeHierarchyBridge(LanguageBridge b, LangRegistration r) : BridgeBase(b, r), ITypeHierarchyProvider
{
    public ValueTask<IReadOnlyList<HierarchyItem>?> PrepareTypeHierarchyAsync(ITextDocument document, TextPosition position, CancellationToken ct) =>
        Call<IReadOnlyList<HierarchyItem>>(LangOps.PrepareTypeHierarchy, document, new LangArgs { Position = position }, ct);

    public ValueTask<IReadOnlyList<HierarchyItem>?> SupertypesAsync(HierarchyItem item, CancellationToken ct) =>
        Call<IReadOnlyList<HierarchyItem>>(LangOps.Supertypes, null, new LangArgs { HierarchyItem = item }, ct, interactive: false);

    public ValueTask<IReadOnlyList<HierarchyItem>?> SubtypesAsync(HierarchyItem item, CancellationToken ct) =>
        Call<IReadOnlyList<HierarchyItem>>(LangOps.Subtypes, null, new LangArgs { HierarchyItem = item }, ct, interactive: false);
}
