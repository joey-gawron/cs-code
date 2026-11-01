using Avalonia.Headless.XUnit;
using AvaloniaEdit.Rendering;
using CsCode.Core.Text;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// AvaloniaEdit redraws a fold's whole range whenever an edit changes its length, folded or not, so typing anywhere inside an expanded region
/// (a namespace, a class: nearly everywhere in code) rebuilt every visible line on each keystroke. <see cref="FoldRedrawFilter"/> suppresses
/// that redraw unless a folded region is at the edit; these tests count the visual lines built per keystroke and check that folding, a folded
/// region edited while collapsed, and a multi-change update still behave.
/// </summary>
public class FoldRedrawFilterTests
{
    /// <summary>A line transformer that only counts: it runs once for every visual line built.</summary>
    internal sealed class BuildCounter : IVisualLineTransformer
    {
        public int Built;
        public void Transform(ITextRunConstructionContext context, IList<VisualLineElement> elements) => Built++;
    }

    // one indentation fold around everything below the first line, as a namespace or class is in code
    private static string Region(int lines) =>
        "region\n" + string.Join("\n", Enumerable.Range(1, lines).Select(i => $"    line {i} inside the region"));

    private static async Task<(CsCode.Editor.Documents.EditorDocument Doc, CodeEditorControl View, FoldingFeature Folding)> OpenRegion(ViewHost host, int lines)
    {
        var (doc, view, _) = await host.OpenAsync(Region(lines), "region.txt");
        var folding = view.Feature<FoldingFeature>()!;
        await ViewHost.WaitFor(() => folding.RangeCount >= 1, "the indentation fold");
        return (doc, view, folding);
    }

    [AvaloniaFact]
    public async Task Typing_inside_an_expanded_region_rebuilds_only_the_edited_line()
    {
        using var host = ViewHost.Create();
        var (_, view, folding) = await OpenRegion(host, 400);
        Assert.True(folding.RedrawFilter.IsActive, "the AvaloniaEdit layout the filter reaches into has changed");
        view.Controller.SetSelection(TextRange.At(200, 8));
        ViewHost.Pump();
        var counter = new BuildCounter();
        view.TextView.LineTransformers.Add(counter);
        var suppressedBefore = folding.RedrawFilter.Suppressed;
        const int keys = 30;
        for (var i = 0; i < keys; i++)
        {
            view.TextArea.PerformTextInput("x");
            ViewHost.Pump();
        }
        var perKey = counter.Built / (double)keys;
        var visible = view.TextView.VisualLines.Count;
        Assert.True(visible > 20, $"the test needs a full viewport, {visible} lines visible");
        Assert.True(perKey < 3, $"{perKey:F1} visual lines built per keystroke inside an expanded region ({visible} visible): the fold's redraw rebuilt the viewport");
        Assert.Equal(keys, folding.RedrawFilter.Suppressed - suppressedBefore);
        Assert.Equal(1, folding.RangeCount); // the region itself is untouched
    }

    [AvaloniaFact]
    public async Task A_region_typed_in_still_folds_and_unfolds()
    {
        using var host = ViewHost.Create();
        var (doc, view, folding) = await OpenRegion(host, 60);
        view.Controller.SetSelection(TextRange.At(10, 8));
        for (var i = 0; i < 5; i++) view.TextArea.PerformTextInput("y");
        ViewHost.Pump();
        view.Controller.SetSelection(TextRange.At(0, 0));
        folding.Fold();
        ViewHost.Pump();
        Assert.Equal([0], folding.FoldedStartLines);
        var header = view.TextView.GetVisualLine(1);
        Assert.NotNull(header);
        Assert.Equal(doc.LineCount, header.LastDocumentLine.LineNumber); // one visual line for the header and everything it folds
        Assert.Contains(view.TextView.VisualLines, l => ReferenceEquals(l, header));
        folding.Unfold();
        ViewHost.Pump();
        Assert.Empty(folding.FoldedStartLines);
        Assert.Equal(1, view.TextView.GetVisualLine(1)!.LastDocumentLine.LineNumber);
    }

    [AvaloniaFact]
    public async Task An_edit_inside_a_folded_region_keeps_it_folded_and_redrawn()
    {
        using var host = ViewHost.Create();
        var text = "first\n    a\n    b\nsecond\n    c\n    d\n" + string.Join("\n", Enumerable.Range(0, 40).Select(i => $"tail {i}"));
        var (doc, view, _) = await host.OpenAsync(text, "two.txt");
        var folding = view.Feature<FoldingFeature>()!;
        await ViewHost.WaitFor(() => folding.RangeCount >= 2, "two indentation folds");
        view.Controller.SetSelection(TextRange.At(3, 0));
        folding.Fold(); // 'second' is folded; 'first' stays expanded
        view.Controller.SetSelection(TextRange.At(1, 4)); // the caret in the expanded region
        ViewHost.Pump();
        Assert.Equal([3], folding.FoldedStartLines);
        var suppressed = folding.RedrawFilter.Suppressed;

        // an edit inside the folded region (as Replace All or another view would make it): AvaloniaEdit's redraw must happen
        doc.Buffer.Insert(doc.OffsetAt(new TextPosition(4, 4)), "changed ");
        ViewHost.Pump();
        Assert.Equal(suppressed, folding.RedrawFilter.Suppressed);
        Assert.Equal([3], folding.FoldedStartLines);
        var folded = view.TextView.GetVisualLine(4)!;
        Assert.Equal(6, folded.LastDocumentLine.LineNumber);

        // one update with an edit in the expanded region and one in the folded region: each decided on its own, nothing left hidden
        doc.Buffer.BeginUpdate();
        doc.Buffer.Insert(doc.OffsetAt(new TextPosition(1, 4)), "e");
        doc.Buffer.Insert(doc.OffsetAt(new TextPosition(5, 4)), "f");
        doc.Buffer.EndUpdate();
        ViewHost.Pump();
        Assert.Equal(suppressed + 1, folding.RedrawFilter.Suppressed);
        Assert.Equal(6, view.TextView.GetVisualLine(4)!.LastDocumentLine.LineNumber);
        Assert.Equal("    ea", doc.GetLineText(1));
        // the views are back: folding another region still collapses its lines
        view.Controller.SetSelection(TextRange.At(0, 0));
        folding.Fold();
        ViewHost.Pump();
        Assert.Equal(3, view.TextView.GetVisualLine(1)!.LastDocumentLine.LineNumber);
    }

    [AvaloniaFact]
    public async Task Deleting_the_whole_of_an_expanded_region_removes_it_without_trouble()
    {
        using var host = ViewHost.Create();
        var (doc, view, folding) = await OpenRegion(host, 30);
        var buffer = doc.Buffer;
        var start = buffer.GetLineByNumber(1).EndOffset;
        buffer.Remove(start, buffer.TextLength - start); // the region's body goes: the fold shrinks to nothing and is removed
        ViewHost.Pump();
        Assert.Equal(1, doc.LineCount);
        Assert.Equal(0, folding.RangeCount);
        Assert.NotNull(view.TextView.GetVisualLine(1));
    }
}
