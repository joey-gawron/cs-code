using Avalonia.Headless.XUnit;
using CsCode.Core.Text;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.TextMate;
using CsCode.Editor.TreeSitter;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// T10, the TextMate livelock fixed in 10c2a72: after an edit inside the viewport, a slice restarted from the viewport's top every time, so
/// when re-tokenising from there to the edit took longer than one slice (a loaded machine, a slow grammar) the viewport never caught up.
/// The fix resumes at the first viewport line that still needs work and always gets one line past it. Reproduced deterministically with a
/// tiny slice budget (every slice "runs out" after its first line); serial, because the budget is process-wide.
/// </summary>
[Collection(TreeSitterBenchCollection.Name)]
public class TextMateLivelockTests
{
    [AvaloniaFact]
    public async Task After_an_edit_inside_the_viewport_tokenisation_catches_up_even_when_every_slice_runs_out_at_once()
    {
        var previous = TokenizationSession.SliceBudget;
        try
        {
            using var host = ViewHost.Create();
            host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TextMate);
            var text = string.Concat(Enumerable.Range(0, 400).Select(i => $"int Value{i} = {i}; // line {i}\n"));
            var (_, view, _) = await host.OpenAsync(text, "Lines.cs", 900, 400);
            view.Controller.Reveal(TextRange.At(150, 0), center: true);
            ViewHost.Pump();
            var (first, last) = view.Tokens!.Viewport;
            Assert.InRange(150, first, last);
            await ViewHost.WaitFor(() => view.Tokens.Store.ValidLines > last, "the viewport tokenised");

            TokenizationSession.SliceBudget = TimeSpan.FromTicks(1); // a machine so loaded that no slice gets past its first line in time
            view.Controller.SetSelection(TextRange.At(150, 0));
            Type(view, "/* x */ ");
            await ViewHost.WaitFor(() => view.Tokens.Store.ValidLines > last, $"the viewport re-tokenised after the edit (valid {view.Tokens.Store.ValidLines}, viewport {first}-{last})");
            Assert.Contains(view.Tokens.Interner.Scopes(view.Tokens.Store.GetTokens(150)![1]), s => s.StartsWith("comment", StringComparison.Ordinal));
        }
        finally
        {
            TokenizationSession.SliceBudget = previous;
        }
    }

    private static string BigCSharp(int lines) =>
        string.Concat(Enumerable.Range(0, lines).Select(i => $"int Value{i} = {i}; // line {i} of a big file with \"strings\" and comments\n"));

    /// <summary>Opens a big TextMate file and waits until the read-ahead below the top viewport has stopped (the valid chain ends near line 3,000).</summary>
    private static async Task<CsCode.Editor.View.CodeEditorControl> OpenBigAsync(ViewHost host, int lines)
    {
        host.Config.Set(TreeSitterService.SettingKey, TreeSitterService.TextMate);
        var (_, view, _) = await host.OpenAsync(BigCSharp(lines), "Big.cs", 900, 400);
        await ViewHost.WaitFor(() => view.Tokens!.Store.ValidLines > TokenizationSession.ReadAheadLines, "the read-ahead below the first viewport");
        long jobs;
        do
        {
            jobs = host.Factory.Tokens.Worker.CompletedJobs;
            for (var i = 0; i < 10; i++)
            {
                ViewHost.Pump();
                await Task.Delay(20);
            }
        }
        while (host.Factory.Tokens.Worker.CompletedJobs != jobs);
        return view;
    }


    /// <summary>
    /// editor-1: a viewport far below the valid chain is coloured as an "island" (a fresh state, <see cref="TokenizationSession.MaxCatchUpLines"/>
    /// past the chain). Each island publish redraws, the redraw reports the viewport, and the report used to schedule the island again: the
    /// worker re-tokenised the same lines at the frame rate while the user did nothing. Once coloured, an island must cost nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task A_coloured_island_far_below_the_chain_costs_nothing_while_idle()
    {
        using var host = ViewHost.Create();
        var view = await OpenBigAsync(host, 12_000);
        var tokens = view.Tokens!;
        view.Controller.Reveal(TextRange.At(11_000, 0), center: true);
        ViewHost.Pump();
        var (first, last) = tokens.Viewport;
        Assert.InRange(11_000, first, last);
        Assert.True(first - tokens.Store.ValidLines > TokenizationSession.MaxCatchUpLines, "the viewport is an island");
        await ViewHost.WaitFor(() => tokens.Store.HasTokens(first, last), "the island coloured");

        var jobs = host.Factory.Tokens.Worker.CompletedJobs;
        var islands = tokens.IslandSlices;
        for (var i = 0; i < 40; i++) // ~1 s of an idle editor: layout passes and render ticks, no input
        {
            ViewHost.Pump();
            await Task.Delay(25);
        }
        Assert.Equal(islands, tokens.IslandSlices);
        Assert.True(host.Factory.Tokens.Worker.CompletedJobs - jobs <= 2, $"{host.Factory.Tokens.Worker.CompletedJobs - jobs} worker jobs while idle");
        Assert.Contains(tokens.Interner.Scopes(tokens.Store.GetTokens(11_000)![^1]), s => s.StartsWith("comment", StringComparison.Ordinal));
    }

    /// <summary>
    /// editor-1, the loaded-machine case: an island that one slice cannot finish used to restart at its top (a fresh state, 200 lines above the
    /// viewport) every slice, so the viewport was never reached. It resumes where it stopped now, with the rule state it reached.
    /// </summary>
    [AvaloniaFact]
    public async Task An_island_that_needs_many_slices_still_reaches_the_viewport()
    {
        var previous = TokenizationSession.SliceBudget;
        try
        {
            using var host = ViewHost.Create();
            var view = await OpenBigAsync(host, 12_000);
            var tokens = view.Tokens!;
            TokenizationSession.SliceBudget = TimeSpan.FromTicks(1); // every slice "runs out" after its first line
            view.Controller.Reveal(TextRange.At(11_000, 0), center: true);
            ViewHost.Pump();
            var (first, last) = tokens.Viewport;
            await ViewHost.WaitFor(() => tokens.Store.HasTokens(first, last), $"the island reaches the viewport {first}-{last}", 30_000);
            Assert.True(tokens.IslandSlices > 50, $"{tokens.IslandSlices} island slices"); // it did take many slices
        }
        finally
        {
            TokenizationSession.SliceBudget = previous;
        }
    }

    /// <summary>
    /// editor-7: an Aggressive memory trim empties every row, the viewport's too. The tree-sitter engine re-highlighted the viewport only after a
    /// new tree or a scroll, so the visible lines stayed plain; and neither engine came back until something happened to redraw them.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("a.toml")] // tree-sitter under auto
    [InlineData("a.cs")]   // TextMate
    public async Task After_an_aggressive_trim_the_viewport_is_coloured_again_without_any_input(string file)
    {
        if (file.EndsWith(".toml", StringComparison.Ordinal)) TreeSitterTestSupport.RequireGrammar("toml");
        using var host = ViewHost.Create();
        var text = file.EndsWith(".toml", StringComparison.Ordinal)
            ? string.Concat(Enumerable.Range(0, 400).Select(i => $"[t{i}]\nkey{i} = \"v{i}\"\n"))
            : BigCSharp(400);
        var (_, view, _) = await host.OpenAsync(text, file, 900, 400);
        var tokens = view.Tokens!;
        await ViewHost.WaitFor(() => tokens.Store.HasTokens(tokens.Viewport.First, tokens.Viewport.Last) && tokens.ViewportReady is not null, "the viewport coloured");
        var (first, last) = tokens.Viewport;

        host.Factory.Tokens.Trim(Core.Diagnostics.MemoryTrimLevel.Aggressive);
        Assert.False(tokens.Store.HasTokens(first, last));
        // no scroll, no edit, no redraw request: the engine brings the rows back on its own
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!tokens.Store.HasTokens(first, last) && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        Assert.True(tokens.Store.HasTokens(first, last), $"viewport {first}-{last} still plain after the trim ({view.SyntaxEngine})");
    }
}
