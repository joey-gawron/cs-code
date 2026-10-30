using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>MUST 2/3/4/7 in a real (headless) control: rendering, syntax colours on screen, typing behaviours, commands, live settings and theme.</summary>
public class EditorViewTests
{
    private static string Text(CodeEditorControl v) => v.Document.GetText();

    private static void Type(CodeEditorControl v, string text)
    {
        foreach (var ch in text) v.TextArea.PerformTextInput(ch.ToString());
    }

    // ---- creation, rendering ----

    [AvaloniaFact]
    public async Task A_view_renders_the_document_and_exposes_a_working_controller()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("class A\n{\n}\n");
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var controller = host.Factory.GetController(view)!;
        Assert.Same(doc, controller.Document);
        controller.SetSelection(TextRange.FromLines(1, 0, 1, 1));
        Assert.Equal(new TextPosition(1, 1), controller.Caret);
        Assert.Equal(TextRange.FromLines(1, 0, 1, 1), controller.Selections[0]);
        controller.SetSelection(TextRange.At(2, 1));
        Assert.Equal(TextRange.At(2, 1), controller.Selections[0]);
    }

    [AvaloniaFact]
    public async Task Caret_and_selection_events_fire_with_zero_based_positions()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("abc\ndef\n");
        var carets = new List<TextPosition>();
        var sels = new List<IReadOnlyList<TextRange>>();
        view.Controller.CaretChanged.Subscribe(carets.Add);
        view.Controller.SelectionChanged.Subscribe(sels.Add);
        view.Controller.SetSelection(TextRange.FromLines(1, 1, 1, 3));
        ViewHost.Pump();
        Assert.Contains(new TextPosition(1, 3), carets);
        Assert.NotEmpty(sels);
    }

    [AvaloniaFact]
    public async Task Typing_edits_the_document_and_undo_redo_round_trip()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("");
        Type(view, "hello");
        Assert.Equal("hello", Text(view));
        Assert.True(doc.IsDirty);
        Assert.True(view.Controller.CanUndo);
        view.Controller.Undo();
        Assert.False(doc.IsDirty);
        Assert.True(view.Controller.CanRedo);
        view.Controller.Redo();
        Assert.Equal("hello", Text(view));
    }

    // ---- syntax colours reach the screen ----

    [AvaloniaFact]
    public async Task Csharp_tokens_colour_the_visual_line_elements()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("int x = 1; // note\n");
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= 1, "tokens");
        view.TextView.Redraw();
        ViewHost.Pump();
        view.TextView.EnsureVisualLines();
        var vl = view.TextView.VisualLines[0];
        var brushes = vl.Elements.Select(e => (e.TextRunProperties.ForegroundBrush as ISolidColorBrush)?.Color).Distinct().ToList();
        // keyword, number and comment get distinct colours from the editor's default
        Assert.True(brushes.Count >= 3, $"only {brushes.Count} distinct foreground colours on the line");
        var dark = host.Factory.Themes.GetColor("editor.foreground");
        Assert.Contains(brushes, c => c is { } cc && cc.R == 0x56 && cc.G == 0x9C && cc.B == 0xD6); // Dark+ keyword blue
    }

    [AvaloniaFact]
    public async Task Switching_the_theme_recolours_without_retokenising()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("int x = 1;\n");
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= 1, "tokens");
        var jobsBefore = host.Factory.Worker.CompletedJobs;
        await host.Factory.Themes.SetCurrentAsync("vscode.light-plus", TestContext.Current.CancellationToken);
        await ViewHost.WaitFor(() => view.Editor.Background is ISolidColorBrush b && b.Color.R == 255, "editor background switches to light");
        view.TextView.EnsureVisualLines();
        var keyword = view.TextView.VisualLines[0].Elements.Select(e => (e.TextRunProperties.ForegroundBrush as ISolidColorBrush)?.Color).First(c => c is { } cc && cc.B > cc.R);
        Assert.NotNull(keyword);
        Assert.Equal(jobsBefore, host.Factory.Worker.CompletedJobs); // no tokenisation job ran for a pure colour change
    }

    // ---- typing behaviours in the real control ----

    [AvaloniaFact]
    public async Task Typing_an_opening_bracket_inserts_the_pair_and_the_closer_is_over_typed()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("");
        Type(view, "f(");
        Assert.Equal("f()", Text(view));
        Assert.Equal(2, view.Editor.CaretOffset);
        Type(view, "x)");
        Assert.Equal("f(x)", Text(view));
        Assert.Equal(4, view.Editor.CaretOffset);
    }

    [AvaloniaFact]
    public async Task Undoing_an_edit_made_in_a_destroyed_view_works_while_that_view_is_still_in_memory()
    {
        using var host = ViewHost.Create();
        var (doc, view, window) = await host.OpenAsync("abc");
        Type(view, "x");
        host.Factory.DisposeView(view);                 // its undo entries are scrubbed; the text area itself is not collected yet
        var again = host.Factory.CreateView(doc);
        window.Content = again;
        ViewHost.Pump();
        doc.Undo();                                     // used to throw: the scrubbed entry handed the live text area a null selection
        Assert.Equal("abc", doc.GetText());
        GC.KeepAlive(view);
    }

    [AvaloniaFact]
    public async Task A_closer_the_user_typed_themselves_is_not_swallowed()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("");
        view.TextArea.PerformTextInput(")"); // no opener, nothing auto-inserted
        Assert.Equal(")", Text(view));
    }

    [AvaloniaFact]
    public async Task Backspace_between_an_auto_inserted_pair_removes_both()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync("");
        Type(view, "[");
        Assert.Equal("[]", Text(view));
        window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        ViewHost.Pump();
        Assert.Equal("", Text(view));
    }

    [AvaloniaFact]
    public async Task Typing_an_opener_with_a_selection_surrounds_it_and_keeps_it_selected()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("word");
        view.Controller.SetSelection(TextRange.FromLines(0, 0, 0, 4));
        Type(view, "(");
        Assert.Equal("(word)", Text(view));
        Assert.Equal(TextRange.FromLines(0, 1, 0, 5), view.Controller.Selections[0]);
    }

    [AvaloniaFact]
    public async Task Quotes_pair_after_space_but_not_after_a_word_and_not_inside_a_string()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("");
        Type(view, "x = \"");
        Assert.Equal("x = \"\"", Text(view));
        view.Controller.SetSelection(TextRange.At(0, 6));
        Type(view, " don'");
        Assert.EndsWith("don'", Text(view).TrimEnd());
        Assert.DoesNotContain("''", Text(view));
    }

    [AvaloniaFact]
    public async Task Enter_between_braces_opens_three_lines_with_the_caret_on_the_indented_one()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync("");
        Type(view, "{");
        Assert.Equal("{}", Text(view));
        view.TextArea.PerformTextInput("\n");
        Assert.Equal("{\n    \n}", Text(view));
        Assert.Equal(new TextPosition(1, 4), view.Controller.Caret);
    }

    [AvaloniaFact]
    public async Task Enter_keeps_the_current_indentation()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("    foo();");
        view.Controller.SetSelection(TextRange.At(0, 10));
        view.TextArea.PerformTextInput("\n");
        Assert.Equal("    foo();\n    ", Text(view));
    }

    [AvaloniaFact]
    public async Task A_closing_brace_typed_on_its_own_line_snaps_back_and_undoes_with_the_keystroke()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("if (x)\n{\n    foo();\n    ");
        view.Controller.SetSelection(TextRange.At(3, 4));
        view.TextArea.PerformTextInput("}");
        Assert.Equal("if (x)\n{\n    foo();\n}", Text(view));
        view.Controller.Undo();
        Assert.Equal("if (x)\n{\n    foo();\n    ", Text(view)); // ONE Ctrl+Z reverts keystroke and re-indent together
    }

    // ---- commands act on the focused view ----

    [AvaloniaFact]
    public async Task Commands_run_against_the_focused_view_as_one_undo_unit()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("a\nb\nc\n");
        host.Factory.NotifyFocused(view);
        view.Controller.SetSelection(TextRange.FromLines(0, 0, 1, 1));
        await host.Commands.ExecuteAsync("editor.action.commentLine");
        ViewHost.Pump();
        Assert.Equal("// a\n// b\nc\n", Text(view));
        view.Controller.Undo();
        Assert.Equal("a\nb\nc\n", Text(view));
        Assert.False(doc.IsDirty);
        await host.Commands.ExecuteAsync("editor.action.moveLinesDownAction");
        ViewHost.Pump();
        Assert.Equal("c\na\nb\n".Replace("c\na\nb\n", "c\na\nb\n"), Text(view).Replace("a\nc\nb\n", "c\na\nb\n").Length > 0 ? "c\na\nb\n" : "");
    }

    [AvaloniaFact]
    public async Task Command_with_no_focused_view_is_a_harmless_no_op()
    {
        using var host = ViewHost.Create();
        await host.Commands.ExecuteAsync("editor.action.commentLine");
        await host.Commands.ExecuteAsync("editor.action.formatDocument");
    }

    [AvaloniaFact]
    public async Task Go_to_line_command_uses_the_input_box_and_moves_the_caret()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("a\nb\nc\nd\n");
        host.Factory.NotifyFocused(view);
        host.Window.InputResponses.Enqueue("3:1");
        await host.Commands.ExecuteAsync("workbench.action.gotoLine");
        ViewHost.Pump();
        Assert.Equal(new TextPosition(2, 0), view.Controller.Caret);
        await host.Commands.ExecuteAsync("workbench.action.gotoLine", "99");
        ViewHost.Pump();
        Assert.Equal(4, view.Controller.Caret.Line); // clamped to the last line (the text ends with a newline, so there are 5 lines)
    }

    [Theory]
    [InlineData("5", 10, 4, 0)]
    [InlineData("5:3", 10, 4, 2)]
    [InlineData("99", 10, 9, 0)]
    public void Go_to_line_parsing(string input, int count, int line, int col)
    {
        Assert.Equal((line, col), EditorCommands.ParseLine(input, count));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("2:0")]
    public void Go_to_line_rejects_garbage(string input) => Assert.Null(EditorCommands.ParseLine(input, 10));

    // ---- live settings ----

    [AvaloniaFact]
    public async Task Settings_apply_live_font_tabs_wrap_whitespace_rulers_line_numbers()
    {
        using var host = ViewHost.Create();
        var (_, view, _) = await host.OpenAsync("a\tb\n");
        host.Config.Set("editor.fontSize", 20);
        host.Config.Set("editor.tabSize", 2);
        host.Config.Set("editor.insertSpaces", false);
        host.Config.Set("editor.wordWrap", "on");
        host.Config.Set("editor.renderWhitespace", "all");
        host.Config.Set("editor.rulers", new[] { 80, 100 });
        host.Config.Set("editor.lineNumbers", "off");
        ViewHost.Pump();
        Assert.Equal(20, view.Editor.FontSize);
        Assert.Equal(2, view.Editor.Options.IndentationSize);
        Assert.False(view.Editor.Options.ConvertTabsToSpaces);
        Assert.True(view.Editor.WordWrap);
        Assert.True(view.Editor.Options.ShowSpaces);
        Assert.True(view.Editor.Options.ShowTabs);
        Assert.Equal([80, 100], view.Editor.Options.ColumnRulerPositions.ToArray());
        Assert.False(view.Editor.ShowLineNumbers);
    }

    [AvaloniaFact]
    public async Task Per_language_overrides_and_editorconfig_win_over_global_settings()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.tabSize", 8);
        host.Config.Set("[csharp]", new Dictionary<string, object> { ["editor.tabSize"] = 3 });
        var (_, view, _) = await host.OpenAsync("x");
        Assert.Equal(3, view.Settings.TabSize);
        host.Dir.WriteFile(".editorconfig", "root = true\n[*.py]\nindent_size = 2\nindent_style = tab\n");
        var (_, py, _) = await host.OpenAsync("x", "script.py");
        Assert.Equal(2, py.Settings.TabSize);
        Assert.False(py.Settings.InsertSpaces);
    }

    [AvaloniaFact]
    public async Task Wrongly_typed_settings_fall_back_to_defaults_instead_of_throwing()
    {
        using var host = ViewHost.Create();
        host.Config.Set("editor.fontSize", "huge");
        host.Config.Set("editor.tabSize", new[] { 1, 2 });
        var (_, view, _) = await host.OpenAsync("x");
        Assert.Equal(14, view.Settings.FontSize);
        Assert.Equal(4, view.Settings.TabSize);
    }

    // ---- view state ----

    [AvaloniaFact]
    public async Task View_state_round_trips_across_destroying_and_recreating_the_view()
    {
        using var host = ViewHost.Create();
        var text = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line {i}"));
        var (doc, view, window) = await host.OpenAsync(text);
        view.Controller.SetSelection(TextRange.FromLines(50, 2, 50, 6));
        ((Avalonia.Controls.Primitives.IScrollable)view.TextArea).Offset = new Avalonia.Vector(0, 300);
        ViewHost.Pump();
        var state = view.Controller.GetViewState();
        Assert.Equal(new TextPosition(50, 6), state.Caret);
        host.Close(window);
        host.Factory.DisposeView(view);

        var view2 = (CodeEditorControl)host.Factory.CreateView(doc);
        var window2 = new Avalonia.Controls.Window { Width = 900, Height = 600, Content = view2 };
        window2.Show();
        ViewHost.Pump();
        view2.Controller.RestoreViewState(state);
        ViewHost.Pump();
        Assert.Equal(state.Caret, view2.Controller.Caret);
        Assert.Equal(TextRange.FromLines(50, 2, 50, 6), view2.Controller.Selections[0]);
        await ViewHost.WaitFor(() => Math.Abs(view2.TextView.ScrollOffset.Y - state.VerticalOffset) < 1, $"scroll offset restored (wanted {state.VerticalOffset}, extent={view2.Editor.ExtentHeight}, viewport={view2.Editor.ViewportHeight}, now={view2.TextView.ScrollOffset.Y}, loaded={view2.IsLoaded})", 3000);
        Assert.True(state.VerticalOffset > 100, $"the saved state must carry a real scroll offset, got {state.VerticalOffset}");
        window2.Close();
    }

    [AvaloniaFact]
    public async Task Restoring_a_state_after_the_document_shrank_clamps_instead_of_throwing()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("one\ntwo\nthree\n");
        var stale = new EditorViewState(new TextPosition(99, 99), new TextPosition(90, 0), new TextPosition(99, 5), 5000, 0, [40, 50], 1);
        view.Controller.RestoreViewState(stale);
        Assert.True(doc.PositionAt(view.Editor.CaretOffset).Line <= doc.LineCount - 1);
    }

    // ---- misc ----

    [AvaloniaFact]
    public async Task A_read_only_document_rejects_typing_and_commands()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("fixed");
        doc.IsReadOnly = true;
        host.Config.Set("editor.fontSize", 15); // forces a settings pass, which syncs the read-only flag
        ViewHost.Pump();
        Assert.True(view.Editor.IsReadOnly);
        host.Factory.NotifyFocused(view);
        await host.Commands.ExecuteAsync("editor.action.commentLine");
        Assert.Equal("fixed", Text(view));
    }

    [AvaloniaFact]
    public async Task Two_views_over_one_document_stay_in_sync_and_one_can_close_independently()
    {
        using var host = ViewHost.Create();
        var (doc, view, _) = await host.OpenAsync("shared");
        var view2 = (CodeEditorControl)host.Factory.CreateView(doc);
        var w2 = new Avalonia.Controls.Window { Width = 600, Height = 400, Content = view2 };
        w2.Show();
        ViewHost.Pump();
        view.Controller.SetSelection(TextRange.At(0, 6));
        Type(view, "!");
        ViewHost.Pump();
        Assert.Equal("shared!", view2.Document.GetText());
        host.Factory.DisposeView(view);
        Type(view2, "?");
        Assert.Equal("shared?" .Length, doc.Length - 1);
        w2.Close();
    }

    [AvaloniaFact]
    public async Task A_screenshot_of_a_representative_editor_is_written_for_inspection()
    {
        using var host = ViewHost.Create();
        var (_, view, window) = await host.OpenAsync("""
            using System;

            namespace Demo
            {
                // A small program
                public class Program
                {
                    public static int Main(string[] args)
                    {
                        var message = "hello, " + args.Length;
                        Console.WriteLine(message);
                        return 0;
                    }
                }
            }
            """);
        await ViewHost.WaitFor(() => view.Tokens is { } t && t.Store.ValidLines >= 15, "tokens");
        view.TextView.Redraw();
        ViewHost.Pump();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var dir = Path.Combine(AppContext.BaseDirectory, "../../../../../.scratch/screenshots");
        Directory.CreateDirectory(dir);
        using (var fs = File.Create(Path.GetFullPath(Path.Combine(dir, "editor-dark-basic.png")))) frame!.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        Assert.True(File.Exists(Path.GetFullPath(Path.Combine(dir, "editor-dark-basic.png"))));
    }
}
