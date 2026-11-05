using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using CsCode.App.Docking;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.ToolWindows.Explorer;
using CsCode.Core.Platform;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// Files dropped on the real window outside the Explorer (<see cref="Input.WindowFileDrop"/>, docs/SHELL.md "Explorer"): from another application
/// or from the Explorer they open as editor tabs, also over an editor whose own drag and drop marks the drag handled; a folder from another
/// application opens as the workspace folder.
/// </summary>
public class WindowFileDropTests
{
    [AvaloniaFact(Timeout = 120_000)]
    public async Task Files_dropped_on_the_editor_area_open_as_tabs_and_a_folder_opens_as_the_workspace()
    {
        using var env = new RealAppHome();
        var file = env.Workspace.WriteFile("dropped.txt", "dropped\n");
        var fromExplorer = env.Workspace.WriteFile("src/explorer.txt", "from the explorer\n");
        using var other = new TempDirectory("cscode-dnd-folder-");
        other.WriteFile("inside.txt", "");
        await using var run = new RealAppRun(env);
        var main = await run.MainWindowAsync();
        var well = run.Services.GetRequiredService<DocumentWell>();
        Dispatcher.UIThread.RunJobs();
        var editorArea = new Point(main.Bounds.Width * 0.3, main.Bounds.Height * 0.45);
        async Task DropAsync(IDataTransfer data)
        {
            // as a platform plays a drag: entered, moved over, dropped (the drop goes to the element the last move was over)
            main.DragDrop(editorArea, RawDragEventType.DragEnter, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.None);
            main.DragDrop(editorArea, RawDragEventType.DragOver, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.None);
            main.DragDrop(editorArea, RawDragEventType.Drop, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.None);
            await main.FileDrop.LastDrop;
            Dispatcher.UIThread.RunJobs();
        }

        using (var data = await ExplorerDragDropTests.FilesFromAnotherAppAsync(main, file)) await DropAsync(data); // onto the Welcome page
        Assert.Equal(new Uri(file), well.ActiveEditor?.Uri);
        Assert.False(well.ActiveEditor!.IsPreview);
        Assert.True(await DevHost.WaitFor(() => main.InputHitTest(editorArea) is AvaloniaEdit.Rendering.TextView), "the next drops land on an editor");

        // an Explorer row dragged onto the editor: the file opens (a folder row would not), though the editor's own drag and drop handled the drag
        using (var data = ExplorerDrag.Create([new Uri(fromExplorer), new Uri(env.Workspace.Combine("src") + "/")], [])) await DropAsync(data);
        Assert.Equal(new Uri(fromExplorer), well.ActiveEditor?.Uri);
        Assert.Equal(2, well.EditorTabs.Count);
        Assert.Equal("dropped\n", well.EditorTabs[0].Doc.GetText().ReplaceLineEndings("\n")); // nothing was typed into the editor dropped on

        // a folder from another application: opened as the workspace folder (File > Open Folder)
        using (var data = await ExplorerDragDropTests.FilesFromAnotherAppAsync(main, other.Path, folder: true)) await DropAsync(data);
        var workspace = run.Services.GetRequiredService<IWorkspaceService>();
        Assert.Equal(TempDirectory.PhysicalPath(other.Path).TrimEnd('/'), TempDirectory.PhysicalPath(workspace.Folders.Single().LocalPath.TrimEnd('/')));

        // a floating window's drops are taken by the same object, which keeps nothing of what it was attached to once detached (a plain control
        // stands for the window here: Avalonia.Headless keeps every Window it made reachable)
        await LeakAssert.NotRetainedAsync(() =>
        {
            var floating = new Border();
            main.FileDrop.Attach(floating).Dispose();
            Assert.False(DragDrop.GetAllowDrop(floating));
            return Task.FromResult<object[]>([floating]);
        });
    }
}
