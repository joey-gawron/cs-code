using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CsCode.App.Views;
using CsCode.Ui.Theming;

namespace CsCode.App.ToolWindows.FindResults;

/// <summary>Find Results: a title line and a virtualised list grouped by file. Preview text loads lazily as rows scroll into view.</summary>
public sealed class FindResultsView : UserControl
{
    private readonly FindResultsViewModel _vm;

    public FindResultsView(FindResultsViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        var title = ViewHelpers.Label("", "descriptionForeground");
        title.Margin = new Thickness(8, 4);
        title.Bind(TextBlock.TextProperty, new Binding(nameof(FindResultsViewModel.Title)));

        var list = new ListBox { ItemsSource = vm.Rows, ItemTemplate = new FuncDataTemplate<FindResultRow>((r, _) => RowFor(r), supportsRecycling: false), SelectionMode = SelectionMode.Single };
        list.DoubleTapped += async (_, _) => { if (list.SelectedItem is FindResultRow r) await vm.OpenAsync(r).ConfigureAwait(true); };
        list.KeyDown += async (_, e) => { if (e.Key == Key.Enter && list.SelectedItem is FindResultRow r) { e.Handled = true; await vm.OpenAsync(r).ConfigureAwait(true); } };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Background = ThemeBrushes.Get("panel.background") };
        root.Children.Add(title);
        Grid.SetRow(list, 1);
        root.Children.Add(list);
        Content = root;
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
