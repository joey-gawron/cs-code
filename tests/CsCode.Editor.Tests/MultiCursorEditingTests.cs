using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Editing;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.Theming;
using CsCode.Editor.View;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

/// <summary>
/// Multi-cursor, second round, in real (headless) views: auto-closing, over-typing and auto-surround at every caret (each by its own context)
/// with one undo step per typed run; Backspace to the previous indentation stop for one caret and for many; Up/Down by wrapped rows at every
/// caret; <c>editor.multiCursorModifier</c>; all carets kept in the view state.
/// </summary>
public class MultiCursorEditingTests
{
    private static async Task<(ViewHost Host, EditorDocument Doc, CodeEditorControl View, Avalonia.Controls.Window Window)> Open(string text, string file = "test.cs", Action<IServiceCollection>? more = null)
    {
        var host = ViewHost.Create(more);
        var (doc, view, window) = await host.OpenAsync(text, file);
        host.Factory.NotifyFocused(view);
        return (host, doc, view, window);
    }

    private static int[] Actives(CodeEditorControl view) => view.MultiCursor.Current.Carets.Select(c => c.Active).ToArray();

    private static void Press(Avalonia.Controls.Window w, Key key, RawInputModifiers m = RawInputModifiers.None)
    {
        Key(w, key, m);
        ViewHost.Pump();
    }

    private static void TypeKeys(Avalonia.Controls.Window w, string text)
    {
        foreach (var ch in text) w.KeyTextInput(ch.ToString());
        ViewHost.Pump();
    }

    private static void Undo(CodeEditorControl view)
    {
        view.Controller.Undo();
        ViewHost.Pump();
    }

    // ------------------------------------------------------------------ auto-closing, over-typing, surround

    [AvaloniaFact]
    public async Task An_opener_typed_at_every_caret_closes_at_each_the_closer_is_over_typed_and_the_run_is_one_undo_step()
    {
        var (host, doc, view, window) = await Open("a\nb\nc");
        using var _ = host;
        view.Controller.SetSelections([TextRange.At(0, 1), TextRange.At(1, 1), TextRange.At(2, 1)]);
        TypeKeys(window, "(");
        Assert.Equal("a()\nb()\nc()", doc.GetText());
        Assert.Equal([2, 6, 10], Actives(view)); // between each pair
        TypeKeys(window, "x)");
        Assert.Equal("a(x)\nb(x)\nc(x)", doc.GetText()); // the closers were stepped over, not doubled
        Assert.Equal([4, 9, 14], Actives(view));
        TypeKeys(window, ";");
        Assert.Equal("a(x);\nb(x);\nc(x);", doc.GetText());

        Undo(view); // "(x);" typed without moving: one step
        Assert.Equal("a\nb\nc", doc.GetText());
        Assert.Equal([1, 3, 5], Actives(view));
        view.Controller.Redo();
        ViewHost.Pump();
        Assert.Equal("a(x);\nb(x);\nc(x);", doc.GetText());
        Assert.Equal([5, 11, 17], Actives(view));
    }

    [AvaloniaFact]
    public async Task Each_caret_decides_by_the_character_after_it()
    {
        var (host, doc, view, window) = await Open("foo\nfoobar");
        using var _ = host;
        view.Controller.SetSelections([TextRange.At(0, 3), TextRange.At(1, 3)]);
        TypeKeys(window, "(");
        // the first caret is at a line end and gets the pair; the second has a word after it, where a single caret types "(" alone
        Assert.Equal("foo()\nfoo(bar", doc.GetText());
        Assert.Equal([4, 10], Actives(view));
    }

    [AvaloniaFact]
    public async Task A_quote_pairs_in_code_but_not_in_a_comment_each_caret_by_the_token_under_it()
    {
        var (host, doc, view, window) = await Open("int x = \n// note ");
        using var _ = host;
        await ViewHost.WaitFor(() => view.Tokens?.KindAt(1, 7) == TokenKind.Comment, "the comment line is tokenised");
        view.Controller.SetSelections([TextRange.At(0, 8), TextRange.At(1, 8)]);
        TypeKeys(window, "\"");
        Assert.Equal("int x = \"\"\n// note \"", doc.GetText()); // C#'s quote pair is notIn comment
        Assert.Equal([9, 20], Actives(view));
    }

    [AvaloniaFact]
    public async Task An_opener_typed_over_several_selections_surrounds_each_and_keeps_them_selected()
    {
        var (host, doc, view, window) = await Open("one two three");
        using var _ = host;
        view.Controller.SetSelections([TextRange.FromLines(0, 0, 0, 3), TextRange.FromLines(0, 8, 0, 13)]);
        TypeKeys(window, "[");
        Assert.Equal("[one] two [three]", doc.GetText());
        Assert.Equal(["one", "three"], view.MultiCursor.Current.Carets.Select(c => doc.Buffer.GetText(c.Start, c.Length)));
        Undo(view);
        Assert.Equal("one two three", doc.GetText());
        Assert.Equal(["one", "three"], view.MultiCursor.Current.Carets.Select(c => doc.Buffer.GetText(c.Start, c.Length)));
    }

    [AvaloniaFact]
    public async Task Auto_closing_turned_off_types_plain_characters_at_every_caret()
    {
        var (host, doc, view, window) = await Open("a\nb");
        using var _ = host;
        host.Config.Set("editor.autoClosingBrackets", "never");
        ViewHost.Pump();
        view.Controller.SetSelections([TextRange.At(0, 1), TextRange.At(1, 1)]);
        TypeKeys(window, "(");
        Assert.Equal("a(\nb(", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Backspace_between_auto_inserted_pairs_at_every_caret_removes_both_characters()
    {
        var (host, doc, view, window) = await Open("x\ny");
        using var _ = host;
        view.Controller.SetSelections([TextRange.At(0, 1), TextRange.At(1, 1)]);
        TypeKeys(window, "[");
        Assert.Equal("x[]\ny[]", doc.GetText());
        Press(window, Avalonia.Input.Key.Back);
        Assert.Equal("x\ny", doc.GetText());
        Assert.Equal([1, 3], Actives(view));
    }

    [AvaloniaFact]
    public async Task A_closing_brace_typed_alone_on_a_line_at_every_caret_is_pulled_back_in_the_same_undo_step()
    {
        var (host, doc, view, window) = await Open("if (a) {\n    \n}\nif (b) {\n    \n}");
        using var _ = host;
        view.Controller.SetSelections([TextRange.At(1, 4), TextRange.At(4, 4)]);
        TypeKeys(window, "}");
        Assert.Equal("if (a) {\n}\n}\nif (b) {\n}\n}", doc.GetText());
        Assert.Equal([new TextPosition(1, 1), new TextPosition(4, 1)], view.MultiCursor.Current.Carets.Select(c => doc.PositionAt(c.Active)));
        Undo(view);
        Assert.Equal("if (a) {\n    \n}\nif (b) {\n    \n}", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Ten_thousand_carets_auto_close_in_one_change_event_and_one_undo_step()
    {
        var text = string.Concat(Enumerable.Repeat("f\n", 10_000));
        var (host, doc, view, window) = await Open(text);
        using var _ = host;
        view.Controller.SetSelection(TextRange.At(0, 0));
        await host.Commands.ExecuteAsync(MultiCursorCommands.SelectHighlights);
        ViewHost.Pump();
        Assert.Equal(10_000, view.MultiCursor.Current.Count);
        view.Controller.SetSelections(view.MultiCursor.Current.Carets.Select(c => new TextRange(doc.PositionAt(c.End), doc.PositionAt(c.End))).ToArray());
        var events = 0;
        using var sub = doc.Changed.Subscribe(_ => events++);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        TypeKeys(window, "(");
        TypeKeys(window, ")");
        sw.Stop();
        Assert.Equal(1, events); // the ")" stepped over every closer: no second change
        Assert.Equal(string.Concat(Enumerable.Repeat("f()\n", 10_000)), doc.GetText());
        Assert.True(sw.ElapsedMilliseconds < 5_000, $"typing at 10,000 carets took {sw.ElapsedMilliseconds} ms");
        Undo(view);
        Assert.Equal(text, doc.GetText());
    }

    // ------------------------------------------------------------------ Backspace to the previous indentation stop

    [AvaloniaFact]
    public async Task Backspace_in_leading_spaces_deletes_back_to_the_previous_indentation_stop_with_one_caret()
    {
        var (host, doc, view, window) = await Open("        x\n      y\nab  ");
        using var _ = host;
        view.Controller.SetSelection(TextRange.At(0, 8));
        Press(window, Avalonia.Input.Key.Back);
        Assert.Equal("    x", doc.GetLineText(0));
        Press(window, Avalonia.Input.Key.Back);
        Assert.Equal("x", doc.GetLineText(0));
        view.Controller.SetSelection(TextRange.At(1, 6));
        Press(window, Avalonia.Input.Key.Back);
        Assert.Equal("    y", doc.GetLineText(1)); // between stops: back to the stop before
        view.Controller.SetSelection(TextRange.At(2, 4));
        Press(window, Avalonia.Input.Key.Back);
        Assert.Equal("ab ", doc.GetLineText(2)); // after text: one character
        Undo(view);
        Undo(view);
        Assert.Equal("      y", doc.GetLineText(1));
    }

    [AvaloniaFact]
    public async Task Backspace_in_leading_spaces_at_every_caret_goes_back_one_stop_as_one_undo_step()
    {
        var (host, doc, view, window) = await Open("        a\n        b\n  c");
        using var _ = host;
        view.Controller.SetSelections([TextRange.At(0, 8), TextRange.At(1, 8), TextRange.At(2, 2)]);
        Press(window, Avalonia.Input.Key.Back);
        Assert.Equal("    a\n    b\nc", doc.GetText());
        Assert.Equal([4, 10, 12], Actives(view));
        Undo(view);
        Assert.Equal("        a\n        b\n  c", doc.GetText());
        Assert.Equal([8, 18, 22], Actives(view));
    }

    [AvaloniaFact]
    public async Task With_use_tab_stops_off_Backspace_deletes_one_space_for_one_caret_and_for_many()
    {
        var (host, doc, view, window) = await Open("        a\n        b");
        using var _ = host;
        host.Config.Set(TypingBehaviors.UseTabStopsKey, false);
        ViewHost.Pump();
        view.Controller.SetSelection(TextRange.At(0, 8));
        Press(window, Avalonia.Input.Key.Back);
        Assert.Equal("       a", doc.GetLineText(0));
        view.Controller.SetSelections([TextRange.At(0, 7), TextRange.At(1, 8)]);
        Press(window, Avalonia.Input.Key.Back);
        Assert.Equal("      a\n       b", doc.GetText());
    }

    // ------------------------------------------------------------------ Up / Down with word wrap

    [AvaloniaFact]
    public async Task Up_and_Down_with_word_wrap_move_every_caret_by_visual_rows_like_the_single_caret()
    {
        var words = string.Join(' ', Enumerable.Repeat("lorem ipsum", 30)); // ~350 characters: several rows at 900 px
        var (host, doc, view, window) = await Open(words + "\n" + words + "\nend", file: "test.txt");
        using var _ = host;
        host.Config.Set("editor.wordWrap", "on");
        ViewHost.Pump();
        view.TextView.EnsureVisualLines();
        Assert.True(view.TextView.GetVisualLine(1)!.TextLines.Count > 2, "the first line wraps");

        // where AvaloniaEdit's own caret goes from column 5 of each line with Down, twice
        int[] SingleCaret(int line, int presses)
        {
            var result = new int[presses];
            view.Controller.SetSelection(TextRange.At(line, 5));
            for (var i = 0; i < presses; i++)
            {
                Press(window, Avalonia.Input.Key.Down);
                result[i] = view.Editor.CaretOffset;
            }
            return result;
        }
        var first = SingleCaret(0, 2);
        var second = SingleCaret(1, 2);
        Assert.True(first[0] < doc.OffsetAt(new TextPosition(1, 0)), "one Down stays on the first line's next row");

        view.Controller.SetSelections([TextRange.At(0, 5), TextRange.At(1, 5)]);
        Press(window, Avalonia.Input.Key.Down);
        Assert.Equal([first[0], second[0]], Actives(view));
        Press(window, Avalonia.Input.Key.Down);
        Assert.Equal([first[1], second[1]], Actives(view));
        Press(window, Avalonia.Input.Key.Up);
        Press(window, Avalonia.Input.Key.Up);
        Assert.Equal([5, doc.OffsetAt(new TextPosition(1, 5))], Actives(view));
    }

    // ------------------------------------------------------------------ editor.multiCursorModifier

    [AvaloniaFact]
    public async Task With_multi_cursor_modifier_ctrlCmd_the_command_click_adds_carets_and_alt_click_does_not()
    {
        var (host, _, view, window) = await Open("first line\nsecond line\nthird line", file: "test.txt");
        using var _h = host;
        host.Config.Set(MultiCursorController.ModifierKey, "ctrlCmd");
        ViewHost.Pump();
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        view.Controller.SetSelection(TextRange.At(0, 2));
        var second = view.Document.OffsetAt(new TextPosition(1, 3));
        Click(window, view, second, command);
        Assert.Equal([2, second], Actives(view));

        var third = view.Document.OffsetAt(new TextPosition(2, 4));
        Click(window, view, third, RawInputModifiers.Alt); // the link modifier now: no caret
        Assert.False(view.MultiCursor.Current.Carets.Any(c => c.Active == third) && view.MultiCursor.Current.Count == 3);
        Assert.Equal(1, view.MultiCursor.Current.Count); // a plain move of the primary collapses as any click

        host.Config.Set(MultiCursorController.ModifierKey, "alt");
        ViewHost.Pump();
        view.Controller.SetSelection(TextRange.At(0, 2));
        Click(window, view, second, RawInputModifiers.Alt);
        Assert.Equal([2, second], Actives(view));
    }

    [Fact]
    public void The_link_modifier_is_the_one_that_does_not_add_carets()
    {
        Assert.Equal(KeyModifiers.Alt, EditorKeys.AddCaretModifier("alt", mac: false));
        Assert.Equal(KeyModifiers.Control, EditorKeys.LinkModifier("alt", mac: false));
        Assert.Equal(KeyModifiers.Meta, EditorKeys.LinkModifier("alt", mac: true));
        Assert.Equal(KeyModifiers.Control, EditorKeys.AddCaretModifier("ctrlCmd", mac: false));
        Assert.Equal(KeyModifiers.Meta, EditorKeys.AddCaretModifier("ctrlCmd", mac: true));
        Assert.Equal(KeyModifiers.Alt, EditorKeys.LinkModifier("ctrlCmd", mac: true));
        Assert.True(EditorKeys.IsLinkKey(Avalonia.Input.Key.LeftAlt, "ctrlCmd"));
    }

    [AvaloniaFact]
    public async Task With_ctrlCmd_alt_click_goes_to_the_definition_and_the_command_modifier_no_longer_does()
    {
        using var host = ViewHost.Create();
        var target = new Uri("file:///other/Target.cs");
        host.Factory.Services.Features.Register<IDefinitionProvider>(DocumentSelector.All, new FakeDefinitionProvider((_, _, _) =>
            Task.FromResult<IReadOnlyList<LocationLink>?>([new(target, TextRange.FromLines(3, 0, 4, 0), TextRange.FromLines(3, 0, 3, 6))])));
        var (_, view, window) = await host.OpenAsync("Target t;", "a.cs");
        host.Config.Set(MultiCursorController.ModifierKey, "ctrlCmd");
        ViewHost.Pump();
        var point = PointOf(window, view, 2);
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.MouseMove(point, command);
        ViewHost.Pump();
        await Task.Delay(400); // longer than the link delay
        ViewHost.Pump();
        Assert.Null(view.Editor.Cursor); // the command modifier adds carets now: no link under it

        window.MouseMove(point, RawInputModifiers.Alt);
        await ViewHost.WaitFor(() => view.Editor.Cursor is not null, "link state while Alt is held");
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.Alt);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.Alt);
        await ViewHost.WaitFor(() => host.Navigation.Opened.Count == 1, "navigated");
        Assert.Equal(target, host.Navigation.Opened[0].Uri);
        Assert.False(view.MultiCursor.IsActive);
    }

    private static void Click(Avalonia.Controls.Window window, CodeEditorControl view, int offset, RawInputModifiers modifiers)
    {
        var p = PointOf(window, view, offset);
        p = new Point(p.X - 2 - view.TextView.WideSpaceWidth / 2 + 1, p.Y); // the caret position's left edge
        window.MouseDown(p, MouseButton.Left, modifiers);
        window.MouseUp(p, MouseButton.Left, modifiers);
        ViewHost.Pump();
    }

    // ------------------------------------------------------------------ a modal layer that handles several carets (the Vim adapter)

    [AvaloniaFact]
    public async Task A_modal_layer_that_allows_several_carets_gets_them_from_the_commands_and_alt_click_and_can_hold_set_and_edit_them()
    {
        var (host, doc, view, window) = await Open("foo foo foo\nbar\nbaz", file: "test.txt");
        using var _ = host;
        var surface = view.Modal;
        surface.Attach(null);
        surface.AllowMultipleCarets = true;
        view.Controller.SetSelection(TextRange.FromLines(0, 0, 0, 3));
        await host.Commands.ExecuteAsync(MultiCursorCommands.SelectHighlights);
        ViewHost.Pump();
        Assert.Equal(3, surface.CaretCount);
        Assert.DoesNotContain(host.Window.StatusMessages, m => m.Contains("Vim", StringComparison.Ordinal));

        // held: the layer moving the primary (its cursor) keeps the others
        surface.SetCarets([new EditorCaretState(new TextPosition(1, 0), new TextPosition(1, 0)), new EditorCaretState(new TextPosition(2, 0), new TextPosition(2, 0))], primary: 0);
        using (surface.HoldCarets()) surface.SetCaret(new TextPosition(1, 2));
        Assert.Equal(2, surface.CaretCount);
        Assert.Equal(new TextPosition(1, 2), surface.Carets[surface.PrimaryCaretIndex].Active);
        // not held: a move of the primary collapses, as a click does
        surface.SetCaret(new TextPosition(1, 1));
        Assert.Equal(1, surface.CaretCount);

        // one edit per caret as one undo step, the carets where the edits say
        surface.SetCarets([new EditorCaretState(new TextPosition(1, 3), new TextPosition(1, 3)), new EditorCaretState(new TextPosition(2, 3), new TextPosition(2, 3))], primary: 1);
        surface.EditAtCarets((i, c) => (new TextRange(c.Active, c.Active), i == 0 ? "!" : "?", 1));
        Assert.Equal("foo foo foo\nbar!\nbaz?", doc.GetText());
        Assert.Equal([new TextPosition(1, 4), new TextPosition(2, 4)], surface.Carets.Select(c => c.Active));
        Assert.Equal(1, surface.PrimaryCaretIndex);
        view.Controller.Undo();
        ViewHost.Pump();
        Assert.Equal("foo foo foo\nbar\nbaz", doc.GetText());

        // Alt+Click adds a caret under the layer too; detaching turns multi-cursor off again
        surface.SetCarets([new EditorCaretState(new TextPosition(0, 0), new TextPosition(0, 0))], primary: 0);
        var at = doc.OffsetAt(new TextPosition(2, 1));
        var p = PointOf(window, view, at);
        p = new Point(p.X - 2 - view.TextView.WideSpaceWidth / 2 + 1, p.Y);
        window.MouseDown(p, MouseButton.Left, RawInputModifiers.Alt);
        window.MouseUp(p, MouseButton.Left, RawInputModifiers.Alt);
        ViewHost.Pump();
        Assert.Equal(2, surface.CaretCount);
        surface.Detach();
        Assert.False(surface.AllowMultipleCarets);
        surface.Attach(null);
        Assert.Equal(1, surface.CaretCount);
    }

    [AvaloniaFact]
    public async Task Under_a_block_caret_the_other_carets_are_drawn_as_blocks_too()
    {
        var (host, _, view, window) = await Open("one\ntwo\nthree", file: "test.txt");
        using var _h = host;
        view.Modal.Attach(null);
        view.Modal.AllowMultipleCarets = true;
        view.Modal.CaretShape = EditorCaretShape.Block;
        view.Controller.SetSelections([TextRange.At(0, 0), TextRange.At(2, 1)]);
        view.Controller.Focus();
        ViewHost.Pump();
        var at = PointOf(window, view, view.Document.OffsetAt(new TextPosition(2, 1))); // 2 px into the 'h'
        var color = ((Avalonia.Media.ISolidColorBrush)view.Palette.Caret).Color;
        // a block covers the character's cell: caret-coloured pixels right of where a 2 px line caret would end, compared with the same
        // cell without the caret (the glyph's own pixels)
        var inside = new PixelRect((int)at.X + 1, (int)at.Y - 4, 3, 8);
        var with = CountPixels(window.CaptureRenderedFrame()!, color, 90, inside);
        view.MultiCursor.Collapse();
        ViewHost.Pump();
        var without = CountPixels(window.CaptureRenderedFrame()!, color, 90, inside);
        Assert.True(with >= without + 8, $"a block caret over the 'h' of line 3: {with} caret-coloured pixels with it, {without} without");
    }

    // ------------------------------------------------------------------ view state

    [AvaloniaFact]
    public async Task A_recreated_view_gets_every_caret_back_from_the_view_state()
    {
        var (host, doc, view, window) = await Open("one\ntwo\nthree\nfour", file: "test.txt");
        using var _ = host;
        view.Controller.SetSelections([TextRange.FromLines(2, 0, 2, 3), TextRange.At(0, 1), TextRange.At(1, 2)]);
        var state = view.Controller.GetViewState();
        Assert.Equal(3, state.Carets?.Count);
        host.Factory.DisposeView(view);
        var again = host.Factory.CreateView(doc);
        window.Content = again;
        ViewHost.Pump();
        var controller = host.Factory.GetController(again)!;
        controller.RestoreViewState(state);
        ViewHost.Pump();
        Assert.Equal([TextRange.FromLines(2, 0, 2, 3), TextRange.At(0, 1), TextRange.At(1, 2)], controller.Selections);

        // and they edit as before
        controller.Focus();
        TypeKeys(window, "!");
        Assert.Equal("o!ne\ntw!o\n!ee\nfour", doc.GetText());
    }

    [AvaloniaFact]
    public async Task A_view_state_with_one_caret_has_no_caret_list()
    {
        var (host, _, view, _) = await Open("one", file: "test.txt");
        using var _h = host;
        view.Controller.SetSelection(TextRange.At(0, 1));
        Assert.Null(view.Controller.GetViewState().Carets);
    }
}
