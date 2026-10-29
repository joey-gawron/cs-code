using System.Text.Json;
using CsCode.Core.Text;

namespace CsCode.Core.Languages;

// Additive contracts (added by the LSP module) for LSP features that have no counterpart in LanguageProviders.cs yet.
// Providers are registered in ILanguageFeatureRegistry exactly like the others; consumers that do not know these
// interfaces simply never ask for them.

/// <summary>Semantic tokens for a visible range (cheaper than the whole document on big files). Same legend as <see cref="ISemanticTokensProvider"/>.</summary>
public interface ISemanticTokensRangeProvider : ILanguageProvider
{
    SemanticTokensLegend Legend { get; }
    ValueTask<SemanticTokens?> ProvideSemanticTokensRangeAsync(ITextDocument document, TextRange range, CancellationToken ct);
}

/// <summary>Ranges that must be edited together (HTML tag pairs, ...). Empty-range edits are applied to every range.</summary>
public sealed record LinkedEditingRanges(IReadOnlyList<TextRange> Ranges, string? WordPattern = null);

public interface ILinkedEditingRangeProvider : ILanguageProvider
{
    ValueTask<LinkedEditingRanges?> ProvideLinkedEditingRangesAsync(ITextDocument document, TextPosition position, CancellationToken ct);
}

/// <summary>Call/type hierarchy node. <see cref="Data"/> is opaque provider data that must be passed back unchanged.</summary>
public sealed record HierarchyItem(
    string Name,
    SymbolKind Kind,
    Uri Uri,
    TextRange Range,
    TextRange SelectionRange,
    string? Detail = null,
    JsonElement? Data = null);

public sealed record CallHierarchyIncomingCall(HierarchyItem From, IReadOnlyList<TextRange> FromRanges);
public sealed record CallHierarchyOutgoingCall(HierarchyItem To, IReadOnlyList<TextRange> FromRanges);

public interface ICallHierarchyProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<HierarchyItem>?> PrepareCallHierarchyAsync(ITextDocument document, TextPosition position, CancellationToken ct);
    ValueTask<IReadOnlyList<CallHierarchyIncomingCall>?> IncomingCallsAsync(HierarchyItem item, CancellationToken ct);
    ValueTask<IReadOnlyList<CallHierarchyOutgoingCall>?> OutgoingCallsAsync(HierarchyItem item, CancellationToken ct);
}

public interface ITypeHierarchyProvider : ILanguageProvider
{
    ValueTask<IReadOnlyList<HierarchyItem>?> PrepareTypeHierarchyAsync(ITextDocument document, TextPosition position, CancellationToken ct);
    ValueTask<IReadOnlyList<HierarchyItem>?> SupertypesAsync(HierarchyItem item, CancellationToken ct);
    ValueTask<IReadOnlyList<HierarchyItem>?> SubtypesAsync(HierarchyItem item, CancellationToken ct);
}

public enum DocumentSaveReason { Manual = 1, AfterDelay = 2, FocusOut = 3 }

/// <summary>
/// Called by the editor/shell right BEFORE a document is saved. Language clients send <c>willSave</c> and apply the edits returned by
/// <c>willSaveWaitUntil</c> (bounded by a short timeout, so a slow server can never block saving). The caller applies the returned edits
/// (as one undo unit) and then saves. Resolve all implementations with <c>IEnumerable&lt;IWillSaveParticipant&gt;</c>.
/// </summary>
public interface IWillSaveParticipant
{
    ValueTask<IReadOnlyList<TextEdit>?> OnWillSaveAsync(ITextDocument document, DocumentSaveReason reason, CancellationToken ct);
}

/// <summary>
/// File create/rename/delete notifications for language servers (<c>workspace/willRenameFiles</c>, <c>didRenameFiles</c>, ...).
/// The shell calls Will* before touching the file system (and applies the returned edit), Did* afterwards.
/// </summary>
public interface IFileOperationParticipant
{
    ValueTask<WorkspaceEdit?> WillRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct);
    ValueTask DidRenameFilesAsync(IReadOnlyList<(Uri OldUri, Uri NewUri)> files, CancellationToken ct);
    ValueTask DidCreateFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct);
    ValueTask DidDeleteFilesAsync(IReadOnlyList<Uri> files, CancellationToken ct);
}
