using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AvaloniaEdit.Folding;
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
}
