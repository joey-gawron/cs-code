using CsCode.Core.Theming;

namespace CsCode.Ui.Theming;

/// <summary>
/// Built-in Visual Studio 2022 themes (Dark, Blue, Light) expressed as plain <see cref="ColorTheme"/> data in VS Code workbench
/// colour ids, so they flow through the same pipeline as a user's theme JSON. Each theme is derived from a compact
/// <see cref="Palette"/> so the ~250 ids stay consistent with each other.
/// </summary>
public static class VsThemes
{
    public const string DarkId = "vs2022-dark";
    public const string BlueId = "vs2022-blue";
    public const string LightId = "vs2022-light";
    public const string DefaultId = DarkId;

    public static IReadOnlyList<ColorTheme> All => [Dark, Blue, Light];

    public static ColorTheme Dark { get; } = Build(DarkId, "Visual Studio Dark", ThemeKind.Dark, new Palette
    {
        Bg = "#1E1E1E", Fg = "#F1F1F1", Subtle = "#A0A0A0", Disabled = "#656565",
        Tool = "#252526", Chrome = "#2D2D30", Input = "#333337", Border = "#3F3F46", Accent = "#007ACC", AccentFg = "#FFFFFF",
        Hover = "#3E3E40", Selection = "#007ACC", SelectionFg = "#FFFFFF", SelectionInactive = "#3F3F46",
        TitleBg = "#2D2D30", TitleFg = "#F1F1F1", ToolbarBg = "#2D2D30", MenuBg = "#333337", MenuHover = "#007ACC",
        Status = "#007ACC", StatusDebug = "#C95000", StatusNoFolder = "#68217A",
        TabActiveBg = "#1E1E1E", TabActiveFg = "#FFFFFF", TabInactiveBg = "#2D2D30", TabInactiveFg = "#CCCCCC", TabHover = "#3E3E40", TabTop = "#007ACC",
        EditorSel = "#264F78", EditorSelInactive = "#3A3D41", LineHighlight = "#282828", LineNo = "#858585", LineNoActive = "#C6C6C6",
        Widget = "#252526", ScrollThumb = "#686868", ScrollThumbHover = "#9E9E9E", ScrollThumbActive = "#BEBEBE",
        Link = "#3794FF", Error = "#F48771", Warning = "#CCA700", Info = "#75BEFF", Hint = "#9E9E9E",
        Success = "#89D185", Modified = "#E2C08D", Added = "#81B88B", Deleted = "#C74E39",
        Welcome = "#1E1E1E", Tile = "#2D2D30", TileHover = "#3E3E40", Light = false,
        Keyword = "#569CD6", ControlKeyword = "#D8A0DF", Type = "#4EC9B0", Str = "#D69D85", Comment = "#57A64A", Number = "#B5CEA8",
        Function = "#DCDCAA", Variable = "#9CDCFE", Constant = "#4FC1FF", Operator = "#B4B4B4", Punct = "#DCDCDC", Tag = "#569CD6",
        Attribute = "#9CDCFE", Regex = "#D16969", Preproc = "#9B9B9B", Invalid = "#F44747",
    });

    public static ColorTheme Blue { get; } = Build(BlueId, "Visual Studio Blue", ThemeKind.Light, new Palette
    {
        Bg = "#FFFFFF", Fg = "#1E1E1E", Subtle = "#6D6D6D", Disabled = "#A0A0A0",
        Tool = "#EEF0F8", Chrome = "#CFD6E5", Input = "#FFFFFF", Border = "#8E9BBC", Accent = "#007ACC", AccentFg = "#FFFFFF",
        Hover = "#FFF29D", Selection = "#007ACC", SelectionFg = "#FFFFFF", SelectionInactive = "#CCCEDB",
        TitleBg = "#293955", TitleFg = "#FFFFFF", ToolbarBg = "#CFD6E5", MenuBg = "#FFFFFF", MenuHover = "#FFF29D",
        Status = "#007ACC", StatusDebug = "#C95000", StatusNoFolder = "#68217A",
        TabActiveBg = "#FFF29D", TabActiveFg = "#1E1E1E", TabInactiveBg = "#293955", TabInactiveFg = "#FFFFFF", TabHover = "#5D6B99", TabTop = "#FFF29D",
        EditorSel = "#ADD6FF", EditorSelInactive = "#E5EBF1", LineHighlight = "#EEF3FA", LineNo = "#2B91AF", LineNoActive = "#0B216F",
        Widget = "#F3F3F3", ScrollThumb = "#8E9BBC", ScrollThumbHover = "#6C7BA6", ScrollThumbActive = "#4E5C8E",
        Link = "#006AB1", Error = "#E51400", Warning = "#BF8803", Info = "#1A85FF", Hint = "#6C6C6C",
        Success = "#388A34", Modified = "#895503", Added = "#587C0C", Deleted = "#AD0707",
        Welcome = "#FFFFFF", Tile = "#EEF0F8", TileHover = "#FFF29D", Light = true,
        Keyword = "#0000FF", ControlKeyword = "#8F08C4", Type = "#2B91AF", Str = "#A31515", Comment = "#008000", Number = "#098658",
        Function = "#74531F", Variable = "#001080", Constant = "#0070C1", Operator = "#000000", Punct = "#000000", Tag = "#800000",
        Attribute = "#FF0000", Regex = "#811F3F", Preproc = "#808080", Invalid = "#CD3131",
    });

    public static ColorTheme Light { get; } = Build(LightId, "Visual Studio Light", ThemeKind.Light, new Palette
    {
        Bg = "#FFFFFF", Fg = "#1E1E1E", Subtle = "#6A6A6A", Disabled = "#A0A0A0",
        Tool = "#F5F5F5", Chrome = "#EEEEF2", Input = "#FFFFFF", Border = "#CCCEDB", Accent = "#007ACC", AccentFg = "#FFFFFF",
        Hover = "#E6E6E6", Selection = "#007ACC", SelectionFg = "#FFFFFF", SelectionInactive = "#CCCEDB",
        TitleBg = "#EEEEF2", TitleFg = "#1E1E1E", ToolbarBg = "#EEEEF2", MenuBg = "#F5F5F5", MenuHover = "#C9DEF5",
        Status = "#007ACC", StatusDebug = "#C95000", StatusNoFolder = "#68217A",
        TabActiveBg = "#FFFFFF", TabActiveFg = "#1E1E1E", TabInactiveBg = "#EEEEF2", TabInactiveFg = "#6A6A6A", TabHover = "#C9DEF5", TabTop = "#007ACC",
        EditorSel = "#ADD6FF", EditorSelInactive = "#E5EBF1", LineHighlight = "#F5F5F5", LineNo = "#2B91AF", LineNoActive = "#0B216F",
        Widget = "#F3F3F3", ScrollThumb = "#C2C3C9", ScrollThumbHover = "#9E9FA5", ScrollThumbActive = "#7B7C82",
        Link = "#006AB1", Error = "#E51400", Warning = "#BF8803", Info = "#1A85FF", Hint = "#6C6C6C",
        Success = "#388A34", Modified = "#895503", Added = "#587C0C", Deleted = "#AD0707",
        Welcome = "#FFFFFF", Tile = "#F5F5F5", TileHover = "#E6E6E6", Light = true,
        Keyword = "#0000FF", ControlKeyword = "#8F08C4", Type = "#2B91AF", Str = "#A31515", Comment = "#008000", Number = "#098658",
        Function = "#74531F", Variable = "#001080", Constant = "#0070C1", Operator = "#000000", Punct = "#000000", Tag = "#800000",
        Attribute = "#FF0000", Regex = "#811F3F", Preproc = "#808080", Invalid = "#CD3131",
    });

    /// <summary>
    /// High-contrast base (black): not offered as a theme of its own (VS Code's Dark High Contrast is), but the palette that
    /// <see cref="ThemeCompletion"/> fills a high-contrast theme's missing chrome keys from, as Dark and Light do for the other kinds. Modelled on
    /// Windows' High Contrast Black and VS Code's defaults for <c>hc-black</c>: black surfaces, white text, cyan borders, orange focus.
    /// </summary>
    public static ColorTheme HighContrastDark { get; } = Build("vs2022-hc-dark", "Visual Studio High Contrast (base)", ThemeKind.HighContrastDark, new Palette
    {
        Bg = "#000000", Fg = "#FFFFFF", Subtle = "#D0D0D0", Disabled = "#A0A0A0",
        Tool = "#000000", Chrome = "#000000", Input = "#000000", Border = "#6FC3DF", Accent = "#F38518", AccentFg = "#FFFFFF",
        Hover = "#262626", Selection = "#1AEBFF", SelectionFg = "#000000", SelectionInactive = "#333333",
        TitleBg = "#000000", TitleFg = "#FFFFFF", ToolbarBg = "#000000", MenuBg = "#000000", MenuHover = "#1AEBFF",
        Status = "#000000", StatusDebug = "#BA592C", StatusNoFolder = "#68217A",
        TabActiveBg = "#000000", TabActiveFg = "#FFFFFF", TabInactiveBg = "#000000", TabInactiveFg = "#FFFFFF", TabHover = "#262626", TabTop = "#F38518",
        EditorSel = "#264F78", EditorSelInactive = "#3A3D41", LineHighlight = "#000000", LineNo = "#FFFFFF", LineNoActive = "#FFFFFF",
        Widget = "#0C141F", ScrollThumb = "#6FC3DF", ScrollThumbHover = "#9BD6E9", ScrollThumbActive = "#FFFFFF",
        Link = "#21A6FF", Error = "#F48771", Warning = "#FFD37A", Info = "#3794FF", Hint = "#EEEEEE",
        Success = "#89D185", Modified = "#E2C08D", Added = "#81B88B", Deleted = "#F48771",
        Welcome = "#000000", Tile = "#000000", TileHover = "#262626", Light = false,
        Keyword = "#569CD6", ControlKeyword = "#C586C0", Type = "#4EC9B0", Str = "#CE9178", Comment = "#7CA668", Number = "#B5CEA8",
        Function = "#DCDCAA", Variable = "#9CDCFE", Constant = "#4FC1FF", Operator = "#D4D4D4", Punct = "#FFFFFF", Tag = "#569CD6",
        Attribute = "#9CDCFE", Regex = "#D16969", Preproc = "#9B9B9B", Invalid = "#F44747",
    }, new Dictionary<string, string>
    {
        // Selections are cyan with black text (Windows' highlight pair); separators and outlines are the high-contrast border.
        ["menu.selectionForeground"] = "#000000", ["quickInputList.focusForeground"] = "#000000", ["list.activeSelectionForeground"] = "#000000",
        ["list.inactiveSelectionForeground"] = "#FFFFFF", ["contrastBorder"] = "#6FC3DF", ["contrastActiveBorder"] = "#F38518",
        ["button.background"] = "#000000", ["button.border"] = "#6FC3DF", ["button.hoverBackground"] = "#262626",
        ["badge.background"] = "#000000", ["badge.foreground"] = "#FFFFFF", ["list.highlightForeground"] = "#F38518",
        ["editorSuggestWidget.highlightForeground"] = "#F38518", ["pickerGroup.foreground"] = "#FFFFFF", ["textSeparator.foreground"] = "#FFFFFF",
    });

    /// <summary>High-contrast base (white), the counterpart of <see cref="HighContrastDark"/> for <c>hc-light</c> themes.</summary>
    public static ColorTheme HighContrastLight { get; } = Build("vs2022-hc-light", "Visual Studio High Contrast Light (base)", ThemeKind.HighContrastLight, new Palette
    {
        Bg = "#FFFFFF", Fg = "#000000", Subtle = "#3C3C3C", Disabled = "#6E6E6E",
        Tool = "#FFFFFF", Chrome = "#FFFFFF", Input = "#FFFFFF", Border = "#0F4A85", Accent = "#006BBD", AccentFg = "#FFFFFF",
        Hover = "#E6E6E6", Selection = "#0F4A85", SelectionFg = "#FFFFFF", SelectionInactive = "#E0E0E0",
        TitleBg = "#FFFFFF", TitleFg = "#000000", ToolbarBg = "#FFFFFF", MenuBg = "#FFFFFF", MenuHover = "#0F4A85",
        Status = "#0F4A85", StatusDebug = "#B5200D", StatusNoFolder = "#68217A",
        TabActiveBg = "#FFFFFF", TabActiveFg = "#000000", TabInactiveBg = "#FFFFFF", TabInactiveFg = "#292929", TabHover = "#E6E6E6", TabTop = "#006BBD",
        EditorSel = "#ADD6FF", EditorSelInactive = "#E5EBF1", LineHighlight = "#FFFFFF", LineNo = "#292929", LineNoActive = "#000000",
        Widget = "#FFFFFF", ScrollThumb = "#0F4A85", ScrollThumbHover = "#3C6E9E", ScrollThumbActive = "#000000",
        Link = "#0F4A85", Error = "#B5200D", Warning = "#895503", Info = "#1A85FF", Hint = "#292929",
        Success = "#388A34", Modified = "#895503", Added = "#374E06", Deleted = "#AD0707",
        Welcome = "#FFFFFF", Tile = "#FFFFFF", TileHover = "#E6E6E6", Light = true,
        Keyword = "#0000FF", ControlKeyword = "#8F08C4", Type = "#185E73", Str = "#A31515", Comment = "#515151", Number = "#096D48",
        Function = "#5E2CBC", Variable = "#001080", Constant = "#0070C1", Operator = "#000000", Punct = "#000000", Tag = "#0F4A85",
        Attribute = "#264F78", Regex = "#811F3F", Preproc = "#515151", Invalid = "#B5200D",
    }, new Dictionary<string, string>
    {
        ["menu.selectionForeground"] = "#FFFFFF", ["contrastBorder"] = "#0F4A85", ["contrastActiveBorder"] = "#006BBD",
        ["button.border"] = "#0F4A85", ["badge.background"] = "#0F4A85", ["badge.foreground"] = "#FFFFFF",
        ["list.highlightForeground"] = "#0F4A85", ["editorSuggestWidget.highlightForeground"] = "#0F4A85", ["pickerGroup.foreground"] = "#000000",
        ["textSeparator.foreground"] = "#000000", ["statusBarItem.hoverBackground"] = "#FFFFFF33", ["statusBarItem.activeBackground"] = "#FFFFFF4D",
    });

    /// <summary>The bases <see cref="ThemeCompletion"/> fills from, by kind: Dark, Light, High Contrast (dark) and High Contrast Light.</summary>
    public static ColorTheme BaseFor(ThemeKind kind) => kind switch
    {
        ThemeKind.Light => Light,
        ThemeKind.HighContrastDark => HighContrastDark,
        ThemeKind.HighContrastLight => HighContrastLight,
        _ => Dark,
    };

    /// <summary>Registers the three built-in themes (the shell does this once at startup, before the first frame).</summary>
    public static void RegisterAll(IColorThemeService service, ICollection<IDisposable>? registrations = null)
    {
        foreach (var t in All)
        {
            var reg = service.Register(t);
            registrations?.Add(reg);
        }
    }

    /// <summary>Compact semantic palette; see <see cref="Build"/> for how each entry fans out to workbench ids.</summary>
    public sealed record Palette
    {
        public required string Bg { get; init; }
        public required string Fg { get; init; }
        public required string Subtle { get; init; }
        public required string Disabled { get; init; }
        public required string Tool { get; init; }
        public required string Chrome { get; init; }
        public required string Input { get; init; }
        public required string Border { get; init; }
        public required string Accent { get; init; }
        public required string AccentFg { get; init; }
        public required string Hover { get; init; }
        public required string Selection { get; init; }
        public required string SelectionFg { get; init; }
        public required string SelectionInactive { get; init; }
        public required string TitleBg { get; init; }
        public required string TitleFg { get; init; }
        public required string ToolbarBg { get; init; }
        public required string MenuBg { get; init; }
        public required string MenuHover { get; init; }
        public required string Status { get; init; }
        public required string StatusDebug { get; init; }
        public required string StatusNoFolder { get; init; }
        public required string TabActiveBg { get; init; }
        public required string TabActiveFg { get; init; }
        public required string TabInactiveBg { get; init; }
        public required string TabInactiveFg { get; init; }
        public required string TabHover { get; init; }
        public required string TabTop { get; init; }
        public required string EditorSel { get; init; }
        public required string EditorSelInactive { get; init; }
        public required string LineHighlight { get; init; }
        public required string LineNo { get; init; }
        public required string LineNoActive { get; init; }
        public required string Widget { get; init; }
        public required string ScrollThumb { get; init; }
        public required string ScrollThumbHover { get; init; }
        public required string ScrollThumbActive { get; init; }
        public required string Link { get; init; }
        public required string Error { get; init; }
        public required string Warning { get; init; }
        public required string Info { get; init; }
        public required string Hint { get; init; }
        public required string Success { get; init; }
        public required string Modified { get; init; }
        public required string Added { get; init; }
        public required string Deleted { get; init; }
        public required string Welcome { get; init; }
        public required string Tile { get; init; }
        public required string TileHover { get; init; }
        public required bool Light { get; init; }
        // Token colours
        public required string Keyword { get; init; }
        public required string ControlKeyword { get; init; }
        public required string Type { get; init; }
        public required string Str { get; init; }
        public required string Comment { get; init; }
        public required string Number { get; init; }
        public required string Function { get; init; }
        public required string Variable { get; init; }
        public required string Constant { get; init; }
        public required string Operator { get; init; }
        public required string Punct { get; init; }
        public required string Tag { get; init; }
        public required string Attribute { get; init; }
        public required string Regex { get; init; }
        public required string Preproc { get; init; }
        public required string Invalid { get; init; }
    }

    private static ColorTheme Build(string id, string label, ThemeKind kind, Palette p, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var c = new Dictionary<string, Rgba>(StringComparer.Ordinal);
        void Set(string hex, params string[] keys)
        {
            var rgba = Rgba.Parse(hex);
            foreach (var k in keys) c[k] = rgba;
        }
        string Alpha(string hex, byte a) => Rgba.Parse(hex) with { A = a } is var r ? r.ToHex() : hex;

        // Base
        Set(p.Fg, "foreground", "icon.foreground", "sideBar.foreground", "editor.foreground", "editorWidget.foreground", "list.hoverForeground",
            "dropdown.foreground", "input.foreground", "checkbox.foreground", "quickInput.foreground", "notifications.foreground", "menu.foreground",
            "editorSuggestWidget.foreground", "editorHoverWidget.foreground", "terminal.foreground", "notificationCenterHeader.foreground", "sideBarSectionHeader.foreground",
            "sideBarTitle.foreground", "panelTitle.activeForeground", "breadcrumb.focusForeground", "pickerGroup.foreground", "inputOption.activeForeground");
        Set(p.Subtle, "descriptionForeground", "input.placeholderForeground", "panelTitle.inactiveForeground", "breadcrumb.foreground", "textSeparator.foreground");
        Set(p.Disabled, "disabledForeground", "list.invalidItemForeground");
        Set(p.Error, "errorForeground", "list.errorForeground", "editorError.foreground", "problemsErrorIcon.foreground", "notificationsErrorIcon.foreground");
        Set(p.Warning, "list.warningForeground", "editorWarning.foreground", "problemsWarningIcon.foreground", "notificationsWarningIcon.foreground");
        Set(p.Info, "editorInfo.foreground", "problemsInfoIcon.foreground", "notificationsInfoIcon.foreground");
        Set(p.Hint, "editorHint.foreground");
        Set(p.Accent, "focusBorder", "contrastActiveBorder", "progressBar.background", "sash.hoverBorder", "inputOption.activeBorder", "button.background",
            "tab.activeBorderTop", "panelTitle.activeBorder", "list.focusOutline", "badge.background", "menubar.selectionBorder", "debugIcon.stepIntoForeground");
        Set(p.AccentFg, "button.foreground", "badge.foreground", "statusBar.foreground", "statusBar.debuggingForeground", "statusBar.noFolderForeground");
        Set(Alpha(p.Accent, 0x2E), "selection.background");
        Set(p.Link, "textLink.foreground", "notificationLink.foreground");
        Set(Alpha(p.Link, 0xFF), "textLink.activeForeground");
        Set(p.Border, "contrastBorder", "widget.shadow");
        c["widget.shadow"] = Rgba.Parse(p.Light ? "#00000029" : "#00000066");
        Set(p.Tool, "textCodeBlock.background");

        // Scrollbars
        Set(p.ScrollThumb, "scrollbarSlider.background");
        Set(p.ScrollThumbHover, "scrollbarSlider.hoverBackground");
        Set(p.ScrollThumbActive, "scrollbarSlider.activeBackground");
        c["scrollbar.shadow"] = Rgba.Parse("#00000000");

        // Buttons / inputs
        Set(Alpha(p.Accent, 0xFF), "button.hoverBackground");
        c["button.hoverBackground"] = Lighten(Rgba.Parse(p.Accent), p.Light ? -0.12 : 0.12);
        Set(p.Border, "button.border", "checkbox.border", "dropdown.border", "input.border", "editorWidget.border", "editorSuggestWidget.border", "editorHoverWidget.border", "commandCenter.border", "notificationCenter.border", "notificationToast.border", "notifications.border");
        Set(p.Input, "button.secondaryBackground", "checkbox.background", "dropdown.background", "dropdown.listBackground", "input.background", "commandCenter.background", "inputValidation.infoBackground");
        c["button.secondaryBackground"] = Rgba.Parse(p.Light ? p.Chrome : p.Input);
        Set(p.Fg, "button.secondaryForeground", "commandCenter.foreground");
        Set(p.Hover, "button.secondaryHoverBackground", "toolbar.hoverBackground", "list.hoverBackground", "statusBarItem.hoverBackground");
        c["statusBarItem.hoverBackground"] = Rgba.Parse("#FFFFFF1F");
        Set(p.Border, "toolbar.activeBackground", "statusBarItem.activeBackground");
        c["statusBarItem.activeBackground"] = Rgba.Parse("#FFFFFF2E");
        Set(Alpha(p.Accent, 0x66), "inputOption.activeBackground");
        Set(p.Chrome, "inputValidation.errorBackground", "inputValidation.warningBackground");
        Set(p.Error, "inputValidation.errorBorder", "inputValidation.errorForeground");
        Set(p.Warning, "inputValidation.warningBorder");
        Set(p.Info, "inputValidation.infoBorder");

        // Lists
        Set(p.Selection, "list.activeSelectionBackground");
        Set(p.SelectionFg, "list.activeSelectionForeground");
        Set(p.SelectionInactive, "list.inactiveSelectionBackground");
        Set(p.Fg, "list.inactiveSelectionForeground");
        Set(p.Hover, "list.focusBackground");
        Set(p.Accent, "list.highlightForeground");
        c["list.highlightForeground"] = Rgba.Parse(p.Light ? "#0066BF" : "#2AAAFF");
        Set(Alpha(p.Accent, 0x40), "list.dropBackground");
        Set(Alpha(p.Warning, 0x55), "list.filterMatchBackground");
        Set(p.Border, "tree.indentGuidesStroke");

        // Side bar / panel
        Set(p.Tool, "sideBar.background", "panel.background", "editorGroup.dropBackground");
        c["editorGroup.dropBackground"] = Rgba.Parse(Alpha(p.Accent, 0x40));
        Set(p.Border, "sideBar.border", "panel.border", "editorGroup.border", "sideBarSectionHeader.border", "pickerGroup.border", "titleBar.border", "statusBar.border", "menu.border", "editorOverviewRuler.border");
        Set(p.Chrome, "sideBarSectionHeader.background", "breadcrumb.background", "notificationCenterHeader.background", "quickInputTitle.background");
        c["breadcrumb.background"] = Rgba.Parse(p.Bg);

        // Tabs / groups
        Set(p.Chrome, "editorGroupHeader.tabsBackground", "editorGroupHeader.noTabsBackground", "tab.border");
        Set(p.Border, "editorGroupHeader.tabsBorder", "tab.lastPinnedBorder");
        c["tab.border"] = Rgba.Parse(p.Light ? "#00000000" : "#00000000");
        Set(p.TabActiveBg, "tab.activeBackground", "tab.unfocusedActiveBackground");
        Set(p.TabActiveFg, "tab.activeForeground", "tab.unfocusedActiveForeground", "tab.hoverForeground");
        Set(p.TabInactiveBg, "tab.inactiveBackground", "tab.unfocusedInactiveBackground");
        Set(p.TabInactiveFg, "tab.inactiveForeground", "tab.unfocusedInactiveForeground");
        Set(p.TabHover, "tab.hoverBackground");
        Set(p.TabTop, "tab.activeBorderTop", "tab.activeBorder");
        c["tab.activeBorder"] = Rgba.Parse("#00000000");
        Set(p.TabActiveFg, "tab.dirtyForeground");
        Set(p.TabInactiveFg, "tab.previewForeground");

        // Status bar / title bar / menus
        Set(p.Status, "statusBar.background", "statusBarItem.prominentBackground");
        Set(p.StatusDebug, "statusBar.debuggingBackground");
        Set(p.StatusNoFolder, "statusBar.noFolderBackground", "statusBar.noFolderBorder");
        Set(p.TitleBg, "titleBar.activeBackground", "titleBar.inactiveBackground");
        Set(p.TitleFg, "titleBar.activeForeground");
        Set(p.TitleFg, "titleBar.inactiveForeground");
        c["titleBar.inactiveForeground"] = Rgba.Parse(Alpha(p.TitleFg, 0x99));
        Set(p.ToolbarBg, "toolbar.background");
        Set(p.TitleFg, "toolbar.foreground");
        c["toolbar.foreground"] = Rgba.Parse(p.Light && p.TitleBg != p.ToolbarBg ? p.Fg : p.TitleFg);
        Set(p.Border, "toolbar.separator");
        Set(p.MenuBg, "menu.background");
        Set(p.MenuHover, "menu.selectionBackground");
        c["menu.selectionForeground"] = Rgba.Parse(p.MenuHover == p.Accent ? p.AccentFg : p.Fg);
        Set(p.Border, "menu.separatorBackground");
        Set(p.Hover, "menubar.selectionBackground");
        c["menubar.selectionBackground"] = Rgba.Parse(p.TitleBg == p.Chrome ? p.Hover : Alpha(p.TitleFg, 0x2E));
        Set(p.TitleFg, "menubar.selectionForeground");

        // Quick input / notifications
        Set(p.MenuBg, "quickInput.background", "notifications.background");
        Set(p.Selection, "quickInputList.focusBackground");
        Set(p.SelectionFg, "quickInputList.focusForeground");
        Set(p.Accent, "pickerGroup.foreground");
        c["pickerGroup.foreground"] = Rgba.Parse(p.Light ? "#0066BF" : "#3794FF");

        // Editor
        Set(p.Bg, "editor.background", "editorGutter.background", "welcomePage.background");
        Set(p.EditorSel, "editor.selectionBackground");
        Set(p.EditorSelInactive, "editor.inactiveSelectionBackground");
        Set(p.LineHighlight, "editor.lineHighlightBackground");
        c["editor.lineHighlightBorder"] = Rgba.Parse("#00000000");
        Set(Alpha(p.Warning, 0x66), "editor.findMatchBackground");
        Set(Alpha(p.Accent, 0x38), "editor.findMatchHighlightBackground", "editor.wordHighlightBackground");
        Set(Alpha(p.Warning, 0x40), "editor.stackFrameHighlightBackground");
        Set(Alpha(p.Success, 0x40), "editor.focusedStackFrameHighlightBackground");
        Set(p.Fg, "editorCursor.foreground");
        Set(p.LineNo, "editorLineNumber.foreground");
        Set(p.LineNoActive, "editorLineNumber.activeForeground");
        Set(Alpha(p.Border, 0x80), "editorIndentGuide.background", "editorWhitespace.foreground");
        Set(Alpha(p.Accent, 0x40), "editorBracketMatch.background");
        Set(p.Subtle, "editorBracketMatch.border");
        Set(p.Widget, "editorWidget.background", "editorSuggestWidget.background", "editorHoverWidget.background");
        Set(p.Selection, "editorSuggestWidget.selectedBackground");
        c["editorSuggestWidget.highlightForeground"] = c["list.highlightForeground"];
        // Replace previews (Find in Files): VS Code's defaults, translucent so the text keeps its colour.
        c["diffEditor.insertedTextBackground"] = Rgba.Parse(p.Light ? "#9CCC2C40" : "#9CCC2C33");
        c["diffEditor.removedTextBackground"] = Rgba.Parse("#FF000033");

        // Debug / problems / git
        Set(p.Chrome, "debugToolBar.background");
        Set(p.Border, "debugToolBar.border");
        Set(p.Error, "debugIcon.breakpointForeground");
        c["debugIcon.breakpointForeground"] = Rgba.Parse(p.Light ? "#E51400" : "#E51400");
        Set(p.Success, "debugIcon.startForeground", "debugIcon.continueForeground");
        Set(p.Error, "debugIcon.stopForeground");
        c["debugIcon.stopForeground"] = Rgba.Parse(p.Light ? "#A1260D" : "#F48771");
        Set(p.Warning, "debugIcon.pauseForeground");
        c["debugIcon.pauseForeground"] = Rgba.Parse(p.Light ? "#007ACC" : "#75BEFF");
        Set(p.Info, "debugIcon.stepOverForeground", "debugIcon.stepOutForeground", "debugIcon.restartForeground");
        c["debugIcon.stepOverForeground"] = c["debugIcon.pauseForeground"];
        c["debugIcon.stepIntoForeground"] = c["debugIcon.pauseForeground"];
        c["debugIcon.stepOutForeground"] = c["debugIcon.pauseForeground"];
        c["debugIcon.restartForeground"] = Rgba.Parse(p.Success);
        Set(p.Modified, "gitDecoration.modifiedResourceForeground");
        Set(p.Added, "gitDecoration.addedResourceForeground", "gitDecoration.untrackedResourceForeground");
        Set(p.Deleted, "gitDecoration.deletedResourceForeground", "gitDecoration.conflictingResourceForeground");
        Set(p.Subtle, "gitDecoration.ignoredResourceForeground");

        // Terminal + welcome
        Set(p.Bg, "terminal.background");
        var ansi = p.Light
            ? new[] { "#000000", "#CD3131", "#00BC00", "#949800", "#0451A5", "#BC05BC", "#0598BC", "#555555", "#666666", "#CD3131", "#14CE14", "#B5BA00", "#0451A5", "#BC05BC", "#0598BC", "#A5A5A5" }
            : new[] { "#000000", "#CD3131", "#0DBC79", "#E5E510", "#2472C8", "#BC3FBC", "#11A8CD", "#E5E5E5", "#666666", "#F14C4C", "#23D18B", "#F5F543", "#3B8EEA", "#D670D6", "#29B8DB", "#E5E5E5" };
        var ansiNames = new[] { "Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White", "BrightBlack", "BrightRed", "BrightGreen", "BrightYellow", "BrightBlue", "BrightMagenta", "BrightCyan", "BrightWhite" };
        for (var i = 0; i < ansi.Length; i++) c["terminal.ansi" + ansiNames[i]] = Rgba.Parse(ansi[i]);
        Set(p.Tile, "welcomePage.tileBackground");
        Set(p.TileHover, "welcomePage.tileHoverBackground");
        Set(p.Border, "welcomePage.tileBorder");

        TokenColorRule Rule(string fg, string? style, params string[] scopes) => new(scopes, fg, null, style);
        IReadOnlyList<TokenColorRule> tokens =
        [
            Rule(p.Comment, null, "comment", "punctuation.definition.comment"),
            Rule(p.Str, null, "string", "string.quoted", "meta.embedded.assembly"),
            Rule(p.Keyword, null, "keyword", "storage", "storage.type", "storage.modifier", "constant.language"),
            Rule(p.ControlKeyword, null, "keyword.control", "keyword.control.flow"),
            Rule(p.Type, null, "entity.name.type", "entity.name.class", "entity.name.namespace", "support.type", "support.class", "entity.other.inherited-class"),
            Rule(p.Number, null, "constant.numeric", "constant.numeric.decimal"),
            Rule(p.Function, null, "entity.name.function", "support.function", "meta.function-call", "entity.name.method"),
            Rule(p.Variable, null, "variable", "variable.parameter", "variable.other.readwrite", "meta.object-literal.key"),
            Rule(p.Constant, null, "variable.other.constant", "variable.other.enummember", "constant.other"),
            Rule(p.Operator, null, "keyword.operator"),
            Rule(p.Punct, null, "punctuation", "meta.brace"),
            Rule(p.Tag, null, "entity.name.tag", "meta.tag"),
            Rule(p.Attribute, null, "entity.other.attribute-name"),
            Rule(p.Regex, null, "string.regexp", "constant.character.escape"),
            Rule(p.Preproc, null, "meta.preprocessor", "entity.name.function.preprocessor"),
            Rule(p.Invalid, null, "invalid", "invalid.illegal"),
            Rule(p.Link, "underline", "markup.underline.link"),
            Rule(p.Keyword, "bold", "markup.heading", "markup.bold"),
            Rule(p.Fg, "italic", "markup.italic"),
        ];

        if (overrides is not null)
            foreach (var (key, hex) in overrides) c[key] = Rgba.Parse(hex);
        return new ColorTheme(id, label, kind, c, tokens);
    }

    /// <summary>Lightens (amount &gt; 0) or darkens (amount &lt; 0) a colour by mixing towards white/black.</summary>
    private static Rgba Lighten(Rgba color, double amount)
    {
        static byte Mix(byte v, double target, double t) => (byte)Math.Clamp(Math.Round(v + (target - v) * t), 0, 255);
        var t = Math.Abs(amount);
        var target = amount >= 0 ? 255.0 : 0.0;
        return new Rgba(Mix(color.R, target, t), Mix(color.G, target, t), Mix(color.B, target, t), color.A);
    }
}
