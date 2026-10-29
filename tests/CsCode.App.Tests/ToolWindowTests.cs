using System.Diagnostics;
using System.Text.Json;
using CsCode.App.Services;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.ToolWindows.ErrorList;
using CsCode.App.ToolWindows.FindResults;
using CsCode.App.ToolWindows.Output;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests;

public class ErrorListTests
{
    private static Diagnostic D(DiagnosticSeverity sev, int line, string msg = "boom", string? code = null) =>
        new(TextRange.At(line, 2), msg, sev, code, "src");

    private static Uri F(string name) => new("file:///proj/" + name);

    private static async Task<ErrorListViewModel> Settle(ManualUiDispatcher ui, ErrorListViewModel vm)
    {
        await vm.WaitIdleAsync();
        ui.RunPending();
        return vm;
    }

    [Fact]
    public async Task Rows_are_sorted_by_severity_then_file_then_line_and_counts_cover_everything()
    {
        var ui = new ManualUiDispatcher();
        var diags = new DiagnosticService();
        diags.Set(F("b.cs"), "t", [D(DiagnosticSeverity.Warning, 3), D(DiagnosticSeverity.Error, 9)]);
        diags.Set(F("a.cs"), "t", [D(DiagnosticSeverity.Error, 5), D(DiagnosticSeverity.Information, 1)]);
        using var vm = new ErrorListViewModel(diags, ui, minIntervalMs: 10);
        await Settle(ui, vm);
        Assert.Equal([("a.cs", 6), ("b.cs", 10), ("b.cs", 4)], vm.Rows.Take(3).Select(r => (r.FileName, r.Line)));
        Assert.Equal(DiagnosticSeverity.Information, vm.Rows[^1].Severity);
        Assert.Equal((2, 1, 1), (vm.ErrorCount, vm.WarningCount, vm.InfoCount));
    }

    [Fact]
    public async Task Filter_chips_and_search_narrow_the_rows_but_not_the_chip_counts()
    {
        var ui = new ManualUiDispatcher();
        var diags = new DiagnosticService();
        diags.Set(F("a.cs"), "t", [D(DiagnosticSeverity.Error, 1, "Missing semicolon", "CS1002"), D(DiagnosticSeverity.Warning, 2, "Unused variable x"), D(DiagnosticSeverity.Information, 3, "fyi")]);
        using var vm = new ErrorListViewModel(diags, ui, minIntervalMs: 5);
        await Settle(ui, vm);
        Assert.Equal(3, vm.Rows.Count);

        vm.ShowWarnings = false;
        vm.ShowInfo = false;
        await Settle(ui, vm);
        Assert.Single(vm.Rows);
        Assert.Equal(1, vm.WarningCount); // chip count still shows what was filtered out

        vm.ShowWarnings = true;
        vm.Search = "semicolon";
        await Settle(ui, vm);
        Assert.Equal("Missing semicolon", Assert.Single(vm.Rows).Message);
        vm.Search = "cs1002";
        await Settle(ui, vm);
        Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task Grouping_by_file_inserts_header_rows_with_counts()
    {
        var ui = new ManualUiDispatcher();
        var diags = new DiagnosticService();
        diags.Set(F("a.cs"), "t", [D(DiagnosticSeverity.Error, 1), D(DiagnosticSeverity.Error, 2)]);
        diags.Set(F("b.cs"), "t", [D(DiagnosticSeverity.Warning, 1)]);
        using var vm = new ErrorListViewModel(diags, ui, minIntervalMs: 5) { GroupByFile = true, Sort = ErrorListSort.File };
        await Settle(ui, vm);
        Assert.Equal([true, false, false, true, false], vm.Rows.Select(r => r.IsHeader));
        Assert.Equal(2, vm.Rows[0].HeaderCount);
        Assert.Contains("a.cs", vm.Rows[0].HeaderText);
    }

    [Fact]
    public async Task A_flood_of_diagnostic_updates_is_throttled_to_ten_rebuilds_per_second()
    {
        var ui = new ManualUiDispatcher();
        var diags = new DiagnosticService();
        using var vm = new ErrorListViewModel(diags, ui);
        await Settle(ui, vm);
        var baseline = vm.RebuildCount;
        var sw = Stopwatch.StartNew();
        var published = 0;
        // ~100k diagnostics/s from a "language server" thread for 600 ms.
        while (sw.ElapsedMilliseconds < 600)
        {
            for (var i = 0; i < 500; i++) { diags.Set(F($"f{published % 200}.cs"), "t", [D(DiagnosticSeverity.Error, published % 100)]); published++; }
            ui.RunPending();
        }
        await vm.WaitIdleAsync();
        ui.RunPending();
        var rebuilds = vm.RebuildCount - baseline;
        Assert.True(published > 20_000, $"only published {published}");
        Assert.True(rebuilds <= 9, $"{rebuilds} rebuilds in 600ms (budget: 10 Hz)"); // 600 ms / 100 ms = 6 + start + tail
        Assert.Equal(200, vm.Rows.Select(r => r.Uri).Distinct().Count());
    }

    [Fact]
    public async Task Fifty_thousand_diagnostics_are_aggregated_off_the_ui_thread_and_applied_in_one_small_post()
    {
        var ui = new ManualUiDispatcher();
        var diags = new DiagnosticService();
        for (var f = 0; f < 500; f++)
            diags.Set(F($"f{f}.cs"), "t", Enumerable.Range(0, 100).Select(i => D(i % 3 == 0 ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, i, "message " + i)).ToArray());
        using var vm = new ErrorListViewModel(diags, ui, minIntervalMs: 10);
        await vm.WaitIdleAsync(20_000);
        // Applying the finished array on the "UI thread" is a single property change.
        var sw = Stopwatch.StartNew();
        ui.RunPending();
        sw.Stop();
        Assert.Equal(50_000, vm.Rows.Count);
        Assert.True(sw.Elapsed.TotalMilliseconds < 4, $"UI-thread apply took {sw.Elapsed.TotalMilliseconds:F2} ms");
    }

    [Fact]
    public async Task Multi_line_messages_show_only_the_first_line_and_navigation_opens_the_range()
    {
        var ui = new ManualUiDispatcher();
        var diags = new DiagnosticService();
        var nav = new RecordingNavigationService(new Core.Text.DocumentManager());
        diags.Set(F("a.cs"), "t", [D(DiagnosticSeverity.Error, 4, "first line\nsecond line")]);
        using var vm = new ErrorListViewModel(diags, ui, nav, minIntervalMs: 5);
        await Settle(ui, vm);
        var row = Assert.Single(vm.Rows);
        Assert.Equal("first line", row.Message);
        await vm.NavigateAsync(row);
        Assert.Equal(F("a.cs"), nav.Opened.Single().Uri);
        Assert.Equal(4, nav.Opened.Single().Selection!.Value.Start.Line);
    }

    [Fact]
    public async Task A_disposed_view_model_stops_reacting_and_is_released()
    {
        var ui = new ManualUiDispatcher();
        var diags = new DiagnosticService();
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var vm = new ErrorListViewModel(diags, ui, minIntervalMs: 5);
            diags.Set(F("a.cs"), "t", [D(DiagnosticSeverity.Error, 1)]);
            await vm.WaitIdleAsync();
            vm.Dispose();
            diags.Set(F("b.cs"), "t", [D(DiagnosticSeverity.Error, 1)]);
            await Task.Delay(30);
            ui.RunPending();
            return [vm];
        });
    }
}

public class OutputViewModelTests
{
    [Fact]
    public void Appended_text_becomes_lines_and_partial_lines_are_completed_by_the_next_batch()
    {
        var ui = new ManualUiDispatcher();
        var svc = new OutputService(ui);
        var ch = svc.GetOrCreate("Build");
        using var vm = new OutputViewModel(svc);
        Assert.Equal("Build", vm.SelectedChannel);
        ch.Append("line one\nline tw");
        ui.RunPending();
        Assert.Equal(["line one", "line tw"], vm.Lines);
        ch.Append("o\nline three\n");
        ui.RunPending();
        Assert.Equal(["line one", "line two", "line three"], vm.Lines);
    }

    [Fact]
    public void Existing_text_is_loaded_when_a_channel_is_selected_and_switching_resets_the_lines()
    {
        var ui = new ManualUiDispatcher();
        var svc = new OutputService(ui);
        svc.GetOrCreate("A").AppendLine("from A");
        svc.GetOrCreate("B").AppendLine("from B");
        ui.RunPending();
        using var vm = new OutputViewModel(svc);
        Assert.Equal(["A", "B"], vm.Channels);
        Assert.Equal(["from A"], vm.Lines);
        vm.SelectedChannel = "B";
        Assert.Equal(["from B"], vm.Lines);
    }

    [Fact]
    public void Clear_empties_the_view_through_the_channel()
    {
        var ui = new ManualUiDispatcher();
        var svc = new OutputService(ui);
        var ch = svc.GetOrCreate("A");
        using var vm = new OutputViewModel(svc);
        ch.AppendLine("x");
        ui.RunPending();
        vm.Clear();
        ui.RunPending();
        Assert.Empty(vm.Lines);
        Assert.Equal("", ch.GetText());
    }

    [Fact]
    public void A_million_line_flood_keeps_the_line_list_capped_and_costs_few_posts()
    {
        var ui = new ManualUiDispatcher();
        var svc = new OutputService(ui);
        var ch = svc.GetOrCreate("Flood");
        using var vm = new OutputViewModel(svc);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1_000_000; i++) ch.AppendLine("line " + i);
        var producer = sw.ElapsedMilliseconds;
        Assert.True(ui.PendingCount <= 2);
        var apply = Stopwatch.StartNew();
        ui.RunPending();
        apply.Stop();
        Assert.InRange(vm.Lines.Count, 1, OutputViewModel.MaxLines);
        Assert.True(ui.Stats.Posted < 20, $"posted {ui.Stats.Posted} times for 1M lines");
        Assert.True(producer < 8000, $"producer side took {producer} ms");
    }

    [Fact]
    public async Task Long_lines_are_clipped_and_a_disposed_view_model_releases_its_channel_subscription()
    {
        var ui = new ManualUiDispatcher();
        var svc = new OutputService(ui);
        var ch = svc.GetOrCreate("A");
        using (var vm = new OutputViewModel(svc))
        {
            ch.AppendLine(new string('x', 50_000));
            ui.RunPending();
            Assert.True(vm.Lines[0].Length <= OutputViewModel.MaxLineLength + 1);
        }
        await LeakAssert.NotRetainedAsync(() =>
        {
            var vm = new OutputViewModel(svc);
            ch.AppendLine("y");
            ui.RunPending();
            vm.Dispose();
            ch.AppendLine("z");
            ui.RunPending();
            return Task.FromResult<object[]>([vm]);
        });
    }
}

public class FindResultsTests
{
    private static Location L(string file, int line, int ch = 0) => new(new Uri("file:///p/" + file), TextRange.At(line, ch));

    [Fact]
    public void Results_are_grouped_by_file_with_counts_and_sorted_by_position()
    {
        var vm = new FindResultsViewModel();
        vm.Show("References", [L("b.cs", 9), L("a.cs", 5), L("a.cs", 2)]);
        Assert.Equal([true, false, false, true, false], vm.Rows.Select(r => r.IsHeader));
        Assert.Equal([3, 6], vm.Rows.Where(r => !r.IsHeader && r.Location.Uri.ToString().EndsWith("a.cs")).Select(r => r.Line));
        Assert.Contains("3 results", vm.Title);
        Assert.Equal(3, vm.HitCount);
    }

    [Fact]
    public void The_show_command_accepts_core_locations_and_lsp_shaped_json()
    {
        var vm = new FindResultsViewModel();
        vm.ShowFromCommand(["Rename", new List<Location> { L("a.cs", 1) }]);
        Assert.Equal(1, vm.HitCount);
        var json = JsonDocument.Parse("""[{"uri":"file:///p/x.cs","range":{"start":{"line":3,"character":1},"end":{"line":3,"character":4}}},{"bad":true}]""").RootElement;
        vm.ShowFromCommand([json, "From JSON"]);
        Assert.Equal(1, vm.HitCount);
        Assert.StartsWith("From JSON", vm.Title);
    }

    [Fact]
    public async Task Preview_text_is_read_lazily_from_the_open_document_then_disk_with_a_bounded_cache()
    {
        using var dir = new TempDirectory("cscode-find-");
        for (var f = 0; f < 40; f++) dir.WriteFile($"f{f}.txt", "zero\n   the hit line  \nlast");
        var docs = new DocumentManager();
        var open = new FakeEditorDocument(new Uri("file:///open/doc.cs"), "alpha\nfrom the open document\n");
        docs.Register(open);
        var vm = new FindResultsViewModel(null, new DiskFileSystem(), docs);
        var locations = Enumerable.Range(0, 40).Select(f => new Location(new Uri(dir.Combine($"f{f}.txt")), TextRange.At(1, 0))).Append(new Location(open.Uri, TextRange.At(1, 0))).ToList();
        vm.Show("x", locations);
        var rows = vm.Rows.Where(r => !r.IsHeader).ToList();
        Assert.All(rows, r => Assert.Null(r.Preview)); // nothing read up front
        foreach (var r in rows) await vm.LoadPreviewAsync(r);
        Assert.All(rows.Where(r => r.Location.Uri != open.Uri), r => Assert.Equal("the hit line", r.Preview));
        Assert.Equal("from the open document", rows.Single(r => r.Location.Uri == open.Uri).Preview);
    }

    [Fact]
    public void Huge_result_sets_are_truncated_and_clear_resets()
    {
        var vm = new FindResultsViewModel();
        vm.Show("many", Enumerable.Range(0, FindResultsViewModel.MaxResults + 500).Select(i => L("a.cs", i)).ToList());
        Assert.True(vm.Truncated);
        Assert.Equal(FindResultsViewModel.MaxResults, vm.HitCount);
        vm.Clear();
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Opening_a_row_navigates_and_headers_do_nothing()
    {
        var nav = new RecordingNavigationService(new DocumentManager());
        var vm = new FindResultsViewModel(nav);
        vm.Show("x", [L("a.cs", 7, 3)]);
        await vm.OpenAsync(vm.Rows[0]);
        Assert.Empty(nav.Opened);
        await vm.OpenAsync(vm.Rows[1]);
        Assert.Equal(new TextPosition(7, 3), nav.Opened.Single().Selection!.Value.Start);
    }
}
