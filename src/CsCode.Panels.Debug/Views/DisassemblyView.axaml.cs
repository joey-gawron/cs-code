using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CsCode.Panels.Debug.Infrastructure;
using CsCode.Panels.Debug.Native;
using CsCode.Ui;

namespace CsCode.Panels.Debug.Views;

/// <summary>
/// Disassembly window. A click in the left margin (or F9 on a row) toggles an instruction breakpoint; double-click / Enter on a source line opens
/// it in the editor. Scrolling near the top or bottom loads one more page (<see cref="DisassemblyViewModel.LoadEarlierAsync"/> /
/// <see cref="DisassemblyViewModel.LoadLaterAsync"/>), keeping what is on screen in place. While the window has the keyboard the step commands
/// step by instruction (<see cref="DisassemblyViewModel.FocusKey"/>).
/// </summary>
public sealed partial class DisassemblyView : UserControl, IDisposable, IToolWindowFocus
{
    private const double RowHeight = 18;
    private const double EdgeRows = 6;
    private ScrollViewer? _scroll;
    private DisassemblyViewModel? _model;

    public DisassemblyView()
    {
        InitializeComponent();
        var rows = Rows!;
        rows.AddHandler(PointerPressedEvent, OnRowsPointerPressed, RoutingStrategies.Tunnel);
        rows.AddHandler(KeyDownEvent, OnRowsKeyDown, RoutingStrategies.Tunnel);
        rows.DoubleTapped += OnRowsDoubleTapped;
        rows.ContextRequested += OnContextRequested;
        rows.TemplateApplied += (_, _) => HookScroll();
        this.FindControl<TextBox>("Address")!.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _model is { } m) { e.Handled = true; _ = m.GoCommand.ExecuteAsync(null); }
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsKeyboardFocusWithinProperty) _model?.SetKeyboardFocus(IsKeyboardFocusWithin);
        };
        DataContextChanged += (_, _) => Attach(DataContext as DisassemblyViewModel);
    }


    public bool FocusPreferred() => ToolWindowFocus.FocusRow(Rows);

    private void Attach(DisassemblyViewModel? model)
    {
        if (_model is { } old)
        {
            old.RowsPrepended -= OnRowsPrepended;
            old.RevealRequested -= OnRevealRequested;
        }
        _model = model;
        if (model is null) return;
        model.RowsPrepended += OnRowsPrepended;
        model.RevealRequested += OnRevealRequested;
    }

    private void HookScroll()
    {
        if (_scroll is not null) return;
        _scroll = Rows.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll is not { } s || _model is not { } m || m.Rows.Count == 0) return;
        if (s.Offset.Y < EdgeRows * RowHeight) _ = m.LoadEarlierAsync();
        if (s.Offset.Y + s.Viewport.Height > s.Extent.Height - EdgeRows * RowHeight) _ = m.LoadLaterAsync();
    }

    /// <summary>Rows went in (or, negative, out) above the first: the same instructions stay on screen.</summary>
    private void OnRowsPrepended(int count)
    {
        if (_scroll is not { } s) return;
        s.Offset = new Vector(s.Offset.X, Math.Max(0, s.Offset.Y + count * RowHeight));
    }

    private void OnRevealRequested(DisassemblyRow row)
    {
        Rows.ScrollIntoView(row);
        if (_scroll is { } s && _model is { } m && m.Rows.IndexOf(row) is var index and >= 0)
            s.Offset = new Vector(s.Offset.X, Math.Max(0, index * RowHeight - s.Viewport.Height / 3)); // a third down, as VS Code places it
    }

    private static DisassemblyRow? RowOf(object? source) =>
        source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is DisassemblyRow row ? row : null;

    private static bool InMargin(object? source) =>
        source is Visual v && v.GetSelfAndVisualAncestors().OfType<Border>().Any(b => b.Name == "Margin");

    private void OnRowsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_model is not { } m || !e.GetCurrentPoint(Rows).Properties.IsLeftButtonPressed || !InMargin(e.Source) || RowOf(e.Source) is not { } row) return;
        e.Handled = true;
        m.ToggleBreakpoint(row);
    }

    private void OnRowsDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_model is { } m && RowOf(e.Source) is { IsSource: true } row) _ = m.GoToSourceAsync(row);
    }

    private void OnRowsKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model is not { } m || m.SelectedRow is not { } row) return;
        switch (e.Key)
        {
            case Key.F9 when e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;
                m.ToggleBreakpoint(row);
                break;
            case Key.Enter when row.IsSource:
                e.Handled = true;
                _ = m.GoToSourceAsync(row);
                break;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_model is not { } m || RowOf(e.Source) is not { } row) return;
        m.SelectedRow = row;
        var menu = new ContextMenu();
        if (row.IsInstruction)
        {
            menu.Items.Add(new MenuItem { Header = row.HasBreakpoint ? "Remove Breakpoint" : "Insert Breakpoint", InputGesture = new KeyGesture(Key.F9), Command = new UiCommand(() => m.ToggleBreakpoint(row)) });
            menu.Items.Add(new MenuItem { Header = "View Memory at This Address", Command = new UiCommand(() => m.ShowMemory(row)) });
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Copy Address", Command = new UiCommand(() => m.CopyAsync(row.AddressText)) });
            menu.Items.Add(new MenuItem { Header = "Copy Instruction", Command = new UiCommand(() => m.CopyAsync($"{row.AddressText}  {row.Bytes}  {row.Text}")) });
        }
        else if (row.IsSource)
        {
            menu.Items.Add(new MenuItem { Header = "Go to Source", InputGesture = new KeyGesture(Key.Enter), Command = new UiCommand(() => m.GoToSourceAsync(row)) });
        }
        if (menu.Items.Count == 0) return;
        menu.Open(Rows);
        e.Handled = true;
    }

    public void Dispose()
    {
        if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
        Attach(null);
        (DataContext as IDisposable)?.Dispose();
    }
}
