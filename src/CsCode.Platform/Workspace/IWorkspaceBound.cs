namespace CsCode.Platform.Workspace;

/// <summary>
/// Implemented by Platform services whose data depends on the open folder(s) (workspace settings, workspace-scoped state).
/// <see cref="WorkspaceService"/> pushes folder changes to them; this inverts the dependency so that configuration and state do not need
/// the workspace service (which itself needs configuration for <c>files.exclude</c>).
/// </summary>
public interface IWorkspaceBound
{
    /// <summary>Called (off the UI thread) before <c>IWorkspaceService.FoldersChanged</c> is raised, so subscribers already see the new data.</summary>
    ValueTask OnWorkspaceFoldersChangedAsync(IReadOnlyList<Uri> folders, CancellationToken ct);
}
