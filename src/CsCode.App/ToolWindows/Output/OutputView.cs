using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CsCode.App.Views;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;

namespace CsCode.App.ToolWindows.Output;

/// <summary>The Output panel: channel dropdown, clear, and a virtualised monospace line list that follows the tail while the user is at the bottom.</summary>
public sealed class OutputView : UserControl, IDisposable
{
    private readonly OutputViewModel _vm;
    private readonly ListBox _list;
    private bool _scrollQueued;

    public OutputView(OutputViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        var combo = new ComboBox { MinWidth = 200, Height = 22, MinHeight = 0, Padding = new Thickness(6, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        combo.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(OutputViewModel.Channels)));
        combo.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, new Binding(nameof(OutputViewModel.SelectedChannel)) { Mode = BindingMode.TwoWay });

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 4) };
        bar.Children.Add(ViewHelpers.Label("Show output from:", "descriptionForeground"));
        bar.Children.Add(combo);
        bar.Children.Add(ViewHelpers.IconButton("clear-all", "Clear Output", vm.Clear));

        _list = new ListBox { ItemsSource = vm.Lines, ItemTemplate = new FuncDataTemplate<string>((s, _) => s is null ? new Panel() : Line(s), supportsRecycling: true) };
        vm.Lines.CollectionChanged += OnLinesChanged;

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Background = ThemeBrushes.Get("panel.background") };
        root.Children.Add(bar);
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);
        Content = root;
    }

    private static Control Line(string? text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(8, 0), TextWrapping = TextWrapping.NoWrap };
        t.FontFamily = ThemeBrushes.CodeFont;
        return t;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_vm.AutoScroll || _scrollQueued || _vm.Lines.Count == 0) return;
        _scrollQueued = true; // one scroll per UI turn, however many appends arrived
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (_vm.Lines.Count > 0) _list.ScrollIntoView(_vm.Lines[^1]);
        }, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _vm.Lines.CollectionChanged -= OnLinesChanged;
        _list.ItemsSource = null;
        DataContext = null;
        _vm.Dispose();
    }
}
