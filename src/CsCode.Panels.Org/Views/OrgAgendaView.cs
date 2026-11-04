using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CsCode.Panels.Org.Engine;
using CsCode.Panels.Org.ViewModels;
using CsCode.Ui;
using CsCode.Ui.Controls;
using CsCode.Ui.Icons;
using CsCode.Ui.Menus;
using CsCode.Ui.Theming;

namespace CsCode.Panels.Org.Views;

/// <summary>
/// The Org Agenda window: Day / Week / To do, previous / Today / next, Capture, Notes, Refresh and more; the title of what is shown; a filter;
/// the agenda as one virtualised list (a header per day, then its entries: a check mark, the TODO state, priority and title, and under them the
/// time or why the entry is there, its file and tags, overdue and due entries in the error colour); below a splitter, the selected entry's notes.
/// Keys on the list follow org's agenda (docs/ORG.md): Enter open, Space done, t cycle, s schedule, Shift+D deadline, Shift+Left/Right a day
/// earlier/later, , + - priority, : tags, n write notes, Shift+I / Shift+O clock in / out, Shift+W refile, $ archive, d w a views, f b . j
/// move, c capture, r refresh. A habit's row shows its consistency graph; the entry the clock runs on, the time so far. Disposing disposes the
/// view-model.
/// </summary>
public sealed class OrgAgendaView : UserControl, IDisposable, IToolWindowFocus
{
    private readonly OrgAgendaViewModel _vm;
    private readonly Action<OrgAgendaViewModel> _release;
    private readonly OrgHost _host;
    private readonly ListBox _list;
    private readonly Button _day, _week, _todo;
    private readonly DispatcherTimer _clock;
    private bool _disposed;

    public OrgAgendaView(OrgAgendaViewModel vm, Action<OrgAgendaViewModel> release, OrgHost host)
    {
        _vm = vm;
        _release = release;
        _host = host;
        DataContext = vm;

        // ---- toolbar: views, moving in time, actions
        _day = ModeButton("Day", "Day view (D)", OrgAgendaMode.Day);
        _week = ModeButton("Week", "Week view (W)", OrgAgendaMode.Week);
        _todo = ModeButton("To do", "Every entry still to do (A)", OrgAgendaMode.Todo);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Children = { _day, _week, _todo } };
        var previous = IconButton("chevron-left", "Previous (B)", () => { _vm.Previous(); return Task.CompletedTask; });
        var today = new Button { Content = new TextBlock { Text = "Today", FontSize = 12 }, Padding = new Thickness(6, 2), Margin = new Thickness(0, 0, 1, 0) };
        today.Classes.Add("flat");
        ToolTip.SetTip(today, "Go to today (.)");
        today.Click += (_, _) => _vm.GoToday();
        var next = IconButton("chevron-right", "Next (F)", () => { _vm.Next(); return Task.CompletedTask; });
        var moves = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), Children = { previous, today, next } };
        moves.Bind(IsVisibleProperty, new Binding(nameof(OrgAgendaViewModel.IsTodo)) { Converter = Avalonia.Data.Converters.BoolConverters.Not });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(IconButton(IconKey.Add, "Capture a task, note or event (C)", () => _vm.CaptureAsync()));
        actions.Children.Add(IconButton("note", "Open the notes file", _vm.OpenNotesFileAsync));
        actions.Children.Add(IconButton(IconKey.Refresh, "Refresh (R)", _vm.RefreshAsync));
        var more = IconButton(IconKey.Ellipsis, "More Actions...", () => Task.CompletedTask);
        more.Click += (_, _) => ContextMenus.Open(MoreMenu(), more, accessibleName: "Org Agenda actions");
        actions.Children.Add(more);

        var toolbar = new DockPanel { Margin = new Thickness(6, 4, 4, 0) };
        DockPanel.SetDock(actions, Avalonia.Controls.Dock.Right);
        toolbar.Children.Add(actions);
        toolbar.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { modes, moves } });

        var title = new TextBlock { Margin = new Thickness(8, 4, 8, 2), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(OrgAgendaViewModel.Title)));

        var filter = new FilterBox { Watermark = "Filter (words; :tag)", Margin = new Thickness(6, 2, 6, 2) };
        filter.Bind(FilterBox.TextProperty, new Binding(nameof(OrgAgendaViewModel.Filter)) { Mode = BindingMode.TwoWay });

        // ---- the agenda
        _list = new ListBox
        {
            ItemsSource = vm.Rows,
            ItemTemplate = new FuncDataTemplate<OrgRow>((row, _) => row is null ? new Panel() : RowFor(row), supportsRecycling: false),
            SelectionMode = SelectionMode.Single,
            Background = Brushes.Transparent,
        };
        AutomationName(_list, "Agenda");
        _list.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, new Binding(nameof(OrgAgendaViewModel.Selected)) { Mode = BindingMode.TwoWay });
        _list.AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _list.DoubleTapped += (_, e) => { if (RowAt(e.Source) is OrgItemRow row) { e.Handled = true; _ = _vm.OpenAsync(row); } };
        _list.ContextRequested += OnContextRequested;
        ToolWindowFocus.KeepOnRows(_list);

        var emptyText = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = ThemeBrushes.Get("descriptionForeground") };
        emptyText.Bind(TextBlock.TextProperty, new Binding(nameof(OrgAgendaViewModel.EmptyText)));
        var capture = new Button { Content = "Capture...", Padding = new Thickness(10, 3) };
        capture.Classes.Add("accent");
        capture.Click += (_, _) => _ = _vm.CaptureAsync();
        var notes = new Button { Content = "Open Notes File", Padding = new Thickness(10, 3) };
        notes.Click += (_, _) => _ = _vm.OpenNotesFileAsync();
        var emptyButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Children = { capture, notes } };
        emptyButtons.Bind(IsVisibleProperty, new Binding(nameof(OrgAgendaViewModel.NoFiles)));
        var empty = new StackPanel { Margin = new Thickness(16), Spacing = 10, VerticalAlignment = VerticalAlignment.Top, Children = { emptyText, emptyButtons } };
        empty.Bind(IsVisibleProperty, new Binding(nameof(OrgAgendaViewModel.IsEmpty)));
        _list.Bind(IsVisibleProperty, new Binding(nameof(OrgAgendaViewModel.IsEmpty)) { Converter = Avalonia.Data.Converters.BoolConverters.Not });

        // ---- the selected entry and its notes
        var header = new SelectableTextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        header.Bind(TextBlock.TextProperty, new Binding(nameof(OrgAgendaViewModel.DetailsHeader)));
        var planning = new SelectableTextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrushes.Get("descriptionForeground") };
        planning.Bind(TextBlock.TextProperty, new Binding(nameof(OrgAgendaViewModel.DetailsPlanning)));
        var open = new Button { Content = "Open", Padding = new Thickness(8, 2) };
        open.Click += (_, _) => { if (_vm.SelectedItem is { } r) _ = _vm.OpenAsync(r); };
        var write = new Button { Content = "Write Notes", Padding = new Thickness(8, 2) };
        ToolTip.SetTip(write, "Open the entry in the editor on a new line after its notes (N)");
        write.Click += (_, _) => { if (_vm.SelectedItem is { } r) _ = _vm.WriteNotesAsync(r); };
        var detailButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0), Children = { open, write } };
        var body = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 4),
            MinHeight = 0,
        };
        AutomationName(body, "Notes");
        body.Bind(TextBox.TextProperty, new Binding(nameof(OrgAgendaViewModel.Details)) { Mode = BindingMode.OneWay });
        var detailsTop = new StackPanel { Spacing = 2, Children = { header, planning, detailButtons } };
        var details = new DockPanel { Margin = new Thickness(8, 4, 8, 4) };
        DockPanel.SetDock(detailsTop, Avalonia.Controls.Dock.Top);
        details.Children.Add(detailsTop);
        details.Children.Add(body);

        var splitter = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Background = ThemeBrushes.Get("panel.border"), ResizeDirection = GridResizeDirection.Rows };
        var bodyGrid = new Grid { RowDefinitions = new RowDefinitions("3*,Auto,2*") };
        bodyGrid.Children.Add(new Panel { Children = { _list, empty } });
        Grid.SetRow(splitter, 1);
        bodyGrid.Children.Add(splitter);
        Grid.SetRow(details, 2);
        bodyGrid.Children.Add(details);
        details.Bind(IsVisibleProperty, new Binding(nameof(OrgAgendaViewModel.HasDetails)));
        splitter.Bind(IsVisibleProperty, new Binding(nameof(OrgAgendaViewModel.HasDetails)));

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), Background = ThemeBrushes.Get("sideBar.background") };
        root.Children.Add(toolbar);
        Grid.SetRow(title, 1);
        root.Children.Add(title);
        Grid.SetRow(filter, 2);
        root.Children.Add(filter);
        Grid.SetRow(bodyGrid, 3);
        root.Children.Add(bodyGrid);
        Content = root;

        vm.PropertyChanged += OnVmChanged;
        UpdateModeButtons();
        // past midnight the agenda's today moves on
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _clock.Tick += (_, _) => _vm.CheckDate();
        _clock.Start();
    }

    internal OrgAgendaViewModel Model => _vm;
    internal ListBox List => _list;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OrgAgendaViewModel.Mode)) UpdateModeButtons();
        if (e.PropertyName == nameof(OrgAgendaViewModel.Selected) && _vm.Selected is { } s)
            Dispatcher.UIThread.Post(() => { if (!_disposed) _list.ScrollIntoView(s); }, DispatcherPriority.Background);
    }

    private void UpdateModeButtons()
    {
        foreach (var (button, mode) in new[] { (_day, OrgAgendaMode.Day), (_week, OrgAgendaMode.Week), (_todo, OrgAgendaMode.Todo) })
        {
            var on = _vm.Mode == mode;
            button.Background = on ? ThemeBrushes.Get("inputOption.activeBackground") : Brushes.Transparent;
            button.BorderBrush = on ? ThemeBrushes.Get("inputOption.activeBorder") : Brushes.Transparent;
        }
    }

    private Button ModeButton(string text, string tip, OrgAgendaMode mode)
    {
        var b = new Button { Content = new TextBlock { Text = text, FontSize = 12 }, Padding = new Thickness(7, 2), BorderThickness = new Thickness(1) };
        b.Classes.Add("flat");
        ToolTip.SetTip(b, tip);
        AutomationName(b, text);
        b.Click += (_, _) => _vm.Mode = mode;
        return b;
    }

    private static Button IconButton(string icon, string tip, Func<Task> onClick)
    {
        var b = new Button { Content = new Icon(icon, 14) { Foreground = ThemeBrushes.Get("foreground") }, Padding = new Thickness(4), Margin = new Thickness(0, 0, 1, 0) };
        b.Classes.Add("flat");
        ToolTip.SetTip(b, tip);
        AutomationName(b, tip);
        b.Click += (_, _) => _ = onClick();
        return b;
    }

    private static void AutomationName(Control c, string name) => Avalonia.Automation.AutomationProperties.SetName(c, name);

    // ------------------------------------------------------------------------------------------------ rows

    private Control RowFor(OrgRow row) => row switch
    {
        OrgHeaderRow h => HeaderFor(h),
        OrgEmptyRow e => new TextBlock { Text = e.Text, FontSize = 11, FontStyle = FontStyle.Italic, Margin = new Thickness(28, 1, 6, 3), Foreground = ThemeBrushes.Get("descriptionForeground") },
        OrgItemRow i => ItemFor(i),
        _ => new Panel(),
    };

    private static Control HeaderFor(OrgHeaderRow h)
    {
        var title = new TextBlock
        {
            Text = h.Title,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrushes.Get(h.IsToday ? "textLink.foreground" : "foreground"),
        };
        var count = new TextBlock { Text = h.Detail ?? "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrushes.Get("descriptionForeground"), Margin = new Thickness(6, 0, 4, 0) };
        var panel = new DockPanel { Margin = new Thickness(4, 6, 0, 1), Height = 20 };
        DockPanel.SetDock(count, Avalonia.Controls.Dock.Right);
        panel.Children.Add(count);
        panel.Children.Add(title);
        return panel;
    }

    private Control ItemFor(OrgItemRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(4, 2, 4, 3), Background = Brushes.Transparent };
        ToolTip.SetShowDelay(grid, 700);
        ToolTip.SetTip(grid, row.Tooltip);

        if (row.HasKeyword)
        {
            var check = new Icon(row.IsDone ? "pass-filled" : "circle", 15)
            {
                Foreground = ThemeBrushes.Get(row.IsDone ? "gitDecoration.addedResourceForeground" : "descriptionForeground"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 0, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(check, row.IsDone ? "Mark as to do (Space)" : "Mark as done (Space)");
            check.PointerPressed += (_, e) => { e.Handled = true; _ = _vm.ToggleDoneAsync(row); };
            Grid.SetRowSpan(check, 2);
            grid.Children.Add(check);
        }

        var line = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var inlines = line.Inlines ??= [];
        if (row.Keyword is { } keyword)
        {
            inlines.Add(new Run(keyword) { FontWeight = FontWeight.Bold, FontSize = 11, Foreground = ThemeBrushes.Get(row.KeywordColorKey) });
            inlines.Add(new Run(" "));
        }
        if (row.HasPriority)
        {
            inlines.Add(new Run(row.Priority) { FontSize = 11, Foreground = ThemeBrushes.Get("list.warningForeground") });
            inlines.Add(new Run(" "));
        }
        var titleRun = new Run(row.Title);
        if (row.IsDone)
        {
            titleRun.Foreground = ThemeBrushes.Get("descriptionForeground");
            titleRun.TextDecorations = TextDecorations.Strikethrough;
        }
        inlines.Add(titleRun);
        Grid.SetColumn(line, 1);
        grid.Children.Add(line);

        var detail = new TextBlock { Text = row.Detail, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ThemeBrushes.Get(row.DetailColorKey), VerticalAlignment = VerticalAlignment.Center };
        Control second = detail;
        if (row.ClockText is { } clocked)
        {
            // the running clock: its time before the rest
            var clock = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            clock.Children.Add(new Icon("watch", 11) { Foreground = ThemeBrushes.Get("textLink.foreground"), VerticalAlignment = VerticalAlignment.Center });
            clock.Children.Add(new TextBlock { Text = clocked + " ·", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = ThemeBrushes.Get("textLink.foreground"), VerticalAlignment = VerticalAlignment.Center });
            clock.Children.Add(detail);
            ToolTip.SetTip(clock, "The clock is running on this entry (Shift+O: clock out)");
            second = clock;
        }
        Grid.SetColumn(second, 1);
        Grid.SetRow(second, 1);
        grid.Children.Add(second);
        if (row.Habit is { } habit)
        {
            var graph = HabitGraph(habit);
            Grid.SetColumn(graph, 1);
            Grid.SetRow(graph, 2);
            grid.Children.Add(graph);
        }
        return grid;
    }

    /// <summary>
    /// Org-habit's consistency graph: a cell per day (three weeks back, a week ahead), blue before it was due, green while it may be done,
    /// yellow on its last day, red after; a dot on the days it was done; today outlined; days to come faded.
    /// </summary>
    private static Control HabitGraph(IReadOnlyList<OrgHabitDay> days)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Margin = new Thickness(0, 3, 0, 1) };
        foreach (var day in days)
        {
            var color = day.State switch
            {
                OrgHabitState.Ahead => "textLink.foreground",
                OrgHabitState.OnTime => "gitDecoration.addedResourceForeground",
                OrgHabitState.Due => "list.warningForeground",
                _ => "list.errorForeground",
            };
            var cell = new Border
            {
                Width = 7,
                Height = 11,
                CornerRadius = new CornerRadius(1),
                Background = ThemeBrushes.Get(color),
                Opacity = day.IsFuture ? 0.35 : 0.85,
                BorderThickness = new Thickness(day.IsToday ? 1 : 0),
                BorderBrush = ThemeBrushes.Get("foreground"),
            };
            if (day.Done) cell.Child = new Ellipse { Width = 3, Height = 3, Fill = ThemeBrushes.Get("editor.background"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(cell, day.Date.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture) + (day.Done ? ": done" : "") + (day.IsToday ? " (today)" : ""));
            panel.Children.Add(cell);
        }
        return panel;
    }

    private static OrgRow? RowAt(object? source)
    {
        for (var v = source as Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
            if (v is ListBoxItem item) return item.DataContext as OrgRow;
        return null;
    }

    // ------------------------------------------------------------------------------------------------ keys (org's agenda keys)

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if ((e.KeyModifiers & (command | KeyModifiers.Alt)) != 0 && !(e.Key == Key.C && e.KeyModifiers == command)) return;
        var row = _vm.Selected as OrgItemRow;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var symbol = e.KeySymbol;
        Func<Task>? run = (e.Key, symbol) switch
        {
            (Key.C, _) when e.KeyModifiers == command && row is not null => () => Copy(row.Title),
            (Key.Enter, _) when row is not null => () => _vm.OpenAsync(row),
            (Key.Space, _) when row?.HasKeyword == true => () => _vm.ToggleDoneAsync(row),
            (Key.Right, _) when shift && row is not null => () => _vm.ShiftDateAsync(row, +1),
            (Key.Left, _) when shift && row is not null => () => _vm.ShiftDateAsync(row, -1),
            (_, "t") when row is not null => () => _vm.CycleTodoAsync(row),
            (_, "T") when row is not null => () => _vm.SetStateAsync(row),
            (_, "s" or ">") when row is not null => () => _vm.PlanAsync(row, OrgPlanningKind.Scheduled),
            (_, "D") when row is not null => () => _vm.PlanAsync(row, OrgPlanningKind.Deadline),
            (_, ",") when row is not null => () => _vm.PriorityAsync(row),
            (_, "+" or "=") when row is not null => () => _vm.ShiftPriorityAsync(row, up: true),
            (_, "-") when row is not null => () => _vm.ShiftPriorityAsync(row, up: false),
            (_, ":") when row is not null => () => _vm.TagsAsync(row),
            (_, "n") when row is not null => () => _vm.WriteNotesAsync(row),
            (_, "o") when row is not null => () => _vm.OpenAsync(row, preserveFocus: true),
            (_, "I") when row is not null => () => _vm.ClockInAsync(row),
            (_, "O") => _vm.ClockOutAsync,
            (_, "W") when row is not null => () => _vm.RefileAsync(row),
            (_, "$") when row is not null => () => _vm.ArchiveAsync(row),
            (_, "d") => Do(() => _vm.Mode = OrgAgendaMode.Day),
            (_, "w") => Do(() => _vm.Mode = OrgAgendaMode.Week),
            (_, "a") => Do(() => _vm.Mode = OrgAgendaMode.Todo),
            (_, "f") => Do(_vm.Next),
            (_, "b") => Do(_vm.Previous),
            (_, ".") => Do(_vm.GoToday),
            (_, "j") => _vm.JumpAsync,
            (_, "r" or "g") => _vm.RefreshAsync,
            (_, "c" or "k") => () => _vm.CaptureAsync(),
            _ => null,
        };
        if (run is null) return;
        e.Handled = true;
        _ = run();

        static Func<Task> Do(Action a) => () => { a(); return Task.CompletedTask; };
    }

    private async Task Copy(string text)
    {
        if (_host.Window is { } window) await window.CopyToClipboardAsync(text).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------------------------------------ menus

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var row = RowAt(e.Source) ?? _vm.Selected;
        if (row is null) return;
        e.Handled = true;
        _vm.Selected = row;
        var entries = row is OrgItemRow item ? MenuEntries(item) : DayEntries(row);
        if (entries.Count > 0) ContextMenus.Open(entries, _list, accessibleName: "Agenda entry");
    }

    internal IReadOnlyList<MenuEntry> MenuEntries(OrgItemRow row)
    {
        var today = _vm.Today;
        var kind = OrgAgendaViewModel.KindOf(row);
        var current = row.Heading.PlanningOf(kind);
        var entries = new List<MenuEntry>
        {
            new("Open", () => _vm.OpenAsync(row)) { Gesture = "Enter", Icon = "go-to-file" },
            new("Write Notes", () => _vm.WriteNotesAsync(row)) { Gesture = "N", Icon = "edit" },
            MenuEntry.Separator(),
        };
        if (row.HasKeyword) entries.Add(new MenuEntry(row.IsDone ? "Mark as To Do" : "Mark as Done", () => _vm.ToggleDoneAsync(row)) { Gesture = "Space", Icon = "check" });
        entries.Add(new MenuEntry("Set TODO State...", () => _vm.SetStateAsync(row)) { Gesture = "Shift+T" });
        entries.Add(MenuEntry.Separator());
        entries.Add(new MenuEntry("Schedule...", () => _vm.PlanAsync(row, OrgPlanningKind.Scheduled)) { Gesture = "S" });
        entries.Add(new MenuEntry("Deadline...", () => _vm.PlanAsync(row, OrgPlanningKind.Deadline)) { Gesture = "Shift+D" });
        entries.Add(new MenuEntry(kind == OrgPlanningKind.Deadline ? "Move Deadline" : "Reschedule")
        {
            Children =
            [
                new MenuEntry("Today", () => _vm.RescheduleAsync(row, today)),
                new MenuEntry("Tomorrow", () => _vm.RescheduleAsync(row, today.AddDays(1))),
                new MenuEntry("Next Monday", () => _vm.RescheduleAsync(row, today.AddDays(((int)DayOfWeek.Monday - (int)today.DayOfWeek + 6) % 7 + 1))),
                MenuEntry.Separator(),
                new MenuEntry("One Day Later", () => _vm.ShiftDateAsync(row, +1)) { Gesture = "Shift+Right", IsEnabled = current is not null },
                new MenuEntry("One Day Earlier", () => _vm.ShiftDateAsync(row, -1)) { Gesture = "Shift+Left", IsEnabled = current is not null },
            ],
        });
        if (row.Heading.Scheduled is not null) entries.Add(new MenuEntry("Remove Schedule", () => _vm.SetPlanningAsync(row, OrgPlanningKind.Scheduled, null)));
        if (row.Heading.Deadline is not null) entries.Add(new MenuEntry("Remove Deadline", () => _vm.SetPlanningAsync(row, OrgPlanningKind.Deadline, null)));
        entries.Add(MenuEntry.Separator());
        entries.Add(new MenuEntry("Priority...", () => _vm.PriorityAsync(row)) { Gesture = "," });
        entries.Add(new MenuEntry("Raise Priority", () => _vm.ShiftPriorityAsync(row, up: true)) { Gesture = "+" });
        entries.Add(new MenuEntry("Lower Priority", () => _vm.ShiftPriorityAsync(row, up: false)) { Gesture = "-" });
        entries.Add(new MenuEntry("Tags...", () => _vm.TagsAsync(row)) { Gesture = ":", Icon = "tag" });
        entries.Add(MenuEntry.Separator());
        entries.Add(row.ClockText is null
            ? new MenuEntry("Clock In", () => _vm.ClockInAsync(row)) { Gesture = "Shift+I", Icon = "watch" }
            : new MenuEntry("Clock Out", _vm.ClockOutAsync) { Gesture = "Shift+O", Icon = "watch" });
        entries.Add(new MenuEntry("Refile...", () => _vm.RefileAsync(row)) { Gesture = "Shift+W" });
        entries.Add(new MenuEntry("Archive Subtree", () => _vm.ArchiveAsync(row)) { Gesture = "$", Icon = "archive" });
        entries.Add(MenuEntry.Separator());
        entries.Add(new MenuEntry("Copy Title", () => Copy(row.Title)) { Icon = "copy" });
        return entries;
    }

    private List<MenuEntry> DayEntries(OrgRow row)
    {
        var day = row switch { OrgHeaderRow { Date: { } d } => d, OrgEmptyRow e => e.Date, _ => (DateOnly?)null };
        if (day is null) return [];
        var entries = _vm.CaptureTemplates.Select(t => new MenuEntry($"Capture {t.Description} on This Day...", () => _vm.CaptureAsync(t.Key))).ToList();
        if (entries.Count > 0) entries[0] = new MenuEntry(entries[0].Header, entries[0].Run) { Icon = "add" };
        entries.Add(MenuEntry.Separator());
        entries.Add(new MenuEntry("Show This Day", () => { _vm.GoTo(day.Value); _vm.Mode = OrgAgendaMode.Day; return Task.CompletedTask; }));
        return entries;
    }

    private IReadOnlyList<MenuEntry> MoreMenu() =>
    [
        .. _vm.CaptureTemplates.Select(t => new MenuEntry($"Capture {t.Description}...", () => _vm.CaptureAsync(t.Key)) { Gesture = t.Key }),
        MenuEntry.Separator(),
        new MenuEntry("Clock Out", _vm.ClockOutAsync) { Gesture = "Shift+O", Icon = "watch", IsEnabled = _vm.ClockRuns },
        MenuEntry.Separator(),
        new MenuEntry("Go to Date...", _vm.JumpAsync) { Gesture = "J" },
        new MenuEntry("Open Notes File", _vm.OpenNotesFileAsync),
        new MenuEntry("Add Active File to Agenda", () => Run(OrgHost.AddToAgenda)),
        new MenuEntry("Org Settings", () => Run(Core.Platform.KnownCommands.OpenSettings, "org.")) { Icon = "settings-gear" },
        MenuEntry.Separator(),
        new MenuEntry("Refresh", _vm.RefreshAsync) { Gesture = "R", Icon = IconKey.Refresh },
    ];

    private async Task Run(string command, params object?[] args)
    {
        if (_host.Commands is { } commands && commands.Has(command)) await commands.ExecuteAsync(command, args).ConfigureAwait(true);
    }

    /// <summary>View &gt; Tool Windows &gt; Org Agenda: the keyboard on the selected (else the first visible) row, not the toolbar.</summary>
    public bool FocusPreferred() => ToolWindowFocus.FocusRow(_list);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clock.Stop();
        _vm.PropertyChanged -= OnVmChanged;
        _list.ItemsSource = null;
        DataContext = null;
        _release(_vm);
    }
}
