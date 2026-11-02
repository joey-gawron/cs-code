using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CsCode.Panels.Debug.Infrastructure;
using CsCode.Panels.Debug.Native;
using CsCode.Ui;

namespace CsCode.Panels.Debug.Views;

/// <summary>
/// Memory window: hex + ASCII rows of 16 bytes around a memory reference. The rows on screen tell the view model which pages to read
/// (<see cref="MemoryViewModel.SetVisibleRows"/>); Enter or a double-click on a row edits its bytes (writeMemory).
/// </summary>
public sealed partial class MemoryView : UserControl, IDisposable, IToolWindowFocus
{
    private const double RowHeight = 18;
    private ScrollViewer? _scroll;
    private MemoryViewModel? _model;
    private MemoryRow? _pendingReveal;
    private bool _revealing;

    public MemoryView()
    {
        InitializeComponent();
        Rows.TemplateApplied += (_, _) => HookScroll();
        Rows.DoubleTapped += (_, _) => Edit();
        Rows.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Edit(); } };
        Rows.ContextRequested += OnContextRequested;
        this.FindControl<TextBox>("Address")!.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _model is { } m) { e.Handled = true; _ = m.GoCommand.ExecuteAsync(null); }
        };
        DataContextChanged += (_, _) => Attach(DataContext as MemoryViewModel);
    }


    public bool FocusPreferred() => ToolWindowFocus.FocusRow(Rows);

    private void Attach(MemoryViewModel? model)
    {
        if (_model is { } old) old.RevealRequested -= Reveal;
        _model = model;
        _pendingReveal = null;
        if (model is null) return;
        model.RevealRequested += Reveal;
        Dispatch(() => Reveal(model.TargetRow));
    }

    private static void Dispatch(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action, Avalonia.Threading.DispatcherPriority.Loaded);

    private void HookScroll()
    {
        if (_scroll is not null) return;
        _scroll = Rows.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scroll is null) return;
        _scroll.ScrollChanged += (_, _) =>
        {
            if (_revealing) return;
            if (_pendingReveal is { } row) Reveal(row);
            else ReportVisible();
        };
    }

    private void ReportVisible()
    {
        if (_scroll is not { } s || _model is not { } m) return;
        var first = (int)(s.Offset.Y / RowHeight);
        var last = first + (int)Math.Ceiling(s.Viewport.Height / RowHeight);
        m.SetVisibleRows(first, last);
    }

    /// <summary>
    /// The reference's row at the top of the view (after a navigation). A window opened by View Binary Data gets its model, and the row, before
    /// the shell lays it out: until the list has a viewport the offset would stay at the top of the range, so the row waits for the layout.
    /// </summary>
    private void Reveal(MemoryRow row)
    {
        _pendingReveal = row;
        if (_revealing || _scroll is not { Viewport.Height: > 0 } s) return;
        var top = row.Index * RowHeight;
        _revealing = true;
        try
        {
            Rows.ScrollIntoView(row);
            s.Offset = new Vector(s.Offset.X, top);
        }
        finally { _revealing = false; }
        if (Math.Abs(s.Offset.Y - top) >= 1) return; // the extent is not that long yet: the next layout
        _pendingReveal = null;
        ReportVisible();
    }

    private void Edit()
    {
        if (_model is { CanWrite: true } m && m.SelectedRow is { } row) _ = m.EditAsync(row);
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_model is not { } m || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not MemoryRow row) return;
        m.SelectedRow = row;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Edit Bytes…", InputGesture = new KeyGesture(Key.Enter), IsEnabled = m.CanWrite, Command = new UiCommand(() => m.EditAsync(row)) });
        menu.Items.Add(new MenuItem { Header = "Copy Row", Command = new UiCommand(() => m.CopyAsync($"{row.AddressText}  {row.Hex}  {row.Ascii}")) });
        menu.Open(Rows);
        e.Handled = true;
    }

    public void Dispose()
    {
        Attach(null);
        (DataContext as IDisposable)?.Dispose();
    }
}
