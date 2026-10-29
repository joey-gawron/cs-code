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

    /// <summary>Publishes the theme-independent resource entries once (brush keys shared with the live vsc: brushes, shape tokens).</summary>
    internal static void PublishStructure(Application app, IReadOnlyDictionary<string, SolidColorBrush> brushes)
    {
        var resources = app.Resources;
        foreach (var (key, id) in BrushMap)
            if (brushes.TryGetValue(id, out var b))
                resources[key] = b;
        // Selection highlight is a Color resource in Fluent; it follows the default (dark) selection colour and is refreshed by UpdatePalette.
        // Shape tokens: VS is flat and dense.
        resources["ControlCornerRadius"] = new CornerRadius(2);
        resources["OverlayCornerRadius"] = new CornerRadius(2);
        resources["ControlContentThemeFontSize"] = 12.0;
        resources["ScrollBarSize"] = 12.0;
    }

    /// <summary>Retargets the Fluent palette of the active variant at the workbench colours. Mutates in place and only what changed.</summary>
    internal static void UpdatePalette(Application app, Func<string, Color> color, ThemeVariant variant)
    {
        var fluent = app.Styles.OfType<FluentTheme>().FirstOrDefault();
        if (fluent is null) return;
        var bg = color("editor.background");
        var fg = color("foreground");
        Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
        // One palette object per variant, mutated in place: assigning an unchanged value is a no-op, so a theme switch touches only what differs
        // (replacing the whole palette would invalidate every resource derived from it).
        var created = !fluent.Palettes.TryGetValue(variant, out var palette);
        palette ??= new ColorPaletteResources();
        palette.Accent = color("focusBorder");
        palette.RegionColor = bg;
        palette.AltHigh = bg; palette.AltMediumHigh = A(bg, 0xCC); palette.AltMedium = A(bg, 0x99); palette.AltMediumLow = A(bg, 0x66); palette.AltLow = A(bg, 0x33);
        palette.BaseHigh = fg; palette.BaseMediumHigh = Mix(fg, bg, 0.15); palette.BaseMedium = color("descriptionForeground"); palette.BaseMediumLow = Mix(fg, bg, 0.55); palette.BaseLow = color("input.border");
        palette.ChromeLow = color("panel.background"); palette.ChromeMediumLow = color("titleBar.activeBackground"); palette.ChromeMedium = color("menu.background"); palette.ChromeHigh = color("panel.border");
        palette.ChromeAltLow = Mix(fg, bg, 0.2); palette.ChromeGray = color("descriptionForeground");
        palette.ChromeDisabledLow = color("disabledForeground"); palette.ChromeDisabledHigh = color("input.border");
        palette.ChromeBlackLow = A(Colors.Black, 0x33); palette.ChromeBlackMediumLow = A(Colors.Black, 0x66); palette.ChromeBlackMedium = A(Colors.Black, 0x99); palette.ChromeBlackHigh = Colors.Black;
        palette.ChromeWhite = Colors.White;
        palette.ListLow = color("list.hoverBackground"); palette.ListMedium = color("list.inactiveSelectionBackground");
        palette.ErrorText = color("errorForeground");
        if (created) fluent.Palettes[variant] = palette;

        var selection = color("selection.background");
        if (!app.Resources.TryGetResource("TextControlSelectionHighlightColor", null, out var current) || current is not Color c || c != selection)
            app.Resources["TextControlSelectionHighlightColor"] = selection;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        255,
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
}
