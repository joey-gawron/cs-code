using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace CsCode.App.Input;

/// <summary>
/// Marks a view as belonging to a shell region so the window can classify keyboard focus (editor / explorer / panel / terminal ...) by walking up the
/// visual tree from the focused control. Set on the root of each tool-window or editor view.
/// </summary>
public static class ShellFocus
{
    public static readonly AttachedProperty<FocusArea> AreaProperty = AvaloniaProperty.RegisterAttached<Control, Control, FocusArea>("Area");

    public static void SetArea(Control control, FocusArea area) => control.SetValue(AreaProperty, area);
    public static FocusArea GetArea(Control control) => control.GetValue(AreaProperty);

    /// <summary>The focus area for a focused element: the nearest marked ancestor, else Input for text boxes, else None.</summary>
    public static FocusArea Classify(Visual? focused)
    {
        for (var v = focused; v is not null; v = v.GetVisualParent())
            if (v is Control c && c.GetValue(AreaProperty) is var area && area != FocusArea.None) return area;
        return focused is TextBox ? FocusArea.Input : FocusArea.None;
    }
}
