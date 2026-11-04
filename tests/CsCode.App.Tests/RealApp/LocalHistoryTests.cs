using Avalonia.Headless.XUnit;
using CsCode.App.Docking;
using CsCode.App.Services;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.ToolWindows.Explorer;
using CsCode.Core.History;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Timeline;
using CsCode.Panels.Timeline.LocalHistory;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Local history on the REAL composition (docs/LOCAL-HISTORY.md): the shell's save of a real editor document stores it, the Timeline window
/// lists it, Restore Contents is one undo step in the real editor, and an Explorer rename takes the entries along.
/// </summary>
public class LocalHistoryTests
{
    private static async Task<IReadOnlyList<LocalHistoryEntry>> EntriesAsync(RealAppHost host, Uri uri, int count)
    {
        var history = host.Get<ILocalHistoryService>();
        var deadline = Environment.TickCount64 + 20_000;
        while (true)
        {
            var entries = await history.GetEntriesAsync(uri);
            if (entries.Count == count || Environment.TickCount64 > deadline) return entries;
            await RealAppHost.WaitFor(() => false, 50); // pumps the UI loop meanwhile
        }
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Saves_go_into_local_history_the_Timeline_lists_them_restore_undoes_in_the_editor_and_renames_follow()
    {
        await using var host = RealAppHost.Create(h => h.Files.WriteFile("a.txt", "one\n"));
        await host.StartAsync([]);
        await host.Get<IWorkspaceService>().OpenFolderAsync(host.Files.Uri);
        var uri = new Uri(host.Files.Combine("a.txt"));
        var history = host.Get<LocalHistoryService>();
        Assert.Same(history, host.Get<ILocalHistoryService>());
        Assert.StartsWith(host.Home.Path, history.Home); // under the user data folder, never the user's real one

        await host.Get<INavigationService>().OpenAsync(uri);
        var tab = host.Shell.Well.ActiveEditor!;
        Assert.True(await RealAppHost.WaitFor(() => tab.Controller is not null));
        tab.Doc.ApplyEdits([new TextEdit(TextRange.FromLines(0, 0, 0, 3), "two")]);
        await host.RunAsync(KnownCommands.Save);
        var saved = Assert.Single(await EntriesAsync(host, uri, 1));
        Assert.Equal("File Saved", saved.SourceLabel);
        Assert.Equal("two\n", File.ReadAllText(saved.Location.LocalPath));

        // a named entry, then another save: its own entry (another source than the newest)
        Assert.NotNull(await history.AddEntryAsync(uri, "checkpoint"));
        tab.Doc.ApplyEdits([new TextEdit(TextRange.FromLines(0, 0, 0, 3), "three")]);
        await host.RunAsync(KnownCommands.Save);
        var entries = await EntriesAsync(host, uri, 3);
        Assert.Equal(["File Saved", "checkpoint", "File Saved"], entries.Select(e => e.SourceLabel));

        // the Timeline window shows them, newest first
        await host.RunAsync(TimelineHost.Focus);
        var timeline = host.Get<TimelineHost>();
        Assert.True(await RealAppHost.WaitFor(() => timeline.Live is { IsLoading: false, Rows.Count: >= 3 }, 20_000), "Timeline rows");
        Assert.Equal(["File Saved", "checkpoint", "File Saved"], timeline.Live!.Rows.Where(r => r.Item?.ProviderId == LocalHistoryTimelineProvider.ProviderId).Select(r => r.Label));
        Assert.Equal("a.txt", timeline.Live.Title);

        // Restore Contents: one edit in the real editor, saved, and Undo takes it back
        Assert.True(await timeline.RestoreAsync(entries[0], ask: false));
        Assert.Equal("two\n", tab.Doc.GetText());
        Assert.False(tab.Doc.IsDirty);
        Assert.Equal("two\n", File.ReadAllText(uri.LocalPath));
        Assert.Equal("File Restored", (await EntriesAsync(host, uri, 4))[^1].SourceLabel);
        tab.Controller!.Undo();
        Assert.Equal("three\n", tab.Doc.GetText());

        // an Explorer rename: the entries follow, with a "File Renamed" entry
        await host.RunAsync(KnownCommands.Save);
        await history.WhenIdleAsync();
        var before = (await history.GetEntriesAsync(uri)).Count;
        var sp = host.Services;
        using var explorer = new ExplorerViewModel(sp.GetRequiredService<IWorkspaceService>(), sp.GetRequiredService<IUiDispatcher>(), sp.GetRequiredService<INavigationService>(),
            sp.GetService<IConfigurationService>(), sp.GetService<IStateStorage>(), dialogs: null, sp.GetRequiredService<DocumentWell>(), sp.GetRequiredService<FileParticipants>());
        await explorer.RefreshRootsAsync();
        var node = explorer.Roots[0].Children!.Single(c => c.Name == "a.txt");
        explorer.BeginRename(node);
        node.EditText = "b.txt";
        Assert.True(await explorer.CommitRenameAsync(node));
        var renamed = new Uri(host.Files.Combine("b.txt"));
        var moved = await EntriesAsync(host, renamed, before + 1);
        Assert.Equal("File Renamed", moved[^1].SourceLabel);
        Assert.Empty(await history.GetEntriesAsync(uri));
        // the Timeline follows the reopened editor of the new name
        Assert.True(await RealAppHost.WaitFor(() => timeline.Live?.Title == "b.txt" && timeline.Live.Rows.Count == before + 1, 20_000), "Timeline of b.txt");
    }
}
