using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CsCode.App.Menus;
using CsCode.Core.Platform;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;

namespace CsCode.App.Views;

/// <summary>
/// The custom title bar: app icon, menu bar, a centred Quick Launch box (opens the palette) and window controls. Dragging the empty area
/// moves the window (the platform's own move, so snapping works), double-click toggles maximise. On platforms that keep native
/// decorations (<c>showCaptionButtons = false</c>) the caption buttons are omitted.
/// </summary>
public sealed class TitleBarView : Border
{
    private readonly Window _window;
    private readonly Button _maxButton;
    private readonly Icon _maxIcon = new(IconKey.WindowMax, 10);
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };

    public TitleBarView(Window window, MenuModel menus, ICommandService commands, bool showCaptionButtons)
    {
        _window = window;
        Height = 30;
        Background = ThemeBrushes.Get("titleBar.activeBackground");
        TextElement.SetForeground(this, ThemeBrushes.Get("titleBar.activeForeground"));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };

        var logo = new Border { Width = 30, Height = 30, Child = new Icon("symbol-class", 16) { Foreground = ThemeBrushes.Get("focusBorder"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        grid.Children.Add(logo);

        var menu = new ShellMenuBar(menus);
        menu.Styles.Add(new Style(x => x.OfType<MenuItem>().Class(":selected")) { Setters = { new Setter(BackgroundProperty, ThemeBrushes.Get("menubar.selectionBackground")) } });
        Grid.SetColumn(menu, 1);
        grid.Children.Add(menu);

        // Centre: Quick Launch box + window title (workspace name, dirty marker).
        var center = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var quick = new Border
        {
            Width = 360, Height = 22, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1),
            Background = ThemeBrushes.Get("commandCenter.background"), BorderBrush = ThemeBrushes.Get("commandCenter.border"), Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center,
                Children = { ViewHelpers.Glyph(IconKey.Search, 12, "descriptionForeground"), _title },
            },
        };
        _title.Foreground = ThemeBrushes.Get("descriptionForeground");
        _title.Text = "Quick Launch (Ctrl+Q)";
        quick.PointerPressed += async (_, e) =>
        {
            if (!e.GetCurrentPoint(quick).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            try { await commands.ExecuteAsync(KnownCommands.CommandPalette).ConfigureAwait(true); } catch (CommandNotFoundException) { }
        };
        ToolTip.SetTip(quick, "Quick Launch (Ctrl+Q)");
        center.Children.Add(quick);
        Grid.SetColumn(center, 2);
        grid.Children.Add(center);

        if (showCaptionButtons)
        {
            _maxButton = CaptionButton(_maxIcon, () => _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(CaptionButton(new Icon(IconKey.WindowMin, 10), () => _window.WindowState = WindowState.Minimized));
            buttons.Children.Add(_maxButton);
            buttons.Children.Add(CaptionButton(new Icon(IconKey.WindowClose, 10), _window.Close, danger: true));
            Grid.SetColumn(buttons, 3);
            grid.Children.Add(buttons);
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty) _maxIcon.Kind = _window.WindowState == WindowState.Maximized ? IconKey.WindowRestore : IconKey.WindowMax;
            };
        }
        else _maxButton = new Button();

        Child = grid;
        PointerPressed += OnPointerPressed;
        DoubleTapped += OnDoubleTapped;
    }

    /// <summary>Sets the text of the Quick Launch box (the workspace name / active file title).</summary>
    public void SetTitle(string text) => _title.Text = text;

    private Button CaptionButton(Icon icon, Action onClick, bool danger = false)
    {
        icon.Foreground = ThemeBrushes.Get("titleBar.activeForeground");
        var b = new Button { Content = icon, Width = 46, Height = 30, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, MinHeight = 0, MinWidth = 0 };
        b.Classes.Add(danger ? "captionclose" : "captionbutton");
        b.Click += (_, _) => onClick();
        return b;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.ClickCount > 1) return;
        _window.BeginMoveDrag(e);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>() is not null) return;
        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}

internal static class VisualExtensions
{
    public static T? FindAncestorOfType<T>(this Visual visual) where T : class
    {
        for (var v = visual; v is not null; v = v.GetVisualParent()) if (v is T t) return t;
        return null;
    }

    private static Visual? GetVisualParent(this Visual v) => Avalonia.VisualTree.VisualExtensions.GetVisualParent(v);
}
