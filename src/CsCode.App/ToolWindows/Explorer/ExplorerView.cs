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
using CsCode.Ui;
using CsCode.Ui.Controls;
using CsCode.Ui.Icons;
using CsCode.Ui.Menus;
using CsCode.Ui.Theming;

namespace CsCode.App.ToolWindows.Explorer;

/// <summary>
/// The Explorer view: header actions, a filter box and a virtualised list of the model's flat visible rows. The view owns no tree logic;
/// it forwards clicks, keys and context-menu commands to <see cref="ExplorerViewModel"/>. Disposed (with its view-model) when the tool window hides.
/// </summary>
public sealed class ExplorerView : UserControl, IDisposable, IClipboardTarget, IToolWindowFocus
{
    private readonly ExplorerViewModel _vm;
    private readonly IWindowService _window;
    private readonly IContributionRegistry? _contributions;
    private readonly ICommandService? _commands;
    private readonly IContextKeyService? _keys;
    private readonly ListBox _list;
    private Button? _openFolder;
    private string _typeAhead = "";
    private DateTime _typeAheadAt;

    private readonly MenuServices? _menus;

    public ExplorerView(ExplorerViewModel vm, IWindowService window, ICommandService? commands = null, IContextKeyService? keys = null, IContributionRegistry? contributions = null,
        MenuServices? menus = null)
    {
        _vm = vm;
        _menus = menus ?? (commands is not null && keys is not null ? new MenuServices(commands, keys, null, contributions) : null);
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

        // Multiple: Ctrl+click (⌘-click) and Shift+click select several rows, which drag together. Commands of the menu act on the clicked row.
        _list = new ListBox { ItemsSource = vm.Rows, ItemTemplate = new FuncDataTemplate<ExplorerNode>((n, _) => n is null ? new Panel() : RowFor(n), supportsRecycling: false), SelectionMode = SelectionMode.Multiple };
        _list.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, new Avalonia.Data.Binding(nameof(ExplorerViewModel.Selected)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        _list.KeyDown += OnKeyDown;
        _list.TextInput += OnTextInput;
        _list.DoubleTapped += (_, e) => { if (_list.SelectedItem is ExplorerNode n && !n.IsDirectory) _ = _vm.OpenAsync(n, preview: false); };
        _list.Tapped += OnTapped;
        _list.ContextRequested += OnContextRequested;
        ToolWindowFocus.KeepOnRows(_list); // every expand and collapse replaces the rows (one Reset), the focused one too
        AttachDragAndDrop();

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

    /// <summary>The view's model (Reveal in Explorer from a document tab).</summary>
    internal ExplorerViewModel Model => _vm;

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExplorerViewModel.Selected) && _vm.Selected is { } s) Dispatcher.UIThread.Post(() => _list.ScrollIntoView(s), DispatcherPriority.Background);
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) { }

    private Control EmptyState()
    {
        var open = _openFolder = new Button { Content = "Open Folder", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(16, 4) };
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
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,16,18,*,Auto"), Height = 22, Background = Brushes.Transparent };
        // the folder a drag would drop into (VS Code's list.dropBackground)
        grid.Bind(Panel.BackgroundProperty, new Avalonia.Data.Binding(nameof(ExplorerNode.IsDropTarget)) { Converter = DropBackground });
        // source control: the name in the status colour, the letter after it, ignored rows dimmed, deleted ones struck through
        var badge = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0), FontSize = 11 };
        badge.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(ExplorerNode.Decoration)) { Converter = DecorationBadge });
        badge.Bind(TextBlock.ForegroundProperty, new Avalonia.Data.Binding(nameof(ExplorerNode.Decoration)) { Converter = DecorationBrush });
        Grid.SetColumn(badge, 4);
        grid.Children.Add(badge);
        grid.Bind(OpacityProperty, new Avalonia.Data.Binding(nameof(ExplorerNode.Decoration)) { Converter = DecorationOpacity });
        badge.Bind(ToolTip.TipProperty, new Avalonia.Data.Binding(nameof(ExplorerNode.Decoration) + "." + nameof(Core.Scm.FileDecoration.Tooltip)));
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
        name.Bind(TextBlock.ForegroundProperty, new Avalonia.Data.Binding(nameof(ExplorerNode.Decoration)) { Converter = DecorationBrush });
        name.Bind(TextBlock.TextDecorationsProperty, new Avalonia.Data.Binding(nameof(ExplorerNode.Decoration))
        {
            Converter = new Avalonia.Data.Converters.FuncValueConverter<Core.Scm.FileDecoration?, TextDecorationCollection?>(d => d?.Strikethrough == true ? TextDecorations.Strikethrough : null),
        });
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

    private static readonly Avalonia.Data.Converters.IValueConverter DecorationBadge =
        new Avalonia.Data.Converters.FuncValueConverter<Core.Scm.FileDecoration?, string>(d => d?.Badge ?? "");

    /// <summary>The decoration's theme colour; no decoration leaves the inherited foreground (UnsetValue).</summary>
    private static readonly Avalonia.Data.Converters.IValueConverter DecorationBrush = new DecorationBrushConverter();

    private static readonly Avalonia.Data.Converters.IValueConverter DropBackground =
        new Avalonia.Data.Converters.FuncValueConverter<bool, IBrush>(target => target ? ThemeBrushes.Get("list.dropBackground") : Brushes.Transparent);

    private static readonly Avalonia.Data.Converters.IValueConverter DecorationOpacity =
        new Avalonia.Data.Converters.FuncValueConverter<Core.Scm.FileDecoration?, double>(d => d?.Faded == true ? 0.6 : 1.0);

    private sealed class DecorationBrushConverter : Avalonia.Data.Converters.IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is Core.Scm.FileDecoration d ? ThemeBrushes.Get(d.ColorKey) : AvaloniaProperty.UnsetValue;
        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
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
            // Shift+Delete is Cut (below), not Delete. ⌘⌫ deletes too on macOS (Finder's Move to Trash), but not from the rename box.
            case Key.Delete or Key.Back when CommandKeys.IsDelete(e) && !node.IsEditing: e.Handled = true; await _vm.DeleteAsync(node).ConfigureAwait(true); break;
            // ⌘Z (Ctrl+Z): the last drag and drop is undone (explorer.enableUndo)
            case Key.Z when e.KeyModifiers == CommandKeys.Command && !node.IsEditing && _vm.CanUndo: e.Handled = true; await _vm.UndoAsync().ConfigureAwait(true); break;
            default: ClipboardKeys.TryHandle(e, this); break; // Ctrl+C/X/V, Ctrl+Insert, Shift+Insert, Shift+Delete: the files
        }
    }

    /// <summary>
    /// View &gt; Tool Windows &gt; Explorer, as Solution Explorer: the keyboard on the selected row (else one on screen), where the arrows, Enter and
    /// F2 work, not on the New File button that leads the tab order. Without a folder there is no row: the keyboard goes on the Open Folder
    /// button, what the window offers then (tab order put it in the filter box, with nothing to filter). An Explorer just reopened has no row at
    /// first (<see cref="ContentPending"/>), then its root row: the folder's rows replace it as they load, and the keyboard stays on them
    /// (<see cref="ToolWindowFocus.KeepOnRows"/>).
    /// </summary>
    public bool FocusPreferred() => ToolWindowFocus.FocusRow(_list) || (!_vm.HasFolder && _openFolder?.IsEffectivelyVisible == true && _openFolder.Focus());

    /// <summary>The folder's rows load after the view's first frame (<see cref="ExplorerToolWindow"/>): until then there is no row to focus.</summary>
    public bool ContentPending => _vm.RootsPending;

    // ---- clipboard: like Solution Explorer, Cut/Copy/Paste move and copy the FILES (Copy Path puts the path on the system clipboard)

    public bool CanCopy => _vm.Selected is not null;
    public bool CanCut => _vm.Selected is { IsRoot: false };
    public bool CanPaste => _vm.Selected is not null && _vm.CanPaste;
    // every selected row (Ctrl/⌘- and Shift-click select several), as VS Code's Explorer copies and cuts them
    public Task CopyAsync() { if (_vm.Selected is { } n) _vm.Copy(DraggedNodes(n, includeRoots: true)); return Task.CompletedTask; }
    public Task CutAsync() { if (_vm.Selected is { IsRoot: false } n) _vm.Cut(DraggedNodes(n)); return Task.CompletedTask; }
    public async Task PasteAsync() { if (_vm.Selected is { } n) await _vm.PasteAsync(n).ConfigureAwait(true); }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || _vm.Selected?.IsEditing == true) return;
        _typeAhead = DateTime.UtcNow - _typeAheadAt > TimeSpan.FromMilliseconds(800) ? e.Text : _typeAhead + e.Text;
        _typeAheadAt = DateTime.UtcNow;
        _vm.TypeAhead(_typeAhead);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ drag and drop (VS Code's Explorer)

    /// <summary>How far the pointer moves with the button down before a press becomes a drag.</summary>
    private const double DragThreshold = 4;

    /// <summary>How long a drag hovers over a collapsed folder before it opens (VS Code: 500 ms).</summary>
    internal static readonly TimeSpan ExpandOnHoverDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Within this distance of the list's top or bottom edge a drag scrolls the rows.</summary>
    private const double AutoScrollMargin = 24;

    private PointerPressedEventArgs? _press;
    private Point _pressAt;
    private ExplorerNode? _pressNode, _collapseOnRelease, _dropTarget, _hoverNode;
    private IDisposable? _hoverExpand;
    private DispatcherTimer? _autoScroll;
    private int _autoScrollDirection;

    /// <summary>Starts the platform's drag (a seam: headless tests capture the data instead, as Avalonia.Headless has no drag source).</summary>
    internal Func<PointerPressedEventArgs, IDataTransfer, DragDropEffects, Task<DragDropEffects>> StartDragDrop { get; set; } = DragDrop.DoDragDropAsync;

    /// <summary>Whether ⌥ (macOS) or Ctrl (elsewhere) copies: the platform's keys, settable for tests of the other platform's.</summary>
    internal bool MacKeys { get; set; } = CommandKeys.IsMac;

    /// <summary>A drag's feedback is up: a highlighted folder, a folder about to open, or rows scrolling (each holds the view while it runs).</summary>
    internal bool DragFeedbackActive => _dropTarget is not null || _hoverExpand is not null || _autoScroll?.IsEnabled == true;

    private void AttachDragAndDrop()
    {
        DragDrop.SetAllowDrop(_list, true);
        // Tunnel: before the row's own press selects (a press on one of several selected rows must keep them all selected to drag them).
        _list.AddHandler(PointerPressedEvent, OnRowPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _list.AddHandler(PointerMovedEvent, OnRowPointerMoved, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _list.AddHandler(PointerReleasedEvent, OnRowReleased, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _list.AddHandler(PointerCaptureLostEvent, (_, _) => ForgetPress(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _list.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        _list.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _list.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        _list.AddHandler(DragDrop.DropEvent, OnDrop);
        // Explorer items over the rest of the view (its header, the filter box) go nowhere: not on to the window, which would open them.
        AddHandler(DragDrop.DragOverEvent, RefuseOwnItems);
        AddHandler(DragDrop.DropEvent, RefuseOwnItems);
    }

    private static void RefuseOwnItems(object? sender, DragEventArgs e)
    {
        if (ExplorerDrag.Items(e.DataTransfer) is null) return;
        e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }

    private static ExplorerNode? NodeAt(object? source) => source is Visual v && ViewHelpersAncestor(v)?.DataContext is ExplorerNode node ? node : null;

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        ForgetPress();
        var point = e.GetCurrentPoint(_list);
        if (!point.Properties.IsLeftButtonPressed || NodeAt(e.Source) is not { IsEditing: false } node) return; // (the row being renamed: the text box's)
        _press = e;
        _pressAt = point.Position;
        _pressNode = node;
        // A plain press on one of several selected rows keeps the selection, so they can be dragged together; if no drag follows, the release
        // selects that row alone (as a press on it would have). The row still gets the keyboard and the click (Tapped) as usual.
        if (e.KeyModifiers == KeyModifiers.None && _list.SelectedItems is { Count: > 1 } selected && selected.Contains(node))
        {
            e.Handled = true;
            _collapseOnRelease = node;
            _list.ContainerFromItem(node)?.Focus();
        }
    }

    private void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _pressNode is not { } node) return;
        var point = e.GetCurrentPoint(_list);
        if (!point.Properties.IsLeftButtonPressed) { ForgetPress(); return; }
        var delta = point.Position - _pressAt;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;
        ForgetPress();
        _ = DragAsync(press, node);
    }

    private void OnRowReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_collapseOnRelease is { } node && _press is not null)
        {
            _list.SelectedItems?.Clear();
            _list.SelectedItem = node;
        }
        ForgetPress();
    }

    private void ForgetPress()
    {
        _press = null;
        _pressNode = null;
        _collapseOnRelease = null;
    }

    /// <summary>The rows a drag that starts on <paramref name="pressed"/> carries (and Cut and Copy take): the selected rows when it is one of them,
    /// else that row alone, in row order; never a workspace folder unless <paramref name="includeRoots"/> (VS Code drags those only to reorder a
    /// multi-root workspace, which CsCode does not).</summary>
    internal IReadOnlyList<ExplorerNode> DraggedNodes(ExplorerNode pressed, bool includeRoots = false)
    {
        var selected = new HashSet<ExplorerNode>(_list.SelectedItems?.OfType<ExplorerNode>() ?? []);
        if (!selected.Contains(pressed)) return pressed.IsRoot && !includeRoots ? [] : [pressed];
        return [.. _vm.Rows.Where(n => selected.Contains(n) && (includeRoots || !n.IsRoot))]; // one pass over the rows: row order
    }

    private async Task DragAsync(PointerPressedEventArgs press, ExplorerNode pressed)
    {
        var nodes = DraggedNodes(pressed);
        if (nodes.Count == 0) return;
        var uris = nodes.Select(n => n.Uri).ToList();
        var files = await ExplorerDrag.StorageItemsAsync(TopLevel.GetTopLevel(_list)?.StorageProvider, uris).ConfigureAwait(true);
        using var data = ExplorerDrag.Create(uris, files);
        ExplorerDrag.Current = new ExplorerDragItems(uris);
        try
        {
            await StartDragDrop(press, data, ExplorerDrag.Offered).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // a platform without a drag source, or a drag the platform refused to start: nothing was dragged
        }
        finally
        {
            ExplorerDrag.Current = null;
            EndDropFeedback();
            foreach (var file in files) (file as IDisposable)?.Dispose();
        }
    }

    /// <summary>What a drag over the Explorer would do: copy, move (internal items only) or nothing, and the folder it would land in.</summary>
    private (DragDropEffects Effect, ExplorerNode? Folder) Evaluate(DragEventArgs e, ExplorerNode? row)
    {
        if (!_vm.HasFolder) return (DragDropEffects.None, null);
        var folder = _vm.DropFolder(row);
        if (ExplorerDrag.Items(e.DataTransfer) is { } items)
        {
            var copy = ExplorerDrag.IsCopy(e.KeyModifiers, MacKeys);
            return _vm.CanDrop(items, row, copy) ? (PickEffect(e.DragEffects, copy ? DragDropEffects.Copy : DragDropEffects.Move), folder) : (DragDropEffects.None, null);
        }
        var files = ExplorerDrag.Files(e.DataTransfer);
        return files.Count > 0 && _vm.CanDropExternal(files, row) ? (PickEffect(e.DragEffects, DragDropEffects.Copy), folder) : (DragDropEffects.None, null);
    }

    /// <summary>The effect told to the platform (its cursor badge): the one wanted when the drag offers it, else one it offers. An Explorer drag offers
    /// Copy only (<see cref="ExplorerDrag.Offered"/>), so a move shows the copy badge; what happens is decided on the drop.</summary>
    private static DragDropEffects PickEffect(DragDropEffects offered, DragDropEffects wanted) =>
        (offered & wanted) != 0 ? wanted : (offered & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : (offered & DragDropEffects.Move) != 0 ? DragDropEffects.Move : wanted;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!_vm.HasFolder) return; // no folder: the window takes it (a folder dropped there opens)
        var row = NodeAt(e.Source);
        var (effect, folder) = Evaluate(e, row);
        e.DragEffects = effect;
        e.Handled = true;
        ShowDropTarget(effect == DragDropEffects.None ? null : folder);
        // A collapsed folder hovered for a moment opens, so a drag can go deeper (VS Code).
        var expandable = effect != DragDropEffects.None && row is { IsDirectory: true, IsExpanded: false } ? row : null;
        if (!ReferenceEquals(expandable, _hoverNode))
        {
            _hoverExpand?.Dispose();
            _hoverNode = expandable;
            _hoverExpand = expandable is null ? null : DispatcherTimer.RunOnce(() =>
            {
                _hoverExpand = null;
                if (ReferenceEquals(_hoverNode, expandable) && !expandable.IsExpanded) _ = _vm.ExpandAsync(expandable);
            }, ExpandOnHoverDelay);
        }
        AutoScroll(e.GetPosition(_list).Y);
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        // Leaving one row for another raises a leave too: only a pointer outside the list ends the feedback.
        var p = e.GetPosition(_list);
        if (p.X < 0 || p.Y < 0 || p.X >= _list.Bounds.Width || p.Y >= _list.Bounds.Height) EndDropFeedback();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!_vm.HasFolder) return;
        var row = NodeAt(e.Source);
        var (effect, _) = Evaluate(e, row);
        e.DragEffects = effect;
        e.Handled = true;
        EndDropFeedback();
        if (effect == DragDropEffects.None) return;
        // The drop returns to the platform at once; the move or copy (questions included) runs after it.
        LastDrop = ExplorerDrag.Items(e.DataTransfer) is { } items
            ? DropSafelyAsync(() => _vm.DropAsync(items, row, ExplorerDrag.IsCopy(e.KeyModifiers, MacKeys)))
            : DropSafelyAsync(() => _vm.DropExternalAsync(ExplorerDrag.Files(e.DataTransfer), row));
    }

    /// <summary>The work of the last drop: what it created (tests await it).</summary>
    internal Task<IReadOnlyList<Uri>> LastDrop { get; private set; } = Task.FromResult<IReadOnlyList<Uri>>([]);

    private async Task<IReadOnlyList<Uri>> DropSafelyAsync(Func<Task<IReadOnlyList<Uri>>> drop)
    {
        try { return await drop().ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            _ = _window.ShowMessageAsync(MessageSeverity.Error, "Drag and drop failed: " + ex.Message).AsTask(); // a toast: not awaited (it waits for its dismissal)
            return [];
        }
    }

    private void ShowDropTarget(ExplorerNode? folder)
    {
        if (ReferenceEquals(folder, _dropTarget)) return;
        if (_dropTarget is not null) _dropTarget.IsDropTarget = false;
        _dropTarget = folder;
        if (folder is not null) folder.IsDropTarget = true;
    }

    private void EndDropFeedback()
    {
        ShowDropTarget(null);
        _hoverExpand?.Dispose();
        _hoverExpand = null;
        _hoverNode = null;
        AutoScroll(double.NaN);
    }

    /// <summary>Near the top or bottom edge a drag scrolls the rows, a row per tick, for as long as it stays there (drag events stop while the
    /// pointer rests on some platforms).</summary>
    private void AutoScroll(double y)
    {
        var direction = double.IsNaN(y) ? 0 : y < AutoScrollMargin ? -1 : y > _list.Bounds.Height - AutoScrollMargin ? 1 : 0;
        _autoScrollDirection = direction;
        if (direction == 0) { _autoScroll?.Stop(); return; }
        if (_autoScroll is null)
        {
            _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _autoScroll.Tick += (_, _) => ScrollRows(_autoScrollDirection);
        }
        if (!_autoScroll.IsEnabled) { ScrollRows(direction); _autoScroll.Start(); }
    }

    /// <summary>Scrolls the rows by one row up (-1) or down (1). True when they moved.</summary>
    internal bool ScrollRows(int direction)
    {
        if (direction == 0 || _list.Scroll is not ScrollViewer scroller) return false;
        var before = scroller.Offset.Y;
        var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(before + direction * 22, 0, max));
        if (scroller.Offset.Y == before) _autoScroll?.Stop();
        return scroller.Offset.Y != before;
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
        e.Handled = true;
        ContextMenus.Open(MenuFor(node), _list, accessibleName: "Explorer menu");
    }

    /// <summary>
    /// The Explorer's node menu (Visual Studio's Solution Explorer and VS Code's Explorer): New File / Folder, Reveal in File Manager, Open in
    /// Terminal (when a terminal is registered), Find in Folder, Cut / Copy / Paste (files), Copy Path / Copy Relative Path, Rename, Duplicate,
    /// Delete, then items other modules contribute to <c>explorer/context</c>. Built when it opens.
    /// </summary>
    internal IReadOnlyList<MenuEntry> MenuFor(ExplorerNode node)
    {
        var uri = node.Uri;
        var folder = node.IsDirectory ? uri : node.Parent?.Uri ?? uri;
        _keys?.Set("explorerResourceIsFolder", node.IsDirectory); // VS Code's key: contributed items for files only (Select for Compare)
        MenuEntry? Command(string header, string id, string? icon = null, params object?[] args) => _menus?.Command(header, id, icon, args: args);
        return MenuSections.Join(
            node.IsDirectory ?
            [
                new MenuEntry("New _File...", () => CreateAsync(false)) { Icon = IconKey.NewFile },
                new MenuEntry("New Fol_der...", () => CreateAsync(true)) { Icon = IconKey.NewFolder },
            ] : null,
            [
                Command("Reveal in File _Manager", Menus.ShellContextMenus.RevealInOs, "folder-opened", uri),
                Command("Open in _Terminal", "workbench.action.terminal.newHere", IconKey.Terminal, folder),
                node.IsDirectory ? Command("Find in F_older...", "search.action.findInFolder", "go-to-search", uri.ToString()) : null,
            ],
            [
                new MenuEntry("Cu_t", CutAsync) { Gesture = ClipboardKeys.Gesture("X"), IsEnabled = CanCut },
                new MenuEntry("_Copy", CopyAsync) { Gesture = ClipboardKeys.Gesture("C"), Icon = IconKey.Copy, IsEnabled = CanCopy },
                new MenuEntry("_Paste", PasteAsync) { Gesture = ClipboardKeys.Gesture("V"), Icon = "clippy", IsEnabled = CanPaste },
            ],
            [
                Command("Copy _Path", Menus.ShellContextMenus.CopyPath, null, uri)
                    ?? new MenuEntry("Copy _Path", () => _window.CopyToClipboardAsync(node.Uri.IsFile ? node.Uri.LocalPath : node.Uri.ToString()).AsTask()),
                Command("Copy Relati_ve Path", Menus.ShellContextMenus.CopyRelativePath, null, uri),
            ],
            [
                new MenuEntry("Rena_me", () => { _vm.BeginRename(node); return Task.CompletedTask; }) { Gesture = "F2", IsEnabled = !node.IsRoot },
                new MenuEntry("D_uplicate", () => _vm.DuplicateAsync(node)) { IsEnabled = !node.IsRoot },
                new MenuEntry("_Delete", () => _vm.DeleteAsync(node)) { Gesture = CommandKeys.DeleteText, Icon = IconKey.Trash, IsEnabled = !node.IsRoot },
            ],
            // Contributed items get the node's URI, as VS Code's explorer/context commands do.
            _menus is null ? null : MenuContributions.Resolve(_menus, ContributedMenus.ExplorerContext, [uri.ToString()]));
    }

    public void Dispose()
    {
        EndDropFeedback();
        _autoScroll = null;
        ForgetPress();
        _vm.PropertyChanged -= OnVmChanged;
        _vm.Rows.CollectionChanged -= OnRowsChanged;
        _list.ItemsSource = null;
        DataContext = null;
        _vm.Dispose();
    }
}
