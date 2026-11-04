using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Docking;
using CsCode.App.Services;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.ToolWindows.Explorer;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.TestDoubles;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// The Explorer's drag and drop (VS Code's behaviour, docs/SHELL.md "Explorer"): the model's moves and copies with their questions (confirmation,
/// replace or skip), the view's drag source and drop target driven through Avalonia.Headless's drag events, drops from other applications, and open
/// editors following a move (the real composition). Drops on the rest of the window: <see cref="WindowFileDropTests"/>.
/// </summary>
public class ExplorerDragDropTests
{
    private sealed class Rig : IDisposable
    {
        public TempDirectory Dir { get; } = new("cscode-explorer-dnd-");
        public FakeWorkspaceService Workspace { get; } = new(new DiskFileSystem(), new FakeFileWatcher());
        public FakeModalDialogs Dialogs { get; } = new();
        public TestConfigurationService Config { get; } = new();
        public ExplorerViewModel Vm { get; }

        public Rig() => Vm = new ExplorerViewModel(Workspace, AvaloniaUiDispatcher.Instance, null, Config, new InMemoryStateStorage(), Dialogs);

        public async Task OpenAsync()
        {
            await Workspace.OpenFolderAsync(Dir.Uri);
            await Vm.RefreshCompleted;
        }

        /// <summary>The node at <paramref name="path"/> below the workspace folder ("dest/a.txt"; its folders loaded).</summary>
        public ExplorerNode Node(string path)
        {
            var node = Vm.Roots[0];
            foreach (var part in path.Split('/')) node = node.Children!.Single(c => c.Name == part);
            return node;
        }
        public string P(string rel) => Dir.Combine(rel.Split('/'));
        public void Dispose() { Vm.Dispose(); Dir.Dispose(); }
    }

    private static string Text(string path) => File.ReadAllText(path);

    // ------------------------------------------------------------------ the model: move, copy, refusals, questions

    [AvaloniaFact]
    public async Task A_move_asks_first_and_Dont_Ask_Again_turns_the_question_off()
    {
        using var rig = new Rig();
        rig.Dir.WriteFile("one.txt", "1");
        rig.Dir.WriteFile("two.txt", "2");
        rig.Dir.WriteFile("three.txt", "3");
        Directory.CreateDirectory(rig.P("dest"));
        await rig.OpenAsync();
        var dest = rig.Node("dest");

        rig.Dialogs.Answers.Enqueue("Cancel");
        Assert.Empty(await rig.Vm.DropAsync([rig.Node("one.txt").Uri], dest, copy: false));
        Assert.Equal("Are you sure you want to move 'one.txt' into 'dest'?", rig.Dialogs.Shown.Single().Message);
        Assert.True(File.Exists(rig.P("one.txt")));

        rig.Dialogs.Answers.Enqueue("Move");
        Assert.Single(await rig.Vm.DropAsync([rig.Node("one.txt").Uri], dest, copy: false));
        Assert.True(File.Exists(rig.P("dest/one.txt")));
        Assert.False(File.Exists(rig.P("one.txt")));

        rig.Dialogs.Answers.Enqueue("Move, Don't Ask Again");
        Assert.Single(await rig.Vm.DropAsync([rig.Node("two.txt").Uri], dest, copy: false));
        Assert.True(File.Exists(rig.P("dest/two.txt")));
        Assert.Equal(System.Text.Json.JsonValueKind.False, rig.Config.GetRaw(ExplorerViewModel.ConfirmDragAndDropSetting)?.ValueKind); // written: off now

        rig.Dialogs.Shown.Clear();
        Assert.Single(await rig.Vm.DropAsync([rig.Node("three.txt").Uri], dest, copy: false));
        Assert.Empty(rig.Dialogs.Shown); // not asked again
        Assert.True(File.Exists(rig.P("dest/three.txt")));
        Assert.True(rig.Node("dest").IsExpanded); // the folder dropped into is shown open, with what came in
        Assert.Equal("three.txt", rig.Vm.Selected?.Name);
    }

    [AvaloniaFact]
    public async Task Several_items_move_together_a_folder_carries_its_content_and_a_file_row_means_its_folder()
    {
        using var rig = new Rig();
        rig.Config.Set(ExplorerViewModel.ConfirmDragAndDropSetting, false);
        rig.Dir.WriteFile("a.txt", "a");
        rig.Dir.WriteFile("b.txt", "b");
        rig.Dir.WriteFile("sub/c.txt", "c");
        rig.Dir.WriteFile("dest/inside.txt", "");
        await rig.OpenAsync();
        await rig.Vm.ExpandAsync(rig.Node("sub"));
        await rig.Vm.ExpandAsync(rig.Node("dest"));
        Uri[] dragged = [rig.Node("a.txt").Uri, rig.Node("b.txt").Uri, rig.Node("sub").Uri, rig.Node("sub/c.txt").Uri];

        // dropped on a file row: into that file's folder
        var moved = await rig.Vm.DropAsync(dragged, rig.Node("dest/inside.txt"), copy: false);

        Assert.Equal(3, moved.Count); // c.txt went with its folder
        Assert.Equal("a", Text(rig.P("dest/a.txt")));
        Assert.Equal("b", Text(rig.P("dest/b.txt")));
        Assert.Equal("c", Text(rig.P("dest/sub/c.txt")));
        Assert.False(File.Exists(rig.P("a.txt")));
        Assert.False(Directory.Exists(rig.P("sub")));
        var root = Path.GetFileName(rig.Dir.Path);
        Assert.Equal([root, "dest", "sub", "a.txt", "b.txt", "inside.txt"], rig.Vm.Rows.Select(r => r.Name).ToArray());
    }

    [AvaloniaFact]
    public async Task The_copy_key_copies_and_a_copy_into_its_own_folder_is_named_like_a_duplicate()
    {
        using var rig = new Rig();
        rig.Dir.WriteFile("x.txt", "x");
        rig.Dir.WriteFile("dir/y.txt", "y");
        Directory.CreateDirectory(rig.P("dest"));
        await rig.OpenAsync();

        var copied = await rig.Vm.DropAsync([rig.Node("x.txt").Uri, rig.Node("dir").Uri], rig.Node("dest"), copy: true);
        Assert.Equal(2, copied.Count);
        Assert.Empty(rig.Dialogs.Shown); // a copy is not confirmed (VS Code asks for moves only)
        Assert.Equal("x", Text(rig.P("x.txt")));
        Assert.Equal("x", Text(rig.P("dest/x.txt")));
        Assert.Equal("y", Text(rig.P("dest/dir/y.txt")));
        Assert.True(File.Exists(rig.P("dir/y.txt")));

        // onto its own folder: allowed for a copy, which gets the duplicate's name
        Assert.True(rig.Vm.CanDrop([rig.Node("x.txt").Uri], rig.Vm.Roots[0], copy: true));
        Assert.Single(await rig.Vm.DropAsync([rig.Node("x.txt").Uri], rig.Vm.Roots[0], copy: true));
        Assert.Equal("x", Text(rig.P("x copy.txt")));
        Assert.Empty(rig.Dialogs.Shown);

        Assert.True(ExplorerDrag.IsCopy(KeyModifiers.Alt, mac: true));
        Assert.False(ExplorerDrag.IsCopy(KeyModifiers.Control, mac: true));
        Assert.True(ExplorerDrag.IsCopy(KeyModifiers.Control, mac: false));
        Assert.False(ExplorerDrag.IsCopy(KeyModifiers.Alt, mac: false));
    }

    [AvaloniaFact]
    public async Task A_folder_never_goes_into_itself_or_below_itself_and_a_move_where_it_already_is_does_nothing()
    {
        using var rig = new Rig();
        rig.Config.Set(ExplorerViewModel.ConfirmDragAndDropSetting, false);
        rig.Dir.WriteFile("a/b/f.txt", "");
        rig.Dir.WriteFile("top.txt", "");
        await rig.OpenAsync();
        var a = rig.Node("a");
        await rig.Vm.ExpandAsync(a);
        var b = rig.Node("a/b");
        await rig.Vm.ExpandAsync(b);
        var root = rig.Vm.Roots[0];

        foreach (var copy in new[] { false, true })
        {
            Assert.False(rig.Vm.CanDrop([a.Uri], a, copy));                    // onto itself
            Assert.False(rig.Vm.CanDrop([a.Uri], b, copy));                    // below itself
            Assert.False(rig.Vm.CanDrop([a.Uri], rig.Node("a/b/f.txt"), copy));    // a file below it: its folder b
            Assert.False(rig.Vm.CanDrop([root.Uri], b, copy));                 // a workspace folder stays where it is
            Assert.Empty(await rig.Vm.DropAsync([a.Uri], b, copy));
        }
        Assert.False(rig.Vm.CanDrop([rig.Node("top.txt").Uri], root, copy: false)); // where it already is
        Assert.False(rig.Vm.CanDrop([b.Uri], a, copy: false));
        Assert.False(rig.Vm.CanDrop([b.Uri, rig.Node("top.txt").Uri], root, copy: false)); // one of them is: the drop is refused, as VS Code's
        Assert.True(rig.Vm.CanDrop([b.Uri], root, copy: false));
        Assert.Empty(await rig.Vm.DropAsync([rig.Node("top.txt").Uri], root, copy: false));
        Assert.True(Directory.Exists(rig.P("a/b")));
        Assert.True(File.Exists(rig.P("top.txt")));
        Assert.Empty(rig.Dialogs.Shown);
    }

    [AvaloniaFact]
    public async Task A_name_taken_in_the_folder_asks_to_replace_skip_or_cancel()
    {
        using var rig = new Rig();
        rig.Config.Set(ExplorerViewModel.ConfirmDragAndDropSetting, false);
        rig.Dir.WriteFile("a.txt", "new a");
        rig.Dir.WriteFile("b.txt", "new b");
        rig.Dir.WriteFile("c.txt", "new c");
        rig.Dir.WriteFile("dest/a.txt", "old a");
        rig.Dir.WriteFile("dest/b.txt", "old b");
        await rig.OpenAsync();
        Uri[] All() => [rig.Node("a.txt").Uri, rig.Node("b.txt").Uri, rig.Node("c.txt").Uri];

        rig.Dialogs.Answers.Enqueue("Cancel");
        Assert.Empty(await rig.Vm.DropAsync(All(), rig.Node("dest"), copy: false));
        var (title, message, buttons) = rig.Dialogs.Shown.Single();
        Assert.Equal("Replace", title);
        Assert.StartsWith("The following 2 files and folders already exist in 'dest':\n\na.txt\nb.txt\n\nDo you want to replace them?", message);
        Assert.Equal(["Replace", "Skip", "Cancel"], buttons);
        Assert.True(File.Exists(rig.P("c.txt"))); // cancel: nothing moved

        rig.Dialogs.Answers.Enqueue("Skip");
        Assert.Equal([new Uri(rig.P("dest/c.txt"))], await rig.Vm.DropAsync(All(), rig.Node("dest"), copy: false));
        Assert.Equal("old a", Text(rig.P("dest/a.txt")));
        Assert.Equal("new a", Text(rig.P("a.txt")));
        Assert.Equal("new c", Text(rig.P("dest/c.txt")));

        rig.Dialogs.Shown.Clear();
        rig.Dialogs.Answers.Enqueue("Replace");
        Assert.Single(await rig.Vm.DropAsync([rig.Node("a.txt").Uri], rig.Node("dest"), copy: true));
        Assert.StartsWith("A file or folder with the name 'a.txt' already exists in 'dest'. Do you want to replace it?", rig.Dialogs.Shown.Single().Message);
        Assert.Equal("new a", Text(rig.P("dest/a.txt"))); // replaced by the copy
        Assert.Equal("new a", Text(rig.P("a.txt")));

        rig.Dialogs.Answers.Enqueue("Replace");
        Assert.Single(await rig.Vm.DropAsync([rig.Node("b.txt").Uri], rig.Node("dest"), copy: false));
        Assert.Equal("new b", Text(rig.P("dest/b.txt"))); // replaced by the move
        Assert.False(File.Exists(rig.P("b.txt")));
    }

    [AvaloniaFact]
    public async Task Files_dropped_from_another_application_are_copied_into_the_folder()
    {
        using var rig = new Rig();
        using var outside = new TempDirectory("cscode-explorer-dnd-os-");
        var file = outside.WriteFile("notes.md", "notes");
        outside.WriteFile("folder/deep/x.txt", "x");
        rig.Dir.WriteFile("dest/keep.txt", "");
        rig.Dir.WriteFile("dest/notes.md", "old notes");
        await rig.OpenAsync();
        var dest = rig.Node("dest");
        await rig.Vm.ExpandAsync(dest);
        Uri[] dropped = [new Uri(file), new Uri(outside.Combine("folder") + "/")];

        Assert.True(rig.Vm.CanDropExternal(dropped, dest));
        rig.Dialogs.Answers.Enqueue("Skip");
        var created = await rig.Vm.DropExternalAsync(dropped, rig.Node("dest/keep.txt")); // a file row: its folder
        Assert.Single(created);
        Assert.Contains("'notes.md' already exists in 'dest'", rig.Dialogs.Shown.Single().Message);
        Assert.Equal("x", Text(rig.P("dest/folder/deep/x.txt")));
        Assert.Equal("old notes", Text(rig.P("dest/notes.md")));
        Assert.True(File.Exists(file)); // copied, never moved
        Assert.True(File.Exists(outside.Combine("folder", "deep", "x.txt")));

        rig.Dialogs.Answers.Enqueue("Replace");
        Assert.Single(await rig.Vm.DropExternalAsync([new Uri(file)], dest));
        Assert.Equal("notes", Text(rig.P("dest/notes.md")));

        // the workspace's own folder dropped into itself (from Finder): refused
        Assert.False(rig.Vm.CanDropExternal([rig.Dir.Uri], dest));
        Assert.Empty(await rig.Vm.DropExternalAsync([rig.Dir.Uri], dest));
    }

    [AvaloniaFact]
    public async Task Undo_puts_moved_items_back_and_sends_copies_to_the_trash_after_asking()
    {
        using var rig = new Rig();
        rig.Config.Set(ExplorerViewModel.ConfirmDragAndDropSetting, false);
        rig.Dir.WriteFile("m.txt", "m");
        rig.Dir.WriteFile("c.txt", "c");
        Directory.CreateDirectory(rig.P("dest"));
        await rig.OpenAsync();
        Assert.False(rig.Vm.CanUndo);

        await rig.Vm.DropAsync([rig.Node("m.txt").Uri], rig.Node("dest"), copy: false);
        Assert.True(rig.Vm.CanUndo);
        Assert.True(await rig.Vm.UndoAsync());
        Assert.Equal("m", Text(rig.P("m.txt")));
        Assert.False(File.Exists(rig.P("dest/m.txt")));
        Assert.False(rig.Vm.CanUndo); // the last one only

        await rig.Vm.DropAsync([rig.Node("c.txt").Uri], rig.Node("dest"), copy: true);
        rig.Dialogs.Answers.Enqueue("Cancel");
        Assert.False(await rig.Vm.UndoAsync());
        Assert.True(File.Exists(rig.P("dest/c.txt")));
        rig.Dialogs.Answers.Enqueue("Undo");
        Assert.True(await rig.Vm.UndoAsync());
        Assert.False(File.Exists(rig.P("dest/c.txt")));
        Assert.True(File.Exists(rig.P("c.txt")));

        await rig.Vm.DropAsync([rig.Node("c.txt").Uri], rig.Node("dest"), copy: false);
        rig.Config.Set(ExplorerViewModel.EnableUndoSetting, false);
        Assert.False(rig.Vm.CanUndo);
        Assert.False(await rig.Vm.UndoAsync());
        Assert.True(File.Exists(rig.P("dest/c.txt")));
    }

    // ------------------------------------------------------------------ the view: drag source and drop target (Avalonia.Headless drag events)

    private sealed class ViewRig : IDisposable
    {
        public Rig Model { get; } = new();
        public ExplorerView View { get; private set; } = null!;
        public Window Window { get; private set; } = null!;
        public ListBox List => View.GetVisualDescendants().OfType<ListBox>().First();
        public IDataTransfer? Dragged { get; private set; }
        public DragDropEffects Offered { get; private set; }
        private TaskCompletionSource<DragDropEffects> _drag = new();

        public async Task OpenAsync(int height = 400)
        {
            await Model.OpenAsync();
            View = new ExplorerView(Model.Vm, new RecordingWindowService()) { MacKeys = OperatingSystem.IsMacOS() };
            // Avalonia.Headless has no drag source: the drag the view starts is captured here and played back with the window's drag events.
            View.StartDragDrop = (_, data, effects) =>
            {
                Dragged = data;
                Offered = effects;
                _drag = new TaskCompletionSource<DragDropEffects>();
                return _drag.Task;
            };
            Window = new Window { Width = 300, Height = height, Content = View };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public ListBoxItem Row(string name) => View.GetVisualDescendants().OfType<ListBoxItem>()
            .First(r => r.IsEffectivelyVisible && r.DataContext is ExplorerNode n && n.Name == name);

        public Point At(string name)
        {
            Dispatcher.UIThread.RunJobs();
            var row = Row(name);
            return row.TranslatePoint(new Point(40, row.Bounds.Height / 2), Window)!.Value;
        }

        /// <summary>Press on a row and move the pointer far enough to start a drag (the view captures its data).</summary>
        public void StartDrag(string name, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            var p = At(name);
            Window.MouseDown(p, MouseButton.Left, modifiers);
            Window.MouseMove(p + new Point(0, 12), RawInputModifiers.LeftMouseButton | modifiers);
            Dispatcher.UIThread.RunJobs();
        }

        public void Drag(RawDragEventType type, Point p, IDataTransfer data, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            Window.DragDrop(p, type, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link, modifiers);
            Dispatcher.UIThread.RunJobs();
        }

        public void EndDrag()
        {
            _drag.TrySetResult(DragDropEffects.Copy);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>The rig lets go of the last drag's data (and the platform's end of it).</summary>
        public void ForgetDrag()
        {
            Dragged = null;
            _drag = new TaskCompletionSource<DragDropEffects>();
        }

        /// <summary>Closes the window and disposes the view (with its model's rows); the rig no longer holds either.</summary>
        public ExplorerView ReleaseView()
        {
            var view = View;
            Window.Close();
            view.Dispose();
            View = null!;
            Window = null!;
            return view;
        }

        public void Dispose()
        {
            _drag.TrySetResult(DragDropEffects.None);
            if (View is not null) ReleaseView();
            Model.Dispose();
        }
    }

    private static RawInputModifiers CopyKey => OperatingSystem.IsMacOS() ? RawInputModifiers.Alt : RawInputModifiers.Control;

    [AvaloniaFact]
    public async Task Dragging_selected_rows_onto_a_folder_row_highlights_it_and_moves_them_there()
    {
        using var rig = new ViewRig();
        rig.Model.Config.Set(ExplorerViewModel.ConfirmDragAndDropSetting, false);
        rig.Model.Dir.WriteFile("a.txt", "a");
        rig.Model.Dir.WriteFile("b.txt", "b");
        rig.Model.Dir.WriteFile("c.txt", "c");
        Directory.CreateDirectory(rig.Model.P("dest"));
        await rig.OpenAsync();
        var list = rig.List;
        list.SelectedItems!.Clear();
        list.SelectedItems.Add(rig.Model.Node("a.txt"));
        list.SelectedItems.Add(rig.Model.Node("c.txt"));
        Dispatcher.UIThread.RunJobs();

        rig.StartDrag("c.txt"); // a plain press on one of the selected rows keeps them all
        Assert.NotNull(rig.Dragged);
        Assert.Equal(DragDropEffects.Copy, rig.Offered); // other applications get a copy, never the file itself
        Assert.Equal([rig.Model.Node("a.txt").Uri, rig.Model.Node("c.txt").Uri], ExplorerDrag.Items(rig.Dragged!));
        Assert.Equal(2, list.SelectedItems.Count);

        var dest = rig.Model.Node("dest");
        rig.Drag(RawDragEventType.DragEnter, rig.At("dest"), rig.Dragged!);
        rig.Drag(RawDragEventType.DragOver, rig.At("dest"), rig.Dragged!);
        Assert.True(dest.IsDropTarget);
        rig.Drag(RawDragEventType.DragOver, rig.At("b.txt"), rig.Dragged!); // a file row: its folder, the root, where a and c already are
        Assert.False(dest.IsDropTarget);
        Assert.False(rig.Model.Vm.Roots[0].IsDropTarget); // refused: no highlight
        rig.Drag(RawDragEventType.DragOver, rig.At("dest"), rig.Dragged!);
        rig.Drag(RawDragEventType.Drop, rig.At("dest"), rig.Dragged!);
        rig.EndDrag();
        var moved = await rig.View.LastDrop;

        Assert.Equal(2, moved.Count);
        Assert.False(dest.IsDropTarget);
        Assert.Equal("a", Text(rig.Model.P("dest/a.txt")));
        Assert.Equal("c", Text(rig.Model.P("dest/c.txt")));
        Assert.True(File.Exists(rig.Model.P("b.txt")));
        Assert.False(File.Exists(rig.Model.P("a.txt")));
    }

    [AvaloniaFact]
    public async Task The_copy_key_held_at_the_drop_copies()
    {
        using var rig = new ViewRig();
        rig.Model.Dir.WriteFile("a.txt", "a");
        Directory.CreateDirectory(rig.Model.P("dest"));
        await rig.OpenAsync();

        rig.StartDrag("a.txt");
        Assert.Equal([rig.Model.Node("a.txt").Uri], ExplorerDrag.Items(rig.Dragged!)); // the pressed row alone
        rig.Drag(RawDragEventType.DragOver, rig.At("dest"), rig.Dragged!, CopyKey);
        rig.Drag(RawDragEventType.Drop, rig.At("dest"), rig.Dragged!, CopyKey);
        rig.EndDrag();
        Assert.Single(await rig.View.LastDrop);

        Assert.Empty(rig.Model.Dialogs.Shown); // copies are not confirmed
        Assert.Equal("a", Text(rig.Model.P("dest/a.txt")));
        Assert.Equal("a", Text(rig.Model.P("a.txt")));
    }

    [AvaloniaFact]
    public async Task A_press_on_one_of_several_selected_rows_without_a_drag_selects_that_row_alone()
    {
        using var rig = new ViewRig();
        rig.Model.Dir.WriteFile("a.txt", "");
        rig.Model.Dir.WriteFile("b.txt", "");
        await rig.OpenAsync();
        var list = rig.List;
        list.SelectedItems!.Clear();
        list.SelectedItems.Add(rig.Model.Node("a.txt"));
        list.SelectedItems.Add(rig.Model.Node("b.txt"));
        Dispatcher.UIThread.RunJobs();

        var p = rig.At("b.txt");
        rig.Window.MouseDown(p, MouseButton.Left);
        Assert.Equal(2, list.SelectedItems.Count); // kept while it may become a drag
        rig.Window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([rig.Model.Node("b.txt")], list.SelectedItems.Cast<ExplorerNode>().ToArray());
        Assert.Null(rig.Dragged);
    }

    [AvaloniaFact]
    public async Task Hovering_a_collapsed_folder_during_a_drag_opens_it_after_a_moment()
    {
        using var rig = new ViewRig();
        rig.Model.Dir.WriteFile("a.txt", "");
        rig.Model.Dir.WriteFile("dest/inner/deep.txt", "");
        await rig.OpenAsync();
        rig.StartDrag("a.txt");
        var dest = rig.Model.Node("dest");

        rig.Drag(RawDragEventType.DragOver, rig.At("dest"), rig.Dragged!);
        Assert.False(dest.IsExpanded); // not at once
        await Task.Delay(ExplorerView.ExpandOnHoverDelay + TimeSpan.FromMilliseconds(400));
        Dispatcher.UIThread.RunJobs();
        Assert.True(await DevHost.WaitFor(() => dest.IsExpanded, 5_000));
        Assert.NotNull(rig.Row("inner")); // the drag can go deeper now

        // leaving the folder before the moment passes keeps it closed
        var inner = rig.Model.Node("dest/inner");
        rig.Drag(RawDragEventType.DragOver, rig.At("inner"), rig.Dragged!);
        rig.Drag(RawDragEventType.DragOver, rig.At("a.txt"), rig.Dragged!);
        await Task.Delay(ExplorerView.ExpandOnHoverDelay + TimeSpan.FromMilliseconds(400));
        Dispatcher.UIThread.RunJobs();
        Assert.False(inner.IsExpanded);
        rig.EndDrag();
    }

    [AvaloniaFact]
    public async Task A_drag_near_the_bottom_edge_scrolls_the_rows()
    {
        using var rig = new ViewRig();
        for (var i = 0; i < 60; i++) rig.Model.Dir.WriteFile($"f{i:D2}.txt", "");
        await rig.OpenAsync(height: 300);
        rig.StartDrag("f00.txt");
        var scroller = rig.List.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.Equal(0, scroller.Offset.Y);

        var bottom = rig.List.TranslatePoint(new Point(40, rig.List.Bounds.Height - 6), rig.Window)!.Value;
        rig.Drag(RawDragEventType.DragOver, bottom, rig.Dragged!);
        Assert.True(scroller.Offset.Y > 0);
        var after = scroller.Offset.Y;
        await Task.Delay(300); // it keeps scrolling while the pointer rests there
        Dispatcher.UIThread.RunJobs();
        Assert.True(await DevHost.WaitFor(() => scroller.Offset.Y > after, 5_000));

        var middle = rig.List.TranslatePoint(new Point(40, rig.List.Bounds.Height / 2), rig.Window)!.Value;
        rig.Drag(RawDragEventType.DragOver, middle, rig.Dragged!);
        var stopped = scroller.Offset.Y;
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(stopped, scroller.Offset.Y);
        rig.EndDrag();
    }

    [AvaloniaFact]
    public async Task Files_from_another_application_dropped_on_a_folder_row_are_copied_there()
    {
        using var rig = new ViewRig();
        using var outside = new TempDirectory("cscode-explorer-dnd-os-");
        var file = outside.WriteFile("from-finder.txt", "finder");
        Directory.CreateDirectory(rig.Model.P("dest"));
        await rig.OpenAsync();
        using var data = await FilesFromAnotherAppAsync(rig.Window, file);

        rig.Drag(RawDragEventType.DragEnter, rig.At("dest"), data);
        rig.Drag(RawDragEventType.DragOver, rig.At("dest"), data);
        Assert.True(rig.Model.Node("dest").IsDropTarget);
        rig.Drag(RawDragEventType.Drop, rig.At("dest"), data);
        Assert.Single(await rig.View.LastDrop);

        Assert.Equal("finder", Text(rig.Model.P("dest/from-finder.txt")));
        Assert.True(File.Exists(file));
        Assert.Empty(rig.Model.Dialogs.Shown);
    }

    // What a drag sets up holds the view while it runs (a highlighted node, the timers of the folder about to open and of the scrolling rows, the
    // drag's items in ExplorerDrag.Current): all of it goes when the drag ends without a drop, and nothing keeps the drag's data afterwards.
    [AvaloniaFact]
    public async Task A_drag_that_ends_without_a_drop_leaves_nothing_behind()
    {
        using var rig = new ViewRig();
        for (var i = 0; i < 40; i++) rig.Model.Dir.WriteFile($"f{i:D2}.txt", "");
        Directory.CreateDirectory(rig.Model.P("dest"));
        await rig.OpenAsync(height: 300);
        var dest = rig.Model.Node("dest");
        await LeakAssert.NotRetainedAsync(async () =>
        {
            rig.StartDrag("f00.txt");
            var data = rig.Dragged!;
            rig.Drag(RawDragEventType.DragOver, rig.At("dest"), data); // highlighted, about to open
            Assert.True(dest.IsDropTarget);
            Assert.True(rig.View.DragFeedbackActive);
            rig.Drag(RawDragEventType.DragOver, rig.List.TranslatePoint(new Point(40, rig.List.Bounds.Height - 6), rig.Window)!.Value, data); // scrolling
            Assert.True(rig.View.DragFeedbackActive);
            Assert.NotNull(ExplorerDrag.Items(new DataTransfer())); // the running drag's items, for platforms that do not hand them back
            rig.EndDrag();
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Assert.False(rig.View.DragFeedbackActive);
            Assert.False(dest.IsDropTarget);
            Assert.Null(ExplorerDrag.Items(new DataTransfer()));
            var payload = ExplorerDrag.Items(data)!;
            rig.ForgetDrag();
            return [data, payload];
        });
        await Task.Delay(ExplorerView.ExpandOnHoverDelay + TimeSpan.FromMilliseconds(300));
        Dispatcher.UIThread.RunJobs();
        Assert.False(dest.IsExpanded); // the folder hovered did not open after the drag ended
    }

    // ------------------------------------------------------------------ the real window: editors following a move

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Open_editors_follow_a_move_in_place_with_their_unsaved_changes()
    {
        using var env = new RealAppHome();
        var file = env.Workspace.WriteFile("a/x.txt", "on disk\n");
        var nested = env.Workspace.WriteFile("a/sub/y.txt", "y\n");
        Directory.CreateDirectory(env.Workspace.Combine("b"));
        await using var run = new RealAppRun(env);
        await run.MainWindowAsync();
        var sp = run.Services;
        await sp.GetRequiredService<IWorkspaceService>().OpenFolderAsync(env.Workspace.Uri);
        var nav = sp.GetRequiredService<INavigationService>();
        var well = sp.GetRequiredService<DocumentWell>();
        var doc = await nav.OpenAsync(new Uri(file)) ?? throw new InvalidOperationException("not opened");
        var end = doc.PositionAt(doc.Length);
        Assert.True(doc.ApplyEdits([new TextEdit(new TextRange(end, end), "unsaved")]));
        var tab = well.Find(new Uri(file))!;
        Assert.NotNull(await nav.OpenAsync(new Uri(nested)));
        var nestedTab = well.Find(new Uri(nested))!;
        var documents = sp.GetRequiredService<IDocumentManager>();

        using var explorer = new ExplorerViewModel(sp.GetRequiredService<IWorkspaceService>(), sp.GetRequiredService<IUiDispatcher>(), nav,
            sp.GetService<IConfigurationService>(), sp.GetService<IStateStorage>(), dialogs: null, well, sp.GetRequiredService<FileParticipants>());
        await explorer.RefreshRootsAsync();
        var a = explorer.Roots[0].Children!.Single(c => c.Name == "a");
        var b = explorer.Roots[0].Children!.Single(c => c.Name == "b");
        await explorer.ExpandAsync(a);
        var x = a.Children!.Single(c => c.Name == "x.txt");
        var sub = a.Children!.Single(c => c.Name == "sub");

        Assert.Equal(2, (await explorer.DropAsync([x.Uri, sub.Uri], b, copy: false)).Count);

        var movedFile = new Uri(env.Workspace.Combine("b", "x.txt"));
        var movedNested = new Uri(env.Workspace.Combine("b", "sub", "y.txt"));
        Assert.Same(tab, well.Find(movedFile)); // the same tab, re-pointed
        Assert.Same(doc, tab.Doc);
        Assert.Equal(movedFile, doc.Uri);
        Assert.True(doc.IsDirty);
        Assert.Equal("on disk\nunsaved", doc.GetText().ReplaceLineEndings("\n"));
        Assert.Equal("x.txt", tab.Title);
        Assert.Equal("on disk\n", File.ReadAllText(movedFile.LocalPath).ReplaceLineEndings("\n")); // nothing was saved
        Assert.Same(nestedTab, well.Find(movedNested)); // a file inside a moved folder too
        Assert.Null(well.Find(new Uri(file)));
        Assert.NotNull(documents.Get(movedFile)); // language servers see the new path open and the old one closed
        Assert.Null(documents.Get(new Uri(file)));

        // saving writes the new path
        await well.SaveAsync(tab);
        Assert.Equal("on disk\nunsaved", File.ReadAllText(movedFile.LocalPath).ReplaceLineEndings("\n"));
        Assert.False(File.Exists(file));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The data of a drag from another application (Finder): the files as the platform's storage items (Avalonia's own, made by the window's
    /// storage provider, as a platform's drop hands them over).
    /// </summary>
    internal static async Task<DataTransfer> FilesFromAnotherAppAsync(TopLevel top, string path, bool folder = false)
    {
        IStorageItem? item = folder
            ? await top.StorageProvider.TryGetFolderFromPathAsync(new Uri(path))
            : await top.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
        Assert.NotNull(item);
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateFile(item!));
        return data;
    }
}
