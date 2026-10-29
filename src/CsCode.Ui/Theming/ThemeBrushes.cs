using Avalonia;
using Avalonia.Media;

namespace CsCode.Ui.Theming;

/// <summary>
/// Access to the live <c>vsc:</c> brushes from code-built views. <see cref="ThemeApplier"/> creates exactly one brush per colour id and
/// only mutates its Color afterwards, so holding the instance is safe and follows theme switches with no re-binding.
/// XAML should still prefer <c>{DynamicResource vsc:...}</c>.
/// </summary>
public static class ThemeBrushes
{
    private static readonly IBrush Fallback = Brushes.Transparent;

    /// <summary>The brush for a workbench colour id (e.g. <c>"statusBar.background"</c>); transparent before the first theme application.</summary>
    public static IBrush Get(string colorId) =>
        Application.Current?.Resources.TryGetResource(WorkbenchColors.BrushKey(colorId), null, out var value) == true && value is IBrush b ? b : Fallback;

    /// <summary>The code font (<c>ui:CodeFontFamily</c>, from the editor font setting) for code-built views such as Output.</summary>
    public static FontFamily CodeFont =>
        Application.Current?.Resources.TryGetResource("ui:CodeFontFamily", null, out var value) == true && value is FontFamily f ? f : FontFamily.Default;
}
