using CsCode.App.Docking;
using CsCode.App.Menus;
using CsCode.App.Notifications;
using CsCode.App.QuickInput;
using CsCode.App.ToolWindows.FindResults;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Theming;
using CsCode.Ui;

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
    ShellViewActivator? Views = null);

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
        Run(KnownCommands.Save, "Save", "File", async () => { if (c.Well.ActiveEditor is { } t) await SaveAsync(c, t).ConfigureAwait(true); });
        Run("workbench.action.files.saveAs", "Save As...", "File", async () =>
        {
            if (c.Well.ActiveEditor is not { } t) return;
            var target = await c.Window.ShowSaveDialogAsync(new SaveDialogOptions(Title: "Save As")).ConfigureAwait(true);
            if (target is not null) await t.Doc.SaveAsAsync(target).ConfigureAwait(true);
        });
        Run(KnownCommands.SaveAll, "Save All", "File", async () =>
        {
            foreach (var t in c.Well.EditorTabs.Where(t => t.IsDirty).ToArray()) await SaveAsync(c, t).ConfigureAwait(true);
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

        // ---- window
        Run(KnownCommands.Quit, "Exit", "File", async () => { await c.Chrome.RequestExitAsync().ConfigureAwait(true); });
        Run(KnownCommands.ReloadWindow, "Reload Window", "Developer", async () => { await c.RefreshExplorer().ConfigureAwait(true); });
        Run("workbench.action.toggleFullScreen", "Toggle Full Screen", "View", () => { c.Chrome.ToggleFullScreen(); return Task.CompletedTask; });
        Run("workbench.action.toggleZenMode", "Toggle Zen Mode", "View", () => { c.Chrome.ToggleZenMode(); return Task.CompletedTask; });
        Run(ShellMenus.ResetLayout, "Reset Window Layout", "View", () => c.ResetLayout());
        Run(ShellMenus.ToggleNotifications, "Toggle Notifications", "View", () => { c.Notifications.IsCenterOpen = !c.Notifications.IsCenterOpen; return Task.CompletedTask; });
        Run(ShellMenus.Welcome, "Welcome", "Help", () => { c.Well.OpenView("welcome", "Welcome", c.CreateWelcome); return Task.CompletedTask; });
        Run(ShellMenus.KeyboardReference, "Keyboard Shortcuts Reference", "Help", () => { c.Well.OpenView("keyboard", "Keyboard Shortcuts", c.CreateKeyboardReference); return Task.CompletedTask; });
        Run(ShellMenus.About, "About CsCode", "Help", async () => { await c.Dialogs.ShowAsync("About CsCode", AboutText.Build(), ["OK"]).ConfigureAwait(true); });
        // The Settings panel owns both ids (the Settings editor and the JSON file); these fallbacks only exist when no such panel is composed.
        // The panels registered their commands while the tool windows were enumerated, before this runs (see ShellController.Initialize).
        if (!c.Commands.Has(KnownCommands.OpenSettings)) Run(KnownCommands.OpenSettings, "Open Settings", "Preferences", () => OpenSettingsFile(c));
        if (!c.Commands.Has(OpenSettingsJson)) Run(OpenSettingsJson, "Open Settings (JSON)", "Preferences", () => OpenSettingsFile(c));
        Run(KnownCommands.SelectTheme, "Color Theme", "Preferences", () => PickThemeAsync(c));

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

    private static async Task SaveAsync(ShellCommandContext c, EditorTab tab)
    {
        if (tab.Doc.IsUntitled)
        {
            var target = await c.Window.ShowSaveDialogAsync(new SaveDialogOptions(Title: "Save " + tab.Title)).ConfigureAwait(true);
            if (target is not null) await tab.Doc.SaveAsAsync(target).ConfigureAwait(true);
        }
        else await tab.Doc.SaveAsync().ConfigureAwait(true);
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

    /// <summary>Quick pick of the registered themes with live preview while arrowing, reverting on Esc.</summary>
    private static async Task PickThemeAsync(ShellCommandContext c)
    {
        if (c.Themes is null) return;
        var original = c.Themes.Current;
        var items = c.Themes.Available.Select(t => new QuickPickItem(t.Label, t.Kind.ToString(), Tag: t.Id, Picked: t.Id == original.Id)).ToArray();
        var preview = new ThemePreview(c.Themes);
        var picked = await c.Palette.PickWithPreviewAsync(items, "Select Color Theme", "Select a color theme (up/down keys to preview)", e =>
        {
            if (e?.Tag is QuickPickItem { Tag: string id }) preview.Preview(id);
        }).ConfigureAwait(true);
        if (picked?.Tag is string chosen)
        {
            await c.Themes.SetCurrentAsync(chosen).ConfigureAwait(true);
            if (c.Config is not null) await c.Config.UpdateAsync(SettingKeys.WorkbenchColorTheme, chosen).ConfigureAwait(true);
        }
        else await c.Themes.SetCurrentAsync(original.Id).ConfigureAwait(true); // Esc: revert
    }

    private sealed class ThemePreview(IColorThemeService themes)
    {
        private string? _last;

        public void Preview(string id)
        {
            if (id == _last) return;
            _last = id;
            _ = themes.SetCurrentAsync(id);
        }
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
