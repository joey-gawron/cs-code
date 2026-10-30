using CsCode.App.Docking;
using CsCode.App.Menus;
using CsCode.App.Notifications;
using CsCode.App.QuickInput;
using CsCode.App.ToolWindows.FindResults;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Theming;
using CsCode.Ui;
using CsCode.Ui.Theming;

namespace CsCode.App.Commands;

/// <summary>Window-level actions the shell commands need from the real window (faked in tests).</summary>
public interface IWindowChrome
{
    /// <summary>Asks the window to close (runs the dirty-tab prompts first). Returns false if the user cancelled.</summary>
    Task<bool> RequestExitAsync();
    void ToggleFullScreen();
    void ToggleZenMode();
}

/// <summary>The collaborators the shell commands operate on.</summary>
public sealed record ShellCommandContext(
    ICommandService Commands,
    DocumentWell Well,
    ToolWindowManager Tools,
    PaletteController Palette,
    IWindowService Window,
    IModalDialogs Dialogs,
    INavigationService Navigation,
    IWorkspaceService? Workspace,
    IConfigurationService? Config,
    IColorThemeService? Themes,
    IKeybindingService? Bindings,
    FindResultsViewModel FindResults,
    NotificationCenter Notifications,
    IWindowChrome Chrome,
    Func<Task> ResetLayout,
    Func<Task> RefreshExplorer,
    IEnumerable<IToolWindow> ToolWindows,
    Func<Avalonia.Controls.Control> CreateWelcome,
    Func<Avalonia.Controls.Control> CreateKeyboardReference,
    CsCode.Core.Languages.ILanguageService? LanguageService = null,
    ShellViewActivator? Views = null,
    CsCode.Ui.Threading.IUiDispatcher? Ui = null);

/// <summary>
/// Registers every command the shell owns (tabs, files, palette modes, window layout, theme picker, Find Results, tool-window toggles).
/// Commands owned by other modules are deliberately NOT registered here (editor.action.* belongs to Editor, debug.* to Dap, terminal to the
/// terminal panel): their menu items stay disabled until that module registers them.
/// </summary>
public static class ShellCommands
{
    public const string ReferencesShow = "cscode.references.show";
    public const string ChangeLanguageMode = "cscode.shell.changeLanguageMode";
    public const string ShowErrorList = "cscode.shell.showErrorList";
    public const string GotoLine = "workbench.action.gotoLine";
    public const string OpenSettingsJson = "workbench.action.openSettingsJson";
    /// <summary>VS Code's "Move Editor into New Window": the active document floats (Visual Studio's Float).</summary>
    public const string FloatEditor = "workbench.action.moveEditorToNewWindow";
    /// <summary>VS Code's "Restore Editors into Main Window": every floating document is docked back (Visual Studio's Dock as Tabbed Document).</summary>
    public const string DockEditor = "workbench.action.restoreEditorsToMainWindow";

    public static IDisposable Register(ShellCommandContext c)
    {
        var regs = new DisposableStore();

        void Reg(string id, string title, string? category, Func<CommandInvocation, ValueTask<object?>> run) =>
            regs.Add(c.Commands.Register(id, run, new CommandMetadata(id, title, category)));
        void Run(string id, string title, string? category, Func<Task> run) =>
            Reg(id, title, category, async _ => { await run().ConfigureAwait(true); return null; });
        // Registered without palette metadata: internal plumbing commands (menus call them, users should not see them).
        void Hidden(string id, Func<CommandInvocation, Task> run) =>
            regs.Add(c.Commands.Register(id, async inv => { await run(inv).ConfigureAwait(true); return null; }));

        // ---- palette modes
        Run(KnownCommands.CommandPalette, "Show All Commands", "View", () => c.Palette.ShowAsync(">"));
        Run(KnownCommands.QuickOpen, "Go to File...", "Go", () => c.Palette.ShowAsync(""));
        // Shadows the editor's standalone Go to Line (input box); takes the same optional "line[:column]" argument so macros and bound args keep working.
        Reg(GotoLine, "Go to Line/Column...", "Go", async inv =>
        {
            if (inv.Args is [{ } arg, ..] && arg.ToString() is { Length: > 0 } target) GoToLine(c, target);
            else await c.Palette.ShowAsync(":").ConfigureAwait(true);
            return null;
        });
        Run("workbench.action.gotoSymbol", "Go to Symbol in Editor...", "Go", () => c.Palette.ShowAsync("@"));
        Run("workbench.action.showAllSymbols", "Go to Symbol in Workspace...", "Go", () => c.Palette.ShowAsync("#"));

        // ---- files and folders
        Run(KnownCommands.OpenFolder, "Open Folder...", "File", async () =>
        {
            if (c.Workspace is null) return;
            var picked = await c.Window.ShowOpenDialogAsync(new OpenDialogOptions(CanSelectFiles: false, CanSelectFolders: true, Title: "Open Folder")).ConfigureAwait(true);
            if (picked is { Count: > 0 }) await c.Workspace.OpenFolderAsync(picked[0]).ConfigureAwait(true);
        });
        Run(KnownCommands.OpenFile, "Open File...", "File", async () =>
        {
            var picked = await c.Window.ShowOpenDialogAsync(new OpenDialogOptions(CanSelectMany: true, Title: "Open File")).ConfigureAwait(true);
            if (picked is not null) foreach (var u in picked) await c.Navigation.OpenAsync(u).ConfigureAwait(true);
        });
        Hidden(ShellMenus.OpenRecent, async inv =>
        {
            if (c.Workspace is not null && inv.Arg<string>(0) is { } s) await c.Workspace.OpenFolderAsync(new Uri(s)).ConfigureAwait(true);
        });
        Run("workbench.action.closeFolder", "Close Folder", "File", async () => { if (c.Workspace is not null) await c.Workspace.CloseFolderAsync().ConfigureAwait(true); });
        Run(KnownCommands.NewUntitledFile, "New Untitled File", "File", () => { c.Well.OpenUntitled(); return Task.CompletedTask; });
        // Every save goes through the well: will-save participants (format on save) run first.
        Run(KnownCommands.Save, "Save", "File", async () => { if (c.Well.ActiveEditor is { } t) await c.Well.SaveAsync(t).ConfigureAwait(true); });
        Run("workbench.action.files.saveAs", "Save As...", "File", async () => { if (c.Well.ActiveEditor is { } t) await c.Well.SaveAsync(t, saveAs: true).ConfigureAwait(true); });
        Run(KnownCommands.SaveAll, "Save All", "File", async () =>
        {
            foreach (var t in c.Well.EditorTabs.Where(t => t.IsDirty).ToArray()) await c.Well.SaveAsync(t).ConfigureAwait(true);
        });

        // ---- tabs
        Run(KnownCommands.CloseActiveEditor, "Close Editor", "View", async () => { if (c.Well.Active is { } t) await c.Well.CloseAsync(t).ConfigureAwait(true); });
        Run("workbench.action.closeAllEditors", "Close All Editors", "View", async () => { await c.Well.CloseAllAsync().ConfigureAwait(true); });
        Run("workbench.action.closeOtherEditors", "Close Other Editors", "View", async () => { if (c.Well.Active is { } t) await c.Well.CloseOthersAsync(t).ConfigureAwait(true); });
        Run("workbench.action.reopenClosedEditor", "Reopen Closed Editor", "View", async () => { await c.Well.ReopenClosedAsync().ConfigureAwait(true); });
        Run("workbench.action.nextEditor", "Open Next Editor", "View", () => { Cycle(c, +1); return Task.CompletedTask; });
        Run("workbench.action.previousEditor", "Open Previous Editor", "View", () => { Cycle(c, -1); return Task.CompletedTask; });
        Run("workbench.action.pinEditor", "Pin Editor", "View", () => { if (c.Well.ActiveEditor is { } t) { if (t.IsPinned) c.Well.Unpin(t); else c.Well.Pin(t); } return Task.CompletedTask; });
        Run("workbench.action.splitEditorRight", "Split Editor Right", "View", () => { if (c.Well.Active is { } t) c.Well.SplitRight(t); return Task.CompletedTask; });
        Run("workbench.action.splitEditorDown", "Split Editor Down", "View", () => { if (c.Well.Active is { } t) c.Well.SplitDown(t); return Task.CompletedTask; });
        Run("workbench.action.moveEditorToNextGroup", "Move Editor into Next Group", "View", () => { if (c.Well.Active is { } t) c.Well.MoveToNextGroup(t); return Task.CompletedTask; });
        Run(FloatEditor, "Move Editor into New Window", "View", () => { if (c.Well.Active is { } t) c.Well.Float(t); return Task.CompletedTask; });
        Run(DockEditor, "Restore Editors into Main Window", "View", () =>
        {
            foreach (var t in c.Well.Tabs.Where(c.Well.IsFloating).ToArray()) c.Well.DockBack(t);
            return Task.CompletedTask;
        });

        // ---- window
        Run(KnownCommands.Quit, "Exit", "File", async () => { await c.Chrome.RequestExitAsync().ConfigureAwait(true); });
        Run(KnownCommands.ReloadWindow, "Reload Window", "Developer", async () => { await c.RefreshExplorer().ConfigureAwait(true); });
        Run("workbench.action.toggleFullScreen", "Toggle Full Screen", "View", () => { c.Chrome.ToggleFullScreen(); return Task.CompletedTask; });
        Run("workbench.action.toggleZenMode", "Toggle Zen Mode", "View", () => { c.Chrome.ToggleZenMode(); return Task.CompletedTask; });
        Run(ShellMenus.ResetLayout, "Reset Window Layout", "View", () => c.ResetLayout());
        Run(ShellMenus.ToggleNotifications, "Toggle Notifications", "View", () => { c.Notifications.IsCenterOpen = !c.Notifications.IsCenterOpen; return Task.CompletedTask; });
        Run(ShellMenus.Welcome, "Welcome", "Help", () => { c.Well.OpenView("welcome", "Welcome", c.CreateWelcome); return Task.CompletedTask; });
        Run(ShellMenus.KeyboardReference, "Keyboard Shortcuts Reference", "Help", () => { c.Well.OpenView("keyboard", "Keyboard Shortcuts Reference", c.CreateKeyboardReference); return Task.CompletedTask; });
        Run(ShellMenus.About, "About CsCode", "Help", async () => { await c.Dialogs.ShowAsync("About CsCode", AboutText.Build(), ["OK"]).ConfigureAwait(true); });
        // The Settings panel owns both ids (the Settings editor and the JSON file); these fallbacks only exist when no such panel is composed.
        // The panels registered their commands while the tool windows were enumerated, before this runs (see ShellController.Initialize).
        if (!c.Commands.Has(KnownCommands.OpenSettings)) Run(KnownCommands.OpenSettings, "Open Settings", "Preferences", () => OpenSettingsFile(c));
        if (!c.Commands.Has(OpenSettingsJson)) Run(OpenSettingsJson, "Open Settings (JSON)", "Preferences", () => OpenSettingsFile(c));
        Run(KnownCommands.SelectTheme, "Color Theme", "Preferences", () => PickThemeAsync(c));
        // View > Toolbars: the group's setting is the state (so settings.json and other windows agree); the toolbar follows the setting.
        Hidden(ShellMenus.ToggleToolbar, async inv =>
        {
            if (c.Config is null || inv.Arg<string>(0) is not { } id || Views.ToolbarView.Groups.FirstOrDefault(g => g.Id == id) is not { } group) return;
            await c.Config.UpdateAsync(group.SettingKey, !c.Config.Get(group.SettingKey, true)).ConfigureAwait(true);
        });

        // ---- tool windows (and document views such as Settings, when their module did not register its own command)
        foreach (var tool in c.ToolWindows)
        {
            var id = tool.Id;
            if (tool.ToggleCommandId is not { } toggle || c.Commands.Has(toggle)) continue;
            if (tool.DefaultLocation == ToolWindowLocation.Document)
                Run(toggle, "Open " + tool.Title, "View", () => { c.Views?.Show(id); return Task.CompletedTask; });
            else
                Run(toggle, "Toggle " + tool.Title, "View", () => { c.Tools.Toggle(id); return Task.CompletedTask; });
        }
        Run(ShowErrorList, "Show Error List", "View", () => { c.Tools.Show("errorlist"); return Task.CompletedTask; });

        // Status-bar language picker.
        Run(ChangeLanguageMode, "Change Language Mode", "Editor", async () =>
        {
            if (c.Well.ActiveEditor is not { } tab || c.LanguageService is null) return;
            var items = c.LanguageService.Languages.OrderBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(l => new QuickPickItem(l.DisplayName, l.Id, Picked: l.Id == tab.Doc.LanguageId, Tag: l.Id)).ToArray();
            var picked = await c.Window.ShowQuickPickAsync(items, new QuickPickOptions(Title: "Select Language Mode", Placeholder: "Select the language of the current file")).ConfigureAwait(true);
            if (picked?.Tag is string id) tab.Doc.SetLanguageId(id);
        });

        // ---- Find Results (the editor's Find References delivers many hits through this command)
        Hidden(ReferencesShow, inv =>
        {
            c.FindResults.ShowFromCommand(inv.Args);
            c.Tools.Show("findresults");
            return Task.CompletedTask;
        });

        return regs;
    }

    /// <summary>Moves the active editor's caret to "line" or "line:column" (1-based, clamped), as the editor's own Go to Line does with an argument.</summary>
    private static void GoToLine(ShellCommandContext c, string target)
    {
        if (c.Well.ActiveEditor is not { } tab) return;
        var parts = target.Split(':', StringSplitOptions.TrimEntries);
        if (!int.TryParse(parts[0], out var line)) return;
        line = Math.Clamp(line, 1, Math.Max(1, tab.Doc.LineCount));
        var column = parts.Length > 1 && int.TryParse(parts[1], out var col) ? Math.Max(1, col) : 1;
        var at = Core.Text.TextRange.At(line - 1, column - 1);
        if (tab.Controller is { } editor)
        {
            editor.SetSelection(at, reveal: true);
            editor.Focus();
        }
        else tab.PendingReveal = at; // applied when the view is created
    }

    private static void Cycle(ShellCommandContext c, int step)
    {
        var tabs = c.Well.Tabs;
        if (tabs.Count < 2 || c.Well.Active is not { } active) return;
        var i = tabs.ToList().IndexOf(active);
        c.Well.Activate(tabs[((i + step) % tabs.Count + tabs.Count) % tabs.Count]);
    }

    private static async Task OpenSettingsFile(ShellCommandContext c)
    {
        if (c.Config is null) return;
        await c.Navigation.OpenAsync(new Uri(c.Config.UserSettingsPath)).ConfigureAwait(true);
    }

    /// <summary>
    /// Quick pick of the registered themes (grouped Dark / Light / High contrast, as in the toolbar dropdown) with live preview while arrowing:
    /// a preview persists nothing, Esc reverts, choosing applies and persists <c>workbench.colorTheme</c> (<see cref="ThemeSelection"/>).
    /// </summary>
    private static async Task PickThemeAsync(ShellCommandContext c)
    {
        if (c.Themes is null) return;
        var original = c.Themes.Current;
        var items = ThemeSelection.BuildItems(c.Themes.Available).OfType<ThemeChoiceItem>()
            .Select(t => new QuickPickItem(t.Label, ThemeSelection.GroupOf(t.Kind), Tag: t.Id, Picked: t.Id == original.Id)).ToArray();
        // At most one preview per ThemePreviewer.MinInterval while an arrow key repeats (the shell always passes its dispatcher).
        using var previewer = c.Ui is { } ui ? new ThemePreviewer(c.Themes, ui) : null;
        var picked = await c.Palette.PickWithPreviewAsync(items, "Select Color Theme", "Select a color theme (up/down keys to preview)", e =>
        {
            if (e?.Tag is not QuickPickItem { Tag: string id }) return;
            if (previewer is not null) previewer.Request(id);
            else ThemeSelection.Preview(c.Themes, id);
        }).ConfigureAwait(true);
        previewer?.Cancel(); // a preview still waiting for its interval must not land after the choice
        if (picked?.Tag is string chosen) await ThemeSelection.CommitAsync(c.Themes, c.Config, chosen).ConfigureAwait(true);
        else ThemeSelection.EndPreview(c.Themes, original.Id); // Esc: revert
    }
}

/// <summary>Text of the About dialog: versions, runtime, and a pointer to the third-party notices.</summary>
public static class AboutText
{
    public static string Build()
    {
        var asm = typeof(AboutText).Assembly.GetName();
        return string.Join('\n',
            $"CsCode {asm.Version?.ToString(3) ?? "dev"}",
            $"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}",
            $"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})",
            $"UI: Avalonia {typeof(Avalonia.Application).Assembly.GetName().Version?.ToString(3)}, Dock.Avalonia",
            "",
            "A VSCodium-class editor with a Visual Studio 2022 interface, LSP, DAP and Vim emulation.",
            "There is no extension system.",
            "",
            "Icons: Codicons (c) Microsoft, CC-BY-4.0. See THIRD-PARTY-NOTICES.md for all third-party licences.");
    }
}
