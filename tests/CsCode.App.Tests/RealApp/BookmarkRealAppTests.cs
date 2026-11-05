using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using CsCode.App.Docking;
using CsCode.App.Input;
using CsCode.App.Services;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.ToolWindows.Bookmarks;
using CsCode.App.ToolWindows.Explorer;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.View;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Bookmarks on the REAL composition (docs/EDITOR.md §7g, docs/SHELL.md): the editor's command at the caret, the glyph in the breakpoint
/// margin Dap gives every editor, the Bookmark Window from its key and the menus, the bookmarks written to the workspace state at exit and read
/// back after a restart (re-anchored in a file that shrank), and an Explorer rename or delete taking them along.
/// </summary>
public class BookmarkRealAppTests
{
    private static async Task<(RealAppHost Host, EditorTab Tab, CodeEditorControl View)> StartAsync(string text)
    {
        var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("a.txt", text);
            var path = h.Home.Combine("config/settings.json");
            File.WriteAllText(path, File.ReadAllText(path).TrimEnd()[..^1].TrimEnd() + ",\n\"vim.enable\": false\n}\n");
        });
        await host.StartAsync([host.Files.Combine("a.txt")]);
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Count == 1));
        var tab = host.Shell.Well.EditorTabs[0];
        Assert.True(await RealAppHost.WaitFor(() => tab.LiveView is CodeEditorControl { IsEffectivelyVisible: true }));
        await DevScene.SettleAsync(10);
        var view = (CodeEditorControl)tab.LiveView!;
        view.Modal.Controller.Focus();
        Assert.True(await RealAppHost.WaitFor(() => view.Modal.Controller.IsFocused));
        await DevScene.SettleAsync(3);
        return (host, tab, view);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_bookmark_toggled_in_the_editor_is_drawn_in_its_breakpoint_margin_and_listed_in_the_Bookmark_Window_its_key_opens()
    {
        var (host, tab, view) = await StartAsync("alpha\nbeta\ngamma\n");
        await using var _ = host;
        var bookmarks = host.Get<IBookmarkService>();
        tab.Controller!.SetSelection(TextRange.At(1, 0), reveal: false);
        await host.RunAsync(BookmarkCommandIds.Toggle);
        Assert.Equal([1], bookmarks.GetForUri(tab.Uri).Select(b => b.Line));
        // one indicator margin, as in Visual Studio: the bookmark is drawn in the breakpoint margin (Dap is composed), none of its own
        var margins = view.GetVisualDescendants().OfType<Control>().Select(c => (Control: c, Name: AutomationProperties.GetName(c))).ToList();
        Assert.DoesNotContain(margins, m => m.Name == "Bookmark margin");
        var margin = margins.Single(m => m.Name == "Breakpoint margin").Control;
        await DevScene.SettleAsync(3);
        var origin = margin.TranslatePoint(new Point(0, 0), host.Window)!.Value;
        Assert.True(BluePixels(host.Window.CaptureRenderedFrame()!, new PixelRect((int)origin.X, (int)origin.Y, (int)margin.Bounds.Width, 120)) > 20,
            "the bookmark glyph (bookmarks.iconForeground) in the breakpoint margin's first rows");

        // Ctrl+K, Ctrl+W (⌘K ⌘W) through the real keymap and keyboard pipeline
        var tools = host.Shell.Tools;
        Assert.False(tools.IsVisible(BookmarkCommandIds.WindowId)); // not in the default layout
        Assert.Equal(KeyDispatch.Handled, host.Shell.Keyboard.Handle(KeyStrokeParser.ParseStroke(PlatformKeys.Stroke("ctrl+k", "cmd+k"))));
        Assert.Equal(KeyDispatch.Handled, host.Shell.Keyboard.Handle(KeyStrokeParser.ParseStroke(PlatformKeys.Stroke("ctrl+w", "cmd+w"))));
        Assert.True(await RealAppHost.WaitFor(() => tools.IsVisible(BookmarkCommandIds.WindowId) && tools.Get(BookmarkCommandIds.WindowId)!.LiveView is BookmarksView));
        var window = (BookmarksView)tools.Get(BookmarkCommandIds.WindowId)!.LiveView!;
        Assert.Contains(window.List.Items.Cast<BookmarkRow>(), r => r.Bookmark?.Line == 1 && r.Uri == tab.Uri);
        Assert.Single(host.Shell.Well.EditorTabs); // the editor stays open beside it
        await DevScene.SettleAsync(5);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        host.Window.CaptureRenderedFrame()!.Save(Path.Combine(DevHost.ScreenshotDirectory(), "bookmarks-window.png"), new PngBitmapEncoderOptions()); // .scratch (git-ignored)

        // Edit > Bookmarks and View > Bookmark Window carry the commands, enabled
        host.Shell.Menus.Refresh();
        var edit = host.Shell.Menus.Roots.Single(r => r.Header == "_Edit").Children.Single(c => c.Header == "_Bookmarks");
        var items = edit.Children.Where(c => !c.IsSeparator).ToDictionary(c => c.Node.CommandId!, c => c.IsEnabled);
        foreach (var id in new[] { BookmarkCommandIds.Toggle, BookmarkCommandIds.JumpToNext, BookmarkCommandIds.JumpToPrevious, BookmarkCommandIds.JumpToNextInDocument,
                     BookmarkCommandIds.JumpToPreviousInDocument, BookmarkCommandIds.Clear, BookmarkCommandIds.ClearFromAllFiles, BookmarkCommandIds.List, BookmarkCommandIds.ShowWindow })
            Assert.True(items.TryGetValue(id, out var enabled) && enabled, $"Edit > Bookmarks: {id}");
        Assert.Contains(host.Shell.Menus.Roots.Single(r => r.Header == "_View").Children, c => c.Node.CommandId == BookmarkCommandIds.ShowWindow && c.IsEnabled);
        Assert.Contains(host.Get<ICommandService>().All, c => c.Id == BookmarkCommandIds.Toggle && c.Title == "Toggle Bookmark" && c.Category == "Bookmarks");
    }

    /// <summary>Pixels of the default bookmark glyph colour (#157EFB, which no built-in theme overrides) inside <paramref name="region"/>.</summary>
    private static int BluePixels(Bitmap frame, PixelRect region)
    {
        using var ms = new MemoryStream();
        frame.Save(ms, new PngBitmapEncoderOptions());
        ms.Position = 0;
        using var bitmap = WriteableBitmap.Decode(ms);
        using var fb = bitmap.Lock();
        var row = new byte[fb.RowBytes];
        var count = 0;
        for (var y = Math.Max(0, region.Y); y < Math.Min(fb.Size.Height, region.Bottom); y++)
        {
            Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, row.Length);
            for (var x = Math.Max(0, region.X); x < Math.Min(fb.Size.Width, region.Right); x++)
            {
                var o = x * 4;
                var (r, g, b) = fb.Format == PixelFormat.Bgra8888 ? (row[o + 2], row[o + 1], row[o]) : (row[o], row[o + 1], row[o + 2]);
                if (Math.Abs(r - 0x15) <= 30 && Math.Abs(g - 0x7E) <= 30 && Math.Abs(b - 0xFB) <= 30) count++;
            }
        }
        return count;
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Bookmarks_written_just_before_exit_come_back_after_a_restart_on_their_lines_clamped_where_the_file_shrank()
    {
        using var env = new RealAppHome();
        var file = new Uri(env.Workspace.WriteFile("notes.txt", string.Join("\n", Enumerable.Range(0, 30).Select(i => $"line {i}")) + "\n"));
        await using (var run = new RealAppRun(env))
        {
            var main = await run.MainWindowAsync();
            await run.Services.GetRequiredService<IWorkspaceService>().OpenFolderAsync(env.Workspace.Uri);
            await run.Services.GetRequiredService<INavigationService>().OpenAsync(file);
            var bookmarks = run.Services.GetRequiredService<IBookmarkService>();
            bookmarks.Toggle(file, 3);
            var labelled = bookmarks.Add(file, 25, "near the end")!;
            bookmarks.Update(labelled with { Enabled = false }); // within the 400 ms debounce of the exit: the shutdown writes it
            main.Close();
            var report = await run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(report is { TimedOut.Count: 0, Failures.Count: 0 }, $"shutdown: {string.Join(" | ", report?.Failures ?? [])}");
        }

        File.WriteAllText(file.LocalPath, "only\nfive\nlines\nleft\nnow\n"); // edited outside CsCode while it was closed
        await using var after = AppServices.Build([], env.Configure);
        var restored = after.GetRequiredService<IBookmarkService>();
        await after.GetRequiredService<IWorkspaceService>().OpenFolderAsync(env.Workspace.Uri);
        Assert.True(await RealAppHost.WaitFor(() => restored.All.Count == 2), $"restored: {restored.All.Count}");
        Assert.Equal([(3, (string?)null, true), (25, "near the end", false)], restored.All.Select(b => (b.Line, b.Label, b.Enabled)));

        var factory = after.GetRequiredService<CsCode.Editor.IEditorViewFactory>();
        var doc = await factory.OpenDocumentAsync(file); // the file opens again: the bookmark past its end goes to its last line
        Assert.True(await RealAppHost.WaitFor(() => restored.All.Select(b => b.Line).SequenceEqual([3, 5])), string.Join(", ", restored.All.Select(b => b.Line)));
        await factory.CloseDocumentAsync(doc);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task An_Explorer_rename_takes_the_bookmarks_along_and_a_delete_removes_them()
    {
        using var env = new RealAppHome();
        var source = new Uri(env.Workspace.WriteFile("src/old.cs", "class Old\n{\n}\n"));
        var other = new Uri(env.Workspace.WriteFile("src/keep/other.cs", "x\ny\n"));
        await using var run = new RealAppRun(env);
        await run.MainWindowAsync();
        var sp = run.Services;
        await sp.GetRequiredService<IWorkspaceService>().OpenFolderAsync(env.Workspace.Uri);
        var bookmarks = sp.GetRequiredService<IBookmarkService>();
        bookmarks.Toggle(source, 1);
        bookmarks.Toggle(other, 1);
        using var explorer = new ExplorerViewModel(sp.GetRequiredService<IWorkspaceService>(), sp.GetRequiredService<IUiDispatcher>(), sp.GetRequiredService<INavigationService>(),
            sp.GetService<IConfigurationService>(), sp.GetService<IStateStorage>(), dialogs: null, sp.GetRequiredService<DocumentWell>(), sp.GetRequiredService<FileParticipants>());
        await explorer.RefreshRootsAsync();
        var src = explorer.Roots[0].Children!.Single(c => c.Name == "src");
        await explorer.ExpandAsync(src);

        var node = src.Children!.Single(c => c.Name == "old.cs");
        explorer.BeginRename(node);
        node.EditText = "new.cs";
        Assert.True(await explorer.CommitRenameAsync(node));
        var renamed = new Uri(env.Workspace.Combine("src", "new.cs"));
        Assert.Equal([(renamed, 1)], bookmarks.GetForUri(renamed).Select(b => (b.Uri, b.Line)));
        Assert.Empty(bookmarks.GetForUri(source));

        var keep = src.Children!.Single(c => c.Name == "keep");
        Assert.True(await explorer.DeleteAsync(keep)); // a folder: the bookmarks of every file under it
        Assert.Empty(bookmarks.GetForUri(other));
        Assert.Equal([renamed], bookmarks.All.Select(b => b.Uri));
    }
}
