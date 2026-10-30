using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia;
using CsCode.Panels.Debug.CallStack;
using CsCode.Panels.Debug.Infrastructure;
using CsCode.Ui;
using CsCode.Ui.Menus;

namespace CsCode.Panels.Debug.Views;

/// <summary>
/// Call Stack window. A click picks the frame (keyboard focus stays here), double-click / Enter picks it and moves focus to the editor;
/// ↑↓ move the highlight only. Context menu: Copy Call Stack, Show External Code.
/// </summary>
public sealed partial class CallStackView : UserControl, IDisposable, IClipboardTarget, IToolWindowFocus
{
    /// <summary>View &gt; Tool Windows &gt; Call Stack: the keyboard on a frame (the selected one, else one on screen), where ↑↓ and Enter work.</summary>
    public bool FocusPreferred() => ToolWindowFocus.FocusRow(this.FindControl<ListBox>("Rows")!);

    /// <summary>One frame as Visual Studio copies it: the frame, then its location in brackets.</summary>
    internal static string FrameText(CallStackRow row) => row.Location is { Length: > 0 } loc ? $"{row.Text}  [{loc}]" : row.Text;

    public bool CanCopy => Model is { Rows.Count: > 0 };

    /// <summary>Copy: the selected frame, or the whole stack of the thread when no frame is selected (a thread row).</summary>
    public async Task CopyAsync()
    {
        if (Model is not { } model) return;
        if (model.SelectedRow is { Kind: CallStackRowKind.Frame } frame && model.Window is { } window) await window.CopyToClipboardAsync(FrameText(frame));
        else await model.CopyCallStackAsync();
    }

    public CallStackView()
    {
        InitializeComponent();
        var rows = this.FindControl<ListBox>("Rows")!;
        rows.AddHandler(PointerReleasedEvent, OnRowsPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        rows.AddHandler(KeyDownEvent, OnRowsKeyDown, RoutingStrategies.Tunnel); // before the list's own arrow/Enter/Space handling
        rows.DoubleTapped += OnRowsDoubleTapped;
        rows.ContextRequested += OnContextRequested;
        ToolWindowFocus.KeepOnRows(rows); // other frames or threads (a stop, a thread expanded) replace the rows in one Reset
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
                case Key.Right when row is { Kind: CallStackRowKind.Thread, IsExpanded: false }:
                case Key.Left when row is { Kind: CallStackRowKind.Thread, IsExpanded: true }:
                    e.Handled = true;
                    await model.ActivateAsync(row, takeFocus: false); // toggles the thread
                    break;
                case Key.Left when row.Kind is CallStackRowKind.Frame or CallStackRowKind.ExternalCode or CallStackRowKind.More:
                    if (model.Rows.LastOrDefault(r => r.Kind == CallStackRowKind.Thread && r.ThreadId == row.ThreadId) is { } owner)
                    {
                        e.Handled = true;
                        model.SelectedRow = owner;
                        this.FindControl<ListBox>("Rows")!.ScrollIntoView(owner);
                    }
                    break;
                case Key.C or Key.Insert when ClipboardKeys.Classify(e) == ClipboardAction.Copy:
                    e.Handled = true;
                    await CopyAsync();
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
        if (model.SelectedRow is { Kind: CallStackRowKind.Frame }) menu.Items.Add(new MenuItem { Header = "Copy", InputGesture = KeyGesture.Parse(CommandKeys.Gesture("C")), Command = new UiCommand(CopyAsync) });
        menu.Items.Add(new MenuItem { Header = "Copy Call Stack", Command = new UiCommand(model.CopyCallStackAsync) });
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
