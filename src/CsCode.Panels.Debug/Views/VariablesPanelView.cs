using Avalonia.Controls;
using CsCode.Panels.Debug.Variables;
using CsCode.Ui;

namespace CsCode.Panels.Debug.Views;

/// <summary>
/// The view of Locals, Autos and Watch: a <see cref="VariablesTreeView"/> over the window's view-model tree. The window's own view-model is the
/// DataContext so the shell's "dispose the view when the tool is hidden" disposes it (and with it every service subscription).
/// </summary>
public sealed class VariablesPanelView : UserControl, IDisposable, IToolWindowFocus
{
    private readonly Action? _onDispose;

    /// <summary>The shell asks the view it created (this one) where the keyboard goes: the grid's row (<see cref="VariablesTreeView.FocusPreferred"/>).</summary>
    public bool FocusPreferred() => Content is IToolWindowFocus tree && tree.FocusPreferred();

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
