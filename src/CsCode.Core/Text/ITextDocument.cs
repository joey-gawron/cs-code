using CsCode.Core.Lifecycle;

namespace CsCode.Core.Text;

/// <summary>
/// UI-independent view of an open document. The editor (Avalonia/AvaloniaEdit) implements this over its own
/// rope; LSP, DAP and tests only ever see this interface.
/// All members must be callable from any thread; implementations marshal to the UI thread internally if needed.
/// </summary>
public interface ITextDocument
{
    Uri Uri { get; }
    string LanguageId { get; }

    /// <summary>Monotonic; incremented by every change (LSP document version).</summary>
    int Version { get; }
    bool IsDirty { get; }
    int Length { get; }
    int LineCount { get; }

    string GetText();
    string GetText(TextRange range);
    /// <summary>Text of the line excluding the line terminator.</summary>
    string GetLineText(int line);
    int OffsetAt(TextPosition position);
    TextPosition PositionAt(int offset);
    /// <summary>Clamp a possibly out-of-range position into the document.</summary>
    TextPosition Validate(TextPosition position);

    /// <summary>Applies edits (all ranges relative to the current state, non-overlapping) as ONE undo unit.</summary>
    bool ApplyEdits(IReadOnlyList<TextEdit> edits);

    IEventSource<TextDocumentChangedEventArgs> Changed { get; }
    IEventSource<ITextDocument> LanguageIdChanged { get; }
}

/// <summary>A document backed by a file (or untitled buffer) that the editor can save/reload.</summary>
public interface IEditorDocument : ITextDocument
{
    bool IsReadOnly { get; }
    bool IsUntitled { get; }
    string EncodingName { get; }
    string EndOfLine { get; }
    void SetLanguageId(string languageId);
    Task SaveAsync(CancellationToken ct = default);
    Task SaveAsAsync(Uri target, CancellationToken ct = default);
    Task ReloadAsync(CancellationToken ct = default);
    IEventSource<IEditorDocument> Saved { get; }
    IEventSource<IEditorDocument> DirtyChanged { get; }

    // ---- additive member (search editor; the default keeps other implementations compiling) ----

    /// <summary>
    /// Holds an undo group open until the result is disposed: every change meanwhile, over any number of dispatcher callbacks, is ONE undo step
    /// (the search editor writes a search's results in many small slices). UI thread. Default: no grouping (each edit is its own step).
    /// </summary>
    IDisposable OpenUndoGroup() => Disposable.Empty;
}

public sealed record DocumentEvent(ITextDocument Document);

/// <summary>
/// Registry of open documents. The editor registers documents on open and disposes the registration on close;
/// LSP (didOpen/didChange/didClose) and diagnostics subscribe here. Core ships a default
/// implementation (<see cref="DocumentManager"/>).
/// </summary>
public interface IDocumentManager
{
    IReadOnlyCollection<ITextDocument> Documents { get; }
    ITextDocument? Get(Uri uri);

    /// <summary>Adds the document and fires <see cref="Opened"/>; disposing the token fires <see cref="Closed"/>.</summary>
    IDisposable Register(ITextDocument document);

    IEventSource<DocumentEvent> Opened { get; }
    IEventSource<DocumentEvent> Closed { get; }
    IEventSource<DocumentEvent> Saved { get; }
    IEventSource<TextDocumentChangedEventArgs> Changed { get; }
    IEventSource<DocumentEvent> LanguageChanged { get; }

    /// <summary>Called by editor documents after a successful save.</summary>
    void NotifySaved(ITextDocument document);
}
