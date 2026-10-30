using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using CsCode.App.Views;
using CsCode.Core.Platform;
using CsCode.Ui;
using CsCode.Ui.Controls;
using CsCode.Ui.Menus;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;

namespace CsCode.App.ToolWindows.ErrorList;

/// <summary>
/// The Error List view: severity chips with live counts, a search box, group-by-file, a column header and a virtualised list.
/// The model hands over a finished immutable row array about ten times a second at most; this view only displays it.
/// </summary>
public sealed class ErrorListView : UserControl, IDisposable, IClipboardTarget, IToolWindowFocus
{
    private const string Columns = "24,90,*,220,60";
    private readonly ErrorListViewModel _vm;
    private readonly ListBox _list;
    private readonly IWindowService? _window;
    private readonly MenuServices? _menus;

    /// <param name="window">Copy and Show Error Help go through it; <paramref name="menus"/> adds contributed <c>problems/context</c> items.</param>
    public ErrorListView(ErrorListViewModel vm, IWindowService? window = null, MenuServices? menus = null)
    {
        _vm = vm;
        _window = window;
        _menus = menus;
        DataContext = vm;

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 4) };
        chips.Children.Add(Chip("error", nameof(ErrorListViewModel.ShowErrors), nameof(ErrorListViewModel.ErrorCount), "Error", "problemsErrorIcon.foreground"));
        chips.Children.Add(Chip("warning", nameof(ErrorListViewModel.ShowWarnings), nameof(ErrorListViewModel.WarningCount), "Warning", "problemsWarningIcon.foreground"));
        chips.Children.Add(Chip("info", nameof(ErrorListViewModel.ShowInfo), nameof(ErrorListViewModel.InfoCount), "Message", "problemsInfoIcon.foreground"));

        var group = new ToggleButton { Content = "Group by file", Padding = new Thickness(8, 2), MinHeight = 22, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        group.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(ErrorListViewModel.GroupByFile)) { Mode = BindingMode.TwoWay });
        var search = new FilterBox { Watermark = "Search Error List", Width = 220, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
        search.Bind(FilterBox.TextProperty, new Binding(nameof(ErrorListViewModel.Search)) { Mode = BindingMode.TwoWay });

        var top = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(search, Avalonia.Controls.Dock.Right);
        top.Children.Add(search);
        DockPanel.SetDock(group, Avalonia.Controls.Dock.Right);
        top.Children.Add(group);
        top.Children.Add(chips);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Height = 22, Background = ThemeBrushes.Get("sideBarSectionHeader.background") };
        foreach (var (text, col) in new[] { ("", 0), ("Code", 1), ("Description", 2), ("File", 3), ("Line", 4) })
        {
            var t = ViewHelpers.Label(text, "sideBarSectionHeader.foreground", 11, FontWeight.SemiBold);
            t.Margin = new Thickness(6, 0);
            Grid.SetColumn(t, col);
            header.Children.Add(t);
        }

        _list = new ListBox { ItemsSource = vm.Rows, ItemTemplate = new FuncDataTemplate<ErrorRow>((r, _) => r is null ? new Panel() : RowFor(r), supportsRecycling: true), SelectionMode = SelectionMode.Single };
        vm.PropertyChanged += OnVmChanged;
        _list.DoubleTapped += async (_, _) => { if (_list.SelectedItem is ErrorRow r) await vm.NavigateAsync(r).ConfigureAwait(true); };
        _list.KeyDown += async (_, e) =>
        {
            if (ClipboardKeys.TryHandle(e, this)) return; // Ctrl+C, Ctrl+Insert
            if (e.Key == Key.F1 && e.KeyModifiers == KeyModifiers.None && Selected is { HelpLink: not null } help) { e.Handled = true; await ShowHelpAsync(help).ConfigureAwait(true); return; }
            if (e.Key == Key.Enter && _list.SelectedItem is ErrorRow r) { e.Handled = true; await vm.NavigateAsync(r).ConfigureAwait(true); }
        };
        _list.ContextRequested += OnContextRequested;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Background = ThemeBrushes.Get("panel.background") };
        root.Children.Add(top);
        Grid.SetRow(header, 1);
        root.Children.Add(header);
        Grid.SetRow(_list, 2);
        root.Children.Add(_list);
        Content = root;
    }

    private ErrorRow? Selected => _list.SelectedItem as ErrorRow;

    /// <summary>View &gt; Tool Windows &gt; Error List: the keyboard on a row (Enter goes to it), not on the first filter button; none without rows.</summary>
    public bool FocusPreferred() => ToolWindowFocus.FocusRow(_list);

    // ---- clipboard and the row menu (Visual Studio: Copy, Go To Location, Show Error Help)

    public bool CanCopy => Selected is not null && _window is not null;

    public async Task CopyAsync()
    {
        if (Selected is { } row && _window is not null) await _window.CopyToClipboardAsync(row.CopyText).ConfigureAwait(true);
    }

    private async Task ShowHelpAsync(ErrorRow row)
    {
        if (row.HelpLink is { } link && _window is not null) await _window.OpenExternalAsync(link).ConfigureAwait(true);
    }

    /// <summary>The row menu for the selected row (built when it opens).</summary>
    internal IReadOnlyList<MenuEntry> RowMenu()
    {
        var row = Selected;
        return MenuSections.Join(
            ClipboardKeys.Entries(this),
            [
                new MenuEntry("_Go To Location", async () => { if (row is not null) await _vm.NavigateAsync(row).ConfigureAwait(true); }) { Gesture = "Enter", IsEnabled = row is { IsHeader: false } },
                new MenuEntry("Show Error _Help", async () => { if (row is not null) await ShowHelpAsync(row).ConfigureAwait(true); }) { Gesture = "F1", Icon = "question", IsEnabled = row?.HelpLink is not null },
            ],
            _menus is null ? null : MenuContributions.Resolve(_menus, Core.Contributions.MenuIds.ProblemsContext, row is null ? [] : [row.Uri]));
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // The row under the pointer becomes the selection, as in Visual Studio.
        if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: ErrorRow row }) _list.SelectedItem = row;
        if (Selected is null) return;
        e.Handled = true;
        ContextMenus.Open(RowMenu(), _list, accessibleName: "Error List menu");
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ErrorListViewModel.Rows)) _list.ItemsSource = _vm.Rows; // the model swaps in a new immutable array
    }

    private static ToggleButton Chip(string icon, string checkedPath, string countPath, string noun, string colorId)
    {
        var count = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        count.Bind(TextBlock.TextProperty, new Binding(countPath) { Converter = new FuncValueConverter<int, string>(n => CountText(n, noun)) });
        var b = new ToggleButton
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { ViewHelpers.Glyph(icon, 13, colorId), count } },
            Padding = new Thickness(8, 2), MinHeight = 22,
        };
        b.Bind(ToggleButton.IsCheckedProperty, new Binding(checkedPath) { Mode = BindingMode.TwoWay });
        return b;
    }

    /// <summary>"1 Error", "0 Errors", "2 Messages" (the chip captions, as Visual Studio words them).</summary>
    public static string CountText(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static Control RowFor(ErrorRow r)
    {
        if (r.IsHeader)
        {
            var h = ViewHelpers.Label(r.HeaderText, "foreground", 12, FontWeight.SemiBold);
            h.Margin = new Thickness(6, 0);
            return new Border { Height = 22, Background = ThemeBrushes.Get("sideBarSectionHeader.background"), Child = h };
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Height = 22, Background = Brushes.Transparent };
        var icon = ViewHelpers.Glyph(r.SeverityIcon, 14, r.SeverityColorId);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        grid.Children.Add(icon);
        void Cell(string text, int col, string? colorId = null)
        {
            var t = ViewHelpers.Label(text, colorId ?? "foreground");
            t.Margin = new Thickness(6, 0);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
        Cell(r.Code ?? "", 1, "descriptionForeground");
        Cell(r.Message, 2);
        Cell(r.FileName, 3);
        Cell(r.Line.ToString(), 4, "descriptionForeground");
        ToolTip.SetTip(grid, $"{r.Source}{(r.Code is null ? "" : " " + r.Code)}: {r.Message}\n{r.Directory}");
        return grid;
    }

    public void Dispose()
    {
        _vm.PropertyChanged -= OnVmChanged;
        _list.ItemsSource = null;
        DataContext = null;
        _vm.Dispose();
    }
}
