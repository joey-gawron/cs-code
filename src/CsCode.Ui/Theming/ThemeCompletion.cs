using CsCode.Core.Theming;

namespace CsCode.Ui.Theming;

/// <summary>Where a completed theme's colour came from.</summary>
public enum ColorSource
{
    /// <summary>The theme defines the key (in <c>colors</c>, or as its global token colour for <c>editor.foreground/background</c>).</summary>
    Theme,
    /// <summary>Taken from a related key the theme does define (its title bar colour for the toolbar, its input colour for the Quick Launch box...).</summary>
    Borrowed,
    /// <summary>Filled from the nearest Visual Studio 2022 base (<see cref="VsThemes.BaseFor"/>).</summary>
    Base,
}

/// <summary>One text colour of the chrome checked against the background it is drawn on.</summary>
/// <param name="Element">What the user sees ("Menu shortcut", "Status bar").</param>
/// <param name="Under">The opaque surface a translucent background is composited over (null: the editor background).</param>
public sealed record ChromeTextPair(string Element, string Foreground, string Background, string? Under = null);

/// <summary>Result of checking one <see cref="ChromeTextPair"/> of a theme (colours composited to what is on screen).</summary>
/// <param name="Ratio">Contrast as the theme (after filling) had it.</param>
/// <param name="Repaired">The foreground the completion substituted to reach WCAG AA; null when it did not move the text colour.</param>
/// <param name="RepairedBackground">The background it substituted instead, when the text was already white (or black) on its side: white text on a
/// mid orange gets a slightly darker orange rather than black text.</param>
/// <param name="FinalRatio">Contrast after the repair (equals <paramref name="Ratio"/> when nothing was repaired).</param>
public sealed record ContrastCheck(
    ChromeTextPair Pair, Rgba Foreground, Rgba Background, double Ratio, ColorSource ForegroundSource, Rgba? Repaired, Rgba? RepairedBackground, double FinalRatio)
{
    public bool Failed => Ratio < Contrast.AaNormalText;
}

/// <summary>A completed theme with the evidence: where every published colour came from and how every chrome text pair fared.</summary>
public sealed record CompletionResult(ColorTheme Source, ColorTheme Theme, IReadOnlyDictionary<string, ColorSource> Sources, IReadOnlyList<ContrastCheck> Checks)
{
    /// <summary>Pairs below WCAG AA (4.5:1) before the completion repaired them.</summary>
    public IEnumerable<ContrastCheck> Failures => Checks.Where(c => c.Failed);
    /// <summary>Pairs still below AA after the repair (none is expected: white or black text reaches 4.58:1 on any background).</summary>
    public IEnumerable<ContrastCheck> Unresolved => Checks.Where(c => c.FinalRatio < Contrast.AaNormalText);
    public int Count(ColorSource source) => Sources.Values.Count(s => s == source);
}

/// <summary>
/// Turns any colour theme (VS Code theme JSON: a built-in, a user file) into a complete CsCode theme for the Visual Studio 2022 style shell:
/// <list type="number">
/// <item>every key the theme defines is kept as it is (and every token colour: completion only touches workbench colours);</item>
/// <item>a published chrome key it lacks is <b>borrowed</b> from the related key it does define, the way the VS 2022 layout maps onto VS Code's
/// (toolbar = title bar band, Quick Launch box = input box, tool windows = side bar, menus = dropdowns...). A foreground is borrowed only when its
/// background came from the theme too, so text and surface stay a matched pair;</item>
/// <item>anything still missing comes from the nearest VS 2022 base (Dark, Light, High Contrast, High Contrast Light), so no chrome element is ever
/// unthemed;</item>
/// <item>every chrome text colour (<see cref="ChromeText"/>) is checked against its background and, below WCAG AA (4.5:1), moved towards white or
/// black just far enough to pass. The failures and the substitutes are reported (<see cref="Analyze"/>; docs/SHELL.md lists them per theme).</item>
/// </list>
/// Pure and deterministic; a complete theme that passes every check is returned as the same instance.
/// </summary>
public static class ThemeCompletion
{
    private abstract record Rule(string Key);
    /// <summary>Borrow the first of <paramref name="Sources"/> the theme defines; with <paramref name="PairedBackground"/>, only when that background is the theme's too.</summary>
    private sealed record From(string Key, string? PairedBackground, string[] Sources) : Rule(Key);
    /// <summary><paramref name="A"/> mixed towards <paramref name="B"/> by <paramref name="T"/> (a dimmed text colour), when the theme defines both.</summary>
    private sealed record Mixed(string Key, string A, string B, double T) : Rule(Key);
    /// <summary><paramref name="Source"/> made translucent (its alpha times <paramref name="Alpha"/>): an overlay in the theme's accent.</summary>
    private sealed record Translucent(string Key, string Source, double Alpha) : Rule(Key);

    private static From F(string key, params string[] sources) => new(key, null, sources);
    private static From Fg(string key, string background, params string[] sources) => new(key, background, sources);

    // In dependency order: a rule may borrow only keys defined by the theme or borrowed by an earlier rule.
    private static readonly Rule[] Rules =
    [
        // Workbench text.
        F("foreground", "editor.foreground"),
        new Mixed("descriptionForeground", "foreground", "editor.background", 0.3),
        new Mixed("disabledForeground", "foreground", "editor.background", 0.5),
        F("icon.foreground", "foreground"),
        F("errorForeground", "editorError.foreground"),
        F("textLink.foreground", "editorLink.activeForeground"),
        F("textLink.activeForeground", "textLink.foreground"),
        F("progressBar.background", "focusBorder"),
        F("sash.hoverBorder", "focusBorder"),

        // Title bar, Quick Launch, toolbar and menus: Visual Studio draws them as one band.
        F("titleBar.activeBackground", "editorGroupHeader.tabsBackground", "sideBar.background"),
        Fg("titleBar.activeForeground", "titleBar.activeBackground", "foreground"),
        F("titleBar.inactiveBackground", "titleBar.activeBackground"),
        Fg("titleBar.inactiveForeground", "titleBar.inactiveBackground", "titleBar.activeForeground"),
        F("titleBar.border", "contrastBorder"),
        F("toolbar.background", "titleBar.activeBackground"),
        Fg("toolbar.foreground", "toolbar.background", "titleBar.activeForeground"),
        F("toolbar.separator", "contrastBorder", "panel.border", "sideBar.border"),
        F("toolbar.hoverBackground", "list.hoverBackground"),
        F("toolbar.activeBackground", "toolbar.hoverBackground"),
        F("menubar.selectionBackground", "toolbar.hoverBackground"),
        Fg("menubar.selectionForeground", "menubar.selectionBackground", "titleBar.activeForeground"),
        F("menubar.selectionBorder", "contrastActiveBorder"),
        F("commandCenter.background", "input.background", "dropdown.background"),
        Fg("commandCenter.foreground", "commandCenter.background", "input.foreground", "foreground"),
        F("commandCenter.border", "input.border", "contrastBorder", "dropdown.border"),
        F("menu.background", "dropdown.background", "editorWidget.background", "sideBar.background"),
        Fg("menu.foreground", "menu.background", "dropdown.foreground", "foreground"),
        F("menu.selectionBackground", "list.activeSelectionBackground", "quickInputList.focusBackground"),
        Fg("menu.selectionForeground", "menu.selectionBackground", "list.activeSelectionForeground", "menu.foreground"),
        F("menu.border", "contrastBorder", "dropdown.border"),
        F("menu.separatorBackground", "menu.border", "contrastBorder", "panel.border"),

        // Tool windows (VS: side bar colours for every docked window, bottom ones included).
        F("sideBar.background", "panel.background", "editorGroupHeader.tabsBackground"),
        Fg("sideBar.foreground", "sideBar.background", "foreground"),
        F("sideBar.border", "contrastBorder", "panel.border"),
        Fg("sideBarTitle.foreground", "sideBar.background", "sideBar.foreground"),
        F("sideBarSectionHeader.background", "editorGroupHeader.tabsBackground", "titleBar.activeBackground"),
        Fg("sideBarSectionHeader.foreground", "sideBarSectionHeader.background", "sideBarTitle.foreground", "sideBar.foreground", "foreground"),
        F("sideBarSectionHeader.border", "contrastBorder", "sideBar.border"),
        F("panel.background", "sideBar.background", "editor.background"),
        F("panel.border", "contrastBorder", "sideBar.border", "editorGroup.border"),
        Fg("panelTitle.activeForeground", "panel.background", "sideBar.foreground", "foreground"),
        Fg("panelTitle.inactiveForeground", "panel.background", "descriptionForeground"),
        F("panelTitle.activeBorder", "focusBorder"),
        F("breadcrumb.background", "editor.background"),
        F("breadcrumb.foreground", "descriptionForeground"),
        F("breadcrumb.focusForeground", "foreground"),
        F("tree.indentGuidesStroke", "editorIndentGuide.background"),

        // Document well.
        F("editorGroupHeader.tabsBackground", "titleBar.activeBackground", "sideBar.background"),
        F("editorGroupHeader.noTabsBackground", "editorGroupHeader.tabsBackground"),
        F("editorGroupHeader.tabsBorder", "contrastBorder"),
        F("editorGroup.border", "contrastBorder", "panel.border"),
        F("editorGroup.dropBackground", "list.dropBackground"),
        F("tab.activeBackground", "editor.background"),
        Fg("tab.activeForeground", "tab.activeBackground", "editor.foreground", "foreground"),
        F("tab.inactiveBackground", "editorGroupHeader.tabsBackground"),
        new Mixed("tab.inactiveForeground", "tab.activeForeground", "tab.inactiveBackground", 0.3),
        F("tab.hoverBackground", "list.hoverBackground"),
        F("tab.hoverForeground", "tab.activeForeground"),
        F("tab.unfocusedActiveBackground", "tab.activeBackground"),
        F("tab.unfocusedActiveForeground", "tab.activeForeground"),
        F("tab.unfocusedInactiveBackground", "tab.inactiveBackground"),
        F("tab.unfocusedInactiveForeground", "tab.inactiveForeground"),
        F("tab.activeBorderTop", "tab.activeBorder", "focusBorder"),
        F("tab.dirtyForeground", "tab.activeForeground"),
        F("tab.previewForeground", "tab.inactiveForeground"),

        // Status bar: a theme's own foreground follows only its own background (a base debugging orange keeps the base's white text).
        Fg("statusBar.debuggingForeground", "statusBar.debuggingBackground", "statusBar.foreground"),
        Fg("statusBar.noFolderForeground", "statusBar.noFolderBackground", "statusBar.foreground"),
        F("statusBar.border", "contrastBorder"),
        F("statusBar.noFolderBorder", "statusBar.border"),
        // The remote indicator's badge: VS Code's default text is the activity bar badge's (a theme that sets only the badge's background got the
        // base's white, 1.5:1 on One Light's grey); the contrast check below repairs what is still too faint.
        Fg("statusBarItem.remoteForeground", "statusBarItem.remoteBackground", "activityBarBadge.foreground", "statusBar.foreground"),

        // Inputs, buttons, lists, pickers, widgets.
        F("input.background", "dropdown.background", "editorWidget.background"),
        Fg("input.foreground", "input.background", "foreground"),
        F("input.border", "dropdown.border", "contrastBorder"),
        Fg("input.placeholderForeground", "input.background", "descriptionForeground"),
        F("dropdown.background", "input.background"),
        Fg("dropdown.foreground", "dropdown.background", "input.foreground", "foreground"),
        F("dropdown.listBackground", "dropdown.background"),
        F("dropdown.border", "input.border", "contrastBorder"),
        F("checkbox.background", "input.background"),
        Fg("checkbox.foreground", "checkbox.background", "foreground"),
        F("checkbox.border", "input.border", "contrastBorder"),
        F("inputOption.activeBorder", "focusBorder"),
        // Checked toggles (Error List severity chips, search options): VS Code's default is the focus colour at 40 %.
        new Translucent("inputOption.activeBackground", "focusBorder", 0.4),
        F("list.activeSelectionBackground", "quickInputList.focusBackground", "list.focusBackground"),
        Fg("list.activeSelectionForeground", "list.activeSelectionBackground", "foreground"),
        Fg("list.inactiveSelectionForeground", "list.inactiveSelectionBackground", "foreground"),
        Fg("list.hoverForeground", "list.hoverBackground", "foreground"),
        F("list.focusOutline", "focusBorder"),
        F("quickInput.background", "editorWidget.background", "sideBar.background"),
        Fg("quickInput.foreground", "quickInput.background", "editorWidget.foreground", "foreground"),
        F("quickInputList.focusBackground", "list.activeSelectionBackground"),
        Fg("quickInputList.focusForeground", "quickInputList.focusBackground", "list.activeSelectionForeground", "quickInput.foreground"),
        F("pickerGroup.border", "panel.border"),
        F("editorWidget.background", "quickInput.background", "sideBar.background"),
        Fg("editorWidget.foreground", "editorWidget.background", "foreground"),
        F("editorWidget.border", "contrastBorder", "panel.border"),
        F("editorSuggestWidget.background", "editorWidget.background"),
        Fg("editorSuggestWidget.foreground", "editorSuggestWidget.background", "editorWidget.foreground"),
        F("editorSuggestWidget.border", "editorWidget.border"),
        F("editorSuggestWidget.selectedBackground", "list.activeSelectionBackground"),
        F("editorSuggestWidget.highlightForeground", "list.highlightForeground"),
        F("editorHoverWidget.background", "editorWidget.background"),
        Fg("editorHoverWidget.foreground", "editorHoverWidget.background", "editorWidget.foreground"),
        F("editorHoverWidget.border", "editorWidget.border"),
        F("notifications.background", "editorWidget.background", "quickInput.background"),
        Fg("notifications.foreground", "notifications.background", "editorWidget.foreground", "foreground"),
        F("notifications.border", "editorWidget.border"),
        F("notificationCenter.border", "notifications.border"),
        F("notificationToast.border", "notifications.border"),
        F("notificationCenterHeader.background", "notifications.background"),
        Fg("notificationCenterHeader.foreground", "notificationCenterHeader.background", "notifications.foreground"),
        F("button.hoverBackground", "button.background"),
        F("button.secondaryBackground", "dropdown.background", "input.background"),
        Fg("button.secondaryForeground", "button.secondaryBackground", "dropdown.foreground", "foreground"),
        F("button.secondaryHoverBackground", "list.hoverBackground"),
        F("badge.background", "activityBarBadge.background", "button.background"),
        Fg("badge.foreground", "badge.background", "activityBarBadge.foreground", "button.foreground"),

        // Editor extras, terminal, welcome page.
        F("editorGutter.background", "editor.background"),
        F("editorCursor.foreground", "editor.foreground"),
        F("terminal.background", "panel.background", "editor.background"),
        Fg("terminal.foreground", "terminal.background", "editor.foreground"),
        F("welcomePage.background", "editor.background"),
        F("welcomePage.tileBackground", "sideBar.background"),
        F("welcomePage.tileHoverBackground", "list.hoverBackground"),
        F("welcomePage.tileBorder", "panel.border"),
        F("debugToolBar.background", "titleBar.activeBackground"),
    ];

    /// <summary>
    /// Every text colour the shell draws in its chrome, with the surface it is drawn on (taken from the views: <c>TitleBarView</c>,
    /// <c>ToolbarView</c>, <c>ShellMenuBar</c>, the Dock tab strips, the tool windows, <c>StatusBarView</c>, the quick input, the Settings page...).
    /// Disabled and inactive-window text is exempt, as in WCAG.
    /// </summary>
    public static IReadOnlyList<ChromeTextPair> ChromeText { get; } =
    [
        new("Title bar and menu bar", "titleBar.activeForeground", "titleBar.activeBackground"),
        new("Quick Launch box", "descriptionForeground", "commandCenter.background", "titleBar.activeBackground"),
        new("Workspace name (title bar)", "commandCenter.foreground", "commandCenter.background", "titleBar.activeBackground"),
        new("Toolbar", "toolbar.foreground", "toolbar.background"),
        new("Dropdown (toolbar, Settings)", "dropdown.foreground", "dropdown.background", "toolbar.background"),
        new("Menu", "menu.foreground", "menu.background"),
        new("Menu shortcut", "descriptionForeground", "menu.background"),
        new("Menu, highlighted item", "menu.selectionForeground", "menu.selectionBackground", "menu.background"),
        new("Document tab, active", "tab.activeForeground", "tab.activeBackground", "editorGroupHeader.tabsBackground"),
        new("Document tab, inactive", "tab.inactiveForeground", "tab.inactiveBackground", "editorGroupHeader.tabsBackground"),
        new("Tool window text", "foreground", "sideBar.background"),
        new("Tool window, side bar text", "sideBar.foreground", "sideBar.background"),
        new("Tool window, secondary text", "descriptionForeground", "sideBar.background"),
        new("Bottom panel text", "foreground", "panel.background"),
        new("Bottom panel, secondary text", "descriptionForeground", "panel.background"),
        new("Column and section headers", "sideBarSectionHeader.foreground", "sideBarSectionHeader.background", "panel.background"),
        new("Group headers", "foreground", "sideBarSectionHeader.background", "panel.background"),
        new("Settings page text", "foreground", "editor.background"),
        new("Settings page, secondary text", "descriptionForeground", "editor.background"),
        new("Settings scope tab, active", "panelTitle.activeForeground", "editor.background"),
        new("Settings scope tab, inactive", "panelTitle.inactiveForeground", "editor.background"),
        new("Link", "textLink.foreground", "editor.background"),
        new("Error message", "errorForeground", "editor.background"),
        new("Status bar", "statusBar.foreground", "statusBar.background"),
        new("Status bar, debugging", "statusBar.debuggingForeground", "statusBar.debuggingBackground"),
        new("Status bar, no folder", "statusBar.noFolderForeground", "statusBar.noFolderBackground"),
        new("Status bar, remote badge", "statusBarItem.remoteForeground", "statusBarItem.remoteBackground", "statusBar.background"),
        new("List selection", "list.activeSelectionForeground", "list.activeSelectionBackground", "sideBar.background"),
        new("List selection, unfocused", "list.inactiveSelectionForeground", "list.inactiveSelectionBackground", "sideBar.background"),
        new("Quick pick", "quickInput.foreground", "quickInput.background"),
        new("Quick pick, focused row", "quickInputList.focusForeground", "quickInputList.focusBackground", "quickInput.background"),
        new("Input box", "input.foreground", "input.background"),
        new("Input placeholder", "input.placeholderForeground", "input.background"),
        new("Button", "button.foreground", "button.background"),
        new("Secondary button", "button.secondaryForeground", "button.secondaryBackground"),
        new("Badge", "badge.foreground", "badge.background", "panel.background"),
        new("Toggle chip, on (Error List)", "foreground", "inputOption.activeBackground", "panel.background"),
        new("Notification", "notifications.foreground", "notifications.background"),
        new("Hover and tooltip", "editorHoverWidget.foreground", "editorHoverWidget.background"),
        new("Completion list", "editorSuggestWidget.foreground", "editorSuggestWidget.background"),
        new("Editor widget (find, peek)", "editorWidget.foreground", "editorWidget.background"),
    ];

    /// <summary>The completed theme (see <see cref="Analyze"/> for the evidence).</summary>
    public static ColorTheme Complete(ColorTheme theme) => Analyze(theme).Theme;

    /// <summary>True when the theme defines every published colour id (it still may fail a contrast check).</summary>
    public static bool DefinesAll(ColorTheme theme) => WorkbenchColors.All.All(theme.Colors.ContainsKey);

    public static CompletionResult Analyze(ColorTheme theme)
    {
        var baseTheme = VsThemes.BaseFor(theme.Kind);
        var colors = new Dictionary<string, Rgba>(theme.Colors, StringComparer.Ordinal);
        var sources = new Dictionary<string, ColorSource>(StringComparer.Ordinal);
        foreach (var key in theme.Colors.Keys) sources[key] = ColorSource.Theme;
        var changed = false;

        // VS Code paints the editor with the theme's global token colours when "colors" has no editor.foreground/background (Quiet Light, Abyss...).
        foreach (var rule in theme.TokenColors)
        {
            if (rule.Scopes.Count != 0) continue;
            if (!sources.ContainsKey("editor.foreground") && Rgba.TryParse(rule.Foreground, out var fg)) Set("editor.foreground", fg, ColorSource.Theme);
            if (!sources.ContainsKey("editor.background") && Rgba.TryParse(rule.Background, out var bg)) Set("editor.background", bg, ColorSource.Theme);
        }

        bool Derived(string key) => sources.TryGetValue(key, out var s) && s != ColorSource.Base;
        void Set(string key, Rgba value, ColorSource source)
        {
            colors[key] = value;
            sources[key] = source;
            changed = true;
        }

        foreach (var rule in Rules)
        {
            if (sources.ContainsKey(rule.Key)) continue;
            switch (rule)
            {
                case From f when f.PairedBackground is null || Derived(f.PairedBackground):
                    foreach (var s in f.Sources)
                        if (Derived(s)) { Set(f.Key, colors[s], ColorSource.Borrowed); break; }
                    break;
                case Mixed m when Derived(m.A) && Derived(m.B):
                    var under = Opaque(colors, m.B, null);
                    Set(m.Key, Contrast.Mix(Contrast.Over(colors[m.A], under), under, m.T), ColorSource.Borrowed);
                    break;
                case Translucent t when Derived(t.Source):
                    var c = colors[t.Source];
                    Set(t.Key, c with { A = (byte)Math.Round(c.A * t.Alpha) }, ColorSource.Borrowed);
                    break;
            }
        }
        foreach (var id in WorkbenchColors.All)
            if (!sources.ContainsKey(id)) Set(id, baseTheme.Colors[id], ColorSource.Base);

        // Contrast: judge every pair as drawn, then repair the failing ones with the smallest change: the text colour moved further from its
        // background on its own side, or, when the text is already white (black) on that side, the background moved away from the text. A colour
        // shared by several pairs (descriptionForeground on menus, tool windows and the Settings page) is re-checked against all of them until
        // nothing moves.
        var before = ChromeText.Select(p => (Fg: Contrast.Over(colors[p.Foreground], Opaque(colors, p.Background, p.Under)), Bg: Opaque(colors, p.Background, p.Under))).ToArray();
        var repairedFg = new Rgba?[ChromeText.Count];
        var repairedBg = new Rgba?[ChromeText.Count];
        for (var pass = 0; pass < 4; pass++)
        {
            var moved = false;
            for (var i = 0; i < ChromeText.Count; i++)
            {
                var p = ChromeText[i];
                var bg = Opaque(colors, p.Background, p.Under);
                if (Contrast.Ratio(Contrast.Over(colors[p.Foreground], bg), bg) >= Contrast.AaNormalText) continue;
                if (Contrast.MoveForeground(colors[p.Foreground], bg) is { } fg) repairedFg[i] = colors[p.Foreground] = fg;
                else if (Contrast.MoveBackground(bg, colors[p.Foreground]) is { } newBg) repairedBg[i] = colors[p.Background] = newBg;
                else repairedFg[i] = colors[p.Foreground] = Contrast.BestExtreme(bg);
                changed = moved = true;
            }
            if (!moved) break;
        }
        var checks = new ContrastCheck[ChromeText.Count];
        for (var i = 0; i < checks.Length; i++)
        {
            var p = ChromeText[i];
            var bg = Opaque(colors, p.Background, p.Under);
            var finalRatio = Contrast.Ratio(Contrast.Over(colors[p.Foreground], bg), bg);
            checks[i] = new ContrastCheck(p, before[i].Fg, before[i].Bg, Contrast.Ratio(before[i].Fg, before[i].Bg), sources[p.Foreground], repairedFg[i], repairedBg[i], finalRatio);
        }

        var completed = changed ? theme with { Colors = colors } : theme;
        return new CompletionResult(theme, completed, sources, checks);
    }

    /// <summary>A background as it appears on screen: translucent colours are composited over <paramref name="under"/> (default: the editor background), that over black.</summary>
    private static Rgba Opaque(IReadOnlyDictionary<string, Rgba> colors, string key, string? under, int depth = 0)
    {
        var c = colors.TryGetValue(key, out var v) ? v : new Rgba(0, 0, 0);
        if (c.A == 255) return c;
        if (depth > 3) return Contrast.Over(c, new Rgba(0, 0, 0));
        var surface = under ?? (key == "editor.background" ? null : "editor.background");
        return Contrast.Over(c, surface is null ? new Rgba(0, 0, 0) : Opaque(colors, surface, null, depth + 1));
    }
}
