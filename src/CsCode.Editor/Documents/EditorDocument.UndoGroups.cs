using AvaloniaEdit.Document;
using CsCode.Core.Lifecycle;

namespace CsCode.Editor.Documents;

/// <summary>
/// Undo groups held open across dispatcher callbacks (<see cref="OpenUndoGroup"/>): every change of the document until the group is closed
/// becomes ONE undo step, however many callbacks, keys, typed runs and commands made it (macro playback: docs/MACROS.md, "Undo").
/// <para>
/// AvaloniaEdit cannot undo while one of its undo groups is open (its <c>UndoStack.Undo</c> throws and forgets the open groups), so while the
/// group is held this document keeps a history of its own of the changes made inside it, in the steps the editor makes without a held group (a
/// typed run, one command's edit, one key's change: the same boundaries, read from the typing path's and modal layers' groups through
/// <see cref="BeginUndoGroup"/> and from the buffer's update events). Undo / Redo meanwhile (the editor's commands, Ctrl+Z / Ctrl+Y on the text
/// area, <see cref="Undo"/>) reverse and re-apply those steps as ordinary edits inside the held group, newest first; they never reach the history
/// from before the group, so undoing the closed group always restores the text exactly as it was when the group opened.
/// </para>
/// <para>
/// Dirty state: AvaloniaEdit recomputes "is this the saved text" only when its outermost group closes, so while a group is held the document
/// turns dirty on its first change itself, a save inside the group marks it clean, and closing the group settles the marker (a save inside the
/// group cannot be reached by undo any more). A reload from disk (clean document, file changed outside) clears the history and keeps the group
/// open with the reloaded text as its start.
/// </para>
/// UI thread only (the buffer's thread), like every write.
/// </summary>
public sealed partial class EditorDocument
{
    private int _holds;           // open handles of the held group
    private int _holdGeneration;  // bumped when the held group closes: handles of an earlier one do nothing
    private int _explicitGroups;  // BeginUndoGroup nesting (typing bursts, modal layers), held or not
    private HeldHistory? _held;
    private bool _heldTopLevelUpdate;

    /// <summary>Whether an undo group is held open (<see cref="OpenUndoGroup"/>).</summary>
    public bool HasOpenUndoGroup => _held is not null;

    /// <summary>
    /// Holds an undo group open until the result is disposed: every change of the document meanwhile, in any view and over any number of
    /// dispatcher callbacks, becomes ONE undo step. Handles nest (the group closes when the last one is disposed); disposing twice, or after the
    /// document was closed, does nothing. Typing just before does not continue into the group. UI thread.
    /// </summary>
    public IDisposable OpenUndoGroup()
    {
        if (!_ui.CheckAccess()) throw new InvalidOperationException("Undo groups are opened on the UI thread.");
        if (_disposed) return Disposable.Empty;
        if (_holds++ == 0) BeginHeldGroup();
        var generation = _holdGeneration;
        return Disposable.Create(() => ReleaseHeldGroup(generation));
    }

    private void ReleaseHeldGroup(int generation)
    {
        if (!_ui.CheckAccess())
        {
            _ui.Post(() => ReleaseHeldGroup(generation));
            return;
        }
        if (generation != _holdGeneration || _holds == 0) return;
        if (--_holds == 0) CloseHeldUndoGroup();
    }

    private void BeginHeldGroup()
    {
        BreakContinuation(); // a typing burst right before must not continue into the group
        _text.UndoStack.StartUndoGroup();
        _held = new HeldHistory(_explicitGroups);
    }

    /// <summary>
    /// The next change starts an undo step of its own: a typed run does not continue the last one, inside a held group too (macro playback
    /// calls this before every step, so a step undone inside the playback is what that step did). UI thread.
    /// </summary>
    public void StartNewUndoStep()
    {
        if (!_ui.CheckAccess()) throw new InvalidOperationException("Undo steps are marked on the UI thread.");
        if (_disposed) return;
        if (_held is { } held) held.CanContinue = false;
        else BreakContinuation();
    }

    /// <summary>The next typed run starts an undo step of its own (AvaloniaEdit continues the last step otherwise): an empty group ends that.</summary>
    private void BreakContinuation()
    {
        if (_explicitGroups != 0 || _text.IsInUpdate) return;
        _text.UndoStack.StartUndoGroup();
        _text.UndoStack.EndUndoGroup();
    }

    /// <summary>Closes the held group now, whatever handles are still open (the document closes, or the last handle went).</summary>
    private void CloseHeldUndoGroup()
    {
        if (_held is not { } held) return;
        _held = null;
        _holds = 0;
        _holdGeneration++;
        _heldTopLevelUpdate = false;
        try { _text.UndoStack.EndUndoGroup(); }
        catch (InvalidOperationException)
        {
            // Code outside this class undid or cleared the history with the group open: AvaloniaEdit threw there and dropped its open
            // groups, so there is nothing left to close.
        }
        BreakContinuation(); // typing right after the group is a step of its own too
        if (held.SavedVersion is { } saved)
        {
            // The group spans a save: the saved text is inside the one undo step and cannot be reached by undo. AvaloniaEdit's marker
            // arithmetic does not know that (it can land on "saved" for different text), so settle it here.
            if (Version == saved) _text.UndoStack.MarkAsOriginalFile();
            else _text.UndoStack.DiscardOriginalFileMarker();
            UpdateDirty(!_text.UndoStack.IsOriginalFile);
        }
    }

    /// <summary>Clears the undo history (a reload from disk); a held group stays open, starting from the text as it is now.</summary>
    private void ClearUndoHistory()
    {
        if (_held is null)
        {
            _text.UndoStack.ClearAll();
            return;
        }
        try { _text.UndoStack.EndUndoGroup(); }
        catch (InvalidOperationException) { /* already dropped (see CloseHeldUndoGroup) */ }
        _text.UndoStack.ClearAll();
        _text.UndoStack.StartUndoGroup();
        _held = new HeldHistory(_explicitGroups);
        _heldTopLevelUpdate = false;
    }

    /// <summary>The text was saved (UI thread, the save's version is still current).</summary>
    private void MarkSaved()
    {
        _text.UndoStack.MarkAsOriginalFile();
        if (_held is not { } held) return;
        held.SavedVersion = Version;
        UpdateDirty(false); // the stack may think it was clean all along (it is not recomputed while the group is open): no event comes
    }

    // ---- the steps inside the held group (called from the buffer's events and the group API) ----

    private void HeldGroupStarting(bool continueLast)
    {
        if (_held is not { Applying: false } h || _explicitGroups != h.Base || _text.IsInUpdate) return;
        h.StartStep(continueLast);
    }

    private void HeldGroupEnded()
    {
        if (_held is not { Applying: false } h || _explicitGroups != h.Base || _text.IsInUpdate) return;
        h.EndStep();
    }

    private void HeldUpdateStarted()
    {
        // an update outside any explicit group is a step of its own (AvaloniaEdit: one undo group per outermost update)
        if (_held is not { Applying: false } h || _explicitGroups != h.Base) return;
        h.StartStep(continueLast: false);
        _heldTopLevelUpdate = true;
    }

    private void HeldUpdateFinished(bool changed)
    {
        if (_held is null) return;
        if (_heldTopLevelUpdate)
        {
            _heldTopLevelUpdate = false;
            _held.EndStep();
        }
        if (changed && !_isDirty) UpdateDirty(true); // AvaloniaEdit says so only when the group closes
    }

    private void HeldChanging(DocumentChangeEventArgs e)
    {
        if (_held is not { Applying: false } h) return;
        h.Add(new HeldChange(e.Offset, e.RemovalLength == 0 ? string.Empty : e.RemovedText.Text, e.InsertedText.Text), _text.UndoStack.SizeLimit);
    }

    /// <summary>
    /// Undo (or redo) of the last step made inside the held group, applied as an edit inside it. Returns the offset where the caret belongs
    /// (after the restored text), or null when there is nothing of the group's to undo (redo) or no group is held.
    /// </summary>
    internal int? UndoRedoHeld(bool redo)
    {
        if (_held is not { } h || h.InStep || _text.IsInUpdate) return null;
        var from = redo ? h.Redo : h.Undo;
        if (from.Count == 0) return null;
        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        var caret = 0;
        h.Applying = true;
        _text.BeginUpdate();
        try
        {
            if (redo)
            {
                foreach (var c in step.Changes)
                {
                    _text.Replace(c.Offset, c.Removed.Length, c.Inserted);
                    caret = c.Offset + c.Inserted.Length;
                }
            }
            else
            {
                for (var i = step.Changes.Count - 1; i >= 0; i--)
                {
                    var c = step.Changes[i];
                    _text.Replace(c.Offset, c.Inserted.Length, c.Removed);
                    caret = c.Offset + c.Removed.Length;
                }
            }
        }
        finally
        {
            _text.EndUpdate();
            h.Applying = false;
        }
        (redo ? h.Undo : h.Redo).Add(step);
        h.CanContinue = false; // as after AvaloniaEdit's own undo: typing next starts a step of its own
        return caret;
    }

    /// <summary>One change as AvaloniaEdit reported it (sequential: its offset is against the text the change applied to).</summary>
    private readonly record struct HeldChange(int Offset, string Removed, string Inserted);

    private sealed class HeldStep
    {
        public readonly List<HeldChange> Changes = new(1);
    }

    /// <summary>The steps made inside the held group: an undo and a redo list, bounded like the editor's own history.</summary>
    private sealed class HeldHistory(int baseDepth)
    {
        /// <summary>The explicit group depth outside the held group: a group or update that starts at this depth is a step.</summary>
        public readonly int Base = baseDepth;
        public readonly List<HeldStep> Undo = new();
        public readonly List<HeldStep> Redo = new();
        /// <summary>The step the changes of the open step-level group go to (null: the next change starts one).</summary>
        private HeldStep? _current;
        public bool InStep { get; private set; }
        /// <summary>The last step may be continued (AvaloniaEdit's own rule: after a group that changed something or continued one).</summary>
        public bool CanContinue { get; set; }
        /// <summary>An undo or redo of this history is being applied: its changes are not a step.</summary>
        public bool Applying { get; set; }
        /// <summary>The version saved inside the group, if any.</summary>
        public int? SavedVersion { get; set; }

        public void StartStep(bool continueLast)
        {
            InStep = true;
            _current = continueLast && CanContinue && Undo.Count > 0 ? Undo[^1] : null;
        }

        public void EndStep()
        {
            CanContinue = _current is not null;
            _current = null;
            InStep = false;
        }

        public void Add(HeldChange change, int limit)
        {
            if (_current is null)
            {
                _current = new HeldStep();
                Undo.Add(_current);
                if (Undo.Count > Math.Max(1, limit)) Undo.RemoveAt(0); // the oldest step cannot be undone inside the group any more
            }
            _current.Changes.Add(change);
            Redo.Clear();
        }
    }
}
