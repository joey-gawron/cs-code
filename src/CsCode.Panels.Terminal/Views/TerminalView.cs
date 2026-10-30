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
    private readonly ToggleButton _wholeWord;
    private readonly ToggleButton _regex;
    private readonly TextBlock _findStatus;
    private readonly TextBlock _announcer;
    private readonly TextBox _accessibleBuffer;
    private TerminalAccessibleBuffer? _buffer;
    private int _highlightSeq;
    private bool _highlightPending;
    private (long ViewportTop, long Sequence, int Rows) _highlighted;
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

        // Find (VS Code's terminal find widget): Enter searches up the output, Shift+Enter down, Escape closes; Alt+C/W/R toggle the options
        _findBox = new TextBox { Width = 220, MinHeight = 22, Padding = new Thickness(4, 1), PlaceholderText = "Find", VerticalContentAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(_findBox, "Find in terminal");
        _findBox.KeyDown += OnFindKeyDown;
        _findBox.TextChanged += (_, _) => { _match = null; _ = FindAsync(backward: true); };
        _matchCase = FindToggle("case-sensitive", "Match Case (Alt+C)");
        _wholeWord = FindToggle("whole-word", "Match Whole Word (Alt+W)");
        _regex = FindToggle("regex", "Use Regular Expression (Alt+R)");
        _findStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), MinWidth = 70 };
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_findStatus, Avalonia.Automation.AutomationLiveSetting.Polite);
        var findRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 2), HorizontalAlignment = HorizontalAlignment.Right };
        findRow.Children.Add(_findBox);
        findRow.Children.Add(_matchCase);
        findRow.Children.Add(_wholeWord);
        findRow.Children.Add(_regex);
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

        // screen readers: a polite live region announcing finished commands (no height: it is never seen)
        _announcer = new TextBlock { Height = 0, ClipToBounds = true, Focusable = false };
        Avalonia.Automation.AutomationProperties.SetName(_announcer, "Terminal announcements");
        Avalonia.Automation.AutomationProperties.SetLiveSetting(_announcer, Avalonia.Automation.AutomationLiveSetting.Polite);
        SetDock(_announcer, DockSide.Top);
        Children.Add(_announcer);

        // the accessible buffer (VS Code's Alt+F2): the whole scrollback as read-only text over the panes
        _accessibleBuffer = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.NoWrap, IsVisible = false,
            FontFamily = runtime.CreateFont().Family,
            Background = ThemeBrushes.Get("terminal.background"), Foreground = ThemeBrushes.Get("terminal.foreground"),
        };
        Avalonia.Automation.AutomationProperties.SetName(_accessibleBuffer, "Terminal buffer");
        _accessibleBuffer.AddHandler(KeyDownEvent, OnAccessibleBufferKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var content = new Panel();
        content.Children.Add(_panesHost);
        content.Children.Add(_accessibleBuffer);
        Children.Add(content);

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
        _findSeq++;
        _findStatus.Text = "";
        foreach (var p in _panes) p.Control.SetFindMatches(null, null);
        Control.Focus();
    }

    private ToggleButton FindToggle(string icon, string tip)
    {
        var toggle = new ToggleButton { Content = new Icon(icon, 16), Padding = new Thickness(3), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        ToolTip.SetTip(toggle, tip);
        Avalonia.Automation.AutomationProperties.SetName(toggle, tip[..tip.IndexOf(" (", StringComparison.Ordinal)]);
        toggle.IsCheckedChanged += (_, _) => { _match = null; _ = FindAsync(backward: true); };
        return toggle;
    }

    /// <summary>What the find bar looks for.</summary>
    public TerminalFindOptions FindOptions => new(_findBox.Text ?? "", _matchCase.IsChecked == true, _wholeWord.IsChecked == true, _regex.IsChecked == true);

    internal ToggleButton MatchCaseToggle => _matchCase;
    internal ToggleButton WholeWordToggle => _wholeWord;
    internal ToggleButton RegexToggle => _regex;

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;
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
            case Key.C or Key.W or Key.R when alt:
                var toggle = e.Key switch { Key.C => _matchCase, Key.W => _wholeWord, _ => _regex };
                toggle.IsChecked = toggle.IsChecked != true;
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// The next match above (<paramref name="backward"/>) or below the current one (none yet: from the bottom), selected and scrolled into
    /// view; then "N of M" and every match in view highlighted. The searching runs on the terminal's worker.
    /// </summary>
    public async Task FindAsync(bool backward)
    {
        if (Control.Session is not { } session) return;
        var control = Control;
        var options = FindOptions;
        var seq = ++_findSeq;
        if (options.Query.Length == 0)
        {
            _match = null;
            _findStatus.Text = "";
            control.SetFindMatches(null, null);
            return;
        }
        if (options.IsInvalid)
        {
            _match = null;
            _findStatus.Text = "Invalid regular expression";
            control.SetFindMatches(null, null);
            return;
        }
        var (fromLine, fromColumn) = _match is { } m ? (m.Line, m.StartColumn) : backward ? (long.MaxValue, 0) : (long.MinValue, 0);
        (TerminalMatch? Found, TerminalFindCount Count) result;
        try
        {
            result = await session.InvokeAsync(e =>
            {
                var found = e.Find(options, fromLine, fromColumn, backward);
                return (found, found is null ? default : e.CountMatches(options, found));
            }).ConfigureAwait(true);
        }
        catch (ObjectDisposedException) { return; }
        catch (TaskCanceledException) { return; }
        if (seq != _findSeq || IsDisposed) return; // typed on meanwhile
        _match = result.Found;
        if (result.Found is not { } match)
        {
            _findStatus.Text = "No results";
            control.SetFindMatches(null, null);
            return;
        }
        var count = result.Count;
        _findStatus.Text = count.Total == 0 ? "" : $"{(count.Index > 0 ? count.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?")} of {count.Total}{(count.Capped ? "+" : "")}";
        control.ShowMatch(match);
        _highlighted = default;
        await RefreshHighlightsAsync().ConfigureAwait(true);
    }

    /// <summary>The status line of the find bar ("3 of 12", "No results").</summary>
    internal string FindCountText => _findStatus.Text ?? "";

    /// <summary>
    /// Highlights every match in view (the logical lines touching the visible rows), after the find, a scroll or new output; one request in
    /// flight at a time, the newest view wins.
    /// </summary>
    private async Task RefreshHighlightsAsync()
    {
        if (!_findBar.IsVisible || _match is null || Control is not { Session: { } session, Snapshot: { } snap } control) return;
        var key = (snap.ViewportTop, snap.Sequence, snap.RowCount);
        if (key == _highlighted) return;
        if (_highlightPending) return; // the running request re-checks when it is done
        _highlightPending = true;
        _highlighted = key;
        var options = FindOptions;
        var seq = ++_highlightSeq;
        var findSeq = _findSeq;
        try
        {
            var matches = await session.InvokeAsync(e => e.FindAll(options, snap.ViewportTop, snap.ViewportTop + snap.RowCount - 1, 2000)).ConfigureAwait(true);
            if (seq == _highlightSeq && findSeq == _findSeq && !IsDisposed && _findBar.IsVisible && ReferenceEquals(control.Session, session))
                control.SetFindMatches(matches, _match);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or TaskCanceledException) { }
        finally { _highlightPending = false; }
        // the view moved on while this ran
        if (!IsDisposed && Control.Snapshot is { } now && (now.ViewportTop, now.Sequence, now.RowCount) != _highlighted) _ = RefreshHighlightsAsync();
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
        if (_accessibleBuffer.IsVisible) HideAccessibleBuffer(); // it showed another terminal
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
        if (session is not null)
            pane.Subscription = session.CommandFinished.Subscribe(c => Dispatcher.UIThread.Post(() => Announce(session, c)));
        return pane;
    }

    // ------------------------------------------------------------------ screen readers

    /// <summary>What the live region said last (tests).</summary>
    internal string Announcement => _announcer.Text ?? "";

    /// <summary>
    /// A command of the active terminal finished (shell integration): the live region says so, with its exit code and the end of its output
    /// (a few lines), as VS Code's terminal reads finished commands to screen readers.
    /// </summary>
    private void Announce(TerminalSession session, TerminalCommandSummary summary)
    {
        if (IsDisposed || !ReferenceEquals(session, _runtime.Service.ActiveSession)) return;
        var c = summary.Command;
        var lines = summary.OutputTail.Split('\n');
        var tail = string.Join('\n', lines.Skip(Math.Max(0, lines.Length - 5)));
        if (tail.Length > 500) tail = tail[^500..];
        var head = c.ExitCode is { } code && code != 0 ? $"Command failed with exit code {code}" : "Command finished";
        _announcer.Text = $"{head}: {c.CommandLine ?? "(unknown command)"}" + (tail.Length > 0 ? $"\n{tail}" : "");
    }

    public bool IsAccessibleBufferVisible => _accessibleBuffer.IsVisible;
    internal TextBox AccessibleBuffer => _accessibleBuffer;

    /// <summary>
    /// VS Code's Focus Accessible Buffer (Alt+F2): the active terminal's scrollback and screen as read-only text a screen reader can walk line
    /// by line, the caret at the terminal's cursor; Ctrl+Up/Down jump between shell-integration prompts, Escape returns to the terminal.
    /// </summary>
    public async Task ShowAccessibleBufferAsync()
    {
        if (Control.Session is not { } session) return;
        TerminalAccessibleBuffer buffer;
        try { buffer = await session.GetAccessibleBufferAsync().ConfigureAwait(true); }
        catch (Exception ex) when (ex is ObjectDisposedException or TaskCanceledException) { return; }
        if (IsDisposed) return;
        _buffer = buffer;
        _accessibleBuffer.Text = buffer.Text;
        _accessibleBuffer.IsVisible = true;
        _accessibleBuffer.CaretIndex = buffer.CursorOffset >= 0 ? buffer.CursorOffset : buffer.Text.Length;
        _accessibleBuffer.Focus();
    }

    public void HideAccessibleBuffer()
    {
        if (!_accessibleBuffer.IsVisible) return;
        _accessibleBuffer.IsVisible = false;
        _accessibleBuffer.Text = "";
        _buffer = null;
        FocusTerminal();
    }

    private void OnAccessibleBufferKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideAccessibleBuffer();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down && e.KeyModifiers == KeyModifiers.Control && _buffer is { PromptOffsets.Count: > 0 } buffer)
        {
            // the previous / next command's prompt
            var caret = _accessibleBuffer.CaretIndex;
            int? target = e.Key == Key.Up ? buffer.PromptOffsets.LastOrDefault(p => p < caret, -1) : buffer.PromptOffsets.FirstOrDefault(p => p > caret, -1);
            if (target is >= 0 and var t) _accessibleBuffer.CaretIndex = t;
            e.Handled = true;
        }
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

    private void OnPaneFrameDrawn(TerminalSnapshot snap)
    {
        UpdateAltBuffer();
        if (_findBar.IsVisible && ReferenceEquals(snap, Control.Snapshot)) _ = RefreshHighlightsAsync();
    }

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
