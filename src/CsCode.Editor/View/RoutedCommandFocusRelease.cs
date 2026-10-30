using System.Reflection;
using Avalonia;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Editing;

namespace CsCode.Editor.View;

/// <summary>
/// AvaloniaEdit's <see cref="RoutedCommand"/> keeps the last element that took focus in a static field (the target of its commands), so after the
/// last document closes nothing moves focus and that one view, its text area and the document text stay reachable until focus next moves
/// anywhere. A view being disposed clears the field when it points into its own text area.
/// <para>Done by reflection against Avalonia.AvaloniaEdit 12.0.0's private <c>_inputElement</c>; when the layout differs nothing is cleared (the
/// retention stays as it was, bounded to one view) and the test of this class says so. UI thread only, like the field's writer.</para>
/// </summary>
internal static class RoutedCommandFocusRelease
{
    private static readonly FieldInfo? InputElementField = typeof(RoutedCommand).GetField("_inputElement", BindingFlags.Static | BindingFlags.NonPublic);

    /// <summary>True when AvaloniaEdit's private layout is the one this was written against (tests).</summary>
    internal static bool Supported => InputElementField is not null;

    /// <summary>The element <see cref="RoutedCommand"/> holds (tests).</summary>
    internal static object? Held => InputElementField?.GetValue(null);

    /// <summary>Clears the static when it holds <paramref name="area"/> or an element inside it. False when it held something else.</summary>
    public static bool Release(TextArea area)
    {
        if (InputElementField is null) return false;
        try
        {
            if (InputElementField.GetValue(null) is not Visual held || (held != area && !area.IsVisualAncestorOf(held))) return false;
            InputElementField.SetValue(null, null);
            return true;
        }
        catch (Exception ex) when (ex is TargetException or ArgumentException or FieldAccessException)
        {
            return false;
        }
    }
}
