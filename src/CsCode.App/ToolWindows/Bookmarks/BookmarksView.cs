using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using CsCode.App.Views;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Ui;
using CsCode.Ui.Menus;
using CsCode.Ui.Theming;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App.ToolWindows.Bookmarks;

/// <summary>
/// Visual Studio's Bookmark Window (View &gt; Bookmark Window): a toolbar (Previous, Next, Enable or Disable All, Rename, Delete, Clear All), a
/// column header and a virtualised list grouped by file. Each bookmark row has its check box (enabled), its name (the label, else "Line N"), its
/// line and the line's text. Double-click or Enter goes to it, F2 renames it, Delete deletes it (on a file's row: all of the file's), Ctrl+C
/// copies <c>path(line): text</c>; the right-click menu has all of these.
/// </summary>
public sealed class BookmarksView : UserControl, IDisposable, IClipboardTarget, IToolWindowFocus
{
    private const string Columns = "28,200,60,*";
    private readonly BookmarksViewModel _vm;
    private readonly ListBox _list;
    private readonly TextBlock _empty;
    private readonly IWindowService? _window;
    private readonly IKeybindingService? _keys;
    private string? _selectedId;
    private Uri? _selectedHeader;

    public BookmarksView(BookmarksViewModel vm, IWindowService? window = null, IKeybindingService? keys = null)
    {
        _vm = vm;
        _window = window;
        _keys = keys;
        DataContext = vm;

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 2) };
        toolbar.Children.Add(Button("arrow-up", "Previous Bookmark", BookmarkCommandIds.JumpToPrevious, () => _ = vm.RunAsync(BookmarkCommandIds.JumpToPrevious)));
        toolbar.Children.Add(Button("arrow-down", "Next Bookmark", BookmarkCommandIds.JumpToNext, () => _ = vm.RunAsync(BookmarkCommandIds.JumpToNext)));
        toolbar.Children.Add(ViewHelpers.Separator());
        toolbar.Children.Add(Button("check-all", "Enable or Disable All Bookmarks", BookmarkCommandIds.ToggleAllEnabled, vm.ToggleAllEnabled));
        toolbar.Children.Add(Button("edit", "Rename Bookmark (F2)", null, () => { if (Selected is { IsHeader: false } r) _ = vm.RenameAsync(r); }));
        toolbar.Children.Add(Button("trash", "Delete (Del)", null, () => { if (Selected is { } r) vm.Delete(r); }));
        toolbar.Children.Add(ViewHelpers.Separator());
        toolbar.Children.Add(Button("clear-all", "Clear All Bookmarks", BookmarkCommandIds.ClearFromAllFiles, () => _ = vm.RunAsync(BookmarkCommandIds.ClearFromAllFiles)));
        var summary = ViewHelpers.Label("", "descriptionForeground");
        summary.Margin = new Thickness(8, 0);
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(BookmarksViewModel.Summary)));
        toolbar.Children.Add(summary);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Height = 22, Background = ThemeBrushes.Get("sideBarSectionHeader.background") };
        foreach (var (text, col) in new[] { ("", 0), ("Bookmark", 1), ("Line", 2), ("Code", 3) })
        {
            var t = ViewHelpers.Label(text, "sideBarSectionHeader.foreground", 11, FontWeight.SemiBold);
            t.Margin = new Thickness(6, 0);
            Grid.SetColumn(t, col);
            header.Children.Add(t);
        }

        _list = new ListBox
        {
            ItemsSource = vm.Rows,
            ItemTemplate = new FuncDataTemplate<BookmarkRow>((r, _) => r is null ? new Panel() : RowFor(r), supportsRecycling: false),
            SelectionMode = SelectionMode.Single,
        };
        _list.DoubleTapped += async (_, e) =>
        {
            if (e.Source is CheckBox) return;
            if (Selected is { } r) await vm.NavigateAsync(r).ConfigureAwait(true);
        };
        // tunnelling: a focused row (ListBoxItem) handles Enter and Space itself before they would bubble up to the list
        _list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        _list.SelectionChanged += (_, _) => Remember();
        _list.ContextRequested += OnContextRequested;
        ToolWindowFocus.KeepOnRows(_list); // every change of the bookmarks replaces the rows
        vm.RowsReplaced += OnRowsReplaced;

        _empty = ViewHelpers.Label(EmptyText(), "descriptionForeground");
        _empty.Margin = new Thickness(12, 8);
        _empty.VerticalAlignment = VerticalAlignment.Top;
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.IsVisible = vm.IsEmpty;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Background = ThemeBrushes.Get("panel.background") };
        root.Children.Add(toolbar);
        Grid.SetRow(header, 1);
        root.Children.Add(header);
        Grid.SetRow(_list, 2);
        root.Children.Add(_list);
        Grid.SetRow(_empty, 2);
        root.Children.Add(_empty);
        Content = root;
        Avalonia.Automation.AutomationProperties.SetName(_list, "Bookmarks");
    }

    internal ListBox List => _list;

    internal BookmarkRow? Selected => _list.SelectedItem as BookmarkRow;

    private Button Button(string icon, string tooltip, string? commandId, Action run)
    {
        var label = commandId is not null && _keys?.GetLabel(commandId) is { Length: > 0 } keys ? $"{tooltip} ({keys})" : tooltip;
        var b = ViewHelpers.IconButton(icon, label, run);
        Avalonia.Automation.AutomationProperties.SetName(b, tooltip);
        return b;
    }

    private string EmptyText()
    {
        var keys = _keys?.GetLabel(BookmarkCommandIds.Toggle) is { Length: > 0 } k ? $" ({k})" : "";
        return $"There are no bookmarks. Toggle Bookmark{keys} marks the caret's line in an editor; Edit > Bookmarks and the editor's right-click menu have it too.";
    }

    /// <summary>View &gt; Bookmark Window: the keyboard on a bookmark (Enter goes to it); none without bookmarks.</summary>
    public bool FocusPreferred() => ToolWindowFocus.FocusRow(_list);

    public bool CanCopy => Selected is not null && _window is not null;

    public async Task CopyAsync()
    {
        if (Selected is { } row && _window is not null) await _window.CopyToClipboardAsync(BookmarksViewModel.CopyText(row)).ConfigureAwait(true);
    }

    private async void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (ClipboardKeys.TryHandle(e, this)) return;
        if (Selected is not { } row || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await _vm.NavigateAsync(row).ConfigureAwait(true);
                break;
            case Key.F2 when !row.IsHeader:
                e.Handled = true;
                await _vm.RenameAsync(row).ConfigureAwait(true);
                break;
            case Key.Delete:
                e.Handled = true;
                _vm.Delete(row);
                break;
            case Key.Space when !row.IsHeader:
                e.Handled = true;
                _vm.SetEnabled(row, !row.Enabled);
                break;
        }
    }

    /// <summary>The row menu for the selected row (built when it opens).</summary>
    internal IReadOnlyList<MenuEntry> RowMenu()
    {
        var row = Selected;
        string? Keys(string id) => _keys?.GetLabel(id);
        return MenuSections.Join(
            [
                new MenuEntry("_Go To Bookmark", async () => { if (row is not null) await _vm.NavigateAsync(row).ConfigureAwait(true); }) { Gesture = "Enter", IsEnabled = row is not null },
                new MenuEntry("_Rename", async () => { if (row is not null) await _vm.RenameAsync(row).ConfigureAwait(true); }) { Gesture = "F2", Icon = "edit", IsEnabled = row is { IsHeader: false } && _window is not null },
                new MenuEntry(row is { Enabled: false } ? "_Enable" : "_Disable", () => { if (row is not null) _vm.SetEnabled(row, !row.Enabled); return Task.CompletedTask; }) { Gesture = "Space", IsEnabled = row is { IsHeader: false } },
                new MenuEntry(row is { IsHeader: true } ? "_Delete Bookmarks in File" : "_Delete", () => { if (row is not null) _vm.Delete(row); return Task.CompletedTask; }) { Gesture = "Del", Icon = "trash", IsEnabled = row is not null },
            ],
            ClipboardKeys.Entries(this),
            [
                new MenuEntry("_Previous Bookmark", () => _vm.RunAsync(BookmarkCommandIds.JumpToPrevious)) { Gesture = Keys(BookmarkCommandIds.JumpToPrevious), Icon = "arrow-up" },
                new MenuEntry("_Next Bookmark", () => _vm.RunAsync(BookmarkCommandIds.JumpToNext)) { Gesture = Keys(BookmarkCommandIds.JumpToNext), Icon = "arrow-down" },
            ],
            [
                new MenuEntry(_vm.AnyEnabled ? "Disable _All Bookmarks" : "Enable _All Bookmarks", () => { _vm.ToggleAllEnabled(); return Task.CompletedTask; }) { IsEnabled = !_vm.IsEmpty },
                new MenuEntry("_Clear All Bookmarks", () => _vm.RunAsync(BookmarkCommandIds.ClearFromAllFiles)) { Gesture = Keys(BookmarkCommandIds.ClearFromAllFiles), Icon = "clear-all", IsEnabled = !_vm.IsEmpty },
            ]);
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: BookmarkRow row }) _list.SelectedItem = row;
        e.Handled = true;
        ContextMenus.Open(RowMenu(), _list, accessibleName: "Bookmarks menu");
    }

    private void Remember()
    {
        if (Selected is not { } row) return;
        _selectedId = row.Bookmark?.Id;
        _selectedHeader = row.IsHeader ? row.Uri : null;
    }

    /// <summary>The rows were rebuilt (a bookmark added, moved by an edit, renamed...): the selection stays on the same bookmark.</summary>
    private void OnRowsReplaced()
    {
        _empty.IsVisible = _vm.IsEmpty;
        var again = _vm.Rows.FirstOrDefault(r => _selectedId is not null ? r.Bookmark?.Id == _selectedId : _selectedHeader is not null && r.IsHeader && r.Uri == _selectedHeader);
        if (again is not null) _list.SelectedItem = again;
    }

    private Control RowFor(BookmarkRow row)
    {
        if (row.IsHeader)
        {
            var h = ViewHelpers.Label(row.HeaderText ?? "", "foreground", 12, FontWeight.SemiBold);
            h.Margin = new Thickness(6, 0);
            h.TextTrimming = TextTrimming.CharacterEllipsis;
            var icon = ViewHelpers.Glyph("file", 14);
            icon.Margin = new Thickness(8, 0, 0, 0);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, h } };
            var border = new Border { Height = 22, Background = ThemeBrushes.Get("sideBarSectionHeader.background"), Child = panel };
            ToolTip.SetTip(border, row.Uri.IsFile ? row.Uri.LocalPath : row.Uri.ToString());
            return border;
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Height = 22, Background = Brushes.Transparent };
        var check = new CheckBox { IsChecked = row.Enabled, MinHeight = 0, Padding = default, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(check, row.Enabled ? "Enabled: Next and Previous Bookmark stop here" : "Disabled: Next and Previous Bookmark skip it");
        Avalonia.Automation.AutomationProperties.SetName(check, $"Enabled: {row.Name}");
        check.IsCheckedChanged += (_, _) => { if (check.IsChecked is { } on && on != row.Enabled) _vm.SetEnabled(row, on); };
        grid.Children.Add(check);
        var name = ViewHelpers.Label(row.Name, row.HasLabel ? "foreground" : "descriptionForeground");
        name.Margin = new Thickness(6, 0);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        if (!row.Enabled) name.Opacity = 0.6;
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var line = ViewHelpers.Label(row.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), "descriptionForeground");
        line.Margin = new Thickness(6, 0);
        Grid.SetColumn(line, 2);
        grid.Children.Add(line);
        var code = ViewHelpers.Label("", "foreground");
        code.Margin = new Thickness(6, 0);
        code.TextTrimming = TextTrimming.CharacterEllipsis;
        code.FontFamily = ThemeBrushes.CodeFont;
        code.FontFeatures = ThemeBrushes.CodeFontFeatures;
        code.Bind(TextBlock.TextProperty, new Binding(nameof(BookmarkRow.Preview)) { Source = row });
        Grid.SetColumn(code, 3);
        grid.Children.Add(code);
        grid.AttachedToVisualTree += async (_, _) => await _vm.LoadPreviewAsync(row).ConfigureAwait(true);
        return grid;
    }

    public void Dispose()
    {
        _vm.RowsReplaced -= OnRowsReplaced;
        _list.ItemsSource = null;
        DataContext = null;
        _vm.Dispose();
    }
}

/// <summary>Visual Studio's Bookmark Window (View &gt; Bookmark Window, Ctrl+K Ctrl+W in the Visual Studio keymap), docked in the bottom well.</summary>
public sealed class BookmarksToolWindow : IToolWindow
{
    public string Id => BookmarkCommandIds.WindowId;
    public string Title => "Bookmarks";
    public string? IconKey => "bookmark";
    public ToolWindowLocation DefaultLocation => ToolWindowLocation.Bottom;
    /// <summary>The editor module registers it (shows and focuses the window), so the shell adds no toggle of its own.</summary>
    public string? ToggleCommandId => BookmarkCommandIds.ShowWindow;
    public string? AutoShowWhen => null;

    public Control CreateView(IServiceProvider services)
    {
        var workspace = services.GetService<IWorkspaceService>();
        var vm = new BookmarksViewModel(services.GetRequiredService<IBookmarkService>(), services.GetRequiredService<Ui.Threading.IUiDispatcher>(),
            services.GetService<INavigationService>(), services.GetService<IDocumentManager>(), workspace?.FileSystem ?? services.GetService<IFileSystem>(),
            services.GetService<ICommandService>(), services.GetService<IWindowService>());
        return new BookmarksView(vm, services.GetService<IWindowService>(), services.GetService<IKeybindingService>());
    }
}
