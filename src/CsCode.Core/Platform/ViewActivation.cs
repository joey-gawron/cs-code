namespace CsCode.Core.Platform;

/// <summary>
/// Optional service implemented by the shell: brings a registered tool window or document view to the front, creating its view if
/// needed. <paramref name="viewId"/> is the <c>CsCode.Ui.IToolWindow.Id</c> ("search", "settings", "keybindings", "output"...).
/// Panels call it from their commands (Find in Files, Open Settings) so they never need a reference to the shell.
/// Additive Core contract added by the Search/Settings panels module; the shell wires it to its ToolWindowManager / DocumentWell.
/// </summary>
public interface IViewActivator
{
    /// <summary>Shows and focuses the view. Returns false when no view with that id is registered.</summary>
    ValueTask<bool> ShowViewAsync(string viewId, CancellationToken ct = default);
}
