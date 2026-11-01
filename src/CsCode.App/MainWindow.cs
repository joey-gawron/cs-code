using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Recycling;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private readonly ScrollViewer _switcherScroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    // The overlays that take the keyboard (MRU switcher, quick input, modal prompt) are shown in the window that has it: this one's overlay grid, or
    // a floating window's (as an adorner over its dock; an owned floating window stays above this one and would hide them). Toasts stay here.
    private readonly Grid _overlays = new() { IsHitTestVisible = true };
    private readonly KeyboardOverlayLayer _keyboardOverlays = new();
    private readonly QuickInputView _quick;
    private Control? _overlaysAdorning; // the floating window's dock the keyboard overlays adorn (null: they are in this window)
    private TopLevel? _overlayHost; // that floating window
    private bool _closeConfirmed, _zen, _switcherActive, _windowClosed;
    private string? _sessionName;
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
        // macOS: the client area runs under the native title bar, as tall as ours, so the traffic lights sit centred in it.
        if (useCustomChrome) WindowDecorations = WindowDecorations.None; else { ExtendClientAreaToDecorationsHint = true; ExtendClientAreaTitleBarHeightHint = 30; }
        _sp.GetRequiredService<WindowHolder>().Window = this;

        var ui = _sp.GetRequiredService<Ui.Threading.IUiDispatcher>();
        _shell = _sp.GetRequiredService<ShellController>();
        _shell.Initialize(this);

        var commands = _sp.GetRequiredService<ICommandService>();
        var keys = _sp.GetRequiredService<IContextKeyService>();
        _titleBar = new TitleBarView(this, _shell.Menus, commands, showCaptionButtons: useCustomChrome, inWindowMenu: !OperatingSystem.IsMacOS());
        // macOS: the application menu (About, Settings...) runs this window's commands; each window's menu bar comes with its shell input.
        if (App.ApplicationMenu is { } appMenu) _disposables.Add(appMenu.Connect(_sp));
        _toolbar = new ToolbarView(commands, keys, ui, _sp.GetService<IKeybindingService>(), _sp.GetService<IDebugService>(), _sp.GetService<IConfigurationService>());
        _status = new StatusBarView(_sp.GetRequiredService<IStatusBarService>(), commands, keys, ui, _sp.GetService<IWindowService>(),
            new Ui.Menus.MenuServices(commands, keys, _sp.GetService<IKeybindingService>(), _sp.GetService<Core.Contributions.IContributionRegistry>()));

        var dock = BuildDock();

        var rows = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        rows.Children.Add(_titleBar);
        Grid.SetRow(_toolbar, 1);
        rows.Children.Add(_toolbar);
        Grid.SetRow(dock, 2);
        rows.Children.Add(dock);
        Grid.SetRow(_status, 3);
        rows.Children.Add(_status);

        _overlays.Children.Add(new NotificationsView(_sp.GetRequiredService<NotificationCenter>()) { IsHitTestVisible = true });
        BuildSwitcher();
        _keyboardOverlays.Children.Add(_switcher);
        // Before the views subscribe: an overlay moves into the window that has the keyboard before it shows and takes the keyboard.
        var quickVm = _sp.GetRequiredService<QuickInputViewModel>();
        var dialogVm = _sp.GetRequiredService<DialogHostViewModel>();
        quickVm.PropertyChanged += OnOverlayVisibilityChanged;
        dialogVm.PropertyChanged += OnOverlayVisibilityChanged;
        _disposables.Add(Disposable.Create(() => { quickVm.PropertyChanged -= OnOverlayVisibilityChanged; dialogVm.PropertyChanged -= OnOverlayVisibilityChanged; }));
        // The palette and the prompts give the keyboard back when they close (to the editor a palette entry opened, else to what had it).
        // (resolved once: a close's focus restore can run after the container is gone, and then does nothing)
        var documents = _shell.Well;
        object? ActiveTab() => _windowClosed ? null : documents.Active;
        void FocusActiveEditor() { if (!_windowClosed) documents.FocusActiveEditor(); }
        _quick = new QuickInputView(quickVm, ActiveTab, FocusActiveEditor) { Margin = new Thickness(0, 30, 0, 0) };
        _keyboardOverlays.Children.Add(_quick);
        _keyboardOverlays.Children.Add(new DialogHostView(dialogVm, ActiveTab, FocusActiveEditor));
        _overlays.Children.Add(_keyboardOverlays);

        var root = new Grid();
        root.Children.Add(rows);
        root.Children.Add(_overlays);
        AddResizeGrips(root, useCustomChrome);
        Content = root;

        AddDockStyles(this);
        _disposables.Add(AttachShellInput(this));
        // Only this window decides whether it closes (WPF's behaviour). Avalonia's default asks the owned (floating) windows first and lets any of them
        // cancel: a floating window's Closing empties it instead (Save prompts, tools hidden) and cancels, so the main window's X closed the floating
        // documents and stayed open, without saving the layout. OnClosing docks the floating windows back itself once the close is confirmed.
        ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        Closing += OnShellClosing;
        Opened += OnOpened;

        // Floating windows (Float in the tab and tool-window menus): each gets the shell's keys, focus keys, menus and clipboard routing, follows
        // this window's visibility (a background session hides it), and is emptied rather than closed by its own close button.
        var factory = _shell.Factory;
        factory.HostWindowCreated += OnHostWindowCreated;
        factory.WindowClosingInterceptor = w => { _ = EmptyFloatingWindowAsync(w); return false; };
        _disposables.Add(Disposable.Create(() => { factory.HostWindowCreated -= OnHostWindowCreated; factory.WindowClosingInterceptor = null; }));
        _shell.Layout.CanPresentWindows = () => IsVisible;
        _disposables.Add(this.GetObservable(IsVisibleProperty).Subscribe(new Avalonia.Reactive.AnonymousObserver<bool>(OnVisibilityChanged)));

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

    // ------------------------------------------------------------------ shell input and styles (main and floating windows)

    /// <summary>
    /// The window-level handlers every shell window needs: the modules' own (<see cref="ShellController.AttachWindow"/>: Vim's end of a key and
    /// clipboard refresh on activation, the macro recorder's and player's guards, the idle trim's activity), Vim and the keyboard pipeline
    /// (tunnelling KeyDown / TextInput / KeyUp), the focus context keys, the right-click menus, the Edit menu's focus routing, and the MRU switcher
    /// ending when the window loses activation. Dispose to remove them.
    /// </summary>
    private IDisposable AttachShellInput(Window window)
    {
        // first: the modules' tunnel handlers run before the window's own, as they did when they were attached to the main window directly
        var hooks = _shell.AttachWindow(window);
        window.AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        window.AddHandler(TextInputEvent, OnPreviewTextInput, RoutingStrategies.Tunnel);
        window.AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
        // Focus context keys (T7/T22): handled events too, because AvaloniaEdit marks its text area's GotFocus handled (the editor taking focus
        // was never seen, and the focus area kept a stale value). This handler runs after the focused control's own (GotFocus bubbles), so it
        // must agree with what a control sets for itself: the terminal sets terminalFocus, and Classify calls a focused terminal a terminal.
        window.AddHandler(GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        var menus = _shell.ContextMenus.Attach(window);
        var clipboard = _shell.Clipboard.Attach(window);
        window.Deactivated += OnShellWindowDeactivated;
        // macOS shows the menu bar of the window that has the keyboard: a floating window gets its own (its Window menu minimises it).
        Menus.NativeMenuExport? menuBar = null;
        if (OperatingSystem.IsMacOS())
        {
            menuBar = Menus.NativeMenuExport.BuildWindowMenu(_shell.Menus, window, _sp.GetService<IKeybindingService>(), _sp.GetRequiredService<Ui.Threading.IUiDispatcher>());
            NativeMenu.SetMenu(window, menuBar.Menu);
        }
        return Disposable.Create(() =>
        {
            menuBar?.Dispose();
            window.RemoveHandler(KeyDownEvent, OnPreviewKeyDown);
            window.RemoveHandler(TextInputEvent, OnPreviewTextInput);
            window.RemoveHandler(KeyUpEvent, OnPreviewKeyUp);
            window.RemoveHandler(GotFocusEvent, OnGotFocus);
            window.Deactivated -= OnShellWindowDeactivated;
            menus.Dispose();
            clipboard.Dispose();
            hooks.Dispose();
        });
    }

    // Ctrl released while another application (or window) had the keyboard: this window never sees the KeyUp that commits the switcher, so it
    // closes without switching (VS Code's and Visual Studio's switchers close the same way). Left open, the next Ctrl press and release here
    // would activate a tab picked from a stale list.
    private void OnShellWindowDeactivated(object? sender, EventArgs e)
    {
        if (_switcherActive) HideSwitcher(commit: false);
    }

    /// <summary>The shell's Dock styles (empty wells take no room, italic preview tabs, the active tab's accent bar), in every shell window.</summary>
    private static void AddDockStyles(Window window)
    {
        // An empty tool well (e.g. nothing docked left) takes no space, like Visual Studio.
        window.Styles.Add(new Style(x => x.OfType<ToolDockControl>()) { Setters = { new Setter(IsVisibleProperty, new Avalonia.Data.Binding("!IsEmpty")) } });
        window.Styles.Add(new Style(x => x.OfType<DocumentTabStripItem>()) { Setters = { new Setter(FontStyleProperty, new Avalonia.Data.Binding("PreviewFontStyle")) } });
        // VS: the active document tab carries a coloured accent bar on its TOP edge.
        window.Styles.Add(new Style(x => x.OfType<DocumentTabStripItem>().Class(":selected"))
        {
            Setters = { new Setter(BorderBrushProperty, ThemeBrushes.Get("tab.activeBorderTop")), new Setter(BorderThicknessProperty, new Thickness(0, 2, 0, 0)) },
        });
    }

    // ------------------------------------------------------------------ floating windows

    private void OnHostWindowCreated(HostWindow window)
    {
        AddDockStyles(window);
        window.Icon = Icon;
        var input = AttachShellInput(window);
        void OnOpened(object? sender, EventArgs e)
        {
            // Its DockControl (from the host's template) presents the shell's tabs as the main one does (ShellDockContent).
            foreach (var dock in window.GetVisualDescendants().OfType<DockControl>()) ShellDockContent.Install(dock);
            if (!IsVisible) window.Hide(); // the main window is hidden (background mode): so are its floating windows
        }
        void OnClosedHost(object? sender, EventArgs e)
        {
            window.Opened -= OnOpened;
            window.Closed -= OnClosedHost;
            if (ReferenceEquals(_overlayHost, window)) HostKeyboardOverlays(this); // back home: the window's tree goes
            input.Dispose();
            // The keyboard was in the window that closed (a closing tab hands it to its window): it comes back to this window's active editor, as in
            // Visual Studio. Left there, the closed window stayed the focused element and kept itself, its last tab's view and document alive.
            if (!_windowClosed && FocusManager?.GetFocusedElement() is Visual focused && (ReferenceEquals(focused, window) || window.IsVisualAncestorOf(focused) || !focused.IsAttachedToVisualTree()))
            {
                if (!Focus()) FocusManager?.Focus(null);
                _shell.Well.FocusActiveEditor();
            }
        }
        window.Opened += OnOpened;
        window.Closed += OnClosedHost;
    }

    // ------------------------------------------------------------------ overlays that take the keyboard

    private void OnOverlayVisibilityChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QuickInputViewModel.IsVisible)) return; // (DialogHostViewModel's has the same name)
        var opening = sender switch { QuickInputViewModel q => q.IsVisible, DialogHostViewModel d => d.IsVisible, _ => false };
        if (opening) HostKeyboardOverlays(FocusManager?.GetFocusedElement() is Visual focused ? TopLevel.GetTopLevel(focused) : null);
    }

    /// <summary>
    /// Shows the keyboard overlays in <paramref name="target"/>: a visible floating window gets them over its dock (an adorner: arranged to the
    /// dock's bounds, in the window's own tree, so focus and keys work there); anything else (this window, nothing) puts them back here.
    /// </summary>
    private void HostKeyboardOverlays(TopLevel? target)
    {
        Control? dock = target is HostWindow { IsVisible: true } floating && !ReferenceEquals(floating, this)
            ? floating.GetVisualDescendants().OfType<DockControl>().FirstOrDefault() : null;
        if (ReferenceEquals(dock, _overlaysAdorning)) return;
        if (_overlaysAdorning is { } adorned) AdornerLayer.SetAdorner(adorned, null);
        else _overlays.Children.Remove(_keyboardOverlays);
        _overlaysAdorning = dock;
        _overlayHost = dock is null ? null : target;
        _quick.Margin = new Thickness(0, dock is null ? 30 : 0, 0, 0); // this window's card sits under its title bar
        if (dock is not null) AdornerLayer.SetAdorner(dock, _keyboardOverlays);
        else _overlays.Children.Add(_keyboardOverlays);
    }

    private void OnVisibilityChanged(bool visible)
    {
        foreach (var w in _shell.Factory.FloatingWindows)
        {
            if (w.Host is not Window host) continue;
            if (visible && !host.IsVisible) host.Show();
            else if (!visible && host.IsVisible) host.Hide();
        }
        if (visible) _shell.Layout.ApplyPendingFloating();
    }

    /// <summary>
    /// A floating window's own close button (or Alt+F4): its tool windows are hidden (they float there again when shown, as in Visual Studio) and
    /// its documents closed through the well (Save prompts); the window closes once it is empty. A cancelled prompt leaves the rest open.
    /// </summary>
    private async Task EmptyFloatingWindowAsync(Dock.Model.Core.IDockWindow window)
    {
        try
        {
            var inWindow = new List<Dock.Model.Core.IDockable>();
            void Collect(Dock.Model.Core.IDockable d)
            {
                if (d is ShellDocument or ToolTab) inWindow.Add(d);
                if (d is Dock.Model.Core.IDock { VisibleDockables: { } kids }) foreach (var k in kids.ToArray()) Collect(k);
            }
            if (window.Layout is { } layout) Collect(layout);
            foreach (var tool in inWindow.OfType<ToolTab>()) _shell.Tools.Hide(tool.Spec.Id);
            foreach (var doc in inWindow.OfType<ShellDocument>())
                if (!await _shell.Well.CloseAsync(doc).ConfigureAwait(true)) break;
            _shell.Factory.CloseEmptyWindows();
        }
        catch (Exception ex) { _sp.GetService<Startup.CrashHandler>()?.Report(ex, fatal: false, source: "Floating window"); }
    }

    /// <summary>Before the main window closes (its layout was saved): floating tools and documents go back into it, and their windows close.</summary>
    private void ReleaseFloatingWindows()
    {
        _shell.Factory.IsShuttingDown = true; // what is still open when this window closes (an owned window) closes with it
        try { _shell.Layout.DockAllFloating(); }
        catch (Exception ex) { _sp.GetService<Startup.CrashHandler>()?.Report(ex, fatal: false, source: "Floating window"); }
    }

    // ------------------------------------------------------------------ dock host

    private DockControl BuildDock()
    {
        var factory = _shell.Factory;
        var layout = factory.EnsureLayout();
        var dock = new DockControl { Layout = layout, Factory = factory, InitializeLayout = false, InitializeFactory = false };
        ShellDockContent.Install(dock);
        // Dock's control recycling caches the content of every dockable it presented, keyed by the dockable, and never evicts: a closed tab's
        // host stayed reachable for the whole session and, through the host's composition visual (no longer updated once the host left the
        // tree), so did the editor view and document it had held. A closed document tab is never presented again (reopening creates a new tab),
        // so its entry goes when it closes. Tool windows keep theirs: closing one hides it (ShellDockFactory.HideToolsOnClose; Dock still raises
        // DockableClosed for it) and its host is reused on the next show.
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

    // ------------------------------------------------------------------ startup

    private void OnOpened(object? sender, EventArgs e)
    {
        Icon = AppIcon.Create();
        // Everything that does I/O or reads state runs after the first frame so the window appears immediately.
        Dispatcher.UIThread.Post(async () =>
        {
            try { await _shell.StartDeferredAsync().ConfigureAwait(true); }
            catch (Exception ex) { _sp.GetService<Startup.CrashHandler>()?.Report(ex, fatal: false, source: "Startup"); }
            // macOS: About or Settings... chosen in the application menu while there was no window runs now, in this one.
            if (!_windowClosed) App.ApplicationMenu?.Started(_sp);
        }, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ keyboard and focus

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        _swallowTextUntil = 0;
        // Ctrl+Tab / Ctrl+Shift+Tab: MRU editor switcher overlay (committed when Ctrl is released).
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (!_switcherActive) HostKeyboardOverlays(sender as TopLevel); // in the window the keys are pressed in
            StepSwitcher(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : +1);
            e.Handled = true;
            return;
        }
        if (_switcherActive && e.Key == Key.Escape) { HideSwitcher(commit: false); e.Handled = true; return; }
        // Vim sees an editor's keys before the key bindings (it decides which are its own: docs/VIM.md, "Keys")
        if (_shell.KeyInterceptor?.OnKeyDown(e) == true) { e.Handled = true; return; }
        if (!AvaloniaKeyMapper.TryMap(e.Key, e.KeyModifiers, out var stroke)) return;
        if (_shell.Keyboard.Handle(stroke) == KeyDispatch.Handled)
        {
            e.Handled = true;
            // A character key the pipeline took (the second key of a chord: Ctrl+W v, Ctrl+K S, or a dead one) is not typing: a platform that
            // sends the character as text input after the key press anyway must not get it into the editor. Only that key's own text input, which
            // follows at once: a platform that sends none for a handled key must not lose later text input with no key press, such
            // as dictation or a character picked in the Character Viewer.
            _swallowTextUntil = Vim.VimKeyPolicy.IsTextKey(stroke) ? Environment.TickCount64 + SwallowTextMs : 0;
        }
    }

    /// <summary>
    /// Until this tick (<see cref="Environment.TickCount64"/>; 0: none) the next single-character text input belongs to the character key the key
    /// pipeline just handled and goes nowhere.
    /// </summary>
    private long _swallowTextUntil;
    private const int SwallowTextMs = 200;

    private void OnPreviewTextInput(object? sender, TextInputEventArgs e)
    {
        if (_swallowTextUntil != 0)
        {
            var own = Environment.TickCount64 <= _swallowTextUntil && e.Text is { } text && new System.Globalization.StringInfo(text).LengthInTextElements == 1;
            _swallowTextUntil = 0;
            if (own)
            {
                e.Handled = true;
                return;
            }
        }
        if (_shell.KeyInterceptor?.OnTextInput(e) == true) e.Handled = true;
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (_switcherActive && e.Key is Key.LeftCtrl or Key.RightCtrl) HideSwitcher(commit: true);
    }

    private void OnGotFocus(object? sender, RoutedEventArgs e) => _shell.ContextKeys.SetFocus(ShellFocus.Classify(e.Source as Visual));

    // ------------------------------------------------------------------ MRU switcher

    private void BuildSwitcher()
    {
        _switcher.IsVisible = false;
        _switcher.HorizontalAlignment = HorizontalAlignment.Center;
        _switcher.VerticalAlignment = VerticalAlignment.Center;
        _switcher.Name = SwitcherName;
        _switcher.MinWidth = OverlaySizing.SwitcherMinWidth;
        _switcher.Padding = new Thickness(6);
        _switcher.Background = ThemeBrushes.Get("editorWidget.background");
        _switcher.BorderBrush = ThemeBrushes.Get("editorWidget.border");
        _switcher.BorderThickness = new Thickness(1);
        _switcher.BoxShadow = BoxShadows.Parse("0 6 18 0 #66000000");
        _switcher.Child = _switcherScroll;
        _switcherScroll.Content = _switcherItems;
        _keyboardOverlays.Measuring = FitSwitcher;
    }

    /// <summary>The switcher's name in the visual tree (tests).</summary>
    internal const string SwitcherName = "CtrlTabSwitcher";

    /// <summary>
    /// The switcher fits the window it is in (a floating one can be narrow or short): at most that window inside a margin, its rows scrolling and
    /// their titles trimmed.
    /// </summary>
    private void FitSwitcher(Size window)
    {
        var width = OverlaySizing.Inside(window.Width);
        var height = OverlaySizing.Inside(window.Height);
        OverlaySizing.Set(_switcher, MaxWidthProperty, width);
        OverlaySizing.Set(_switcher, MinWidthProperty, Math.Min(OverlaySizing.SwitcherMinWidth, width));
        OverlaySizing.Set(_switcher, MaxHeightProperty, height);
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
        Border? selected = null;
        for (var i = 0; i < _switcherList.Count; i++)
        {
            var label = ViewHelpers.Label(_switcherList[i].Title, i == _switcherIndex ? "list.activeSelectionForeground" : "foreground", 12);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            var row = new Border
            {
                Padding = new Thickness(10, 4), Background = i == _switcherIndex ? ThemeBrushes.Get("list.activeSelectionBackground") : Brushes.Transparent,
                Child = label,
            };
            if (i == _switcherIndex) selected = row;
            _switcherItems.Children.Add(row);
        }
        _switcher.IsVisible = true;
        // a short window shows some of the rows: the selected one is among them
        if (selected is not null) Dispatcher.UIThread.Post(() => { if (_switcher.IsVisible && selected.IsAttachedToVisualTree()) selected.BringIntoView(); }, DispatcherPriority.Loaded);
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

    /// <summary>Background mode: the named session this window shows (title "file - session — folder - CsCode"); null for none or an unnamed one.</summary>
    public string? SessionName
    {
        get => _sessionName;
        set
        {
            if (_sessionName == value) return;
            _sessionName = value;
            UpdateTitle();
        }
    }

    /// <summary>
    /// Background mode: asked before a user's close goes ahead; returning true means it was handled (the window was hidden, or a quit with hot exit
    /// started) and the close is cancelled. <see cref="CloseWithoutPrompt"/> and <see cref="RequestCloseAsync"/> bypass it, and so does the close
    /// that a module repeats after it held one confirmed with prompts (unless <see cref="ForgetHeldClose"/>).
    /// </summary>
    public Func<WindowCloseReason, bool>? CloseInterceptor { get; set; }

    /// <summary>
    /// A confirmed close goes ahead: raised once every Closing handler let it (before the floating windows dock back and the window closes), also
    /// for a close that a module held and repeats when the user agrees. Background mode commits its quit here: until then a module may still
    /// hold the close, and a held close the user declines leaves the window working.
    /// </summary>
    public event Action? CloseProceeding;

    private void UpdateTitle()
    {
        var well = _shell.Well;
        var name = _sp.GetService<IWorkspaceService>()?.Name;
        if (_sessionName is { } session) name = name is null ? session : session + " — " + name;
        var active = well.ActiveEditor;
        var dirty = well.EditorTabs.Any(t => t.IsDirty) ? "● " : "";
        var file = active?.Title is { } t ? t + " - " : "";
        var title = $"{dirty}{file}{(name is null ? "" : name + " - ")}CsCode";
        Title = title;
        _titleBar.SetWorkspaceName(name);
    }

    // ------------------------------------------------------------------ closing

    /// <summary>A close this window confirmed that a module's Closing handler then held, and how it was confirmed (see <see cref="OnClosing"/>).</summary>
    private enum HeldClose { None, Prompted, WithoutPrompt }

    private HeldClose _held, _confirming;
    private bool _closeProceeding;
    /// <summary>
    /// The questions (notifications with actions) unanswered when the last confirmed close began: those shown since, while that close is held,
    /// are the holding module's (<see cref="HeldQuestions"/>).
    /// </summary>
    private HashSet<NotificationItem>? _questionsBefore;

    /// <summary>
    /// This window's own close handling, the first Closing handler: background mode's hide, then (cancelling this close and closing again when they
    /// are answered) the layout save and the Save prompts. The modules' handlers (the terminal's <c>confirmOnExit</c>) run after it.
    /// </summary>
    private async void OnShellClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed) return;
        var held = _held;
        // The module that held a confirmed close closes again when the user agrees: a close confirmed without prompts goes ahead as it was; one
        // confirmed with prompts asks only for what is dirty since (normally nothing), and is past background mode's hide already.
        if (e.IsProgrammatic && held == HeldClose.WithoutPrompt)
        {
            _held = HeldClose.None;
            _closeConfirmed = true;
            _confirming = HeldClose.WithoutPrompt;
            return;
        }
        if (!(e.IsProgrammatic && held == HeldClose.Prompted) && CloseInterceptor?.Invoke(e.CloseReason) == true)
        {
            e.Cancel = true; // hidden (background mode): a held close stays held
            return;
        }
        if (!(e.IsProgrammatic && held == HeldClose.Prompted)) IsExiting = false; // a close of its own (the close button), not File > Exit's repeat
        _held = HeldClose.None;
        e.Cancel = true;
        await CloseWithPromptsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// After every Closing handler: a confirmed close that goes ahead docks the floating windows back first (the layout was saved with them
    /// floating); one that a module held (the terminal asks, and closes again when the user agrees) leaves this window as it was, working, and
    /// the next close starts over (Save prompts included: the user may have edited since declining).
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!_closeConfirmed) return;
        if (!e.Cancel)
        {
            _closeProceeding = true;
            try { CloseProceeding?.Invoke(); }
            catch (Exception ex) { _sp.GetService<Startup.CrashHandler>()?.Report(ex, fatal: false, source: "Closing"); }
            ReleaseFloatingWindows();
            return;
        }
        _closeConfirmed = false;
        _held = _confirming;
    }

    /// <summary>The Save prompts, then the close: how it ended (a prompt cancelled, a module holding the close, or the window closed).</summary>
    private async Task<ShellController.CloseEnd> CloseWithPromptsAsync()
    {
        _shell.BeginClose(); // saves the layout before tabs close, so the next start restores them
        var end = ShellController.CloseEnd.Cancelled;
        try
        {
            if (await _shell.Well.CloseAllAsync().ConfigureAwait(true)) end = CloseConfirmed(HeldClose.Prompted);
        }
        catch (Exception ex)
        {
            _sp.GetService<Startup.CrashHandler>()?.Report(ex, fatal: false, source: "Closing");
            end = CloseConfirmed(HeldClose.Prompted);
        }
        finally { _shell.EndClose(end); } // cancelled (a Save prompt): keep the layout as it is now
        return end;
    }

    private ShellController.CloseEnd CloseConfirmed(HeldClose how)
    {
        _closeConfirmed = true;
        _confirming = how;
        _closeProceeding = false;
        _questionsBefore = PendingQuestions().ToHashSet();
        Close();
        return _closeProceeding ? ShellController.CloseEnd.Closed : ShellController.CloseEnd.Held;
    }

    /// <summary>
    /// Closes without any save prompts (background mode's hot-exit quit, tests and crash recovery): <see cref="ShellController.CloseEnd.Closed"/>, or
    /// <see cref="ShellController.CloseEnd.Held"/> when a module holds the close with a question (it closes again when the user agrees).
    /// </summary>
    public ShellController.CloseEnd CloseWithoutPrompt() => CloseConfirmed(HeldClose.WithoutPrompt);

    public async Task<bool> RequestExitAsync()
    {
        IsExiting = true;
        var end = await CloseWithPromptsAsync().ConfigureAwait(true);
        if (end == ShellController.CloseEnd.Cancelled) IsExiting = false; // a Save prompt was cancelled: the user works on
        return end == ShellController.CloseEnd.Closed;
    }

    /// <summary>
    /// The close under way (or the one a module holds) is File &gt; Exit's (also ⌘Q's, which runs it), which ends the app; false for a close of the
    /// window alone (its close button), which on macOS leaves the app running without a window (<see cref="Startup.AppSession"/>).
    /// </summary>
    public bool IsExiting { get; private set; }

    /// <summary>An editor has changes not saved yet (what the Save prompts of a close would ask about).</summary>
    public bool HasUnsavedChanges => _shell.Well.EditorTabs.Any(t => t.IsDirty);

    /// <summary>
    /// The desktop session ends (macOS log out with nothing unsaved): the layout is saved, as for any close, so the next start restores the tabs,
    /// and the window closes without prompts. <see cref="ShellController.CloseEnd.Held"/> when a module holds the close with a question.
    /// </summary>
    public ShellController.CloseEnd CloseForSystemShutdown()
    {
        _shell.BeginClose();
        var end = ShellController.CloseEnd.Cancelled;
        try { end = CloseWithoutPrompt(); }
        finally { _shell.EndClose(end); }
        return end;
    }

    /// <summary>Shows the window (restored when minimised) and activates it: the system asked for the app (macOS Dock icon, documents opened).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        Activate();
    }

    /// <summary>Opens paths handed over after the start (macOS file activation) as the command line's are opened (<see cref="ShellController.OpenTargets"/>).</summary>
    public void OpenTargets(IReadOnlyList<Startup.StartupTarget> targets) => _shell.OpenTargets(targets);

    /// <summary>
    /// The Save prompts, then the close (background mode's quit with prompts): how it ended. <see cref="ShellController.CloseEnd.Held"/> is not a
    /// cancel: every prompt was answered and the tabs are closed, and the module that holds the close repeats it when the user agrees (see
    /// <see cref="CloseProceeding"/>), or does nothing when they decline.
    /// </summary>
    public Task<ShellController.CloseEnd> RequestCloseAsync() => CloseWithPromptsAsync();

    /// <summary>
    /// Background mode: the quit that a module's held close belonged to was given up (the user declined the module's question and works on, the
    /// quit was refused, or it starts over), so that close no longer stands: the question it raised is withdrawn (dismissed, unanswered), and the
    /// next close of this window is a new one (background mode's hide first, and the Save prompts again).
    /// </summary>
    public void ForgetHeldClose()
    {
        var questions = HeldQuestions().ToArray();
        _held = HeldClose.None;
        _questionsBefore = null;
        // Its question goes with it (as if declined): the module then does nothing, and a later answer cannot close the window behind the quit's
        // back (or hide it, and leave the module believing the user agreed).
        foreach (var question in questions) question.DismissCommand.Execute(null);
    }

    /// <summary>How a close that a module held (<see cref="ShellController.CloseEnd.Held"/>) settled (<see cref="SettleHeldCloseAsync"/>).</summary>
    public enum HeldCloseState
    {
        /// <summary>The module let the close go ahead (it closed again without asking, or the user agreed already): the window closes.</summary>
        Closed,
        /// <summary>The module asks: a question (a notification with actions) shown since the close was confirmed is up, unanswered.</summary>
        Asking,
        /// <summary>Neither, within the bound: the module still holds the close and shows no question.</summary>
        Undecided,
    }

    /// <summary>
    /// After a close came back held (<see cref="ShellController.CloseEnd.Held"/>): waits, without blocking the UI thread, until the module that holds
    /// it asks (<see cref="HeldCloseState.Asking"/>) or lets it go ahead (<see cref="HeldCloseState.Closed"/>), at most <paramref name="bound"/>. A
    /// held close is not always a question: the terminal's <c>confirmOnExit</c> "hasChildProcesses" holds every close while it looks for programs
    /// other than the shell (off the UI thread), and closes again without a word when only shells run. Background mode's quits decide nothing
    /// before this settles.
    /// </summary>
    public async Task<HeldCloseState> SettleHeldCloseAsync(TimeSpan bound)
    {
        Dispatcher.UIThread.VerifyAccess();
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (_closeProceeding || HeldQuestions().Any()) settled.TrySetResult();
        }
        void OnProceeding() => settled.TrySetResult();
        void OnNotifications(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Check();
        var notifications = _sp.GetRequiredService<NotificationCenter>();
        CloseProceeding += OnProceeding;
        notifications.History.CollectionChanged += OnNotifications;
        try
        {
            Check();
            try { await settled.Task.WaitAsync(bound).ConfigureAwait(true); }
            catch (TimeoutException) { /* still held, no question shown */ }
        }
        finally
        {
            CloseProceeding -= OnProceeding;
            notifications.History.CollectionChanged -= OnNotifications;
        }
        if (_closeProceeding) return HeldCloseState.Closed;
        return HeldQuestions().Any() ? HeldCloseState.Asking : HeldCloseState.Undecided;
    }

    /// <summary>The notifications with actions that wait for an answer (UI thread).</summary>
    private IEnumerable<NotificationItem> PendingQuestions() =>
        _sp.GetRequiredService<NotificationCenter>().History.Where(n => n.HasActions && !n.IsCompleted);

    /// <summary>The questions shown since the held close (if one stands) was confirmed and still unanswered: the holding module's.</summary>
    private IEnumerable<NotificationItem> HeldQuestions() =>
        _held == HeldClose.None || _questionsBefore is not { } before ? [] : PendingQuestions().Where(n => !before.Contains(n));

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
        _windowClosed = true;
        base.OnClosed(e);
        if (_shell.Factory.FloatingWindows.Count > 0) ReleaseFloatingWindows(); // closed some other way (the lifetime ended)
        _disposables.Dispose();
        _status.Dispose();
        _toolbar.Dispose();
        _shell.Dispose();
    }
}
