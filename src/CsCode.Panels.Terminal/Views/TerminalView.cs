using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.Core.Lifecycle;
using CsCode.Core.Terminal;
using CsCode.Terminal.Emulator;
using CsCode.Terminal.Sessions;
using CsCode.Ui.Icons;
using ToggleButton = Avalonia.Controls.Primitives.ToggleButton;
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
/// The Terminal tool window's content (Visual Studio's "Terminal" in the bottom well): a toolbar with the terminal list (VS Code's dropdown,
/// one entry per terminal group: "1: bash", "2: bash, bash" for a split group), New, Split, Kill, Clear and Maximize, and the shown group's
/// panes side by side (<see cref="TerminalPane"/>: a <see cref="TerminalControl"/> and its scrollbar each, separated by splitters). The view is
/// created when the window is shown and disposed when it is hidden; the terminals live on in the service. The window's title carries the
/// terminal count. Hiding and maximizing go through the Dock dockable that hosts the view (the same operation as its title bar buttons).
/// </summary>
public sealed class TerminalView : DockPanel, IDisposable, IClipboardTarget
{
    /// <summary>VS Code resizes a pane by four character cells per command.</summary>
    private const int ResizeCells = 4;
    private const double MinPaneWidth = 80;

    private readonly TerminalPanelRuntime _runtime;
    private readonly DisposableStore _subs = new();
    private readonly ComboBox _picker;
    private readonly Grid _panesHost = new();
    private readonly List<TerminalPane> _panes = new();
    private TerminalGroup? _shownGroup;
    private bool _altBuffer;
    private readonly Border _findBar;
    private readonly TextBox _findBox;
    private readonly ToggleButton _matchCase;
    private readonly TextBlock _findStatus;
    private TerminalMatch? _match;
    private int _findSeq;
    private double? _restoreProportion;

    public TerminalView(TerminalPanelRuntime runtime)
    {
        _runtime = runtime;
        DataContext = TerminalViewState.Instance;
        Background = ThemeBrushes.Get("terminal.background");

        _picker = new ComboBox { MinWidth = 160, MaxWidth = 320, Height = 22, Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(_picker, "Terminals");
        _picker.SelectionChanged += (_, _) =>
        {
            if (_picker.SelectedItem is ComboBoxItem { Tag: TerminalGroup g } && !ReferenceEquals(g, runtime.Service.ActiveGroup)
                && (g.ActiveTerminal ?? g.Terminals.FirstOrDefault()) is { } s)
                runtime.Service.SetActive(s);
        };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 2), HorizontalAlignment = HorizontalAlignment.Right };
        toolbar.Children.Add(_picker);
        toolbar.Children.Add(Button("add", "New Terminal", TerminalCommandSet.New));
        toolbar.Children.Add(Button("split-horizontal", "Split Terminal", TerminalCommandSet.Split));
        toolbar.Children.Add(Button("trash", "Kill Terminal", TerminalCommandSet.Kill));
        toolbar.Children.Add(Button("clear-all", "Clear Terminal", TerminalCommandSet.Clear));
        toolbar.Children.Add(Button("chevron-up", "Maximize Panel Size", null, ToggleMaximize));
        var header = new Border { Child = toolbar, Background = ThemeBrushes.Get("panel.background"), BorderBrush = ThemeBrushes.Get("panel.border"), BorderThickness = new Thickness(0, 0, 0, 1) };
        SetDock(header, DockSide.Top);
        Children.Add(header);

        // Find (VS Code's terminal find widget): Enter searches up the output, Shift+Enter down, Escape closes
        _findBox = new TextBox { Width = 220, MinHeight = 22, Padding = new Thickness(4, 1), PlaceholderText = "Find", VerticalContentAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(_findBox, "Find in terminal");
        _findBox.KeyDown += OnFindKeyDown;
        _findBox.TextChanged += (_, _) => { _match = null; _ = FindAsync(backward: true); };
        _matchCase = new ToggleButton { Content = new Icon("case-sensitive", 16), Padding = new Thickness(3), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(_matchCase, "Match Case");
        Avalonia.Automation.AutomationProperties.SetName(_matchCase, "Match Case");
        _matchCase.IsCheckedChanged += (_, _) => { _match = null; _ = FindAsync(backward: true); };
        _findStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), MinWidth = 70 };
        var findRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 2), HorizontalAlignment = HorizontalAlignment.Right };
        findRow.Children.Add(_findBox);
        findRow.Children.Add(_matchCase);
        findRow.Children.Add(_findStatus);
        findRow.Children.Add(Button("arrow-up", "Previous Match (Enter)", null, () => _ = FindAsync(backward: true)));
        findRow.Children.Add(Button("arrow-down", "Next Match (Shift+Enter)", null, () => _ = FindAsync(backward: false)));
        findRow.Children.Add(Button("close", "Close (Escape)", null, HideFind));
        _findBar = new Border
        {
            Child = findRow, IsVisible = false,
            Background = ThemeBrushes.Get("panel.background"), BorderBrush = ThemeBrushes.Get("panel.border"), BorderThickness = new Thickness(0, 0, 0, 1),
        };
        SetDock(_findBar, DockSide.Top);
        Children.Add(_findBar);
        Children.Add(_panesHost);

        var service = runtime.Service;
        void Refresh() => Dispatcher.UIThread.Post(RefreshList);
        _subs.Add(service.Created.Subscribe(_ => Refresh()));
        _subs.Add(service.Removed.Subscribe(_ => Refresh()));
        _subs.Add(service.TitleChanged.Subscribe(_ => Refresh()));
        _subs.Add(service.ActiveChanged.Subscribe(_ => Refresh()));
        _subs.Add(service.Exited.Subscribe(_ => Refresh()));
        _subs.Add(service.GroupsChanged.Subscribe(_ => Refresh()));
        runtime.Attach(this);
        RefreshList();
    }

    /// <summary>The active terminal's control (the focused pane of the shown group; the empty pane's control when there is no terminal).</summary>
    public TerminalControl Control => ActivePane.Control;

    /// <summary>The panes of the shown group, left to right.</summary>
    public IReadOnlyList<TerminalPane> Panes => _panes;

    private TerminalPane ActivePane
    {
        get
        {
            var active = _runtime.Service.ActiveSession;
            foreach (var p in _panes)
                if (ReferenceEquals(p.Session, active)) return p;
            return _panes[0]; // never empty: a window without terminals shows one empty pane
        }
    }

    public bool IsDisposed { get; private set; }

    // ------------------------------------------------------------------ find

    public bool IsFindVisible => _findBar.IsVisible;
    internal TextBox FindBox => _findBox;
    internal string FindStatus => _findStatus.Text ?? "";

    /// <summary>Ctrl+F: shows the find bar with the selection (one line) as the query, and focuses it.</summary>
    public void ShowFind()
    {
        _findBar.IsVisible = true;
        _ = ShowFindAsync();
    }

    private async Task ShowFindAsync()
    {
        if (Control.Selection is not null && await Control.GetSelectionTextAsync().ConfigureAwait(true) is { Length: > 0 } text && !text.Contains('\n', StringComparison.Ordinal))
            _findBox.Text = text;
        _findBox.Focus();
        _findBox.SelectAll();
    }

    public void HideFind()
    {
        _findBar.IsVisible = false;
        _match = null;
        _findStatus.Text = "";
        Control.Focus();
    }

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        switch (e.Key)
        {
            case Key.Enter or Key.F3:
                // Enter goes up the output (VS Code), F3 down; Shift reverses either
                _ = FindAsync(backward: e.Key == Key.Enter ? !shift : shift);
                e.Handled = true;
                break;
            case Key.Escape:
                HideFind();
                e.Handled = true;
                break;
        }
    }

    /// <summary>The next match above (<paramref name="backward"/>) or below the current one (none yet: from the bottom), selected and scrolled into view.</summary>
    public async Task FindAsync(bool backward)
    {
        if (Control.Session is not { } session) return;
        var control = Control;
        var query = _findBox.Text ?? "";
        var matchCase = _matchCase.IsChecked == true;
        var seq = ++_findSeq;
        if (query.Length == 0)
        {
            _match = null;
            _findStatus.Text = "";
            return;
        }
        var (fromLine, fromColumn) = _match is { } m ? (m.Line, m.StartColumn) : backward ? (long.MaxValue, 0) : (long.MinValue, 0);
        TerminalMatch? found;
        try { found = await session.InvokeAsync(e => e.Find(query, fromLine, fromColumn, backward, matchCase)).ConfigureAwait(true); }
        catch (ObjectDisposedException) { return; }
        catch (TaskCanceledException) { return; }
        if (seq != _findSeq || IsDisposed) return; // typed on meanwhile
        _match = found;
        if (found is not { } match)
        {
            _findStatus.Text = "No results";
            return;
        }
        _findStatus.Text = "";
        control.ShowMatch(match);
    }

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
        _runtime.WatchWindowClosing(TopLevel.GetTopLevel(this));
        // Opening the Terminal window with no terminal creates one, as VS Code does.
        if (_runtime.Service.Terminals.Count == 0) _ = CreateFirstAsync();
        if (_runtime.TakeFocusRequest()) FocusTerminal();
    }

    private async Task CreateFirstAsync()
    {
        try { await _runtime.Service.CreateAsync(new TerminalCreateOptions { Show = false }); }
        catch (ObjectDisposedException) { }
    }

    // ------------------------------------------------------------------ groups and panes

    /// <summary>
    /// Rebuilds the dropdown (one entry per group: "1: bash", "2: bash, zsh"), shows the active terminal's group as panes (reusing the panes
    /// of terminals still shown) and updates the window title with the count.
    /// </summary>
    private void RefreshList()
    {
        if (IsDisposed) return;
        var service = _runtime.Service;
        var groups = service.Groups;
        var shown = service.ActiveGroup ?? groups.FirstOrDefault();
        static string Name(TerminalSession s) => s.Title + (s.HasExited ? $" (exited {s.ExitCode})" : "");
        static string Label(int index, TerminalGroup g) => $"{index + 1}: " + string.Join(", ", g.Terminals.Select(Name));
        var items = _picker.Items.OfType<ComboBoxItem>().ToList();
        if (items.Count == groups.Count && items.Select(i => i.Tag).SequenceEqual(groups))
        {
            // the same groups (a title changed, a program exited, a split): relabel in place, no new controls
            var selected = items.FirstOrDefault(i => ReferenceEquals(i.Tag, shown));
            var selectedRelabelled = false;
            for (var i = 0; i < items.Count; i++)
            {
                if (Equals(items[i].Content, Label(i, groups[i]))) continue;
                items[i].Content = Label(i, groups[i]);
                selectedRelabelled |= ReferenceEquals(items[i], selected);
            }
            // the closed box shows a copy of the selected item's content taken when it was selected: select it again to refresh it
            if (selectedRelabelled && ReferenceEquals(_picker.SelectedItem, selected)) _picker.SelectedItem = null;
            if (!ReferenceEquals(_picker.SelectedItem, selected)) _picker.SelectedItem = selected;
        }
        else
        {
            _picker.Items.Clear();
            for (var i = 0; i < groups.Count; i++)
            {
                var item = new ComboBoxItem { Content = Label(i, groups[i]), Tag = groups[i] };
                _picker.Items.Add(item);
                if (ReferenceEquals(groups[i], shown)) _picker.SelectedItem = item;
            }
        }
        _picker.IsVisible = groups.Count > 0;
        var focusWasInside = _panes.Any(p => p.Control.IsKeyboardFocusWithin);
        SyncPanes(shown);
        if (focusWasInside && !Control.IsKeyboardFocusWithin) FocusTerminal(); // a pane closed or the group changed under the keyboard
        _runtime.SetSplitPaneActive(_panes.Count > 1);
        var count = service.Terminals.Count;
        var title = count > 1 ? $"Terminal ({count})" : "Terminal";
        if (Dockable() is { } dockable && dockable.Title != title) dockable.Title = title;
    }

    /// <summary>The group's terminals as panes, left to right: panes of terminals still in it are kept, the rest disposed, new ones created.</summary>
    private void SyncPanes(TerminalGroup? group)
    {
        var sessions = group?.Terminals ?? [];
        var same = sessions.Count == 0 ? _panes is [{ Session: null }] : _panes.Select(p => p.Session).SequenceEqual(sessions);
        if (ReferenceEquals(group, _shownGroup) && same)
        {
            UpdateAltBuffer();
            return;
        }
        _shownGroup = group;
        var keep = new List<TerminalPane>();
        foreach (var s in sessions)
            keep.Add(_panes.FirstOrDefault(p => ReferenceEquals(p.Session, s)) ?? NewPane(s));
        foreach (var old in _panes.Where(p => !keep.Contains(p)).ToList())
        {
            if (old.Control.IsKeyboardFocusWithin) old.Control.ReleaseFocus();
            old.Control.FrameDrawn -= OnPaneFrameDrawn;
            old.Dispose();
        }
        _panes.Clear();
        _panes.AddRange(keep);
        if (_panes.Count == 0) _panes.Add(NewPane(null));
        LayoutPanes(group);
        UpdateAltBuffer();
    }

    private TerminalPane NewPane(TerminalSession? session)
    {
        var pane = new TerminalPane(_runtime, session);
        pane.Control.FrameDrawn += OnPaneFrameDrawn;
        return pane;
    }

    /// <summary>Panes in star columns (the group's remembered weights, else equal) with a splitter between each pair.</summary>
    private void LayoutPanes(TerminalGroup? group)
    {
        _panesHost.Children.Clear();
        _panesHost.ColumnDefinitions.Clear();
        var weights = group?.PaneWeights is { } w && w.Length == _panes.Count ? w : null;
        for (var i = 0; i < _panes.Count; i++)
        {
            if (i > 0)
            {
                _panesHost.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                var splitter = new GridSplitter
                {
                    Width = 4, ResizeDirection = GridResizeDirection.Columns, Background = ThemeBrushes.Get("panel.border"),
                    Focusable = false,
                };
                splitter.DragCompleted += (_, _) => RememberWeights();
                Grid.SetColumn(splitter, _panesHost.ColumnDefinitions.Count - 1);
                _panesHost.Children.Add(splitter);
            }
            _panesHost.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(weights?[i] ?? 1, GridUnitType.Star)) { MinWidth = MinPaneWidth });
            Grid.SetColumn(_panes[i], _panesHost.ColumnDefinitions.Count - 1);
            _panesHost.Children.Add(_panes[i]);
        }
    }

    private ColumnDefinition PaneColumn(int index) => _panesHost.ColumnDefinitions[index * 2];

    private void RememberWeights()
    {
        if (_shownGroup is null || _panes.Count < 2) return;
        _shownGroup.PaneWeights = Enumerable.Range(0, _panes.Count).Select(i => Math.Max(1, PaneColumn(i).ActualWidth)).ToArray();
        for (var i = 0; i < _panes.Count; i++) PaneColumn(i).Width = new GridLength(_shownGroup.PaneWeights[i], GridUnitType.Star);
    }

    /// <summary>
    /// VS Code's Resize Pane Left/Right: the divider next to the active pane (its right one; the last pane's left one) moves four cells that
    /// way, never below the minimum width.
    /// </summary>
    public void ResizeActivePane(int direction)
    {
        if (_panes.Count < 2 || _shownGroup is null) return;
        var index = _panes.IndexOf(ActivePane);
        if (index < 0) return;
        var widths = Enumerable.Range(0, _panes.Count).Select(i => PaneColumn(i).ActualWidth).ToArray();
        if (widths.Any(w => w <= 0)) return; // not laid out yet
        var amount = ResizeCells * ActivePane.Control.Font.CellWidth;
        var other = index < _panes.Count - 1 ? index + 1 : index - 1;
        // moving the divider right grows the pane on its left
        var leftPane = Math.Min(index, other);
        var rightPane = Math.Max(index, other);
        var delta = direction > 0 ? amount : -amount;
        delta = Math.Clamp(delta, MinPaneWidth - widths[leftPane], widths[rightPane] - MinPaneWidth);
        widths[leftPane] += delta;
        widths[rightPane] -= delta;
        _shownGroup.PaneWeights = widths;
        for (var i = 0; i < _panes.Count; i++) PaneColumn(i).Width = new GridLength(widths[i], GridUnitType.Star);
    }

    /// <summary>Focus Next/Previous Pane (VS Code): the neighbouring pane of the shown group, wrapping around.</summary>
    public void FocusPane(int delta)
    {
        if (_panes.Count < 2) return;
        var index = _panes.IndexOf(ActivePane);
        var next = _panes[((index + delta) % _panes.Count + _panes.Count) % _panes.Count];
        if (next.Session is { } s) _runtime.Service.SetActive(s);
        FocusTerminal();
    }

    private void OnPaneFrameDrawn(TerminalSnapshot snap) => UpdateAltBuffer();

    /// <summary><c>terminalAltBufferActive</c> follows the active pane's screen.</summary>
    private void UpdateAltBuffer()
    {
        var alt = ActivePane.Control.Snapshot?.Modes.AlternateScreen ?? false;
        if (alt == _altBuffer) return;
        _altBuffer = alt;
        _runtime.SetAltBuffer(alt);
    }

    /// <summary>Shows <paramref name="session"/> (it becomes the active terminal, its group the shown one).</summary>
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
        foreach (var p in _panes) p.Control.ReleaseFocus(); // before the view leaves the window: a focused control would stay reachable from the focus machinery
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
        _subs.Dispose();
        foreach (var p in _panes)
        {
            p.Control.FrameDrawn -= OnPaneFrameDrawn;
            p.Dispose();
        }
        _panesHost.Children.Clear(); // the (disposed) panes stay listed: Control never changes after disposal
        _runtime.Detach(this);
        _runtime.SetAltBuffer(false);
        _runtime.SetSplitPaneActive(false);
    }
}
