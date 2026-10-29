using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;

namespace CsCode.App.Views;

/// <summary>Small helpers that keep the code-built views readable.</summary>
internal static class ViewHelpers
{
    private static readonly Dictionary<string, IBrush> HexCache = new(StringComparer.OrdinalIgnoreCase);
    public static readonly FuncValueConverter<string?, IBrush?> HexBrush = new(s =>
    {
        if (s is null || !Color.TryParse(s, out var color)) return null;
        if (!HexCache.TryGetValue(s, out var brush)) HexCache[s] = brush = new SolidColorBrush(color).ToImmutable(); // a few dozen distinct colours at most
        return brush;
    });
    public static readonly FuncValueConverter<bool, double> ZeroOrFull = new(v => v ? 1.0 : 0.0);

    public static T WithBinding<T>(this T control, AvaloniaProperty property, string path, IValueConverter? converter = null, BindingMode mode = BindingMode.OneWay) where T : AvaloniaObject
    {
        control.Bind(property, new Binding(path) { Converter = converter, Mode = mode });
        return control;
    }

    public static Icon Glyph(string kind, double size = 16, string colorId = "icon.foreground") =>
        new(kind, size) { Foreground = ThemeBrushes.Get(colorId), VerticalAlignment = VerticalAlignment.Center };

    public static TextBlock Label(string text, string colorId = "foreground", double size = 12, FontWeight? weight = null) =>
        new() { Text = text, Foreground = ThemeBrushes.Get(colorId), FontSize = size, FontWeight = weight ?? FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };

    public static Button IconButton(string kind, string tooltip, Action onClick, double size = 14)
    {
        var b = new Button { Content = Glyph(kind, size, "foreground"), Padding = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
        b.Classes.Add("flat");
        ToolTip.SetTip(b, tooltip);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Parses VS Code style <c>$(icon-name)</c> runs inside a status/label string into icons and text.</summary>
    public static Control RichText(string text, IBrush? foreground, double iconSize = 12)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf("$(", i, StringComparison.Ordinal);
            var close = open >= 0 ? text.IndexOf(')', open + 2) : -1;
            if (open < 0 || close < 0)
            {
                AddText(panel, text[i..], foreground);
                break;
            }
            if (open > i) AddText(panel, text[i..open], foreground);
            var name = text[(open + 2)..close];
            var spin = name.EndsWith("~spin", StringComparison.Ordinal);
            name = spin ? name[..^5] : name;
            panel.Children.Add(new Icon(IconCatalog.Contains(name) ? name : "circle-small-filled", iconSize) { Foreground = foreground, VerticalAlignment = VerticalAlignment.Center });
            i = close + 1;
        }
        return panel;
    }

    private static void AddText(StackPanel panel, string text, IBrush? foreground)
    {
        var t = text.Trim();
        if (t.Length == 0) return;
        panel.Children.Add(new TextBlock { Text = t, Foreground = foreground, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
    }

    public static Border Separator(string colorId = "toolbar.separator", double height = 16) =>
        new() { Width = 1, Height = height, Background = ThemeBrushes.Get(colorId), Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
}
