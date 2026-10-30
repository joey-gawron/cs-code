using Avalonia.Controls;
using CsCode.Panels.Search.Settings;

namespace CsCode.Panels.Search.Views;

/// <summary>The Keyboard Shortcuts document view (a filterable table of the active key bindings).</summary>
public sealed partial class KeyboardShortcutsView : UserControl, IDisposable
{
    private bool _disposed;

    public KeyboardShortcutsView() : this(null!) { }

    public KeyboardShortcutsView(KeyboardShortcutsViewModel viewModel)
    {
        InitializeComponent();
        if (viewModel is not null) DataContext = viewModel;
        // A binding changed or a command registered reloads the table, and the filter narrows it: both replace the rows (one Reset).
        CsCode.Ui.ToolWindowFocus.KeepOnRows(this.FindControl<ListBox>("Rows")!);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (DataContext as IDisposable)?.Dispose();
    }
}
