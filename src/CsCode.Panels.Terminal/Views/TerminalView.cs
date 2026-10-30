using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.Core.Lifecycle;
using CsCode.Core.Terminal;
using CsCode.Terminal.Emulator;
using CsCode.Terminal.Sessions;
using CsCode.Ui.Icons;
using CsCode.Ui.Menus;
using CsCode.Ui.Theming;
using DockSide = Avalonia.Controls.Dock;
using IDock = Dock.Model.Core.IDock;
using IDockable = Dock.Model.Core.IDockable;
using Orientation = Avalonia.Layout.Orientation;

namespace CsCode.Panels.Terminal.Views;

/// <summary>Plain DataContext of the view (the shell disposes a disposable DataContext with the view; this one holds nothing).</summary>
internal sealed class TerminalViewState
{
    public static readonly TerminalViewState Instance = new();
}

/// <summary>
/// The Terminal tool window's content (Visual Studio's "Terminal" in the bottom well): a toolbar with the terminal list (VS Code's dropdown:
/// "1: bash"), New, Kill, Clear and Maximize, and the active terminal's <see cref="TerminalControl"/> with its scrollbar. The view is created
/// when the window is shown and disposed when it is hidden; the terminals live on in the service. The window's title carries the terminal
/// count. Hiding and maximizing go through the Dock dockable that hosts the view (the same operation as its title bar buttons).
/// </summary>
public sealed class TerminalView : DockPanel, IDisposable, IClipboardTarget
{
    private readonly TerminalPanelRuntime _runtime;
    private readonly DisposableStore _subs = new();
    private readonly ComboBox _picker;
    private readonly ScrollBar _scroll;
    private bool _updatingScroll;
    private bool _altBuffer;
    private double? _restoreProportion;

    public TerminalView(TerminalPanelRuntime runtime)
    {
        _runtime = runtime;
        DataContext = TerminalViewState.Instance;
        Control = new TerminalControl(runtime);
        Background = ThemeBrushes.Get("terminal.background");

        _picker = new ComboBox { MinWidth = 160, MaxWidth = 320, Height = 22, Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(_picker, "Terminals");
        _picker.SelectionChanged += (_, _) =>
        {
            if (_picker.SelectedItem is ComboBoxItem { Tag: TerminalSession s } && !ReferenceEquals(s, runtime.Service.ActiveSession)) runtime.Service.SetActive(s);
        };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 2), HorizontalAlignment = HorizontalAlignment.Right };
        toolbar.Children.Add(_picker);
        toolbar.Children.Add(Button("add", "New Terminal", TerminalCommandSet.New));
        toolbar.Children.Add(Button("trash", "Kill Terminal", TerminalCommandSet.Kill));
        toolbar.Children.Add(Button("clear-all", "Clear Terminal", TerminalCommandSet.Clear));
        toolbar.Children.Add(Button("chevron-up", "Maximize Panel Size", null, ToggleMaximize));
        var header = new Border { Child = toolbar, Background = ThemeBrushes.Get("panel.background"), BorderBrush = ThemeBrushes.Get("panel.border"), BorderThickness = new Thickness(0, 0, 0, 1) };
        SetDock(header, DockSide.Top);
        Children.Add(header);

        _scroll = new ScrollBar { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 0, ViewportSize = 1, AllowAutoHide = false };
        _scroll.Scroll += (_, e) =>
        {
            if (_updatingScroll || Control.Snapshot is not { } snap) return;
            Control.ScrollTo(snap.ScrollbackTop + (long)Math.Round(e.NewValue));
        };
        SetDock(_scroll, DockSide.Right);
        Children.Add(_scroll);
        Children.Add(Control);

        Control.FrameDrawn += OnFrameDrawn;
        var service = runtime.Service;
        void Refresh() => Dispatcher.UIThread.Post(RefreshList);
        _subs.Add(service.Created.Subscribe(_ => Refresh()));
        _subs.Add(service.Removed.Subscribe(_ => Refresh()));
        _subs.Add(service.TitleChanged.Subscribe(_ => Refresh()));
        _subs.Add(service.ActiveChanged.Subscribe(_ => Refresh()));
        _subs.Add(service.Exited.Subscribe(_ => Refresh()));
        runtime.Attach(this);
        RefreshList();
    }

    public TerminalControl Control { get; }
    public bool IsDisposed { get; private set; }

    /// <summary>On screen (the tool window is shown and this is its visible tab).</summary>
    public bool IsShown => this.IsAttachedToVisualTree() && IsEffectivelyVisible;

    private Button Button(string icon, string tip, string? command, Action? action = null)
    {
        var b = new Button
        {
            Content = new Icon(icon, 16),
            Padding = new Thickness(4),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(b, command is not null && _runtime.Bindings?.GetLabel(command) is { } key ? $"{tip} ({key})" : tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        b.Click += async (_, _) =>
        {
            if (action is not null) action();
            else if (command is not null && _runtime.Commands is { } commands)
            {
                try { await commands.ExecuteAsync(command); }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { ContextMenus.ErrorSink?.Invoke(ex); }
            }
        };
        return b;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Opening the Terminal window with no terminal creates one, as VS Code does.
        if (_runtime.Service.Terminals.Count == 0) _ = CreateFirstAsync();
        if (_runtime.TakeFocusRequest()) FocusTerminal();
    }

    private async Task CreateFirstAsync()
    {
        try { await _runtime.Service.CreateAsync(new TerminalCreateOptions { Show = false }); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Rebuilds the dropdown ("1: bash"), attaches the active terminal and updates the window title with the count.</summary>
    private void RefreshList()
    {
        if (IsDisposed) return;
        var service = _runtime.Service;
        var sessions = service.Sessions;
        var active = service.ActiveSession ?? sessions.FirstOrDefault();
        static string Label(TerminalSession s) => $"{s.Id}: {s.Title}" + (s.HasExited ? $" (exited {s.ExitCode})" : "");
        var items = _picker.Items.OfType<ComboBoxItem>().ToList();
        if (items.Count == sessions.Count && items.Select(i => i.Tag).SequenceEqual(sessions))
        {
            // the same terminals (a title changed, the program exited): relabel in place, no new controls
            foreach (var item in items)
                if (item.Tag is TerminalSession s && !Equals(item.Content, Label(s))) item.Content = Label(s);
            var selected = items.FirstOrDefault(i => ReferenceEquals(i.Tag, active));
            if (!ReferenceEquals(_picker.SelectedItem, selected)) _picker.SelectedItem = selected;
        }
        else
        {
            _picker.Items.Clear();
            foreach (var s in sessions)
            {
                var item = new ComboBoxItem { Content = Label(s), Tag = s };
                _picker.Items.Add(item);
                if (ReferenceEquals(s, active)) _picker.SelectedItem = item;
            }
        }
        _picker.IsVisible = sessions.Count > 0;
        Control.Session = active;
        var title = sessions.Count > 1 ? $"Terminal ({sessions.Count})" : "Terminal";
        if (Dockable() is { } dockable && dockable.Title != title) dockable.Title = title;
    }

    private void OnFrameDrawn(TerminalSnapshot snap)
    {
        _updatingScroll = true;
        try
        {
            var lines = snap.ScreenTop - snap.ScrollbackTop;
            _scroll.Maximum = Math.Max(0, lines);
            _scroll.ViewportSize = snap.RowCount;
            _scroll.Value = Math.Clamp(snap.ViewportTop - snap.ScrollbackTop, 0, _scroll.Maximum);
            _scroll.IsEnabled = lines > 0;
        }
        finally { _updatingScroll = false; }
        if (snap.Modes.AlternateScreen != _altBuffer)
        {
            _altBuffer = snap.Modes.AlternateScreen;
            _runtime.SetAltBuffer(_altBuffer);
        }
    }

    /// <summary>Shows <paramref name="session"/> (it becomes the active terminal).</summary>
    public void Show(TerminalSession session)
    {
        _runtime.Service.SetActive(session);
        RefreshList();
    }

    public void FocusTerminal() => Dispatcher.UIThread.Post(() => { if (!IsDisposed) Control.Focus(); }, DispatcherPriority.Loaded);

    // ------------------------------------------------------------------ the hosting dockable

    private IDockable? Dockable()
    {
        for (Visual? v = this.GetVisualParent(); v is not null; v = v.GetVisualParent())
            if (v is StyledElement { DataContext: IDockable d } && d.Id?.EndsWith(TerminalService.ViewId, StringComparison.Ordinal) == true) return d;
        return null;
    }

    /// <summary>Hides the Terminal window (the shell disposes this view; terminals keep running).</summary>
    public void Hide()
    {
        if (Dockable() is not { Factory: { } factory } dockable) return;
        Control.ReleaseFocus(); // before the view leaves the window: a focused control would stay reachable from the focus machinery
        factory.HideDockable(dockable);
    }

    /// <summary>VS Code's Maximize Panel Size: the bottom well takes most of the height, and back.</summary>
    public void ToggleMaximize()
    {
        if (Dockable()?.Owner is not IDock well) return;
        if (_restoreProportion is { } restore)
        {
            well.Proportion = restore;
            _restoreProportion = null;
        }
        else
        {
            _restoreProportion = double.IsNaN(well.Proportion) ? 0.28 : well.Proportion;
            well.Proportion = 0.85;
        }
    }

    // ------------------------------------------------------------------ clipboard target (Edit menu)

    public bool CanCopy => Control.CanCopy;
    public bool CanPaste => Control.CanPaste;
    public Task CopyAsync() => Control.CopyAsync();
    public Task PasteAsync() => Control.PasteAsync();

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Control.FrameDrawn -= OnFrameDrawn;
        _subs.Dispose();
        Control.Dispose();
        _runtime.Detach(this);
        _runtime.SetAltBuffer(false);
    }
}
