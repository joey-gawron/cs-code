using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CsCode.Panels.Debug.Infrastructure;
using CsCode.Panels.Debug.Threads;

namespace CsCode.Panels.Debug.Views;

/// <summary>Threads window: double-click or Enter makes a thread current (its top frame is focused and its source opened).</summary>
public sealed partial class ThreadsView : UserControl, IDisposable
{
    public ThreadsView()
    {
        InitializeComponent();
        var rows = this.FindControl<ListBox>("Rows")!;
        rows.DoubleTapped += OnDoubleTapped;
        rows.AddHandler(KeyDownEvent, OnKeyDown);
        rows.ContextRequested += OnContextRequested;
    }

    private ThreadsViewModel? Model => DataContext as ThreadsViewModel;

    private static ThreadRow? RowOf(object? source) =>
        source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is ThreadRow row ? row : null;

    private async void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Model is not { } model || RowOf(e.Source) is not { } row) return;
        try { await model.SwitchToAsync(row); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Model is not { SelectedRow: { } row } model) return;
        e.Handled = true;
        try { await model.SwitchToAsync(row); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Model is not { } model || RowOf(e.Source) is not { } row) return;
        model.SelectedRow = row;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Switch To Thread", InputGesture = KeyGesture.Parse("Enter"), Command = new UiCommand(() => model.SwitchToAsync(row)) });
        menu.Open(this.FindControl<ListBox>("Rows")!);
        e.Handled = true;
    }

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
