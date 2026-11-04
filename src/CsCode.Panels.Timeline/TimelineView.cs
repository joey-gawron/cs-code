using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CsCode.Core.History;
using CsCode.Core.Platform;
using CsCode.Core.Timeline;
using CsCode.Panels.Timeline.LocalHistory;
using CsCode.Ui;
using CsCode.Ui.Icons;
using CsCode.Ui.Menus;
using CsCode.Ui.Theming;

namespace CsCode.Panels.Timeline;

/// <summary>
/// The Timeline window (VS Code's Timeline view): the file's name, a toolbar (Filter, follow the active editor, Refresh, Create Entry, more
/// actions), and one virtualised list of its history newest first: local-history entries and the commits that changed it, each with how long
/// ago. Clicking a row (or Enter) compares it: a local entry with the file, a commit with the version before it. The right-click menu has the
/// rest (Restore Contents, Rename, Delete...); Delete and F2 on a local entry delete and rename it. Disposing releases the view-model.
/// </summary>
public sealed class TimelineView : UserControl, IDisposable, IToolWindowFocus
{
    private readonly TimelineHost _host;
    private readonly TimelineViewModel _vm;
    private readonly ListBox _list;
    private readonly TextBlock _title;
    private readonly Button _follow;
    private bool _syncing, _disposed;

    public TimelineView(TimelineHost host, TimelineViewModel vm)
    {
        _host = host;
        _vm = vm;
        DataContext = vm;

        _title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var filter = IconButton(IconKey.Filter, "Filter Timeline", () => Task.CompletedTask);
        filter.Click += (_, _) => ContextMenus.Open(FilterMenu(), filter, accessibleName: "Filter Timeline");
        actions.Children.Add(filter);
        _follow = IconButton(IconKey.Pin, "", () => { _host.FollowActiveEditor = !_host.FollowActiveEditor; return Task.CompletedTask; });
        actions.Children.Add(_follow);
        actions.Children.Add(IconButton(IconKey.Refresh, "Refresh", _vm.ReloadAsync));
        actions.Children.Add(IconButton(IconKey.Add, "Create Local History Entry...", _host.CreateEntryAsync));
        var more = IconButton(IconKey.Ellipsis, "More Actions...", () => Task.CompletedTask);
        more.Click += (_, _) => ContextMenus.Open(MoreMenu(), more, accessibleName: "Timeline actions");
        actions.Children.Add(more);
        var top = new DockPanel { Margin = new Thickness(4, 3, 4, 1) };
        DockPanel.SetDock(actions, Avalonia.Controls.Dock.Right);
        top.Children.Add(actions);
        top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { new Icon(IconKey.History, 14) { Foreground = ThemeBrushes.Get("foreground") }, _title } });

        _list = new ListBox
        {
            ItemsSource = vm.Rows,
            SelectionMode = SelectionMode.Single,
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<TimelineRow>((r, _) => r is null ? new Panel() : RowFor(r), supportsRecycling: false),
        };
        Avalonia.Automation.AutomationProperties.SetName(_list, "Timeline");
        _list.AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _list.SelectionChanged += OnSelectionChanged;
        _list.Tapped += (_, e) => { if (RowAt(e.Source) is { } row) { e.Handled = true; _ = ActivateAsync(row); } };
        _list.ContextRequested += OnContextRequested;
        ToolWindowFocus.KeepOnRows(_list); // every change replaces the rows (one Reset)

        var empty = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Foreground = ThemeBrushes.Get("descriptionForeground") };
        empty.Bind(TextBlock.TextProperty, new Binding(nameof(TimelineViewModel.EmptyText)));
        empty.Bind(IsVisibleProperty, new Binding(nameof(TimelineViewModel.IsEmpty)));

        var root = new DockPanel { Background = ThemeBrushes.Get("sideBar.background") };
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        root.Children.Add(top);
        root.Children.Add(new Panel { Children = { _list, empty } });
        Content = root;

        _vm.PropertyChanged += OnModelChanged;
        _vm.RowsRebuilt += SyncSelection;
        SyncHeader();
    }

    internal ListBox List => _list;
    internal TimelineViewModel Model => _vm;

    private static Button IconButton(string icon, string tip, Func<Task> run)
    {
        var b = new Button { Content = new Icon(icon, 14) { Foreground = ThemeBrushes.Get("foreground") }, Padding = new Thickness(3), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false };
        b.Classes.Add("flat");
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => _ = run();
        return b;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimelineViewModel.Title) or nameof(TimelineViewModel.Resource) or nameof(TimelineViewModel.FollowActiveEditor)) SyncHeader();
        else if (e.PropertyName == nameof(TimelineViewModel.Selected)) SyncSelection();
    }

    private void SyncHeader()
    {
        _title.Text = _vm.Title;
        ToolTip.SetTip(_title, _vm.Resource is { } r ? HistoryFormat.Describe(r) : null);
        var tip = _host.FollowActiveEditor ? "Pin the Current Timeline (stop following the active editor)" : "Unpin the Timeline (follow the active editor)";
        _follow.Content = new Icon(_host.FollowActiveEditor ? IconKey.Pin : IconKey.Pinned, 14) { Foreground = ThemeBrushes.Get("foreground") };
        ToolTip.SetTip(_follow, tip);
        Avalonia.Automation.AutomationProperties.SetName(_follow, tip);
    }

    private void SyncSelection()
    {
        if (_disposed || ReferenceEquals(_list.SelectedItem, _vm.Selected)) return;
        _syncing = true;
        try { _list.SelectedItem = _vm.Selected; }
        finally { _syncing = false; }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _disposed) return;
        _vm.Selected = _list.SelectedItem as TimelineRow;
    }

    private static TimelineRow? RowAt(object? source)
    {
        for (var v = source as Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
            if (v is ListBoxItem item) return item.DataContext as TimelineRow;
        return null;
    }

    /// <summary>A row's default action: a local entry ↔ the file, a commit's change; the "Load more" row loads the next page.</summary>
    internal Task ActivateAsync(TimelineRow row)
    {
        if (row.IsLoadMore) return _vm.LoadMoreAsync();
        _vm.Selected = row;
        var item = row.Item!;
        return item.Data is LocalHistoryEntry ? _host.CompareWithFileAsync(item) : _host.CompareWithPreviousAsync(item);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm.Selected is not { } row) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = ActivateAsync(row);
                break;
            case Key.Delete when row.Item?.Data is LocalHistoryEntry entry:
                e.Handled = true;
                _ = _host.DeleteAsync(entry);
                break;
            case Key.F2 when row.Item?.Data is LocalHistoryEntry entry:
                e.Handled = true;
                _ = _host.RenameAsync(entry);
                break;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var row = RowAt(e.Source) ?? _vm.Selected;
        if (row is null || row.IsLoadMore) return;
        e.Handled = true;
        _vm.Selected = row;
        ContextMenus.Open(MenuEntries(row), _list, accessibleName: "Timeline entry menu");
    }

    /// <summary>A row's right-click menu: VS Code's local history entry menu, or a commit's.</summary>
    internal IReadOnlyList<MenuEntry> MenuEntries(TimelineRow row)
    {
        var item = row.Item!;
        var entries = new List<MenuEntry>
        {
            new("Compare with _File", () => _host.CompareWithFileAsync(item)) { Icon = "diff", Gesture = item.Data is LocalHistoryEntry ? "Enter" : null },
            new("Compare with _Previous", () => _host.CompareWithPreviousAsync(item)) { Gesture = item.Data is LocalHistoryEntry ? null : "Enter" },
            new("_Show Contents", () => _host.ShowContentsAsync(item)) { Icon = "go-to-file" },
        };
        if (item.Data is LocalHistoryEntry entry)
        {
            entries.Add(MenuEntry.Separator());
            entries.Add(new MenuEntry("_Restore Contents...", () => _host.RestoreAsync(entry)) { Icon = IconKey.Undo });
            entries.Add(MenuEntry.Separator());
            entries.Add(new MenuEntry("Re_name...", () => _host.RenameAsync(entry)) { Icon = IconKey.Edit, Gesture = "F2" });
            entries.Add(new MenuEntry("_Delete", () => _host.DeleteAsync(entry)) { Icon = IconKey.Trash, Gesture = "Delete" });
        }
        else
        {
            entries.Add(MenuEntry.Separator());
            entries.Add(new MenuEntry("Copy Commit _ID", () => CopyAsync(item.Id)) { Icon = IconKey.Copy });
            entries.Add(new MenuEntry("Copy Commit _Message", () => CopyAsync(item.Label)));
        }
        return entries;
    }

    private async Task CopyAsync(string text)
    {
        if (_host.Windows is { } windows) await windows.CopyToClipboardAsync(text).ConfigureAwait(true);
    }

    internal IReadOnlyList<MenuEntry> FilterMenu() =>
        _host.Providers.Select(p => new MenuEntry(p.Label, () => { _host.SetSourceIncluded(p.Id, !_host.IsSourceIncluded(p.Id)); return Task.CompletedTask; })
        {
            IsChecked = _host.IsSourceIncluded(p.Id),
        }).ToList();

    internal IReadOnlyList<MenuEntry> MoreMenu() =>
    [
        new("_Find Entry to Restore...", () => _host.RestoreViaPickerAsync()) { Icon = IconKey.History },
        new("_Create Entry...", _host.CreateEntryAsync) { Icon = IconKey.Add },
        MenuEntry.Separator(),
        new("Delete All Entries of _This File...", () => _host.DeleteAllOfFileAsync(_vm.Resource)) { IsEnabled = _vm.Resource is not null },
        new("Delete _All Local History...", _host.DeleteAllAsync) { Icon = IconKey.Trash },
    ];

    private Control RowFor(TimelineRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("22,Auto,*,Auto"), Height = 22, Background = Brushes.Transparent };
        if (row.Tooltip is { } tip) { ToolTip.SetTip(grid, tip); ToolTip.SetShowDelay(grid, 600); }
        var icon = new Icon(row.IconKey, 14) { Foreground = ThemeBrushes.Get(row.IsLoadMore ? "descriptionForeground" : "foreground"), HorizontalAlignment = HorizontalAlignment.Center };
        grid.Children.Add(icon);

        var label = new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        if (row.IsLoadMore) label.Foreground = ThemeBrushes.Get("textLink.foreground");
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var description = new TextBlock { Text = row.Description, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = ThemeBrushes.Get("descriptionForeground"), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(description, 2);
        grid.Children.Add(description);

        var time = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new Thickness(6, 0, 6, 0), Foreground = ThemeBrushes.Get("descriptionForeground") };
        time.Bind(TextBlock.TextProperty, new Binding(nameof(TimelineRow.Time)) { Source = row });
        Grid.SetColumn(time, 3);
        grid.Children.Add(time);
        return grid;
    }

    /// <summary>View &gt; Tool Windows &gt; Timeline: the keyboard on the selected (else the first visible) row.</summary>
    public bool FocusPreferred() => ToolWindowFocus.FocusRow(_list);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vm.PropertyChanged -= OnModelChanged;
        _vm.RowsRebuilt -= SyncSelection;
        _list.SelectionChanged -= OnSelectionChanged;
        _list.ItemsSource = null;
        DataContext = null;
        _host.ReleaseViewModel(_vm);
    }
}
