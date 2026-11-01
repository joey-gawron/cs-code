using System.Diagnostics;
using Avalonia.Headless.XUnit;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.TreeSitter;
using CsCode.Editor.View;
using Xunit;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterTestSupport;
using static CsCode.Editor.Tests.TreeSitter.TreeSitterViewTests;

namespace CsCode.Editor.Tests.TreeSitter;

/// <summary>
/// Sticky scroll (<c>editor.stickyScroll.enabled</c>, off by default): the first lines of the regions around the first visible line stay pinned
/// above the text — by syntax for a tree-sitter document (its folds), by indentation otherwise — and clicking one goes there.
/// </summary>
public class StickyScrollTests
{
    internal static string Methods(int count, int bodyLines) =>
        "namespace Demo\n{\n    public class Big\n    {\n" +
        string.Concat(Enumerable.Range(0, count).Select(m =>
            $"        public void Method{m}()\n        {{\n" + string.Concat(Enumerable.Range(0, bodyLines).Select(i => $"            var x{i} = {i};\n")) + "        }\n")) +
        "    }\n}\n";

    internal static async Task ScrollToLineAsync(CodeEditorControl view, int line)
    {
        // Half a pixel into the line (TextEditor.ScrollToVerticalOffset ignores absolute positions): with a fractional line height (Fira Code at
        // 14 is 17.2307…) line * DefaultLineHeight, or the line's summed top, can land a rounding step above the line, which then counts the
        // line before it as the first visible one.
        ((Avalonia.Controls.Primitives.IScrollable)view.TextArea).Offset = new Avalonia.Vector(0, line * view.TextView.DefaultLineHeight + 0.5);
        ViewHost.Pump();
        await ViewHost.WaitFor(() => view.TextView.VisualLinesValid && view.TextView.GetDocumentLineByVisualTop(view.TextView.ScrollOffset.Y)?.LineNumber - 1 == line, $"scrolled to {line}");
        ViewHost.Pump();
    }

    [AvaloniaFact]
    public async Task The_headers_of_the_regions_around_the_first_visible_line_stay_pinned()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.stickyScroll.enabled", true);
        var text = Methods(3, 40);
        var (_, view, _) = await host.OpenAsync(text, "Big.cs", 900, 400);
        var sticky = view.Feature<StickyScrollFeature>()!;
        await ViewHost.WaitFor(() => view.Feature<FoldingFeature>()!.RangeCount >= 5, "folds");
        Assert.Empty(sticky.PinnedLines); // at the top nothing is pinned

        // Method1 starts at line 4 + 43 = 47; scroll into its body
        var method1 = text.Split('\n').ToList().FindIndex(l => l.Contains("Method1", StringComparison.Ordinal));
        await ScrollToLineAsync(view, method1 + 10);
        await ViewHost.WaitFor(() => sticky.PinnedLines.Count >= 3, $"pinned: {string.Join(" | ", sticky.PinnedTexts)}");
        var pinned = sticky.PinnedTexts.Select(t => t.Trim()).ToArray();
        Assert.Equal(["namespace Demo", "public class Big", "public void Method1()"], pinned.Take(3));

        // clicking a pinned line goes there
        sticky.GoTo(sticky.PinnedLines[^1]);
        ViewHost.Pump();
        Assert.Equal(method1, view.Controller.Caret.Line);

        // turned off: nothing pinned
        host.Config.Set("editor.stickyScroll.enabled", false);
        await ViewHost.WaitFor(() => sticky.PinnedLines.Count == 0, "unpinned when off");
    }

    /// <summary>
    /// editor-11: a pinned header sits above the first visible line, so it is changed from elsewhere (another split view, Replace All, a rename,
    /// undo, a macro) while the set of pinned line numbers stays the same. The row used to keep the old text until a scroll changed that set.
    /// </summary>
    [AvaloniaFact]
    public async Task A_pinned_line_changed_from_elsewhere_shows_its_new_text()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.stickyScroll.enabled", true);
        var text = Methods(3, 40);
        var (doc, view, _) = await host.OpenAsync(text, "Big.cs", 900, 400);
        var sticky = view.Feature<StickyScrollFeature>()!;
        await ViewHost.WaitFor(() => view.Feature<FoldingFeature>()!.RangeCount >= 5, "folds");
        var method1 = text.Split('\n').ToList().FindIndex(l => l.Contains("Method1", StringComparison.Ordinal));
        await ScrollToLineAsync(view, method1 + 10);
        await ViewHost.WaitFor(() => sticky.PinnedTexts.Any(t => t.Contains("Method1()", StringComparison.Ordinal)), $"pinned: {string.Join(" | ", sticky.PinnedTexts)}");
        var pinned = sticky.PinnedLines.ToArray();

        // the rename lands through the document (as from another view), nowhere near this view's caret
        var column = doc.GetLineText(method1).IndexOf("Method1", StringComparison.Ordinal);
        Assert.True(doc.ApplyEdits([new CsCode.Core.Text.TextEdit(new CsCode.Core.Text.TextRange(new CsCode.Core.Text.TextPosition(method1, column),
            new CsCode.Core.Text.TextPosition(method1, column + "Method1".Length)), "Renamed1")]));
        await ViewHost.WaitFor(() => sticky.PinnedTexts.Any(t => t.Contains("Renamed1()", StringComparison.Ordinal)), $"pinned: {string.Join(" | ", sticky.PinnedTexts)}");
        Assert.Equal(pinned, sticky.PinnedLines);
        Assert.DoesNotContain(sticky.PinnedTexts, t => t.Contains("Method1()", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Off_by_default_and_cheap_on_the_UI_thread_when_on()
    {
        using var host = ViewHost.Create();
        var text = Methods(40, 30);
        var (_, view, _) = await host.OpenAsync(text, "Big.cs", 900, 400);
        var sticky = view.Feature<StickyScrollFeature>()!;
        await ViewHost.WaitFor(() => view.Feature<FoldingFeature>()!.RangeCount >= 40, "folds");
        await ScrollToLineAsync(view, 500);
        Assert.Empty(sticky.PinnedLines);

        host.Config.Set("editor.stickyScroll.enabled", true);
        ViewHost.Pump();
        var times = new List<double>();
        for (var i = 0; i < 60; i++)
        {
            var sw = Stopwatch.StartNew();
            ((Avalonia.Controls.Primitives.IScrollable)view.TextArea).Offset = new Avalonia.Vector(0, (300 + i * 7) * view.TextView.DefaultLineHeight); // scroll events run the feature inline
            times.Add(sw.Elapsed.TotalMilliseconds);
            ViewHost.Pump();
        }
        Assert.NotEmpty(sticky.PinnedLines);
        times.Sort();
        Assert.True(times[^1] < 16, $"slowest scroll step {times[^1]:F2} ms (median {times[times.Count / 2]:F2})");
    }

    [AvaloniaFact]
    public async Task On_a_tree_sitter_document_the_pinned_lines_come_from_the_syntax_tree()
    {
        RequireGrammar("toml");
        using var host = ViewHost.Create();
        host.Config.Set("editor.stickyScroll.enabled", true);
        // TOML has no indentation: any pinned line here can only come from the tree's folds
        var text = "[package]\nname = \"demo\"\n\n[dependencies]\n" + string.Concat(Enumerable.Range(0, 80).Select(i => $"dep{i} = \"{i}\"\n"));
        var (_, view, _) = await host.OpenAsync(text, "Cargo.toml", 900, 400);
        await WaitForTreeSitter(view, 1);
        var sticky = view.Feature<StickyScrollFeature>()!;
        await ViewHost.WaitFor(() => view.Feature<FoldingFeature>()!.RangeCount >= 2, "tree folds");
        await ScrollToLineAsync(view, 40);
        await ViewHost.WaitFor(() => sticky.PinnedLines.Count == 1, $"pinned: {string.Join(" | ", sticky.PinnedTexts)}");
        Assert.Equal("[dependencies]", sticky.PinnedTexts.Single());
    }
}
