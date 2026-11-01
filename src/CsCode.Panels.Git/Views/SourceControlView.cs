using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CsCode.Core.Scm;
using CsCode.Panels.Git.ViewModels;
using CsCode.Ui;
using CsCode.Ui.Icons;
using CsCode.Ui.Menus;
using CsCode.Ui.Theming;

namespace CsCode.Panels.Git.Views;

/// <summary>
/// The Source Control window (Visual Studio's Git Changes, VS Code's Source Control view): branch and sync actions, the commit message box with
/// Commit (Ctrl/Cmd+Enter) and Amend, then the Merge / Staged / Changes groups as one virtualised list. A row shows the file name, its folder
/// dimmed and its status letter in the git colour, with Open File, Discard, Stage/Unstage buttons; click opens the change in the diff editor.
/// Keys on a row: Enter (open the change), Space (stage/unstage), Delete (discard, after confirmation). Disposing disposes the view-model.
/// </summary>
public sealed class SourceControlView : UserControl, IDisposable, IToolWindowFocus
{
    private readonly SourceControlViewModel _vm;
    private readonly Action<SourceControlViewModel> _release;
    private readonly Core.Platform.IContextKeyService? _keys;
    private readonly ListBox _list;
    private readonly TextBox _message;
    private readonly Button _commit;
    private readonly TextBlock _branch, _sync, _info;
    private readonly ComboBox _repos;
    private readonly CheckBox _amend;
    private bool _disposed;

    public SourceControlView(SourceControlViewModel vm, Action<SourceControlViewModel> release, Core.Platform.IContextKeyService? keys = null)
    {
        _vm = vm;
        _release = release;
        _keys = keys;
        DataContext = vm;

        _branch = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _sync = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), Foreground = ThemeBrushes.Get("descriptionForeground"), TextTrimming = TextTrimming.CharacterEllipsis };
        var branchButton = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new Icon(IconKey.SourceControl, 14), _branch, new Icon(IconKey.ChevronDown, 12) } },
            Padding = new Thickness(4, 2), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        };
        ToolTip.SetTip(branchButton, "Switch or create a branch");
        branchButton.Click += (_, _) => _ = vm.RunCommandAsync(GitHost.Checkout);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(IconButton(IconKey.Sync, "Fetch", () => vm.RunCommandAsync(GitHost.Fetch)));
        actions.Children.Add(IconButton("arrow-down", "Pull", () => vm.RunCommandAsync(GitHost.Pull)));
        actions.Children.Add(IconButton("arrow-up", "Push", () => vm.RunCommandAsync(GitHost.Push)));
        actions.Children.Add(IconButton(IconKey.Refresh, "Refresh", vm.RefreshAsync));
        var more = IconButton(IconKey.Ellipsis, "More Actions...", () => Task.CompletedTask);
        more.Click += (_, _) => ContextMenus.Open(MoreMenu(), more, accessibleName: "Source Control actions");
        actions.Children.Add(more);
        var top = new DockPanel { Margin = new Thickness(6, 4, 4, 0) };
        DockPanel.SetDock(actions, Avalonia.Controls.Dock.Right);
        top.Children.Add(actions);
        top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { branchButton, _sync } });

        _repos = new ComboBox { Margin = new Thickness(6, 2), HorizontalAlignment = HorizontalAlignment.Stretch, ItemTemplate = new FuncDataTemplate<Engine.GitRepository>((r, _) => new TextBlock { Text = r?.Name, [ToolTip.TipProperty] = r?.Root }) };
        _repos.SelectionChanged += (_, _) => { if (_repos.SelectedItem is Engine.GitRepository r) vm.Repository = r; };

        _message = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxHeight = 160, Margin = new Thickness(6, 4, 6, 2) };
        Avalonia.Automation.AutomationProperties.SetName(_message, "Commit message");
        _message.Bind(TextBox.TextProperty, new Avalonia.Data.Binding(nameof(SourceControlViewModel.CommitMessage)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        _message.Bind(TextBox.PlaceholderTextProperty, new Avalonia.Data.Binding(nameof(SourceControlViewModel.Watermark)));
        // tunnelling: the text box itself would take Ctrl+Enter as a line break
        _message.AddHandler(KeyDownEvent, OnMessageKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _message.GotFocus += (_, _) => _keys?.Set(ScmContextKeys.ScmInputFocus, true);
        _message.LostFocus += (_, _) => _keys?.Set(ScmContextKeys.ScmInputFocus, false);

        _commit = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 2), Padding = new Thickness(8, 3) };
        _commit.Classes.Add("accent");
        _commit.Bind(ContentProperty, new Avalonia.Data.Binding(nameof(SourceControlViewModel.CommitLabel)));
        _commit.Bind(IsEnabledProperty, new Avalonia.Data.Binding(nameof(SourceControlViewModel.CanCommit)));
        _commit.Click += (_, _) => _ = vm.CommitAsync();
        ToolTip.SetTip(_commit, "Commit (Ctrl+Enter)");
        _amend = new CheckBox { Content = "Amend", Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
        _amend.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Avalonia.Data.Binding(nameof(SourceControlViewModel.Amend)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        var commitRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        commitRow.Children.Add(_commit);
        Grid.SetColumn(_amend, 1);
        commitRow.Children.Add(_amend);

        _info = new TextBlock { Margin = new Thickness(10, 8), TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrushes.Get("descriptionForeground") };
        _info.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(SourceControlViewModel.Message)));
        _info.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(SourceControlViewModel.Message)) { Converter = Avalonia.Data.Converters.StringConverters.IsNotNullOrEmpty });

        _list = new ListBox { ItemsSource = vm.Rows, SelectionMode = SelectionMode.Multiple, Background = Brushes.Transparent, ItemTemplate = new FuncDataTemplate<ScmRow>((r, _) => r is null ? new Panel() : RowFor(r), supportsRecycling: false) };
        Avalonia.Automation.AutomationProperties.SetName(_list, "Changes");
        _list.KeyDown += OnListKeyDown;
        _list.DoubleTapped += (_, e) => { if (RowOf(e.Source) is ScmFileRow f) _ = vm.OpenFileAsync(f); };
        _list.Tapped += (_, e) => { if (RowOf(e.Source) is ScmFileRow f && !IsInButton(e.Source)) _ = vm.OpenChangeAsync(f); };
        _list.ContextRequested += OnContextRequested;
        ToolWindowFocus.KeepOnRows(_list);

        var header = new StackPanel { Children = { top, _repos, _message, commitRow, _info } };
        var root = new DockPanel { Background = ThemeBrushes.Get("sideBar.background") };
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_list);
        Content = root;
        vm.PropertyChanged += OnVmChanged;
        Sync();
    }

    internal ListBox List => _list;
    internal TextBox MessageBox => _message;
    internal Button CommitButton => _commit;

    private static Button IconButton(string icon, string tip, Func<Task> run)
    {
        var b = new Button { Content = new Icon(icon, 14), Padding = new Thickness(3), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false };
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => _ = run();
        return b;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void Sync()
    {
        _branch.Text = _vm.BranchText;
        _sync.Text = _vm.SyncText;
        _repos.IsVisible = _vm.HasManyRepositories;
        if (!ReferenceEquals(_repos.ItemsSource, _vm.Repositories)) _repos.ItemsSource = _vm.Repositories;
        if (!ReferenceEquals(_repos.SelectedItem, _vm.Repository)) _repos.SelectedItem = _vm.Repository;
        var hasRepo = _vm.Repository is not null;
        _message.IsEnabled = hasRepo;
        _list.IsVisible = hasRepo;
    }

    // ------------------------------------------------------------------------------------- rows

    private Control RowFor(ScmRow row)
    {
        if (row is ScmGroupRow g)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Height = 22 };
            grid.Children.Add(new TextBlock { Text = g.Title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            if (g.Group == ScmGroup.Staged) buttons.Children.Add(IconButton(IconKey.Remove, "Unstage All Changes", _vm.UnstageAllAsync));
            if (g.Group == ScmGroup.Changes && g.Count > 0)
            {
                buttons.Children.Add(IconButton(IconKey.Undo, "Discard All Changes", _vm.DiscardAllAsync));
                buttons.Children.Add(IconButton(IconKey.Add, "Stage All Changes", _vm.StageAllAsync));
            }
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);
            var count = new Border
            {
                Background = ThemeBrushes.Get("badge.background"), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0), Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = g.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 11, Foreground = ThemeBrushes.Get("badge.foreground") },
            };
            Grid.SetColumn(count, 2);
            grid.Children.Add(count);
            return grid;
        }
        var f = (ScmFileRow)row;
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("18,Auto,*,Auto,16"), Height = 22, Background = Brushes.Transparent };
        ToolTip.SetTip(line, f.Tooltip);
        var icon = new Icon(IconKey.File, 14) { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = ThemeBrushes.Get("descriptionForeground") };
        line.Children.Add(icon);
        var name = new TextBlock { Text = f.FileName, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0), Foreground = ThemeBrushes.Get(f.ColorKey) };
        if (f.Letter == 'D') name.TextDecorations = TextDecorations.Strikethrough;
        Grid.SetColumn(name, 1);
        line.Children.Add(name);
        var folder = new TextBlock { Text = f.Folder, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = ThemeBrushes.Get("descriptionForeground"), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(folder, 2);
        line.Children.Add(folder);
        var buttons2 = new StackPanel { Orientation = Orientation.Horizontal };
        if (f.Entry.WorkTree != 'D') buttons2.Children.Add(IconButton("go-to-file", "Open File", () => _vm.OpenFileAsync(f)));
        if (f.IsStagedRow) buttons2.Children.Add(IconButton(IconKey.Remove, "Unstage Changes", () => _vm.UnstageAsync([f])));
        else
        {
            if (f.Group != ScmGroup.Merge) buttons2.Children.Add(IconButton(IconKey.Undo, "Discard Changes", () => _vm.DiscardAsync([f])));
            buttons2.Children.Add(IconButton(IconKey.Add, f.Group == ScmGroup.Merge ? "Stage (mark as resolved)" : "Stage Changes", () => _vm.StageAsync([f])));
        }
        Grid.SetColumn(buttons2, 3);
        line.Children.Add(buttons2);
        var letter = new TextBlock { Text = f.Letter.ToString(), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeight.SemiBold, Foreground = ThemeBrushes.Get(f.ColorKey) };
        Grid.SetColumn(letter, 4);
        line.Children.Add(letter);
        return line;
    }

    private static ScmRow? RowOf(object? source)
    {
        for (var v = source as Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
            if (v is ListBoxItem item) return item.DataContext as ScmRow;
        return null;
    }

    private static bool IsInButton(object? source)
    {
        for (var v = source as Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
        {
            if (v is Button) return true;
            if (v is ListBoxItem) return false;
        }
        return false;
    }

    private IReadOnlyList<ScmFileRow> SelectedFiles(ScmFileRow? fallback = null)
    {
        var selected = _list.SelectedItems?.OfType<ScmFileRow>().ToList() ?? [];
        if (fallback is not null && !selected.Contains(fallback)) return [fallback];
        return selected;
    }

    // ------------------------------------------------------------------------------------- input

    private void OnMessageKeyDown(object? sender, KeyEventArgs e)
    {
        var commitKey = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key == Key.Enter && e.KeyModifiers == commitKey)
        {
            e.Handled = true;
            _ = _vm.CommitAsync();
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        var files = SelectedFiles();
        if (files.Count == 0) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                foreach (var f in files.Take(10)) _ = _vm.OpenChangeAsync(f);
                break;
            case Key.Space:
                e.Handled = true;
                var staged = files.Where(f => f.IsStagedRow).ToList();
                var unstaged = files.Where(f => !f.IsStagedRow).ToList();
                if (staged.Count > 0) _ = _vm.UnstageAsync(staged);
                if (unstaged.Count > 0) _ = _vm.StageAsync(unstaged);
                break;
            case Key.Delete or Key.Back when CommandKeys.IsDelete(e):
                e.Handled = true;
                var discardable = files.Where(f => f.Group == ScmGroup.Changes).ToList();
                if (discardable.Count > 0) _ = _vm.DiscardAsync(discardable);
                break;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (RowOf(e.Source) is not ScmFileRow row) return;
        e.Handled = true;
        ContextMenus.Open(MenuFor(row), _list, accessibleName: "Change menu");
    }

    internal IReadOnlyList<MenuEntry> MenuFor(ScmFileRow row)
    {
        var files = SelectedFiles(row);
        return MenuSections.Join(
            [
                new MenuEntry("Open _Changes", () => _vm.OpenChangeAsync(row)) { Icon = "diff" },
                new MenuEntry("Open _File", () => _vm.OpenFileAsync(row)) { Icon = "go-to-file", IsEnabled = row.Entry.WorkTree != 'D' },
            ],
            [
                row.IsStagedRow
                    ? new MenuEntry("_Unstage Changes", () => _vm.UnstageAsync(files)) { Icon = IconKey.Remove }
                    : new MenuEntry("_Stage Changes", () => _vm.StageAsync(files)) { Icon = IconKey.Add },
                row.Group == ScmGroup.Changes ? new MenuEntry("_Discard Changes", () => _vm.DiscardAsync(files)) { Icon = IconKey.Undo } : null,
            ]);
    }

    private IReadOnlyList<MenuEntry> MoreMenu() => MenuSections.Join(
        [
            new MenuEntry("_Commit", () => _vm.CommitAsync()),
            new MenuEntry("Commit (_Amend)", () => _vm.RunCommandAsync(GitHost.CommitAmend)),
        ],
        [
            new MenuEntry("_Pull", () => _vm.RunCommandAsync(GitHost.Pull)),
            new MenuEntry("P_ush", () => _vm.RunCommandAsync(GitHost.Push)),
            new MenuEntry("_Fetch", () => _vm.RunCommandAsync(GitHost.Fetch)),
            new MenuEntry("S_ync", () => _vm.RunCommandAsync(GitHost.Sync)),
        ],
        [
            new MenuEntry("Check_out to...", () => _vm.RunCommandAsync(GitHost.Checkout)),
            new MenuEntry("Create _Branch...", () => _vm.RunCommandAsync(GitHost.Branch)),
        ],
        [
            new MenuEntry("Stage All Changes", _vm.StageAllAsync),
            new MenuEntry("Unstage All Changes", _vm.UnstageAllAsync),
            new MenuEntry("Discard All Changes", _vm.DiscardAllAsync),
        ],
        [new MenuEntry("Show Git _Output", () => _vm.RunCommandAsync(GitHost.ShowOutput))]);

    /// <summary>View &gt; Tool Windows &gt; Source Control: the keyboard in the commit message box, where it is wanted most (VS Code focuses it too).</summary>
    public bool FocusPreferred() => _message.IsEffectivelyEnabled && _message.Focus() || ToolWindowFocus.FocusRow(_list);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _keys?.Set(ScmContextKeys.ScmInputFocus, false);
        _vm.PropertyChanged -= OnVmChanged;
        _list.ItemsSource = null;
        _repos.ItemsSource = null;
        DataContext = null;
        _release(_vm);
    }
}
