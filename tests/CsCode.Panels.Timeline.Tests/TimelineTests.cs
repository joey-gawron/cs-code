using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CsCode.Core.History;
using CsCode.Core.Scm;
using CsCode.Core.Timeline;
using CsCode.Panels.Timeline.LocalHistory;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Timeline.Tests;

/// <summary>The Timeline window and VS Code's local history commands over the real engine, in a headless window.</summary>
public class TimelineTests
{
    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Until(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(because);
            Pump();
            await Task.Delay(10);
        }
    }

    private static (Window Window, TimelineView View) Show(TimelineRig rig)
    {
        var view = (TimelineView)rig.Provider.GetServices<CsCode.Ui.IToolWindow>().Single(t => t.Id == TimelineHost.ViewId).CreateView(rig.Provider);
        var window = new Window { Width = 400, Height = 500, Content = view };
        window.Show();
        Pump();
        return (window, view);
    }

    private static string Rows(TimelineViewModel vm) => string.Join("|", vm.Rows.Select(r => r.IsLoadMore ? "more" : $"{r.Label}:{r.Time}"));

    [AvaloniaFact]
    public async Task The_window_shows_the_active_files_entries_newest_first_and_follows_saves_and_the_active_editor()
    {
        using var rig = new TimelineRig();
        await rig.History.SaveAsync("a.txt", "one");
        rig.History.Time.Advance(TimeSpan.FromMinutes(5));
        rig.History.Write("a.txt", "two");
        await rig.History.History.AddEntryAsync(rig.History.UriOf("a.txt"), "named");
        rig.History.Time.Advance(TimeSpan.FromHours(2));
        var doc = rig.Activate("a.txt");
        var (window, view) = Show(rig);
        var vm = view.Model;
        await Until(() => vm.Rows.Count == 2, "two rows");
        Assert.Equal("a.txt", vm.Title);
        Assert.Equal("named:2 hrs|File Saved:2 hrs", Rows(vm));
        Assert.Equal(["history", "history"], vm.Rows.Select(r => r.IconKey));

        // a save shows up (the provider's change event, coalesced onto the UI thread)
        await doc.SaveAsync();
        await Until(() => vm.Rows.Count == 3, "the saved entry");
        Assert.Equal("File Saved:now", Rows(vm).Split('|')[0]);

        // another editor: its file; no editor (a diff tab): the same file stays
        rig.History.Write("b.txt", "b");
        rig.Activate("b.txt");
        await Until(() => vm.Title == "b.txt", "b.txt shown");
        Assert.True(vm.IsEmpty);
        Assert.Contains("No timeline information for 'b.txt'", vm.EmptyText);
        rig.Navigation.SetActive(null);
        Pump();
        Assert.Equal("b.txt", vm.Title);

        // pinned: the active editor no longer moves it
        await rig.Commands.ExecuteAsync(TimelineHost.ToggleFollow);
        rig.Navigation.SetActive(doc);
        Pump();
        Assert.Equal("b.txt", vm.Title);
        await rig.Commands.ExecuteAsync(TimelineHost.ToggleFollow); // following again: the active editor's file
        await Until(() => vm.Title == "a.txt" && vm.Rows.Count == 3, "back to a.txt");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sources_merge_by_time_page_with_Load_more_and_can_be_filtered_out()
    {
        var resource = new Uri("file:///w/x.cs");
        var t0 = new DateTimeOffset(2026, 11, 4, 11, 0, 0, TimeSpan.Zero);
        // git: 120 commits one minute apart, newest at t0; local: entries between them
        var commits = Enumerable.Range(0, 120).Select(i => ($"c{i}", t0 - TimeSpan.FromMinutes(i))).ToList();
        var locals = new[] { ("l-new", t0 + TimeSpan.FromSeconds(30)), ("l-mid", t0 - TimeSpan.FromMinutes(30.5)), ("l-old", t0 - TimeSpan.FromMinutes(100.5)) };
        var git = new PagedProvider("git-history", "Git History", resource, commits);
        var local = new PagedProvider("local", "Local", resource, locals);
        using var rig = new TimelineRig(s => { s.AddSingleton<ITimelineProvider>(local); s.AddSingleton<ITimelineProvider>(git); });
        var (window, view) = Show(rig);
        var vm = view.Model;
        vm.Show(resource);
        await Until(() => vm.Rows.Count > 0 && !vm.IsLoading, "first page");
        // the first page of git reaches c49 (49 minutes back): l-old (100 min back) waits for git's later pages
        var labels = vm.Rows.Select(r => r.Label).ToList();
        Assert.Equal("l-new", labels[0]);
        Assert.Equal("c0", labels[1]);
        Assert.Contains("l-mid", labels);
        Assert.DoesNotContain("l-old", labels);
        Assert.Equal("Load more", labels[^1]);
        Assert.Equal(TimelineViewModel.PageSize + 2 + 1, labels.Count);

        await view.ActivateAsync(vm.Rows[^1]); // Load more
        await Until(() => !vm.IsLoading && vm.Rows.Count == 103, "second page");
        Assert.DoesNotContain("l-old", vm.Rows.Select(r => r.Label)); // still older than the oldest commit loaded (c99)
        await view.ActivateAsync(vm.Rows[^1]);
        await Until(() => !vm.IsLoading && !vm.HasMore, "last page");
        Assert.Equal(123, vm.Rows.Count);
        labels = vm.Rows.Select(r => r.Label).ToList();
        Assert.Equal(labels.IndexOf("c100") + 1, labels.IndexOf("l-old")); // between c100 and c101, in time order
        Assert.Equal([null, "50", "100"], git.Cursors);

        // the filter: the commits' rows go, and that is remembered
        var filter = view.FilterMenu();
        Assert.Equal(["Local", "Git History", "Local History"], filter.Select(e => e.Header));
        Assert.All(filter, e => Assert.True(e.IsChecked));
        await filter[1].Run!();
        await Until(() => vm.Rows.Count == 3, "local rows only");
        Assert.Equal(["git-history"], rig.State.Get<string[]>("timeline.excludedSources")!);
        Assert.False(view.FilterMenu()[1].IsChecked);
        await view.FilterMenu()[1].Run!();
        await Until(() => vm.Rows.Count > 3, "git rows back");
        Assert.Null(rig.State.Get<string[]>("timeline.excludedSources"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Compare_with_file_and_with_previous_open_diff_tabs()
    {
        using var rig = new TimelineRig();
        await rig.History.SaveAsync("a.txt", "one");
        rig.History.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.History.SaveAsync("a.txt", "two");
        var entries = await rig.History.History.GetEntriesAsync(rig.History.UriOf("a.txt"));
        var older = LocalHistoryTimelineProvider.ToItem(entries[0]);
        var newer = LocalHistoryTimelineProvider.ToItem(entries[1]);

        await rig.Commands.ExecuteAsync(TimelineHost.CompareWithFile, newer);
        var withFile = Assert.Single(rig.Diff.Requests);
        Assert.Equal(entries[1].Location, withFile.Original.Uri);
        Assert.Equal(rig.History.UriOf("a.txt"), withFile.Modified.Uri);
        Assert.StartsWith("a.txt (File Saved • ", withFile.Title);
        Assert.EndsWith(" ↔ a.txt", withFile.Title);
        Assert.Equal("a.txt", withFile.LanguageHint);

        await rig.Commands.ExecuteAsync(TimelineHost.CompareWithPrevious, newer);
        var withPrevious = rig.Diff.Requests[^1];
        Assert.Equal(entries[0].Location, withPrevious.Original.Uri);
        Assert.Equal(entries[1].Location, withPrevious.Modified.Uri);

        // the oldest has nothing before it: shown alone
        await rig.Commands.ExecuteAsync(TimelineHost.CompareWithPrevious, older);
        var alone = rig.Diff.Requests[^1];
        Assert.Null(alone.Original.Uri);
        Assert.Null(alone.Original.Text);
        Assert.Equal(entries[0].Location, alone.Modified.Uri);

        await rig.Commands.ExecuteAsync(TimelineHost.Open, newer);
        Assert.Equal(entries[1].Location, rig.Diff.Requests[^1].Modified.Uri);

        // without an argument the commands act on the Timeline's selected row
        var (window, view) = Show(rig);
        rig.Navigation.SetActive(rig.History.EditorDocument("a.txt"));
        await Until(() => view.Model.Rows.Count == 2, "rows");
        view.List.SelectedIndex = 1;
        Pump();
        await rig.Commands.ExecuteAsync(TimelineHost.CompareWithFile);
        Assert.Equal(entries[0].Location, rig.Diff.Requests[^1].Original.Uri);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Restore_asks_then_edits_the_open_document_once_saves_it_and_records_File_Restored()
    {
        using var rig = new TimelineRig();
        await rig.History.SaveAsync("a.txt", "line 1\nline 2\nline 3\n");
        rig.History.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.History.SaveAsync("a.txt", "line 1\nline two\nline 3\n");
        var doc = rig.Activate("a.txt");
        var first = (await rig.History.History.GetEntriesAsync(rig.History.UriOf("a.txt")))[0];

        rig.Window.MessageActionResponses.Enqueue("Cancel");
        await rig.Commands.ExecuteAsync(TimelineHost.Restore, first);
        Assert.Equal("line 1\nline two\nline 3\n", doc.GetText());
        Assert.Contains("Do you want to restore the contents of 'a.txt'", rig.Window.Messages[^1].Message);

        rig.Window.MessageActionResponses.Enqueue("Restore");
        rig.History.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.Commands.ExecuteAsync(TimelineHost.Restore, first);
        Assert.Equal("line 1\nline 2\nline 3\n", doc.GetText());
        Assert.Equal(1, doc.ApplyCount); // one edit: one undo step in the editor
        Assert.False(doc.IsDirty);
        Assert.Equal("line 1\nline 2\nline 3\n", File.ReadAllText(rig.History.PathOf("a.txt")));
        await rig.History.History.WhenIdleAsync();
        var entries = await rig.History.History.GetEntriesAsync(rig.History.UriOf("a.txt"));
        Assert.Equal(["File Saved", "File Saved", "File Restored"], entries.Select(e => e.SourceLabel));
        Assert.Contains(rig.History.UriOf("a.txt"), rig.Navigation.Opened);
    }

    [AvaloniaFact]
    public async Task Restore_of_a_file_that_is_not_open_writes_it_and_keeps_what_it_overwrote()
    {
        using var rig = new TimelineRig();
        await rig.History.SaveAsync("a.txt", "old");
        rig.History.Time.Advance(TimeSpan.FromMinutes(1));
        rig.History.Write("a.txt", "changed outside, never saved in CsCode");
        var entry = Assert.Single(await rig.History.History.GetEntriesAsync(rig.History.UriOf("a.txt")));
        // the rig's document of a.txt is no editor document: the file counts as not open
        Assert.True(await rig.Host.RestoreAsync(entry, ask: false));
        Assert.Equal("old", File.ReadAllText(rig.History.PathOf("a.txt")));
        Assert.Equal(["old", "changed outside, never saved in CsCode", "old"], await rig.History.ContentsAsync("a.txt"));
        Assert.Equal(["File Saved", "Before Restore", "File Restored"], (await rig.History.History.GetEntriesAsync(rig.History.UriOf("a.txt"))).Select(e => e.SourceLabel));
    }

    [AvaloniaFact]
    public async Task Rename_delete_delete_all_and_create_entry_run_from_their_commands_after_asking()
    {
        using var rig = new TimelineRig();
        foreach (var v in new[] { "one", "two", "three" })
        {
            rig.History.Time.Advance(TimeSpan.FromMinutes(1));
            await rig.History.SaveAsync("a.txt", v);
        }
        await rig.History.SaveAsync("b.txt", "b");
        var uri = rig.History.UriOf("a.txt");
        var entries = await rig.History.History.GetEntriesAsync(uri);

        rig.Window.InputResponses.Enqueue("before the refactoring");
        await rig.Commands.ExecuteAsync(TimelineHost.Rename, LocalHistoryTimelineProvider.ToItem(entries[0]));
        Assert.Equal("before the refactoring", (await rig.History.History.GetEntriesAsync(uri))[0].SourceLabel);

        rig.Window.MessageActionResponses.Enqueue("Cancel");
        await rig.Commands.ExecuteAsync(TimelineHost.Delete, entries[1]);
        Assert.Equal(3, (await rig.History.History.GetEntriesAsync(uri)).Count);
        rig.Window.MessageActionResponses.Enqueue("Delete");
        await rig.Commands.ExecuteAsync(TimelineHost.Delete, entries[1]);
        Assert.Equal(["one", "three"], await rig.History.ContentsAsync("a.txt"));

        // Create Entry: the active editor's file under a name
        rig.Activate("a.txt");
        rig.History.Write("a.txt", "four");
        rig.Window.InputResponses.Enqueue("checkpoint");
        await rig.Commands.ExecuteAsync(TimelineHost.Create);
        Assert.Equal("checkpoint", (await rig.History.History.GetEntriesAsync(uri))[^1].SourceLabel);

        rig.Window.MessageActionResponses.Enqueue("Delete All");
        await rig.Commands.ExecuteAsync(TimelineHost.DeleteAllOfFile, uri);
        Assert.Empty(await rig.History.History.GetEntriesAsync(uri));
        Assert.Single(await rig.History.History.GetEntriesAsync(rig.History.UriOf("b.txt")));

        rig.Window.MessageActionResponses.Enqueue("Cancel");
        await rig.Commands.ExecuteAsync(TimelineHost.DeleteAll);
        Assert.Single(await rig.History.History.GetEntriesAsync(rig.History.UriOf("b.txt")));
        rig.Window.MessageActionResponses.Enqueue("Delete All");
        await rig.Commands.ExecuteAsync(TimelineHost.DeleteAll);
        Assert.Empty(await rig.History.History.GetResourcesAsync());
        Assert.Contains("all entries of all files", rig.Window.Messages[^1].Message);
    }

    [AvaloniaFact]
    public async Task Find_entry_to_restore_picks_a_file_then_an_entry_compares_and_restores()
    {
        using var rig = new TimelineRig();
        await rig.History.SaveAsync("a.txt", "a-old");
        rig.History.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.History.SaveAsync("a.txt", "a-new");
        rig.History.Time.Advance(TimeSpan.FromMinutes(1));
        await rig.History.SaveAsync("gone.txt", "deleted file's text");
        File.Delete(rig.History.PathOf("gone.txt"));

        rig.Window.QuickPickResponses.Enqueue(0); // gone.txt: the most recent
        rig.Window.QuickPickResponses.Enqueue(0); // its only entry
        rig.Window.MessageActionResponses.Enqueue("Restore");
        Assert.True(await rig.Host.RestoreViaPickerAsync());
        Assert.Equal("deleted file's text", File.ReadAllText(rig.History.PathOf("gone.txt")));
        Assert.Equal(rig.History.UriOf("gone.txt"), rig.Diff.Requests[^1].Modified.Uri); // compared with the file first

        rig.Window.QuickPickResponses.Enqueue(1); // a.txt
        rig.Window.QuickPickResponses.Enqueue(1); // its older entry (the list is newest first)
        rig.Window.MessageActionResponses.Enqueue("Restore");
        Assert.True(await rig.Host.RestoreViaPickerAsync());
        Assert.Equal("a-old", File.ReadAllText(rig.History.PathOf("a.txt")));
    }

    [AvaloniaFact]
    public async Task The_commands_have_VS_Codes_ids_and_the_view_its_menus()
    {
        using var rig = new TimelineRig();
        foreach (var id in new[]
        {
            TimelineHost.Focus, TimelineHost.Refresh, TimelineHost.ToggleFollow, "workbench.action.localHistory.create", "workbench.action.localHistory.restoreViaPicker",
            "workbench.action.localHistory.deleteAll", "workbench.action.localHistory.compareWithFile", "workbench.action.localHistory.compareWithPrevious",
            "workbench.action.localHistory.open", "workbench.action.localHistory.restore", "workbench.action.localHistory.rename", "workbench.action.localHistory.delete",
        })
            Assert.True(rig.Commands.Has(id), id);
        await rig.Commands.ExecuteAsync(TimelineHost.Focus);
        Assert.Equal(["timeline"], rig.Views.Shown);

        await rig.History.SaveAsync("a.txt", "a");
        rig.Activate("a.txt");
        var (window, view) = Show(rig);
        await Until(() => view.Model.Rows.Count == 1, "row");
        var menu = view.MenuEntries(view.Model.Rows[0]).Where(e => !e.IsSeparator).Select(e => e.AccessibleName).ToList();
        Assert.Equal(["Compare with File", "Compare with Previous", "Show Contents", "Restore Contents...", "Rename...", "Delete"], menu);
        var git = new TimelineRow[] { TimelineRow.For(new TimelineItem("git-history", "abc123", rig.History.UriOf("a.txt"), DateTimeOffset.UnixEpoch, "Fix it"), DateTimeOffset.UnixEpoch) };
        Assert.Equal(["Compare with File", "Compare with Previous", "Show Contents", "Copy Commit ID", "Copy Commit Message"],
            view.MenuEntries(git[0]).Where(e => !e.IsSeparator).Select(e => e.AccessibleName));

        // Enter on a local entry compares it with the file
        view.List.SelectedIndex = 0;
        Pump();
        Assert.True(view.FocusPreferred());
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => rig.Diff.Requests.Count == 1, "diff opened");
        Assert.Equal(rig.History.UriOf("a.txt"), rig.Diff.Requests[0].Modified.Uri);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_hidden_window_keeps_neither_its_view_nor_its_view_model()
    {
        using var rig = new TimelineRig();
        await rig.History.SaveAsync("a.txt", "a");
        rig.Activate("a.txt");
        var subscribersBefore = SubscriberCount(rig);
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var (window, view) = Show(rig);
            await Until(() => view.Model.Rows.Count == 1, "row");
            var vm = view.Model;
            Assert.Same(vm, rig.Host.Live);
            window.Content = null;
            window.Close();
            view.Dispose(); // what the shell does when the tool window is hidden
            Pump();
            Assert.Null(rig.Host.Live);
            return [view, vm];
        });
        Assert.Equal(subscribersBefore, SubscriberCount(rig));
    }

    private static int SubscriberCount(TimelineRig rig) =>
        ((CsCode.Core.Lifecycle.Emitter<Uri?>)typeof(LocalHistoryService).GetField("_changed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(rig.History.History)!).SubscriberCount;

    [Fact]
    public void Relative_times_read_as_in_VS_Codes_Timeline()
    {
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("now", RelativeTime.Format(now - TimeSpan.FromSeconds(59), now));
        Assert.Equal("1 min", RelativeTime.Format(now - TimeSpan.FromMinutes(1), now));
        Assert.Equal("59 mins", RelativeTime.Format(now - TimeSpan.FromMinutes(59.9), now));
        Assert.Equal("1 hr", RelativeTime.Format(now - TimeSpan.FromHours(1), now));
        Assert.Equal("3 days", RelativeTime.Format(now - TimeSpan.FromDays(3), now));
        Assert.Equal("2 wks", RelativeTime.Format(now - TimeSpan.FromDays(14), now));
        Assert.Equal("4 mos", RelativeTime.Format(now - TimeSpan.FromDays(125), now));
        Assert.Equal("1 yr", RelativeTime.Format(now - TimeSpan.FromDays(400), now));
    }

    [Fact]
    public void The_restore_edit_covers_only_what_differs_and_never_splits_a_line_break()
    {
        Assert.Null(TimelineHost.MinimalChange("same", "same"));
        Assert.Equal((5, 6, "X"), TimelineHost.MinimalChange("line a\n", "line X\n"));
        Assert.Equal((0, 3, ""), TimelineHost.MinimalChange("abc", ""));
        var crlf = TimelineHost.MinimalChange("a\r\nb", "a\nb")!.Value;
        Assert.Equal("a\nb", "a\r\nb"[..crlf.Start] + crlf.Insert + "a\r\nb"[crlf.End..]);
        Assert.Equal("éé", TimelineHost.Decode([0xEF, 0xBB, 0xBF, 0xC3, 0xA9, 0xC3, 0xA9], "utf8"));
        Assert.Equal("é", TimelineHost.Decode([0xE9], "windows1252"));
    }
}
