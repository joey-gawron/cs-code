using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.ToolWindows.Bookmarks;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Bookmarks;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// The Bookmark Window (<see cref="BookmarksViewModel"/>, <see cref="BookmarksView"/>) over the editor's real <see cref="BookmarkService"/>: rows
/// by file with their line's text, rename, enable / disable, delete, go to, a file that is gone, the keys of the list, and that a closed window
/// leaves nothing behind.
/// </summary>
public class BookmarksWindowTests : IDisposable
{
    private readonly TempDirectory _dir = new("cscode-bookmarks-window-");
    private readonly DocumentManager _documents = new();
    private readonly RecordingWindowService _window = new();
    private readonly BookmarkService _service;
    private readonly RecordingNavigationService _navigation;
    private readonly TestCommandService _commands = new();

    public BookmarksWindowTests()
    {
        _service = new BookmarkService(_documents);
        _navigation = new RecordingNavigationService(_documents);
    }

    public void Dispose()
    {
        _service.Dispose();
        _dir.Dispose();
    }

    private BookmarksViewModel Model() => new(_service, new ImmediateUiDispatcher(), _navigation, _documents, new DiskFileSystem(), _commands, _window);

    private static string[] Shown(BookmarksViewModel vm) =>
        [.. vm.Rows.Select(r => r.IsHeader ? "# " + r.HeaderText![..r.HeaderText!.IndexOf("  ", StringComparison.Ordinal)] : $"{r.Name}|{r.Line}|{r.Preview}|{(r.Enabled ? "on" : "off")}")];

    [AvaloniaFact]
    public async Task The_window_lists_the_bookmarks_by_file_with_the_text_of_their_lines_and_follows_every_change()
    {
        var closed = new Uri(_dir.WriteFile("b/closed.cs", "first\n    second();\n"));
        var open = new InMemoryTextDocument(new Uri(_dir.Combine("a/open.cs")), "csharp", "int a;\nint b;\nint c;\n");
        using var registration = _documents.Register(open);
        _service.Toggle(open.Uri, 2);
        _service.Toggle(closed, 1);
        _service.Add(open.Uri, 0, "start here");
        using var vm = Model();
        foreach (var row in vm.Rows) await vm.LoadPreviewAsync(row); // what the list does as rows come on screen

        Assert.Equal(["# open.cs", "start here|1|int a;|on", "Line 3|3|int c;|on", "# closed.cs", "Line 2|2|second();|on"], Shown(vm));
        Assert.Equal("3 bookmarks in 2 files", vm.Summary);

        open.ApplyEdits([TextEdit.Insert(new TextPosition(2, 3), "eresting")]); // typed on a bookmarked line: no bookmark moved, the text follows
        Assert.Equal("interesting c;", vm.Rows[2].Preview);
        open.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "// header\n")]); // a line above: both move, the rows follow
        Assert.Equal(["# open.cs", "start here|2|int a;|on", "Line 4|4|interesting c;|on", "# closed.cs", "Line 2|2||on"], Shown(vm));

        _service.ClearAll();
        Assert.True(vm.IsEmpty);
        Assert.Equal("No bookmarks", vm.Summary);
    }

    [AvaloniaFact]
    public async Task Rename_disable_delete_and_go_to_act_on_the_bookmarks_and_a_vanished_file_loses_its_own()
    {
        var file = new Uri(_dir.WriteFile("x.cs", "a\nb\nc\nd\n"));
        var gone = new Uri(_dir.Combine("gone.cs"));
        _service.Toggle(file, 1);
        _service.Toggle(file, 3);
        _service.Toggle(gone, 0);
        using var vm = Model();
        BookmarkRow Row(Uri uri, int line) => vm.Rows.Single(r => r.Bookmark is { } b && b.Uri == uri && b.Line == line);

        _window.InputResponses.Enqueue("  the loop ");
        await vm.RenameAsync(Row(file, 1));
        Assert.Equal("the loop", _service.GetForUri(file)[0].Label);
        Assert.Equal("the loop", Row(file, 1).Name);
        _window.InputResponses.Enqueue("");
        await vm.RenameAsync(Row(file, 1)); // an empty name: no label, the window says "Line 2" again
        Assert.Equal("Line 2", Row(file, 1).Name);

        vm.SetEnabled(Row(file, 3), false);
        Assert.False(_service.GetForUri(file)[1].Enabled);
        vm.ToggleAllEnabled(); // some are enabled: all off
        Assert.All(_service.All, b => Assert.False(b.Enabled));
        vm.ToggleAllEnabled();
        Assert.All(_service.All, b => Assert.True(b.Enabled));

        await vm.NavigateAsync(Row(file, 3));
        Assert.Equal((file, (TextRange?)TextRange.At(3, 0)), _navigation.Opened[^1]);
        await vm.NavigateAsync(Row(gone, 0)); // the file is gone: nothing opens, its bookmarks go, the status bar says why
        Assert.Single(_navigation.Opened);
        Assert.DoesNotContain(_service.All, b => b.Uri == gone);
        Assert.Contains(_window.StatusMessages, m => m.Contains("gone.cs") && m.Contains("no longer exists"));

        vm.Delete(Row(file, 1));
        Assert.Equal([3], _service.GetForUri(file).Select(b => b.Line));
        vm.Delete(vm.Rows.Single(r => r.IsHeader)); // a file's row: all of its bookmarks
        Assert.Empty(_service.All);
    }

    [AvaloniaFact]
    public async Task In_the_list_Enter_goes_F2_renames_Space_disables_Delete_deletes_and_the_selection_stays_on_its_bookmark()
    {
        var file = new Uri(_dir.WriteFile("k.cs", "0\n1\n2\n3\n4\n"));
        _service.Toggle(file, 1);
        _service.Toggle(file, 4);
        var view = new BookmarksView(Model(), _window);
        var window = new Window { Width = 700, Height = 300, Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var list = view.List;
        list.SelectedItem = list.Items.Cast<BookmarkRow>().Single(r => r.Bookmark?.Line == 4);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(list.ContainerFromItem(list.SelectedItem!)?.Focus());

        async Task Press(Key key)
        {
            window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
            await DevScene.SettleAsync(3); // the rows are rebuilt and the keyboard put back on the selected one
        }
        await Press(Key.Enter);
        Assert.True(await RealAppHost.WaitFor(() => _navigation.Opened.Count == 1), "Enter went to the bookmark");
        Assert.Equal((file, (TextRange?)TextRange.At(4, 0)), _navigation.Opened[0]);

        _window.InputResponses.Enqueue("end");
        await Press(Key.F2);
        Assert.True(await RealAppHost.WaitFor(() => _service.GetForUri(file).Any(b => b.Label == "end")), "F2 renamed it");
        Assert.Equal(4, view.Selected?.Bookmark?.Line); // the rows were rebuilt: still on the same bookmark

        _service.Toggle(file, 0); // a bookmark added above: rebuilt again, the selection stays
        await DevScene.SettleAsync(3);
        Assert.Equal("end", view.Selected?.Name);
        await Press(Key.Space);
        Assert.False(_service.GetForUri(file).Single(b => b.Line == 4).Enabled);
        await Press(Key.Delete);
        Assert.Equal([0, 1], _service.GetForUri(file).Select(b => b.Line));

        Assert.Contains("_Go To Bookmark", view.RowMenu().Select(e => e.Header));
        window.Close();
        view.Dispose();
    }

    [AvaloniaFact]
    public void A_closed_window_is_not_retained_and_stops_listening()
    {
        var file = new Uri(_dir.WriteFile("m.cs", "0\n1\n"));
        _service.Toggle(file, 1);
        var baseline = _service.ChangedSubscriberCount;
        LeakAssert.NotRetained(() =>
        {
            var vm = Model();
            var view = new BookmarksView(vm, _window);
            Assert.Equal(baseline + 1, _service.ChangedSubscriberCount);
            _service.Toggle(file, 0);
            view.Dispose(); // what the tool window manager does when the window is hidden
            return [vm, view];
        });
        Assert.Equal(baseline, _service.ChangedSubscriberCount);
    }
}
