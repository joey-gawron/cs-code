using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Threading;
using CsCode.Core.Lifecycle;
using CsCode.Panels.Search.ViewModels;
using CsCode.Ui.Menus;

namespace CsCode.Panels.Search.Views;

/// <summary>
/// The Find in Files tool window. Implements <see cref="IDisposable"/> because the shell disposes tool views when they are hidden:
/// disposing releases the view-model (which cancels a running search and drops every subscription).
/// </summary>
public sealed partial class FindInFilesView : UserControl, IDisposable, CsCode.Ui.Menus.IClipboardTarget
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
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { ReplaceAll(); e.Handled = true; }
        else if (e.Key == Key.Escape) { ResultsList.Focus(); e.Handled = true; }
    }

    /// <summary>Keyboard Replace All goes through the command, so it obeys the same rules as the button (replace UI open, results, not busy).</summary>
    private void ReplaceAll()
    {
        if (Vm.ReplaceAllCommand.CanExecute(null)) Vm.ReplaceAllCommand.Execute(null);
    }

    /// <summary>
    /// Shortcuts that work anywhere in the panel: F4 / Shift+F4, Alt+C/W/R/P toggles, Ctrl+Alt+Enter. On macOS the toggles are ⌥⌘C/W/R/P (Option
    /// alone types ç, ∑, ®, π into the boxes) and Replace All is ⌥⌘Enter, as in VS Code.
    /// </summary>
    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        var m = e.KeyModifiers;
        var toggle = CommandKeys.AltToggle;
        if (e.Key == Key.F4 && m == KeyModifiers.None) { _ = Vm.NextResultAsync(); e.Handled = true; }
        else if (e.Key == Key.F4 && m == KeyModifiers.Shift) { _ = Vm.PreviousResultAsync(); e.Handled = true; }
        else if (m == toggle && e.Key == Key.C) { Vm.MatchCase = !Vm.MatchCase; e.Handled = true; }
        else if (m == toggle && e.Key == Key.W) { Vm.WholeWord = !Vm.WholeWord; e.Handled = true; }
        else if (m == toggle && e.Key == Key.R) { Vm.IsRegex = !Vm.IsRegex; e.Handled = true; }
        else if (m == toggle && e.Key == Key.P) { Vm.PreserveCase = !Vm.PreserveCase; e.Handled = true; }
        else if (m == (CommandKeys.Command | KeyModifiers.Alt) && e.Key == Key.Enter) { ReplaceAll(); e.Handled = true; }
    }

    // The tooltips of the toggles, Replace All and Dismiss, with this platform's keys.
    public static string MatchCaseTip => $"Match Case ({CommandKeys.AltToggleText("C")})";
    public static string WholeWordTip => $"Match Whole Word ({CommandKeys.AltToggleText("W")})";
    public static string RegexTip => $"Use Regular Expression ({CommandKeys.AltToggleText("R")})";
    public static string PreserveCaseTip => $"Preserve Case ({CommandKeys.AltToggleText("P")})";
    public static string ReplaceAllTip => CommandKeys.IsMac ? "Replace All (⌥⌘↩)" : "Replace All (Ctrl+Alt+Enter)";
    public static string DismissTip => $"Dismiss ({CommandKeys.DeleteText})";

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
            case Key.Back when row is not null && CommandKeys.IsMac && (e.KeyModifiers == KeyModifiers.None || CommandKeys.IsMacDelete(e)): // the Mac 'delete' key, ⌘⌫ too
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
            case Key.C or Key.Insert when row is not null && CsCode.Ui.Menus.ClipboardKeys.Classify(e) == CsCode.Ui.Menus.ClipboardAction.Copy:
                _ = CopyAsync();
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
        MenuItem Item(string header, Action action, bool enabled = true, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled, InputGesture = gesture is null ? null : KeyGesture.Parse(gesture) };
            mi.Click += (_, _) => action();
            return mi;
        }
        items.Add(Item("Open", () => _ = vm.OpenAsync(row)));
        items.Add(new Separator());
        items.Add(Item("Copy", () => _ = CopyAsync(), gesture: CommandKeys.Gesture("C")));
        items.Add(Item("Copy Path", () => _ = vm.CopyPathAsync(row)));
        items.Add(Item("Copy All", () => _ = CopyAllAsync(), enabled: vm.Rows.Count > 0));
        if (vm.HasCommand("revealInExplorer")) items.Add(Item("Reveal in Explorer", () => _ = vm.RevealInExplorerAsync(row)));
        items.Add(new Separator());
        if (row is FileRow file)
        {
            items.Add(Item(file.IsExpanded ? "Collapse" : "Expand", () => vm.SetExpanded(file, !file.IsExpanded)));
            items.Add(Item("Exclude This Folder", () => vm.ExcludeFolderOf(file), enabled: file.Directory.Length > 0));
        }
        if (vm.ReplaceVisible)
            items.Add(Item("Replace", () => _ = row is FileRow rf ? vm.ReplaceFileAsync(rf) : vm.ReplaceMatchAsync((MatchRow)row)));
        items.Add(Item("Dismiss", () => vm.Dismiss(row), gesture: CommandKeys.IsMac ? CommandKeys.DeleteGesture : null)); // ⌘⌫: Fn+Delete is not obvious on a Mac
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(target);
        e.Handled = true;
    }

    // ------------------------------------------------------------------------------------- clipboard (Ctrl+C, Ctrl+Insert, Edit > Copy)

    /// <summary>A result as Visual Studio's Find Results copies it: a match as <c>path(line): text</c>, a file as its path.</summary>
    internal static string CopyText(ResultRow row) => row switch
    {
        MatchRow m => $"{m.File.FullPath}({m.LineNumber}): {m.Preview.Trim()}",
        FileRow f => f.FullPath,
        _ => "",
    };

    public bool CanCopy => DataContext is FindInFilesViewModel { SelectedRow: not null };

    public async Task CopyAsync()
    {
        if (DataContext is FindInFilesViewModel { SelectedRow: { } row } vm) await vm.CopyTextAsync(CopyText(row));
    }

    /// <summary>Every result, files and their matches, one per line.</summary>
    private async Task CopyAllAsync()
    {
        if (DataContext is not FindInFilesViewModel vm) return;
        var text = string.Join("\n", vm.Rows.Select(CopyText));
        await vm.CopyTextAsync(text);
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
