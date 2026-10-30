using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AKey = Avalonia.Input.Key;
using CsCode.Core.Text;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.TextMate;
using CsCode.Editor.View;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FindSearchCollection
{
    public const string Name = "Find search (process-wide timeout)";
}

/// <summary>
/// editor-4, the in-editor Find (AvaloniaEdit's search panel) the way VS Code's find widget behaves: at most 19,999 matches, a regex that
/// backtracks without end is stopped by a match timeout, and while the panel is open an edit no longer re-searches the whole document on the
/// UI thread: the matches move with the edit, and the re-search runs on the editor worker once the typing pauses. The limit is on what the
/// panel holds, not on what Find reaches: navigation past the held matches, a new pattern and Replace All search the text (VS Code too).
/// </summary>
[Collection(FindSearchCollection.Name)]
public class FindSearchTests
{
    private static FindSearch OpenFind(CodeEditorControl view, string pattern, bool regex = false)
    {
        Assert.True(FindSearch.Supported, "AvaloniaEdit's search panel layout changed: the find search is not installed");
        view.Controller.ShowFind(false);
        var panel = view.Editor.SearchPanel;
        panel.UseRegex = regex;
        panel.SearchPattern = pattern;
        ViewHost.Pump();
        return view.Feature<FindSearch>()!;
    }

    [AvaloniaFact]
    public async Task Typing_with_Find_open_on_a_big_document_never_re_searches_it_on_the_UI_thread()
    {
        using var host = ViewHost.Create();
        var text = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"the quick brown fox {i} jumped over the fence\n"));
        var (doc, view, _) = await host.OpenAsync(text, "big.txt");
        var find = OpenFind(view, "e");
        Assert.Equal(1, find.UiThreadSearches);
        Assert.Equal(FindSearch.MaxResults, find.ResultCount); // VS Code's limit: ~140k matches in the text
        Assert.True(find.LastCapped);
        Assert.Contains("19,999", find.Message, StringComparison.Ordinal);

        view.Controller.Focus();
        view.Controller.SetSelection(TextRange.At(100, 0));
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
        {
            Type(view, "x");
            ViewHost.Pump();
        }
        Assert.Equal(1, find.UiThreadSearches); // no whole-document search per keystroke
        Assert.Equal(FindSearch.MaxResults, find.ResultCount); // what the panel shows stays (moved with the edit)
        await ViewHost.WaitFor(() => find.WorkerSearches >= 1, "the re-search once the typing paused");
        await ViewHost.WaitFor(() => find.ResultCount == FindSearch.MaxResults, "the worker's results applied");
        Assert.Equal(1, find.UiThreadSearches);
        Assert.Equal("xxxxx", doc.GetLineText(100)[..5]);
        TestContext.Current.TestOutputHelper?.WriteLine($"5 keystrokes with Find open: {clock.ElapsedMilliseconds} ms");
    }

    [AvaloniaFact]
    public async Task Matches_move_with_an_edit_and_the_re_search_finds_new_ones()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("foo bar foo\nfoo\n", "small.txt");
        var find = OpenFind(view, "foo");
        Assert.Equal([0, 8, 12], find.ResultStarts.Order());

        view.Controller.Focus();
        view.Controller.SetSelection(TextRange.At(0, 4)); // inside the first line, between the first two matches
        Type(view, "XY");
        ViewHost.Pump();
        Assert.Equal([0, 10, 14], find.ResultStarts.Order()); // moved at once, nothing searched yet
        Type(view, "foo");
        ViewHost.Pump();
        await ViewHost.WaitFor(() => find.ResultCount == 4, $"the new match found after the pause ({string.Join(",", find.ResultStarts)})");
        Assert.Equal([0, 6, 13, 17], find.ResultStarts.Order());
        Assert.Equal("foo XYfoobar foo\nfoo\n", doc.GetText());
        Assert.Equal(1, find.UiThreadSearches);
    }

    [AvaloniaFact]
    public async Task A_regex_that_backtracks_without_end_is_stopped_and_the_panel_says_so()
    {
        var previous = FindSearch.MatchTimeout;
        FindSearch.MatchTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            using var host = ViewHost.Create();
            // (x+x+)+y over 40 x's: ~36 s per attempt with .NET's backtracking engine (the review measured 2.5x per 2 more characters)
            var (_, view, _) = await host.OpenAsync("abc\n" + new string('x', 40) + "\nxy\n", "redos.txt");
            var clock = Stopwatch.StartNew();
            var find = OpenFind(view, "(x+x+)+y", regex: true);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the search held the UI thread for {clock.Elapsed.TotalSeconds:0.0} s");
            Assert.True(find.LastTimedOut);
            Assert.Contains("took too long", find.Message, StringComparison.Ordinal);
        }
        finally
        {
            FindSearch.MatchTimeout = previous;
        }
    }

    /// <summary>A log of <paramref name="lines"/> lines of 12 characters, "ERROR" at column 6 of each: more matches than the limit.</summary>
    private static string Log(int lines) => string.Concat(Enumerable.Range(0, lines).Select(i => $"{i:D5} ERROR\n"));

    private static int ErrorAt(int line) => 12 * line + 6;

    private static int Count(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    private static void PlaceCaret(CodeEditorControl view, int offset)
    {
        view.Editor.TextArea.ClearSelection();
        view.Editor.CaretOffset = offset;
    }

    [AvaloniaFact]
    public async Task Find_Next_and_Previous_reach_the_matches_past_the_limit_and_wrap_over_the_whole_document()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync(Log(30_000), "log.txt");
        var find = OpenFind(view, "ERROR");
        Assert.True(find.LastCapped);
        Assert.Equal(FindSearch.MaxResults, find.ResultCount);

        // F3 / Shift+F3 with the keyboard in the text, the caret far below the last held match (line 19,998)
        view.Controller.Focus();
        PlaceCaret(view, 12 * 25_000);
        Key(window, AKey.F3);
        Assert.Equal((ErrorAt(25_000), 5), (view.Editor.SelectionStart, view.Editor.SelectionLength)); // not line 0
        Key(window, AKey.F3);
        Assert.Equal(ErrorAt(25_001), view.Editor.SelectionStart);
        Key(window, AKey.F3, RawInputModifiers.Shift);
        Assert.Equal(ErrorAt(25_000), view.Editor.SelectionStart); // not the last held match

        // Enter / Shift+Enter in the find box
        view.Editor.SearchPanel.Reactivate();
        ViewHost.Pump();
        Key(window, AKey.Enter);
        Assert.Equal(ErrorAt(25_001), view.Editor.SelectionStart);
        Key(window, AKey.Enter, RawInputModifiers.Shift);
        Assert.Equal(ErrorAt(25_000), view.Editor.SelectionStart);

        // wrapping: from the last match to the first, and from the first back to the LAST IN THE DOCUMENT
        view.Controller.Focus();
        PlaceCaret(view, ErrorAt(29_999) + 5);
        Key(window, AKey.F3);
        Assert.Equal(ErrorAt(0), view.Editor.SelectionStart);
        Key(window, AKey.F3, RawInputModifiers.Shift);
        Assert.Equal(ErrorAt(29_999), view.Editor.SelectionStart);

        // among the held matches the panel answers as before (no search)
        var searches = find.TextSearches;
        PlaceCaret(view, 12 * 100);
        Key(window, AKey.F3);
        Assert.Equal(ErrorAt(100), view.Editor.SelectionStart);
        Key(window, AKey.F3, RawInputModifiers.Shift);
        Assert.Equal(ErrorAt(99), view.Editor.SelectionStart);
        Assert.Equal(searches, find.TextSearches);
        Assert.Contains("19,999", find.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_new_pattern_selects_the_match_after_the_caret_even_past_the_limit()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync(Log(30_000), "log.txt");
        view.Controller.Focus();
        PlaceCaret(view, 12 * 25_000 + 2);
        var find = OpenFind(view, "ERROR");
        Assert.True(find.LastCapped);
        Assert.Equal((ErrorAt(25_000), 5), (view.Editor.SelectionStart, view.Editor.SelectionLength)); // not the first match at the top
        // and F3 goes on from there
        view.Controller.Focus();
        Key(window, AKey.F3);
        Assert.Equal(ErrorAt(25_001), view.Editor.SelectionStart);
    }

    [AvaloniaFact]
    public async Task Replace_All_replaces_every_match_past_the_limit_in_one_undo_step()
    {
        using var host = ViewHost.Create();
        var text = Log(30_000);
        var (doc, view, window) = await host.OpenAsync(text, "log.txt");
        view.Controller.ShowFind(true);
        var panel = view.Editor.SearchPanel;
        panel.SearchPattern = "ERROR";
        panel.ReplacePattern = "WARN";
        ViewHost.Pump();
        var find = view.Feature<FindSearch>()!;
        Assert.True(find.LastCapped);

        Key(window, AKey.A, RawInputModifiers.Alt); // Alt+A, the keyboard in the find box
        ViewHost.Pump();
        var replaced = doc.GetText();
        Assert.Equal(0, Count(replaced, "ERROR")); // the stock panel replaced the 19,999 it held and left 10,001
        Assert.Equal(30_000, Count(replaced, "WARN"));
        Assert.Equal(0, find.ResultCount); // searched again at once

        view.Controller.Undo();
        Assert.Equal(text, doc.GetText());
    }

    /// <summary>
    /// Between an edit and its re-search (the pause plus the worker) the panel's matches are only moved. A match deleted from another view of the
    /// document became an empty match there that Replace All filled; a match edited into "fox" had its "fo" replaced. Replace and Replace All
    /// search the text as it is now, as VS Code's do.
    /// </summary>
    [AvaloniaFact]
    public async Task Replace_and_Replace_All_act_on_the_current_text_not_on_matches_moved_by_an_edit()
    {
        var previous = FindSearch.EditDelay;
        FindSearch.EditDelay = TimeSpan.FromHours(1); // the window between an edit and its re-search, held open
        try
        {
            using var host = ViewHost.Create();
            var (doc, view, window) = await host.OpenAsync("foo bar foo\nfoo\n", "small.txt");
            view.Controller.ShowFind(true);
            var panel = view.Editor.SearchPanel;
            panel.SearchPattern = "foo";
            panel.ReplacePattern = "X";
            ViewHost.Pump();
            var find = view.Feature<FindSearch>()!;
            Assert.Equal([0, 8, 12], find.ResultStarts.Order());

            view.Editor.Document.Remove(8, 3); // another view of the document, an LSP edit, a reload
            ViewHost.Pump();
            Assert.Equal([0, 8, 9], find.ResultStarts.Order()); // moved, not searched: an empty match at 8
            Key(window, AKey.A, RawInputModifiers.Alt);
            ViewHost.Pump();
            Assert.Equal("X bar \nX\n", doc.GetText());

            view.Editor.Document.Text = "foo foo\n";
            ViewHost.Pump();
            panel.SearchPattern = "fo";
            panel.SearchPattern = "foo";
            ViewHost.Pump();
            Assert.Equal([0, 4], find.ResultStarts.Order());
            PlaceCaret(view, 4);
            view.Editor.Document.Replace(6, 1, "x"); // "foo fox": the second held match still covers 4-7
            ViewHost.Pump();
            Assert.Equal([0, 4], find.ResultStarts.Order());
            Key(window, AKey.R, RawInputModifiers.Alt); // Replace: nothing matches after the caret, so the first match (wrapping)
            ViewHost.Pump();
            Assert.Equal("X fox\n", doc.GetText());
        }
        finally
        {
            FindSearch.EditDelay = previous;
        }
    }

    [AvaloniaFact]
    public async Task A_pattern_that_ran_into_the_time_limit_is_not_run_again_after_edits()
    {
        var previous = FindSearch.MatchTimeout;
        FindSearch.MatchTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            using var host = ViewHost.Create();
            var (_, view, _) = await host.OpenAsync("abc\n" + new string('x', 40) + "\nxy\n", "redos.txt");
            var find = OpenFind(view, "(x+x+)+y", regex: true);
            Assert.True(find.LastTimedOut);
            view.Controller.Focus();
            view.Controller.SetSelection(TextRange.At(0, 0));
            for (var i = 0; i < 3; i++)
            {
                Type(view, "q");
                ViewHost.Pump();
                await Task.Delay(FindSearch.EditDelay * 3); // a pause after each keystroke
                ViewHost.Pump();
            }
            Assert.Equal(0, find.WorkerSlices); // the worker never ran it (each run held it for the whole timeout)
            Assert.True(find.LastTimedOut);

            // a new pattern searches again, and so do the edits after it
            view.Editor.SearchPanel.SearchPattern = "abc";
            ViewHost.Pump();
            Assert.False(find.LastTimedOut);
            view.Controller.Focus();
            Type(view, "q");
            await ViewHost.WaitFor(() => find.WorkerSearches == 1, "the re-search of the new pattern");
        }
        finally
        {
            FindSearch.MatchTimeout = previous;
        }
    }

    /// <summary>
    /// The re-search after an edit is a series of short worker jobs over chunks of the snapshot (never one job over the whole document, never a
    /// copy of the whole document), and a newer edit stops the one in progress.
    /// </summary>
    [AvaloniaFact]
    public async Task The_re_search_after_an_edit_runs_in_slices_and_a_newer_edit_stops_it()
    {
        var previous = TokenizationSession.SliceBudget;
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        FindSearch? target = null;
        try
        {
            using var host = ViewHost.Create();
            // ~3 MB, "needle" on every 5,000th line: a scan of the whole text for a few matches
            var text = string.Concat(Enumerable.Range(0, 50_000).Select(i => i % 5_000 == 0 ? $"{i:D6} a needle in the haystack of lines here\n" : $"{i:D6} the quick brown fox jumps over the lazy dog, again and again\n"));
            var (doc, view, _) = await host.OpenAsync(text, "hay.txt");
            var find = target = OpenFind(view, "needle");
            Assert.Equal(10, find.ResultCount);
            TokenizationSession.SliceBudget = TimeSpan.FromTicks(1); // every slice "runs out" after its first chunk

            view.Controller.Focus();
            view.Controller.SetSelection(TextRange.At(1, 0));
            Type(view, "needle ");
            await ViewHost.WaitFor(() => find.WorkerSearches == 1, "the re-search after the pause");
            await ViewHost.WaitFor(() => find.ResultCount == 11, "its results applied");
            Assert.True(find.WorkerSlices >= text.Length / FindSearch.ChunkChars, $"{find.WorkerSlices} slices for {text.Length / FindSearch.ChunkChars} chunks");
            Assert.Equal(1, find.UiThreadSearches);

            // the next re-search is held at its second slice; an edit meanwhile stops it, and the one after that edit lands
            FindSearch.ResearchSliceHookForTests = (f, slice) =>
            {
                if (!ReferenceEquals(f, target) || slice != 2 || reached.IsSet) return;
                reached.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            };
            Type(view, "needle ");
            await ViewHost.WaitFor(() => reached.IsSet, "the re-search's second slice");
            Type(view, "needle ");
            release.Set();
            await ViewHost.WaitFor(() => find.WorkerSearchesStopped == 1, "the stale re-search stopped");
            await ViewHost.WaitFor(() => find.WorkerSearches == 2 && find.ResultCount == 13, $"the newest re-search applied ({find.ResultCount})");
            Assert.Equal(1, find.UiThreadSearches);
            Assert.Equal(13, Count(doc.GetText(), "needle"));
        }
        finally
        {
            FindSearch.ResearchSliceHookForTests = null;
            release.Set();
            TokenizationSession.SliceBudget = previous;
        }
    }
}
