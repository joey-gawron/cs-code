using Avalonia.Headless.XUnit;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Editor.Documents;
using CsCode.Vim;
using Xunit;

namespace CsCode.App.Tests.RealApp.Vim;

/// <summary>
/// T50 (data loss) with Vim on, in the real app: an Insert session continues one undo step over many keys, and a save in the middle of it (Ctrl+S
/// is the application's in Insert mode) must neither leave the document clean for different text nor split Vim's change: <c>u</c> still undoes
/// the whole session, through the editor's own history (two groups of one Vim step), and the dirty state follows the saved text.
/// </summary>
public class VimSaveDirtyTests
{
    private const string Quiet = "\"editor.quickSuggestions\": false";

    [AvaloniaFact(Timeout = 120_000)]
    public async Task An_insert_session_across_a_save_stays_dirty_and_u_undoes_the_whole_session()
    {
        await using var app = await VimApp.StartAsync("x\n", settings: Quiet);
        var path = app.Host.Files.Combine("test.txt");
        var doc = (EditorDocument)app.View.Document;
        var ledger = app.Vim.BufferOf(doc)!.Ledger;

        app.Keys("Aa");
        Assert.Equal(VimMode.Insert, app.Engine.Mode);
        Assert.True(doc.IsDirty);
        await app.KeysAsync(OperatingSystem.IsMacOS() ? "<D-s>" : "<C-s>", 10); // the application's Save, the Insert session goes on
        Assert.True(await RealAppHost.WaitFor(() => File.ReadAllText(path) == "xa\n"), File.ReadAllText(path));
        Assert.True(await RealAppHost.WaitFor(() => !doc.IsDirty));
        Assert.Equal(VimMode.Insert, app.Engine.Mode);

        app.Keys("b"); // same caret, same session, same document version as the save
        Assert.Equal("xab\n", app.Text);
        Assert.True(doc.IsDirty, "the text is 'xab' and the file holds 'xa': closing must prompt");
        app.Keys("c<Esc>");
        Assert.True(doc.IsDirty);
        var groupsBefore = ledger.UndoCount;

        app.Keys("u"); // one Vim change: the whole session, two groups of the editor's history
        Assert.Equal("x\n", app.Text);
        Assert.True(doc.IsDirty); // the file holds "xa"
        Assert.Equal(groupsBefore - 1, ledger.UndoCount); // the editor undid it (no fallback: the ledger moved one entry, not invalidated)
        Assert.Equal(1, ledger.RedoCount);
        app.Keys("<C-r>");
        Assert.Equal("xabc\n", app.Text);
        Assert.True(doc.IsDirty);

        // Saved at the end of a session and then a new one: the saved state is reachable by undo again.
        await app.KeysAsync(":w<CR>", 10);
        Assert.True(await RealAppHost.WaitFor(() => !doc.IsDirty));
        app.Keys("Ad<Esc>");
        Assert.True(doc.IsDirty);
        app.Keys("u");
        Assert.Equal("xabc\n", app.Text);
        Assert.False(doc.IsDirty);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task The_editors_undo_command_after_a_save_inside_an_insert_session_is_vims_u()
    {
        await using var app = await VimApp.StartAsync("one\n", settings: Quiet);
        var doc = (EditorDocument)app.View.Document;
        var commands = app.Host.Get<ICommandService>();

        app.Keys("otwo");
        await commands.ExecuteAsync(KnownCommands.Save);
        Assert.True(await RealAppHost.WaitFor(() => !doc.IsDirty));
        app.Keys("!<Esc>");
        Assert.Equal("one\ntwo!\n", app.Text);
        Assert.True(doc.IsDirty);

        await commands.ExecuteAsync("undo"); // the menu / toolbar Undo is Vim's u
        await DevScene.SettleAsync(3);
        Assert.Equal("one\n", app.Text);
        Assert.True(doc.IsDirty);
        await commands.ExecuteAsync("redo");
        await DevScene.SettleAsync(3);
        Assert.Equal("one\ntwo!\n", app.Text);
        Assert.True(doc.IsDirty);
    }
}
