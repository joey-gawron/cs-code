using Avalonia;
using Avalonia.Controls;

namespace CsCode.App.Views;

/// <summary>
/// The sizes of the overlays that take the keyboard (quick input, modal prompt, Ctrl+Tab switcher), relative to the window they are shown in: the
/// main window, or a floating window's dock (an adorner, clipped to the dock). A fixed size was cut off in a narrow or short floating window (a tool
/// window floats at its docked size). As VS Code sizes its quick input to the window it is in: 62 % of the width (the golden cut), its list at most
/// 40 % of the height; its dialogs at most 90 % of the width. The caps keep the sizes the main window always had.
/// </summary>
internal static class OverlaySizing
{
    public const double QuickInputMaxWidth = 620;
    public const double QuickInputMaxListHeight = 360;
    public const double PromptMaxWidth = 460;
    public const double SwitcherMinWidth = 300;
    /// <summary>The space a centred overlay keeps from the window's edges.</summary>
    public const double Margin = 12;

    public static double QuickInputWidth(double windowWidth) => Math.Floor(Math.Min(QuickInputMaxWidth, windowWidth * 0.62));

    public static double QuickInputListHeight(double windowHeight) => Math.Floor(Math.Min(QuickInputMaxListHeight, windowHeight * 0.4));

    public static double PromptWidth(double windowWidth) => Math.Floor(Math.Min(PromptMaxWidth, windowWidth * 0.9));

    /// <summary>What is left of <paramref name="length"/> inside the margins (never negative).</summary>
    public static double Inside(double length) => Math.Max(0, length - 2 * Margin);

    /// <summary>Sets <paramref name="property"/> only when it changes (these run in a measure pass).</summary>
    public static void Set(Control control, StyledProperty<double> property, double value)
    {
        if (!double.IsFinite(value) || control.GetValue(property) == value) return;
        control.SetValue(property, value);
    }
}

/// <summary>A grid that reports the size it is given before it measures its children, so an overlay in it can fit the window it is in.</summary>
internal sealed class KeyboardOverlayLayer : Grid
{
    public Action<Size>? Measuring { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        Measuring?.Invoke(availableSize);
        return base.MeasureOverride(availableSize);
    }
}
