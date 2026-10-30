using CsCode.Core.Platform;
using CsCode.Ui.Icons;

namespace CsCode.App.Menus;

/// <summary>
/// One entry of a menu definition. <see cref="CheckWhen"/> is a context-key expression that makes the item show a check mark;
/// <see cref="Dynamic"/> names a generated submenu body ("recent", "toolWindows") filled in by the menu model.
/// </summary>
public sealed record MenuNode(
    string Header,
    string? CommandId = null,
    string? When = null,
    string? CheckWhen = null,
    string? IconKey = null,
    IReadOnlyList<MenuNode>? Children = null,
    bool IsSeparator = false,
    string? Dynamic = null,
    object? Argument = null)
{
    public static readonly MenuNode Separator = new("-", IsSeparator: true);
}

/// <summary>
/// The standard Visual Studio menu bar: File, Edit, View, Go, Debug, Terminal, Tools, Window, Help. Every item is bound to a command id
/// (VS Code ids where they exist) and gated by an optional `when` clause; the shell does not hard-wire behaviour here. There are no
/// extension or marketplace entries: CsCode has no extension system.
/// </summary>
public static class ShellMenus
{
    public const string ResetLayout = "cscode.shell.resetLayout";
    public const string OpenRecent = "cscode.shell.openRecent";
    public const string About = "cscode.shell.about";
    public const string Welcome = "cscode.shell.showWelcome";
    public const string KeyboardReference = "cscode.shell.showKeyboardReference";
    public const string ToggleNotifications = "cscode.shell.toggleNotifications";
    public const string ToggleToolWindow = "cscode.shell.toggleToolWindow";
    /// <summary>Shows or hides a toolbar group (argument: the group id, <see cref="Views.ToolbarView.Groups"/>); persisted in its setting.</summary>
    public const string ToggleToolbar = "cscode.shell.toggleToolbar";
    /// <summary>The Keyboard Shortcuts editor (owned by the Settings panel; the item stays disabled without it).</summary>
    public const string OpenKeyboardShortcuts = "workbench.action.openGlobalKeybindings";

    private static MenuNode M(string header, string cmd, string? when = null, string? icon = null) => new(header, cmd, when, IconKey: icon);
    private static MenuNode Sub(string header, params MenuNode[] children) => new(header, Children: children);
    private static MenuNode S => MenuNode.Separator;

    public static IReadOnlyList<MenuNode> Build() =>
    [
        Sub("_File",
            M("_New File", KnownCommands.NewUntitledFile, icon: IconKey.NewFile),
            M("_Open File...", KnownCommands.OpenFile, icon: IconKey.File),
            M("Open _Folder...", KnownCommands.OpenFolder, icon: IconKey.FolderOpened),
            M("Open Re_mote Folder...", "remote.openFolder", icon: IconKey.Remote),
            new MenuNode("Open _Recent", Dynamic: "recent", Children: []),
            S,
            M("_Save", KnownCommands.Save, "hasOpenEditors", IconKey.Save),
            M("Save _As...", "workbench.action.files.saveAs", "hasOpenEditors", IconKey.SaveAll),
            M("Save A_ll", KnownCommands.SaveAll, "hasOpenEditors", IconKey.SaveAll),
            S,
            M("_Close Editor", KnownCommands.CloseActiveEditor, "hasOpenEditors"),
            M("Close All Edi_tors", "workbench.action.closeAllEditors", "hasOpenEditors"),
            M("Re_open Closed Editor", "workbench.action.reopenClosedEditor"),
            M("Close F_older", "workbench.action.closeFolder", "workspaceFolderCount"),
            S,
            // Background mode (docs/SESSIONS.md): enabled in session processes, where these commands exist.
            Sub("Sess_ions",
                M("_Open Session...", Background.SessionCommandIds.OpenSession),
                M("S_witch Session...", Background.SessionCommandIds.SwitchSession),
                M("_Save Session As...", Background.SessionCommandIds.SaveSessionAs),
                M("_Rename Session...", Background.SessionCommandIds.RenameSession),
                M("_Delete Session...", Background.SessionCommandIds.DeleteSession),
                S,
                M("Start in the _Background at Login...", Background.SessionCommandIds.StartAtLogin)),
            M("Close _Window (Keep Running)", Background.SessionCommandIds.CloseWindow),
            M("_Quit Session", Background.SessionCommandIds.QuitSession),
            S,
            M("E_xit", KnownCommands.Quit),
            M("Exit _All", Background.SessionCommandIds.ExitAll)),

        Sub("_Edit",
            M("_Undo", "undo", "hasOpenEditors", IconKey.Undo),
            M("_Redo", "redo", "hasOpenEditors", IconKey.Redo),
            S,
            // Cut/Copy/Paste act on what has focus (editor, text box, a panel's selection): Input.ClipboardRouter sets these keys.
            M("Cu_t", "editor.action.clipboardCutAction", Input.ClipboardRouter.CanCutKey),
            M("_Copy", "editor.action.clipboardCopyAction", Input.ClipboardRouter.CanCopyKey, IconKey.Copy),
            M("_Paste", "editor.action.clipboardPasteAction", Input.ClipboardRouter.CanPasteKey, "clippy"),
            M("Cycle Clipboard _Ring", "cscode.editor.cycleClipboardRing", Input.ClipboardRouter.EditorPasteKey),
            M("Select _All", "editor.action.selectAll", "editorTextFocus"),
            S,
            M("_Find...", KnownCommands.Find, "hasOpenEditors", IconKey.Search),
            M("R_eplace...", KnownCommands.Replace, "hasOpenEditors && !editorReadonly"),
            M("Find in F_iles...", "workbench.action.findInFiles", "workspaceFolderCount"),
            S,
            M("_Format Document", KnownCommands.FormatDocument, "hasOpenEditors && !editorReadonly"),
            M("Co_mment Selection", "editor.action.addCommentLine", "editorTextFocus && !editorReadonly"),
            M("Uncomme_nt Selection", "editor.action.removeCommentLine", "editorTextFocus && !editorReadonly")),

        Sub("_View",
            M("_Command Palette...", KnownCommands.CommandPalette),
            M("Go to _File...", KnownCommands.QuickOpen),
            S,
            new MenuNode("_Tool Windows", Dynamic: "toolWindows", Children: []),
            Sub("Tool_bars", Views.ToolbarView.Groups.Select(g =>
                new MenuNode("_" + g.Title, ToggleToolbar, CheckWhen: "config." + g.SettingKey, Argument: g.Id)).ToArray()),
            S,
            Sub("_Appearance",
                M("_Color Theme...", KnownCommands.SelectTheme),
                M("_Full Screen", "workbench.action.toggleFullScreen"),
                M("_Zen Mode", "workbench.action.toggleZenMode"),
                S,
                M("_Reset Window Layout", ResetLayout)),
            M("_Notifications", ToggleNotifications, icon: IconKey.Bell)),

        Sub("_Go",
            M("Go to _Line...", "workbench.action.gotoLine", "hasOpenEditors"),
            M("Go to Symbol in _Editor...", "workbench.action.gotoSymbol", "hasOpenEditors"),
            M("Go to Symbol in _Workspace...", "workbench.action.showAllSymbols"),
            S,
            M("Go to _Definition", KnownCommands.GotoDefinition, "hasOpenEditors"),
            M("Go to Type D_efinition", "editor.action.goToTypeDefinition", "hasOpenEditors"),
            M("Go to _Implementations", "editor.action.goToImplementation", "hasOpenEditors"),
            M("Go to _References", KnownCommands.FindReferences, "hasOpenEditors"),
            S,
            M("_Back", "workbench.action.navigateBack", icon: IconKey.ArrowLeft),
            M("_Forward", "workbench.action.navigateForward", icon: IconKey.ArrowRight),
            S,
            M("Next _Problem", "editor.action.marker.next", "hasOpenEditors"),
            M("_Previous Problem", "editor.action.marker.prev", "hasOpenEditors")),

        Sub("_Debug",
            M("_Start Debugging", KnownCommands.DebugStart, "debugState == 'inactive'", IconKey.Play),
            M("Start _Without Debugging", KnownCommands.DebugRun, "debugState == 'inactive'"),
            M("S_top Debugging", KnownCommands.DebugStop, "inDebugMode", IconKey.Stop),
            M("_Restart", KnownCommands.DebugRestart, "inDebugMode", IconKey.Restart),
            S,
            M("_Continue", KnownCommands.DebugContinue, "debugState == 'stopped'", IconKey.Continue),
            M("_Pause", KnownCommands.DebugPause, "debugState == 'running'", IconKey.Pause),
            M("Step _Over", KnownCommands.DebugStepOver, "debugState == 'stopped'", IconKey.StepOver),
            M("Step _Into", KnownCommands.DebugStepInto, "debugState == 'stopped'", IconKey.StepInto),
            M("Step O_ut", KnownCommands.DebugStepOut, "debugState == 'stopped'", IconKey.StepOut),
            S,
            M("Toggle _Breakpoint", KnownCommands.DebugToggleBreakpoint, "hasOpenEditors"),
            M("Open _launch.json", "workbench.action.debug.configure"),
            M("_Add Configuration...", "debug.addConfiguration")),

        Sub("_Terminal",
            M("_New Terminal", "workbench.action.terminal.new", icon: IconKey.Terminal),
            M("_Toggle Terminal", KnownCommands.ToggleTerminal),
            S,
            M("_Run Task...", "workbench.action.tasks.runTask"),
            M("Run _Build Task...", "workbench.action.tasks.build")),

        Sub("T_ools",
            M("_Options...", KnownCommands.OpenSettings, icon: IconKey.Gear),
            M("_Settings (JSON)", "workbench.action.openSettingsJson"),
            M("_Keyboard Shortcuts...", OpenKeyboardShortcuts, icon: IconKey.Key),
            M("Keyboard Shortcuts _Reference", KeyboardReference),
            M("_Color Theme...", KnownCommands.SelectTheme),
            S,
            Sub("_Macros",
                M("_Record Macro", "macro.record", "!cscode.macroRecording && !cscode.macroPlaying", "record"),
                M("_Stop Recording", "macro.stop", "cscode.macroRecording || cscode.macroPlaying", "stop-circle"),
                S,
                M("_Play Last Macro", "macro.playLast", "cscode.macroHasLast && !cscode.macroRecording && !cscode.macroPlaying", "play"),
                new MenuNode("Play Last Macro _N Times...", "macro.play", "cscode.macroHasLast && !cscode.macroRecording && !cscode.macroPlaying", Argument: "times"),
                new MenuNode("Play Until _End of File", "macro.play", "cscode.macroHasLast && !cscode.macroRecording && !cscode.macroPlaying", Argument: "untilEndOfFile"),
                new MenuNode("Play Until Search _Fails", "macro.play", "cscode.macroHasLast && !cscode.macroRecording && !cscode.macroPlaying", Argument: "untilSearchFails"),
                new MenuNode("Play on Selected _Lines", "macro.play", "cscode.macroHasLast && !cscode.macroRecording && !cscode.macroPlaying", Argument: "onSelectedLines"),
                S,
                M("Play Sa_ved Macro...", "macro.playNamed", "!cscode.macroRecording && !cscode.macroPlaying"),
                M("S_ave Last Macro As...", "macro.save", "cscode.macroHasLast && !cscode.macroRecording"),
                M("_Manage Macros (macros.json)", "macro.edit"))),

        Sub("_Window",
            M("Split Editor _Right", "workbench.action.splitEditorRight", "hasOpenEditors", IconKey.SplitRight),
            M("Split Editor _Down", "workbench.action.splitEditorDown", "hasOpenEditors", IconKey.SplitDown),
            M("Move Editor to _Next Group", "workbench.action.moveEditorToNextGroup", "hasOpenEditors"),
            M("_Float Editor", Commands.ShellCommands.FloatEditor, "hasOpenEditors"),
            M("Dock _All Floating Editors", Commands.ShellCommands.DockEditor, "hasOpenEditors"),
            S,
            M("Ne_xt Editor", "workbench.action.nextEditor", "hasOpenEditors"),
            M("Pre_vious Editor", "workbench.action.previousEditor", "hasOpenEditors"),
            M("_Pin Tab", "workbench.action.pinEditor", "hasOpenEditors", IconKey.Pin),
            M("_Close Other Editors", "workbench.action.closeOtherEditors", "hasOpenEditors"),
            S,
            M("_Reset Window Layout", ResetLayout),
            M("Reload _Window", KnownCommands.ReloadWindow)),

        Sub("_Help",
            M("_Welcome", Welcome, icon: IconKey.Home),
            M("_Keyboard Shortcuts Reference", KeyboardReference),
            S,
            M("Toggle _Output", "workbench.action.output.toggleOutput", icon: IconKey.Output),
            M("_About CsCode", About, icon: "info")),
    ];
}
