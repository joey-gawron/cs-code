using Avalonia.Controls;

namespace CsCode.Panels.Debug.Views;

/// <summary>The Exception Helper card: type, message, stack and inner exceptions of the exception the debuggee stopped on, with Copy Details and Continue.</summary>
public sealed partial class ExceptionHelperView : UserControl, IDisposable
{
    public ExceptionHelperView() => InitializeComponent();

    public void Dispose() => (DataContext as IDisposable)?.Dispose();
}
