using System.Collections;
using System.Reflection;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace CsCode.Editor.Documents;

/// <summary>
/// AvaloniaEdit 12 (like AvalonEdit before it) records, with every edit, a <c>RestoreCaretAndSelectionUndoAction</c> in the document's undo stack. It
/// keeps the <c>TextArea</c> by a weak reference — but also the <em>Selection</em> object, which holds the <c>TextArea</c> STRONGLY. So the undo history
/// of a document pins every text area that ever edited it (and through it the whole view: renderers, visual lines, popups) until those history
/// entries scroll off the undo limit. With a shell that destroys the views of inactive tabs while their documents stay open, that is exactly the
/// "memory is not flat" leak this module must not have.
///
/// On disposal of a view the entries it created are scrubbed: their selection reference is cleared (they then restore nothing, which is right — the
/// view is gone; the weak reference to the text area is dead or about to be). Done with reflection against the known private layout of
/// AvaloniaEdit 12.0.0; if the layout is different the scrub silently does nothing (and the leak test in this module's suite says so).
/// </summary>
internal static class UndoStackScrubber
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static readonly FieldInfo? UndoStackField = typeof(UndoStack).GetField("_undostack", Flags);
    private static readonly FieldInfo? RedoStackField = typeof(UndoStack).GetField("_redostack", Flags);

    /// <summary>Clears the selection held by every caret/selection undo entry that belongs to <paramref name="textArea"/>. Returns how many were scrubbed.</summary>
    public static int Scrub(TextDocument document, TextArea textArea)
    {
        var scrubbed = 0;
        try
        {
            var stack = document.UndoStack;
            foreach (var field in new[] { UndoStackField, RedoStackField })
            {
                if (field?.GetValue(stack) is not IEnumerable operations) continue;
                foreach (var op in operations)
                    if (op is not null) scrubbed += ScrubOperation(op, textArea);
            }
        }
        catch (Exception)
        {
            // a different AvaloniaEdit layout: leave the history alone (it is only a memory optimisation)
        }
        return scrubbed;
    }

    /// <summary>An undo entry is an operation or a group of them (groups nest: a typing burst inside a command inside…).</summary>
    private static int ScrubOperation(object operation, TextArea textArea)
    {
        if (operation.GetType().GetField("_undolist", Flags)?.GetValue(operation) is not IEnumerable group) return ScrubOne(operation, textArea);
        var scrubbed = 0;
        foreach (var inner in group)
            if (inner is not null) scrubbed += ScrubOperation(inner, textArea);
        return scrubbed;
    }

    private static int ScrubOne(object operation, TextArea textArea)
    {
        var type = operation.GetType();
        if (type.Name != "RestoreCaretAndSelectionUndoAction") return 0;
        var reference = type.GetField("_textAreaReference", Flags)?.GetValue(operation) as WeakReference;
        if (reference is null || (reference.Target is { } target && !ReferenceEquals(target, textArea))) return 0;
        var selection = type.GetField("_selection", Flags);
        if (selection is null) return 0;
        selection.SetValue(operation, null);
        return 1;
    }
}
