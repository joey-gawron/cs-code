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

    // ---------------------------------------------------------------- folds

    /// <summary>If <paramref name="line"/> is inside a CLOSED fold returns its extent.</summary>
    bool TryGetClosedFold(int line, out int startLine, out int endLine) { startLine = endLine = line; return false; }
    void FoldCommand(VimFoldCommand command, int startLine, int endLine) { }

    // ---------------------------------------------------------------- clipboard (registers + and *)

    string? ReadClipboard() => null;
    void WriteClipboard(string text, VimRegisterType type) { }

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

    // ---------------------------------------------------------------- time

    IVimTimer Timer { get; }
}
