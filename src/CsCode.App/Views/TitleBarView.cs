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
/// The custom title bar: app icon, menu bar, a centred Quick Launch box (opens the palette; always reads "Search (Ctrl+Q)" like Visual Studio's),
/// the workspace name beside it (Visual Studio's solution name badge) and window controls. Dragging the empty area moves the window (the platform's
/// own move, so snapping works), double-click toggles maximise. On platforms that keep native decorations (<c>showCaptionButtons = false</c>)
/// the caption buttons are omitted.
/// <para>Narrow windows: the caption buttons are laid out first and always get their width, then the menu (clipped if even that does not fit),
/// then <see cref="TitleCenterPanel"/> shrinks the middle into what is left. Nothing draws under the window controls.</para>
/// </summary>
public sealed class TitleBarView : Border
{
    private readonly Window _window;
    private readonly Button _maxButton;
    private readonly Icon _maxIcon = new(IconKey.WindowMax, 10);
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _workspace = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly Border _workspaceBadge;
    private readonly Border _quickLaunch;
    private readonly StackPanel? _captionButtons;

    public TitleBarView(Window window, MenuModel menus, ICommandService commands, bool showCaptionButtons)
    {
        _window = window;
        Height = 30;
        Background = ThemeBrushes.Get("titleBar.activeBackground");
        TextElement.SetForeground(this, ThemeBrushes.Get("titleBar.activeForeground"));

        // Docked in priority order (a DockPanel measures its children in order, each in the room the earlier ones left): logo, caption
        // buttons, menu, and the middle fills the rest.
        var dock = new DockPanel();

        var logo = new Border { Width = 30, Height = 30, Child = new AppLogo { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        DockPanel.SetDock(logo, Avalonia.Controls.Dock.Left);
        dock.Children.Add(logo);

        if (showCaptionButtons)
        {
            _maxButton = CaptionButton(_maxIcon, () => _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
            _captionButtons = new StackPanel { Orientation = Orientation.Horizontal };
            _captionButtons.Children.Add(CaptionButton(new Icon(IconKey.WindowMin, 10), () => _window.WindowState = WindowState.Minimized));
            _captionButtons.Children.Add(_maxButton);
            _captionButtons.Children.Add(CaptionButton(new Icon(IconKey.WindowClose, 10), _window.Close, danger: true));
            DockPanel.SetDock(_captionButtons, Avalonia.Controls.Dock.Right);
            dock.Children.Add(_captionButtons);
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty) _maxIcon.Kind = _window.WindowState == WindowState.Maximized ? IconKey.WindowRestore : IconKey.WindowMax;
            };
        }
        else _maxButton = new Button();

        var menu = new ShellMenuBar(menus) { ClipToBounds = true };
        menu.Styles.Add(new Style(x => x.OfType<MenuItem>().Class(":selected")) { Setters = { new Setter(BackgroundProperty, ThemeBrushes.Get("menubar.selectionBackground")) } });
        DockPanel.SetDock(menu, Avalonia.Controls.Dock.Left);
        dock.Children.Add(menu);

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
        dock.Children.Add(new TitleCenterPanel(quick, _workspaceBadge));

        Child = dock;
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

    /// <summary>"Search (Ctrl+Q)": Visual Studio's wording with the palette's real shortcut, preferring Ctrl+Q (the Visual Studio keymap's Quick Launch key).</summary>
    public static string PlaceholderFor(IKeybindingService? bindings)
    {
        if (bindings is null) return "Search";
        var quickLaunch = bindings.GetBindingsFor(KnownCommands.CommandPalette)
            .Any(b => !b.IsNegative && b.Chord is [{ Key: "q", Ctrl: true, Shift: false, Alt: false, Meta: false }]);
        var label = quickLaunch ? "Ctrl+Q" : bindings.GetLabel(KnownCommands.CommandPalette);
        return label is null ? "Search" : $"Search ({label})";
    }

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
