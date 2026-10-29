using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaEdit.Document;
using CsCode.Core.Debugging;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Features;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

/// <summary>Bracket matching/colourisation, occurrence highlights and the debugger surface (breakpoint margin, stop line, breakpoints that follow edits).</summary>
public class BracketsHighlightsDebugTests
{
    // ---- brackets (pure) ----

    private static readonly BracketPairs Pairs = BracketPairs.From(new LanguageConfiguration { Brackets = [("(", ")"), ("[", "]"), ("{", "}")] });

    private static DocumentSnapshot Snap(string text) => new(1, ((RopeTextSource)new TextDocument(text).CreateSnapshot()).GetRope());

    private static (int, int)? Match(string text, int caret) => BracketFeature.Find(Snap(text), caret, Pairs, null, CancellationToken.None);

    [Theory]
    [InlineData("f(a, b)", 1, 1, 6)] // caret before the opener
    [InlineData("f(a, b)", 2, 1, 6)] // caret right after the opener
    [InlineData("f(a, b)", 6, 6, 1)] // caret before the closer
    [InlineData("f(a, b)", 7, 6, 1)] // caret after the closer
    [InlineData("a[(b)]c", 1, 1, 5)]
    [InlineData("a[(b)]c", 2, 2, 4)]
    [InlineData("{\n  {\n  }\n}", 0, 0, 10)]
    public void A_bracket_next_to_the_caret_is_matched_with_its_partner(string text, int caret, int a, int b)
    {
        Assert.Equal((a, b), Match(text, caret));
    }

    [Theory]
    [InlineData("f(a, b", 1)] // unbalanced
    [InlineData("abc", 1)] // no bracket
    [InlineData(") (", 1)] // wrong order: the opener at 2 has no closer
    public void Without_a_partner_there_is_no_match(string text, int caret)
    {
        Assert.Null(Match(text, caret));
    }

    [Fact]
    public void Nested_brackets_of_the_same_kind_pair_by_depth()
    {
        Assert.Equal((0, 7), Match("((a)(b))", 0));
        Assert.Equal((1, 3), Match("((a)(b))", 1));
        Assert.Equal((4, 6), Match("((a)(b))", 4));
    }

    [Fact]
    public void The_depth_map_counts_nesting_at_the_start_of_each_line()
    {
        var depths = BracketFeature.BuildDepths(Snap("((\n)\n)\nx"), Pairs, null, CancellationToken.None);
        Assert.Equal([0, 2, 1, 0], depths);
    }

    [Fact]
    public void An_unmatched_closer_never_makes_the_depth_negative()
    {
        var depths = BracketFeature.BuildDepths(Snap(")\n(\nx"), Pairs, null, CancellationToken.None);
        Assert.Equal([0, 0, 1], depths);
    }

    [AvaloniaFact]
    public async Task The_view_matches_brackets_off_the_ui_thread_and_follows_the_caret()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("void F(int a) { return (a); }\n", "a.cs");
        view.Controller.SetSelection(TextRange.At(0, 6)); // right after the '('
        await ViewHost.WaitFor(() => Brackets(view).CurrentMatch is not null, "match computed");
        Assert.Equal((6, 12), Brackets(view).CurrentMatch);
        view.Controller.SetSelection(TextRange.At(0, 0));
        await ViewHost.WaitFor(() => Brackets(view).CurrentMatch is null, "match cleared when the caret leaves");
    }

    [AvaloniaFact]
    public async Task Brackets_inside_strings_and_comments_are_ignored()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("var s = \"(\"; // )\nf(x);\n", "a.cs");
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= 2, "tokens");
        view.Controller.SetSelection(TextRange.At(1, 2));
        await ViewHost.WaitFor(() => Brackets(view).CurrentMatch is not null, "match");
        var doc = view.Document;
        Assert.Equal((doc.OffsetAt(new TextPosition(1, 1)), doc.OffsetAt(new TextPosition(1, 3))), Brackets(view).CurrentMatch);
    }

    [AvaloniaFact]
    public async Task Pair_colourisation_builds_a_depth_map_and_is_gated_by_the_setting()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("a(\n  b[\n  ]\n)\n", "a.cs");
        await ViewHost.WaitFor(() => Brackets(view).Depths is not null, "depth map");
        Assert.Equal([0, 1, 2, 1, 0], Brackets(view).Depths!);
        host.Config.Set("editor.bracketPairColorization.enabled", false);
        await ViewHost.WaitFor(() => Brackets(view).Depths is null, "map dropped when colourisation is off");
        host.Config.Set("editor.bracketPairColorization.enabled", true);
        await ViewHost.WaitFor(() => Brackets(view).Depths is not null, "map back on");
    }

    private static BracketFeature Brackets(CodeEditorControl v) => v.Feature<BracketFeature>()!;

    // ---- occurrence highlights ----

    private static HighlightFeature Highlights(CodeEditorControl v) => v.Feature<HighlightFeature>()!;

    [Fact]
    public void Word_occurrences_are_whole_words_only()
    {
        var found = HighlightFeature.FindWords(Snap("foo foobar barfoo foo\nxfoo foo_ foo"), "foo", CancellationToken.None);
        Assert.Equal([(0, 3, false), (18, 21, false), (32, 35, false)], found.Select(f => (f.Start, f.End, f.Strong)).ToArray());
    }

    [AvaloniaFact]
    public async Task Without_a_provider_the_occurrences_of_the_word_under_the_caret_are_highlighted_and_cleared_on_edit()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("count = count + 1;\nreturn count;\n", "a.txt");
        view.Controller.SetSelection(TextRange.At(0, 2));
        await ViewHost.WaitFor(() => Highlights(view).Ranges.Count == 3, "three occurrences");
        view.Controller.SetSelection(TextRange.At(0, 3)); // still touching `count`
        Type(view, "x"); // edit: stale offsets must vanish at once
        Assert.Empty(Highlights(view).Ranges);
    }

    [AvaloniaFact]
    public async Task A_document_highlight_provider_takes_over_and_writes_are_marked_strong()
    {
        using var host = ViewHost.Create();
        host.Factory.Services.Features.Register<IDocumentHighlightProvider>(DocumentSelector.All, new FakeHighlightProvider((_, _) => Task.FromResult<IReadOnlyList<DocumentHighlight>?>(
        [
            new DocumentHighlight(TextRange.FromLines(0, 4, 0, 5), DocumentHighlightKind.Write),
            new DocumentHighlight(TextRange.FromLines(1, 7, 1, 8), DocumentHighlightKind.Read),
        ])));
        var (_, view, _) = await host.OpenAsync("int a = 1;\nreturn a;\n", "a.txt");
        view.Controller.SetSelection(TextRange.At(0, 5));
        await ViewHost.WaitFor(() => Highlights(view).Ranges.Count == 2, "provider highlights");
        Assert.Equal([true, false], Highlights(view).Ranges.Select(r => r.Strong).ToArray());
    }

    [AvaloniaFact]
    public async Task Occurrence_highlighting_can_be_turned_off()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.occurrencesHighlight", false);
        var (_, view, _) = await host.OpenAsync("a a a\n", "a.txt");
        view.Controller.SetSelection(TextRange.At(0, 1));
        await Task.Delay(400);
        ViewHost.Pump();
        Assert.Empty(Highlights(view).Ranges);
    }

    // ---- breakpoint shifting (pure) ----

    [Fact]
    public void Pressing_enter_in_the_middle_of_a_line_keeps_its_breakpoint_and_moves_the_following_ones()
    {
        var change = new TextContentChange(TextRange.At(5, 3), 0, 0, "\n");
        Assert.Equal((1, 0, 6, 1), BreakpointShift.Of(change));
    }

    [Fact]
    public void Pressing_enter_at_the_start_of_a_line_moves_that_line_s_breakpoint_down_with_it()
    {
        var change = new TextContentChange(TextRange.At(5, 0), 0, 0, "\n");
        Assert.Equal((1, 0, 5, 1), BreakpointShift.Of(change));
    }

    [Fact]
    public void Deleting_lines_removes_the_breakpoints_on_them_and_pulls_the_rest_up()
    {
        var change = new TextContentChange(TextRange.FromLines(3, 2, 5, 4), 0, 0, "");
        Assert.Equal((4, 5, 6, -2), BreakpointShift.Of(change)); // something of line 3 survives: its breakpoint stays, 4 and 5 vanish
    }

    [Fact]
    public void Deleting_whole_lines_from_column_zero_removes_exactly_those_lines_breakpoints()
    {
        var change = new TextContentChange(TextRange.FromLines(3, 0, 5, 0), 0, 0, "");
        Assert.Equal((3, 4, 5, -2), BreakpointShift.Of(change)); // lines 3 and 4 are gone; what was line 5 is now line 3
    }

    [Fact]
    public void A_pasted_block_moves_later_breakpoints_by_its_line_count()
    {
        var change = new TextContentChange(TextRange.At(2, 4), 0, 0, "a\nb\nc");
        Assert.Equal((1, 0, 3, 2), BreakpointShift.Of(change));
    }

    [Fact]
    public void Edits_within_a_line_move_nothing()
    {
        Assert.Null(BreakpointShift.Of(new TextContentChange(TextRange.FromLines(3, 1, 3, 4), 0, 3, "xy")));
    }

    // ---- the debugger surface ----

    private static (FakeDebugService Debug, ViewHost Host) DebugHost()
    {
        var debug = new FakeDebugService();
        return (debug, ViewHost.Create(s => s.AddSingleton<IDebugService>(debug)));
    }

    private static DebugFeature DebugOf(CodeEditorControl v) => v.Feature<DebugFeature>()!;

    [AvaloniaFact]
    public async Task Without_a_debugger_the_view_has_no_breakpoint_margin()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("a\n", "a.cs");
        Assert.Null(view.Feature<DebugFeature>());
        Assert.DoesNotContain(view.TextArea.LeftMargins, m => m.GetType().Name == "BreakpointMargin");
    }

    [AvaloniaFact]
    public async Task Clicking_the_glyph_margin_toggles_a_breakpoint_and_right_click_is_handled()
    {
        var (debug, host) = DebugHost();
        using var _ = host;
        var (doc, view, window) = await host.OpenAsync("line0\nline1\nline2\n", "a.cs");
        var margin = view.TextArea.LeftMargins[0];
        Assert.Equal("BreakpointMargin", margin.GetType().Name);
        ViewHost.Pump();
        var line1 = margin.TranslatePoint(new Point(9, view.TextView.DefaultLineHeight * 1.5), window)!.Value;
        window.MouseDown(line1, MouseButton.Left);
        window.MouseUp(line1, MouseButton.Left);
        ViewHost.Pump();
        Assert.Equal([1], debug.Manager.GetForUri(doc.Uri).Select(b => b.Line).ToArray());
        window.MouseDown(line1, MouseButton.Left);
        window.MouseUp(line1, MouseButton.Left);
        ViewHost.Pump();
        Assert.Empty(debug.Manager.GetForUri(doc.Uri));
    }

    [AvaloniaFact]
    public async Task The_glyph_is_drawn_in_the_breakpoint_colour_and_hollow_grey_when_unverified()
    {
        var (debug, host) = DebugHost();
        using var _ = host;
        var (doc, view, window) = await host.OpenAsync("line0\nline1\nline2\n", "a.cs");
        var margin = view.TextArea.LeftMargins[0];
        var red = ((Avalonia.Media.ISolidColorBrush)view.Palette.BreakpointFill).Color;
        var bounds = margin.TranslatePoint(new Point(0, 0), window)!.Value;
        var region = new PixelRect((int)bounds.X, (int)bounds.Y, (int)margin.Bounds.Width, (int)margin.Bounds.Height);
        var empty = CountPixels(window.CaptureRenderedFrame()!, red, 12, region);
        var bp = debug.Manager.Add(doc.Uri, 1);
        ViewHost.Pump();
        await ViewHost.WaitFor(() => DebugOf(view).BreakpointAt(1) is not null, "breakpoint shown");
        ViewHost.Pump();
        var solid = CountPixels(window.CaptureRenderedFrame()!, red, 12, region);
        Assert.True(solid > empty + 20, $"a filled red circle must be drawn ({empty} -> {solid})");

        debug.Manager.ReportStatus(new DebugBreakpointStatus(new FakeDebugSession(), bp.Id, false, null, "Source file not found"));
        await ViewHost.WaitFor(() => DebugOf(view).Describe(1)!.Contains("Source file not found"), "status message in the tooltip text");
        ViewHost.Pump();
        var unverified = CountPixels(window.CaptureRenderedFrame()!, red, 12, region);
        Assert.True(unverified < solid / 2, "an unverified breakpoint is hollow grey, not filled red");
        Assert.Contains("Unverified: Source file not found", DebugOf(view).Describe(1));
    }

    [AvaloniaFact]
    public async Task Breakpoints_follow_edits_above_them_and_disappear_with_their_line()
    {
        var (debug, host) = DebugHost();
        using var _ = host;
        var (doc, view, _) = await host.OpenAsync("a\nb\nc\nd\ne\n", "a.cs");
        debug.Manager.Add(doc.Uri, 3); // "d"
        debug.Manager.Add(doc.Uri, 1); // "b"
        ViewHost.Pump();

        doc.ApplyEdits([TextEdit.Insert(TextPosition.Zero, "new first line\n")]); // everything moves down one
        ViewHost.Pump();
        Assert.Equal([2, 4], debug.Manager.GetForUri(doc.Uri).Select(b => b.Line).Order().ToArray());

        // delete the line holding the (now line 2) breakpoint: "b\n"
        doc.ApplyEdits([TextEdit.Delete(TextRange.FromLines(2, 0, 3, 0))]);
        ViewHost.Pump();
        Assert.Equal([3], debug.Manager.GetForUri(doc.Uri).Select(b => b.Line).ToArray()); // "d" is now on line 3
        Assert.Equal("d", doc.GetLineText(3));
        Assert.True(debug.Manager.Shifts.Count >= 2);
    }

    [AvaloniaFact]
    public async Task Stopping_in_this_file_shows_the_arrow_highlights_and_reveals_the_line_resuming_clears_it()
    {
        var (debug, host) = DebugHost();
        using var _ = host;
        var (doc, view, window) = await host.OpenAsync(string.Join('\n', Enumerable.Range(0, 200).Select(i => "line " + i)), "a.cs", height: 300);
        debug.Stop(doc.Uri, 150);
        ViewHost.Pump();
        Assert.Equal(150, view.Controller.StopLine);
        await ViewHost.WaitFor(() => view.TextView.VerticalOffset > 100, "revealed: scrolled to the stop line");
        var yellow = ((Avalonia.Media.ISolidColorBrush)view.Palette.StackFrameArrow).Color;
        ViewHost.Pump();
        Assert.True(CountPixels(window.CaptureRenderedFrame()!, yellow, 16) > 8, "a yellow arrow must be on screen");

        debug.Resume();
        ViewHost.Pump();
        Assert.Null(view.Controller.StopLine);
    }

    [AvaloniaFact]
    public async Task Stopping_in_another_file_does_not_mark_this_view()
    {
        var (debug, host) = DebugHost();
        using var _ = host;
        var (_, view, _) = await host.OpenAsync("a\nb\n", "a.cs");
        debug.Stop(new Uri("file:///elsewhere/Other.cs"), 1);
        ViewHost.Pump();
        Assert.Null(view.Controller.StopLine);
    }

    [AvaloniaFact]
    public async Task A_view_created_while_already_stopped_shows_the_stop_line_immediately()
    {
        var (debug, host) = DebugHost();
        using var _ = host;
        var path = host.Dir.WriteFile("a.cs", "a\nb\nc\n");
        debug.Stop(new Uri(path), 2);
        var doc = (EditorDocument)await host.Factory.OpenDocumentAsync(new Uri(path));
        var view = (CodeEditorControl)host.Factory.CreateView(doc);
        Assert.Equal(2, view.Controller.StopLine);
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task Conditions_hit_counts_and_logpoints_are_edited_through_input_boxes()
    {
        var (debug, host) = DebugHost();
        using var _ = host;
        var (doc, view, _) = await host.OpenAsync("a\nb\nc\n", "a.cs");
        var feature = DebugOf(view);

        host.Window.InputResponses.Enqueue("i > 3");
        await feature.EditConditionAsync(1);
        Assert.Equal("i > 3", debug.Manager.GetForUri(doc.Uri).Single().Condition);
        await ViewHost.WaitFor(() => feature.BreakpointAt(1)?.Condition == "i > 3", "margin refreshed");
        Assert.Contains("Conditional breakpoint", feature.Describe(1));

        await ViewHost.WaitFor(() => feature.BreakpointAt(1) is not null, "refresh");
        host.Window.InputResponses.Enqueue("% 2 == 0");
        await feature.EditHitCountAsync(1);
        Assert.Equal("% 2 == 0", debug.Manager.GetForUri(doc.Uri).Single().HitCondition);

        host.Window.InputResponses.Enqueue("value is {i}");
        await feature.EditLogMessageAsync(2);
        Assert.Equal("value is {i}", debug.Manager.GetForUri(doc.Uri).Single(b => b.Line == 2).LogMessage);
        await ViewHost.WaitFor(() => feature.BreakpointAt(2)?.LogMessage is not null, "margin refreshed for the logpoint");
        Assert.StartsWith("Logpoint", feature.Describe(2));

        host.Window.InputResponses.Enqueue(null); // cancel: nothing changes
        await feature.EditConditionAsync(1);
        Assert.Equal("i > 3", debug.Manager.GetForUri(doc.Uri).Single(b => b.Line == 1).Condition);

        host.Window.InputResponses.Enqueue(""); // empty clears the condition
        await feature.EditConditionAsync(1);
        Assert.Null(debug.Manager.GetForUri(doc.Uri).Single(b => b.Line == 1).Condition);
    }

    [AvaloniaFact]
    public async Task A_disabled_breakpoint_can_be_toggled_and_removed()
    {
        var (debug, host) = DebugHost();
        using var _ = host;
        var (doc, view, _) = await host.OpenAsync("a\nb\n", "a.cs");
        var feature = DebugOf(view);
        feature.Toggle(0);
        await ViewHost.WaitFor(() => feature.BreakpointAt(0) is not null, "added");
        feature.SetEnabled(0, false);
        await ViewHost.WaitFor(() => feature.BreakpointAt(0) is { Enabled: false }, "disabled");
        Assert.Contains("Disabled", feature.Describe(0));
        feature.Remove(0);
        await ViewHost.WaitFor(() => feature.BreakpointAt(0) is null, "removed");
        Assert.Empty(debug.Manager.GetForUri(doc.Uri));
    }

    [AvaloniaFact]
    public async Task Disposing_the_view_removes_the_margin_and_every_debug_subscription()
    {
        var (debug, host) = DebugHost();
        using var _ = host;
        var (doc, view, _) = await host.OpenAsync("a\nb\n", "a.cs");
        var margins = view.TextArea.LeftMargins;
        var count = margins.Count;
        host.Factory.DisposeView(view);
        Assert.True(margins.Count < count);
        debug.Manager.Add(doc.Uri, 1); // raising events after disposal must be harmless
        debug.Stop(doc.Uri, 1);
        debug.Resume();
    }

    [AvaloniaFact]
    public async Task The_toggle_breakpoint_command_acts_on_the_focused_view()
    {
        var (debug, host) = DebugHost();
        using var _ = host;
        var (doc, view, _) = await host.OpenAsync("a\nb\nc\n", "a.cs");
        host.Factory.NotifyFocused(view);
        view.Controller.SetSelection(TextRange.At(2, 0));
        await host.Commands.ExecuteAsync(CsCode.Core.Platform.KnownCommands.DebugToggleBreakpoint);
        Assert.Equal([2], debug.Manager.GetForUri(doc.Uri).Select(b => b.Line).ToArray());
    }
}
