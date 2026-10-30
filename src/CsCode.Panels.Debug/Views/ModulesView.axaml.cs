using Avalonia.Controls;
using CsCode.Ui;

namespace CsCode.Panels.Debug.Views;

/// <summary>Modules window: the loaded modules of the debuggee (name, path, version, user code, symbol status).</summary>
public sealed partial class ModulesView : UserControl, IDisposable, IToolWindowFocus
{
    public ModulesView() => InitializeComponent();

    /// <summary>View &gt; Tool Windows &gt; Modules: the keyboard on a module (the selected one, else one on screen), not on Refresh, which leads the tab order.</summary>
    public bool FocusPreferred() => ToolWindowFocus.FocusRow(this.FindControl<ListBox>("Rows")!);

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
