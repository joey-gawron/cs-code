using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Threading;
using CsCode.Core.Lifecycle;
using CsCode.Panels.Search.ViewModels;

namespace CsCode.Panels.Search.Views;

/// <summary>
/// The Find in Files tool window. Implements <see cref="IDisposable"/> because the shell disposes tool views when they are hidden:
/// disposing releases the view-model (which cancels a running search and drops every subscription).
/// </summary>
public sealed partial class FindInFilesView : UserControl, IDisposable
{
    public static readonly IValueConverter ChevronConverter = new FuncValueConverter<bool, string>(expanded => expanded ? "chevron-down" : "chevron-right");

    private readonly DisposableStore _subs = new();
    private readonly Action<FindInFilesViewModel>? _release;
    private bool _disposed;

    public FindInFilesView() : this(null!, null) { }

    public FindInFilesView(FindInFilesViewModel viewModel, Action<FindInFilesViewModel>? release = null)
    {
        _release = release;
        InitializeComponent();
        if (viewModel is null) return; // designer
        DataContext = viewModel;
        _subs.Add(viewModel.FocusRequested.Subscribe(t => Dispatcher.UIThread.Post(() => FocusTarget(t), DispatcherPriority.Input)));

        var results = ResultsList;
        results.AddHandler(KeyDownEvent, OnResultsKeyDown, RoutingStrategies.Tunnel);
        results.AddHandler(TappedEvent, OnResultsTapped, RoutingStrategies.Bubble);
        QueryBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        ReplaceBox.AddHandler(KeyDownEvent, OnReplaceKeyDown, RoutingStrategies.Tunnel);
        IncludeBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        ExcludeBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);
    }

    private FindInFilesViewModel Vm => (FindInFilesViewModel)DataContext!;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is FindInFilesViewModel { PendingFocus: { } target } vm)
        {
            vm.PendingFocus = null;
            Dispatcher.UIThread.Post(() => FocusTarget(target), DispatcherPriority.Input);
        }
    }

    public void FocusTarget(FindFocusTarget target)
    {
        switch (target)
        {
            case FindFocusTarget.Replace: ReplaceBox.Focus(); ReplaceBox.SelectAll(); break;
            case FindFocusTarget.Results: ResultsList.Focus(); break;
            default: QueryBox.Focus(); QueryBox.SelectAll(); break;
        }
    }

    // ------------------------------------------------------------------------------------- keyboard

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                Vm.Start();
                e.Handled = true;
                break;
            case Key.Up when sender == QueryBox:
                Vm.HistoryUp();
                QueryBox.CaretIndex = QueryBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.Down when sender == QueryBox:
                Vm.HistoryDown();
                QueryBox.CaretIndex = QueryBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.Escape:
                if (Vm.IsSearching) Vm.Cancel(); else ResultsList.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnReplaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { _ = Vm.ReplaceAllAsync(); e.Handled = true; }
        else if (e.Key == Key.Escape) { ResultsList.Focus(); e.Handled = true; }
    }

    /// <summary>Shortcuts that work anywhere in the panel: F4 / Shift+F4, Alt+C/W/R/P toggles, Ctrl+Alt+Enter.</summary>
    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        var m = e.KeyModifiers;
        if (e.Key == Key.F4 && m == KeyModifiers.None) { _ = Vm.NextResultAsync(); e.Handled = true; }
        else if (e.Key == Key.F4 && m == KeyModifiers.Shift) { _ = Vm.PreviousResultAsync(); e.Handled = true; }
        else if (m == KeyModifiers.Alt && e.Key == Key.C) { Vm.MatchCase = !Vm.MatchCase; e.Handled = true; }
        else if (m == KeyModifiers.Alt && e.Key == Key.W) { Vm.WholeWord = !Vm.WholeWord; e.Handled = true; }
        else if (m == KeyModifiers.Alt && e.Key == Key.R) { Vm.IsRegex = !Vm.IsRegex; e.Handled = true; }
        else if (m == KeyModifiers.Alt && e.Key == Key.P) { Vm.PreserveCase = !Vm.PreserveCase; e.Handled = true; }
        else if (m == (KeyModifiers.Control | KeyModifiers.Alt) && e.Key == Key.Enter) { _ = Vm.ReplaceAllAsync(); e.Handled = true; }
    }

    private void OnResultsKeyDown(object? sender, KeyEventArgs e)
    {
        var row = Vm.SelectedRow;
        switch (e.Key)
        {
            case Key.Enter when row is not null:
                _ = Vm.OpenAsync(row, preview: false, preserveFocus: false);
                e.Handled = true;
                break;
            case Key.Delete when row is not null:
                Vm.Dismiss(row);
                e.Handled = true;
                break;
            case Key.Left:
                if (row is FileRow { IsExpanded: true } f1) Vm.SetExpanded(f1, false);
                else if (row is MatchRow m1) Vm.SelectedRow = m1.File;
                e.Handled = true;
                break;
            case Key.Right when row is FileRow { IsExpanded: false } f2:
                Vm.SetExpanded(f2, true);
                e.Handled = true;
                break;
            case Key.C when e.KeyModifiers == KeyModifiers.Control && row is not null:
                _ = Vm.CopyPathAsync(row);
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------------------------------- mouse

    private void OnResultsTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Control c || c.DataContext is not ResultRow row) return;
        if (c.FindAncestorOfType<Button>() is not null || c is Button) return; // a row action, not the row
        switch (row)
        {
            case FileRow f: Vm.SetExpanded(f, !f.IsExpanded); break;
            case MatchRow: _ = Vm.OpenAsync(row, preview: true, preserveFocus: true); break;
        }
    }

    private void OnMatchDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: MatchRow m }) { _ = Vm.OpenAsync(m, preview: false, preserveFocus: false); e.Handled = true; }
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: FileRow f }) { _ = Vm.OpenAsync(f); e.Handled = true; }
    }

    private void OnDismissClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ResultRow row }) { Vm.Dismiss(row); e.Handled = true; }
    }

    private void OnReplaceFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FileRow f }) { _ = Vm.ReplaceFileAsync(f); e.Handled = true; }
    }

    private void OnReplaceMatchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MatchRow m }) { _ = Vm.ReplaceMatchAsync(m); e.Handled = true; }
    }

    private void OnRowContext(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: ResultRow row } target) return;
        Vm.SelectedRow = row;
        var vm = Vm;
        var items = new List<Control>();
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            return mi;
        }
        items.Add(Item("Open", () => _ = vm.OpenAsync(row)));
        items.Add(Item("Copy Path", () => _ = vm.CopyPathAsync(row)));
        if (vm.HasCommand("revealInExplorer")) items.Add(Item("Reveal in Explorer", () => _ = vm.RevealInExplorerAsync(row)));
        items.Add(new Separator());
        if (row is FileRow file)
        {
            items.Add(Item(file.IsExpanded ? "Collapse" : "Expand", () => vm.SetExpanded(file, !file.IsExpanded)));
            items.Add(Item("Exclude This Folder", () => vm.ExcludeFolderOf(file), enabled: file.Directory.Length > 0));
        }
        if (vm.ReplaceVisible)
            items.Add(Item("Replace", () => _ = row is FileRow rf ? vm.ReplaceFileAsync(rf) : vm.ReplaceMatchAsync((MatchRow)row)));
        items.Add(Item("Dismiss", () => vm.Dismiss(row)));
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(target);
        e.Handled = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subs.Dispose();
        if (DataContext is FindInFilesViewModel vm)
        {
            if (_release is not null) _release(vm); else vm.Dispose();
        }
    }
}
