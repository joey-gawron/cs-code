using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Lifecycle;
using CsCode.Core.Platform;
using CsCode.Core.Scm;
using CsCode.Core.Text;
using CsCode.Editor.Diff;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using CsCode.Editor.View.Decorations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>The overview ruler on the scrollbar and the minimap, in real views (docs/EDITOR.md §7c).</summary>
public class ScrollDecorationsViewTests(ITestOutputHelper output)
{
    private static string Lines(int n, Func<int, string>? line = null) => string.Join('\n', Enumerable.Range(0, n).Select(line ?? (i => $"int value{i} = {i}; // line {i}")));

    private static ScrollDecorations Deco(CodeEditorControl view) => view.ScrollDecorations!;

    /// <summary>Collects the marks, lays out the ruler and waits for the minimap's render to land.</summary>
    private static async Task Settle(CodeEditorControl view)
    {
        ViewHost.Pump();
        Deco(view).FlushForTests();
        await ViewHost.WaitFor(() => !Deco(view).Minimap.Active || (!Deco(view).Minimap.IsRenderPending && Deco(view).Minimap.RenderedWindowTop >= 0), "minimap rendered");
        ViewHost.Pump();
    }

    /// <summary>The pixel row of the ruler where document line <paramref name="line"/> (0-based) starts.</summary>
    private static int RulerY(CodeEditorControl view, int line)
    {
        var ruler = Deco(view).Ruler;
        var (top, height) = ruler.Track;
        var extent = ((IScrollable)view.TextArea).Extent.Height;
        return (int)Math.Floor(top + view.TextView.GetVisualTopByDocumentLine(line + 1) / extent * height);
    }

    private static MarkerKind RulerAt(CodeEditorControl view, MarkerLane lane, int line) => Deco(view).Ruler.Model.At(lane, RulerY(view, line));

    // ------------------------------------------------------------------------------------------------ placement and settings

    [AvaloniaFact]
    public async Task The_minimap_is_on_by_default_left_of_the_ruler_and_no_text_runs_under_either()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync(Lines(300), "a.cs");
        await Settle(view);
        var d = Deco(view);
        Assert.True(d.Minimap.Control.IsVisible);
        Assert.True(d.Ruler.Control.IsVisible);
        var ruler = d.Ruler.Width;
        Assert.Equal(120, d.Minimap.Control.Bounds.Width); // editor.minimap.maxColumn columns of one pixel
        Assert.Equal(new Thickness(0, 0, 120 + ruler, 0), view.TextArea.Margin);
        var minimap = d.Minimap.Control.TranslatePoint(default, view)!.Value;
        Assert.Equal(view.Bounds.Width - ruler - 120, minimap.X, 1);
        Assert.Equal(view.Bounds.Width - ruler, d.Ruler.Control.TranslatePoint(default, view)!.Value.X, 1);
        Assert.True(view.TextView.Bounds.Right <= minimap.X + 0.5, "the text view ends where the minimap starts");
        // the ruler sits over the vertical scrollbar
        var bar = view.Editor.GetVisualDescendants().OfType<ScrollBar>().First(s => s.Orientation == Avalonia.Layout.Orientation.Vertical);
        Assert.Equal(bar.TranslatePoint(default, view)!.Value.X, d.Ruler.Control.TranslatePoint(default, view)!.Value.X, 1);
        Assert.NotNull(d.Minimap.Bitmap);
    }

    [AvaloniaFact]
    public async Task Minimap_settings_apply_live_enabled_side_and_max_column()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Lines(300), "a.cs");
        await Settle(view);
        var d = Deco(view);
        var ruler = d.Ruler.Width;

        host.Config.Set(SettingKeys.EditorMinimapEnabled, false);
        await Settle(view);
        Assert.False(d.Minimap.Control.IsVisible);
        Assert.Null(d.Minimap.Bitmap); // its memory goes with it
        Assert.Equal(new Thickness(0, 0, ruler, 0), view.TextArea.Margin);

        host.Config.Set(SettingKeys.EditorMinimapEnabled, true);
        await Settle(view);
        Assert.True(d.Minimap.Control.IsVisible);
        Assert.NotNull(d.Minimap.Bitmap);

        host.Config.Set(ScrollDecorations.MinimapSideKey, "left");
        await Settle(view);
        Assert.Equal(new Thickness(120, 0, ruler, 0), view.TextArea.Margin);
        Assert.Equal(0, d.Minimap.Control.TranslatePoint(default, view)!.Value.X, 1);

        host.Config.Set(ScrollDecorations.MinimapMaxColumnKey, 80);
        await Settle(view);
        Assert.Equal(80, d.Minimap.Control.Bounds.Width);

        host.Config.Set(ScrollDecorations.RulerLanesKey, 0);
        await Settle(view);
        Assert.False(d.Ruler.Control.IsVisible);
        Assert.Equal(new Thickness(80, 0, 0, 0), view.TextArea.Margin);
    }

    [AvaloniaFact]
    public async Task A_narrow_editor_gets_a_narrower_minimap_and_a_very_narrow_one_none()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync(Lines(50), "a.cs", width: 300);
        await Settle(view);
        var d = Deco(view);
        Assert.InRange(d.Minimap.Control.Bounds.Width, Minimap.MinWidth, 300 * Minimap.MaxShare);
        window.Width = 70;
        await Settle(view);
        Assert.False(d.Minimap.Control.IsVisible);
        window.Width = 900;
        await Settle(view);
        Assert.Equal(120, d.Minimap.Control.Bounds.Width);
    }

    [AvaloniaFact]
    public async Task Sticky_scroll_rows_stay_over_the_text_and_leave_the_minimap_and_ruler_uncovered()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.stickyScroll.enabled", true);
        var (_, view, _) = await host.OpenAsync(Lines(300), "a.cs");
        await Settle(view);
        var sticky = view.Feature<StickyScrollFeature>()!;
        Assert.Equal(new Thickness(0, 0, view.TextArea.Margin.Right, 0), sticky.Panel.Margin);
        host.Config.Set(ScrollDecorations.MinimapSideKey, "left");
        await Settle(view);
        Assert.Equal(new Thickness(120, 0, Deco(view).Ruler.Width, 0), sticky.Panel.Margin);
    }

    /// <summary>
    /// The pinned rows used to lose the right inset as soon as any were shown (lining them up with the text set the panel's margin to the text
    /// view's origin and 0 on the right), so they ran over the minimap and the ruler and took their clicks; a resize then dropped the origin.
    /// </summary>
    [AvaloniaFact]
    public async Task Pinned_sticky_scroll_rows_end_at_the_minimap_and_keep_lining_up_with_the_text_after_a_resize()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.stickyScroll.enabled", true);
        var text = TreeSitter.StickyScrollTests.Methods(3, 40);
        var (_, view, window) = await host.OpenAsync(text, "Big.cs", 900, 400);
        var sticky = view.Feature<StickyScrollFeature>()!;
        await ViewHost.WaitFor(() => view.Feature<FoldingFeature>()!.RangeCount >= 5, "folds");
        await Settle(view);
        var method1 = text.Split('\n').ToList().FindIndex(l => l.Contains("Method1", StringComparison.Ordinal));
        await TreeSitter.StickyScrollTests.ScrollToLineAsync(view, method1 + 10);
        await ViewHost.WaitFor(() => sticky.PinnedLines.Count >= 3, $"pinned: {string.Join(" | ", sticky.PinnedTexts)}");

        void AssertBetweenTextAndMinimap(string when)
        {
            ViewHost.Pump();
            var panel = sticky.Panel.Bounds;
            var minimapLeft = Deco(view).Minimap.Control.Bounds.Left;
            var textLeft = view.TextView.TranslatePoint(default, view)!.Value.X;
            Assert.True(Deco(view).Minimap.Control.IsVisible, when);
            Assert.True(panel.Width > 0, when);
            Assert.True(panel.Right <= minimapLeft + 0.5, $"{when}: rows end at {panel.Right}, the minimap starts at {minimapLeft}");
            Assert.Equal(textLeft, panel.Left, 1);
        }

        AssertBetweenTextAndMinimap("pinned");
        window.Width = 1100;
        await ViewHost.WaitFor(() => view.Bounds.Width > 1000, "resized");
        AssertBetweenTextAndMinimap("after a resize");
        Assert.True(sticky.Panel.Margin.Left > 0, "the rows still start past the line numbers");
    }

    [AvaloniaFact]
    public async Task Diff_editor_panes_have_no_minimap_but_keep_the_ruler()
    {
        using var host = ViewHost.Create();
        var original = Lines(200);
        var modified = Lines(200, i => i == 50 ? "changed" : $"int value{i} = {i}; // line {i}");
        var diff = (DiffEditorControl)host.Factory.CreateDiffView(new DiffEditorRequest(new DiffSide("a", Text: original), new DiffSide("b", Text: modified), "a.cs", "a.cs"))!;
        var window = new Window { Width = 1000, Height = 500, Content = diff };
        window.Show();
        await ViewHost.WaitFor(() => diff.ModifiedView is not null, "diff panes");
        ViewHost.Pump();
        foreach (var pane in new[] { diff.OriginalView!, diff.ModifiedView! })
        {
            Assert.False(pane.AllowMinimap);
            Assert.False(Deco(pane).Minimap.Control.IsVisible);
            Assert.True(Deco(pane).Ruler.Control.IsVisible);
            Assert.Equal(Deco(pane).Ruler.Width, pane.TextArea.Margin.Right);
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task Toggle_Minimap_flips_the_user_setting()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Lines(10), "a.cs");
        Assert.Contains(host.Commands.All, c => c.Id == "editor.action.toggleMinimap" && c.Title == "View: Toggle Minimap");
        await host.Commands.ExecuteAsync("editor.action.toggleMinimap");
        Assert.False(host.Config.Get(SettingKeys.EditorMinimapEnabled, true));
        await Settle(view);
        Assert.False(Deco(view).Minimap.Control.IsVisible);
        await host.Commands.ExecuteAsync("editor.action.toggleMinimap");
        Assert.True(host.Config.Get(SettingKeys.EditorMinimapEnabled, false));
    }

    [AvaloniaFact]
    public async Task The_settings_are_registered_with_their_defaults_for_the_settings_panel()
    {
        using var host = ViewHost.Create();
        var keys = host.Config.Properties.Select(p => p.Key).ToHashSet();
        foreach (var p in ScrollDecorations.Properties) Assert.Contains(p.Key, keys);
        Assert.Equal("right", ScrollDecorations.Properties.Single(p => p.Key == ScrollDecorations.MinimapSideKey).Default!.Value.GetString());
        Assert.Equal(120, ScrollDecorations.Properties.Single(p => p.Key == ScrollDecorations.MinimapMaxColumnKey).Default!.Value.GetInt32());
        Assert.Equal("mouseover", ScrollDecorations.Properties.Single(p => p.Key == ScrollDecorations.MinimapShowSliderKey).Default!.Value.GetString());
        Assert.True(new EditorSettings().Minimap); // editor.minimap.enabled: true, like VS Code
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------------------------------------ minimap content

    [AvaloniaFact]
    public async Task The_minimap_draws_each_line_in_its_token_colours_and_follows_typing()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("public class Sample\n{\n    // a comment here\n    int x = 1;\n}\n", "a.cs");
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.HasTokens(0, 4), "tokens");
        await Settle(view);
        var (pixels, w, h) = Deco(view).Minimap.LastPixels;
        var background = pixels[w * h - 1]; // bottom right: past the text
        var rowPx = (int)Math.Round(Minimap.RowHeight);
        int P(int column, int row) => pixels[(row * rowPx + rowPx - 1) * w + column]; // a cell's bottom pixel row: lower-case cells start lower
        Assert.NotEqual(background, P(0, 0));            // "public"
        Assert.Equal(background, P(6, 0));               // the space after it
        Assert.NotEqual(P(0, 0), P(7, 2));               // a keyword and a comment have different colours
        Assert.Equal(background, P(0, 2));               // indentation is blank
        Assert.Equal(background, P(0, 6));               // no line 6

        view.Editor.CaretOffset = doc.Buffer.TextLength;
        view.TextArea.PerformTextInput("zzz");
        await Settle(view);
        (pixels, w, h) = Deco(view).Minimap.LastPixels;
        Assert.NotEqual(background, pixels[(5 * rowPx + rowPx - 1) * w + 0]); // the typed line is drawn
    }

    [AvaloniaFact]
    public async Task Clicking_the_minimap_jumps_there_and_dragging_the_slider_scrolls()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync(Lines(2000), "a.cs", height: 600);
        await Settle(view);
        var d = Deco(view);
        var origin = d.Minimap.Control.TranslatePoint(default, window)!.Value;
        Assert.Equal(0, view.Controller.TopLine, 1);

        // a click away from the slider centres the slider on it
        window.MouseDown(origin + new Point(40, 400), MouseButton.Left);
        window.MouseUp(origin + new Point(40, 400), MouseButton.Left);
        await Settle(view);
        var f = d.Minimap.Frame;
        Assert.InRange(f.SliderTop + f.SliderHeight / 2, 395, 405);
        var afterClick = view.Controller.TopLine;
        Assert.True(afterClick > 100, $"jumped to {afterClick}");

        // dragging the slider down by 100 px scrolls further, the slider staying under the pointer
        var grab = origin + new Point(40, f.SliderTop + 5);
        window.MouseDown(grab, MouseButton.Left);
        Assert.True(d.Minimap.IsDragging);
        window.MouseMove(grab + new Point(0, 100));
        await Settle(view);
        Assert.InRange(d.Minimap.Frame.SliderTop, f.SliderTop + 95, f.SliderTop + 105);
        window.MouseUp(grab + new Point(0, 100), MouseButton.Left);
        Assert.False(d.Minimap.IsDragging);
        Assert.True(view.Controller.TopLine > afterClick + 40);

        // the wheel over the minimap scrolls the editor
        var before = view.TextView.ScrollOffset.Y;
        window.MouseWheel(origin + new Point(40, 100), new Vector(0, -2));
        ViewHost.Pump();
        Assert.True(view.TextView.ScrollOffset.Y > before);
    }

    [AvaloniaFact]
    public async Task The_slider_shows_on_mouseover_by_default_and_always_when_set()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync(Lines(500), "a.cs");
        await Settle(view);
        var d = Deco(view);
        var origin = d.Minimap.Control.TranslatePoint(default, window)!.Value;
        window.MouseMove(new Point(100, 100)); // over the text
        ViewHost.Pump();
        Assert.False(d.Minimap.Control.IsPointerOver);
        Assert.False(SliderDrawn(view));
        window.MouseMove(origin + new Point(20, 300));
        ViewHost.Pump();
        Assert.True(SliderDrawn(view));
        window.MouseMove(new Point(100, 100));
        host.Config.Set(ScrollDecorations.MinimapShowSliderKey, "always");
        await Settle(view);
        Assert.True(SliderDrawn(view));
    }

    private static bool SliderDrawn(CodeEditorControl view)
    {
        var d = Deco(view);
        return d.Options.SliderAlways || d.Minimap.IsDragging || d.Minimap.Control.IsPointerOver;
    }

    [AvaloniaFact]
    public async Task Folded_lines_leave_the_minimap_and_the_ruler_places_marks_after_them_higher()
    {
        using var host = ViewHost.Create();
        var text = "class A\n{\n" + Lines(400, i => $"    int f{i};") + "\n}\nclass B\n{\n    int g;\n}\n";
        var (doc, view, _) = await host.OpenAsync(text, "a.cs");
        var folding = view.Feature<FoldingFeature>()!;
        await ViewHost.WaitFor(() => folding.Ranges.Any(r => r.Start == 0 || r.Start == 1), "class A's fold");
        view.Services.Diagnostics.Set(doc.Uri, "t", [new Diagnostic(TextRange.FromLines(406, 4, 406, 7), "g", DiagnosticSeverity.Error)]);
        await ViewHost.WaitFor(() => view.Feature<DiagnosticsFeature>()!.Count == 1, "diagnostic applied");
        await Settle(view);
        var d = Deco(view);
        var rowsBefore = d.Minimap.Rows.RowCount;
        var yBefore = RulerY(view, 406);
        Assert.Equal(MarkerKind.Error, RulerAt(view, MarkerLane.Right, 406));

        var header = folding.Ranges.First(r => r.Start is 0 or 1).Start;
        folding.FoldLines(EditorFoldAction.Close, header, header);
        await Settle(view);
        Assert.True(d.Minimap.Rows.RowCount < rowsBefore - 300, $"rows {rowsBefore} -> {d.Minimap.Rows.RowCount}");
        Assert.Equal(header, d.Minimap.Rows.RowOfLine(200)); // a hidden line is on its header's row
        var yAfter = RulerY(view, 406);
        Assert.True(yAfter < yBefore - 50, $"the mark moved from {yBefore} to {yAfter}");
        Assert.Equal(MarkerKind.Error, RulerAt(view, MarkerLane.Right, 406));
    }

    // ------------------------------------------------------------------------------------------------ the ruler's marks

    [AvaloniaFact]
    public async Task Problems_are_marked_on_the_right_lane_where_their_lines_are_the_most_severe_winning()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync(Lines(1000), "a.cs");
        view.Services.Diagnostics.Set(doc.Uri, "t",
        [
            new Diagnostic(TextRange.FromLines(100, 0, 100, 3), "w", DiagnosticSeverity.Warning),
            new Diagnostic(TextRange.FromLines(500, 0, 500, 3), "w", DiagnosticSeverity.Warning),
            new Diagnostic(TextRange.FromLines(500, 4, 500, 8), "e", DiagnosticSeverity.Error),
            new Diagnostic(TextRange.FromLines(900, 0, 900, 3), "i", DiagnosticSeverity.Information),
            new Diagnostic(TextRange.FromLines(700, 0, 700, 3), "h", DiagnosticSeverity.Hint),
        ]);
        await ViewHost.WaitFor(() => view.Feature<DiagnosticsFeature>()!.Count == 5, "diagnostics applied");
        await Settle(view);
        Assert.Equal(MarkerKind.Warning, RulerAt(view, MarkerLane.Right, 100));
        Assert.Equal(MarkerKind.Error, RulerAt(view, MarkerLane.Right, 500));
        Assert.Equal(MarkerKind.Info, RulerAt(view, MarkerLane.Right, 900));
        Assert.Equal(MarkerKind.None, RulerAt(view, MarkerLane.Right, 700)); // hints are not on the ruler
        Assert.Equal(MarkerKind.None, RulerAt(view, MarkerLane.Right, 300));
        Assert.Equal(MarkerKind.None, RulerAt(view, MarkerLane.Center, 500));

        view.Services.Diagnostics.Clear(doc.Uri, "t");
        await ViewHost.WaitFor(() => view.Feature<DiagnosticsFeature>()!.Count == 0, "diagnostics cleared");
        await Settle(view);
        Assert.Equal(MarkerKind.None, RulerAt(view, MarkerLane.Right, 500));
    }

    [AvaloniaFact]
    public async Task Find_matches_are_marked_while_the_find_panel_is_open()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Lines(1000, i => i % 250 == 0 ? $"needle {i}" : $"hay {i}"), "a.txt");
        view.Controller.ShowFind(false);
        view.Editor.SearchPanel.SearchPattern = "needle";
        await Settle(view);
        foreach (var line in new[] { 0, 250, 500, 750 }) Assert.Equal(MarkerKind.FindMatch, RulerAt(view, MarkerLane.Center, line));
        Assert.Equal(MarkerKind.None, RulerAt(view, MarkerLane.Center, 400));
        Assert.Equal(4, Deco(view).FindMatches.Count);

        view.Editor.SearchPanel.Close();
        await Settle(view);
        Assert.Empty(Deco(view).FindMatches);
        Assert.Equal(MarkerKind.None, RulerAt(view, MarkerLane.Center, 500));
    }

    [AvaloniaFact]
    public async Task Occurrence_highlights_are_marked_in_the_centre_lane()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync(Lines(600, i => i is 10 or 300 or 590 ? $"target {i}" : $"other {i}"), "a.txt");
        view.Editor.CaretOffset = doc.Buffer.GetLineByNumber(11).Offset + 2;
        await ViewHost.WaitFor(() => view.Feature<HighlightFeature>()!.Ranges.Count == 3, "occurrences");
        await Settle(view);
        Assert.Equal(MarkerKind.WordHighlight, RulerAt(view, MarkerLane.Center, 300));
        Assert.Equal(MarkerKind.WordHighlight, RulerAt(view, MarkerLane.Center, 590));
    }

    [AvaloniaFact]
    public async Task The_caret_lines_are_marked_across_the_ruler_unless_hidden()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync(Lines(1000), "a.cs");
        view.Editor.CaretOffset = doc.Buffer.GetLineByNumber(601).Offset;
        await Settle(view);
        Assert.Equal(MarkerKind.Cursor, RulerAt(view, MarkerLane.Full, 600));
        Assert.Equal(MarkerKind.None, RulerAt(view, MarkerLane.Full, 0));

        host.Config.Set(ScrollDecorations.HideCursorKey, true);
        await Settle(view);
        Assert.Equal(MarkerKind.None, RulerAt(view, MarkerLane.Full, 600));
    }

    [AvaloniaFact]
    public async Task Source_control_changes_are_marked_on_the_left_lane_and_in_the_minimap_gutter()
    {
        var original = Lines(800);
        var lines = Enumerable.Range(0, 800).Select(i => $"int value{i} = {i}; // line {i}").ToList();
        lines[200] = "changed";
        lines.Insert(600, "added");
        var quick = new FakeQuickDiff { Original = original };
        using var host = ViewHost.Create(s => s.AddSingleton<IQuickDiffProvider>(quick));
        var (_, view, _) = await host.OpenAsync(string.Join('\n', lines), "a.cs");
        await ViewHost.WaitFor(() => view.Feature<QuickDiffFeature>()!.Changes.Count == 2, "changes");
        await Settle(view);
        Assert.Equal(MarkerKind.Modified, RulerAt(view, MarkerLane.Left, 200));
        Assert.Equal(MarkerKind.Added, RulerAt(view, MarkerLane.Left, 600));
        Assert.Equal(MarkerKind.None, RulerAt(view, MarkerLane.Left, 400));
        Assert.Contains(Deco(view).Scm, m => m is { Line: 200, Kind: MarkerKind.Modified });
    }

    [AvaloniaFact]
    public async Task Breakpoints_are_marked_only_with_debug_showBreakpointsInOverviewRuler()
    {
        var debug = new FakeDebugService();
        using var host = ViewHost.Create(s => s.AddSingleton<IDebugService>(debug));
        var (doc, view, _) = await host.OpenAsync(Lines(1000), "a.cs");
        debug.Manager.Add(doc.Uri, 700);
        await Settle(view);
        Assert.Equal(MarkerKind.None, RulerAt(view, MarkerLane.Left, 700));
        host.Config.Set(ScrollDecorations.BreakpointsKey, true);
        await Settle(view);
        Assert.Equal(MarkerKind.Breakpoint, RulerAt(view, MarkerLane.Left, 700));
    }

    [AvaloniaFact]
    public async Task Word_wrap_places_marks_by_the_wrapped_rows()
    {
        using var host = ViewHost.Create();
        host.Config.Set(SettingKeys.EditorWordWrap, "on");
        // the first 100 lines wrap into several rows each: line 150 is far lower than 150 / 300 of the way down
        var (doc, view, _) = await host.OpenAsync(Lines(300, i => i < 100 ? string.Concat(Enumerable.Repeat($"word{i} ", 80)) : $"short {i}"), "a.txt");
        view.Services.Diagnostics.Set(doc.Uri, "t", [new Diagnostic(TextRange.FromLines(150, 0, 150, 3), "e", DiagnosticSeverity.Error)]);
        await ViewHost.WaitFor(() => view.Feature<DiagnosticsFeature>()!.Count == 1, "diagnostic");
        for (var line = 0; line < 110; line += 4) // the height tree measures wrapped lines as they are shown (as the scrollbar's thumb does)
        {
            view.Controller.ScrollToLine(line);
            ViewHost.Pump();
        }
        view.Controller.ScrollToLine(0);
        await Settle(view);
        var (top, height) = Deco(view).Ruler.Track;
        var y = RulerY(view, 150);
        Assert.True(y > top + height * 150 / 300.0 + 10, $"line 150 at {y} of {top}+{height}");
        Assert.Equal(MarkerKind.Error, RulerAt(view, MarkerLane.Right, 150));
        Assert.Equal(300, Deco(view).Minimap.Rows.RowCount); // a wrapped line is one minimap row
    }

    [AvaloniaFact]
    public async Task A_press_on_the_scrollbar_track_jumps_there_and_the_thumb_still_drags()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync(Lines(3000), "a.cs", height: 600);
        await Settle(view);
        var d = Deco(view);
        var (top, height) = d.Ruler.Track;
        var rulerOrigin = d.Ruler.Control.TranslatePoint(default, window)!.Value;
        var at = rulerOrigin + new Point(d.Ruler.Width / 2, top + height * 0.75);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        ViewHost.Pump();
        // three quarters of the way down is in the middle of the view now
        var scrollable = (IScrollable)view.TextArea;
        var middle = (view.TextView.ScrollOffset.Y + scrollable.Viewport.Height / 2) / scrollable.Extent.Height;
        Assert.InRange(middle, 0.73, 0.77);
    }

    [AvaloniaFact]
    public async Task Theme_changes_recolour_the_ruler_and_the_minimap()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Lines(100), "a.cs");
        await Settle(view);
        var (pixels, w, h) = Deco(view).Minimap.LastPixels;
        var darkBackground = pixels[w * h - 1];
        var darkError = Deco(view).Brushes.Markers[(int)MarkerKind.Error];
        await host.Factory.Themes.SetCurrentAsync("vscode.light-plus", TestContext.Current.CancellationToken);
        await Settle(view);
        (pixels, w, h) = Deco(view).Minimap.LastPixels;
        Assert.NotEqual(darkBackground, pixels[w * h - 1]);
        Assert.NotSame(darkError, Deco(view).Brushes.Markers[(int)MarkerKind.Error]);
    }

    /// <summary>Writes <c>.scratch/screenshots/minimap-{theme}.png</c> (not committed) with every kind of mark, and checks the minimap shows code.</summary>
    [AvaloniaTheory]
    [InlineData("dark", "vscode.dark-plus")]
    [InlineData("light", "vscode.light-plus")]
    public async Task Screenshot_of_the_minimap_and_the_ruler_with_every_kind_of_mark(string name, string theme)
    {
        var quick = new FakeQuickDiff();
        using var host = ViewHost.Create(s => s.AddSingleton<IQuickDiffProvider>(quick));
        await host.Factory.Themes.SetCurrentAsync(theme, TestContext.Current.CancellationToken);
        const string block = """
            using System;

            namespace Demo
            {
                /// <summary>A sample class.</summary>
                public sealed class SampleNN
                {
                    private readonly int _count = NN;

                    public string Describe(string prefix)
                    {
                        if (prefix is null) throw new ArgumentNullException(nameof(prefix));
                        return $"{prefix}: {_count}"; // formatted
                    }
                }
            }

            """;
        var text = string.Concat(Enumerable.Range(0, 40).Select(i => block.Replace("NN", i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var lines = text.Split('\n').ToList();
        quick.Original = string.Join('\n', lines.Where((_, i) => i is not (100 or 101 or 102)).Select((l, i) => i == 300 ? "old" : l));
        var (doc, view, window) = await host.OpenAsync(text, "Sample.cs", 1000, 640);
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= 60, "tokens");
        view.Services.Diagnostics.Set(doc.Uri, "demo",
        [
            new Diagnostic(TextRange.FromLines(40, 8, 40, 20), "error", DiagnosticSeverity.Error),
            new Diagnostic(TextRange.FromLines(250, 8, 250, 20), "warning", DiagnosticSeverity.Warning),
            new Diagnostic(TextRange.FromLines(500, 8, 500, 20), "info", DiagnosticSeverity.Information),
        ]);
        view.Controller.ShowFind(false);
        view.Editor.SearchPanel.SearchPattern = "Sample3";
        view.Controller.SetSelection(TextRange.At(12, 20));
        await ViewHost.WaitFor(() => view.Feature<DiagnosticsFeature>()!.Count == 3 && view.Feature<QuickDiffFeature>()!.Changes.Count > 0, "marks");
        window.MouseMove(Deco(view).Minimap.Control.TranslatePoint(new Point(30, 30), window)!.Value); // shows the slider
        await Settle(view);
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.scratch/screenshots"));
        Directory.CreateDirectory(dir);
        var frame = window.CaptureRenderedFrame()!;
        using (var fs = File.Create(Path.Combine(dir, $"minimap-{name}.png"))) frame.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        // the minimap shows code: several distinct colours, not one flat fill
        var d = Deco(view);
        var (pixels, w, h) = d.Minimap.LastPixels;
        Assert.True(pixels.Take(w * Math.Min(h, 200)).Distinct().Count() >= 4, "colours in the minimap");
        Assert.True(d.Minimap.Control.TranslatePoint(default, window)!.Value.X > 800);
    }

    [AvaloniaFact]
    public async Task Disposing_the_view_removes_both_controls()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync(Lines(100), "a.cs");
        await Settle(view);
        var d = Deco(view);
        var grid = (Grid)d.Minimap.Control.Parent!;
        host.Factory.DisposeView(view);
        Assert.True(d.IsDisposed);
        Assert.DoesNotContain(d.Minimap.Control, grid.Children);
        Assert.DoesNotContain(d.Ruler.Control, grid.Children);
        Assert.Null(d.Minimap.Bitmap);
    }

    // ------------------------------------------------------------------------------------------------ large files

    [AvaloniaFact]
    public async Task A_million_line_file_draws_only_the_minimap_window_and_lays_out_twenty_thousand_marks_fast()
    {
        using var host = ViewHost.Create();
        var text = Lines(1_000_000, i => $"line {i} text");
        var (doc, view, window) = await host.OpenAsync(text, "big.txt", height: 700);
        Assert.True(doc.IsLargeFile);
        await Settle(view);
        var d = Deco(view);
        Assert.Equal(1_000_000, d.Minimap.Rows.RowCount);
        Assert.Equal(0, d.Minimap.RenderedWindowTop);
        var windowRows = d.Minimap.Frame.WindowRows;
        Assert.InRange(windowRows, 100, 400);

        // the end of the file: the minimap's window is its last rows, the slider at the bottom
        view.Controller.ScrollToLine(999_990);
        await Settle(view);
        Assert.InRange(d.Minimap.RenderedWindowTop, 1_000_000 - windowRows - 2, 1_000_000 - windowRows);
        output.WriteLine($"minimap render on the UI thread: capture {d.Minimap.LastCaptureMs:F2} ms, blit {d.Minimap.LastBlitMs:F2} ms ({windowRows} rows)");
        Assert.True(d.Minimap.LastCaptureMs + d.Minimap.LastBlitMs < 30, "the UI thread's part of a render");
        var f = d.Minimap.Frame;
        Assert.True(f.SliderTop + f.SliderHeight >= d.Minimap.Control.Bounds.Height - 30, $"slider at {f.SliderTop}+{f.SliderHeight}");

        // 19,999 find matches (the find limit): collected and laid out in well under a frame's budget of many frames
        view.Controller.ShowFind(false);
        view.Editor.SearchPanel.SearchPattern = "text";
        ViewHost.Pump();
        var sw = Stopwatch.StartNew();
        d.FlushForTests();
        sw.Stop();
        var layout = Stopwatch.StartNew();
        d.Ruler.Refresh();
        output.WriteLine($"layout alone: {layout.Elapsed.TotalMilliseconds:F1} ms");
        output.WriteLine($"collect + layout of {d.FindMatches.Count} find matches on 1M lines: {sw.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(19_999, d.FindMatches.Count);
        Assert.True(sw.ElapsedMilliseconds < 50, $"took {sw.ElapsedMilliseconds} ms"); // measured 2 ms: a lookup per pixel row, not per mark
        Assert.Equal(MarkerKind.FindMatch, d.Ruler.Model.At(MarkerLane.Center, (int)d.Ruler.Track.Top));

        // a scroll recomputes nothing of the ruler, and only moves the minimap's window
        var layouts = d.Ruler.Layouts;
        var sw2 = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            view.Controller.ScrollToLine(500_000 + i * 3);
            ViewHost.Pump();
        }
        output.WriteLine($"20 scroll steps: {sw2.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(layouts, d.Ruler.Layouts);
        await Settle(view);
        Assert.InRange(d.Minimap.RenderedWindowTop, 499_000, 501_000);
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task Typing_marks_the_minimap_stale_without_rendering_on_the_keystroke()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync(Lines(5000), "a.cs");
        await Settle(view);
        var d = Deco(view);
        var renders = d.Minimap.Renders;
        var layouts = d.Ruler.Layouts;
        for (var i = 0; i < 30; i++) view.TextArea.PerformTextInput("x"); // no dispatcher turn between the keystrokes
        Assert.Equal(renders, d.Minimap.Renders);
        Assert.Equal(layouts, d.Ruler.Layouts); // the marks are recollected once typing pauses
        await Settle(view);
        Assert.InRange(d.Minimap.Renders, renders + 1, renders + 3);
    }

    private sealed class FakeQuickDiff : IQuickDiffProvider
    {
        public readonly Emitter<Uri?> Changed = new();
        public string? Original { get; set; }
        public ValueTask<QuickDiffOriginal?> GetOriginalAsync(Uri uri, CancellationToken ct = default) =>
            ValueTask.FromResult(Original is null ? null : new QuickDiffOriginal(Original, "Index"));
        public IEventSource<Uri?> OriginalChanged => Changed;
        public ValueTask<bool> StageRangeAsync(Uri uri, int startLine, int endLine, CancellationToken ct = default) => ValueTask.FromResult(true);
    }
}
