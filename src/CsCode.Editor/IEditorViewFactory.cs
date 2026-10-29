using Avalonia.Controls;
using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Editor;

/// <summary>
/// THE contract between the shell (CsCode.App) and the editor module. The shell hosts one <see cref="Control"/> per
/// open document tab and talks to it only through these interfaces. The module owner may ADD members but must not
/// rename or remove existing ones.
/// </summary>
public interface IEditorViewFactory
{
    /// <summary>
    /// Loads <paramref name="uri"/> through IFileSystem (encoding/EOL detection, large-file safeguards), detects its
    /// language via ILanguageService, and registers the document with IDocumentManager (disposed on close).
    /// Returns the existing document if already open.
    /// </summary>
    ValueTask<IEditorDocument> OpenDocumentAsync(Uri uri, CancellationToken ct = default);

    /// <summary>New "Untitled-N" buffer (scheme `untitled`).</summary>
    IEditorDocument CreateUntitled(string? languageId = null);

    /// <summary>Creates a view over a document. Multiple views per document (split) must be supported.</summary>
    Control CreateView(IEditorDocument document);

    /// <summary>The controller for a view returned by <see cref="CreateView"/> (null if the control isn't ours).</summary>
    IEditorViewController? GetController(Control view);

    /// <summary>Releases the document (unregisters from IDocumentManager, drops caches). Call when the last tab closes.</summary>
    ValueTask CloseDocumentAsync(IEditorDocument document);
}

/// <summary>Imperative handle on one editor view (menu/command/navigation code drives the editor through this).</summary>
public interface IEditorViewController
{
    IEditorDocument Document { get; }
    TextPosition Caret { get; }
    IReadOnlyList<TextRange> Selections { get; }
    void SetSelection(TextRange range, bool reveal = true);
    void Reveal(TextRange range, bool center = true);
    void Focus();
    bool CanUndo { get; }
    bool CanRedo { get; }
    void Undo();
    void Redo();
    void ShowFind(bool replace);
    void FormatDocument();
    /// <summary>Current debug-stop line decoration (null clears). Driven by the shell from IDebugService.StopLocation.</summary>
    void SetDebugStopLine(int? line);
    IEventSource<TextPosition> CaretChanged { get; }
    IEventSource<IReadOnlyList<TextRange>> SelectionChanged { get; }
}
