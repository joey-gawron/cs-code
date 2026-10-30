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

    // ---- additive members (clipboard and context menus; defaults keep other implementations compiling) ----

    /// <summary>Whether Cut / Copy / Paste can act now (a selection, or the whole-line mode; a writable document).</summary>
    bool CanCut => false;
    bool CanCopy => false;
    bool CanPaste => false;

    /// <summary>Visual Studio's Cut / Copy / Paste on this view (the Edit menu runs these when the editor has focus).</summary>
    Task CutAsync() => Task.CompletedTask;
    Task CopyAsync() => Task.CompletedTask;
    Task PasteAsync() => Task.CompletedTask;

    /// <summary>The caret's rectangle in the view's coordinates: where a keyboard-opened context menu goes.</summary>
    Avalonia.Rect GetCaretBounds() => default;

    /// <summary>Brings the context keys of the view's right-click menu up to date (the host calls it right before building that menu).</summary>
    void PrepareContextMenu() { }

    // ---- additive members (macros; defaults keep other implementations compiling) ----

    /// <summary>
    /// Text the user types into this view (a key's character or an IME commit), raised as the text input reaches the view's text area and before
    /// the editor applies it; only for input no earlier handler took (a modal layer such as Vim takes its own). Control characters that some
    /// platforms send after Enter, Tab or Backspace are not reported (those keys are key presses). Text inserted by code (paste, commands,
    /// <see cref="ReplayText"/>) is not reported. UI thread.
    /// </summary>
    Core.Lifecycle.IEventSource<string> TextInput => Core.Lifecycle.EventSource.Never<string>();

    /// <summary>
    /// Types <paramref name="text"/> through the editor's typing path exactly as if the user typed it (auto-closing and over-typing, completion
    /// and signature-help triggers, snippets, language-aware Enter for "\n"). Pass one character at a time to reproduce typing character by
    /// character (auto-closing applies to single characters). False when the view cannot take text (destroyed, read-only document). UI thread.
    /// </summary>
    bool ReplayText(string text) => false;

    /// <summary>
    /// Presses <paramref name="key"/> on the view's text area as the keyboard would (a routed key-down and key-up raised on the text area: the
    /// window's key handlers see it first, then the editor's own key handling: caret movement and selection, Backspace, Delete, Enter, Tab, the
    /// completion list, snippet placeholders). The text area takes the keyboard focus first: the editor's key commands act on the focused text
    /// area. Returns whether a handler took the key; false when the view cannot take the focus (destroyed, not in a window). UI thread.
    /// </summary>
    bool ReplayKey(Avalonia.Input.Key key, Avalonia.Input.KeyModifiers modifiers) => false;

    // ---- additive members (macros: one undo step per playback; defaults keep other implementations compiling) ----

    /// <summary>
    /// Holds an undo group of this view's document open until the result is disposed: every change of the document meanwhile (typing, keys,
    /// commands, in any view of it, over any number of dispatcher callbacks) becomes ONE undo step. Groups of one document nest: the group closes
    /// when its last handle is disposed, or when the document closes. While it is open, Undo and Redo (<see cref="Undo"/>, the menu, Ctrl+Z /
    /// Ctrl+Y on the text area) never throw: they reverse and re-apply the steps made inside the group, newest first, as the editor groups them
    /// (a typed run, one command's edit, one key's change), and never reach the history from before it, so one Undo after the group closed
    /// restores the text exactly as it was when it opened. Typing right before the group does not continue into it. Disposing twice, or after
    /// the view or the document is gone, does nothing. UI thread.
    /// </summary>
    IDisposable OpenUndoGroup() => Disposable.Empty;
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
