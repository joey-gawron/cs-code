using Avalonia.Controls;
using CsCode.Panels.Debug.Variables;

namespace CsCode.Panels.Debug.Views;

/// <summary>
/// The view of Locals, Autos and Watch: a <see cref="VariablesTreeView"/> over the window's view-model tree. The window's own view-model is the
/// DataContext so the shell's "dispose the view when the tool is hidden" disposes it (and with it every service subscription).
/// </summary>
public sealed class VariablesPanelView : UserControl, IDisposable
{
    private readonly Action? _onDispose;

    public VariablesPanelView(IDisposable owner, VariablesTreeViewModel tree, Action? onDispose = null)
    {
        _onDispose = onDispose;
        DataContext = owner;
        Content = new VariablesTreeView { DataContext = tree };
    }

    public void Dispose()
    {
        var owner = DataContext as IDisposable;
        Content = null;
        _onDispose?.Invoke();
        owner?.Dispose();
    }
}
