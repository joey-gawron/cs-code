using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using CsCode.App.Views;
using CsCode.Core.Platform;
using CsCode.Ui;
using CsCode.Ui.Menus;
using CsCode.Ui.Theming;

namespace CsCode.App.ToolWindows.FindResults;

/// <summary>Find Results: a title line and a virtualised list grouped by file. Preview text loads lazily as rows scroll into view.</summary>
public sealed class FindResultsView : UserControl, IClipboardTarget, IToolWindowFocus
{
    private readonly FindResultsViewModel _vm;
    private readonly ListBox _list;
    private readonly IWindowService? _window;
    private readonly MenuServices? _menus;

    public FindResultsView(FindResultsViewModel vm, IWindowService? window = null, MenuServices? menus = null)
    {
        _vm = vm;
        _window = window;
        _menus = menus;
        DataContext = vm;
        var title = ViewHelpers.Label("", "descriptionForeground");
        title.Margin = new Thickness(8, 4);
        title.Bind(TextBlock.TextProperty, new Binding(nameof(FindResultsViewModel.Title)));

        var list = _list = new ListBox { ItemsSource = vm.Rows, ItemTemplate = new FuncDataTemplate<FindResultRow>((r, _) => r is null ? new Panel() : RowFor(r), supportsRecycling: false), SelectionMode = SelectionMode.Single };
        list.DoubleTapped += async (_, _) => { if (list.SelectedItem is FindResultRow r) await vm.OpenAsync(r).ConfigureAwait(true); };
        list.KeyDown += async (_, e) =>
        {
            if (ClipboardKeys.TryHandle(e, this)) return;
            if (e.Key == Key.Enter && list.SelectedItem is FindResultRow r) { e.Handled = true; await vm.OpenAsync(r).ConfigureAwait(true); }
        };
        list.ContextRequested += OnContextRequested;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Background = ThemeBrushes.Get("panel.background") };
        root.Children.Add(title);
        Grid.SetRow(list, 1);
        root.Children.Add(list);
        Content = root;
    }

    private FindResultRow? Selected => _list.SelectedItem as FindResultRow;

    /// <summary>A result as Visual Studio's Find Results copies it: <c>path(line,column): text</c>; a file header copies its path.</summary>
    internal static string CopyText(FindResultRow row)
    {
        var path = row.Location.Uri.IsFile ? row.Location.Uri.LocalPath : row.Location.Uri.ToString();
        return row.IsHeader ? path : $"{path}({row.Line},{row.Column}): {row.Preview?.Trim()}";
    }

    /// <summary>View &gt; Tool Windows &gt; Find Results: the keyboard on a result (Enter goes to it); none without results.</summary>
    public bool FocusPreferred() => ToolWindowFocus.FocusRow(_list);

    public bool CanCopy => Selected is not null && _window is not null;

    public async Task CopyAsync()
    {
        if (Selected is { } row && _window is not null) await _window.CopyToClipboardAsync(CopyText(row)).ConfigureAwait(true);
    }

    internal IReadOnlyList<MenuEntry> RowMenu()
    {
        var row = Selected;
        return MenuSections.Join(
            [
                new MenuEntry("_Go To Location", async () => { if (row is not null) await _vm.OpenAsync(row).ConfigureAwait(true); }) { Gesture = "Enter", IsEnabled = row is { IsHeader: false } },
            ],
            ClipboardKeys.Entries(this),
            [
                new MenuEntry("Copy Full _Path", async () => { if (row is not null && _window is not null) await _window.CopyToClipboardAsync(row.Location.Uri.IsFile ? row.Location.Uri.LocalPath : row.Location.Uri.ToString()).ConfigureAwait(true); }) { IsEnabled = row is not null && _window is not null },
            ],
            [
                new MenuEntry("Clear _All", () => { _vm.Clear(); return Task.CompletedTask; }) { Icon = "clear-all", IsEnabled = _vm.Rows.Count > 0 },
            ],
            _menus is null ? null : MenuContributions.Resolve(_menus, Core.Contributions.MenuIds.SearchContext, row is null ? [] : [row.Location.Uri]));
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: FindResultRow row }) _list.SelectedItem = row;
        e.Handled = true;
        ContextMenus.Open(RowMenu(), _list, accessibleName: "Find Results menu");
    }

    private Control RowFor(FindResultRow row)
    {
        if (row.IsHeader)
        {
            var h = ViewHelpers.Label(row.HeaderText ?? "", "foreground", 12, FontWeight.SemiBold);
            h.Margin = new Thickness(8, 0);
            return new Border { Height = 22, Background = ThemeBrushes.Get("sideBarSectionHeader.background"), Child = h };
        }
        var line = ViewHelpers.Label($"{row.Line}:{row.Column}", "descriptionForeground");
        line.Margin = new Thickness(24, 0, 8, 0);
        line.MinWidth = 56;
        var preview = ViewHelpers.Label("", "foreground");
        preview.TextTrimming = TextTrimming.CharacterEllipsis;
        preview.FontFamily = ThemeBrushes.CodeFont;
        preview.Bind(TextBlock.TextProperty, new Binding(nameof(FindResultRow.Preview)) { Source = row });
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Height = 22, Background = Brushes.Transparent, Children = { line, preview } };
        panel.AttachedToVisualTree += async (_, _) => await _vm.LoadPreviewAsync(row).ConfigureAwait(true);
        return panel;
    }
}
