using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CsCode.Panels.Debug.Views;

/// <summary>
/// Attached behaviour for inline editors that live inside a list row template. When the text box becomes visible (the row switched into edit
/// mode) it takes keyboard focus and selects its text; when it is hidden again (Enter committed, Escape cancelled) the keyboard focus goes
/// back to the row it sat in, so arrow-key navigation continues where the user was — a template cannot express either on its own.
/// </summary>
public static class EditorFocus
{
    public static readonly AttachedProperty<bool> FocusWhenVisibleProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("FocusWhenVisible", typeof(EditorFocus));

    /// <summary>Whether the editor held the keyboard focus until it was hidden (as opposed to the user clicking elsewhere).</summary>
    private static readonly AttachedProperty<bool> HeldFocusProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("HeldFocus", typeof(EditorFocus));

    static EditorFocus() => FocusWhenVisibleProperty.Changed.AddClassHandler<TextBox>(OnChanged);

    public static bool GetFocusWhenVisible(TextBox box) => box.GetValue(FocusWhenVisibleProperty);
    public static void SetFocusWhenVisible(TextBox box, bool value) => box.SetValue(FocusWhenVisibleProperty, value);

    private static void OnChanged(TextBox box, AvaloniaPropertyChangedEventArgs e)
    {
        box.PropertyChanged -= OnBoxChanged;
        box.AttachedToVisualTree -= OnAttached;
        box.GotFocus -= OnGotFocus;
        box.LostFocus -= OnLostFocus;
        if (e.NewValue is not true) return;
        box.PropertyChanged += OnBoxChanged;
        box.AttachedToVisualTree += OnAttached;
        box.GotFocus += OnGotFocus;
        box.LostFocus += OnLostFocus;
    }

    private static void OnGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is TextBox box) box.SetValue(HeldFocusProperty, true);
    }

    private static void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        // Losing focus because the box is being hidden keeps the flag; the user moving the focus elsewhere clears it.
        if (sender is TextBox { IsVisible: true } box) box.SetValue(HeldFocusProperty, false);
    }

    private static void OnBoxChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty || sender is not TextBox box) return;
        if (e.NewValue is true)
        {
            Focus(box);
            return;
        }
        if (!box.GetValue(HeldFocusProperty)) return;
        box.SetValue(HeldFocusProperty, false);
        ReturnFocusToRow(box);
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox { IsEffectivelyVisible: true } box) Focus(box);
    }

    private static void Focus(TextBox box) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!box.IsEffectivelyVisible) return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);

    private static void ReturnFocusToRow(TextBox box)
    {
        var row = box.FindAncestorOfType<ListBoxItem>();
        if (row is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (row.IsEffectivelyVisible && row.IsAttachedToVisualTree()) row.Focus();
        }, DispatcherPriority.Input);
    }
}
