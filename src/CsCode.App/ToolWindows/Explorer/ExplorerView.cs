using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CsCode.App.Menus;
using CsCode.App.Views;
using CsCode.Core.Contributions;
using CsCode.Core.Platform;
using CsCode.Ui.Controls;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;

namespace CsCode.App.ToolWindows.Explorer;

/// <summary>
/// The Explorer view: header actions, a filter box and a virtualised list of the model's flat visible rows. The view owns no tree logic;
/// it forwards clicks, keys and context-menu commands to <see cref="ExplorerViewModel"/>. Disposed (with its view-model) when the tool window hides.
/// </summary>
public sealed class ExplorerView : UserControl, IDisposable
{
    private readonly ExplorerViewModel _vm;
    private readonly IWindowService _window;
    private readonly IContributionRegistry? _contributions;
    private readonly ICommandService? _commands;
    private readonly IContextKeyService? _keys;
    private readonly ListBox _list;
    private string _typeAhead = "";
    private DateTime _typeAheadAt;

    public ExplorerView(ExplorerViewModel vm, IWindowService window, ICommandService? commands = null, IContextKeyService? keys = null, IContributionRegistry? contributions = null)
    {
        _vm = vm;
        _window = window;
        _commands = commands;
        _keys = keys;
        _contributions = contributions;
        DataContext = vm;

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(8, 4, 4, 2) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(ViewHelpers.IconButton(IconKey.NewFile, "New File", () => _ = CreateAsync(isFolder: false), 13));
        actions.Children.Add(ViewHelpers.IconButton(IconKey.NewFolder, "New Folder", () => _ = CreateAsync(isFolder: true), 13));
        actions.Children.Add(ViewHelpers.IconButton(IconKey.Refresh, "Refresh Explorer", () => _ = _vm.RefreshRootsAsync(), 13));
        actions.Children.Add(ViewHelpers.IconButton("collapse-all", "Collapse Folders in Explorer", () => { foreach (var r in _vm.Roots) foreach (var c in r.Children?.Where(c => c.IsExpanded).ToArray() ?? []) _vm.Collapse(c); }, 13));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);

        var filter = new FilterBox { Watermark = "Filter", Margin = new Thickness(6, 2, 6, 4) };
        filter.Bind(FilterBox.TextProperty, new Avalonia.Data.Binding(nameof(ExplorerViewModel.Filter)) { Mode = Avalonia.Data.BindingMode.TwoWay });

        _list = new ListBox { ItemsSource = vm.Rows, ItemTemplate = new FuncDataTemplate<ExplorerNode>((n, _) => n is null ? new Panel() : RowFor(n), supportsRecycling: false), SelectionMode = SelectionMode.Single };
        _list.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, new Avalonia.Data.Binding(nameof(ExplorerViewModel.Selected)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        _list.KeyDown += OnKeyDown;
        _list.TextInput += OnTextInput;
        _list.DoubleTapped += (_, e) => { if (_list.SelectedItem is ExplorerNode n && !n.IsDirectory) _ = _vm.OpenAsync(n, preview: false); };
        _list.Tapped += OnTapped;
        _list.ContextRequested += OnContextRequested;

        var empty = EmptyState();
        empty.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(ExplorerViewModel.HasFolder)) { Converter = Avalonia.Data.Converters.BoolConverters.Not });
        _list.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(ExplorerViewModel.HasFolder)));

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Background = ThemeBrushes.Get("sideBar.background") };
        root.Children.Add(header);
        Grid.SetRow(filter, 1);
        root.Children.Add(filter);
        var body = new Grid { Children = { _list, empty } };
        Grid.SetRow(body, 2);
        root.Children.Add(body);
        Content = root;
        vm.PropertyChanged += OnVmChanged;
        vm.Rows.CollectionChanged += OnRowsChanged;
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExplorerViewModel.Selected) && _vm.Selected is { } s) Dispatcher.UIThread.Post(() => _list.ScrollIntoView(s), DispatcherPriority.Background);
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) { }

    private Control EmptyState()
    {
        var open = new Button { Content = "Open Folder", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(16, 4) };
        open.Classes.Add("accent");
        open.Click += async (_, _) => { if (_commands is not null) await _commands.ExecuteAsync(KnownCommands.OpenFolder).ConfigureAwait(true); };
        return new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16), Spacing = 4,
            Children =
            {
                new TextBlock { Text = "You have not yet opened a folder.", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = ThemeBrushes.Get("descriptionForeground") },
                open,
            },
        };
    }

    // ------------------------------------------------------------------ rows

    private Control RowFor(ExplorerNode node)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,16,18,*"), Height = 22, Background = Brushes.Transparent };
        grid.Children.Add(new Border { Width = node.IndentWidth, Height = 1 });
        var chevron = new Icon { Size = 12, Foreground = ThemeBrushes.Get("descriptionForeground"), HorizontalAlignment = HorizontalAlignment.Center };
        chevron.Bind(Icon.KindProperty, new Avalonia.Data.Binding(nameof(ExplorerNode.Chevron)));
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);

        var icon = new Icon { Size = 15, HorizontalAlignment = HorizontalAlignment.Center };
        icon.Bind(Icon.KindProperty, new Avalonia.Data.Binding(nameof(ExplorerNode.IconKey)));
        icon.Bind(Icon.ForegroundProperty, new Avalonia.Data.Binding(nameof(ExplorerNode.IconColor)) { Converter = ViewHelpers.HexBrush });
        Grid.SetColumn(icon, 2);
        grid.Children.Add(icon);

        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        name.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ExplorerNode.Name)));
        name.Bind(OpacityProperty, new Avalonia.Data.Binding(nameof(ExplorerNode.IsCut)) { Converter = new Avalonia.Data.Converters.FuncValueConverter<bool, double>(cut => cut ? 0.5 : 1.0) });
        name.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(ExplorerNode.IsEditing)) { Converter = Avalonia.Data.Converters.BoolConverters.Not });
        Grid.SetColumn(name, 3);
        grid.Children.Add(name);

        var edit = new TextBox { Margin = new Thickness(2, 0, 4, 0), MinHeight = 0, Height = 20, Padding = new Thickness(3, 0), VerticalContentAlignment = VerticalAlignment.Center };
        edit.Bind(TextBox.TextProperty, new Avalonia.Data.Binding(nameof(ExplorerNode.EditText)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        edit.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(ExplorerNode.IsEditing)));
        edit.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await _vm.CommitRenameAsync(node).ConfigureAwait(true); _list.Focus(); }
            else if (e.Key == Key.Escape) { e.Handled = true; _vm.CancelRename(node); _list.Focus(); }
        };
        edit.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && edit.IsVisible)
                Dispatcher.UIThread.Post(() =>
                {
                    edit.Focus();
                    var dot = node.EditText.LastIndexOf('.');
                    edit.SelectionStart = 0;
                    edit.SelectionEnd = node.IsDirectory || dot <= 0 ? node.EditText.Length : dot;
                }, DispatcherPriority.Input);
        };
        Grid.SetColumn(edit, 3);
        grid.Children.Add(edit);
        return grid;
    }

    // ------------------------------------------------------------------ input

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual v || ViewHelpersAncestor(v) is not { } row || row.DataContext is not ExplorerNode node) return;
        if (node.IsDirectory) _ = _vm.ToggleAsync(node);
        else _ = _vm.OpenAsync(node, preview: true); // single click opens a preview tab, like VS
    }

    private static Control? ViewHelpersAncestor(Visual v)
    {
        for (var c = v; c is not null; c = Avalonia.VisualTree.VisualExtensions.GetVisualParent(c)) if (c is ListBoxItem item) return item;
        return null;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var node = _vm.Selected;
        if (node is null) return;
        switch (e.Key)
        {
            case Key.Enter: e.Handled = true; await _vm.OpenAsync(node, preview: false).ConfigureAwait(true); break;
            case Key.Right when node.IsDirectory: e.Handled = true; if (!node.IsExpanded) await _vm.ExpandAsync(node).ConfigureAwait(true); break;
            case Key.Left: e.Handled = true; if (node.IsDirectory && node.IsExpanded) _vm.Collapse(node); else if (node.Parent is { } p) _vm.Selected = p; break;
            case Key.F2: e.Handled = true; if (!node.IsRoot) _vm.BeginRename(node); break;
            case Key.Delete: e.Handled = true; await _vm.DeleteAsync(node).ConfigureAwait(true); break;
            case Key.C when e.KeyModifiers == KeyModifiers.Control: e.Handled = true; _vm.Copy([node]); break;
            case Key.X when e.KeyModifiers == KeyModifiers.Control: e.Handled = true; _vm.Cut([node]); break;
            case Key.V when e.KeyModifiers == KeyModifiers.Control: e.Handled = true; await _vm.PasteAsync(node).ConfigureAwait(true); break;
        }
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || _vm.Selected?.IsEditing == true) return;
        _typeAhead = DateTime.UtcNow - _typeAheadAt > TimeSpan.FromMilliseconds(800) ? e.Text : _typeAhead + e.Text;
        _typeAheadAt = DateTime.UtcNow;
        _vm.TypeAhead(_typeAhead);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ actions / context menu

    private async Task CreateAsync(bool isFolder)
    {
        var target = _vm.Selected ?? _vm.Roots.FirstOrDefault();
        if (target is null) return;
        var name = await _window.ShowInputBoxAsync(new InputBoxOptions(Title: isFolder ? "New Folder" : "New File", Prompt: isFolder ? "Folder name" : "File name",
            Validate: s => _vm.ValidateName(s, target.IsDirectory ? target : target.Parent ?? target, null))).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (isFolder) await _vm.CreateFolderAsync(target, name).ConfigureAwait(true);
        else
        {
            var created = await _vm.CreateFileAsync(target, name).ConfigureAwait(true);
            if (created is not null && _vm.Rows.FirstOrDefault(r => r.Uri == created) is { } node) await _vm.OpenAsync(node, preview: false).ConfigureAwait(true);
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Visual v || ViewHelpersAncestor(v)?.DataContext is not ExplorerNode node) return;
        _vm.Selected = node;
        var menu = new ContextMenu();
        void Add(string header, Func<Task> action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += async (_, _) => await action().ConfigureAwait(true);
            menu.Items.Add(item);
        }
        if (node.IsDirectory)
        {
            Add("New File...", () => CreateAsync(false));
            Add("New Folder...", () => CreateAsync(true));
            menu.Items.Add(new Separator());
        }
        Add("Rename", () => { _vm.BeginRename(node); return Task.CompletedTask; }, !node.IsRoot);
        Add("Duplicate", () => _vm.DuplicateAsync(node), !node.IsRoot);
        Add("Delete", () => _vm.DeleteAsync(node), !node.IsRoot);
        menu.Items.Add(new Separator());
        Add("Cut", () => { _vm.Cut([node]); return Task.CompletedTask; }, !node.IsRoot);
        Add("Copy", () => { _vm.Copy([node]); return Task.CompletedTask; });
        Add("Paste", () => _vm.PasteAsync(node), _vm.CanPaste);
        menu.Items.Add(new Separator());
        Add("Copy Path", () => _window.CopyToClipboardAsync(node.Uri.IsFile ? node.Uri.LocalPath : node.Uri.ToString()).AsTask());

        // Contributed items (user files only; there is no extension system), gated by their `when` clause.
        if (_contributions is not null && _commands is not null && _keys is not null)
        {
            var contributed = ContributedMenus.Get(_contributions, _commands, _keys, ContributedMenus.ExplorerContext);
            if (contributed.Count > 0)
            {
                menu.Items.Add(new Separator());
                foreach (var (item, title) in contributed)
                {
                    var id = item.Command;
                    Add(title, async () => { await _commands.ExecuteAsync(id, node.Uri.ToString()).ConfigureAwait(true); }, _commands.Has(id));
                }
            }
        }
        menu.Open(_list);
        e.Handled = true;
    }

    public void Dispose()
    {
        _vm.PropertyChanged -= OnVmChanged;
        _vm.Rows.CollectionChanged -= OnRowsChanged;
        _list.ItemsSource = null;
        DataContext = null;
        _vm.Dispose();
    }
}
