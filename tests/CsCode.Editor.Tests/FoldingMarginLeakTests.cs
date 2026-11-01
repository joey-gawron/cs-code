using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AvaloniaEdit.Folding;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// T36 (a real leak found by the memory soak, docs/MEMORY.md §12 item 8): AvaloniaEdit's folding margin makes a marker control for every visible
/// fold each time the visual lines change and takes the old ones out of the visual tree, but leaves itself their logical (and so property
/// inheritance) parent, which keeps every marker it ever made, and with them their visual lines, text lines and glyph runs: scrolling a big file
/// grew the heap by megabytes per pass until the tab closed. The editor's margin detaches them.
/// </summary>
public class FoldingMarginLeakTests
{
    private static string Blocks(int lines) =>
        string.Join("\n", Enumerable.Range(0, lines).Select(i => i % 4 == 0 ? $"block {i}" : $"    line {i} of the block above"));

    [AvaloniaFact]
    public async Task Markers_of_lines_scrolled_away_are_not_kept_by_the_margin()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Blocks(4000), "a.txt");
        var folding = view.Feature<FoldingFeature>()!;
        await ViewHost.WaitFor(() => folding.RangeCount >= 999, "indentation folds");
        var margin = view.TextArea.LeftMargins.OfType<FoldingMargin>().Single();

        var weak = new List<WeakReference>();
        void Scroll(int line)
        {
            view.Editor.ScrollToLine(line);
            ViewHost.Pump();
            foreach (var marker in margin.GetVisualChildren()) weak.Add(new WeakReference(marker));
        }
        for (var line = 1; line < 4000; line += 97) Scroll(line);
        Assert.True(weak.Count > 200, $"markers seen: {weak.Count}");
        view.Editor.ScrollToLine(1);
        ViewHost.Pump();
        var current = margin.GetVisualChildren().ToHashSet();
        Assert.NotEmpty(current);
        // the markers on screen now have the margin as their parent in both trees; none of the earlier ones keeps it
        foreach (var marker in current) Assert.Same(margin, ((ILogical)marker).LogicalParent);

        var alive = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            ViewHost.Pump();
            alive = weak.Count(w => w.Target is Visual v && !current.Contains(v));
            if (alive == 0) break;
        }
        Assert.True(alive == 0, $"{alive} of {weak.Count} markers scrolled away are still alive");
    }

    [AvaloniaFact]
    public async Task The_editors_margin_replaces_AvaloniaEdits_takes_the_theme_and_leaves_with_the_view()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Blocks(40), "a.txt");
        var folding = view.Feature<FoldingFeature>()!;
        await ViewHost.WaitFor(() => folding.RangeCount >= 10, "indentation folds");
        var margin = Assert.Single(view.TextArea.LeftMargins.OfType<FoldingMargin>()); // one margin, not two
        Assert.IsType<FoldingFeature.DetachingFoldingMargin>(margin);
        Assert.Same(folding.Margin, margin);
        Assert.Equal(view.Palette.FoldingControl, margin.FoldingMarkerBrush);
        Assert.NotEmpty(margin.GetVisualChildren()); // markers for the visible folds
        folding.FoldAll(); // the visual lines change: the markers are rebuilt, the new ones attached in both trees
        ViewHost.Pump();
        var markers = margin.GetVisualChildren().ToList();
        Assert.NotEmpty(markers);
        Assert.All(markers, m => Assert.Same(margin, ((ILogical)m).LogicalParent));

        var area = view.TextArea;
        host.Factory.DisposeView(view);
        Assert.DoesNotContain(area.LeftMargins, m => m is FoldingMargin);
        Assert.Null(margin.FoldingManager);
    }

    [AvaloniaFact]
    public async Task Typing_keeps_the_markers_unless_the_folds_on_screen_change()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Blocks(400), "a.txt");
        var folding = view.Feature<FoldingFeature>()!;
        await ViewHost.WaitFor(() => folding.RangeCount >= 99, "indentation folds");
        var margin = (FoldingFeature.DetachingFoldingMargin)folding.Margin;
        view.Controller.SetSelection(TextRange.At(41, 8)); // inside a block, on a line that starts no fold
        ViewHost.Pump();
        var markers = margin.GetVisualChildren().ToList();
        Assert.True(markers.Count > 5, $"markers on screen: {markers.Count}");
        var keptBefore = margin.Kept;
        for (var i = 0; i < 10; i++)
        {
            view.TextArea.PerformTextInput("x");
            ViewHost.Pump();
        }
        Assert.True(margin.Kept - keptBefore >= 10, $"kept {margin.Kept - keptBefore} times in 10 keystrokes");
        Assert.Equal(markers, margin.GetVisualChildren().ToList()); // the same marker controls, not new ones

        // a keystroke on a line that starts a fold rebuilds them (that line's visual line is new)
        view.Controller.SetSelection(TextRange.At(40, 5));
        view.TextArea.PerformTextInput("y");
        ViewHost.Pump();
        Assert.NotEqual(markers, margin.GetVisualChildren().ToList());
        Assert.All(margin.GetVisualChildren(), m => Assert.Same(margin, ((ILogical)m).LogicalParent));

        // folding a block on screen changes the markers too: one fewer line, and the folded one shows collapsed
        markers = margin.GetVisualChildren().ToList();
        view.Controller.SetSelection(TextRange.At(44, 0));
        folding.Fold();
        ViewHost.Pump();
        Assert.NotEqual(markers, margin.GetVisualChildren().ToList());
        Assert.Contains(44, folding.FoldedStartLines);

        // Enter below the folds moves lines down: the kept markers follow their lines (arranged again), and the next layout still matches
        view.Controller.SetSelection(TextRange.At(42, 4));
        ViewHost.Pump();
        var tops = margin.GetVisualChildren().ToDictionary(m => m, m => m.Bounds.Y);
        view.TextArea.PerformTextInput("\n");
        ViewHost.Pump();
        var lineHeight = view.TextView.VisualLines[0].Height;
        var kept = margin.GetVisualChildren().Where(tops.ContainsKey).ToList();
        var moved = kept.Count(m => Math.Abs(m.Bounds.Y - tops[m] - lineHeight) < 1.5);
        Assert.True(moved >= 1, $"{moved} kept markers moved down one line with their lines");
        Assert.All(kept, m => Assert.True(Math.Abs(m.Bounds.Y - tops[m]) < 1.5 || Math.Abs(m.Bounds.Y - tops[m] - lineHeight) < 1.5, $"a marker above the new line stays, one below moves one line: {tops[m]} -> {m.Bounds.Y}"));
        foreach (var marker in margin.GetVisualChildren())
            Assert.True(marker.Bounds.Height > 0 && ((Avalonia.Layout.Layoutable)marker).IsArrangeValid, "every marker is arranged");
    }
}
