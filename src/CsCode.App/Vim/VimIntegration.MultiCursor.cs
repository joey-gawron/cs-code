using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor;
using CsCode.Vim;

namespace CsCode.App.Vim;

/// <summary>
/// Multi-cursor with Vim on, in the spirit of VSCodeVim's multi-cursor mode, without changing the engine: the engine runs every key at its one
/// cursor (the editor's primary caret) and this part places the other carets around it.
/// <list type="bullet">
/// <item><b>Creating carets</b> is the editor's (Add Next Occurrence, <c>gb</c>, Select All Occurrences, Insert Cursor Above/Below, Alt+Click);
/// carets with selections put the engine in Visual mode at the primary.</item>
/// <item><b>Normal mode</b>: <c>i a I A o O</c> enter Insert mode at every caret; Esc leaves one caret (VSCodeVim); any other key first collapses
/// to the primary, then runs there.</item>
/// <item><b>Visual mode</b> (selections at every caret): <c>c s</c> change and <c>d x</c> delete every selection, Esc keeps a caret at each; <c>gb</c>
/// adds the next occurrence; any other key collapses to the primary first.</item>
/// <item><b>Insert mode</b>: typing (through the editor's typing path), Enter, Backspace, Delete, Tab and the caret keys act at every caret (the
/// key policy gives the last ones to the editor); Esc returns to Normal mode with every caret one character left, as Vim's cursor goes; other
/// Insert-mode Ctrl keys collapse to the primary first.</item>
/// </list>
/// The edits made at the other carets come right after the engine's key, before any other key: the buffer reports them as part of that key's
/// change (<see cref="EditorVimBuffer.BeginEditorKey"/>), so the engine adopts them into the same Vim change and they join its undo group in the
/// editor: one <c>u</c> (or Ctrl+Z) undoes a change made at every caret.
/// </summary>
public sealed partial class VimIntegration
{
    /// <summary>The editor commands that leave selections at the carets: after them the engine enters Visual mode at the primary.</summary>
    private static bool SelectsAtCarets(string commandId) =>
        commandId is "editor.action.addSelectionToNextFindMatch" or "editor.action.moveSelectionToNextFindMatch" or "editor.action.selectHighlights";

    /// <summary>A key (or typed text) while the view has several carets. True when it was handled here; false: the engine takes it as usual.</summary>
    private bool MultiCaretKey(VimView v, KeyStroke? key, string? text)
    {
        var engine = v.Engine;
        var mode = engine.Mode;
        var pending = engine.PendingKeys;
        var escape = key is { Key: "escape", Ctrl: false, Alt: false, Shift: false, Meta: false };
        if (mode is VimMode.Insert or VimMode.Replace)
        {
            if (mode == VimMode.Insert && escape)
            {
                LeaveInsertAtCarets(v);
                return true;
            }
            // typed text and Enter go through the editor's typing path, which types at every caret; any other key Vim takes (its Ctrl keys)
            // acts at the primary alone
            if (key is not null && mode == VimMode.Insert && key is not { Key: "enter", Ctrl: false, Alt: false, Meta: false }) v.Surface.CollapseCarets();
            else if (mode == VimMode.Replace) v.Surface.CollapseCarets();
            return false;
        }
        if (pending.Length == 0 && mode == VimMode.Normal)
        {
            if (escape)
            {
                v.Surface.CollapseCarets(); // VSCodeVim: Esc in Normal mode leaves one cursor
                return false;
            }
            if (text is "i" or "a" or "A" or "I" or "o" or "O")
            {
                EnterInsertAtCarets(v, text[0]);
                return true;
            }
            if (text == "g") return false; // gb (add next occurrence) may follow
        }
        else if (pending.Length == 0 && mode is VimMode.Visual or VimMode.VisualLine or VimMode.VisualBlock)
        {
            if (escape)
            {
                LeaveVisualAtCarets(v);
                return true;
            }
            if (text is "c" or "s" or "d" or "x")
            {
                DeleteSelectionsAtCarets(v, text);
                return true;
            }
            if (text == "g") return false;
        }
        else if (pending == "g" && text == "b") return false;
        // anything else runs at the primary alone: the other carets go first, as a click on the text takes them away
        v.Surface.CollapseCarets();
        return false;
    }

    /// <summary>
    /// After the engine's key, the edits at the other carets: the buffer attributes them to that key's change (the engine adopts them at the
    /// next key, before it closes the change) and they join its undo group in the editor.
    /// </summary>
    private static void EditOtherCarets(VimView v, Func<EditorCaretState, (TextRange Range, string Text, int CaretInText)?> edit)
    {
        var primary = v.Surface.PrimaryCaretIndex;
        v.Buffer.SetActiveEngine(v.Engine);
        v.Buffer.BeginEditorKey();
        try { v.Surface.EditAtCarets((i, c) => i == primary ? null : edit(c)); }
        finally { v.Buffer.EndEditorKey(); }
    }

    /// <summary>The carets moved by <paramref name="move"/>, all but the primary (the engine's cursor, which is where the engine put it).</summary>
    private static void MoveOtherCarets(VimView v, Func<EditorCaretState, TextPosition> move)
    {
        var carets = v.Surface.Carets;
        var primary = v.Surface.PrimaryCaretIndex;
        var moved = new EditorCaretState[carets.Count];
        for (var i = 0; i < moved.Length; i++)
        {
            var to = i == primary ? carets[i].Active : move(carets[i]);
            moved[i] = new EditorCaretState(to, to);
        }
        v.Surface.SetCarets(moved, primary);
    }

    /// <summary><c>i a I A o O</c> with several carets: the engine enters Insert mode at the primary, the other carets go where it would put them.</summary>
    private void EnterInsertAtCarets(VimView v, char command)
    {
        var doc = v.Surface.Document;
        using (v.Surface.HoldCarets())
        {
            Run(v, () => v.Engine.HandleText(command.ToString()));
            if (v.IsDisposed) return;
            if (v.Engine.Mode != VimMode.Insert)
            {
                v.Surface.CollapseCarets(); // a read-only document: no Insert mode
                return;
            }
            var eol = doc.EndOfLine;
            switch (command)
            {
                case 'a':
                    MoveOtherCarets(v, c => c.Active.Character < doc.GetLineText(c.Active.Line).Length ? c.Active with { Character = c.Active.Character + 1 } : c.Active);
                    break;
                case 'A':
                    MoveOtherCarets(v, c => c.Active with { Character = doc.GetLineText(c.Active.Line).Length });
                    break;
                case 'I':
                    MoveOtherCarets(v, c => c.Active with { Character = Indentation(doc.GetLineText(c.Active.Line)).Length });
                    break;
                case 'o':
                    // a new line below with the line's indentation (autoindent), the caret at its end
                    EditOtherCarets(v, c =>
                    {
                        var line = doc.GetLineText(c.Active.Line);
                        var text = eol + Indentation(line);
                        return (TextRange.At(c.Active.Line, line.Length), text, text.Length);
                    });
                    break;
                case 'O':
                    EditOtherCarets(v, c =>
                    {
                        var indent = Indentation(doc.GetLineText(c.Active.Line));
                        return (TextRange.At(c.Active.Line, 0), indent + eol, indent.Length);
                    });
                    break;
            }
        }
    }

    /// <summary>Esc in Insert mode with several carets: the engine returns to Normal mode, every caret one character left (not past the line's start).</summary>
    private void LeaveInsertAtCarets(VimView v)
    {
        var doc = v.Surface.Document;
        using (v.Surface.HoldCarets())
        {
            Run(v, () => v.Engine.HandleKey(new KeyStroke("escape")));
            if (v.IsDisposed) return;
            MoveOtherCarets(v, c => NormalModeCaret(doc, c.Active with { Character = Math.Max(0, c.Active.Character - 1) }));
        }
    }

    /// <summary>Esc in Visual mode with several selections: Normal mode with a caret on the character each selection's cursor was on.</summary>
    private void LeaveVisualAtCarets(VimView v)
    {
        var doc = v.Surface.Document;
        var carets = v.Surface.Carets; // before the engine clears the primary's selection
        using (v.Surface.HoldCarets())
        {
            Run(v, () => v.Engine.HandleKey(new KeyStroke("escape")));
            if (v.IsDisposed) return;
            MoveOtherCarets(v, c => NormalModeCaret(doc, VimCursorOf(doc, Find(carets, c) ?? c)));
        }
    }

    /// <summary>
    /// <c>c s</c> (change) and <c>d x</c> (delete) in Visual mode with several selections: the engine does it at the primary (registers, mode),
    /// every other selection is deleted in the same change; after <c>c s</c> Insert mode types at every caret.
    /// </summary>
    private void DeleteSelectionsAtCarets(VimView v, string command)
    {
        var doc = v.Surface.Document;
        using (v.Surface.HoldCarets())
        {
            Run(v, () => v.Engine.HandleText(command));
            if (v.IsDisposed) return;
            EditOtherCarets(v, c => c.Anchor == c.Active ? null : (Ordered(c), "", 0));
            if (v.Engine.Mode != VimMode.Insert) MoveOtherCarets(v, c => NormalModeCaret(doc, c.Active));
        }
    }

    /// <summary>A command of the editor that leaves selections at the carets ran: the engine shows the primary's as Visual mode (VSCodeVim does).</summary>
    private void SelectionsToVisual()
    {
        if (!_enabled || _disposed || _current is not { IsDisposed: false } v) return;
        var engine = v.Engine;
        if (engine.Mode is not (VimMode.Normal or VimMode.Visual) || engine.PendingKeys.Length != 0) return;
        var carets = v.Surface.Carets;
        var p = carets[v.Surface.PrimaryCaretIndex];
        if (p.Anchor == p.Active) return;
        // the engine's selection includes both end characters: the editor's end is after the last one
        var forward = p.Anchor < p.Active;
        var anchor = forward ? p.Anchor : Before(v, p.Anchor);
        var active = forward ? Before(v, p.Active) : p.Active;
        // held: the engine drawing its selection moves the primary, and the other carets keep their selections
        using (v.Surface.HoldCarets()) Run(v, () => engine.SelectVisual(anchor, active) ? VimKeyResult.Handled : VimKeyResult.PassThrough);
    }

    private static EditorCaretState? Find(IReadOnlyList<EditorCaretState> carets, EditorCaretState now)
    {
        foreach (var c in carets)
            if (c.Active == now.Active || c.Anchor == now.Anchor) return c;
        return null;
    }

    /// <summary>Where Vim's cursor is in a selection: on its last character when it runs forwards, else at its start.</summary>
    private static TextPosition VimCursorOf(Core.Text.IEditorDocument doc, EditorCaretState c)
    {
        if (c.Anchor == c.Active || c.Active < c.Anchor) return c.Active;
        if (c.Active.Character > 0) return c.Active with { Character = c.Active.Character - 1 };
        return c.Active.Line > 0 ? new TextPosition(c.Active.Line - 1, Math.Max(0, doc.GetLineText(c.Active.Line - 1).Length - 1)) : c.Active;
    }

    /// <summary>Normal mode's cursor is on a character: never after the last one of a non-empty line.</summary>
    private static TextPosition NormalModeCaret(Core.Text.IEditorDocument doc, TextPosition p)
    {
        var length = doc.GetLineText(Math.Clamp(p.Line, 0, doc.LineCount - 1)).Length;
        return p with { Character = Math.Clamp(p.Character, 0, Math.Max(0, length - 1)) };
    }

    private static TextRange Ordered(EditorCaretState c) => c.Anchor < c.Active ? new TextRange(c.Anchor, c.Active) : new TextRange(c.Active, c.Anchor);

    private static string Indentation(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] is ' ' or '\t') i++;
        return line[..i];
    }
}
