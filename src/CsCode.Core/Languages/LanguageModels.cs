using System.Text.Json;
using CsCode.Core.Text;

namespace CsCode.Core.Languages;

// Immutable, LSP-shaped (0-based, UTF-16) models shared by the native LSP client, the extension-host bridge,
// the editor and the Error List. They intentionally mirror LSP 3.17 semantics so mapping is mechanical.

public sealed record MarkupContent(string Value, bool IsMarkdown = false)
{
    public static MarkupContent Plain(string text) => new(text, false);
    public static MarkupContent Markdown(string text) => new(text, true);
}

public sealed record Command(string Title, string CommandId, IReadOnlyList<JsonElement>? Arguments = null);

public sealed record Location(Uri Uri, TextRange Range);

/// <summary>Definition/references target with optional origin selection (LSP LocationLink).</summary>
public sealed record LocationLink(Uri TargetUri, TextRange TargetRange, TextRange TargetSelectionRange, TextRange? OriginSelectionRange = null);

public enum DiagnosticSeverity { Error = 1, Warning = 2, Information = 3, Hint = 4 }
public enum DiagnosticTag { Unnecessary = 1, Deprecated = 2 }

public sealed record DiagnosticRelatedInformation(Location Location, string Message);

public sealed record Diagnostic(
    TextRange Range,
    string Message,
    DiagnosticSeverity Severity = DiagnosticSeverity.Error,
    string? Code = null,
    string? Source = null,
    IReadOnlyList<DiagnosticTag>? Tags = null,
    IReadOnlyList<DiagnosticRelatedInformation>? RelatedInformation = null,
    Uri? CodeDescriptionHref = null);

// ---- Completion ----

public enum CompletionItemKind
{
    Text = 1, Method, Function, Constructor, Field, Variable, Class, Interface, Module, Property, Unit, Value,
    Enum, Keyword, Snippet, Color, File, Reference, Folder, EnumMember, Constant, Struct, Event, Operator, TypeParameter
}

public enum InsertTextFormat { PlainText = 1, Snippet = 2 }
public enum CompletionTriggerKind { Invoked = 1, TriggerCharacter = 2, TriggerForIncompleteCompletions = 3 }
public sealed record CompletionContext(CompletionTriggerKind TriggerKind, string? TriggerCharacter = null);

public sealed record CompletionItem
{
    public required string Label { get; init; }
    public string? LabelDetail { get; init; }
    public string? LabelDescription { get; init; }
    public CompletionItemKind Kind { get; init; } = CompletionItemKind.Text;
    public string? Detail { get; init; }
    public MarkupContent? Documentation { get; init; }
    public string? SortText { get; init; }
    public string? FilterText { get; init; }
    /// <summary>Used when <see cref="TextEdit"/> is null; otherwise Label.</summary>
    public string? InsertText { get; init; }
    public InsertTextFormat InsertTextFormat { get; init; } = InsertTextFormat.PlainText;
    /// <summary>Primary edit; its range must be single-line and contain the request position.</summary>
    public TextEdit? TextEdit { get; init; }
    public IReadOnlyList<TextEdit>? AdditionalTextEdits { get; init; }
    public IReadOnlyList<string>? CommitCharacters { get; init; }
    public bool Preselect { get; init; }
    public bool Deprecated { get; init; }
    public Command? Command { get; init; }
    /// <summary>Opaque provider data round-tripped to <c>ResolveAsync</c>.</summary>
    public JsonElement? Data { get; init; }
}

public sealed record CompletionList(IReadOnlyList<CompletionItem> Items, bool IsIncomplete = false);

// ---- Hover / signature ----

public sealed record Hover(IReadOnlyList<MarkupContent> Contents, TextRange? Range = null);

public sealed record ParameterInformation(string Label, MarkupContent? Documentation = null, (int Start, int End)? LabelRange = null);

public sealed record SignatureInformation(
    string Label,
    MarkupContent? Documentation = null,
    IReadOnlyList<ParameterInformation>? Parameters = null,
    int? ActiveParameter = null);

public sealed record SignatureHelp(IReadOnlyList<SignatureInformation> Signatures, int ActiveSignature = 0, int? ActiveParameter = null);

public enum SignatureHelpTriggerKind { Invoked = 1, TriggerCharacter = 2, ContentChange = 3 }
public sealed record SignatureHelpContext(SignatureHelpTriggerKind TriggerKind, string? TriggerCharacter, bool IsRetrigger, SignatureHelp? ActiveSignatureHelp);

// ---- Edits ----

public abstract record ResourceOperation;
public sealed record CreateFileOperation(Uri Uri, bool Overwrite = false, bool IgnoreIfExists = false) : ResourceOperation;
public sealed record RenameFileOperation(Uri OldUri, Uri NewUri, bool Overwrite = false, bool IgnoreIfExists = false) : ResourceOperation;
public sealed record DeleteFileOperation(Uri Uri, bool Recursive = false, bool IgnoreIfNotExists = false) : ResourceOperation;

/// <summary>Text edits per document plus ordered resource operations (applied before text edits unless Order says otherwise).</summary>
public sealed record WorkspaceEdit(
    IReadOnlyDictionary<Uri, IReadOnlyList<TextEdit>> Changes,
    IReadOnlyList<ResourceOperation>? ResourceOperations = null)
{
    public static readonly WorkspaceEdit Empty = new(new Dictionary<Uri, IReadOnlyList<TextEdit>>());
    public bool IsEmpty => Changes.Count == 0 && (ResourceOperations is null || ResourceOperations.Count == 0);
}

public sealed record CodeAction(
    string Title,
    string? Kind = null,
    IReadOnlyList<Diagnostic>? Diagnostics = null,
    bool IsPreferred = false,
    string? DisabledReason = null,
    WorkspaceEdit? Edit = null,
    Command? Command = null,
    JsonElement? Data = null);

public enum CodeActionTriggerKind { Invoked = 1, Automatic = 2 }
public sealed record CodeActionContext(
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<string>? Only = null,
    CodeActionTriggerKind TriggerKind = CodeActionTriggerKind.Invoked);

public sealed record PrepareRenameResult(TextRange Range, string? Placeholder = null);

public sealed record FormattingOptions(int TabSize = 4, bool InsertSpaces = true, bool TrimTrailingWhitespace = false, bool InsertFinalNewline = false);

// ---- Symbols / navigation ----

public enum SymbolKind
{
    File = 1, Module, Namespace, Package, Class, Method, Property, Field, Constructor, Enum, Interface, Function,
    Variable, Constant, String, Number, Boolean, Array, Object, Key, Null, EnumMember, Struct, Event, Operator, TypeParameter
}

public sealed record DocumentSymbol(
    string Name,
    SymbolKind Kind,
    TextRange Range,
    TextRange SelectionRange,
    string? Detail = null,
    IReadOnlyList<DocumentSymbol>? Children = null,
    bool Deprecated = false);

public sealed record WorkspaceSymbol(string Name, SymbolKind Kind, Location Location, string? ContainerName = null);

public enum FoldingRangeKind { Comment, Imports, Region }
public sealed record FoldingRange(int StartLine, int EndLine, FoldingRangeKind? Kind = null, string? CollapsedText = null);

public enum DocumentHighlightKind { Text = 1, Read = 2, Write = 3 }
public sealed record DocumentHighlight(TextRange Range, DocumentHighlightKind Kind = DocumentHighlightKind.Text);

public sealed record SelectionRange(TextRange Range, SelectionRange? Parent = null);

// ---- Semantic tokens / inlay hints / lenses (optional features; providers may return null) ----

public sealed record SemanticTokensLegend(IReadOnlyList<string> TokenTypes, IReadOnlyList<string> TokenModifiers);

/// <summary>Raw LSP-encoded semantic tokens (5 uints per token, relative line/char encoding).</summary>
public sealed record SemanticTokens(IReadOnlyList<uint> Data, string? ResultId = null);

public enum InlayHintKind { Type = 1, Parameter = 2 }
public sealed record InlayHint(TextPosition Position, string Label, InlayHintKind? Kind = null, bool PaddingLeft = false, bool PaddingRight = false, MarkupContent? Tooltip = null);

public sealed record CodeLens(TextRange Range, Command? Command = null, JsonElement? Data = null);

public sealed record ColorInformation(TextRange Range, double Red, double Green, double Blue, double Alpha);
