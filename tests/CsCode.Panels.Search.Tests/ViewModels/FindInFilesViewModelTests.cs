using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using CsCode.Panels.Search.ViewModels;
using Xunit;

namespace CsCode.Panels.Search.Tests.ViewModels;

public class FindInFilesViewModelTests : IDisposable
{
    private readonly VmFixture _f = new();
    private readonly List<IDisposable> _regs = [];

    public void Dispose()
    {
        foreach (var r in _regs) r.Dispose();
        _f.Dispose();
    }

    private void Populate()
    {
        _f.Harness.Write("src/b.cs", "needle one\nneedle two\nother");
        _f.Harness.Write("src/a.cs", "x needle");
        _f.Harness.Write("docs/readme.md", "no match here\nneedle in docs");
        _f.Harness.Write("root.txt", "nothing");
    }

    private TestEditorDocument Open(string rel, string text, bool dirty = true)
    {
        var d = new TestEditorDocument(_f.Harness.UriOf(rel), text, dirty);
        _regs.Add(_f.Harness.Documents.Register(d));
        return d;
    }

    // ------------------------------------------------------------------------- results and summary

    [Fact]
    public async Task Results_are_grouped_by_file_with_counts_and_sorted_by_path_when_done()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        var files = vm.Rows.OfType<FileRow>().ToList();
        Assert.Equal(["docs/readme.md", "src/a.cs", "src/b.cs"], files.Select(f => f.Directory + "/" + f.Name));
        Assert.Equal([1, 1, 2], files.Select(f => f.MatchCount));
        Assert.Equal(4, vm.TotalMatches);
        Assert.Equal(7, vm.Rows.Count); // 3 file rows + 4 match rows, all expanded
        Assert.Equal("FMFMFMM", string.Concat(FileKinds(vm)));
        Assert.False(vm.IsSearching);
    }

    private static string[] FileKinds(FindInFilesViewModel vm) => vm.Rows.Select(r => r.IsFile ? "F" : "M").ToArray();

    [Fact]
    public async Task Match_rows_carry_one_based_line_numbers_and_highlight_offsets()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        var m = vm.Rows.OfType<MatchRow>().First(r => r.File.Name == "readme.md");
        Assert.Equal(2, m.LineNumber);
        Assert.Equal("needle", m.Preview.Substring(m.PreviewMatchStart, m.PreviewMatchLength));
    }

    [Fact]
    public async Task Summary_reports_results_files_and_time()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        Assert.Matches(@"^4 results in 3 files — \d+ ms$", vm.Summary);
    }

    [Fact]
    public async Task Summary_for_no_results_names_the_query()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "zzzzqqq");
        Assert.StartsWith("No results found for 'zzzzqqq'", vm.Summary);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task A_bad_regex_shows_the_error_and_no_results()
    {
        Populate();
        using var vm = _f.NewFind();
        vm.IsRegex = true;
        await _f.SearchAsync(vm, "(oops");
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.Empty(vm.Rows);
        Assert.False(vm.IsSearching);
    }

    [Fact]
    public async Task Truncated_searches_say_so_in_the_summary()
    {
        using var f = new VmFixture(new SearchLimits { MaxResults = 5 });
        f.Harness.Write("a.txt", string.Join("\n", Enumerable.Repeat("needle", 20)));
        using var vm = f.NewFind();
        await f.SearchAsync(vm, "needle");
        Assert.True(vm.Truncated);
        Assert.Contains("truncated at 5", vm.Summary);
        Assert.Equal(5, vm.TotalMatches);
    }

    [Fact]
    public async Task With_no_folder_open_the_panel_explains_instead_of_searching()
    {
        await _f.Harness.Workspace.CloseFolderAsync(TestContext.Current.CancellationToken);
        using var vm = _f.NewFind();
        vm.Query = "x";
        vm.Start();
        Assert.Contains("No folder is open", vm.Summary);
        Assert.False(vm.IsSearching);
    }

    [Fact]
    public async Task Toggles_and_fields_flow_into_the_engine_query()
    {
        using var vm = _f.NewFind();
        vm.Query = "q"; vm.MatchCase = true; vm.WholeWord = true; vm.IsRegex = true; vm.Include = "*.cs"; vm.Exclude = "bin";
        vm.UseExcludeSettings = false; vm.UseIgnoreFiles = false; vm.OpenEditorsOnly = true;
        var q = vm.BuildQuery();
        Assert.Equal(new SearchQuery("q", true, true, true, false, "*.cs", "bin", false, false, true, null, null), q);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Start_is_a_no_op_for_an_empty_query()
    {
        using var vm = _f.NewFind();
        Assert.False(vm.CanSearch);
        vm.Start();
        Assert.False(vm.IsSearching);
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------------- history

    [Fact]
    public async Task History_records_queries_and_up_down_walk_it_restoring_the_draft()
    {
        Populate();
        using var vm = _f.NewFind();
        foreach (var q in new[] { "one", "two", "three" }) await _f.SearchAsync(vm, q);
        vm.Query = "draft";
        vm.HistoryUp(); Assert.Equal("three", vm.Query);
        vm.HistoryUp(); Assert.Equal("two", vm.Query);
        vm.HistoryUp(); Assert.Equal("one", vm.Query);
        vm.HistoryUp(); Assert.Equal("one", vm.Query);   // already the oldest
        vm.HistoryDown(); Assert.Equal("two", vm.Query);
        vm.HistoryDown(); vm.HistoryDown(); Assert.Equal("draft", vm.Query);
    }

    [Fact]
    public void History_is_capped_at_50_deduplicated_and_persisted()
    {
        var state = new InMemoryStateStorage();
        var h = new SearchHistory(state);
        for (var i = 0; i < 80; i++) h.Add("q" + i);
        h.Add("q70"); // moves to the end instead of duplicating
        Assert.Equal(SearchHistory.Capacity, h.Items.Count);
        Assert.Equal("q70", h.Items[^1]);
        Assert.Equal(1, h.Items.Count(x => x == "q70"));
        Assert.DoesNotContain("q0", h.Items);
        var reloaded = new SearchHistory(state);
        Assert.Equal(h.Items, reloaded.Items);
    }

    [Fact]
    public void Empty_terms_are_not_remembered()
    {
        var h = new SearchHistory(null);
        h.Add("");
        Assert.Empty(h.Items);
        Assert.Null(h.Previous("x"));
        Assert.Null(h.Next());
    }

    // ------------------------------------------------------------------------------------ tree

    [Fact]
    public async Task Collapse_and_expand_all_rebuild_the_flat_list()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        vm.CollapseAll();
        Assert.Equal(3, vm.Rows.Count);
        Assert.True(vm.AllCollapsed);
        vm.ToggleCollapseAll();
        Assert.Equal(7, vm.Rows.Count);
        Assert.False(vm.AllCollapsed);
    }

    [Fact]
    public async Task Expanding_one_file_inserts_its_matches_right_after_it()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        vm.CollapseAll();
        var b = vm.Rows.OfType<FileRow>().Single(f => f.Name == "b.cs");
        vm.SetExpanded(b, true);
        var at = vm.Rows.IndexOf(b);
        Assert.IsType<MatchRow>(vm.Rows[at + 1]);
        Assert.IsType<MatchRow>(vm.Rows[at + 2]);
        Assert.Equal(5, vm.Rows.Count);
        vm.SetExpanded(b, false);
        Assert.Equal(3, vm.Rows.Count);
    }

    [Fact]
    public async Task The_collapse_results_setting_starts_files_collapsed()
    {
        Populate();
        _f.ConfigStore.Set(FindInFilesViewModel.CollapseResultsKey, "alwaysCollapse");
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        Assert.Equal(3, vm.Rows.Count);
        Assert.All(vm.Rows.OfType<FileRow>(), f => Assert.False(f.IsExpanded));
    }

    [Fact]
    public async Task Dismissing_a_match_removes_it_and_a_files_last_match_removes_the_file()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        var m = vm.Rows.OfType<MatchRow>().First(r => r.File.Name == "b.cs");
        vm.Dismiss(m);
        Assert.Equal(3, vm.TotalMatches);
        Assert.Equal(1, vm.Rows.OfType<FileRow>().Single(f => f.Name == "b.cs").MatchCount);
        vm.Dismiss(vm.Rows.OfType<MatchRow>().First(r => r.File.Name == "a.cs"));
        Assert.DoesNotContain(vm.Rows.OfType<FileRow>(), f => f.Name == "a.cs");
        Assert.Equal(2, vm.TotalMatches);
        Assert.Equal(2, vm.TotalFiles);
    }

    [Fact]
    public async Task Dismissing_a_file_removes_all_its_rows_and_selection()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        var b = vm.Rows.OfType<FileRow>().Single(f => f.Name == "b.cs");
        vm.SelectedRow = b.Matches[1];
        vm.Dismiss(b);
        Assert.Equal(4, vm.Rows.Count);
        Assert.Null(vm.SelectedRow);
        Assert.Equal(2, vm.TotalMatches);
    }

    [Fact]
    public async Task Exclude_this_folder_adds_a_glob_and_drops_its_results()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        vm.ExcludeFolderOf(vm.Rows.OfType<FileRow>().Single(f => f.Name == "a.cs"));
        Assert.Equal("./src", vm.Exclude);
        Assert.Equal(["readme.md"], vm.Rows.OfType<FileRow>().Select(f => f.Name));
    }

    [Fact]
    public async Task Clear_empties_the_tree_and_the_summary()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        vm.Clear();
        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.Summary);
        Assert.Equal(0, vm.TotalMatches);
    }

    // --------------------------------------------------------------------------------- navigation

    [Fact]
    public async Task Opening_a_match_asks_the_navigation_service_to_select_it()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        var m = vm.Rows.OfType<MatchRow>().First(r => r.File.Name == "readme.md");
        await vm.OpenAsync(m);
        var (uri, sel) = Assert.Single(_f.Navigation.Opened);
        Assert.Equal(m.File.Uri, uri);
        Assert.Equal(new TextRange(new(1, 0), new(1, 6)), sel);
    }

    [Fact]
    public async Task Next_and_previous_walk_every_match_in_tree_order_and_wrap()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        var order = vm.Rows.OfType<MatchRow>().ToList();
        var visited = new List<MatchRow>();
        for (var i = 0; i < 5; i++) { await vm.NextResultAsync(); visited.Add((MatchRow)vm.SelectedRow!); }
        Assert.Equal(order.Concat([order[0]]), visited);   // 4 matches, then wraps to the first
        await vm.PreviousResultAsync();
        Assert.Same(order[^1], vm.SelectedRow);            // back from the first wraps to the last
        Assert.Equal(6, _f.Navigation.Opened.Count);       // every step opened its match
    }

    [Fact]
    public async Task Next_result_expands_a_collapsed_file_first()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        vm.CollapseAll();
        await vm.NextResultAsync();
        Assert.True(((MatchRow)vm.SelectedRow!).File.IsExpanded);
        Assert.Contains(vm.SelectedRow, vm.Rows);
    }

    [Fact]
    public async Task Next_with_no_results_does_nothing()
    {
        using var vm = _f.NewFind();
        await vm.NextResultAsync();
        Assert.Empty(_f.Navigation.Opened);
    }

    // -------------------------------------------------------------------------- searching behaviour

    [Fact]
    public async Task A_new_search_supersedes_the_running_one_and_stale_results_never_appear()
    {
        for (var i = 0; i < 100; i++) _f.Harness.Write($"old{i}.txt", string.Join("\n", Enumerable.Repeat("alpha", 200)));
        _f.Harness.Write("new.txt", "beta");
        using var vm = _f.NewFind();
        vm.Query = "alpha";
        vm.Start();
        vm.Query = "beta";
        vm.Start();
        await _f.Ui.PumpAsync(vm.WaitForSearchAsync());
        Assert.Equal(["new.txt"], vm.Rows.OfType<FileRow>().Select(f => f.Name));
        Assert.Equal(1, vm.TotalMatches);
    }

    [Fact]
    public async Task Cancel_stops_the_search_and_keeps_what_arrived()
    {
        for (var i = 0; i < 50; i++) _f.Harness.Write($"f{i}.txt", "needle");
        using var vm = _f.NewFind();
        vm.Query = "needle";
        vm.Start();
        vm.Cancel();
        await _f.Ui.PumpAsync(vm.WaitForSearchAsync());
        Assert.False(vm.IsSearching);
        Assert.StartsWith("Search cancelled", vm.Summary);
    }

    [Fact]
    public async Task Apply_request_fills_the_form_and_can_trigger_the_search()
    {
        Populate();
        using var vm = _f.NewFind();
        vm.Apply(new FindRequest { Query = "needle", Include = "*.cs", IsRegex = false, MatchCase = true, TriggerSearch = true, ShowReplace = true, Replace = "x" });
        await _f.Ui.PumpAsync(vm.WaitForSearchAsync());
        Assert.True(vm.ReplaceVisible);
        Assert.Equal("x", vm.ReplaceText);
        Assert.Equal("*.cs", vm.Include);
        Assert.Equal(["a.cs", "b.cs"], vm.Rows.OfType<FileRow>().Select(f => f.Name));
        Assert.Equal(FindFocusTarget.Replace, vm.PendingFocus);
    }

    [Fact]
    public async Task Changing_the_workspace_marks_old_results_stale()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        using var other = new CsCode.TestDoubles.TempDirectory();
        await _f.Harness.Workspace.OpenFolderAsync(other.Uri, TestContext.Current.CancellationToken); // raises FoldersChanged
        _f.Ui.RunPending();
        Assert.True(vm.IsStale);
        vm.Start();
        Assert.False(vm.IsStale);
        await _f.Ui.PumpAsync(vm.WaitForSearchAsync());
    }

    [Fact]
    public async Task The_streamed_tree_is_sorted_after_arrival_regardless_of_worker_order()
    {
        for (var i = 0; i < 60; i++) _f.Harness.Write($"d{59 - i:00}/f.txt", "needle");
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        var dirs = vm.Rows.OfType<FileRow>().Select(f => f.Directory).ToList();
        Assert.Equal(dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(), dirs);
    }

    [Fact]
    public async Task Open_editors_only_and_dirty_documents_flow_through_the_view_model()
    {
        _f.Harness.Write("a.txt", "disk");
        Open("a.txt", "memory needle");
        using var vm = _f.NewFind();
        vm.OpenEditorsOnly = true;
        await _f.SearchAsync(vm, "needle");
        var file = Assert.Single(vm.Rows.OfType<FileRow>());
        Assert.True(file.FromMemory);
    }

    // -------------------------------------------------------------------------------------- replace

    [Fact]
    public async Task Replace_all_asks_once_for_several_files_then_replaces_and_removes_the_rows()
    {
        Populate();
        Open("src/a.cs", "x needle"); Open("src/b.cs", "needle one\nneedle two\nother"); Open("docs/readme.md", "no match here\nneedle in docs");
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        vm.ReplaceVisible = true;
        vm.ReplaceText = "thread";
        _f.Windows.MessageActionResponses.Enqueue("Replace");
        await vm.ReplaceAllAsync();
        Assert.Single(_f.Windows.Messages); // exactly one confirmation
        Assert.Contains("4 occurrences across 3 files", _f.Windows.Messages[0].Message);
        Assert.Equal("x thread", _f.Harness.Documents.Get(_f.Harness.UriOf("src/a.cs"))!.GetText());
        Assert.Equal("thread one\nthread two\nother", _f.Harness.Documents.Get(_f.Harness.UriOf("src/b.cs"))!.GetText());
        Assert.Empty(vm.Rows);
        Assert.Contains("Replaced 4 occurrences across 3 files", _f.Windows.StatusMessages.Single());
    }

    [Fact]
    public async Task Declining_the_confirmation_replaces_nothing()
    {
        Populate();
        var a = Open("src/a.cs", "x needle"); Open("src/b.cs", "needle");
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        vm.ReplaceVisible = true; vm.ReplaceText = "t";
        _f.Windows.MessageActionResponses.Enqueue("Cancel");
        await vm.ReplaceAllAsync();
        Assert.Equal("x needle", a.GetText());
        Assert.Equal(0, a.ApplyEditsCalls);
        Assert.NotEmpty(vm.Rows);
    }

    [Fact]
    public async Task Replacing_in_a_single_file_needs_no_confirmation()
    {
        _f.Harness.Write("only.txt", "needle needle");
        var doc = Open("only.txt", "needle needle");
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        vm.ReplaceVisible = true; vm.ReplaceText = "x";
        await vm.ReplaceAllAsync();
        Assert.Empty(_f.Windows.Messages);
        Assert.Equal("x x", doc.GetText());
        Assert.Equal(1, doc.ApplyEditsCalls);   // one undo step for the whole file
    }

    [Fact]
    public async Task Replace_in_one_file_leaves_the_other_files_untouched()
    {
        Populate();
        var a = Open("src/a.cs", "x needle"); var b = Open("src/b.cs", "needle one\nneedle two");
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        vm.ReplaceVisible = true; vm.ReplaceText = "t";
        await vm.ReplaceFileAsync(vm.Rows.OfType<FileRow>().Single(f => f.Name == "a.cs"));
        Assert.Equal("x t", a.GetText());
        Assert.Equal("needle one\nneedle two", b.GetText());
    }

    [Fact]
    public async Task Replace_one_match_replaces_only_that_match_and_refreshes_the_rest()
    {
        _f.Harness.Write("m.txt", "needle needle needle");
        var doc = Open("m.txt", "needle needle needle");
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        vm.ReplaceVisible = true; vm.ReplaceText = "ab";
        await vm.ReplaceMatchAsync(vm.Rows.OfType<MatchRow>().ElementAt(1));
        Assert.Equal("needle ab needle", doc.GetText());
        await _f.Ui.PumpAsync(vm.WaitForSearchAsync());      // the remaining matches were re-searched (positions moved)
        Assert.Equal(2, vm.TotalMatches);
    }

    [Fact]
    public async Task The_replace_preview_state_follows_the_replace_box()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        Assert.False(vm.Previews.IsActive);
        vm.ReplaceVisible = true;
        vm.ReplaceText = "pin";
        Assert.Equal("pin", vm.Previews.Preview("needle"));
        vm.PreserveCase = true;
        Assert.Equal("Pin", vm.Previews.Preview("Needle"));
        vm.ReplaceVisible = false;
        Assert.Null(vm.Previews.Preview("needle"));
    }

    [Fact]
    public async Task Regex_replace_preview_expands_groups()
    {
        using var vm = _f.NewFind();
        vm.IsRegex = true; vm.Query = @"(\w+)@(\w+)";
        vm.ReplaceVisible = true; vm.ReplaceText = "$2 at $1";
        Assert.Equal("example at user", vm.Previews.Preview("user@example"));
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------------------------ lifetime

    [Fact]
    public async Task Disposing_stops_the_search_and_empties_everything()
    {
        for (var i = 0; i < 100; i++) _f.Harness.Write($"f{i}.txt", string.Join("\n", Enumerable.Repeat("needle", 100)));
        var vm = _f.NewFind();
        vm.Query = "needle";
        vm.Start();
        vm.Dispose();
        _f.Ui.RunPending();
        Assert.Empty(vm.Rows);
        for (var i = 0; i < 100 && _f.Harness.Engine.RunningWorkers > 0; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(0, _f.Harness.Engine.RunningWorkers);
        Assert.True(vm.WaitForSearchAsync().IsCompleted);
    }

    [Fact]
    public async Task Copy_path_uses_the_window_service()
    {
        Populate();
        using var vm = _f.NewFind();
        await _f.SearchAsync(vm, "needle");
        var f = vm.Rows.OfType<FileRow>().First();
        Assert.Equal(f.Uri.LocalPath, vm.CopyPathOf(f));
        await vm.CopyPathAsync(f);
    }
}
