namespace CsCode.Ui.Theming;

/// <summary>
/// The catalogue of workbench colour ids the shell publishes as application resources: every id here becomes a
/// <c>SolidColorBrush</c> under <c>vsc:&lt;id&gt;</c> (use <c>{DynamicResource vsc:editor.background}</c>). Panel authors may rely on
/// exactly these keys (documented in docs/SHELL.md; a test keeps the doc and this list in sync). Values come from
/// <c>IColorThemeService.GetColor</c>, i.e. the Core fallback chain applies to ids a theme does not define.
/// </summary>
public static class WorkbenchColors
{
    public const string BrushPrefix = "vsc:";

    public static string BrushKey(string colorId) => BrushPrefix + colorId;

    /// <summary>Group name to ids, in documentation order.</summary>
    public static IReadOnlyList<(string Group, string[] Ids)> Groups { get; } =
    [
        ("Base", [
            "foreground", "descriptionForeground", "disabledForeground", "errorForeground", "focusBorder", "contrastBorder", "contrastActiveBorder",
            "icon.foreground", "widget.shadow", "selection.background", "textLink.foreground", "textLink.activeForeground",
            "textCodeBlock.background", "textSeparator.foreground", "sash.hoverBorder", "progressBar.background"]),
        ("Scrollbars", ["scrollbar.shadow", "scrollbarSlider.background", "scrollbarSlider.hoverBackground", "scrollbarSlider.activeBackground"]),
        ("Buttons and inputs", [
            "button.background", "button.foreground", "button.hoverBackground", "button.border",
            "button.secondaryBackground", "button.secondaryForeground", "button.secondaryHoverBackground",
            "checkbox.background", "checkbox.foreground", "checkbox.border",
            "dropdown.background", "dropdown.foreground", "dropdown.border", "dropdown.listBackground",
            "input.background", "input.foreground", "input.border", "input.placeholderForeground",
            "inputOption.activeBorder", "inputOption.activeBackground", "inputOption.activeForeground",
            "inputValidation.errorBackground", "inputValidation.errorBorder", "inputValidation.errorForeground",
            "inputValidation.warningBackground", "inputValidation.warningBorder", "inputValidation.infoBackground", "inputValidation.infoBorder",
            "badge.background", "badge.foreground", "toolbar.hoverBackground", "toolbar.activeBackground"]),
        ("Lists and trees", [
            "list.activeSelectionBackground", "list.activeSelectionForeground", "list.inactiveSelectionBackground", "list.inactiveSelectionForeground",
            "list.hoverBackground", "list.hoverForeground", "list.focusBackground", "list.focusOutline", "list.highlightForeground",
            "list.errorForeground", "list.warningForeground", "list.dropBackground", "list.invalidItemForeground", "list.filterMatchBackground",
            "tree.indentGuidesStroke"]),
        ("Side bar and panel", [
            "sideBar.background", "sideBar.foreground", "sideBar.border", "sideBarTitle.foreground",
            "sideBarSectionHeader.background", "sideBarSectionHeader.foreground", "sideBarSectionHeader.border",
            "panel.background", "panel.border", "panelTitle.activeForeground", "panelTitle.inactiveForeground", "panelTitle.activeBorder",
            "breadcrumb.foreground", "breadcrumb.background", "breadcrumb.focusForeground"]),
        ("Editor groups and tabs", [
            "editorGroup.border", "editorGroup.dropBackground", "editorGroupHeader.tabsBackground", "editorGroupHeader.tabsBorder", "editorGroupHeader.noTabsBackground",
            "tab.activeBackground", "tab.activeForeground", "tab.inactiveBackground", "tab.inactiveForeground", "tab.border",
            "tab.activeBorder", "tab.activeBorderTop", "tab.hoverBackground", "tab.hoverForeground",
            "tab.unfocusedActiveBackground", "tab.unfocusedActiveForeground", "tab.unfocusedInactiveBackground", "tab.unfocusedInactiveForeground",
            "tab.lastPinnedBorder", "tab.dirtyForeground", "tab.previewForeground"]),
        ("Status bar, title bar, menus", [
            "statusBar.background", "statusBar.foreground", "statusBar.border", "statusBar.debuggingBackground", "statusBar.debuggingForeground",
            "statusBar.noFolderBackground", "statusBar.noFolderForeground", "statusBar.noFolderBorder", "statusBarItem.hoverBackground", "statusBarItem.prominentBackground",
            "titleBar.activeBackground", "titleBar.activeForeground", "titleBar.inactiveBackground", "titleBar.inactiveForeground", "titleBar.border",
            "menu.background", "menu.foreground", "menu.selectionBackground", "menu.selectionForeground", "menu.separatorBackground", "menu.border",
            "menubar.selectionBackground", "menubar.selectionForeground", "menubar.selectionBorder",
            "commandCenter.background", "commandCenter.foreground", "commandCenter.border"]),
        ("Quick input and notifications", [
            "quickInput.background", "quickInput.foreground", "quickInputList.focusBackground", "quickInputList.focusForeground", "quickInputTitle.background", "pickerGroup.foreground", "pickerGroup.border",
            "notifications.background", "notifications.foreground", "notifications.border", "notificationCenter.border", "notificationCenterHeader.background", "notificationCenterHeader.foreground",
            "notificationToast.border", "notificationLink.foreground", "notificationsErrorIcon.foreground", "notificationsWarningIcon.foreground", "notificationsInfoIcon.foreground"]),
        ("Editor", [
            "editor.background", "editor.foreground", "editor.selectionBackground", "editor.inactiveSelectionBackground", "editor.lineHighlightBackground", "editor.lineHighlightBorder",
            "editor.findMatchBackground", "editor.findMatchHighlightBackground", "editor.wordHighlightBackground", "editor.stackFrameHighlightBackground", "editor.focusedStackFrameHighlightBackground",
            "editorCursor.foreground", "editorLineNumber.foreground", "editorLineNumber.activeForeground", "editorIndentGuide.background", "editorWhitespace.foreground",
            "editorBracketMatch.background", "editorBracketMatch.border", "editorGutter.background",
            "editorError.foreground", "editorWarning.foreground", "editorInfo.foreground", "editorHint.foreground",
            "editorWidget.background", "editorWidget.foreground", "editorWidget.border",
            "editorSuggestWidget.background", "editorSuggestWidget.border", "editorSuggestWidget.foreground", "editorSuggestWidget.selectedBackground", "editorSuggestWidget.highlightForeground",
            "editorHoverWidget.background", "editorHoverWidget.foreground", "editorHoverWidget.border", "editorOverviewRuler.border",
            "diffEditor.insertedTextBackground", "diffEditor.removedTextBackground"]),
        ("Debug, problems, git", [
            "debugToolBar.background", "debugToolBar.border", "debugIcon.breakpointForeground", "debugIcon.startForeground", "debugIcon.stopForeground", "debugIcon.pauseForeground",
            "debugIcon.continueForeground", "debugIcon.stepOverForeground", "debugIcon.stepIntoForeground", "debugIcon.stepOutForeground", "debugIcon.restartForeground",
            "problemsErrorIcon.foreground", "problemsWarningIcon.foreground", "problemsInfoIcon.foreground",
            "gitDecoration.modifiedResourceForeground", "gitDecoration.addedResourceForeground", "gitDecoration.deletedResourceForeground",
            "gitDecoration.untrackedResourceForeground", "gitDecoration.ignoredResourceForeground", "gitDecoration.conflictingResourceForeground"]),
        ("Shell-specific (not VS Code ids; user themes may omit them, ShellFallbacks applies)", [
            "toolbar.background", "toolbar.foreground", "toolbar.separator", "statusBarItem.activeBackground"]),
        ("Terminal and welcome", [
            "terminal.background", "terminal.foreground",
            "terminal.ansiBlack", "terminal.ansiRed", "terminal.ansiGreen", "terminal.ansiYellow", "terminal.ansiBlue", "terminal.ansiMagenta", "terminal.ansiCyan", "terminal.ansiWhite",
            "terminal.ansiBrightBlack", "terminal.ansiBrightRed", "terminal.ansiBrightGreen", "terminal.ansiBrightYellow", "terminal.ansiBrightBlue", "terminal.ansiBrightMagenta", "terminal.ansiBrightCyan", "terminal.ansiBrightWhite",
            "welcomePage.background", "welcomePage.tileBackground", "welcomePage.tileHoverBackground", "welcomePage.tileBorder"]),
    ];

    /// <summary>
    /// Fallbacks for the shell-specific ids when a (user) theme does not define them. Consulted BEFORE the Core chain because
    /// the Core chain only knows VS Code ids.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ShellFallbacks { get; } = new Dictionary<string, string>
    {
        ["toolbar.background"] = "titleBar.activeBackground",
        ["toolbar.foreground"] = "titleBar.activeForeground",
        ["toolbar.separator"] = "panel.border",
        ["statusBarItem.activeBackground"] = "statusBarItem.hoverBackground",
        ["tab.dirtyForeground"] = "tab.activeForeground",
        ["tab.previewForeground"] = "tab.inactiveForeground",
        ["welcomePage.tileBorder"] = "panel.border",
    };

    /// <summary>Every published colour id, de-duplicated, in documentation order.</summary>
    public static IReadOnlyList<string> All { get; } = Groups.SelectMany(g => g.Ids).Distinct(StringComparer.Ordinal).ToArray();
}
