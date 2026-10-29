using CsCode.Core.Lifecycle;
using CsCode.Core.Text;

namespace CsCode.Vim;

/// <summary>
/// One edit: replace the half-open range [Start, End) with <see cref="Text"/>. Line breaks inside <see cref="Text"/> are
/// ALWAYS "\n" — the buffer implementation converts to its own end-of-line sequence. A struct so the typing path never allocates.
/// </summary>
public readonly record struct VimEdit(TextPosition Start, TextPosition End, string Text);

/// <summary>
/// Line-oriented access to the document Vim edits. Lines are numbered from 0; a buffer always has at least one line
/// (an empty buffer is one empty line, exactly like Vim). Positions are UTF-16 code-unit based (<see cref="TextPosition"/>).
/// All members are called on the host's UI thread from inside <see cref="VimEngine.HandleKey(int)"/> and must be fast and non-blocking.
/// </summary>
public interface IVimBuffer
{
    /// <summary>Display name (file name or "[No Name]").</summary>
    string Name { get; }
    Uri? Uri { get; }
    string LanguageId { get; }

    /// <summary>Monotonic; bumps on every change (including ones the engine itself made).</summary>
    int Version { get; }
    int LineCount { get; }

    /// <summary>Text of a line without its terminator. Implementations should return the same instance until the line changes (no allocation per read).</summary>
    string GetLine(int line);
    int GetLineLength(int line) => GetLine(line).Length;

    /// <summary>End-of-line sequence of the document ("\n" or "\r\n"); only informational — edits always use "\n".</summary>
    string EndOfLine { get; }
    bool IsReadOnly { get; }

    /// <summary>Applies one edit atomically. Returns false (and changes nothing) if the buffer rejects it.</summary>
    bool ApplyEdit(in VimEdit edit);

    /// <summary>
    /// Applies edits atomically; ranges are relative to the CURRENT state and must not overlap. A large batch is the way the engine
    /// performs big operations (<c>:%s</c>, <c>dG</c>, <c>gg=G</c>) so the host can apply them in one pass.
    /// </summary>
    bool ApplyEdits(ReadOnlySpan<VimEdit> edits);

    /// <summary>The host may group everything between these calls into one of ITS undo steps (the engine keeps its own undo tree).</summary>
    void BeginEditGroup() { }
    void EndEditGroup() { }

    /// <summary>
    /// Optional: fires after every change (including the engine's own) so marks can follow external edits. Without it the engine
    /// still detects foreign edits through <see cref="Version"/> and then drops its undo history and clamps marks.
    /// </summary>
    IEventSource<VimBufferChange>? Changed => null;

    // ---------------------------------------------------------------- host integration (optional; the defaults keep the engine self-contained)

    /// <summary>
    /// Optional: moves the changes that something other than this engine made since the last call (the editor's own typing path, an accepted
    /// completion, a formatter, a workspace edit, another engine) into <paramref name="into"/>, oldest first. Return false when the buffer does
    /// not track them. When it does, the engine keeps its undo history across such edits (each becomes an undo step, or joins the change in
    /// progress) instead of dropping it, and marks follow inserted and deleted lines.
    /// </summary>
    bool TryTakeExternalChanges(List<VimExternalChange> into) => false;

    /// <summary>
    /// Optional (a host whose own undo history mirrors the engine's steps): true when the host can undo (<paramref name="redo"/> false) or redo
    /// engine step <paramref name="seq"/> itself. The engine then computes the step's effect without editing and calls <see cref="UndoRedoStep"/>.
    /// </summary>
    bool CanUndoRedoStep(int seq, bool redo) => false;

    /// <summary>
    /// Performs the undo or redo of step <paramref name="seq"/> in the host's history, which must produce exactly the text the engine would.
    /// Return false (with the buffer unchanged) when that is not possible after all: the engine then applies the edits itself.
    /// </summary>
    bool UndoRedoStep(int seq, bool redo) => false;
}

/// <summary>
/// A change the engine did not make: lines [<see cref="StartLine"/>, <see cref="StartLine"/> + <see cref="OldLines"/>.Length) were replaced by
/// <see cref="NewLineCount"/> lines (relative to the state left by the previous change). <see cref="Group"/> 0 joins the change in progress
/// (the key being handled, the Insert session); equal non-zero groups form one undo step of their own.
/// </summary>
public readonly record struct VimExternalChange(int StartLine, string[] OldLines, int NewLineCount, int Group);

/// <summary>A change to a buffer: ordered content changes, each relative to the state left by the previous one (LSP didChange semantics).</summary>
public sealed record VimBufferChange(int Version, IReadOnlyList<TextContentChange> Changes);
