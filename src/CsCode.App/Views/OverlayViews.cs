using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CsCode.App.Notifications;
using CsCode.Core.Platform;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;

namespace CsCode.App.Views;

/// <summary>The in-window modal dialog (dirty-close prompts, destructive confirmations, About). Esc dismisses (null result), Enter picks the default button.</summary>
public sealed class DialogHostView : Grid
{
    private readonly DialogHostViewModel _vm;
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 16) };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
    private readonly OverlayFocus _focus;

    /// <param name="activeEditor">The active document tab (identity only), for giving the keyboard back when the prompt closes (<see cref="OverlayFocus"/>).</param>
    /// <param name="focusActiveEditor">Puts the keyboard into the active editor.</param>
    public DialogHostView(DialogHostViewModel vm, Func<object?>? activeEditor = null, Action? focusActiveEditor = null)
    {
        _vm = vm;
        _focus = new OverlayFocus(this, activeEditor, focusActiveEditor);
        IsVisible = false;
        Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
        Focusable = true;
        var card = new Border
        {
            Width = 460, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(18),
            Background = ThemeBrushes.Get("editorWidget.background"), BorderBrush = ThemeBrushes.Get("editorWidget.border"), BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 8 24 0 #88000000"),
            Child = new StackPanel { Children = { _title, _message, _buttons } },
        };
        _title.Foreground = ThemeBrushes.Get("editorWidget.foreground");
        _message.Foreground = ThemeBrushes.Get("editorWidget.foreground");
        Children.Add(card);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(DialogHostViewModel.IsVisible)) return;
            IsVisible = vm.IsVisible;
            if (vm.IsVisible)
            {
                _focus.Opened();
                Rebuild();
            }
            else _focus.Closed();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { vm.Choose(null); e.Handled = true; }
            else if (e.Key == Key.Enter && vm.Buttons.Count > 0) { vm.Choose(vm.Buttons[Math.Clamp(vm.DefaultIndex, 0, vm.Buttons.Count - 1)]); e.Handled = true; }
        };
    }

    private void Rebuild()
    {
        _title.Text = _vm.Title;
        _message.Text = _vm.Message;
        _buttons.Children.Clear();
        for (var i = 0; i < _vm.Buttons.Count; i++)
        {
            var label = _vm.Buttons[i];
            var b = new Button { Content = label, MinWidth = 84, Padding = new Thickness(14, 3), HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == _vm.DefaultIndex) b.Classes.Add("accent");
            b.Click += (_, _) => _vm.Choose(label);
            _buttons.Children.Add(b);
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() => (_buttons.Children.ElementAtOrDefault(_vm.DefaultIndex) as Control)?.Focus(), Avalonia.Threading.DispatcherPriority.Input);
    }
}

/// <summary>Toast stack (bottom right, max three) plus the notification-centre list opened from the bell.</summary>
public sealed class NotificationsView : Grid
{
    private readonly NotificationCenter _center;
    private readonly StackPanel _toasts = new() { Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 12, 30) };
    private readonly Border _centerPanel;
    private readonly StackPanel _centerItems = new() { Spacing = 1 };

    public NotificationsView(NotificationCenter center)
    {
        _center = center;
        IsHitTestVisible = true;
        Background = null;
        center.Toasts.CollectionChanged += OnToastsChanged;
        center.History.CollectionChanged += OnHistoryChanged;
        center.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NotificationCenter.IsCenterOpen)) RefreshCenter(); };

        var clear = new Button { Content = "Clear All", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 4) };
        clear.Classes.Add("flat");
        clear.Click += (_, _) => _center.ClearAll();
        _centerPanel = new Border
        {
            Width = 420, MaxHeight = 420, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 12, 30), IsVisible = false,
            Background = ThemeBrushes.Get("notifications.background"), BorderBrush = ThemeBrushes.Get("notificationCenter.border"), BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 4 16 0 #66000000"),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    new Border { Background = ThemeBrushes.Get("notificationCenterHeader.background"), Height = 28, [DockPanel.DockProperty] = Avalonia.Controls.Dock.Top,
                        Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { Pad(ViewHelpers.Label("NOTIFICATIONS", "notificationCenterHeader.foreground", 11, FontWeight.SemiBold)), clearHost(clear) } } },
                    new ScrollViewer { Content = _centerItems },
                },
            },
        };
        Children.Add(_centerPanel);
        Children.Add(_toasts);

        static Control Pad(Control c) { c.Margin = new Thickness(10, 0); return c; }
        static Control clearHost(Control c) { Grid.SetColumn(c, 1); return c; }
    }

    private void OnToastsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _toasts.Children.Clear();
        foreach (var n in _center.Toasts.Reverse()) _toasts.Children.Add(Card(n, toast: true));
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshCenter();

    private void RefreshCenter()
    {
        _centerPanel.IsVisible = _center.IsCenterOpen;
        _centerItems.Children.Clear();
        if (!_center.IsCenterOpen) return;
        if (_center.History.Count == 0) _centerItems.Children.Add(new TextBlock { Text = "No new notifications", Margin = new Thickness(12), Foreground = ThemeBrushes.Get("descriptionForeground") });
        foreach (var n in _center.History) _centerItems.Children.Add(Card(n, toast: false));
    }

    private static Control Card(NotificationItem n, bool toast)
    {
        var (icon, color) = n.Severity switch
        {
            MessageSeverity.Error => (IconKey.Error, "notificationsErrorIcon.foreground"),
            MessageSeverity.Warning => (IconKey.Warning, "notificationsWarningIcon.foreground"),
            _ => (IconKey.Info, "notificationsInfoIcon.foreground"),
        };
        var message = new TextBlock { Text = n.Message, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrushes.Get("notifications.foreground"), VerticalAlignment = VerticalAlignment.Center };
        var close = ViewHelpers.IconButton(IconKey.Close, "Dismiss", () => n.DismissCommand.Execute(null), 12);
        Grid.SetColumn(close, 2);
        var iconControl = ViewHelpers.Glyph(icon, 16, color);
        iconControl.Margin = new Thickness(0, 2, 10, 0);
        iconControl.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(message, 1);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { iconControl, message, close } };

        var stack = new StackPanel { Spacing = 8, Children = { top } };
        if (n.IsProgress)
        {
            var bar = new ProgressBar { Height = 3, MinHeight = 3, IsIndeterminate = true };
            bar.Bind(ProgressBar.ValueProperty, new Binding(nameof(NotificationItem.Progress)) { Source = n, TargetNullValue = 0.0 });
            bar.Bind(ProgressBar.IsIndeterminateProperty, new Binding(nameof(NotificationItem.Progress)) { Source = n, Converter = new Avalonia.Data.Converters.FuncValueConverter<double?, bool>(p => p is null) });
            stack.Children.Add(bar);
            if (n.CanCancel)
            {
                var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right };
                cancel.Click += (_, _) => n.CancelCommand.Execute(null);
                stack.Children.Add(cancel);
            }
        }
        if (n.HasActions)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var a in n.Actions)
            {
                var b = new Button { Content = a.Title, Padding = new Thickness(12, 2) };
                if (row.Children.Count == 0) b.Classes.Add("accent");
                var action = a;
                b.Click += (_, _) => n.InvokeCommand.Execute(action);
                row.Children.Add(b);
            }
            stack.Children.Add(row);
        }
        return new Border
        {
            Width = toast ? 380 : double.NaN, Padding = new Thickness(12, 10),
            Background = ThemeBrushes.Get("notifications.background"), BorderBrush = ThemeBrushes.Get("notifications.border"), BorderThickness = new Thickness(1),
            BoxShadow = toast ? BoxShadows.Parse("0 3 10 0 #55000000") : default,
            Child = stack,
        };
    }
}
