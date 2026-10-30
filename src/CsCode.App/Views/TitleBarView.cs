using Avalonia;
using Avalonia.Automation;
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
/// The custom title bar: app icon, menu bar, a centred Quick Launch box (opens the palette; always reads "Search (Ctrl+Q)" like Visual Studio's),
/// the workspace name beside it (Visual Studio's solution name badge) and window controls. Dragging the empty area moves the window (the platform's
/// own move, so snapping works), double-click toggles maximise. On platforms that keep native decorations (<c>showCaptionButtons = false</c>)
/// the caption buttons are omitted and <see cref="NativeCaptionButtonsInset"/> is kept clear for the platform's own.
/// <para>Narrow windows: the caption buttons are laid out first and always get their width, then the menu (clipped if even that does not fit),
/// then <see cref="TitleCenterPanel"/> shrinks the middle into what is left. Nothing draws under the window controls. They are still the last
/// children, so screen readers reach them after the menu (see <see cref="TitleBarPanel"/>), and they are no Tab stops, as native ones are not.</para>
/// </summary>
public sealed class TitleBarView : Border
{
    /// <summary>
    /// Room kept clear at the left, where macOS draws its traffic lights when the platform keeps native decorations (<c>showCaptionButtons =
    /// false</c>: the client area is extended under the native title bar, so the logo and the File menu would sit beneath them). They end about
    /// 70 px from the left under the standard title bar and about 78 px under the taller unified one; the larger is kept. Full screen hides
    /// them, and the room goes back to the menu.
    /// </summary>
    public const double NativeCaptionButtonsInset = 78;

    private readonly Window _window;
    private readonly Button _maxButton;
    private readonly Icon _maxIcon = new(IconKey.WindowMax, 10);
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _workspace = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly Border _workspaceBadge;
    private readonly Border _quickLaunch;
    private readonly StackPanel? _captionButtons;
    private readonly TitleBarPanel _row;

    public TitleBarView(Window window, MenuModel menus, ICommandService commands, bool showCaptionButtons)
    {
        _window = window;
        Height = 30;
        Background = ThemeBrushes.Get("titleBar.activeBackground");
        TextElement.SetForeground(this, ThemeBrushes.Get("titleBar.activeForeground"));

        var logo = new Border { Width = 30, Height = 30, Child = new AppLogo { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };

        if (showCaptionButtons)
        {
            _maxButton = CaptionButton(_maxIcon, "Maximize", () => _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
            _captionButtons = new StackPanel { Orientation = Orientation.Horizontal };
            _captionButtons.Children.Add(CaptionButton(new Icon(IconKey.WindowMin, 10), "Minimize", () => _window.WindowState = WindowState.Minimized));
            _captionButtons.Children.Add(_maxButton);
            _captionButtons.Children.Add(CaptionButton(new Icon(IconKey.WindowClose, 10), "Close", _window.Close, danger: true));
        }
        else _maxButton = new Button();

        var menu = new ShellMenuBar(menus) { ClipToBounds = true };
        menu.Styles.Add(new Style(x => x.OfType<MenuItem>().Class(":selected")) { Setters = { new Setter(BackgroundProperty, ThemeBrushes.Get("menubar.selectionBackground")) } });

        // Centre: the Quick Launch box, then the workspace name.
        var searchGlyph = ViewHelpers.Glyph(IconKey.Search, 12, "descriptionForeground");
        searchGlyph.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(searchGlyph, Avalonia.Controls.Dock.Left);
        var quick = _quickLaunch = new Border
        {
            Height = 22, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1), ClipToBounds = true,
            Background = ThemeBrushes.Get("commandCenter.background"), BorderBrush = ThemeBrushes.Get("commandCenter.border"), Cursor = new Cursor(StandardCursorType.Hand),
            // A DockPanel, not a StackPanel: the placeholder gets the box's width and trims ("Search (Ctr…") when the box narrows.
            Child = new DockPanel { Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center, Children = { searchGlyph, _title } },
        };
        _title.Foreground = ThemeBrushes.Get("descriptionForeground");
        _title.Text = "Search";
        quick.PointerPressed += async (_, e) =>
        {
            if (!e.GetCurrentPoint(quick).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            try { await commands.ExecuteAsync(KnownCommands.CommandPalette).ConfigureAwait(true); } catch (CommandNotFoundException) { }
        };
        ToolTip.SetTip(quick, "Search commands, files and symbols");
        _workspaceBadge = new Border
        {
            Padding = new Thickness(8, 2), CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, IsVisible = false, ClipToBounds = true,
            Background = ThemeBrushes.Get("commandCenter.background"), Child = _workspace,
        };
        _row = new TitleBarPanel(logo, menu, new TitleCenterPanel(quick, _workspaceBadge), _captionButtons);
        OnWindowStateChanged();
        window.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty) OnWindowStateChanged(); };

        Child = _row;
        PointerPressed += OnPointerPressed;
        DoubleTapped += OnDoubleTapped;
    }

    /// <summary>Placeholder of the Quick Launch box.</summary>
    public string QuickLaunchText => _title.Text ?? "";

    /// <summary>The Quick Launch box (for layout tests: it narrows, then hides, in narrow windows).</summary>
    internal Control QuickLaunchBox => _quickLaunch;

    /// <summary>The workspace name badge (for layout tests).</summary>
    internal Control WorkspaceBadge => _workspaceBadge;

    /// <summary>Minimise, maximise and close; null where the platform draws them.</summary>
    internal Control? CaptionButtons => _captionButtons;

    /// <summary>The workspace name shown beside the Quick Launch box (null when no folder is open).</summary>
    public string? WorkspaceName => _workspaceBadge.IsVisible ? _workspace.Text : null;

    /// <summary>Sets the Quick Launch placeholder from the palette's shortcut (see <see cref="PlaceholderFor"/>).</summary>
    public void SetShortcut(IKeybindingService? bindings) => _title.Text = PlaceholderFor(bindings);

    public void SetWorkspaceName(string? name)
    {
        _workspace.Text = name;
        _workspaceBadge.IsVisible = !string.IsNullOrEmpty(name);
    }

    /// <summary>
    /// "Search (Ctrl+Q)": Visual Studio's wording with the palette's real shortcut, preferring Ctrl+Q (the Visual Studio keymap's Quick Launch key;
    /// ⇧⌘A on macOS).
    /// </summary>
    public static string PlaceholderFor(IKeybindingService? bindings) => PlaceholderFor(bindings, OperatingSystem.IsMacOS());

    internal static string PlaceholderFor(IKeybindingService? bindings, bool mac)
    {
        if (bindings is null) return "Search";
        var quickLaunchKey = mac ? new KeyStroke("a", Shift: true, Meta: true) : new KeyStroke("q", Ctrl: true);
        var quickLaunch = bindings.GetBindingsFor(KnownCommands.CommandPalette).Any(b => !b.IsNegative && b.Chord is [var s] && s == quickLaunchKey);
        var label = quickLaunch ? (mac ? "⇧⌘A" : "Ctrl+Q") : bindings.GetLabel(KnownCommands.CommandPalette);
        return label is null ? "Search" : $"Search ({label})";
    }

    /// <summary>
    /// The restore or maximise glyph and name on our own maximise button; with native decorations, the traffic lights' room, which full screen gives
    /// back (macOS hides them there).
    /// </summary>
    private void OnWindowStateChanged()
    {
        if (_captionButtons is not null)
        {
            var maximized = _window.WindowState == WindowState.Maximized;
            _maxIcon.Kind = maximized ? IconKey.WindowRestore : IconKey.WindowMax;
            NameCaptionButton(_maxButton, maximized ? "Restore Down" : "Maximize");
        }
        else _row.LeftInset = _window.WindowState == WindowState.FullScreen ? 0 : NativeCaptionButtonsInset;
    }

    /// <summary>
    /// A window control. Not focusable, like the native ones: Tab and Shift+Tab never stop on it, a click leaves the keyboard where it was (in
    /// the editor, not on the button), and the window manager's keys (Alt+F4, Super+arrows) are the keyboard's way to the window's state. Named
    /// as Windows names its own (tooltip and screen-reader name): an icon alone gives a screen reader nothing to read.
    /// </summary>
    private static Button CaptionButton(Icon icon, string name, Action onClick, bool danger = false)
    {
        icon.Foreground = ThemeBrushes.Get("titleBar.activeForeground");
        var b = new Button { Content = icon, Width = 46, Height = 30, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, MinHeight = 0, MinWidth = 0, Focusable = false };
        b.Classes.Add(danger ? "captionclose" : "captionbutton");
        b.Click += (_, _) => onClick();
        NameCaptionButton(b, name);
        return b;
    }

    private static void NameCaptionButton(Button button, string name)
    {
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
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

/// <summary>
/// The title bar's row: logo, menu, middle (<see cref="TitleCenterPanel"/>) and caption buttons, left to right and as children in that order,
/// which is also the Tab order and the order screen readers (Narrator, AT-SPI) read them in. The room goes by priority instead: the caption
/// buttons are measured first and always get their width, then the logo, then the menu (clipped if even that does not fit), and the middle
/// gets what is left. A DockPanel measures its children in child order, so docking the buttons first for their width put minimise, maximise
/// and close before the File menu for Tab and for screen readers.
/// </summary>
internal sealed class TitleBarPanel : Panel
{
    private readonly Control _logo;
    private readonly Control _menu;
    private readonly Control _middle;
    private readonly Control? _buttons;
    private double _leftInset;

    public TitleBarPanel(Control logo, Control menu, Control middle, Control? buttons)
    {
        _logo = logo;
        _menu = menu;
        _middle = middle;
        _buttons = buttons;
        Children.Add(logo);
        Children.Add(menu);
        Children.Add(middle);
        if (buttons is not null) Children.Add(buttons);
    }

    /// <summary>Room kept empty left of the logo, for window controls the platform draws there (<see cref="TitleBarView.NativeCaptionButtonsInset"/>).</summary>
    public double LeftInset
    {
        get => _leftInset;
        set
        {
            if (_leftInset == value) return;
            _leftInset = value;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = availableSize.Height;
        var buttons = 0.0;
        if (_buttons is not null)
        {
            _buttons.Measure(new Size(availableSize.Width, height));
            buttons = _buttons.DesiredSize.Width;
        }
        var room = Math.Max(0, availableSize.Width - buttons - _leftInset);
        _logo.Measure(new Size(room, height));
        room = Math.Max(0, room - _logo.DesiredSize.Width);
        _menu.Measure(new Size(room, height));
        room = Math.Max(0, room - _menu.DesiredSize.Width);
        _middle.Measure(new Size(room, height));
        var tallest = Math.Max(Math.Max(_logo.DesiredSize.Height, _menu.DesiredSize.Height), Math.Max(_middle.DesiredSize.Height, _buttons?.DesiredSize.Height ?? 0));
        return new Size(_leftInset + _logo.DesiredSize.Width + _menu.DesiredSize.Width + _middle.DesiredSize.Width + buttons, tallest);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var height = finalSize.Height;
        var buttons = Math.Min(_buttons?.DesiredSize.Width ?? 0, finalSize.Width);
        var right = finalSize.Width - buttons;
        _buttons?.Arrange(new Rect(right, 0, buttons, height));
        var x = ArrangeLeft(_logo, Math.Min(_leftInset, right));
        x = ArrangeLeft(_menu, x);
        _middle.Arrange(new Rect(x, 0, right - x, height));
        return finalSize;

        // Left to right from the inset, each child at its desired width or what is left before the caption buttons.
        double ArrangeLeft(Control child, double left)
        {
            var width = Math.Min(child.DesiredSize.Width, right - left);
            child.Arrange(new Rect(left, 0, width, height));
            return left + width;
        }
    }
}

/// <summary>
/// The middle of the title bar: the Quick Launch box and the workspace badge, centred in the width the menu and the caption buttons leave.
/// Short of room, the box narrows first (from <see cref="QuickLaunchWidth"/> down to <see cref="MinQuickLaunchWidth"/>), then the badge
/// goes, then the box: a hidden child is arranged empty and both clip, so neither can draw outside this panel (a fixed-width box used to run
/// under minimise and maximise, and the badge past the close button, in a half-screen window at 170 % scaling).
/// </summary>
internal sealed class TitleCenterPanel : Panel
{
    public const double QuickLaunchWidth = 360, MinQuickLaunchWidth = 120, Spacing = 10;

    private readonly Control _quick;
    private readonly Control _badge;

    public TitleCenterPanel(Control quick, Control badge)
    {
        _quick = quick;
        _badge = badge;
        Children.Add(quick);
        Children.Add(badge);
        ClipToBounds = true;
    }

    /// <summary>The widths the box and the badge get in <paramref name="available"/> (0 = hidden). The badge must have been measured.</summary>
    internal (double Quick, double Badge) WidthsFor(double available)
    {
        var badge = _badge.IsVisible ? _badge.DesiredSize.Width : 0;
        if (badge > 0 && available >= MinQuickLaunchWidth + Spacing + badge) return (Math.Min(QuickLaunchWidth, available - Spacing - badge), badge);
        if (available >= MinQuickLaunchWidth) return (Math.Min(QuickLaunchWidth, available), 0);
        return (0, 0);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _badge.Measure(Size.Infinity);
        var (quick, badge) = WidthsFor(availableSize.Width);
        _quick.Measure(new Size(quick, availableSize.Height));
        return new Size(quick + (badge > 0 ? Spacing + badge : 0), Math.Max(_quick.DesiredSize.Height, _badge.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (quick, badge) = WidthsFor(finalSize.Width);
        var x = Math.Max(0, (finalSize.Width - quick - (badge > 0 ? Spacing + badge : 0)) / 2);
        var qh = _quick.DesiredSize.Height;
        _quick.Arrange(quick > 0 ? new Rect(x, (finalSize.Height - qh) / 2, quick, qh) : default);
        var bh = _badge.DesiredSize.Height;
        _badge.Arrange(badge > 0 ? new Rect(x + quick + Spacing, (finalSize.Height - bh) / 2, badge, bh) : default);
        return finalSize;
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
