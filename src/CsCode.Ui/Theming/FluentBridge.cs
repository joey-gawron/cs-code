using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace CsCode.Ui.Theming;

/// <summary>
/// Retargets Avalonia's Fluent theme (used only as the structural base of stock controls) at the workbench palette:
/// the per-variant <see cref="ColorPaletteResources"/> ramp is derived from the workbench colours, and a table of control-specific
/// resource keys (menus, scrollbars, tooltips, buttons, tabs) is pointed at the live <c>vsc:</c> brushes so those controls follow
/// a theme switch without re-templating. Anything not listed keeps the palette-derived colours.
/// </summary>
internal static class FluentBridge
{
    // Fluent resource key -> workbench colour id (brushes shared with the vsc: resources, so they update in place).
    private static readonly (string Key, string ColorId)[] BrushMap =
    [
        // Menus / context menus / flyouts: VS flat menus, accent hover.
        ("MenuFlyoutPresenterBackground", "menu.background"), ("MenuFlyoutPresenterBorderBrush", "menu.border"),
        ("MenuFlyoutItemBackground", "menu.background"), ("MenuFlyoutItemBackgroundPointerOver", "menu.selectionBackground"), ("MenuFlyoutItemBackgroundPressed", "menu.selectionBackground"),
        ("MenuFlyoutItemForeground", "menu.foreground"), ("MenuFlyoutItemForegroundPointerOver", "menu.selectionForeground"), ("MenuFlyoutItemForegroundPressed", "menu.selectionForeground"),
        ("MenuFlyoutItemKeyboardAcceleratorTextForeground", "descriptionForeground"), ("MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "menu.selectionForeground"),
        ("MenuFlyoutSubItemChevron", "menu.foreground"), ("MenuFlyoutSubItemChevronPointerOver", "menu.selectionForeground"), ("MenuFlyoutSubItemChevronPressed", "menu.selectionForeground"),
        ("MenuFlyoutSubItemChevronSubMenuOpened", "menu.selectionForeground"),
        ("MenuFlyoutItemBackgroundDisabled", "menu.background"), ("MenuFlyoutItemForegroundDisabled", "disabledForeground"),
        // Scroll bars: thin pill thumbs.
        ("ScrollBarThumbFill", "scrollbarSlider.background"), ("ScrollBarThumbFillPointerOver", "scrollbarSlider.hoverBackground"), ("ScrollBarThumbFillPressed", "scrollbarSlider.activeBackground"),
        ("ScrollBarThumbFillDisabled", "scrollbarSlider.background"), ("ScrollBarPanningThumbBackground", "scrollbarSlider.background"),
        // Tooltips.
        ("ToolTipBackground", "editorHoverWidget.background"), ("ToolTipForeground", "editorHoverWidget.foreground"), ("ToolTipBorderBrush", "editorHoverWidget.border"),
        // Buttons: flat, accent on the default/accent button.
        ("ButtonBackground", "button.secondaryBackground"), ("ButtonBackgroundPointerOver", "button.secondaryHoverBackground"), ("ButtonBackgroundPressed", "toolbar.activeBackground"),
        ("ButtonForeground", "button.secondaryForeground"), ("ButtonForegroundPointerOver", "button.secondaryForeground"), ("ButtonForegroundPressed", "button.secondaryForeground"),
        ("ButtonBorderBrush", "button.border"), ("ButtonBorderBrushPointerOver", "button.border"), ("ButtonBorderBrushPressed", "button.border"),
        ("AccentButtonBackground", "button.background"), ("AccentButtonBackgroundPointerOver", "button.hoverBackground"), ("AccentButtonBackgroundPressed", "button.hoverBackground"),
        ("AccentButtonForeground", "button.foreground"), ("AccentButtonForegroundPointerOver", "button.foreground"), ("AccentButtonForegroundPressed", "button.foreground"),
        ("ToggleButtonBackground", "button.secondaryBackground"), ("ToggleButtonBackgroundPointerOver", "button.secondaryHoverBackground"),
        ("ToggleButtonBackgroundChecked", "inputOption.activeBackground"), ("ToggleButtonBackgroundCheckedPointerOver", "inputOption.activeBackground"),
        ("ToggleButtonForeground", "button.secondaryForeground"), ("ToggleButtonForegroundChecked", "foreground"),
        ("ToggleButtonBorderBrush", "button.border"), ("ToggleButtonBorderBrushChecked", "inputOption.activeBorder"),
        // Text inputs: VS input box (1px border, accent on focus).
        ("TextControlBackground", "input.background"), ("TextControlBackgroundPointerOver", "input.background"), ("TextControlBackgroundFocused", "input.background"),
        ("TextControlForeground", "input.foreground"), ("TextControlForegroundPointerOver", "input.foreground"), ("TextControlForegroundFocused", "input.foreground"),
        ("TextControlBorderBrush", "input.border"), ("TextControlBorderBrushPointerOver", "input.border"), ("TextControlBorderBrushFocused", "focusBorder"),
        ("TextControlPlaceholderForeground", "input.placeholderForeground"), ("TextControlPlaceholderForegroundPointerOver", "input.placeholderForeground"), ("TextControlPlaceholderForegroundFocused", "input.placeholderForeground"),
        ("TextControlSelectionHighlightColor", "selection.background"),
        // Combo boxes.
        ("ComboBoxBackground", "dropdown.background"), ("ComboBoxBackgroundPointerOver", "dropdown.background"), ("ComboBoxBackgroundPressed", "dropdown.background"),
        ("ComboBoxForeground", "dropdown.foreground"), ("ComboBoxForegroundPointerOver", "dropdown.foreground"),
        ("ComboBoxBorderBrush", "dropdown.border"), ("ComboBoxBorderBrushPointerOver", "focusBorder"), ("ComboBoxBorderBrushFocused", "focusBorder"),
        ("ComboBoxDropDownBackground", "dropdown.listBackground"), ("ComboBoxDropDownBorderBrush", "dropdown.border"),
        ("ComboBoxItemBackgroundPointerOver", "list.hoverBackground"), ("ComboBoxItemBackgroundSelected", "list.activeSelectionBackground"),
        ("ComboBoxItemBackgroundSelectedPointerOver", "list.activeSelectionBackground"), ("ComboBoxItemForegroundSelected", "list.activeSelectionForeground"),
        // List and tree rows.
        ("ListBoxItemBackgroundPointerOver", "list.hoverBackground"), ("ListBoxItemBackgroundSelected", "list.activeSelectionBackground"),
        ("ListBoxItemBackgroundSelectedPointerOver", "list.activeSelectionBackground"), ("ListBoxItemBackgroundSelectedPressed", "list.activeSelectionBackground"),
        ("ListBoxItemForegroundSelected", "list.activeSelectionForeground"), ("ListBoxItemForegroundSelectedPointerOver", "list.activeSelectionForeground"),
        ("TreeViewItemBackgroundPointerOver", "list.hoverBackground"), ("TreeViewItemBackgroundSelected", "list.activeSelectionBackground"),
        ("TreeViewItemBackgroundSelectedPointerOver", "list.activeSelectionBackground"), ("TreeViewItemForegroundSelected", "list.activeSelectionForeground"),
        ("TreeViewItemForegroundSelectedPointerOver", "list.activeSelectionForeground"), ("TreeViewItemBorderBrushSelected", "list.activeSelectionBackground"),
        // Tabs.
        ("TabItemHeaderBackgroundSelected", "tab.activeBackground"), ("TabItemHeaderBackgroundUnselected", "tab.inactiveBackground"),
        ("TabItemHeaderBackgroundUnselectedPointerOver", "tab.hoverBackground"), ("TabItemHeaderForegroundSelected", "tab.activeForeground"),
        ("TabItemHeaderForegroundUnselected", "tab.inactiveForeground"), ("TabItemHeaderSelectedPipeFill", "tab.activeBorderTop"),
        // Expander / progress / selection / separators.
        ("ExpanderHeaderBackground", "sideBarSectionHeader.background"), ("ExpanderHeaderForeground", "sideBarSectionHeader.foreground"), ("ExpanderContentBackground", "sideBar.background"),
        ("ProgressBarForeground", "progressBar.background"),
        ("FlyoutPresenterBackground", "editorWidget.background"), ("FlyoutBorderThemeBrush", "editorWidget.border"),
    ];

    internal static void Apply(Application app, IReadOnlyDictionary<string, SolidColorBrush> brushes, Func<string, Color> color, ThemeVariant variant)
    {
        var resources = app.Resources;
        foreach (var (key, id) in BrushMap)
            if (brushes.TryGetValue(id, out var b) && (!resources.TryGetResource(key, null, out var cur) || !ReferenceEquals(cur, b)))
                resources[key] = id == "selection.background" ? b.Color : b;

        // Shape tokens: VS is flat and dense.
        resources["ControlCornerRadius"] = new CornerRadius(2);
        resources["OverlayCornerRadius"] = new CornerRadius(2);
        resources["ControlContentThemeFontSize"] = 12.0;
        resources["ScrollBarSize"] = 12.0;

        var fluent = app.Styles.OfType<FluentTheme>().FirstOrDefault();
        if (fluent is null) return;
        var bg = color("editor.background");
        var fg = color("foreground");
        Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
        var palette = new ColorPaletteResources
        {
            Accent = color("focusBorder"),
            RegionColor = bg,
            AltHigh = bg, AltMediumHigh = A(bg, 0xCC), AltMedium = A(bg, 0x99), AltMediumLow = A(bg, 0x66), AltLow = A(bg, 0x33),
            BaseHigh = fg, BaseMediumHigh = Mix(fg, bg, 0.15), BaseMedium = color("descriptionForeground"), BaseMediumLow = Mix(fg, bg, 0.55), BaseLow = color("input.border"),
            ChromeLow = color("panel.background"), ChromeMediumLow = color("titleBar.activeBackground"), ChromeMedium = color("menu.background"), ChromeHigh = color("panel.border"),
            ChromeAltLow = Mix(fg, bg, 0.2), ChromeGray = color("descriptionForeground"),
            ChromeDisabledLow = color("disabledForeground"), ChromeDisabledHigh = color("input.border"),
            ChromeBlackLow = A(Colors.Black, 0x33), ChromeBlackMediumLow = A(Colors.Black, 0x66), ChromeBlackMedium = A(Colors.Black, 0x99), ChromeBlackHigh = Colors.Black,
            ChromeWhite = Colors.White,
            ListLow = color("list.hoverBackground"), ListMedium = color("list.inactiveSelectionBackground"),
            ErrorText = color("errorForeground"),
        };
        fluent.Palettes[variant] = palette;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        255,
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
}
