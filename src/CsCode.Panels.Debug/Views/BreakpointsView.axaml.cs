using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CsCode.Panels.Debug.Breakpoints;
using CsCode.Panels.Debug.Infrastructure;

namespace CsCode.Panels.Debug.Views;

/// <summary>
/// Breakpoints window: checkbox enables, double-click / Enter navigates, Space toggles, Delete removes, context menu edits the condition,
/// hit count and log message.
/// </summary>
public sealed partial class BreakpointsView : UserControl, IDisposable
{
    public BreakpointsView()
    {
        InitializeComponent();
        var rows = this.FindControl<ListBox>("Rows")!;
        rows.DoubleTapped += OnDoubleTapped;
        rows.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        rows.ContextRequested += OnContextRequested;
        _focusKeeper = new ListFocusKeeper(rows);
    }

    private readonly ListFocusKeeper _focusKeeper;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _focusKeeper.Attach(DataContext);
    }

    private BreakpointsViewModel? Model => DataContext as BreakpointsViewModel;

    private static BreakpointRow? RowOf(object? source) =>
        source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is BreakpointRow row ? row : null;

    private async void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<CheckBox>(includeSelf: true) is not null) return; // the checkbox double-click is two toggles
        if (Model is not { } model || RowOf(e.Source) is not { IsBreakpoint: true } row) return;
        await model.NavigateAsync(row);
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { SelectedRow: { } row } model) return;
        switch (e.Key)
        {
            case Key.Enter when row.IsBreakpoint:
                e.Handled = true;
                await model.NavigateAsync(row);
                break;
            case Key.Space:
                e.Handled = true;
                model.ToggleEnabled(row);
                break;
            case Key.Delete:
                e.Handled = true;
                model.Delete(row);
                break;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Model is not { } model) return;
        var menu = new ContextMenu();
        if (RowOf(e.Source) is { IsHeader: false } row)
        {
            model.SelectedRow = row;
            if (row.Kind == BreakpointRowKind.Source)
            {
                menu.Items.Add(new MenuItem { Header = "Go to Source", InputGesture = KeyGesture.Parse("Enter"), Command = new UiCommand(() => model.NavigateAsync(row)) });
                menu.Items.Add(new Separator());
            }
            if (row.Kind is BreakpointRowKind.Source or BreakpointRowKind.Exception)
                menu.Items.Add(new MenuItem { Header = "Condition…", Command = new UiCommand(() => model.EditConditionAsync(row)) });
            if (row.Kind == BreakpointRowKind.Source)
            {
                // Offered only when the active adapter supports them (netcoredbg: neither); an existing one can still be edited away.
                menu.Items.Add(Gated("Hit Count…", row.HitCondition is null ? model.HitCountUnsupported : null, () => model.EditHitCountAsync(row)));
                menu.Items.Add(Gated("Log Message…", row.LogMessage is null ? model.LogMessageUnsupported : null, () => model.EditLogMessageAsync(row)));
            }
            if (row.CanToggle)
                menu.Items.Add(new MenuItem { Header = row.IsEnabled ? "Disable" : "Enable", InputGesture = KeyGesture.Parse("Space"), Command = new UiCommand(() => model.ToggleEnabled(row)) });
            if (row.IsBreakpoint)
                menu.Items.Add(new MenuItem { Header = "Delete", InputGesture = KeyGesture.Parse("Delete"), Command = new UiCommand(() => model.Delete(row)) });
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(new MenuItem { Header = "Enable All Breakpoints", Command = model.EnableAllCommand });
        menu.Items.Add(new MenuItem { Header = "Disable All Breakpoints", Command = model.DisableAllCommand });
        menu.Items.Add(new MenuItem { Header = "Delete All Breakpoints", Command = model.DeleteAllCommand });
        menu.Open(this.FindControl<ListBox>("Rows")!);
        e.Handled = true;
    }

    /// <summary>A menu entry that is disabled, with the reason in its header and tooltip, when the debug adapter cannot do it.</summary>
    internal static MenuItem Gated(string header, string? unsupported, Func<Task> run)
    {
        var item = new MenuItem { Header = unsupported is null ? header : header + " (not supported)", Command = new UiCommand(run), IsEnabled = unsupported is null };
        if (unsupported is not null)
        {
            ToolTip.SetTip(item, unsupported);
            ToolTip.SetShowOnDisabled(item, true);
        }
        return item;
    }

    public void Dispose()
    {
        _focusKeeper.Dispose();
        (DataContext as IDisposable)?.Dispose();
    }
}
