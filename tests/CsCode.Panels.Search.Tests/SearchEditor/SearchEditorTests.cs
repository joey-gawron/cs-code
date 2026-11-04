using System.Diagnostics;
using System.Text.Json;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Panels.Search.Engine;
using CsCode.Panels.Search.SearchEditor;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Search.Tests.SearchEditor;

/// <summary>The search editor end to end: open, stream, re-run, cancel, navigate, highlight, release.</summary>
public sealed class SearchEditorTests : IDisposable
{
    private readonly SearchEditorFixture _x = new();

    public void Dispose() => _x.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Workspace()
    {
        _x.Write("src/a.cs", "using System;\nclass Alpha\n{\n    int alpha = 1;\n}\n");
        _x.Write("src/b.cs", "// beta\nclass Beta { }\n");
        _x.Write("docs/readme.md", "alpha and beta\n");
    }

    [Fact]
    public async Task A_new_search_editor_shows_the_header_then_the_results_grouped_by_file_in_path_order()
    {
        Workspace();
        var doc = await _x.OpenAsync(new SearchEditorConfig { Query = "alpha", ContextLines = 1 });
        Assert.Equal(
            "# Query: alpha\n# ContextLines: 1\n\n3 results - 2 files\n\n" +
            "docs/readme.md:\n  1: alpha and beta\n\n" +
            "src/a.cs:\n  1  using System;\n  2: class Alpha\n  3  {\n  4:     int alpha = 1;\n  5  }\n\n",
            doc.GetText());
        Assert.Equal(SearchEditorFormat.LanguageId, doc.LanguageId);
        var options = Assert.Single(_x.Nav.Untitled);
        Assert.Equal("Search: alpha", options.Title);
        Assert.Equal("alpha.code-search", Path.GetFileName(options.SuggestedSaveUri!.LocalPath));
        Assert.Equal(new TextPosition(0, "# Query: alpha".Length), options.Selection!.Value.Start); // the caret on the query
        // the whole run is one undo step
        Assert.Equal(1, doc.UndoGroupsOpened);
        Assert.Equal(0, doc.UndoGroupsOpen);
    }

    [Fact]
    public async Task Editing_the_query_and_searching_again_replaces_the_results_in_place()
    {
        Workspace();
        var doc = await _x.OpenAsync(new SearchEditorConfig { Query = "alpha" });
        doc.SetLine(0, "# Query: Beta");
        var flags = "# Flags: CaseSensitive";
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(1, 0), flags + "\n")]);
        _x.Nav.Activate(doc, new TextPosition(0, 5));
        await _x.F.Commands.ExecuteAsync(SearchEditorService.RerunEditorSearch);
        await _x.WaitAsync(doc);
        Assert.Equal("# Query: Beta\n# Flags: CaseSensitive\n\n1 result - 1 file\n\nsrc/b.cs:\n  2: class Beta { }\n\n", doc.GetText());
        Assert.Equal("Search: Beta", _x.Nav.Titles[doc.Uri].Title); // the untitled tab follows its query
        Assert.Equal(2, doc.UndoGroupsOpened);
    }

    [Fact]
    public async Task An_empty_query_clears_the_results_and_a_bad_regex_says_why()
    {
        Workspace();
        var doc = await _x.OpenAsync(new SearchEditorConfig { Query = "alpha" });
        doc.SetLine(0, "# Query: ");
        _x.Service.SessionOf(doc)!.Rerun();
        Assert.Equal("# Query: \n", doc.GetText());

        doc.SetLine(0, "# Query: (unclosed");
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(1, 0), "# Flags: RegExp\n")]);
        _x.Service.SessionOf(doc)!.Rerun();
        await _x.WaitAsync(doc);
        var summary = doc.GetLineText(3);
        Assert.StartsWith("No Results - ", summary);
        Assert.False(summary.EndsWith(':'));
    }

    [Fact]
    public async Task A_superseded_run_is_cancelled_and_leaves_nothing_behind()
    {
        for (var i = 0; i < 400; i++) _x.Write($"many/f{i:D3}.txt", string.Join("\n", Enumerable.Repeat("needle haystack", 50)));
        _x.Write("one.txt", "unique\n");
        var open = _x.Service.OpenNewAsync(new SearchEditorConfig { Query = "needle" });
        await _x.Ui.PumpAsync(open);
        var doc = (SearchEditorDocument)(await open)!;
        var session = _x.Service.SessionOf(doc)!;
        var first = session.Run!;
        var firstEngine = first.Session;

        doc.SetLine(0, "# Query: unique");
        var second = session.Rerun()!;
        Assert.NotSame(first, second);
        Assert.Null(await first.Completion);                 // stopped, not completed
        if (firstEngine is not null)
        {
            var outcome = await firstEngine.Result.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.True(outcome.Outcome is SearchOutcome.Cancelled or SearchOutcome.Completed);
            await firstEngine.WorkersStopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
        await _x.WaitAsync(doc);
        Assert.Equal("# Query: unique\n\n1 result - 1 file\n\none.txt:\n  1: unique\n\n", doc.GetText());
        Assert.Equal(0, doc.UndoGroupsOpen);
        _x.Ui.RunPending(); // chunks the first run had queued are dropped
        Assert.DoesNotContain("needle", doc.GetText());
    }

    [Fact]
    public async Task Search_as_you_type_runs_once_typing_pauses_in_the_header()
    {
        Workspace();
        _x.F.ConfigStore.Set(SearchEditorService.SearchOnTypeDebounceKey, 20);
        var doc = await _x.OpenAsync(new SearchEditorConfig { Query = "alpha" });
        var session = _x.Service.SessionOf(doc)!;
        var before = session.Run;
        doc.SetLine(0, "# Query: b");
        doc.SetLine(0, "# Query: be");
        doc.SetLine(0, "# Query: beta");
        await _x.Ui.PumpUntilAsync(() => !ReferenceEquals(session.Run, before));
        await _x.WaitAsync(doc);
        Assert.Contains("src/b.cs:", doc.GetText());
        Assert.Equal("beta", session.LastConfig!.Query);

        // edits of the results do not search; with the setting off the header does not either
        var run = session.Run;
        doc.ApplyEdits([TextEdit.Insert(new TextPosition(doc.LineCount - 1, 0), "note\n")]);
        _x.F.ConfigStore.Set(SearchEditorService.SearchOnTypeKey, false);
        doc.SetLine(0, "# Query: alpha");
        await Task.Delay(80, Ct);
        _x.Ui.RunPending();
        Assert.Same(run, session.Run);
    }

    [Fact]
    public async Task Large_result_sets_are_capped_and_stream_in_small_ui_slices()
    {
        using var x = new SearchEditorFixture(new SearchLimits { MaxResults = 3_000 });
        for (var i = 0; i < 60; i++) x.Write($"big/f{i:D2}.txt", string.Join("\n", Enumerable.Range(0, 100).Select(n => $"hit number {n} of file {i}")));
        var timed = new TimingDispatcher(x.Ui);
        var open = x.Service.OpenNewAsync(new SearchEditorConfig { Query = "hit" });
        await x.Ui.PumpAsync(open);
        var doc = (SearchEditorDocument)(await open)!;
        var run = x.Service.SessionOf(doc)!.Run!;
        await timed.PumpAsync(run.Completion);
        var completion = await run.Completion;
        Assert.Equal(SearchOutcome.Truncated, completion!.Outcome);
        Assert.Contains(SearchEditorFormat.LimitHitMessage, doc.GetLineText(2));
        Assert.Equal(3_000, doc.GetText().Split('\n').Count(l => SearchEditorFormat.Classify(l) == SearchResultLineKind.Match));
        Assert.True(run.ChunksApplied > 1, $"streamed in {run.ChunksApplied} chunk(s)");
        // each UI callback inserts at most a chunk's worth of finished text: far below a frame
        Assert.True(timed.Longest < TimeSpan.FromMilliseconds(100), $"longest UI callback {timed.Longest.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void Starting_a_run_costs_the_caller_only_its_first_edit()
    {
        for (var i = 0; i < 200; i++) _x.Write($"f{i}.txt", "x\n");
        var doc = new SearchEditorDocument(new Uri("untitled:Untitled-99"), SearchEditorFormat.LanguageId, "# Query: x\n\n");
        using var reg = _x.F.Harness.Documents.Register(doc);
        var session = _x.Service.SessionOf(doc)!;
        var sw = Stopwatch.StartNew();
        var run = session.Rerun()!;
        sw.Stop();
        Assert.False(run.IsFinished);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250), $"Rerun took {sw.Elapsed.TotalMilliseconds:F1} ms on the caller");
        Assert.Equal("# Query: x\n\n" + SearchEditorRun.Searching + "\n\n", doc.GetText());
        run.Stop();
    }

    [Fact]
    public async Task Result_lines_lead_to_their_file_line_and_column()
    {
        Workspace();
        var doc = await _x.OpenAsync(new SearchEditorConfig { Query = "alpha", ContextLines = 1 });
        var folders = _x.F.Harness.Workspace.Folders;
        var a = _x.F.Harness.UriOf("src/a.cs");
        int Line(string text) => Enumerable.Range(0, doc.LineCount).First(l => doc.GetLineText(l) == text);

        var match = Line("  4:     int alpha = 1;");
        var t = SearchResultFeatures.Resolve(doc, new TextPosition(match, "  4: ".Length + 8), folders, Ct)!;
        Assert.Equal(a.LocalPath, t.Uri.LocalPath);
        Assert.Equal(new TextPosition(3, 8), t.Position);                                 // the column under the click
        Assert.Equal(new TextPosition(3, 0), SearchResultFeatures.Resolve(doc, new TextPosition(match, 1), folders, Ct)!.Position); // the line number: the line's start
        Assert.Equal(new TextPosition(0, 2), SearchResultFeatures.Resolve(doc, new TextPosition(Line("  1  using System;"), 7), folders, Ct)!.Position); // context lines too
        var file = SearchResultFeatures.Resolve(doc, new TextPosition(Line("src/a.cs:"), 3), folders, Ct)!;
        Assert.Equal((a.LocalPath, TextPosition.Zero), (file.Uri.LocalPath, file.Position));
        Assert.Null(SearchResultFeatures.Resolve(doc, new TextPosition(0, 3), folders, Ct));     // the header
        Assert.Null(SearchResultFeatures.Resolve(doc, new TextPosition(3, 3), folders, Ct));     // the summary

        // F12 / Ctrl+click: the definition provider; path lines are document links
        var definition = (await _x.Service.Features.ProvideDefinitionAsync(doc, new TextPosition(match, 10), DefinitionKind.Definition, Ct))!;
        Assert.Equal(new TextPosition(3, 5), Assert.Single(definition).TargetSelectionRange.Start);
        var links = (await _x.Service.Features.ProvideDocumentLinksAsync(doc, Ct))!;
        Assert.Equal(2, links.Count);
        Assert.Contains(links, l => l.Target!.LocalPath == a.LocalPath && l.Range.Start.Line == Line("src/a.cs:"));
        Assert.Same(_x.Service.Features, Assert.Single(_x.Features.GetProviders<IDefinitionProvider>(doc)));
    }

    [Fact]
    public void Elided_text_maps_back_to_the_columns_it_stands_for()
    {
        var text = "# Query: x\n\n1 result - 1 file\n\n/abs/f.txt:\n  7: ⟪ 2900 characters skipped ⟫abcdef⟪ 50 characters skipped ⟫\n";
        var doc = new SearchEditorDocument(new Uri("untitled:Untitled-1"), SearchEditorFormat.LanguageId, text);
        var row = 5;
        var start = doc.GetLineText(row).IndexOf("abc", StringComparison.Ordinal);
        var t = SearchResultFeatures.Resolve(doc, new TextPosition(row, start + 2), [], Ct)!;
        Assert.Equal("/abs/f.txt", t.Uri.LocalPath);
        Assert.Equal(new TextPosition(6, 2902), t.Position);
    }

    [Fact]
    public async Task A_double_click_opens_the_result_as_the_setting_says()
    {
        Workspace();
        var doc = await _x.OpenAsync(new SearchEditorConfig { Query = "beta" });
        var handler = Assert.Single(_x.Features.GetProviders<IDoubleClickHandler>(doc));
        var line = Enumerable.Range(0, doc.LineCount).First(l => doc.GetLineText(l) == "  2: class Beta { }");

        Assert.True(handler.OnDoubleClick(doc, new TextPosition(line, 12)));
        await WaitFor(() => _x.Nav.Opened.Count == 1);
        Assert.Equal(new TextRange(new TextPosition(1, 7), new TextPosition(1, 7)), _x.Nav.Opened[0].Selection);

        _x.F.ConfigStore.Set(SearchEditorService.DoubleClickKey, SearchEditorService.DoubleClickOpenToSide);
        Assert.True(handler.OnDoubleClick(doc, new TextPosition(line, 12)));
        await WaitFor(() => _x.Nav.OpenedToSide.Count == 1);

        _x.F.ConfigStore.Set(SearchEditorService.DoubleClickKey, SearchEditorService.DoubleClickSelectWord);
        Assert.False(handler.OnDoubleClick(doc, new TextPosition(line, 12)));
        _x.F.ConfigStore.Set(SearchEditorService.DoubleClickKey, SearchEditorService.DoubleClickGoTo);
        Assert.False(handler.OnDoubleClick(doc, new TextPosition(0, 3))); // the header selects words
    }

    [Fact]
    public async Task Matches_are_highlighted_with_the_query_the_results_came_from()
    {
        Workspace();
        var doc = await _x.OpenAsync(new SearchEditorConfig { Query = "alpha", ContextLines = 1 });
        var provider = Assert.Single(_x.Features.GetProviders<IMatchHighlightProvider>(doc));
        var ranges = (await provider.ProvideMatchHighlightsAsync(doc, Ct))!;
        Assert.Equal(3, ranges.Count); // not the summary, not the header, not the context lines
        Assert.All(ranges, r => Assert.Equal("alpha", doc.GetText(r), StringComparer.OrdinalIgnoreCase));

        // typing another query does not move the highlights until it runs
        _x.F.ConfigStore.Set(SearchEditorService.SearchOnTypeKey, false);
        doc.SetLine(0, "# Query: class");
        Assert.Equal(3, (await provider.ProvideMatchHighlightsAsync(doc, Ct))!.Count);

        // a saved file opened here: its header's query
        var saved = new SearchEditorDocument(new Uri("file:///w/saved.code-search"), SearchEditorFormat.LanguageId,
            "# Query: beta\n# Flags: CaseSensitive\n\n1 result - 1 file\n\nsrc/b.cs:\n  1: // beta Beta\n");
        var r = Assert.Single((await provider.ProvideMatchHighlightsAsync(saved, Ct))!);
        Assert.Equal(new TextRange(new TextPosition(6, 8), new TextPosition(6, 12)), r);
    }

    [Fact]
    public async Task Open_results_in_editor_writes_what_find_in_files_shows()
    {
        Workspace();
        _x.F.ConfigStore.Set(SearchEditorService.ContextLinesKey, 0);
        using var find = new CsCode.Panels.Search.ViewModels.FindInFilesViewModel(_x.F.With(navigation: _x.Nav));
        find.Include = "src";
        await _x.F.SearchAsync(find, "class");
        find.Dismiss(find.Rows.OfType<CsCode.Panels.Search.ViewModels.FileRow>().Single(f => f.Name == "b.cs"));
        var open = _x.Service.OpenInEditorAsync(find);
        await _x.Ui.PumpAsync(open);
        var doc = (SearchEditorDocument)(await open)!;
        await _x.WaitAsync(doc);
        Assert.Equal("# Query: class\n# Including: src\n\n1 result - 1 file\n\nsrc/a.cs:\n  2: class Alpha\n\n", doc.GetText());
    }

    [Fact]
    public async Task Commands_open_search_editors_from_arguments_and_the_selection()
    {
        Workspace();
        using var x = new SearchEditorFixture(seed: () => "Beta");
        x.Write("b.cs", "class Beta { }\n");
        foreach (var id in new[] { SearchEditorService.OpenNewEditor, SearchEditorService.OpenNewEditorToSide, SearchEditorService.OpenInEditor, SearchEditorService.RerunEditorSearch })
            Assert.True(x.F.Commands.Has(id), id);

        var opened = x.F.Commands.ExecuteAsync(SearchEditorService.OpenNewEditor).AsTask();
        await x.Ui.PumpAsync(opened);
        var seeded = (SearchEditorDocument)x.Nav.ActiveDocument!;
        await x.WaitAsync(seeded);
        Assert.StartsWith("# Query: Beta\n# ContextLines: 1\n\n1 result - 1 file\n", seeded.GetText());

        var args = JsonSerializer.SerializeToElement(new { query = "B.ta", isRegexp = true, isCaseSensitive = true, contextLines = 0, triggerSearch = false });
        var toSide = x.F.Commands.ExecuteAsync(SearchEditorService.OpenNewEditorToSide, args).AsTask();
        await x.Ui.PumpAsync(toSide);
        Assert.True(x.Nav.Untitled[^1].ToSide);
        Assert.Equal("# Query: B.ta\n# Flags: CaseSensitive RegExp\n\n", x.Nav.ActiveDocument!.GetText()); // not searched
    }

    [Fact]
    public async Task A_new_search_editor_can_reuse_the_previous_ones_flags_and_globs()
    {
        Workspace();
        await _x.OpenAsync(new SearchEditorConfig { Query = "x", IsRegex = true, Include = "src", ContextLines = 3 });
        Assert.Equal(new SearchEditorConfig { Query = "y", ContextLines = 1 }, _x.Service.NewConfig("y"));
        _x.F.ConfigStore.Set(SearchEditorService.ReuseConfigurationKey, true);
        Assert.Equal(new SearchEditorConfig { Query = "y", IsRegex = true, Include = "src", ContextLines = 3 }, _x.Service.NewConfig("y"));
    }

    [Fact]
    public async Task Context_keys_say_when_the_caret_is_on_the_header_of_the_active_search_editor()
    {
        Workspace();
        var doc = await _x.OpenAsync(new SearchEditorConfig { Query = "alpha", ContextLines = 1 });
        var keys = _x.F.ContextKeys;
        Assert.Equal(true, keys.Get(SearchEditorService.InSearchEditorKey));
        Assert.Equal(true, keys.Get(SearchEditorService.HeaderFocusKey)); // opened with the caret on the query
        _x.Nav.MoveCaret(new TextPosition(1, 0));
        Assert.Equal(true, keys.Get(SearchEditorService.HeaderFocusKey)); // # ContextLines
        _x.Nav.MoveCaret(new TextPosition(5, 0));
        Assert.Equal(false, keys.Get(SearchEditorService.HeaderFocusKey));
        _x.Nav.Activate(null);
        Assert.Equal(false, keys.Get(SearchEditorService.InSearchEditorKey));
    }

    [Fact]
    public async Task Code_search_files_get_a_session_and_lose_it_when_closed_or_turned_into_another_language()
    {
        var file = new SearchEditorDocument(new Uri("file:///w/x.code-search"), SearchEditorFormat.LanguageId, "# Query: a\n\n");
        var reg = _x.F.Harness.Documents.Register(file);
        Assert.NotNull(_x.Service.SessionOf(file));
        file.SetLanguageId("plaintext");
        Assert.Null(_x.Service.SessionOf(file));
        file.SetLanguageId(SearchEditorFormat.LanguageId);
        Assert.NotNull(_x.Service.SessionOf(file));
        reg.Dispose();
        _x.Ui.RunPending();
        Assert.Null(_x.Service.SessionOf(file));
        Assert.Equal(0, _x.Service.SessionCount);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_closed_search_editor_and_its_session_are_not_retained()
    {
        Workspace();
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var open = _x.Service.OpenNewAsync(new SearchEditorConfig { Query = "alpha", ContextLines = 2 });
            await _x.Ui.PumpAsync(open);
            var doc = (SearchEditorDocument)(await open)!;
            var session = _x.Service.SessionOf(doc)!;
            await _x.WaitAsync(doc);
            var run = session.Run!;
            _ = await _x.Service.Features.ProvideMatchHighlightsAsync(doc, Ct);
            _x.Nav.Close(doc);
            _x.Ui.RunPending();
            Assert.Null(_x.Service.SessionOf(doc));
            return [doc, session, run];
        });
        Assert.Equal(0, _x.Service.SessionCount);
    }

    [Fact]
    public async Task Closing_a_search_editor_mid_run_stops_its_search()
    {
        for (var i = 0; i < 300; i++) _x.Write($"m/f{i:D3}.txt", string.Join("\n", Enumerable.Repeat("needle", 40)));
        var open = _x.Service.OpenNewAsync(new SearchEditorConfig { Query = "needle" });
        await _x.Ui.PumpAsync(open);
        var doc = (SearchEditorDocument)(await open)!;
        var run = _x.Service.SessionOf(doc)!.Run!;
        var engine = run.Session;
        _x.Nav.Close(doc);
        _x.Ui.RunPending();
        Assert.Null(await run.Completion);
        if (engine is not null) await engine.WorkersStopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(0, doc.UndoGroupsOpen);
    }

    private async Task WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            _x.Ui.RunPending();
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException();
            await Task.Delay(5, Ct);
        }
    }

    /// <summary>Pumps a manual dispatcher and records how long its longest callback ran.</summary>
    private sealed class TimingDispatcher(ManualUiDispatcher inner)
    {
        public TimeSpan Longest { get; private set; }

        public async Task PumpAsync(Task until, int timeoutMs = 30_000)
        {
            var sw = Stopwatch.StartNew();
            while (!until.IsCompleted)
            {
                var t = Stopwatch.StartNew();
                var ran = inner.RunOne();
                t.Stop();
                if (ran && t.Elapsed > Longest) Longest = t.Elapsed;
                if (!ran) await Task.Delay(2, Ct);
                if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException();
            }
        }
    }
}
