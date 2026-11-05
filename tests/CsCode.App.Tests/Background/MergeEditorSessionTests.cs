using Avalonia.Headless.XUnit;
using CsCode.App.Docking;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.RealApp;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Editor.Diff;
using CsCode.Sessions;
using CsCode.Sessions.Ipc;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests.Background;

/// <summary>
/// A merge editor's unsaved result that no editor tab shows, in a REAL session process (docs/SESSIONS.md, hot exit): quitting asks to save it like
/// any dirty document, and hot exit backs it up so the next start brings it back (as the file in an editor tab, with the unsaved changes).
/// </summary>
public class MergeEditorSessionTests
{
    /// <summary>Opens the merge editor over <paramref name="path"/> (conflict markers) and takes the incoming side: the result is the user's change.</summary>
    private static async Task<DocumentWell> OpenAndResolveAsync(InProcessSession s, string path)
    {
        await s.Run.Services.GetRequiredService<IMergeEditorService>().OpenMergeEditorAsync(MergeConflictTests.Request(new Uri(path)));
        var well = s.Run.Services.GetRequiredService<DocumentWell>();
        Assert.True(await DevHost.WaitFor(() => well.Active?.LiveView is MergeEditorControl { Merge: not null }, 20_000), "merge editor");
        ((MergeEditorControl)well.Active!.LiveView!).Set(0, MergeSides.Incoming);
        Assert.Empty(well.EditorTabs);
        Assert.True(well.HasUnsavedChanges);
        return well;
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task Quitting_asks_to_save_a_merge_editors_unsaved_result_that_no_editor_tab_shows()
    {
        using var files = new TempDirectory("cscode-merge-quit-");
        var path = files.WriteFile("c.txt", MergeConflictTests.Markers);
        await using var s = await InProcessSession.StartAsync("mq", "\"files.hotExit\": \"off\"");
        await OpenAndResolveAsync(s, path);

        s.Dialogs.Answers.Enqueue("Save");
        var quit = await s.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false }, 60);
        Assert.True(quit.Ok == true, quit.Error);
        var prompt = Assert.Single(s.Dialogs.Shown);
        Assert.Contains("c.txt", prompt.Message, StringComparison.Ordinal);
        Assert.Contains("Don't Save", prompt.Buttons);
        await s.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("one\ntheirs\ntwo\n", File.ReadAllText(path));
        Assert.Empty(s.Store.Backups.List("mq"));
    }

    [AvaloniaFact(Timeout = 240_000)]
    public async Task Hot_exit_backs_up_a_merge_editors_unsaved_result_and_the_next_start_brings_it_back_in_an_editor_tab()
    {
        using var files = new TempDirectory("cscode-merge-hotexit-");
        var path = files.WriteFile("c.txt", MergeConflictTests.Markers);
        var uri = new Uri(path).ToString();
        SessionDocument saved;
        List<BackupEntry> backups;
        await using (var first = await InProcessSession.StartAsync("mh"))
        {
            await OpenAndResolveAsync(first, path);
            // the autosave backs it up already (crash recovery)
            Assert.True(await DevHost.WaitFor(() => first.Store.Backups.List("mh") is [var b] && b.Uri == uri, 15_000), "the autosave wrote no backup");

            var quit = await first.RequestAsync(new IpcMessage { Type = IpcVerbs.Quit, Force = false }, 60);
            Assert.True(quit.Ok == true, quit.Error);
            await first.Run.Session.Ended.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Empty(first.Dialogs.Shown); // hot exit: no prompt
            Assert.Equal(MergeConflictTests.Markers, File.ReadAllText(path)); // hot exit never writes the user's files

            saved = first.Store.Load("mh").Session!;
            var editor = Assert.Single(saved.Editors, e => e.Uri == uri);
            Assert.NotNull(editor.BackupId);
            var info = Assert.Single(first.Store.Backups.List("mh"));
            Assert.Equal(editor.BackupId, info.Id);
            Assert.Equal("one\ntheirs\ntwo\n", first.Store.Backups.ReadText("mh", info.Id));
            backups = [new BackupEntry(info.Id, info.Uri, first.Store.Backups.ReadText("mh", info.Id)!, info.Encoding, info.EndOfLine, info.LanguageId, info.DocumentVersion)];
        }

        // the next start of the session (its saved state and backups, in a fresh home): the file in an editor tab, with the unsaved result
        await using var second = await InProcessSession.StartAsync("mh", seed: store =>
        {
            store.Save(saved);
            store.Backups.Write("mh", backups, [], long.MaxValue);
        });
        var restored = await UntilRestoredAsync(second, uri);
        Assert.True(restored.Dirty);
        Assert.Equal("one\ntheirs\ntwo\n", restored.Text);
        Assert.Equal(MergeConflictTests.Markers, File.ReadAllText(path));
    }

    private static async Task<CsCode.App.Background.InspectedTab> UntilRestoredAsync(InProcessSession s, string uri)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var state = await s.InspectAsync(withText: true);
            if (state.Tabs.FirstOrDefault(t => t.Uri == uri) is { Text: not null } tab) return tab;
            if (clock.ElapsedMilliseconds > 20_000) throw new TimeoutException("the backup was not restored: " + string.Join(", ", state.Tabs.Select(t => t.Uri)));
            await Task.Delay(50);
        }
    }
}
