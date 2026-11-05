using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Editing;

namespace CsCode.Editor.View;

/// <summary>
/// The line commands at every caret (multi-cursor), as VS Code runs them: Move Line Up/Down, Copy Line Up/Down, Delete Line, Toggle / Add /
/// Remove Line Comment, Indent / Outdent Line. The carets' lines form <i>blocks</i> (the lines a caret's selection covers; carets on one line
/// share it, and for moving, copying and deleting adjacent blocks join, so a column of carets moves as one block); each block gets the
/// single-selection command's edits (<see cref="EditingOperations"/>), all of them ONE batch and undo step, and every caret keeps its column on
/// its line wherever that line went. With one caret the commands run as before (<see cref="EditorCommands"/>).
/// </summary>
internal static class MultiCaretLineCommands
{
    public const string MoveUp = "editor.action.moveLinesUpAction";
    public const string MoveDown = "editor.action.moveLinesDownAction";
    public const string CopyUp = "editor.action.copyLinesUpAction";
    public const string CopyDown = "editor.action.copyLinesDownAction";
    public const string Delete = "editor.action.deleteLines";
    public const string Comment = "editor.action.commentLine";
    public const string AddComment = "editor.action.addCommentLine";
    public const string RemoveComment = "editor.action.removeCommentLine";
    public const string Indent = "editor.action.indentLines";
    public const string Outdent = "editor.action.outdentLines";

    /// <summary>Runs <paramref name="commandId"/> at every caret of <paramref name="view"/> when it has several and the command is one of these; false otherwise.</summary>
    public static bool TryRun(CodeEditorControl view, string commandId)
    {
        if (!view.MultiCursor.IsActive) return false;
        switch (commandId)
        {
            case MoveUp or MoveDown:
                if (!view.Document.IsReadOnly) MoveLines(view, down: commandId == MoveDown);
                return true;
            case CopyUp or CopyDown:
                if (!view.Document.IsReadOnly) CopyLines(view, down: commandId == CopyDown);
                return true;
            case Delete:
                if (!view.Document.IsReadOnly) DeleteLines(view);
                return true;
            case Comment or AddComment or RemoveComment:
                if (!view.Document.IsReadOnly) CommentLines(view, commandId);
                return true;
            case Indent or Outdent:
                if (!view.Document.IsReadOnly) IndentLines(view, outdent: commandId == Outdent);
                return true;
            default:
                return false;
        }
    }

    /// <summary>A run of lines [<see cref="First"/>, <see cref="Last"/>] and the carets on it (indexes into the caret set, in order).</summary>
    internal sealed record Block(int First, int Last, List<int> Carets);

    /// <summary>
    /// The carets' lines as blocks in document order: a caret covers the lines of its selection (a selection ending at a later line's start
    /// leaves that line out, <see cref="EditingOperations.LineSpan"/>); blocks that share a line join, and with <paramref name="joinAdjacent"/>
    /// so do blocks that touch.
    /// </summary>
    internal static List<Block> Blocks(EditorDocument doc, CaretSet set, bool joinAdjacent)
    {
        var blocks = new List<Block>();
        for (var i = 0; i < set.Count; i++)
        {
            var c = set[i];
            var (first, last) = EditingOperations.LineSpan(new TextRange(doc.PositionAt(c.Start), doc.PositionAt(c.End)));
            if (blocks.Count > 0 && (first <= blocks[^1].Last || (joinAdjacent && first == blocks[^1].Last + 1)))
            {
                var b = blocks[^1];
                b.Carets.Add(i);
                blocks[^1] = b with { Last = Math.Max(b.Last, last) };
            }
            else blocks.Add(new Block(first, last, [i]));
        }
        return blocks;
    }

    /// <summary>A selection the single-selection commands read as exactly lines <paramref name="first"/>..<paramref name="last"/>.</summary>
    private static TextRange LinesOf(int first, int last) => new(new TextPosition(first, 0), new TextPosition(last, 1));

    private static OffsetEdit ToOffsets(EditorDocument doc, TextEdit e) => new(doc.OffsetAt(e.Range.Start), doc.OffsetAt(e.Range.End), e.NewText);

    private static (TextPosition Anchor, TextPosition Active) Positions(EditorDocument doc, CaretRange c) => (doc.PositionAt(c.Anchor), doc.PositionAt(c.Active));

    private static (TextPosition Anchor, TextPosition Active) ShiftLines((TextPosition Anchor, TextPosition Active) p, int lines) =>
        (p.Anchor with { Line = p.Anchor.Line + lines }, p.Active with { Line = p.Active.Line + lines });

    private static void MoveLines(CodeEditorControl view, bool down)
    {
        var doc = view.Document;
        var set = view.MultiCursor.Current;
        var ctx = view.GetEditingContext();
        var edits = new List<OffsetEdit>();
        var after = set.Carets.Select(c => Positions(doc, c)).ToArray();
        foreach (var block in Blocks(doc, set, joinAdjacent: true))
        {
            // a block at the edge stays where it is (VS Code); the others move
            if (EditingOperations.MoveLines(doc, LinesOf(block.First, block.Last), ctx, down) is not { } plan) continue;
            edits.AddRange(plan.Edits.Select(e => ToOffsets(doc, e)));
            foreach (var i in block.Carets) after[i] = ShiftLines(after[i], down ? 1 : -1);
        }
        if (edits.Count > 0) view.MultiCursor.ApplyLineEdits(set, edits, after, set.PrimaryIndex);
    }

    private static void CopyLines(CodeEditorControl view, bool down)
    {
        var doc = view.Document;
        var set = view.MultiCursor.Current;
        var ctx = view.GetEditingContext();
        var edits = new List<OffsetEdit>();
        var after = set.Carets.Select(c => Positions(doc, c)).ToArray();
        var added = 0; // lines the blocks before this one added
        foreach (var block in Blocks(doc, set, joinAdjacent: true))
        {
            if (EditingOperations.CopyLines(doc, LinesOf(block.First, block.Last), ctx, down) is not { } plan) continue;
            edits.AddRange(plan.Edits.Select(e => ToOffsets(doc, e)));
            var count = block.Last - block.First + 1;
            // down: the carets go with the lower copy; up: they stay on the upper one (VS Code)
            foreach (var i in block.Carets) after[i] = ShiftLines(after[i], added + (down ? count : 0));
            added += count;
        }
        if (edits.Count > 0) view.MultiCursor.ApplyLineEdits(set, edits, after, set.PrimaryIndex);
    }

    private static void DeleteLines(CodeEditorControl view)
    {
        var doc = view.Document;
        var set = view.MultiCursor.Current;
        var ctx = view.GetEditingContext();
        var edits = new List<OffsetEdit>();
        var after = new List<(TextPosition, TextPosition)>();
        var primary = 0;
        var removed = 0; // lines the blocks before this one removed
        foreach (var block in Blocks(doc, set, joinAdjacent: true))
        {
            if (EditingOperations.DeleteLines(doc, LinesOf(block.First, block.Last), ctx) is not { } plan) continue;
            edits.AddRange(plan.Edits.Select(e => ToOffsets(doc, e)));
            // one caret per block, on the line that takes the block's place, at its first caret's column (clamped when the line is shorter)
            var first = doc.PositionAt(set[block.Carets[0]].Active);
            var line = plan.Selection.Start.Line - removed;
            if (block.Carets.Contains(set.PrimaryIndex)) primary = after.Count;
            var at = new TextPosition(Math.Max(0, line), first.Character);
            after.Add((at, at));
            removed += block.Last - block.First + 1;
        }
        if (edits.Count > 0) view.MultiCursor.ApplyLineEdits(set, edits, after, primary);
    }

    private static void CommentLines(CodeEditorControl view, string commandId)
    {
        var doc = view.Document;
        var set = view.MultiCursor.Current;
        var ctx = view.GetEditingContext();
        var token = ctx.Language.LineComment;
        var textEdits = new List<TextEdit>();
        foreach (var block in Blocks(doc, set, joinAdjacent: false))
        {
            var lines = LinesOf(block.First, block.Last);
            if (commandId != Comment && !string.IsNullOrEmpty(token))
            {
                // Add only comments lines that are not all commented, Remove only uncomments lines that are (EditorCommands' rule)
                var commented = Enumerable.Range(block.First, block.Last - block.First + 1).Select(doc.GetLineText).Where(t => t.Trim().Length > 0)
                    .All(t => t.TrimStart().StartsWith(token, StringComparison.Ordinal));
                if (commented == (commandId == AddComment)) continue;
            }
            else if (commandId != Comment) continue; // add/remove need a line comment token
            if (EditingOperations.ToggleLineComment(doc, lines, ctx) is { } plan) textEdits.AddRange(plan.Edits);
        }
        ApplyInPlace(view, set, textEdits);
    }

    private static void IndentLines(CodeEditorControl view, bool outdent)
    {
        var doc = view.Document;
        var set = view.MultiCursor.Current;
        var ctx = view.GetEditingContext();
        var textEdits = new List<TextEdit>();
        foreach (var block in Blocks(doc, set, joinAdjacent: false))
        {
            var lines = LinesOf(block.First, block.Last);
            var plan = outdent ? EditingOperations.OutdentLines(doc, lines, ctx) : EditingOperations.IndentLines(doc, lines, ctx);
            if (plan is not null) textEdits.AddRange(plan.Edits);
        }
        ApplyInPlace(view, set, textEdits);
    }

    /// <summary>Edits within lines (comment markers, indentation): every caret stays on its line, its columns moved along the edits on it.</summary>
    private static void ApplyInPlace(CodeEditorControl view, CaretSet set, List<TextEdit> textEdits)
    {
        if (textEdits.Count == 0) return;
        var doc = view.Document;
        var after = set.Carets.Select(c =>
        {
            var (anchor, active) = Positions(doc, c);
            return (EditingOperations.Map(anchor, textEdits), EditingOperations.Map(active, textEdits));
        }).ToArray();
        view.MultiCursor.ApplyLineEdits(set, textEdits.Select(e => ToOffsets(doc, e)).ToArray(), after, set.PrimaryIndex);
    }
}
