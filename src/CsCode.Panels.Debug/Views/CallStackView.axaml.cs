using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia;
using CsCode.Panels.Debug.CallStack;
using CsCode.Panels.Debug.Infrastructure;

namespace CsCode.Panels.Debug.Views;

/// <summary>
/// Call Stack window. A click picks the frame (keyboard focus stays here), double-click / Enter picks it and moves focus to the editor;
/// ↑↓ move the highlight only. Context menu: Copy Call Stack, Show External Code.
/// </summary>
public sealed partial class CallStackView : UserControl, IDisposable
{
    public CallStackView()
    {
        InitializeComponent();
        var rows = this.FindControl<ListBox>("Rows")!;
        rows.AddHandler(PointerReleasedEvent, OnRowsPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        rows.AddHandler(KeyDownEvent, OnRowsKeyDown);
        rows.DoubleTapped += OnRowsDoubleTapped;
        rows.ContextRequested += OnContextRequested;
    }

    private CallStackViewModel? Model => DataContext as CallStackViewModel;

    private static CallStackRow? RowOf(object? source) =>
        source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is CallStackRow row ? row : null;

    private async void OnRowsPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || Model is not { } model || RowOf(e.Source) is not { } row) return;
        try { await model.ActivateAsync(row, takeFocus: false); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { }
    }

    private async void OnRowsDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Model is not { } model || RowOf(e.Source) is not { } row || row.Kind != CallStackRowKind.Frame) return;
        try { await model.ActivateAsync(row, takeFocus: true); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { }
    }

    private async void OnRowsKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model || model.SelectedRow is not { } row) return;
        try
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    await model.ActivateAsync(row, takeFocus: true);
                    break;
                case Key.C when (e.KeyModifiers & KeyModifiers.Control) != 0:
                    e.Handled = true;
                    await model.CopyCallStackAsync();
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Model is not { } model) return;
        var menu = new ContextMenu();
        if (RowOf(e.Source) is { Kind: CallStackRowKind.Frame } row)
        {
            model.SelectedRow = row;
            menu.Items.Add(new MenuItem { Header = "Switch To Frame", InputGesture = KeyGesture.Parse("Enter"), Command = new UiCommand(() => model.ActivateAsync(row, takeFocus: true)) });
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(new MenuItem { Header = "Copy Call Stack", InputGesture = KeyGesture.Parse("Ctrl+C"), Command = new UiCommand(model.CopyCallStackAsync) });
        menu.Items.Add(new MenuItem
        {
            Header = "Show External Code",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = model.ShowExternalCode,
            Command = new UiCommand(() => { model.ShowExternalCode = !model.ShowExternalCode; }),
        });
        menu.Open(this.FindControl<ListBox>("Rows")!);
        e.Handled = true;
    }

    public void Dispose()
    {
        (DataContext as IDisposable)?.Dispose();
    }
}
