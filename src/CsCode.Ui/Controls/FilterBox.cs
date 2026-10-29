using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;

namespace CsCode.Ui.Controls;

/// <summary>A search box: magnifier, watermark, and a clear button that appears when there is text. Esc clears.</summary>
public sealed class FilterBox : TemplatedControl
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<FilterBox, string>(nameof(Text), "", defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<string?> WatermarkProperty = AvaloniaProperty.Register<FilterBox, string?>(nameof(Watermark));

    private readonly TextBox _box;
    private readonly Button _clear;
    private bool _syncing;

    public FilterBox()
    {
        Focusable = false;
        _box = new TextBox { Padding = new Thickness(22, 2, 22, 2) };
        var icon = new Icon(IconKey.Search, 12)
        {
            IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0), Foreground = ThemeBrushes.Get("descriptionForeground"),
        };
        _clear = new Button
        {
            Content = new Icon(IconKey.Close, 12), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0), IsVisible = false, Padding = new Thickness(2),
        };
        _clear.Classes.Add("flat");
        _clear.Click += (_, _) => { Text = ""; _box.Focus(); };
        _box.TextChanged += (_, _) => { if (!_syncing) Text = _box.Text ?? ""; };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || Text.Length == 0) return;
            Text = "";
            e.Handled = true;
        };
        Template = new Avalonia.Controls.Templates.FuncControlTemplate((_, _) => new Grid { Children = { _box, icon, _clear } });
    }

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? Watermark { get => GetValue(WatermarkProperty); set => SetValue(WatermarkProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            var t = (string?)change.NewValue ?? "";
            _clear.IsVisible = t.Length > 0;
            if (_box.Text != t)
            {
                _syncing = true;
                _box.Text = t;
                _syncing = false;
            }
        }
        else if (change.Property == WatermarkProperty) _box.PlaceholderText = (string?)change.NewValue;
    }

    public void FocusInput() => _box.Focus();
}
