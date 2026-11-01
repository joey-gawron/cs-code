using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Editing;
using CsCode.Editor.Features;
using CsCode.Editor.Threading;

namespace CsCode.Editor.View;

/// <summary>
/// Registers the editor's commands with <see cref="ICommandService"/> under VS Code's ids, so keybindings, menus, the palette, macros and Vim all
/// reach the editor through one table. Each acts on the view that last had focus. Plan-based commands compute their edits with the pure functions
/// in <see cref="EditingOperations"/> (on the editor worker when the document is big) and apply them as ONE undo unit.
/// </summary>
internal static class EditorCommands
{
    /// <summary>Documents above this many characters compute whole-document commands on the worker instead of the UI thread.</summary>
    private const int WorkerThreshold = 150_000;

    public static IDisposable Register(EditorViewFactory factory, ICommandService commands)
    {
        var store = new DisposableStore();

        void Cmd(string id, string title, Func<CodeEditorControl, CommandInvocation, Task> run) =>
            store.Add(commands.Register(
                id,
                async inv =>
                {
                    var view = factory.ActiveView;
                    if (view is null || view.IsDisposed) return null;
                    await factory.Services.Ui.InvokeAsync(() => { }).ConfigureAwait(false); // hop to the UI thread (inline when already there)
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    factory.Services.Ui.Post(async () =>
                    {
                        try
                        {
                            if (!view.IsDisposed) await run(view, inv);
                            tcs.SetResult();
                        }
                        catch (Exception ex)
                        {
                            tcs.SetException(ex);
                        }
                    });
                    await tcs.Task.ConfigureAwait(false);
                    return null;
                },
                new CommandMetadata(id, title, "Editor")));

        void Plan(string id, string title, Func<EditorDocument, TextRange, EditingContext, EditPlan?> compute) =>
            Cmd(id, title, (view, _) => RunPlanAsync(view, factory, compute));

        // ---- comments ----
        Plan("editor.action.commentLine", "Toggle Line Comment", (d, s, c) => EditingOperations.ToggleLineComment(d, s, c));
        Plan("editor.action.addCommentLine", "Add Line Comment", (d, s, c) => AddOrRemove(d, s, c, add: true));
        Plan("editor.action.removeCommentLine", "Remove Line Comment", (d, s, c) => AddOrRemove(d, s, c, add: false));
        Plan("editor.action.blockComment", "Toggle Block Comment", (d, s, c) => EditingOperations.ToggleBlockComment(d, s, c));

        // ---- lines ----
        Plan("editor.action.moveLinesUpAction", "Move Line Up", (d, s, c) => EditingOperations.MoveLines(d, s, c, down: false));
        Plan("editor.action.moveLinesDownAction", "Move Line Down", (d, s, c) => EditingOperations.MoveLines(d, s, c, down: true));
        Plan("editor.action.copyLinesUpAction", "Copy Line Up", (d, s, c) => EditingOperations.CopyLines(d, s, c, down: false));
        Plan("editor.action.copyLinesDownAction", "Copy Line Down", (d, s, c) => EditingOperations.CopyLines(d, s, c, down: true));
        Plan("editor.action.deleteLines", "Delete Line", (d, s, c) => EditingOperations.DeleteLines(d, s, c));
        Plan("editor.action.indentLines", "Indent Line", (d, s, c) => EditingOperations.IndentLines(d, s, c));
        Plan("editor.action.outdentLines", "Outdent Line", (d, s, c) => EditingOperations.OutdentLines(d, s, c));
        Plan("editor.action.joinLines", "Join Lines", (d, s, c) => EditingOperations.JoinLines(d, s, c));
        Plan("editor.action.transposeLetters", "Transpose Letters", (d, s, c) => EditingOperations.TransposeCharacters(d, s, c));
        Plan("editor.action.trimTrailingWhitespace", "Trim Trailing Whitespace", (d, s, _) => EditingOperations.TrimTrailingWhitespace(d, s));
        Plan("editor.action.sortLinesAscending", "Sort Lines Ascending", (d, s, c) => EditingOperations.SortLines(d, s, c, descending: false));
        Plan("editor.action.sortLinesDescending", "Sort Lines Descending", (d, s, c) => EditingOperations.SortLines(d, s, c, descending: true));

        // ---- selection ----
        var shrinkStacks = new System.Runtime.CompilerServices.ConditionalWeakTable<CodeEditorControl, Stack<TextRange>>();
        Cmd("editor.action.smartSelect.expand", "Expand Selection", async (view, _) =>
        {
            var current = view.Controller.Selections[0];
            var stack = shrinkStacks.GetOrCreateValue(view);
            var ctx = view.GetEditingContext();
            var provider = factory.Services.Features.GetProviders<ISelectionRangeProvider>(view.Document).FirstOrDefault();
            TextRange? next = null;
            if (provider is not null)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    var ranges = await provider.ProvideSelectionRangesAsync(view.Document, [current.Start], cts.Token).ConfigureAwait(true);
                    for (var r = ranges?.FirstOrDefault(); r is not null; r = r.Parent)
                        if (Contains(r.Range, current) && r.Range != current)
                        {
                            next = r.Range;
                            break;
                        }
                }
                catch (OperationCanceledException)
                {
                    // slow server: fall back to the heuristic below
                }
            }
            next ??= SelectionExpander.Expand(view.Document, current, ctx.Language);
            if (next is not { } n) return;
            stack.Push(current);
            view.SetSelection(n);
        });
        Cmd("editor.action.smartSelect.shrink", "Shrink Selection", (view, _) =>
        {
            if (shrinkStacks.TryGetValue(view, out var stack) && stack.Count > 0) view.SetSelection(stack.Pop());
            return Task.CompletedTask;
        });
        Cmd("editor.action.selectAll", "Select All", (view, _) =>
        {
            view.Editor.SelectAll();
            return Task.CompletedTask;
        });

        // ---- multi-cursor (VS Code's ids; MultiCursorController; off while Vim is on) ----
        Cmd(MultiCursorCommands.AddSelectionToNextFindMatch, "Add Selection to Next Find Match", (view, _) => { view.MultiCursor.AddSelectionToNextFindMatch(); return Task.CompletedTask; });
        Cmd(MultiCursorCommands.MoveSelectionToNextFindMatch, "Move Last Selection to Next Find Match", (view, _) => { view.MultiCursor.MoveSelectionToNextFindMatch(); return Task.CompletedTask; });
        Cmd(MultiCursorCommands.SelectHighlights, "Select All Occurrences of Find Match", (view, _) => view.MultiCursor.SelectAllOccurrencesAsync());
        Cmd(MultiCursorCommands.InsertCursorAbove, "Add Cursor Above", (view, _) => { view.MultiCursor.InsertCursor(below: false); return Task.CompletedTask; });
        Cmd(MultiCursorCommands.InsertCursorBelow, "Add Cursor Below", (view, _) => { view.MultiCursor.InsertCursor(below: true); return Task.CompletedTask; });
        Cmd(MultiCursorCommands.CursorUndo, "Cursor Undo", (view, _) => { view.MultiCursor.CursorUndo(); return Task.CompletedTask; });
        Cmd(MultiCursorCommands.RemoveSecondaryCursors, "Remove Secondary Cursors", (view, _) => { view.MultiCursor.RemoveSecondaryCursors(); return Task.CompletedTask; });

        // ---- clipboard (Visual Studio's behaviours: EditorClipboard, ClipboardOperations) and the context menu ----
        Cmd(EditorMenus.Cut, "Cut", (view, _) => view.Clipboard.CutAsync());
        Cmd(EditorMenus.Copy, "Copy", (view, _) => view.Clipboard.CopyAsync());
        Cmd(EditorMenus.CopyWithFormatting, "Copy with Formatting", (view, _) => view.Clipboard.CopyAsync(withFormatting: true));
        Cmd(EditorMenus.Paste, "Paste", (view, _) => view.Clipboard.PasteAsync());
        Cmd(EditorMenus.CycleClipboardRing, "Cycle Clipboard Ring", (view, _) => view.Clipboard.CycleRingAsync());
        Cmd(EditorMenus.ShowContextMenu, "Show Editor Context Menu", (view, _) => { view.ShowContextMenu(); return Task.CompletedTask; });
        Cmd(EditorMenus.InsertSnippet, "Insert Snippet...", (view, _) => view.Feature<ISnippetFeature>()?.PickAndInsertAsync(surround: false) ?? Task.CompletedTask);
        Cmd(EditorMenus.SurroundWithSnippet, "Surround With...", (view, _) => view.Feature<ISnippetFeature>()?.PickAndInsertAsync(surround: true) ?? Task.CompletedTask);

        // ---- debugging from the editor (the right-click menu's Breakpoint submenu, Run To Cursor, Add Watch, QuickWatch) ----
        Cmd(EditorMenus.RunToCursor, "Run To Cursor", (view, _) => EditorMenus.RunToCursorAsync(view));
        Cmd(EditorMenus.AddWatch, "Add Watch", (view, _) => EditorMenus.AddWatchAsync(view));
        Cmd(EditorMenus.QuickWatch, "QuickWatch...", (view, _) => EditorMenus.QuickWatchAsync(view));
        Cmd(EditorMenus.ToggleBreakpointEnabled, "Enable or Disable Breakpoint", (view, _) =>
        {
            var line = view.Controller.Caret.Line;
            if (view.Feature<DebugFeature>() is { } debug && debug.BreakpointAt(line) is { } bp) debug.SetEnabled(line, !bp.Enabled);
            return Task.CompletedTask;
        });
        Cmd(EditorMenus.EditBreakpointCondition, "Breakpoint Conditions...", (view, _) => view.Feature<DebugFeature>()?.EditConditionAsync(view.Controller.Caret.Line) ?? Task.CompletedTask);

        // ---- history / find ----
        Cmd("undo", "Undo", (view, _) => { view.Controller.Undo(); return Task.CompletedTask; });
        Cmd("redo", "Redo", (view, _) => { view.Controller.Redo(); return Task.CompletedTask; });
        Cmd(KnownCommands.Find, "Find", (view, _) => { view.Controller.ShowFind(false); return Task.CompletedTask; });
        Cmd(KnownCommands.Replace, "Replace", (view, _) => { view.Controller.ShowFind(true); return Task.CompletedTask; });

        // ---- go to line ----
        Cmd("workbench.action.gotoLine", "Go to Line", async (view, inv) =>
        {
            var window = factory.Services.Window;
            var count = view.Document.LineCount;
            var text = inv.Arg<string>(0);
            if (text is null && window is not null)
                text = await window.ShowInputBoxAsync(new InputBoxOptions(
                    Title: "Go to Line",
                    Prompt: $"Type a line number between 1 and {count} (optionally :column) to go to",
                    Validate: v => ParseLine(v, count) is null ? $"Enter a number between 1 and {count}" : null));
            if (text is null || ParseLine(text, count) is not { } target) return;
            view.Controller.SetSelection(TextRange.At(target.Line, target.Column), reveal: false);
            view.Controller.Reveal(TextRange.At(target.Line, target.Column), center: true);
            view.Controller.Focus();
        });

        // ---- language-feature commands (no-ops when the feature does not exist for this view, e.g. large-file mode) ----
        Cmd(KnownCommands.TriggerSuggest, "Trigger Suggest", (view, _) => { view.Feature<ICompletionFeature>()?.Trigger(); return Task.CompletedTask; });
        Cmd(KnownCommands.TriggerParameterHints, "Trigger Parameter Hints", (view, _) => { view.Feature<ISignatureFeature>()?.Trigger(); return Task.CompletedTask; });
        Cmd(KnownCommands.QuickFix, "Quick Fix", (view, _) => view.Feature<ICodeActionFeature>()?.ShowQuickFixAsync() ?? Task.CompletedTask);
        Cmd("editor.action.showHover", "Show Hover", (view, _) => view.Feature<HoverFeature>()?.HoverAtAsync(view.Editor.CaretOffset) ?? Task.CompletedTask);
        Cmd("editor.action.marker.next", "Go to Next Problem", (view, _) => GoToProblemAsync(view, forward: true));
        Cmd("editor.action.marker.prev", "Go to Previous Problem", (view, _) => GoToProblemAsync(view, forward: false));
        Cmd("editor.action.organizeImports", "Organize Imports", (view, _) => view.Feature<ICodeActionFeature>()?.OrganizeImportsAsync() ?? Task.CompletedTask);
        Cmd(KnownCommands.GotoDefinition, "Go to Definition", (view, _) => view.Feature<INavigationFeature>()?.GoToDefinitionAsync() ?? Task.CompletedTask);
        Cmd("editor.action.goToDeclaration", "Go to Declaration", (view, _) => view.Feature<INavigationFeature>()?.GoToDefinitionAsync(DefinitionKind.Declaration) ?? Task.CompletedTask);
        Cmd("editor.action.goToTypeDefinition", "Go to Type Definition", (view, _) => view.Feature<INavigationFeature>()?.GoToDefinitionAsync(DefinitionKind.TypeDefinition) ?? Task.CompletedTask);
        Cmd("editor.action.goToImplementation", "Go to Implementation", (view, _) => view.Feature<INavigationFeature>()?.GoToDefinitionAsync(DefinitionKind.Implementation) ?? Task.CompletedTask);
        Cmd(KnownCommands.FindReferences, "Go to References", (view, _) => view.Feature<INavigationFeature>()?.FindReferencesAsync() ?? Task.CompletedTask);
        // ---- peek (an editor embedded under the line: PeekFeature) ----
        Cmd(PeekFeature.PeekDefinitionCommand, "Peek Definition", (view, _) => view.Feature<PeekFeature>()?.PeekDefinitionAsync() ?? Task.CompletedTask);
        Cmd(PeekFeature.PeekDeclarationCommand, "Peek Declaration", (view, _) => view.Feature<PeekFeature>()?.PeekDefinitionAsync(DefinitionKind.Declaration) ?? Task.CompletedTask);
        Cmd(PeekFeature.PeekTypeDefinitionCommand, "Peek Type Definition", (view, _) => view.Feature<PeekFeature>()?.PeekDefinitionAsync(DefinitionKind.TypeDefinition) ?? Task.CompletedTask);
        Cmd(PeekFeature.PeekImplementationCommand, "Peek Implementations", (view, _) => view.Feature<PeekFeature>()?.PeekDefinitionAsync(DefinitionKind.Implementation) ?? Task.CompletedTask);
        Cmd(PeekFeature.PeekReferencesCommand, "Peek References", (view, _) => view.Feature<PeekFeature>()?.PeekReferencesAsync() ?? Task.CompletedTask);
        Cmd(PeekFeature.CloseCommand, "Close Peek", (view, _) => { PeekFeature.CloseFor(view); return Task.CompletedTask; });
        Cmd(LinkedEditingFeature.StartCommand, "Start Linked Editing", (view, _) => view.Feature<LinkedEditingFeature>()?.ToggleAsync() ?? Task.CompletedTask);
        Cmd(KnownCommands.Rename, "Rename Symbol", (view, _) => view.Feature<INavigationFeature>()?.RenameAsync() ?? Task.CompletedTask);
        Cmd(KnownCommands.FormatDocument, "Format Document", (view, _) => view.Feature<IFormattingFeature>()?.FormatDocumentAsync() ?? Task.CompletedTask);
        Cmd("editor.action.formatSelection", "Format Selection", (view, _) => view.Feature<IFormattingFeature>()?.FormatSelectionAsync() ?? Task.CompletedTask);

        // ---- folding ----
        Cmd("editor.fold", "Fold", (view, _) => { view.Feature<IFoldingFeature>()?.Fold(); return Task.CompletedTask; });
        Cmd("editor.foldRecursively", "Fold Recursively", (view, _) => { view.Feature<IFoldingFeature>()?.Fold(recursive: true); return Task.CompletedTask; });
        Cmd("editor.unfold", "Unfold", (view, _) => { view.Feature<IFoldingFeature>()?.Unfold(); return Task.CompletedTask; });
        Cmd("editor.unfoldRecursively", "Unfold Recursively", (view, _) => { view.Feature<IFoldingFeature>()?.Unfold(recursive: true); return Task.CompletedTask; });
        Cmd("editor.toggleFold", "Toggle Fold", (view, _) => { view.Feature<IFoldingFeature>()?.ToggleFold(); return Task.CompletedTask; });
        Cmd("editor.foldAll", "Fold All", (view, _) => { view.Feature<IFoldingFeature>()?.FoldAll(); return Task.CompletedTask; });
        Cmd("editor.unfoldAll", "Unfold All", (view, _) => { view.Feature<IFoldingFeature>()?.UnfoldAll(); return Task.CompletedTask; });

        // ---- snippets / debugging ----
        Cmd("editor.action.insertSnippet", "Insert Snippet", (view, inv) =>
        {
            if (inv.Arg<string>(0) is { } body) view.Feature<ISnippetFeature>()?.Insert(body);
            return Task.CompletedTask;
        });
        Cmd(KnownCommands.DebugToggleBreakpoint, "Toggle Breakpoint", (view, _) =>
        {
            if (factory.Services.Debug is { } debug && view.Document.Uri.IsFile)
                debug.Breakpoints.Toggle(view.Document.Uri, view.Controller.Caret.Line);
            return Task.CompletedTask;
        });
        return store;
    }

    /// <summary>Moves the caret to the next/previous diagnostic (wrapping) and shows its hover, like VS Code's F8 / Shift+F8.</summary>
    private static Task GoToProblemAsync(CodeEditorControl view, bool forward)
    {
        if (view.Feature<DiagnosticsFeature>()?.NextMarker(view.Editor.CaretOffset, forward) is not { } offset) return Task.CompletedTask;
        var at = view.Document.PositionAt(offset);
        view.Controller.SetSelection(new TextRange(at, at), reveal: true);
        return view.Feature<HoverFeature>()?.HoverAtAsync(offset) ?? Task.CompletedTask;
    }

    private static async Task RunPlanAsync(CodeEditorControl view, EditorViewFactory factory, Func<EditorDocument, TextRange, EditingContext, EditPlan?> compute)
    {
        var doc = view.Document;
        if (doc.IsReadOnly) return;
        var selection = view.Controller.Selections[0];
        var ctx = view.GetEditingContext();
        if (doc.Length < WorkerThreshold)
        {
            if (compute(doc, selection, ctx) is { } plan) view.ApplyPlan(plan);
            return;
        }
        // Big document: compute from a snapshot on the editor worker, apply back only if nothing changed meanwhile.
        var version = doc.Version;
        var result = await factory.Worker.RunAsync(WorkPriority.Interactive, _ => compute(doc, selection, ctx)).ConfigureAwait(true);
        if (result is not null && !view.IsDisposed && doc.Version == version) view.ApplyPlan(result);
    }

    private static EditPlan? AddOrRemove(EditorDocument d, TextRange s, EditingContext c, bool add)
    {
        var (first, last) = EditingOperations.LineSpan(s);
        var token = c.Language.LineComment;
        if (string.IsNullOrEmpty(token)) return null;
        var commented = Enumerable.Range(first, last - first + 1).Select(d.GetLineText).Where(t => t.Trim().Length > 0).All(t => t.TrimStart().StartsWith(token, StringComparison.Ordinal));
        // "Add" only acts when not already all commented; "Remove" only when they are
        return commented != add ? EditingOperations.ToggleLineComment(d, s, c) : null;
    }

    private static bool Contains(TextRange outer, TextRange inner) => outer.Start <= inner.Start && outer.End >= inner.End;

    internal static (int Line, int Column)? ParseLine(string text, int lineCount)
    {
        var parts = text.Trim().Split(':', StringSplitOptions.TrimEntries);
        if (!int.TryParse(parts[0], out var line) || line < 1) return null;
        var column = 1;
        if (parts.Length > 1 && (!int.TryParse(parts[1], out column) || column < 1)) return null;
        return (Math.Min(line, lineCount) - 1, column - 1);
    }
}

/// <summary>The multi-cursor command ids (VS Code's).</summary>
internal static class MultiCursorCommands
{
    public const string AddSelectionToNextFindMatch = "editor.action.addSelectionToNextFindMatch";
    public const string MoveSelectionToNextFindMatch = "editor.action.moveSelectionToNextFindMatch";
    public const string SelectHighlights = "editor.action.selectHighlights";
    public const string InsertCursorAbove = "editor.action.insertCursorAbove";
    public const string InsertCursorBelow = "editor.action.insertCursorBelow";
    public const string CursorUndo = "cursorUndo";
    public const string RemoveSecondaryCursors = "removeSecondaryCursors";
}
