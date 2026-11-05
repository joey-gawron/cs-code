using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CsCode.App.Docking;
using CsCode.App.ToolWindows.Explorer;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;

namespace CsCode.App.Input;

/// <summary>
/// Files dropped on a shell window anywhere the Explorer does not take them (VS Code's drop on the editor area): files, from another application
/// (Finder, a file manager) or from the Explorer, open as editor tabs, in the editor group under the pointer, the last one active. A folder from
/// another application opens as the workspace folder, as File > Open Folder does (VS Code opens a dropped folder in the window); with several,
/// the first opens and the others are added beside it (a multi-root workspace, as VS Code makes of several). Folders dragged from the Explorer
/// are not opened. What a drop opens is looked at off the UI thread (<see cref="IFileSystem.StatAsync"/>); the drop itself returns to the
/// platform at once.
/// </summary>
public sealed class WindowFileDrop
{
    private readonly DocumentWell _well;
    private readonly INavigationService _navigation;
    private readonly IWorkspaceService? _workspace;
    private readonly IFileSystem _fs;
    private readonly IWindowService? _windows;

    public WindowFileDrop(DocumentWell well, INavigationService navigation, IFileSystem fs, IWorkspaceService? workspace = null, IWindowService? windows = null)
    {
        _well = well;
        _navigation = navigation;
        _fs = fs;
        _workspace = workspace;
        _windows = windows;
    }

    /// <summary>The work of the last drop (tests await it).</summary>
    internal Task LastDrop { get; private set; } = Task.CompletedTask;

    /// <summary>Takes file drops on <paramref name="window"/> (the main window or a floating one). Dispose to stop.</summary>
    public IDisposable Attach(Control window)
    {
        DragDrop.SetAllowDrop(window, true);
        // Handled events too: an editor's text area (AvaloniaEdit's text drag and drop) marks every drag over it handled, files included, which it
        // cannot use. Ours: what nothing handled, and what an editor handled without text to take (see Ours).
        window.AddHandler(DragDrop.DragEnterEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        window.AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        window.AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        return Disposable.Create(() =>
        {
            window.RemoveHandler(DragDrop.DragEnterEvent, OnDragOver);
            window.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
            window.RemoveHandler(DragDrop.DropEvent, OnDrop);
            DragDrop.SetAllowDrop(window, false);
        });
    }

    /// <summary>A drag nothing took (the Explorer takes drops on itself), or one an editor marked handled although it carries no text for it.</summary>
    private static bool Ours(DragEventArgs e) =>
        !e.Handled || (!e.DataTransfer.Contains(DataFormat.Text) && (e.Source as Visual)?.FindAncestorOfType<AvaloniaEdit.Editing.TextArea>(includeSelf: true) is not null);

    private static (IReadOnlyList<Uri> Uris, bool FromExplorer) Dropped(DragEventArgs e)
    {
        if (!Ours(e)) return ([], false);
        if (ExplorerDrag.Items(e.DataTransfer) is { } items) return ([.. items.Where(u => !u.AbsoluteUri.EndsWith('/'))], true); // its files
        return (ExplorerDrag.Files(e.DataTransfer), false);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var (uris, _) = Dropped(e);
        if (uris.Count == 0) return; // not files (text dragged from elsewhere), or the Explorer's: not ours
        e.DragEffects = (e.DragEffects & DragDropEffects.Copy) != 0 || e.DragEffects == DragDropEffects.None ? DragDropEffects.Copy : e.DragEffects & (DragDropEffects.Move | DragDropEffects.Link);
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var (uris, fromExplorer) = Dropped(e);
        if (uris.Count == 0) return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        ActivateGroupAt(e.Source as Visual);
        LastDrop = OpenAsync(uris, fromExplorer);
    }

    /// <summary>The editor group under the pointer becomes the active one, so the files open there.</summary>
    private void ActivateGroupAt(Visual? hit)
    {
        if (hit is null) return;
        foreach (var tab in _well.Tabs)
            if (tab.LiveView is { } view && (ReferenceEquals(view, hit) || view.IsVisualAncestorOf(hit)))
            {
                if (!ReferenceEquals(_well.Active, tab)) _well.Activate(tab, preserveFocus: true);
                return;
            }
    }

    private async Task OpenAsync(IReadOnlyList<Uri> uris, bool fromExplorer)
    {
        var files = new List<Uri>();
        var folders = new List<Uri>();
        foreach (var uri in uris)
        {
            if (fromExplorer) { files.Add(uri); continue; } // the Explorer knows which of its rows are folders
            try
            {
                var stat = await _fs.StatAsync(uri).ConfigureAwait(true);
                if (stat is null) continue;
                if (stat.Type.HasFlag(FileType.Directory)) folders.Add(uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/"));
                else files.Add(uri);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report(uri, ex); }
        }
        if (folders.Count > 0 && _workspace is not null)
        {
            await _workspace.OpenFolderAsync(folders[0]).ConfigureAwait(true);
            if (_workspace is Platform.Workspace.WorkspaceService multiRoot)
                foreach (var more in folders.Skip(1)) await multiRoot.AddFolderAsync(more).ConfigureAwait(true);
        }
        foreach (var file in files)
        {
            try { await _navigation.OpenAsync(file).ConfigureAwait(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException) { Report(file, ex); }
        }
    }

    // A toast, not awaited: one without actions completes only when it is dismissed.
    private void Report(Uri uri, Exception ex) =>
        _ = _windows?.ShowMessageAsync(MessageSeverity.Error, $"Could not open '{(uri.IsFile ? uri.LocalPath : uri.ToString())}': {ex.Message}").AsTask();
}
