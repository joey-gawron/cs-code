using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Recycling;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CsCode.App.Commands;
using CsCode.App.Docking;
using CsCode.App.Input;
using CsCode.App.Notifications;
using CsCode.App.QuickInput;
using CsCode.App.Views;
using CsCode.Core.Debugging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Ui.Theming;
using Dock.Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace CsCode.App;

/// <summary>
/// The Visual Studio 2022 style main window: custom title bar with menus and Quick Launch, standard toolbar, Dock-based workbench (document well and
/// tool windows), status bar, and overlay layers (quick input, modal dialog, notifications, MRU switcher). Everything dynamic is rendered from
/// view-models owned by <see cref="ShellController"/>; this class only composes views and routes keys and focus.
/// </summary>
public sealed class MainWindow : Window, IWindowChrome
{
    private readonly IServiceProvider _sp;
    private readonly ShellController _shell;
    private readonly DisposableStore _disposables = new();
    private readonly StatusBarView _status;
    private readonly ToolbarView _toolbar;
    private readonly TitleBarView _titleBar;
    private readonly Border _switcher = new();
    private readonly StackPanel _switcherItems = new() { Spacing = 1 };
    private bool _closeConfirmed, _zen, _switcherActive;
    private int _switcherIndex;
    private WindowState _stateBeforeFullScreen;
    private IReadOnlyList<ShellDocument> _switcherList = [];

    /// <summary>For the XAML previewer only; the app uses the provider overload.</summary>
    public MainWindow() : this(AppServices.Build([])) { }

    public MainWindow(IServiceProvider sp)
    {
        _sp = sp;
        Title = "CsCode";
        Width = 1280;
        Height = 800;
        MinWidth = 640;
        MinHeight = 400;
        var useCustomChrome = !OperatingSystem.IsMacOS();
        if (useCustomChrome) WindowDecorations = WindowDecorations.None; else { ExtendClientAreaToDecorationsHint = true; }
        _sp.GetRequiredService<WindowHolder>().Window = this;

        var ui = _sp.GetRequiredService<Ui.Threading.IUiDispatcher>();
        _shell = _sp.GetRequiredService<ShellController>();
        _shell.Initialize(this);

        var commands = _sp.GetRequiredService<ICommandService>();
        var keys = _sp.GetRequiredService<IContextKeyService>();
        _titleBar = new TitleBarView(this, _shell.Menus, commands, showCaptionButtons: useCustomChrome);
        _toolbar = new ToolbarView(commands, keys, ui, _sp.GetService<IKeybindingService>(), _sp.GetService<IDebugService>());
        _status = new StatusBarView(_sp.GetRequiredService<IStatusBarService>(), commands, keys, ui);

        var dock = BuildDock();

        var rows = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        rows.Children.Add(_titleBar);
        Grid.SetRow(_toolbar, 1);
        rows.Children.Add(_toolbar);
        Grid.SetRow(dock, 2);
        rows.Children.Add(dock);
        Grid.SetRow(_status, 3);
        rows.Children.Add(_status);

        var overlays = new Grid { IsHitTestVisible = true };
        overlays.Children.Add(new NotificationsView(_sp.GetRequiredService<NotificationCenter>()) { IsHitTestVisible = true });
        BuildSwitcher();
        overlays.Children.Add(_switcher);
        var quick = new QuickInputView(_sp.GetRequiredService<QuickInputViewModel>()) { Margin = new Thickness(0, 30, 0, 0) };
        overlays.Children.Add(quick);
        overlays.Children.Add(new DialogHostView(_sp.GetRequiredService<DialogHostViewModel>()));

        var root = new Grid();
        root.Children.Add(rows);
        root.Children.Add(overlays);
        AddResizeGrips(root, useCustomChrome);
        Content = root;

        // An empty tool well (e.g. nothing docked left) takes no space, like Visual Studio.
        Styles.Add(new Style(x => x.OfType<ToolDockControl>()) { Setters = { new Setter(IsVisibleProperty, new Avalonia.Data.Binding("!IsEmpty")) } });
        Styles.Add(new Style(x => x.OfType<DocumentTabStripItem>()) { Setters = { new Setter(FontStyleProperty, new Avalonia.Data.Binding("PreviewFontStyle")) } });
        // VS: the active document tab carries a coloured accent bar on its TOP edge.
        Styles.Add(new Style(x => x.OfType<DocumentTabStripItem>().Class(":selected"))
        {
            Setters = { new Setter(BorderBrushProperty, ThemeBrushes.Get("tab.activeBorderTop")), new Setter(BorderThicknessProperty, new Thickness(0, 2, 0, 0)) },
        });

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
        AddHandler(GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble | RoutingStrategies.Tunnel);
        Closing += OnClosing;
        Opened += OnOpened;

        var well = _shell.Well;
        _disposables.Add(well.ActiveChanged.Subscribe(_ => UpdateTitle()));
        _disposables.Add(well.TabsChanged.Subscribe(_ => UpdateTitle()));
        var workspace = _sp.GetService<IWorkspaceService>();
        if (workspace is not null) _disposables.Add(workspace.FoldersChanged.Subscribe(_ => ui.Post(UpdateTitle)));
        UpdateTitle();
        var bindings = _sp.GetService<IKeybindingService>();
        _titleBar.SetShortcut(bindings);
        if (bindings is not null) _disposables.Add(bindings.Changed.Subscribe(_ => ui.PostCoalesced("titlebar.shortcut", () => _titleBar.SetShortcut(bindings))));
    }

    // ------------------------------------------------------------------ dock host

    private DockControl BuildDock()
    {
        var factory = _shell.Factory;
        var layout = factory.EnsureLayout();
        var dock = new DockControl { Layout = layout, Factory = factory, InitializeLayout = false, InitializeFactory = false };
        dock.DataTemplates.Add(new FuncDataTemplate<EditorTab>((t, _) => t is null ? new Panel() : Mark(t.EnsureHost(), FocusArea.Editor)));
        dock.DataTemplates.Add(new FuncDataTemplate<ShellDocument>((t, _) => t is null ? new Panel() : Mark(t.EnsureHost(), FocusArea.None)));
        dock.DataTemplates.Add(new FuncDataTemplate<ToolTab>((t, _) => t is null ? new Panel() : Mark(t.EnsureHost(), t.Spec.Id is "explorer" ? FocusArea.Explorer : FocusArea.Panel)));
        // Dock's control recycling caches the content of every dockable it presented, keyed by the dockable, and never evicts: a closed tab's
        // host stayed reachable for the whole session and, through the host's composition visual (no longer updated once the host left the
        // tree), so did the editor view and document it had held. A closed document tab is never presented again (reopening creates a new tab),
        // so its entry goes when it closes. Tool windows keep theirs: they are hidden, not closed, and their host is reused on the next show.
        void EvictClosed(object? sender, Dock.Model.Core.Events.DockableClosedEventArgs e)
        {
            if (e.Dockable is ShellDocument && ControlRecyclingDataTemplate.GetControlRecycling(dock) is ControlRecycling recycling) recycling.Remove(e.Dockable);
        }
        factory.DockableClosed += EvictClosed;
        _disposables.Add(Disposable.Create(() => factory.DockableClosed -= EvictClosed));
        // Tool windows exist in the model before they are shown: the manager adds them to their docks, then the layout is restored.
        _ = _shell.Tools;
        return dock;
    }

    private static Control Mark(Control view, FocusArea area)
    {
        if (area != FocusArea.None) ShellFocus.SetArea(view, area);
        return view;
    }

    // ------------------------------------------------------------------ startup

    private void OnOpened(object? sender, EventArgs e)
    {
        Icon = AppIcon.Create();
        // Everything that does I/O or reads state runs after the first frame so the window appears immediately.
        Dispatcher.UIThread.Post(async () =>
        {
            try { await _shell.StartDeferredAsync().ConfigureAwait(true); }
            catch (Exception ex) { _sp.GetService<Startup.CrashHandler>()?.Report(ex, fatal: false, source: "Startup"); }
        }, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ keyboard and focus

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+Tab / Ctrl+Shift+Tab: MRU editor switcher overlay (committed when Ctrl is released).
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            StepSwitcher(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : +1);
            e.Handled = true;
            return;
        }
        if (_switcherActive && e.Key == Key.Escape) { HideSwitcher(commit: false); e.Handled = true; return; }
        if (!AvaloniaKeyMapper.TryMap(e.Key, e.KeyModifiers, out var stroke)) return;
        if (_shell.Keyboard.Handle(stroke) == KeyDispatch.Handled) e.Handled = true;
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (_switcherActive && e.Key is Key.LeftCtrl or Key.RightCtrl) HideSwitcher(commit: true);
    }

    private void OnGotFocus(object? sender, RoutedEventArgs e)
    {
        var area = ShellFocus.Classify(e.Source as Visual);
        if (area != _shell.ContextKeys.Focus) _shell.ContextKeys.SetFocus(area);
    }

    // ------------------------------------------------------------------ MRU switcher

    private void BuildSwitcher()
    {
        _switcher.IsVisible = false;
        _switcher.HorizontalAlignment = HorizontalAlignment.Center;
        _switcher.VerticalAlignment = VerticalAlignment.Center;
        _switcher.MinWidth = 300;
        _switcher.Padding = new Thickness(6);
        _switcher.Background = ThemeBrushes.Get("editorWidget.background");
        _switcher.BorderBrush = ThemeBrushes.Get("editorWidget.border");
        _switcher.BorderThickness = new Thickness(1);
        _switcher.BoxShadow = BoxShadows.Parse("0 6 18 0 #66000000");
        _switcher.Child = _switcherItems;
    }

    private void StepSwitcher(int delta)
    {
        var mru = _shell.Well.MruOrder;
        if (mru.Count < 2) return;
        if (!_switcherActive)
        {
            _switcherActive = true;
            _switcherList = mru.Take(12).ToArray();
            _switcherIndex = 0;
        }
        _switcherIndex = ((_switcherIndex + delta) % _switcherList.Count + _switcherList.Count) % _switcherList.Count;
        _switcherItems.Children.Clear();
        for (var i = 0; i < _switcherList.Count; i++)
        {
            var row = new Border
            {
                Padding = new Thickness(10, 4), Background = i == _switcherIndex ? ThemeBrushes.Get("list.activeSelectionBackground") : Brushes.Transparent,
                Child = ViewHelpers.Label(_switcherList[i].Title, i == _switcherIndex ? "list.activeSelectionForeground" : "foreground", 12),
            };
            _switcherItems.Children.Add(row);
        }
        _switcher.IsVisible = true;
    }

    private void HideSwitcher(bool commit)
    {
        _switcher.IsVisible = false;
        _switcherActive = false;
        if (commit && _switcherIndex >= 0 && _switcherIndex < _switcherList.Count && _shell.Well.Tabs.Contains(_switcherList[_switcherIndex]))
            _shell.Well.Activate(_switcherList[_switcherIndex]);
        _switcherList = [];
    }

    // ------------------------------------------------------------------ title

    private void UpdateTitle()
    {
        var well = _shell.Well;
        var name = _sp.GetService<IWorkspaceService>()?.Name;
        var active = well.ActiveEditor;
        var dirty = well.EditorTabs.Any(t => t.IsDirty) ? "● " : "";
        var file = active?.Title is { } t ? t + " - " : "";
        var title = $"{dirty}{file}{(name is null ? "" : name + " - ")}CsCode";
        Title = title;
        _titleBar.SetWorkspaceName(name);
    }

    // ------------------------------------------------------------------ closing

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed) return;
        e.Cancel = true;
        try
        {
            _shell.SaveLayoutNow(); // before tabs close, so the next start restores them
            if (await _shell.Well.CloseAllAsync().ConfigureAwait(true))
            {
                _closeConfirmed = true;
                Close();
            }
            else _shell.Layout.Save(); // the user cancelled a Save prompt: keep the layout as it is
        }
        catch (Exception ex)
        {
            _sp.GetService<Startup.CrashHandler>()?.Report(ex, fatal: false, source: "Closing");
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>Closes without any save prompts (tests and crash recovery).</summary>
    public void CloseWithoutPrompt()
    {
        _closeConfirmed = true;
        Close();
    }

    public async Task<bool> RequestExitAsync()
    {
        _shell.SaveLayoutNow();
        if (!await _shell.Well.CloseAllAsync().ConfigureAwait(true)) return false;
        _closeConfirmed = true;
        Close();
        return true;
    }

    // ------------------------------------------------------------------ window modes

    public void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen) WindowState = _stateBeforeFullScreen;
        else { _stateBeforeFullScreen = WindowState; WindowState = WindowState.FullScreen; }
    }

    public void ToggleZenMode()
    {
        _zen = !_zen;
        _toolbar.IsVisible = !_zen;
        _status.IsVisible = !_zen;
        if (_zen && WindowState != WindowState.FullScreen) ToggleFullScreen();
        else if (!_zen && WindowState == WindowState.FullScreen) ToggleFullScreen();
        if (_zen) foreach (var t in _shell.Tools.Tools.ToArray()) _shell.Tools.Hide(t.Spec.Id);
    }

    // ------------------------------------------------------------------ resizing a borderless window

    private void AddResizeGrips(Grid root, bool enabled)
    {
        if (!enabled) return;
        void Grip(WindowEdge edge, StandardCursorType cursor, HorizontalAlignment h, VerticalAlignment v, double w, double ht)
        {
            var g = new Border { Background = Brushes.Transparent, HorizontalAlignment = h, VerticalAlignment = v, Cursor = new Cursor(cursor) };
            if (!double.IsNaN(w)) g.Width = w;
            if (!double.IsNaN(ht)) g.Height = ht;
            g.PointerPressed += (_, e) => { if (WindowState == WindowState.Normal && e.GetCurrentPoint(g).Properties.IsLeftButtonPressed) BeginResizeDrag(edge, e); };
            root.Children.Add(g);
        }
        const double t = 4, c = 10;
        Grip(WindowEdge.North, StandardCursorType.SizeNorthSouth, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, t);
        Grip(WindowEdge.South, StandardCursorType.SizeNorthSouth, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, t);
        Grip(WindowEdge.West, StandardCursorType.SizeWestEast, HorizontalAlignment.Left, VerticalAlignment.Stretch, t, double.NaN);
        Grip(WindowEdge.East, StandardCursorType.SizeWestEast, HorizontalAlignment.Right, VerticalAlignment.Stretch, t, double.NaN);
        Grip(WindowEdge.NorthWest, StandardCursorType.TopLeftCorner, HorizontalAlignment.Left, VerticalAlignment.Top, c, c);
        Grip(WindowEdge.NorthEast, StandardCursorType.TopRightCorner, HorizontalAlignment.Right, VerticalAlignment.Top, c, c);
        Grip(WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner, HorizontalAlignment.Left, VerticalAlignment.Bottom, c, c);
        Grip(WindowEdge.SouthEast, StandardCursorType.BottomRightCorner, HorizontalAlignment.Right, VerticalAlignment.Bottom, c, c);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _disposables.Dispose();
        _status.Dispose();
        _toolbar.Dispose();
        _shell.Dispose();
    }
}
