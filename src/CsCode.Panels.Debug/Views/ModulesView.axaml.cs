using Avalonia.Controls;

namespace CsCode.Panels.Debug.Views;

/// <summary>Modules window: the loaded modules of the debuggee (name, path, version, user code, symbol status).</summary>
public sealed partial class ModulesView : UserControl, IDisposable
{
    public ModulesView() => InitializeComponent();

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
