using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CsCode.Core.Platform;
using CsCode.Testing.Engine;
using CsCode.Ui;
using CsCode.Ui.Controls;
using CsCode.Ui.Icons;
using CsCode.Ui.Menus;
using CsCode.Ui.Theming;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.Panels.Testing;

/// <summary>
/// The Test Explorer: a toolbar (Run All, Run, Debug, Run Failed, Cancel, Refresh, Collapse All, Clear Results), a filter box, the summary line,
/// the virtualised list of the tree's visible rows (state icon, kind icon, name, duration) and, below a splitter, the selected test's details
/// (message, stack trace, output). Keys in the list: Enter opens the test's source, Left/Right collapse/expand, Ctrl+R / Ctrl+D... are the
/// keymap's (testing.*); Ctrl+C copies the test's id. Double-click opens the source; the context menu has Run, Debug, Go to Test, Go to
/// Failure, Copy. Disposed (with its view-model) when the tool window hides.
/// </summary>
public sealed class TestExplorerView : UserControl, IDisposable, IToolWindowFocus
{
    private readonly TestExplorerViewModel _vm;
    private readonly Action<TestExplorerViewModel> _release;
    private readonly ListBox _list;
    private readonly TextBlock _empty;
    private bool _disposed;

    public TestExplorerView(TestExplorerViewModel vm, Action<TestExplorerViewModel> release)
    {
        _vm = vm;
        _release = release;
        DataContext = vm;

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 3, 4, 1) };
        toolbar.Children.Add(Button("run-all", "Run All Tests", () => _ = _vm.RunAllAsync(), nameof(TestExplorerViewModel.CanRun)));
        toolbar.Children.Add(Button("play", "Run (selected, or all)", () => _ = _vm.RunSelectedAsync(), nameof(TestExplorerViewModel.CanRun)));
        toolbar.Children.Add(Button("debug-alt", "Debug (selected, or all)", () => _ = _vm.RunSelectedAsync(debug: true), nameof(TestExplorerViewModel.CanRun)));
        toolbar.Children.Add(Button("debug-rerun", "Rerun Failed Tests", () => _ = _vm.RunFailedAsync(), nameof(TestExplorerViewModel.HasFailed)));
        toolbar.Children.Add(Button("debug-stop", "Cancel Test Run", _vm.Cancel, nameof(TestExplorerViewModel.IsRunning)));
        toolbar.Children.Add(Separator());
        toolbar.Children.Add(Button(IconKey.Refresh, "Refresh Tests", () => _ = _vm.RefreshAsync(), null));
        toolbar.Children.Add(Button("collapse-all", "Collapse All", _vm.CollapseAll, null));
        toolbar.Children.Add(Button("expand-all", "Expand All", _vm.ExpandAll, null));
        toolbar.Children.Add(Button("clear-all", "Clear All Results", _vm.ClearResults, null));

        var filter = new FilterBox { Watermark = "Filter (e.g. Calc, @failed, @passed, @skipped, @notrun)", Margin = new Thickness(6, 2, 6, 2) };
        filter.Bind(FilterBox.TextProperty, new Binding(nameof(TestExplorerViewModel.Filter)) { Mode = BindingMode.TwoWay });

        var summary = new TextBlock { Margin = new Thickness(8, 1, 8, 3), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ThemeBrushes.Get("descriptionForeground") };
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(TestExplorerViewModel.Summary)));
        summary.Bind(ToolTip.TipProperty, new Binding(nameof(TestExplorerViewModel.Summary)));

        _list = new ListBox
        {
            ItemsSource = vm.Rows,
            ItemTemplate = new FuncDataTemplate<TestRow>((row, _) => row is null ? new Panel() : RowFor(row), supportsRecycling: false),
            SelectionMode = SelectionMode.Single,
            Background = Brushes.Transparent,
        };
        AutomationName(_list, "Tests");
        _list.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, new Binding(nameof(TestExplorerViewModel.Selected)) { Mode = BindingMode.TwoWay });
        _list.AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel); // before the ListBox, which takes Left/Right itself
        _list.DoubleTapped += (_, e) => { if (RowAt(e.Source) is { } row) { e.Handled = true; if (row.HasChildren && row.Node.IsGroup) _vm.Toggle(row); else _ = _vm.GoToAsync(row, preview: false); } };
        _list.ContextRequested += OnContextRequested;
        ToolWindowFocus.KeepOnRows(_list); // every rebuild replaces the rows (one Reset)

        _empty = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Foreground = ThemeBrushes.Get("descriptionForeground") };
        _empty.Bind(TextBlock.TextProperty, new Binding(nameof(TestExplorerViewModel.EmptyText)));
        _empty.Bind(IsVisibleProperty, new Binding(nameof(TestExplorerViewModel.IsEmpty)));

        var details = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = ThemeBrushes.CodeFont,
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 4),
        };
        AutomationName(details, "Test details");
        ScrollViewer.SetHorizontalScrollBarVisibility(details, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        details.Bind(TextBox.TextProperty, new Binding(nameof(TestExplorerViewModel.Details)) { Mode = BindingMode.OneWay });

        var splitter = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Background = ThemeBrushes.Get("panel.border"), ResizeDirection = GridResizeDirection.Rows };
        var body = new Grid { RowDefinitions = new RowDefinitions("3*,Auto,2*") };
        body.Children.Add(new Panel { Children = { _list, _empty } });
        Grid.SetRow(splitter, 1);
        body.Children.Add(splitter);
        Grid.SetRow(details, 2);
        body.Children.Add(details);
        details.Bind(IsVisibleProperty, new Binding(nameof(TestExplorerViewModel.HasDetails)));
        splitter.Bind(IsVisibleProperty, new Binding(nameof(TestExplorerViewModel.HasDetails)));

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), Background = ThemeBrushes.Get("sideBar.background") };
        root.Children.Add(toolbar);
        Grid.SetRow(filter, 1);
        root.Children.Add(filter);
        Grid.SetRow(summary, 2);
        root.Children.Add(summary);
        Grid.SetRow(body, 3);
        root.Children.Add(body);
        Content = root;

        vm.RevealRequested += OnReveal;
        vm.PropertyChanged += OnVmChanged;
    }

    /// <summary>The view-model (tests).</summary>
    internal TestExplorerViewModel Model => _vm;
    internal ListBox List => _list;

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TestExplorerViewModel.Selected) && _vm.Selected is { } s) Dispatcher.UIThread.Post(() => { if (!_disposed) _list.ScrollIntoView(s); }, DispatcherPriority.Background);
    }

    private void OnReveal(TestRow? row)
    {
        if (row is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            _list.ScrollIntoView(row);
            ToolWindowFocus.FocusRow(_list);
        }, DispatcherPriority.Background);
    }

    private static void AutomationName(Control c, string name) => Avalonia.Automation.AutomationProperties.SetName(c, name);

    private Button Button(string icon, string tip, Action onClick, string? enabledBy)
    {
        var b = new Button { Content = new Icon(icon, 14) { Foreground = ThemeBrushes.Get("foreground") }, Padding = new Thickness(4), Margin = new Thickness(0, 0, 1, 0) };
        b.Classes.Add("flat");
        ToolTip.SetTip(b, tip);
        AutomationName(b, tip);
        b.Click += (_, _) => onClick();
        if (enabledBy is not null) b.Bind(IsEnabledProperty, new Binding(enabledBy));
        return b;
    }

    private static Control Separator() => new Border { Width = 1, Height = 16, Margin = new Thickness(4, 0), Background = ThemeBrushes.Get("panel.border"), VerticalAlignment = VerticalAlignment.Center };

    private Control RowFor(TestRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,16,18,Auto,*,Auto"), Height = 22, Background = Brushes.Transparent };
        ToolTip.SetShowDelay(grid, 600);
        grid.Bind(ToolTip.TipProperty, new Binding(nameof(TestRow.Tooltip)));
        grid.Children.Add(new Border { Width = row.Indent + 4, Height = 1 });

        var chevron = new Icon { Size = 12, Foreground = ThemeBrushes.Get("descriptionForeground"), HorizontalAlignment = HorizontalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand) };
        chevron.Bind(Icon.KindProperty, new Binding(nameof(TestRow.Chevron)));
        chevron.PointerPressed += (_, e) => { e.Handled = true; _vm.Toggle(row); };
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);

        var state = new Icon { Size = 14, HorizontalAlignment = HorizontalAlignment.Center };
        state.Bind(Icon.KindProperty, new Binding(nameof(TestRow.StateIcon)));
        state.Bind(Icon.ForegroundProperty, new Binding(nameof(TestRow.StateColor)) { Converter = HexBrush });
        Grid.SetColumn(state, 2);
        grid.Children.Add(state);

        var kind = new Icon { Size = 13, Margin = new Thickness(3, 0, 0, 0), Foreground = ThemeBrushes.Get("descriptionForeground") };
        kind.Bind(Icon.KindProperty, new Binding(nameof(TestRow.KindIcon)));
        kind.IsVisible = row.KindIcon is not null;
        Grid.SetColumn(kind, 3);
        grid.Children.Add(kind);

        var name = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Spacing = 6 };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.Bind(TextBlock.TextProperty, new Binding(nameof(TestRow.Label)));
        var detail = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = ThemeBrushes.Get("descriptionForeground"), TextTrimming = TextTrimming.CharacterEllipsis };
        detail.Bind(TextBlock.TextProperty, new Binding(nameof(TestRow.Detail)));
        name.Children.Add(label);
        name.Children.Add(detail);
        Grid.SetColumn(name, 4);
        grid.Children.Add(name);

        var duration = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0), FontSize = 11, Foreground = ThemeBrushes.Get("descriptionForeground") };
        duration.Bind(TextBlock.TextProperty, new Binding(nameof(TestRow.DurationText)));
        Grid.SetColumn(duration, 5);
        grid.Children.Add(duration);
        return grid;
    }

    private static readonly IValueConverter HexBrush = new FuncValueConverter<string?, IBrush?>(hex => hex is not null && Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : null);

    private static TestRow? RowAt(object? source)
    {
        for (var v = source as Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
            if (v is ListBoxItem item) return item.DataContext as TestRow;
        return null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm.Selected is not { } row) return;
        var ctrl = e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Enter: e.Handled = true; _ = _vm.GoToAsync(row, preview: false); break;
            case Key.Right when row.HasChildren: e.Handled = true; if (!row.IsExpanded) _vm.Expand(row); break;
            case Key.Left:
                e.Handled = true;
                if (row.HasChildren && row.IsExpanded) _vm.Collapse(row);
                else if (row.Node.Parent is { } parent && _vm.Rows.FirstOrDefault(r => ReferenceEquals(r.Node, parent)) is { } p) _vm.Selected = p;
                break;
            case Key.C when ctrl: e.Handled = true; _ = CopyAsync(_vm.CopyText(row)); break;
        }
    }

    private async Task CopyAsync(string text)
    {
        if (HostServices?.GetService<IWindowService>() is { } window) await window.CopyToClipboardAsync(text).ConfigureAwait(true);
    }

    internal IServiceProvider? HostServices { get; init; }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var row = RowAt(e.Source) ?? _vm.Selected;
        if (row is null) return;
        e.Handled = true;
        _vm.Selected = row;
        ContextMenus.Open(MenuEntries(row), _list);
    }

    internal IReadOnlyList<MenuEntry> MenuEntries(TestRow row)
    {
        var running = _vm.IsRunning;
        var entries = new List<MenuEntry>
        {
            new("Run", () => _vm.RunAsync(row, debug: false)) { Icon = "play", IsEnabled = !running },
            new("Debug", () => _vm.RunAsync(row, debug: true)) { Icon = "debug-alt", IsEnabled = !running },
            MenuEntry.Separator(),
            new("Go to Test", () => _vm.GoToAsync(row, preview: false)) { IsEnabled = row.Node.Location is not null || row.Node.IsCase },
        };
        if (row.Node.FailureLocation is not null) entries.Add(new MenuEntry("Go to Failure", () => _vm.GoToAsync(row, failure: true, preview: false)));
        entries.Add(MenuEntry.Separator());
        entries.Add(new MenuEntry("Copy Name", () => CopyAsync(_vm.CopyText(row))));
        if (row.Node.Message is { Length: > 0 } message) entries.Add(new MenuEntry("Copy Message", () => CopyAsync(message + (row.Node.StackTrace is { } st ? Environment.NewLine + st : ""))));
        if (row.HasChildren) entries.Add(new MenuEntry(row.IsExpanded ? "Collapse" : "Expand", () => { _vm.Toggle(row); return Task.CompletedTask; }));
        return entries;
    }

    /// <summary>View &gt; Tool Windows &gt; Test Explorer: the keyboard on the selected (else the first visible) row, not the toolbar.</summary>
    public bool FocusPreferred() => ToolWindowFocus.FocusRow(_list);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vm.RevealRequested -= OnReveal;
        _vm.PropertyChanged -= OnVmChanged;
        _list.ItemsSource = null;
        DataContext = null;
        _release(_vm);
    }
}
