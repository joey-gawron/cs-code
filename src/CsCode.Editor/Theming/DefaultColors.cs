using CsCode.Core.Theming;

namespace CsCode.Editor.Theming;

/// <summary>
/// The colour a theme gets for a key it does not define: VS Code's documented defaults (dark / light / high-contrast) plus the
/// derivation chain. A key's rule is an ordered list of steps; the first that yields a colour wins:
/// <list type="bullet">
/// <item><c>Themed(k)</c> — another key, but only if the THEME itself defines it (so a theme that sets <c>editorGroupHeader.tabsBackground</c>
/// but not <c>tab.inactiveBackground</c> gets a consistent tab strip);</item>
/// <item><c>Ref(k, alpha)</c> — another key fully resolved (theme, then its own defaults), optionally with its alpha multiplied;</item>
/// <item><c>Lit(dark, light, hc)</c> — a literal per theme kind.</item>
/// </list>
/// </summary>
public static class DefaultColors
{
    private abstract record Step;
    private sealed record Themed(string Key) : Step;
    private sealed record Ref(string Key, double Alpha) : Step;
    private sealed record Lit(string Dark, string Light, string Hc, string HcLight) : Step;

    private static readonly Dictionary<string, Step[]> Rules = Build();

    /// <summary>All keys that have a default (the documented set the editor and shell rely on).</summary>
    public static IReadOnlyCollection<string> KnownKeys => Rules.Keys;

    /// <summary>
    /// Resolves <paramref name="key"/> for a theme: explicit theme colour, else the fallback chain, else null (unknown key).
    /// </summary>
    public static Rgba? Resolve(string key, IReadOnlyDictionary<string, Rgba> themeColors, ThemeKind kind) => Resolve(key, themeColors, kind, 0);

    private static Rgba? Resolve(string key, IReadOnlyDictionary<string, Rgba> themeColors, ThemeKind kind, int depth)
    {
        if (themeColors.TryGetValue(key, out var explicitColor)) return explicitColor;
        if (depth > 8 || !Rules.TryGetValue(key, out var steps)) return null; // depth guard: a cyclic chain can never hang the UI
        foreach (var step in steps)
        {
            switch (step)
            {
                case Themed t when themeColors.TryGetValue(t.Key, out var c):
                    return c;
                case Ref r when Resolve(r.Key, themeColors, kind, depth + 1) is { } rc:
                    return r.Alpha >= 1 ? rc : rc with { A = (byte)Math.Round(rc.A * r.Alpha) };
                case Lit l:
                {
                    var hex = kind switch { ThemeKind.Light => l.Light, ThemeKind.HighContrastDark => l.Hc, ThemeKind.HighContrastLight => l.HcLight, _ => l.Dark };
                    return hex.Length == 0 ? new Rgba(0, 0, 0, 0) : Rgba.Parse(hex);
                }
            }
        }
        return null;
    }

    private static Dictionary<string, Step[]> Build()
    {
        var d = new Dictionary<string, Step[]>(StringComparer.Ordinal);
        // Lit with dark/light and optional high-contrast (hc defaults to dark / light respectively).
        Step L(string dark, string? light = null, string? hc = null, string? hcLight = null) => new Lit(dark, light ?? dark, hc ?? dark, hcLight ?? light ?? dark);
        void Def(string key, string dark, string? light = null, string? hc = null, string? hcLight = null) => d[key] = [L(dark, light, hc, hcLight)];
        void Alias(string key, string target) => d[key] = [new Ref(target, 1)];
        void Alpha(string key, string target, double alpha) => d[key] = [new Ref(target, alpha)];
        void Chain(string key, params Step[] steps) => d[key] = steps;

        // ---- base ----
        Def("foreground", "#CCCCCC", "#616161", "#FFFFFF", "#292929");
        Def("errorForeground", "#F48771", "#A1260D", "#F48771", "#B5200D");
        Alpha("descriptionForeground", "foreground", 0.7);
        Def("icon.foreground", "#C5C5C5", "#424242", "#FFFFFF", "#292929");
        Def("focusBorder", "#007FD4", "#0090F1", "#F38518", "#006BBD");
        Def("contrastBorder", "", "", "#6FC3DF", "#0F4A85");
        Def("contrastActiveBorder", "", "", "#F38518", "#006BBD");
        Def("selection.background", "");
        Def("textLink.foreground", "#3794FF", "#006AB1", "#21A6FF", "#0F4A85");
        Alias("textLink.activeForeground", "textLink.foreground");
        Def("textCodeBlock.background", "#0A0A0A66", "#DCDCDC66", "#0A0A0A66", "#DCDCDC66");
        Def("textPreformat.foreground", "#D7BA7D", "#A31515", "#D7BA7D", "#A31515");
        Def("textBlockQuote.background", "#7F7F7F1A");
        Def("textBlockQuote.border", "#007ACC80", "#007ACC80", "#FFFFFF", "#292929");
        Def("textSeparator.foreground", "#FFFFFF2E", "#0000002E", "#FFFFFF", "#292929");
        Def("widget.shadow", "#0000005C", "#00000029", "", "");
        Def("scrollbar.shadow", "#000000", "#DDDDDD");
        Def("scrollbarSlider.background", "#79797966", "#64646466");
        Def("scrollbarSlider.hoverBackground", "#646464B3", "#646464B3");
        Def("scrollbarSlider.activeBackground", "#BFBFBF66", "#00000099");
        Def("progressBar.background", "#0E70C0");
        Def("badge.background", "#4D4D4D", "#C4C4C4", "#000000", "#0F4A85");
        Def("badge.foreground", "#FFFFFF", "#333333", "#FFFFFF", "#FFFFFF");

        // ---- editor ----
        Def("editor.background", "#1E1E1E", "#FFFFFF", "#000000", "#FFFFFF");
        Def("editor.foreground", "#D4D4D4", "#333333", "#FFFFFF", "#292929");
        Def("editorLineNumber.foreground", "#858585", "#237893", "#FFFFFF", "#292929");
        Def("editorLineNumber.activeForeground", "#C6C6C6", "#0B216F", "#FFFFFF", "#292929");
        Alias("editorLineNumber.dimmedForeground", "editorLineNumber.foreground");
        Def("editorCursor.foreground", "#AEAFAD", "#000000", "#FFFFFF", "#292929");
        Alias("editorCursor.background", "editor.background");
        Def("editor.selectionBackground", "#264F78", "#ADD6FF", "#FFFFFF", "#0F4A85");
        Chain("editor.inactiveSelectionBackground", new Ref("editor.selectionBackground", 0.5));
        Chain("editor.selectionHighlightBackground", new Ref("editor.selectionBackground", 0.5));
        Def("editor.wordHighlightBackground", "#575757B8", "#57575740");
        Def("editor.wordHighlightStrongBackground", "#004972B8", "#0E639C40");
        Def("editor.wordHighlightTextBackground", "#575757B8", "#57575740");
        Def("editor.findMatchBackground", "#515C6A", "#A8AC94");
        Def("editor.findMatchHighlightBackground", "#EA5C0055", "#EA5C0055");
        Def("editor.findRangeHighlightBackground", "#3A3D4166", "#B4B4B44D");
        Def("editor.hoverHighlightBackground", "#264F7840", "#ADD6FF26");
        Def("editor.lineHighlightBackground", "#FFFFFF0A", "#0000000A", "", "");
        Def("editor.lineHighlightBorder", "#282828", "#EEEEEE", "#F38518", "#0F4A85");
        Def("editor.rangeHighlightBackground", "#FFFFFF0B", "#FDFF0033");
        Def("editor.snippetTabstopHighlightBackground", "#7C7C7C4D", "#0A326433");
        Def("editor.snippetFinalTabstopHighlightBorder", "#525252", "#0A326480");
        Def("editor.stackFrameHighlightBackground", "#FFFF0033", "#FFFF6673");
        Def("editor.focusedStackFrameHighlightBackground", "#7ABD7A4D", "#CEE7CE73");
        Def("editorWhitespace.foreground", "#E3E4E229", "#33333333", "#E3E4E229", "#33333333");
        Def("editorIndentGuide.background1", "#404040", "#D3D3D3", "#FFFFFF4D", "#0F4A854D");
        Def("editorIndentGuide.activeBackground1", "#707070", "#939393", "#FFFFFF", "#292929");
        Alias("editorIndentGuide.background", "editorIndentGuide.background1");
        Alias("editorIndentGuide.activeBackground", "editorIndentGuide.activeBackground1");
        Def("editorRuler.foreground", "#5A5A5A", "#D3D3D3", "#FFFFFF", "#292929");
        Def("editorBracketMatch.background", "#0064001A", "#0064001A");
        Def("editorBracketMatch.border", "#888888", "#B9B9B9", "#FFFFFF", "#0F4A85");
        Def("editorBracketHighlight.foreground1", "#FFD700", "#0431FA");
        Def("editorBracketHighlight.foreground2", "#DA70D6", "#319331");
        Def("editorBracketHighlight.foreground3", "#179FFF", "#7B3814");
        Def("editorBracketHighlight.foreground4", "#FFD700", "#0431FA");
        Def("editorBracketHighlight.foreground5", "#DA70D6", "#319331");
        Def("editorBracketHighlight.foreground6", "#179FFF", "#7B3814");
        Def("editorBracketHighlight.unexpectedBracket.foreground", "#FF1212CC", "#FF1212CC");
        Def("editorError.foreground", "#F14C4C", "#E51400", "#F48771", "#B5200D");
        Def("editorWarning.foreground", "#CCA700", "#BF8803", "#FFD37A", "#895503");
        Def("editorInfo.foreground", "#3794FF", "#1A85FF", "#3794FF", "#1A85FF");
        Def("editorLightBulb.foreground", "#FFCC00", "#DDB100", "#FFFF00", "#007ACC");
        Def("editorHint.foreground", "#EEEEEEB3", "#6C6C6C", "#EEEEEEB3", "#6C6C6C");
        Def("editorError.background", ""); Def("editorWarning.background", ""); Def("editorInfo.background", "");
        Def("editorError.border", ""); Def("editorWarning.border", ""); Def("editorInfo.border", ""); Def("editorHint.border", "");
        Def("editorUnnecessaryCode.border", "");
        Def("editorUnnecessaryCode.opacity", "#00000080", "#00000080");
        Alias("editorGutter.background", "editor.background");
        Def("editorGutter.modifiedBackground", "#1B81A8", "#2090D3");
        Def("editorGutter.addedBackground", "#487E02", "#48985D");
        Def("editorGutter.deletedBackground", "#F14C4C", "#E51400");
        Def("editorGutter.foldingControlForeground", "#C5C5C5", "#424242");
        Def("editorOverviewRuler.border", "#7F7F7F4D", "#7F7F7F4D");
        Alias("editorOverviewRuler.errorForeground", "editorError.foreground");
        Alias("editorOverviewRuler.warningForeground", "editorWarning.foreground");
        Alias("editorOverviewRuler.infoForeground", "editorInfo.foreground");
        Def("editorOverviewRuler.findMatchForeground", "#D186167E", "#D186167E");
        Def("editorOverviewRuler.wordHighlightForeground", "#A0A0A0CC", "#A0A0A0CC");
        Def("editorLink.activeForeground", "#4E94CE", "#0000FF", "#21A6FF", "#0F4A85");
        Def("editorCodeLens.foreground", "#999999", "#919191");
        Def("editorInlayHint.foreground", "#969696", "#969696", "#FFFFFF", "#292929");
        Def("editorInlayHint.background", "#4D4D4D4D", "#4D4D4D1A");
        Alias("editorInlayHint.typeForeground", "editorInlayHint.foreground");
        Alias("editorInlayHint.parameterForeground", "editorInlayHint.foreground");
        Def("editorGhostText.foreground", "#FFFFFF56", "#00000056");
        Def("editorSuggestWidget.selectedBackground", "#04395E", "#0060C0", "#04395E", "#0060C0");
        Def("editorSuggestWidget.highlightForeground", "#18A3FF", "#0066BF", "#18A3FF", "#0066BF");
        Def("editorSuggestWidget.focusHighlightForeground", "#18A3FF", "#BBE7FF", "#18A3FF", "#BBE7FF");
        Def("editorSuggestWidget.selectedForeground", "#FFFFFF", "#FFFFFF");

        // ---- editor widgets (hover, suggest, find, peek) ----
        Def("editorWidget.background", "#252526", "#F3F3F3", "#0C141F", "#F2F2F2");
        Alias("editorWidget.foreground", "foreground");
        Def("editorWidget.border", "#454545", "#C8C8C8", "#6FC3DF", "#0F4A85");
        Alias("editorSuggestWidget.background", "editorWidget.background");
        Alias("editorSuggestWidget.foreground", "editorWidget.foreground");
        Alias("editorSuggestWidget.border", "editorWidget.border");
        Alias("editorHoverWidget.background", "editorWidget.background");
        Alias("editorHoverWidget.foreground", "editorWidget.foreground");
        Def("editorHoverWidget.border", "#454545", "#C8C8C8", "#6FC3DF", "#0F4A85");
        Def("editorHoverWidget.statusBarBackground", "#2C2C2D", "#ECECEC");
        Alias("editorStickyScroll.background", "editor.background");
        Def("editorStickyScrollHover.background", "#2A2D2E", "#F0F0F0");
        Def("peekView.border", "#3794FF", "#1A85FF", "#6FC3DF", "#0F4A85");
        Def("peekViewEditor.background", "#001F33", "#F2F8FC", "#001F33", "#F2F8FC");
        Def("peekViewResult.background", "#252526", "#F3F3F3", "#000000", "#FFFFFF");
        Def("peekViewTitle.background", "#252526", "#F3F3F3");
        Def("peekViewResult.selectionBackground", "#3399FF33", "#3399FF33");
        Def("peekViewEditor.matchHighlightBackground", "#FF8F0099", "#F5D8934D");
        Def("peekViewTitleLabel.foreground", "#FFFFFF", "#000000", "#FFFFFF", "#292929");
        Def("peekViewTitleDescription.foreground", "#CCCCCCB3", "#616161", "#FFFFFF99", "#292929B3");
        Alias("peekViewResult.fileForeground", "foreground");
        Def("editor.linkedEditingBackground", "#FF00004D", "#FF00004D", "#FF00004D", "#FFFFFF");
        Def("diffEditor.insertedTextBackground", "#9CCC2C33", "#9CCC2C40");
        Def("diffEditor.removedTextBackground", "#FF000033", "#FF000033");
        Def("diffEditor.insertedLineBackground", "#9BB95533", "#9BB95533");
        Def("diffEditor.removedLineBackground", "#FF000033", "#FF000033");
        Alias("minimap.background", "editor.background");
        Def("minimap.selectionHighlight", "#264F78", "#ADD6FF");
        Def("minimap.findMatchHighlight", "#D18616", "#D18616");
        Alpha("minimapSlider.background", "scrollbarSlider.background", 0.5);
        Alpha("minimapSlider.hoverBackground", "scrollbarSlider.hoverBackground", 0.5);
        Alpha("minimapSlider.activeBackground", "scrollbarSlider.activeBackground", 0.5);
        Alpha("breadcrumb.foreground", "foreground", 0.8);
        Alias("breadcrumb.focusForeground", "foreground");
        Alias("breadcrumb.activeSelectionForeground", "foreground");
        Alias("breadcrumb.background", "editor.background");

        // ---- debugging ----
        Def("debugIcon.breakpointForeground", "#E51400");
        Def("debugIcon.breakpointDisabledForeground", "#848484");
        Def("debugIcon.breakpointUnverifiedForeground", "#848484");
        Def("debugIcon.breakpointCurrentStackframeForeground", "#FFCC00", "#BE8700");
        Def("debugIcon.breakpointStackframeForeground", "#89D185", "#57A64A");
        Def("debugToolBar.background", "#333333", "#F3F3F3");

        // ---- workbench: lists, trees, inputs, buttons ----
        Def("list.activeSelectionBackground", "#04395E", "#0060C0", "#000000", "#0F4A85");
        Def("list.activeSelectionForeground", "#FFFFFF", "#FFFFFF");
        Def("list.inactiveSelectionBackground", "#37373D", "#E4E6F1");
        Def("list.inactiveSelectionForeground", "", "");
        Def("list.hoverBackground", "#2A2D2E", "#E8E8E8");
        Def("list.focusBackground", "#062F4A", "#D6EBFF");
        Def("list.highlightForeground", "#18A3FF", "#0066BF", "#F38518", "#0F4A85");
        Alias("list.focusOutline", "focusBorder");
        Def("list.errorForeground", "#F88070", "#B01011");
        Def("list.warningForeground", "#CCA700", "#855F00");
        Def("list.dropBackground", "#383B3D", "#D6EBFF");
        Def("tree.indentGuidesStroke", "#585858", "#A9A9A9");
        Def("input.background", "#3C3C3C", "#FFFFFF", "#000000", "#FFFFFF");
        Alias("input.foreground", "foreground");
        Def("input.border", "", "", "#6FC3DF", "#0F4A85");
        Def("input.placeholderForeground", "#A6A6A6", "#767676");
        Def("inputOption.activeBorder", "#007ACC", "#007ACC", "#6FC3DF", "#0F4A85");
        Def("inputOption.activeBackground", "#007FD466", "#0090F133");
        Def("inputValidation.errorBackground", "#5A1D1D", "#F2DEDE", "#000000", "#FFFFFF");
        Def("inputValidation.errorBorder", "#BE1100", "#BE1100", "#6FC3DF", "#0F4A85");
        Def("inputValidation.warningBackground", "#352A05", "#F6F5D2", "#000000", "#FFFFFF");
        Def("inputValidation.warningBorder", "#B89500", "#B89500", "#6FC3DF", "#0F4A85");
        Def("inputValidation.infoBackground", "#063B49", "#D6ECF2", "#000000", "#FFFFFF");
        Def("inputValidation.infoBorder", "#007ACC", "#007ACC", "#6FC3DF", "#0F4A85");
        Def("dropdown.background", "#3C3C3C", "#FFFFFF", "#000000", "#FFFFFF");
        Def("dropdown.foreground", "#F0F0F0", "#616161", "#FFFFFF", "#292929");
        Def("dropdown.border", "#3C3C3C", "#CECECE", "#6FC3DF", "#0F4A85");
        Def("button.background", "#0E639C", "#007ACC", "#000000", "#FFFFFF");
        Def("button.foreground", "#FFFFFF", "#FFFFFF");
        Def("button.hoverBackground", "#1177BB", "#0062A3");
        Def("button.secondaryBackground", "#3A3D41", "#5F6A79");
        Def("button.secondaryForeground", "#FFFFFF", "#FFFFFF");
        Def("button.secondaryHoverBackground", "#45494E", "#4C5561");
        Def("checkbox.background", "#3C3C3C", "#FFFFFF");
        Alias("checkbox.foreground", "foreground");
        Alias("checkbox.border", "contrastBorder");
        Def("toolbar.hoverBackground", "#5A5D5E50", "#B8B8B850");
        Def("toolbar.activeBackground", "#63666750", "#A6A6A650");
        Def("menu.background", "#3C3C3C", "#FFFFFF", "#000000", "#FFFFFF");
        Alias("menu.foreground", "dropdown.foreground");
        Def("menu.selectionBackground", "#04395E", "#0060C0");
        Def("menu.selectionForeground", "#FFFFFF", "#FFFFFF");
        Def("menu.separatorBackground", "#606060", "#D4D4D4");
        Def("menu.border", "", "");
        Def("menubar.selectionBackground", "#FFFFFF1A", "#0000001A");
        Def("quickInput.background", "#252526", "#F3F3F3");
        Alias("quickInput.foreground", "foreground");
        Alias("quickInputList.focusBackground", "list.focusBackground");
        Def("pickerGroup.foreground", "#3794FF", "#0066BF", "#FFFFFF", "#292929");
        Def("pickerGroup.border", "#3F3F46", "#CCCEDB", "#FFFFFF", "#292929");
        Def("notifications.background", "#252526", "#F3F3F3");
        Alias("notifications.foreground", "foreground");
        Def("notifications.border", "#303031", "#E7E7E7");
        Def("notificationCenterHeader.background", "#303031", "#E7E7E7");
        Def("notificationLink.foreground", "#3794FF", "#006AB1");
        Def("settings.headerForeground", "#E7E7E7", "#444444");
        Def("settings.modifiedItemIndicator", "#0C7D9D", "#66AFE0");

        // ---- workbench: chrome ----
        Def("sideBar.background", "#252526", "#F3F3F3", "#000000", "#FFFFFF");
        Alias("sideBar.foreground", "foreground");
        Def("sideBar.border", "", "");
        Def("sideBarTitle.foreground", "#BBBBBB", "#6F6F6F");
        Def("sideBarSectionHeader.background", "#00000000", "#00000000");
        Alias("sideBarSectionHeader.foreground", "sideBarTitle.foreground");
        Def("activityBar.background", "#333333", "#2C2C2C", "#000000", "#FFFFFF");
        Def("activityBar.foreground", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#292929");
        Alpha("activityBar.inactiveForeground", "activityBar.foreground", 0.4);
        Def("activityBarBadge.background", "#007ACC", "#007ACC", "#000000", "#0F4A85");
        Def("activityBarBadge.foreground", "#FFFFFF", "#FFFFFF");
        Def("statusBar.background", "#007ACC", "#007ACC", "", "");
        Def("statusBar.foreground", "#FFFFFF", "#FFFFFF", "#FFFFFF", "#292929");
        Def("statusBar.noFolderBackground", "#68217A", "#68217A", "", "");
        Alias("statusBar.noFolderForeground", "statusBar.foreground");
        Def("statusBar.debuggingBackground", "#CC6633", "#CC6633", "", "");
        Alias("statusBar.debuggingForeground", "statusBar.foreground");
        Def("statusBar.border", "", "", "#6FC3DF", "#0F4A85");
        Def("statusBarItem.hoverBackground", "#FFFFFF1F", "#FFFFFF1F");
        Def("statusBarItem.activeBackground", "#FFFFFF2E", "#FFFFFF2E");
        Def("statusBarItem.remoteBackground", "#16825D", "#16825D");
        Def("statusBarItem.remoteForeground", "#FFFFFF", "#FFFFFF");
        Def("titleBar.activeBackground", "#3C3C3C", "#DDDDDD", "#000000", "#FFFFFF");
        Def("titleBar.activeForeground", "#CCCCCC", "#333333", "#FFFFFF", "#292929");
        Alpha("titleBar.inactiveBackground", "titleBar.activeBackground", 0.6);
        Alpha("titleBar.inactiveForeground", "titleBar.activeForeground", 0.6);
        Def("titleBar.border", "", "");
        Chain("tab.activeBackground", new Ref("editor.background", 1));
        Def("tab.activeForeground", "#FFFFFF", "#333333", "#FFFFFF", "#292929");
        Chain("tab.inactiveBackground", new Themed("editorGroupHeader.tabsBackground"), new Lit("#2D2D2D", "#ECECEC", "#000000", "#FFFFFF"));
        Alpha("tab.inactiveForeground", "tab.activeForeground", 0.5);
        Def("tab.border", "#252526", "#F3F3F3", "#6FC3DF", "#0F4A85");
        Def("tab.activeBorder", "", "");
        Def("tab.activeBorderTop", "", "");
        Chain("tab.unfocusedActiveBackground", new Ref("tab.activeBackground", 1));
        Alpha("tab.unfocusedActiveForeground", "tab.activeForeground", 0.5);
        Def("tab.hoverBackground", "", "");
        Def("editorGroupHeader.tabsBackground", "#252526", "#F3F3F3", "#000000", "#FFFFFF");
        Def("editorGroupHeader.noTabsBackground", "#1E1E1E", "#FFFFFF");
        Def("editorGroup.border", "#444444", "#E7E7E7", "#6FC3DF", "#0F4A85");
        Def("editorGroup.dropBackground", "#53595D80", "#2677CB2E");
        Alias("panel.background", "editor.background");
        Def("panel.border", "#80808059", "#80808059", "#6FC3DF", "#0F4A85");
        Def("panelTitle.activeForeground", "#E7E7E7", "#424242", "#FFFFFF", "#292929");
        Alpha("panelTitle.inactiveForeground", "panelTitle.activeForeground", 0.6);
        Def("panelTitle.activeBorder", "#E7E7E7", "#424242", "#6FC3DF", "#0F4A85");
        Def("terminal.background", "", "");
        Alias("terminal.foreground", "foreground");
        Alias("terminal.selectionBackground", "editor.selectionBackground");
        Alias("terminalCursor.foreground", "editorCursor.foreground");
        string[] ansiDark = ["#000000", "#CD3131", "#0DBC79", "#E5E510", "#2472C8", "#BC3FBC", "#11A8CD", "#E5E5E5", "#666666", "#F14C4C", "#23D18B", "#F5F543", "#3B8EEA", "#D670D6", "#29B8DB", "#E5E5E5"];
        string[] ansiLight = ["#000000", "#CD3131", "#00BC00", "#949800", "#0451A5", "#BC05BC", "#0598BC", "#555555", "#666666", "#CD3131", "#14CE14", "#B5BA00", "#0451A5", "#BC05BC", "#0598BC", "#A5A5A5"];
        string[] ansiNames = ["Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White", "BrightBlack", "BrightRed", "BrightGreen", "BrightYellow", "BrightBlue", "BrightMagenta", "BrightCyan", "BrightWhite"];
        for (var i = 0; i < 16; i++) Def("terminal.ansi" + ansiNames[i], ansiDark[i], ansiLight[i]);
        Def("gitDecoration.modifiedResourceForeground", "#E2C08D", "#895503");
        Def("gitDecoration.untrackedResourceForeground", "#73C991", "#007100");
        Def("gitDecoration.deletedResourceForeground", "#C74E39", "#AD0707");
        Def("gitDecoration.ignoredResourceForeground", "#8C8C8C", "#8E8E90");
        Def("gitDecoration.conflictingResourceForeground", "#E4676B", "#6C6CC4");
        return d;
    }
}
