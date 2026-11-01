using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CsCode.Core.Platform;
using CsCode.Ui;
using CsCode.Ui.Icons;
using CsCode.Ui.Menus;
using CsCode.Ui.Theming;

namespace CsCode.Panels.Navigation;

/// <summary>
/// The Hierarchy window: a toolbar (direction toggle, Refresh, Collapse All, Clear), the header ("Calls To 'Run'"), and the virtualised list of
/// the tree's visible rows (chevron, symbol kind, name, detail and place). Selecting a row shows its call site (or the symbol) in a preview tab
/// without taking the keyboard; Enter or a double-click opens it and moves the keyboard there. Right / Left expand and collapse (Left on a
/// collapsed row goes to its parent). The model lives in <see cref="HierarchyHost"/>; this view only subscribes, and unsubscribes on dispose.
/// </summary>
public sealed class HierarchyView : UserControl, IDisposable, IToolWindowFocus
{
    private readonly HierarchyHost _host;
    private readonly HierarchyModel _vm;
    private readonly ListBox _list;
    private readonly Button _toggle;
    private bool _syncing, _disposed;

    public HierarchyView(HierarchyHost host, IServiceProvider? services = null)
    {
        _host = host;
        _vm = host.Model;
        HostServices = services;
        DataContext = _vm;

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 3, 4, 1) };
        _toggle = Button("arrow-swap", _vm.ToggleTip, () => _ = _host.ToggleDirectionAsync());
        _toggle.Bind(IsEnabledProperty, new Binding(nameof(HierarchyModel.HasQuery)));
        toolbar.Children.Add(_toggle);
        var refresh = Button(IconKey.Refresh, "Refresh", () => _ = _host.RefreshAsync());
        refresh.Bind(IsEnabledProperty, new Binding(nameof(HierarchyModel.HasQuery)));
        toolbar.Children.Add(refresh);
        toolbar.Children.Add(Button("collapse-all", "Collapse All", _vm.CollapseAll));
        toolbar.Children.Add(Button("clear-all", "Clear", _vm.Clear));

        var header = new TextBlock { Margin = new Thickness(8, 1, 8, 3), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        header.Bind(TextBlock.TextProperty, new Binding(nameof(HierarchyModel.Header)));

        _list = new ListBox
        {
            ItemsSource = _vm.Rows,
            ItemTemplate = new FuncDataTemplate<HierarchyNode>((row, _) => row is null ? new Panel() : RowFor(row), supportsRecycling: false),
            SelectionMode = SelectionMode.Single,
            Background = Brushes.Transparent,
        };
        Avalonia.Automation.AutomationProperties.SetName(_list, "Hierarchy");
        _list.AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel); // before the ListBox, which takes Left/Right itself
        _list.SelectionChanged += OnSelectionChanged;
        _list.DoubleTapped += (_, e) =>
        {
            if (RowAt(e.Source) is not { } row) return;
            e.Handled = true;
            _ = _host.GoToAsync(row, preview: false, preserveFocus: false);
        };
        _list.ContextRequested += OnContextRequested;
        ToolWindowFocus.KeepOnRows(_list); // every expand/collapse replaces the rows (one Reset)

        var empty = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Foreground = ThemeBrushes.Get("descriptionForeground") };
        empty.Bind(TextBlock.TextProperty, new Binding(nameof(HierarchyModel.EmptyText)));
        empty.Bind(IsVisibleProperty, new Binding(nameof(HierarchyModel.IsEmpty)));

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Background = ThemeBrushes.Get("panel.background") };
        root.Children.Add(toolbar);
        Grid.SetRow(header, 1);
        root.Children.Add(header);
        var body = new Panel { Children = { _list, empty } };
        Grid.SetRow(body, 2);
        root.Children.Add(body);
        Content = root;

        _vm.PropertyChanged += OnModelChanged;
        _vm.RowsRebuilt += OnRowsRebuilt;
        SyncSelection();
    }

    internal IServiceProvider? HostServices { get; }
    internal ListBox List => _list;
    internal Button DirectionButton => _toggle;

    private Button Button(string icon, string tip, Action onClick)
    {
        var b = new Button { Content = new Icon(icon, 14) { Foreground = ThemeBrushes.Get("foreground") }, Padding = new Thickness(4), Margin = new Thickness(0, 0, 1, 0) };
        b.Classes.Add("flat");
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HierarchyModel.ToggleTip))
        {
            ToolTip.SetTip(_toggle, _vm.ToggleTip);
            Avalonia.Automation.AutomationProperties.SetName(_toggle, _vm.ToggleTip);
        }
        else if (e.PropertyName == nameof(HierarchyModel.Selected)) SyncSelection();
    }

    private void OnRowsRebuilt() => SyncSelection();

    /// <summary>The model's selection on the list, without treating it as the user's (no navigation).</summary>
    private void SyncSelection()
    {
        if (_disposed || ReferenceEquals(_list.SelectedItem, _vm.Selected)) return;
        _syncing = true;
        try { _list.SelectedItem = _vm.Selected; }
        finally { _syncing = false; }
        if (_vm.Selected is { } s) Dispatcher.UIThread.Post(() => { if (!_disposed) _list.ScrollIntoView(s); }, DispatcherPriority.Background);
    }

    /// <summary>The user moved the selection: preview the row's place, the keyboard stays here (VS Code's call hierarchy).</summary>
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // A rebuild resets the list (its selection goes, and may come back): only a different row than the model's is the user's choice.
        if (_syncing || _disposed || _list.SelectedItem is not HierarchyNode row || ReferenceEquals(row, _vm.Selected)) return;
        _vm.Selected = row;
        _ = _host.GoToAsync(row, preview: true, preserveFocus: true);
    }

    private static HierarchyNode? RowAt(object? source)
    {
        for (var v = source as Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
            if (v is ListBoxItem item) return item.DataContext as HierarchyNode;
        return null;
    }

    internal void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm.Selected is not { } row) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = _host.GoToAsync(row, preview: false, preserveFocus: false);
                break;
            case Key.Right when row.HasChildren:
                e.Handled = true;
                if (!row.IsExpanded) _ = _vm.ExpandAsync(row);
                break;
            case Key.Left:
                e.Handled = true;
                if (row.IsExpanded) _vm.Collapse(row);
                else if (row.Parent is { } parent) { _vm.Selected = parent; }
                break;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var row = RowAt(e.Source) ?? _vm.Selected;
        if (row is null) return;
        e.Handled = true;
        if (!ReferenceEquals(_vm.Selected, row)) _vm.Selected = row;
        ContextMenus.Open(MenuEntries(row), _list, accessibleName: "Hierarchy menu");
    }

    internal IReadOnlyList<MenuEntry> MenuEntries(HierarchyNode row)
    {
        var entries = new List<MenuEntry>();
        if (row.Sites.Count > 0) entries.Add(new MenuEntry("Go To _Call Site", () => _host.GoToAsync(row, preview: false, preserveFocus: false)) { Gesture = "Enter" });
        entries.Add(new MenuEntry("Go To _Definition", () => _host.GoToSymbolAsync(row)) { Icon = "go-to-file", Gesture = row.Sites.Count > 0 ? null : "Enter" });
        entries.Add(MenuEntry.Separator());
        entries.Add(new MenuEntry(_vm.IsCalls ? "_Show Call Hierarchy" : "_Show Type Hierarchy", () => _host.RerootAsync(row)) { IsEnabled = row.Parent is not null });
        if (row.HasChildren) entries.Add(new MenuEntry(row.IsExpanded ? "Co_llapse" : "E_xpand", () => _vm.ToggleAsync(row)));
        entries.Add(MenuEntry.Separator());
        entries.Add(new MenuEntry("Cop_y", CopyAsync(row)) { Icon = "copy" });
        entries.Add(new MenuEntry("_Refresh", () => _host.RefreshAsync()) { Icon = IconKey.Refresh });
        return entries;
    }

    private Func<Task> CopyAsync(HierarchyNode row) => async () =>
    {
        if (HostServices?.GetService(typeof(IWindowService)) is IWindowService window) await window.CopyToClipboardAsync(row.Label).ConfigureAwait(true);
    };

    private Control RowFor(HierarchyNode row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,16,18,Auto,*"), Height = 22, Background = Brushes.Transparent };
        ToolTip.SetShowDelay(grid, 600);
        grid.Bind(ToolTip.TipProperty, new Binding(nameof(HierarchyNode.Tooltip)) { Source = row });
        grid.Children.Add(new Border { Width = row.Indent + 4, Height = 1 });

        var chevron = new Icon { Size = 12, Foreground = ThemeBrushes.Get("descriptionForeground"), HorizontalAlignment = HorizontalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand) };
        chevron.Bind(Icon.KindProperty, new Binding(nameof(HierarchyNode.Chevron)) { Source = row });
        chevron.PointerPressed += (_, e) => { e.Handled = true; _vm.Selected = row; _ = _vm.ToggleAsync(row); };
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);

        var kind = new Icon(row.Icon, 14) { Foreground = ThemeBrushes.Get("foreground"), HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumn(kind, 2);
        grid.Children.Add(kind);

        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        label.Bind(TextBlock.TextProperty, new Binding(nameof(HierarchyNode.Label)) { Source = row });
        Grid.SetColumn(label, 3);
        grid.Children.Add(label);

        var detail = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = ThemeBrushes.Get("descriptionForeground"), TextTrimming = TextTrimming.CharacterEllipsis };
        detail.Bind(TextBlock.TextProperty, new Binding(nameof(HierarchyNode.Detail)) { Source = row });
        Grid.SetColumn(detail, 4);
        grid.Children.Add(detail);
        return grid;
    }

    /// <summary>View &gt; Tool Windows &gt; Hierarchy: the keyboard on the selected (else the first visible) row, not the toolbar.</summary>
    public bool FocusPreferred() => ToolWindowFocus.FocusRow(_list);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vm.PropertyChanged -= OnModelChanged;
        _vm.RowsRebuilt -= OnRowsRebuilt;
        _list.SelectionChanged -= OnSelectionChanged;
        _list.ItemsSource = null;
        DataContext = null;
    }
}
