using CsCode.Core.Text;

namespace CsCode.Vim;

/// <summary>Injectable timer so 'timeoutlen' disambiguation is deterministic in tests. The callback must run on the engine's thread.</summary>
public interface IVimTimer
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}

/// <summary>
/// Everything the engine needs from the editor, kept deliberately small so the Editor/Shell adapter is thin.
/// Only <see cref="Buffer"/>, <see cref="GetCaret"/> and <see cref="SetCaret"/> are mandatory; every other member has a harmless
/// default. All calls happen on the UI thread, synchronously inside key handling: implementations must not block, and LSP-backed
/// actions (<see cref="ExecuteCommand"/>) must be fire-and-forget.
/// </summary>
public interface IVimHost
{
    // ---------------------------------------------------------------- buffer and caret

    /// <summary>The buffer the engine currently edits. Changes after <see cref="PerformAction"/> (e.g. :e, :bn) are picked up on the next key.</summary>
    IVimBuffer Buffer { get; }

    /// <summary>The caret as the host sees it. The engine adopts it at the start of each key, so mouse clicks just work.</summary>
    TextPosition GetCaret();

    /// <summary>Engine -> host after every key. <paramref name="insertLike"/> is true where the caret may sit after the last character.</summary>
    void SetCaret(TextPosition position, bool insertLike);

    /// <summary>The Visual selection to draw (<see cref="VimSelection.None"/> when not in Visual mode).</summary>
    void SetSelection(in VimSelection selection) { }

    // ---------------------------------------------------------------- feedback (status bar, message line, command line)

    void OnModeChanged(VimMode mode, in VimModeInfo info) { }
    void OnMessage(in VimMessage message) { }
    /// <summary>Pending keys / counts to show in the status bar ('showcmd'), e.g. "2d".</summary>
    void OnShowCommand(string pendingKeys) { }
    /// <summary>Non-null while recording a macro ("recording @a").</summary>
    void OnRecordingChanged(char? register) { }
    void OnCommandLineChanged(in VimCommandLine commandLine) { }
    /// <summary>Highlight matches of the last search (null clears). Raised for 'hlsearch' and 'incsearch'.</summary>
    void OnSearchHighlight(VimSearchHighlight? highlight) { }
    /// <summary>The Normal command being typed was a yank: flash the range ('highlightedyank').</summary>
    void OnYankHighlight(TextPosition start, TextPosition end, bool linewise) { }

    // ---------------------------------------------------------------- viewport

    VimViewport GetViewport() => new(0, Math.Max(0, Buffer.LineCount - 1), Math.Max(1, Buffer.LineCount), 0);
    /// <summary>Scroll so that <paramref name="firstLine"/> is the first visible line.</summary>
    void SetTopLine(int firstLine) { }
    /// <summary>Number of display rows <paramref name="line"/> occupies when wrapped (1 when not wrapping).</summary>
    int GetDisplayRows(int line) => 1;
    /// <summary>Width in cells of one display row (0 = no wrapping); used by gj/gk/g0/g$/gm.</summary>
    int WrapWidth => 0;
    /// <summary>Width of the editor window in cells (for gm).</summary>
    int WindowWidth => WrapWidth > 0 ? WrapWidth : 80;

    // ---------------------------------------------------------------- folds

    /// <summary>If <paramref name="line"/> is inside a CLOSED fold returns its extent.</summary>
    bool TryGetClosedFold(int line, out int startLine, out int endLine) { startLine = endLine = line; return false; }
    void FoldCommand(VimFoldCommand command, int startLine, int endLine) { }

    // ---------------------------------------------------------------- clipboard (registers + and *)

    /// <summary>The system clipboard, for a host that does not tell <c>+</c> and <c>*</c> apart. The engine calls <see cref="ReadClipboard(char)"/>.</summary>
    string? ReadClipboard() => null;
    /// <summary>Writes the system clipboard, for a host that does not tell <c>+</c> and <c>*</c> apart. The engine calls <see cref="WriteClipboard(char, string, VimRegisterType)"/>.</summary>
    void WriteClipboard(string text, VimRegisterType type) { }

    /// <summary>
    /// Register <paramref name="register"/>: <c>'+'</c> (the clipboard) or <c>'*'</c> (the selection: PRIMARY on X11; the clipboard where there is
    /// none). Text with <c>'\n'</c> line ends; null or empty when it holds nothing (the engine then uses the unnamed register). The read is
    /// synchronous, inside key handling: a host whose clipboard reads asynchronously answers from what it last read. Defaults to <see cref="ReadClipboard()"/>.
    /// </summary>
    string? ReadClipboard(char register) => ReadClipboard();

    /// <summary>
    /// How the text <see cref="ReadClipboard(char)"/> returned for <paramref name="register"/> was copied, when the host knows it (a Vim yank of its
    /// own, an editor copy of whole lines or a box); null makes the engine decide from the text (a final newline means linewise), as Vim does
    /// for another application's text.
    /// </summary>
    VimRegisterType? ReadClipboardType(char register) => null;

    /// <summary>Writes register <c>'+'</c> or <c>'*'</c> (text with <c>'\n'</c> line ends). Defaults to <see cref="WriteClipboard(string, VimRegisterType)"/>.</summary>
    void WriteClipboard(char register, string text, VimRegisterType type) => WriteClipboard(text, type);

    // ---------------------------------------------------------------- windows, buffers, files

    /// <summary>Performs :w :q :e :bn :sp :vsp Ctrl-W... Returns false if unsupported (the engine then reports an error).</summary>
    bool PerformAction(VimEditorAction action, in VimActionArgs args) => false;

    // ---------------------------------------------------------------- application commands

    /// <summary>Runs an application command (gd -> editor.action.revealDefinition ...). Fire-and-forget; must not block.</summary>
    void ExecuteCommand(string commandId, IReadOnlyList<object?>? args) { }

    // ---------------------------------------------------------------- language hooks

    VimCommentTokens GetCommentTokens() => new("//", "/*", "*/");
    /// <summary>The '=' operator and gq with a formatter: format the lines in place. Return false when no formatter is available (the engine re-indents instead).</summary>
    bool FormatLines(int startLine, int endLine) => false;
    /// <summary>Insert-mode Ctrl-N / Ctrl-P / Ctrl-X completion triggers.</summary>
    void RequestCompletion(VimCompletionKind kind) { }

    /// <summary>
    /// Insert mode: insert text the user typed (a character, or "\n" for Enter) through the editor's own typing path, so its auto-closing
    /// pairs, completion and signature triggers, snippets and language-aware indentation apply. Return false to let the engine insert it.
    /// The engine adopts whatever the editor changed (reported through <see cref="IVimBuffer.TryTakeExternalChanges"/>) as part of the
    /// insert, and the caret from <see cref="GetCaret"/>. Never called for Replace mode or for repeated text (a count, Ctrl-A).
    /// </summary>
    bool InsertTypedText(string text) => false;

    // ---------------------------------------------------------------- time

    IVimTimer Timer { get; }
}
