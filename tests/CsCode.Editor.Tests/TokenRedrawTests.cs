using Avalonia.Headless.XUnit;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using CsCode.Core.Text;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.Tests.TreeSitter;
using CsCode.Editor.View;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>
/// When the tokeniser publishes new rows, the colorizer redraws the visual lines of those rows. It used <c>TextView.Redraw(offset, length)</c>,
/// which also throws away the line before the range, so every keystroke rebuilt the line above the caret too. These tests count the lines
/// built per relative position and check that the edited line still takes its new colours.
/// </summary>
public class TokenRedrawTests
{
    private sealed class RelativeCounter(CodeEditorControl view) : IVisualLineTransformer
    {
        public readonly Dictionary<int, int> Built = new();
        public void Transform(ITextRunConstructionContext context, IList<VisualLineElement> elements)
        {
            var relative = context.VisualLine.FirstDocumentLine.LineNumber - view.TextArea.Caret.Line;
            Built[relative] = Built.GetValueOrDefault(relative) + 1;
        }
    }

    private static string Flat(int lines) =>
        "// a comment on the first line\n" + string.Join("\n", Enumerable.Range(1, lines).Select(i => $"var value{i} = Compute({i}, \"text\");"));

    private static IBrush? ForegroundAt(CodeEditorControl view, int line, int column)
    {
        var visual = view.TextView.GetVisualLine(line + 1);
        var element = visual?.Elements.FirstOrDefault(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + e.DocumentLength);
        return element?.TextRunProperties.ForegroundBrush;
    }

    private static Color? ColorOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    [AvaloniaFact]
    public async Task New_rows_redraw_the_edited_line_and_not_the_one_above_it()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync(Flat(300), "flat.cs");
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.HasTokens(0, 120), "tokens for the top of the file");
        view.Controller.SetSelection(TextRange.At(100, 4));
        ViewHost.Pump();
        var counter = new RelativeCounter(view);
        view.TextView.LineTransformers.Add(counter);
        const int keys = 12;
        for (var i = 0; i < keys; i++)
        {
            var version = doc.Version;
            view.TextArea.PerformTextInput("q");
            // the rows of this keystroke are published and drawn
            await ViewHost.WaitFor(() => view.Tokens!.Store.GetTokens(100) is not null && doc.Version == version + 1 && TokensCover(view, 100, doc.GetLineText(100).Length), "the edited line's rows");
            ViewHost.Pump();
        }
        var above = counter.Built.GetValueOrDefault(-1);
        var edited = counter.Built.GetValueOrDefault(0);
        Assert.True(edited >= keys, $"the edited line is built at least once per keystroke ({edited} for {keys})");
        Assert.True(above <= 1, $"the line above the caret was built {above} times in {keys} keystrokes: the rows' redraw threw it away");
    }

    private static bool TokensCover(CodeEditorControl view, int line, int length)
    {
        var row = view.Tokens!.Store.GetTokens(line);
        return row is { Length: >= 2 } && row[^2] < length; // the last token starts inside the (longer) edited line
    }

    [AvaloniaFact]
    public async Task A_new_line_built_by_the_caret_before_the_next_layout_is_redrawn_when_its_rows_arrive()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync(Flat(80), "flat.cs");
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.HasTokens(0, 60), "tokens");
        view.Controller.SetSelection(TextRange.At(30, doc.GetLineText(30).Length));
        ViewHost.Pump();
        view.TextArea.PerformTextInput("\n");
        view.TextArea.PerformTextInput("// new comment");
        var line = view.TextArea.Caret.Line;
        var built = view.TextView.GetVisualLine(line); // the caret built it, before any layout and before its rows exist
        Assert.NotNull(built);
        // run the posted jobs (the rows' redraw among them) but no render tick, so no layout rebuilds the line meanwhile
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!(view.Tokens!.Store.GetTokens(line - 1) is { } row && TreeSitterViewTests.ScopesAt(view, line - 1, 5).Any(s => s.Contains("comment", StringComparison.Ordinal))) && sw.ElapsedMilliseconds < 10_000)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(built.IsDisposed, "the visual line built before the rows arrived must be redrawn when they do");
    }

    [AvaloniaFact]
    public async Task A_line_turned_into_a_comment_is_drawn_in_the_comment_colour()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Flat(80), "flat.cs");
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.HasTokens(0, 60), "tokens");
        ViewHost.Pump();
        await ViewHost.WaitFor(() => ColorOf(ForegroundAt(view, 0, 5)) is not null, "the first line drawn with its tokens");
        var comment = ColorOf(ForegroundAt(view, 0, 5));
        var before = ColorOf(ForegroundAt(view, 20, 6));
        Assert.NotEqual(comment, before);
        view.Controller.SetSelection(TextRange.At(20, 0));
        view.TextArea.PerformTextInput("/");
        view.TextArea.PerformTextInput("/");
        await ViewHost.WaitFor(() =>
        {
            ViewHost.Pump();
            return ColorOf(ForegroundAt(view, 20, 8)) == comment;
        }, "the edited line redrawn in the comment colour once its rows arrive");
        Assert.Contains("comment", string.Join(" ", TreeSitterViewTests.ScopesAt(view, 20, 8)), StringComparison.Ordinal);
    }
}
