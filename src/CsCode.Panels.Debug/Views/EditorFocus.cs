using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace CsCode.Panels.Debug.Views;

/// <summary>
/// Attached behaviour for inline editors that live inside a list row template: when the text box becomes visible (the row switched into
/// edit mode) it takes keyboard focus and selects its text, which a template cannot express on its own.
/// </summary>
public static class EditorFocus
{
    public static readonly AttachedProperty<bool> FocusWhenVisibleProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("FocusWhenVisible", typeof(EditorFocus));

    static EditorFocus() => FocusWhenVisibleProperty.Changed.AddClassHandler<TextBox>(OnChanged);

    public static bool GetFocusWhenVisible(TextBox box) => box.GetValue(FocusWhenVisibleProperty);
    public static void SetFocusWhenVisible(TextBox box, bool value) => box.SetValue(FocusWhenVisibleProperty, value);

    private static void OnChanged(TextBox box, AvaloniaPropertyChangedEventArgs e)
    {
        box.PropertyChanged -= OnBoxChanged;
        box.AttachedToVisualTree -= OnAttached;
        if (e.NewValue is not true) return;
        box.PropertyChanged += OnBoxChanged;
        box.AttachedToVisualTree += OnAttached;
    }

    private static void OnBoxChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && e.NewValue is true && sender is TextBox box) Focus(box);
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box) Focus(box);
    }

    private static void Focus(TextBox box) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!box.IsVisible || !box.IsEffectivelyVisible) return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
}
