using System.Diagnostics;
using Avalonia.Headless.XUnit;
using CsCode.Core.Text;
using CsCode.Editor.Tests.Support;
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
/// UI thread: the matches move with the edit, and the re-search runs on the editor worker once the typing pauses.
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
}
