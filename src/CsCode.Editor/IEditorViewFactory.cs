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

    // ---- additive members (Editor module) ----

    /// <summary>
    /// Destroys a view returned by <see cref="CreateView"/>: unhooks every handler, renderer, transformer and margin it added so
    /// that nothing keeps the view alive. Capture <see cref="IEditorViewController.GetViewState"/> first when the view will be recreated
    /// (inactive tabs: destroy the view, keep the document, recreate from the saved state — that is what keeps memory flat).
    /// </summary>
    void DisposeView(Control view);
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

    // ---- additive members (Editor module) ----

    /// <summary>Scroll position, caret, selection and folded regions: enough to recreate this view later exactly as it was.</summary>
    EditorViewState GetViewState();

    /// <summary>Restores state captured by <see cref="GetViewState"/> (clamped to the current document; safe after the document changed).</summary>
    void RestoreViewState(EditorViewState state);

    /// <summary>Symbol path (outermost first) containing the caret, from IDocumentSymbolProvider; empty when unavailable. Never blocks.</summary>
    ValueTask<IReadOnlyList<string>> GetBreadcrumbsAsync(CancellationToken ct = default);

    /// <summary>True while this view (or one of its popups) has keyboard focus.</summary>
    bool IsFocused { get; }
}

/// <summary>Everything needed to recreate a destroyed view. Positions are 0-based lines/UTF-16 columns so they survive edits made while no view existed.</summary>
public sealed record EditorViewState(
    TextPosition Caret,
    TextPosition SelectionStart,
    TextPosition SelectionEnd,
    double VerticalOffset,
    double HorizontalOffset,
    IReadOnlyList<int> FoldedStartLines,
    int DocumentVersion)
{
    public static readonly EditorViewState Initial = new(TextPosition.Zero, TextPosition.Zero, TextPosition.Zero, 0, 0, [], 0);
}
