using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CsCode.App.QuickInput;
using CsCode.Ui.Controls;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;

namespace CsCode.App.Views;

/// <summary>
/// The quick-input overlay (command palette, quick picks, input boxes): a Command-Palette-style card under the title bar, not an OS dialog.
/// Keyboard-first: type to filter, Up/Down/PageUp/PageDown move, Enter accepts, Esc cancels, Ctrl+Space toggles a multi-pick check.
/// The list is virtualised and rows carry fuzzy-match highlight spans, an icon, a description and the keybinding label.
/// </summary>
public sealed class QuickInputView : Grid
{
    private readonly QuickInputViewModel _vm;
    private readonly TextBox _input;
    private readonly ListBox _list;
    private readonly TextBlock _title;
    private readonly TextBlock _validation;

    public QuickInputView(QuickInputViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        IsVisible = false;

        // Click-away layer.
        var backdrop = new Border { Background = Brushes.Transparent };
        backdrop.PointerPressed += (_, e) => { _vm.Cancel(); e.Handled = true; };
        Children.Add(backdrop);

        _title = new TextBlock { Margin = new Thickness(10, 6, 10, 0), FontWeight = FontWeight.SemiBold, Foreground = ThemeBrushes.Get("quickInput.foreground") };
        _title.Bind(TextBlock.TextProperty, new Binding(nameof(QuickInputViewModel.Title)));
        _title.Bind(IsVisibleProperty, new Binding(nameof(QuickInputViewModel.Title)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<string?, bool>(s => !string.IsNullOrEmpty(s)) });

        _input = new TextBox { Margin = new Thickness(8, 6), MinHeight = 24, Padding = new Thickness(6, 3), FontSize = 13 };
        _input.Bind(TextBox.TextProperty, new Binding(nameof(QuickInputViewModel.Text)) { Mode = BindingMode.TwoWay });
        _input.Bind(TextBox.PlaceholderTextProperty, new Binding(nameof(QuickInputViewModel.Placeholder)));
        _input.AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _validation = new TextBlock { Margin = new Thickness(10, 0, 10, 6), Foreground = ThemeBrushes.Get("inputValidation.errorBorder"), FontSize = 12 };
        _validation.Bind(TextBlock.TextProperty, new Binding(nameof(QuickInputViewModel.Validation)));
        _validation.Bind(IsVisibleProperty, new Binding(nameof(QuickInputViewModel.Validation)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<string?, bool>(s => !string.IsNullOrEmpty(s)) });

        _list = new ListBox { ItemsSource = vm.Items, MaxHeight = 360, ItemTemplate = new FuncDataTemplate<QuickPickEntry>((e, _) => RowFor(e), supportsRecycling: false), Focusable = false };
        _list.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedIndexProperty, new Binding(nameof(QuickInputViewModel.SelectedIndex)) { Mode = BindingMode.TwoWay });
        _list.Tapped += (_, e) =>
        {
            if (e.Source is Visual v && Ancestor<ListBoxItem>(v) is { DataContext: QuickPickEntry entry } && !entry.IsSeparator && !_vm.CanPickMany) _vm.Accept();
        };
        _list.Bind(IsVisibleProperty, new Binding(nameof(QuickInputViewModel.IsInput)) { Converter = Avalonia.Data.Converters.BoolConverters.Not });

        var card = new Border
        {
            Width = 620, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 34, 0, 0),
            Background = ThemeBrushes.Get("quickInput.background"), BorderBrush = ThemeBrushes.Get("editorWidget.border"), BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 6 18 0 #66000000"),
            Child = new StackPanel { Children = { _title, _input, _validation, _list } },
        };
        Children.Add(card);

        vm.PropertyChanged += OnVmChanged;
    }

    private static T? Ancestor<T>(Visual v) where T : class
    {
        for (var c = v; c is not null; c = Avalonia.VisualTree.VisualExtensions.GetVisualParent(c)) if (c is T t) return t;
        return null;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(QuickInputViewModel.IsVisible):
                IsVisible = _vm.IsVisible;
                if (_vm.IsVisible)
                    Dispatcher.UIThread.Post(() =>
                    {
                        _input.PasswordChar = _vm.IsPassword ? '•' : '\0';
                        _input.Focus();
                        _input.CaretIndex = _input.Text?.Length ?? 0;
                    }, DispatcherPriority.Input);
                break;
            case nameof(QuickInputViewModel.SelectedIndex) when _vm.SelectedIndex >= 0:
                _list.ScrollIntoView(_vm.SelectedIndex);
                break;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: _vm.MoveSelection(+1); e.Handled = true; break;
            case Key.Up: _vm.MoveSelection(-1); e.Handled = true; break;
            case Key.PageDown: for (var i = 0; i < 8; i++) _vm.MoveSelection(+1); e.Handled = true; break;
            case Key.PageUp: for (var i = 0; i < 8; i++) _vm.MoveSelection(-1); e.Handled = true; break;
            case Key.Enter: _vm.Accept(); e.Handled = true; break;
            case Key.Escape: _vm.Cancel(); e.Handled = true; break;
            case Key.Tab: _vm.MoveSelection(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : +1); e.Handled = true; break;
            case Key.Space when e.KeyModifiers == KeyModifiers.Control: _vm.ToggleChecked(); e.Handled = true; break;
        }
    }

    private static Control RowFor(QuickPickEntry e)
    {
        if (e.IsSeparator)
            return new Border { Height = 20, Child = ViewHelpers.Label(e.Label, "pickerGroup.foreground", 11, FontWeight.SemiBold), Padding = new Thickness(10, 3, 0, 0), IsHitTestVisible = false };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), MinHeight = 22, Margin = new Thickness(8, 0) };
        var col = 0;
        var check = new CheckBox { IsVisible = false, MinWidth = 0, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(0) };
        check.Bind(ToggleButtonChecked, new Binding(nameof(QuickPickEntry.IsChecked)) { Mode = BindingMode.TwoWay });
        grid.Children.Add(check);
        col++;
        if (e.IconKey is { } k && IconCatalog.Contains(k))
        {
            var icon = ViewHelpers.Glyph(k, 14, "icon.foreground");
            icon.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);
        }
        var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var label = new HighlightedTextBlock { SourceText = e.Label, Matches = e.LabelMatches, HighlightBrush = ThemeBrushes.Get("list.highlightForeground"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        text.Children.Add(label);
        if (e.HasDescription)
        {
            var d = ViewHelpers.Label(e.Description!, "descriptionForeground", 11);
            d.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(d);
        }
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        if (e.HasKeybinding)
        {
            var kb = new Border
            {
                Padding = new Thickness(5, 1), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
                BorderBrush = ThemeBrushes.Get("input.border"), Background = ThemeBrushes.Get("badge.background"),
                Child = ViewHelpers.Label(e.KeybindingLabel!, "badge.foreground", 10),
            };
            Grid.SetColumn(kb, 3);
            grid.Children.Add(kb);
        }
        return grid;
    }

    private static readonly AvaloniaProperty ToggleButtonChecked = Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty;
}
