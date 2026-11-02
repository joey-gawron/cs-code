using CsCode.Core.Contributions;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Features;

namespace CsCode.Editor.View;

/// <summary>
/// What the editor adds to the shell's menus and keymap: the items of the editor's right-click menu (<see cref="MenuIds.EditorContext"/>, Visual
/// Studio 2022's wording and order), the clipboard keys, and the debug and snippet commands those items run. The shell builds the menu from these
/// contributions; it does not know the items. Visibility follows context keys the editor keeps current (<see cref="PrepareContextKeys"/>):
/// an item that cannot apply (no language-server capability, not debugging, read-only document) is hidden, not shown disabled.
/// </summary>
internal static class EditorMenus
{
    public const string ShowContextMenu = "editor.action.showContextMenu";
    public const string Cut = "editor.action.clipboardCutAction";
    public const string Copy = "editor.action.clipboardCopyAction";
    /// <summary>VS Code's Copy With Syntax Highlighting: the text plus HTML with the theme's colours (<see cref="Editing.HtmlClipboard"/>).</summary>
    public const string CopyWithFormatting = "editor.action.clipboardCopyWithSyntaxHighlightingAction";
    public const string Paste = "editor.action.clipboardPasteAction";
    public const string CycleClipboardRing = "cscode.editor.cycleClipboardRing";
    public const string RunToCursor = "editor.debug.action.runToCursor";
    public const string AddWatch = "editor.debug.action.selectionToWatch";
    public const string QuickWatch = "cscode.debug.quickWatch";
    public const string EditBreakpointCondition = "editor.debug.action.conditionalBreakpoint";
    public const string ToggleBreakpointEnabled = "debug.enableOrDisableBreakpoint";
    public const string SurroundWithSnippet = "editor.action.surroundWithSnippet";
    public const string InsertSnippet = "cscode.editor.insertSnippetPicker";
    /// <summary>The Debug panels' command (Watch window); the editor's Add Watch passes it the expression under the caret.</summary>
    public const string PanelsAddToWatch = "debug.addToWatch";

    public const string BreakpointSubmenu = "editor/context/breakpoint";
    public const string SnippetSubmenu = "editor/context/snippet";

    // Context keys the editor sets when its menu opens (and on focus).
    public const string BreakpointsAvailableKey = "cscode.editor.breakpointsAvailable";
    public const string LineHasBreakpointKey = "cscode.editor.lineHasBreakpoint";
    public const string LineBreakpointEnabledKey = "cscode.editor.lineBreakpointEnabled";
    public const string CanCutCopyKey = "cscode.editor.canCutCopy";
    public const string HasSurroundSnippetsKey = "cscode.editor.hasSurroundSnippets";

    public static IReadOnlyList<SubmenuContribution> Submenus { get; } =
    [
        new(BreakpointSubmenu, "_Breakpoint", "debug-breakpoint-conditional"),
        new(SnippetSubmenu, "S_nippet", "symbol-snippet"),
    ];

    /// <summary>The editor's right-click menu. Groups sort by name; each is a separator-delimited section.</summary>
    public static IReadOnlyList<MenuContribution> Items(EditorViewFactory factory)
    {
        const string Menu = MenuIds.EditorContext;
        const string Writable = "!editorReadonly";
        return
        [
            new(Menu, KnownCommands.QuickFix, "1_actions@1", "editorHasCodeActionsProvider && " + Writable) { Title = "Quick Actions and Refactorings...", Icon = "lightbulb" },
            new(Menu, KnownCommands.Rename, "1_actions@2", "editorHasRenameProvider && " + Writable)
            {
                Title = "Re_name...",
                Icon = "edit",
                // Rename availability is a language-server round trip (prepareRename): it resolves after the menu opened, never on the UI thread.
                EnablementCheck = (_, ct) => factory.ActiveView?.Feature<INavigationFeature>() is { } nav ? new ValueTask<bool>(nav.CanRenameAsync(ct)) : ValueTask.FromResult(false),
            },

            new(Menu, "editor.action.peekDefinition", "2_navigation@1", "editorHasDefinitionProvider") { Title = "Pee_k Definition" },
            new(Menu, KnownCommands.GotoDefinition, "2_navigation@2", "editorHasDefinitionProvider") { Title = "Go To _Definition", Icon = "go-to-file" },
            new(Menu, "editor.action.goToDeclaration", "2_navigation@3", "editorHasDeclarationProvider") { Title = "Go To D_eclaration" },
            new(Menu, "editor.action.goToImplementation", "2_navigation@4", "editorHasImplementationProvider") { Title = "Go To _Implementation" },
            new(Menu, KnownCommands.FindReferences, "2_navigation@5", "editorHasReferenceProvider") { Title = "Find All Re_ferences", Icon = "references" },

            new(Menu, BreakpointSubmenu, "3_debug@1", BreakpointsAvailableKey) { Submenu = BreakpointSubmenu },
            new(BreakpointSubmenu, KnownCommands.DebugToggleBreakpoint, "1@1", "!" + LineHasBreakpointKey) { Title = "Insert _Breakpoint" },
            new(BreakpointSubmenu, KnownCommands.DebugToggleBreakpoint, "1@2", LineHasBreakpointKey) { Title = "_Delete Breakpoint" },
            new(BreakpointSubmenu, ToggleBreakpointEnabled, "1@3", LineHasBreakpointKey + " && " + LineBreakpointEnabledKey) { Title = "Disa_ble Breakpoint" },
            new(BreakpointSubmenu, ToggleBreakpointEnabled, "1@4", LineHasBreakpointKey + " && !" + LineBreakpointEnabledKey) { Title = "E_nable Breakpoint" },
            new(BreakpointSubmenu, EditBreakpointCondition, "1@5") { Title = "_Conditions..." },
            new(Menu, RunToCursor, "3_debug@2", BreakpointsAvailableKey + " && debugState != 'running'") { Title = "Run To C_ursor" },
            new(Menu, AddWatch, "3_debug@3", "inDebugMode") { Title = "Add _Watch", Icon = "watch" },
            new(Menu, QuickWatch, "3_debug@4", "debugState == 'stopped'") { Title = "_QuickWatch...", Icon = "eye" },

            new(Menu, SnippetSubmenu, "4_snippets@1", Writable) { Submenu = SnippetSubmenu },
            new(SnippetSubmenu, InsertSnippet, "1@1") { Title = "_Insert Snippet..." },
            new(SnippetSubmenu, SurroundWithSnippet, "1@2", HasSurroundSnippetsKey) { Title = "_Surround With..." },

            new(Menu, Cut, "5_cutcopypaste@1", CanCutCopyKey + " && " + Writable) { Title = "Cu_t" },
            new(Menu, Copy, "5_cutcopypaste@2", CanCutCopyKey) { Title = "_Copy", Icon = "copy" },
            new(Menu, CopyWithFormatting, "5_cutcopypaste@2.5", CanCutCopyKey) { Title = "Copy wit_h Formatting" },
            new(Menu, Paste, "5_cutcopypaste@3", Writable)
            {
                Title = "_Paste",
                Icon = "clippy",
                EnablementCheck = async (_, ct) => await factory.Clipboard.HasTextAsync(ct).ConfigureAwait(true),
            },

            new(Menu, KnownCommands.FormatDocument, "6_format@1", "editorHasDocumentFormattingProvider && " + Writable) { Title = "_Format Document" },
            new(Menu, "editor.action.formatDocument.multiple", "6_format@1.5", "editorHasDocumentFormattingProvider && " + Writable) { Title = "Format Document _With..." },
            new(Menu, "editor.action.formatSelection", "6_format@2", "editorHasDocumentSelectionFormattingProvider && editorHasSelection && " + Writable) { Title = "Format _Selection" },
            new(Menu, "editor.action.addCommentLine", "6_format@3", Writable) { Title = "Co_mment Selection", Icon = "comment" },
            new(Menu, "editor.action.removeCommentLine", "6_format@4", Writable) { Title = "_Uncomment Selection" },

            new(Menu, KnownCommands.CommandPalette, "9_palette@1") { Title = "Command _Palette..." },
        ];
    }

    /// <summary>
    /// The clipboard keys (Visual Studio's and VS Code's defaults), contributed as built-in bindings so menus show them and keybindings.json can
    /// rebind them. Within one command the last entry is the one menus display (highest precedence). Every Ctrl key has a Mac one (Cmd as in the
    /// keymap presets; Ctrl+Insert stays, the Insert key of a Mac's full keyboard).
    /// </summary>
    public static IReadOnlyList<KeybindingContribution> Keybindings { get; } =
    [
        new(Cut, "shift+delete", When: "editorTextFocus && !editorReadonly"),
        new(Cut, "ctrl+x", Mac: "cmd+x", When: "editorTextFocus && !editorReadonly"),
        new(Copy, "ctrl+insert", Mac: "ctrl+insert", When: "editorTextFocus"),
        new(Copy, "ctrl+c", Mac: "cmd+c", When: "editorTextFocus"),
        new(Paste, "shift+insert", When: "editorTextFocus && !editorReadonly"),
        new(Paste, "ctrl+v", Mac: "cmd+v", When: "editorTextFocus && !editorReadonly"),
        // In a Markdown file the key opens the preview, as in VS Code (markdown.showPreview, docs/MARKDOWN.md); Edit > Cycle Clipboard Ring stays.
        new(CycleClipboardRing, "ctrl+shift+v", Mac: "cmd+shift+v", When: "editorTextFocus && !editorReadonly && editorLangId != 'markdown' && !markdownPreviewFocus"),
        new(RunToCursor, "ctrl+f10", Mac: "cmd+f10", When: "editorTextFocus && " + BreakpointsAvailableKey),
        new(QuickWatch, "shift+f9", When: "editorTextFocus && debugState == 'stopped'"),
        new(ToggleBreakpointEnabled, "ctrl+f9", Mac: "cmd+f9", When: "editorTextFocus && " + BreakpointsAvailableKey),
        new(SurroundWithSnippet, "ctrl+k ctrl+s", Mac: "cmd+k cmd+s", When: "editorTextFocus && !editorReadonly"),
        new(InsertSnippet, "ctrl+k ctrl+x", Mac: "cmd+k cmd+x", When: "editorTextFocus && !editorReadonly"),
    ];

    /// <summary>Contributes the items, submenus and keys; dispose to withdraw them.</summary>
    public static IDisposable Contribute(EditorViewFactory factory, IContributionRegistry contributions)
    {
        var dir = AppContext.BaseDirectory;
        return Disposable.Combine(
            contributions.Contribute(ContributionSources.Builtin, dir, Submenus),
            contributions.Contribute(ContributionSources.Builtin, dir, Items(factory)),
            contributions.Contribute(ContributionSources.Builtin, dir, Keybindings));
    }

    /// <summary>
    /// Sets the context keys the menu's `when` clauses read, for <paramref name="view"/> at its caret. Called when the view gets focus and right
    /// before its menu is built (the routed ContextRequested passes the view first), so the menu never waits for anything. O(1) lookups only.
    /// </summary>
    public static void PrepareContextKeys(CodeEditorControl view)
    {
        if (view.Services.ContextKeys is not { } keys) return;
        var debug = view.Feature<DebugFeature>();
        var line = view.Controller.Caret.Line;
        // The breakpoint manager is asked directly (the margin's cache is refreshed on a later turn).
        var bp = debug is null ? null : view.Services.Debug?.Breakpoints.GetForUri(view.Document.Uri).FirstOrDefault(b => b.Line == line);
        keys.Set(BreakpointsAvailableKey, debug is not null && debug.IsFileDocument);
        keys.Set(LineHasBreakpointKey, bp is not null);
        keys.Set(LineBreakpointEnabledKey, bp?.Enabled ?? false);
        keys.Set(CanCutCopyKey, view.Clipboard.CanCopy);
        keys.Set(HasSurroundSnippetsKey, view.Feature<ISnippetFeature>() is { } snippets && snippets.HasSurroundSnippets);
        keys.Set("editorHasSelection", view.Editor.SelectionLength > 0);
    }

    // ------------------------------------------------------------------ commands behind the items

    /// <summary>The selection when it is on one line, else the word at the caret: what Add Watch / QuickWatch evaluate.</summary>
    internal static string? ExpressionAtCaret(CodeEditorControl view)
    {
        var editor = view.Editor;
        if (editor.SelectionLength > 0)
        {
            var text = editor.SelectedText.Trim();
            return text.Length == 0 || text.Contains('\n') ? null : text;
        }
        var (start, end) = FeatureSupport.WordAt(editor.Document, editor.CaretOffset);
        return end > start ? editor.Document.GetText(start, end - start) : null;
    }

    /// <summary>
    /// Visual Studio's Run To Cursor (Ctrl+F10): a temporary breakpoint at the caret's line (unless one is there), then Continue (or Start when
    /// not debugging); the temporary breakpoint goes away at the next stop or when the session ends, whichever comes first.
    /// </summary>
    internal static async Task RunToCursorAsync(CodeEditorControl view)
    {
        if (view.Services.Debug is not { } debug || view.Services.Commands is not { } commands || !view.Document.Uri.IsFile) return;
        var uri = view.Document.Uri;
        var line = view.Controller.Caret.Line;
        var breakpoints = debug.Breakpoints;
        SourceBreakpoint? temporary = breakpoints.GetForUri(uri).Any(b => b.Line == line && b.Enabled) ? null : breakpoints.Add(uri, line);
        var subs = new DisposableStore();
        var done = 0;
        void Cleanup()
        {
            if (Interlocked.Exchange(ref done, 1) != 0) return;
            subs.Dispose();
            if (temporary is not null) breakpoints.Remove(temporary.Id);
        }
        if (temporary is not null)
        {
            var resumed = debug.State == DebugState.Inactive;
            subs.Add(debug.StateChanged.Subscribe(state =>
            {
                if (state is DebugState.Running or DebugState.Initializing) resumed = true;
                else if (state == DebugState.Inactive || (state == DebugState.Stopped && resumed)) Cleanup();
            }));
        }
        try
        {
            if (debug.State == DebugState.Stopped) await commands.ExecuteAsync(KnownCommands.DebugContinue).ConfigureAwait(true);
            else if (debug.State == DebugState.Inactive)
            {
                await commands.ExecuteAsync(KnownCommands.DebugStart).ConfigureAwait(true);
                if (debug.State == DebugState.Inactive) Cleanup(); // nothing started (no launch configuration, cancelled)
            }
            else Cleanup();
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    internal static async Task AddWatchAsync(CodeEditorControl view)
    {
        var expression = ExpressionAtCaret(view);
        if (view.Services.Commands is { } commands && commands.Has(PanelsAddToWatch)) await commands.ExecuteAsync(PanelsAddToWatch, expression).ConfigureAwait(true);
        else if (expression is not null) view.Services.Debug?.AddWatch(expression);
    }

    /// <summary>
    /// QuickWatch (Shift+F9): the expression under the caret (editable), evaluated in the frame the debugger stopped in; the value is shown with an
    /// "Add Watch" action. A lighter take on Visual Studio's modal dialog: no expandable tree (the Watch window has it).
    /// </summary>
    internal static async Task QuickWatchAsync(CodeEditorControl view)
    {
        if (view.Services.Debug is not { StopLocation: { } stop } debug || view.Services.Window is not { } window) return;
        var expression = await window.ShowInputBoxAsync(new InputBoxOptions(Title: "QuickWatch", Prompt: "Expression to evaluate in the current frame", Value: ExpressionAtCaret(view))).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(expression)) return;
        string text;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await stop.Session.EvaluateAsync(expression, stop.FrameId, "watch", cts.Token).ConfigureAwait(true);
            text = $"{expression} = {result.Result}" + (result.Type is { Length: > 0 } t ? $"  ({t})" : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            text = $"{expression}: {ex.Message}";
        }
        var choice = await window.ShowMessageAsync(MessageSeverity.Info, text, [new MessageAction("Add Watch"), new MessageAction("Close", IsCloseAffordance: true)]).ConfigureAwait(true);
        if (choice == "Add Watch")
        {
            if (view.Services.Commands is { } commands && commands.Has(PanelsAddToWatch)) await commands.ExecuteAsync(PanelsAddToWatch, expression).ConfigureAwait(true);
            else debug.AddWatch(expression);
        }
    }
}
