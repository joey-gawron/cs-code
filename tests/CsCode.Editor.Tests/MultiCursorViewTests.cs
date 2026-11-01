using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaEdit.Editing;
using CsCode.Core.Languages;
using CsCode.Core.Platform;
using CsCode.Core.Text;
using CsCode.Editor.Documents;
using CsCode.Editor.Editing;
using CsCode.Editor.Tests.Support;
using CsCode.Editor.View;
using CsCode.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CsCode.Editor.Tests.Support.ViewTestHelpers;

namespace CsCode.Editor.Tests;

/// <summary>
/// Multi-cursor in real (headless) views: each command (Add Next Occurrence, Select All Occurrences, Move to Next Occurrence, Insert Cursor
/// Above/Below, Cursor Undo, Remove Secondary Cursors), Alt+Click, typing and the editing keys at every caret as one undo step, the clipboard,
/// carets following edits made elsewhere, collapsing, the limit, Vim and read-only documents, drawing and disposal.
/// </summary>
public class MultiCursorViewTests
{
    private static RawInputModifiers Word => OperatingSystem.IsMacOS() ? RawInputModifiers.Alt : RawInputModifiers.Control;

    private static async Task Run(ViewHost host, string command)
    {
        await host.Commands.ExecuteAsync(command);
        ViewHost.Pump();
    }

    private static async Task<(ViewHost Host, EditorDocument Doc, CodeEditorControl View, Avalonia.Controls.Window Window)> Open(string text, Action<IServiceCollection>? more = null, string file = "test.txt")
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

    // ------------------------------------------------------------------ Add Next Occurrence

    [AvaloniaFact]
    public async Task Add_next_occurrence_selects_the_word_then_adds_each_next_whole_word_match_and_typing_replaces_them_all_in_one_undo_step()
    {
        var (host, doc, view, window) = await Open("foo food foo bar foo");
        using var _ = host;
        view.Controller.SetSelection(TextRange.At(0, 1));
        await Run(host, MultiCursorCommands.AddSelectionToNextFindMatch);
        Assert.Equal(TextRange.FromLines(0, 0, 0, 3), view.Controller.Selections.Single()); // the word under the caret
        await Run(host, MultiCursorCommands.AddSelectionToNextFindMatch);
        Assert.True(view.MultiCursor.IsActive);
        Assert.Equal([TextRange.FromLines(0, 9, 0, 12), TextRange.FromLines(0, 0, 0, 3)], view.Controller.Selections); // "food" skipped; new primary first
        await Run(host, MultiCursorCommands.AddSelectionToNextFindMatch);
        Assert.Equal(3, view.Controller.Selections.Count);

        TypeKeys(window, "XY");
        Assert.Equal("XY food XY bar XY", doc.GetText());
        Assert.Equal([2, 10, 17], Actives(view));

        view.Controller.Undo(); // the typed run is one step
        ViewHost.Pump();
        Assert.Equal("foo food foo bar foo", doc.GetText());
        Assert.Equal(3, view.Controller.Selections.Count); // the selections come back with the text
        Assert.All(view.Controller.Selections, s => Assert.Equal("foo", doc.GetText(s)));
        view.Controller.Redo();
        ViewHost.Pump();
        Assert.Equal("XY food XY bar XY", doc.GetText());
        Assert.Equal([2, 10, 17], Actives(view));
    }

    [AvaloniaFact]
    public async Task Add_next_occurrence_from_a_selection_matches_text_ignoring_case_wraps_around_and_stops_when_all_are_selected()
    {
        var (host, _, view, _) = await Open("ab AB xab ab");
        using var _h = host;
        view.Controller.SetSelection(TextRange.FromLines(0, 10, 0, 12)); // the last "ab"
        for (var i = 0; i < 6; i++) await Run(host, MultiCursorCommands.AddSelectionToNextFindMatch);
        var starts = view.MultiCursor.Current.Carets.Select(c => c.Start).ToArray();
        Assert.Equal([0, 3, 7, 10], starts); // wrapped to the start; "AB" (case) and "xab" (not a whole word) included
    }

    [AvaloniaFact]
    public async Task Move_selection_to_next_find_match_moves_the_primary_instead_of_adding()
    {
        var (host, _, view, _) = await Open("x = 1; x = 2; x = 3;");
        using var _h = host;
        view.Controller.SetSelection(TextRange.At(0, 0));
        await Run(host, MultiCursorCommands.AddSelectionToNextFindMatch); // selects "x"
        await Run(host, MultiCursorCommands.AddSelectionToNextFindMatch); // adds the second
        await Run(host, MultiCursorCommands.MoveSelectionToNextFindMatch); // the second moves to the third
        Assert.Equal([0, 14], view.MultiCursor.Current.Carets.Select(c => c.Start));
        Assert.Equal(14, view.MultiCursor.Current.Primary.Start);
    }

    // ------------------------------------------------------------------ Select All Occurrences

    [AvaloniaFact]
    public async Task Select_all_occurrences_selects_every_match_of_the_word_at_the_caret_and_Escape_collapses_to_the_primary()
    {
        var (host, doc, view, window) = await Open("let a = b;\nlet c = a + a;\nlet aa = 1;");
        using var _ = host;
        view.Controller.SetSelection(TextRange.At(1, 8)); // the first "a" of line 2
        await Run(host, MultiCursorCommands.SelectHighlights);
        var selections = view.MultiCursor.Current.Carets;
        Assert.Equal(3, selections.Count); // whole words only: "aa" is not one
        Assert.All(selections, c => Assert.Equal("a", doc.GetText(new TextRange(doc.PositionAt(c.Start), doc.PositionAt(c.End)))));
        Assert.Equal(view.Document.OffsetAt(new TextPosition(1, 8)), view.MultiCursor.Current.Primary.Start);

        Press(window, Avalonia.Input.Key.Escape);
        Assert.False(view.MultiCursor.IsActive);
        Assert.Single(view.Controller.Selections);
    }

    [AvaloniaFact]
    public async Task The_cursor_limit_setting_bounds_select_all_occurrences_and_says_so()
    {
        var (host, _, view, _) = await Open(string.Concat(Enumerable.Repeat("x ", 50)));
        using var _h = host;
        host.Config.Set(MultiCursorController.LimitKey, 7);
        ViewHost.Pump();
        view.Controller.SetSelection(TextRange.At(0, 0));
        await Run(host, MultiCursorCommands.SelectHighlights);
        Assert.Equal(7, view.MultiCursor.Current.Count);
        Assert.Contains(host.Window.StatusMessages, m => m.Contains("limited to 7", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ Insert Cursor Above / Below, keys

    [AvaloniaFact]
    public async Task Insert_cursor_below_then_typing_Backspace_Enter_and_Tab_act_at_every_caret_each_as_one_undo_step()
    {
        var (host, doc, view, window) = await Open("aaa\nbbb\nccc\n");
        using var _ = host;
        view.Controller.SetSelection(TextRange.At(0, 1));
        await Run(host, MultiCursorCommands.InsertCursorBelow);
        await Run(host, MultiCursorCommands.InsertCursorBelow);
        Assert.Equal([1, 5, 9], Actives(view));

        TypeKeys(window, "-");
        Assert.Equal("a-aa\nb-bb\nc-cc\n", doc.GetText());
        Press(window, Avalonia.Input.Key.Back);
        Assert.Equal("aaa\nbbb\nccc\n", doc.GetText());
        Press(window, Avalonia.Input.Key.Delete);
        Assert.Equal("aa\nbb\ncc\n", doc.GetText());
        Press(window, Avalonia.Input.Key.Tab);
        Assert.Equal("a   a\nb   b\nc   c\n", doc.GetText());
        Press(window, Avalonia.Input.Key.Enter);
        Assert.Equal("a   \na\nb   \nb\nc   \nc\n", doc.GetText());

        // each key was its own step: four undos go back to after the typed "-"... which Backspace removed
        for (var i = 0; i < 3; i++) view.Controller.Undo();
        ViewHost.Pump();
        Assert.Equal("aaa\nbbb\nccc\n", doc.GetText());
        Assert.Equal([1, 5, 9], Actives(view));
    }

    [AvaloniaFact]
    public async Task Insert_cursor_above_adds_carets_upwards_at_the_same_column()
    {
        var (host, _, view, _) = await Open("abcdef\nab\nabcdef");
        using var _h = host;
        view.Controller.SetSelection(TextRange.At(2, 5));
        await Run(host, MultiCursorCommands.InsertCursorAbove);
        await Run(host, MultiCursorCommands.InsertCursorAbove);
        Assert.Equal([5, 9, 15], Actives(view));
        Assert.Equal(5, view.MultiCursor.Current.Primary.Active); // the top one: the next press goes on from there
    }

    [AvaloniaFact]
    public async Task Arrows_Home_End_word_moves_and_Shift_selection_move_every_caret()
    {
        var (host, doc, view, window) = await Open("  one two\n  six ten");
        using var _ = host;
        view.Controller.SetSelections([TextRange.At(0, 5), TextRange.At(1, 5)]);
        Press(window, Avalonia.Input.Key.Right);
        Assert.Equal([6, 16], Actives(view));
        Press(window, Avalonia.Input.Key.Left, Word);
        Assert.Equal([2, 12], Actives(view));
        Press(window, Avalonia.Input.Key.Home);
        Assert.Equal([0, 10], Actives(view));
        Press(window, Avalonia.Input.Key.Home);
        Assert.Equal([2, 12], Actives(view)); // smart Home toggles
        Press(window, Avalonia.Input.Key.End, RawInputModifiers.Shift);
        Assert.Equal(["one two", "six ten"], view.MultiCursor.Current.Carets.Select(c => doc.Buffer.GetText(c.Start, c.Length)));
        Press(window, Avalonia.Input.Key.Up);
        Assert.Equal([0, 9], Actives(view)); // the first line's Up goes to its start; the second caret keeps its column on line 1
    }

    [AvaloniaFact]
    public async Task Word_Backspace_and_Shift_Down_work_at_every_caret()
    {
        var (host, doc, view, window) = await Open("alpha beta\ngamma delta\nx");
        using var _ = host;
        view.Controller.SetSelections([TextRange.At(0, 10), TextRange.At(1, 11)]);
        Press(window, Avalonia.Input.Key.Back, Word);
        Assert.Equal("alpha \ngamma \nx", doc.GetText());
        Press(window, Avalonia.Input.Key.Down, RawInputModifiers.Shift);
        Assert.All(view.MultiCursor.Current.Carets, c => Assert.False(c.IsEmpty));
    }

    [AvaloniaFact]
    public async Task Shift_Backspace_deletes_at_every_caret_like_Backspace()
    {
        var (host, doc, view, window) = await Open("aaa\nbbb\nccc");
        using var _ = host;
        view.Controller.SetSelections([TextRange.At(0, 3), TextRange.At(1, 3), TextRange.At(2, 3)]);
        Press(window, Avalonia.Input.Key.Back, RawInputModifiers.Shift);
        Assert.Equal("aa\nbb\ncc", doc.GetText());
        Assert.Equal([2, 5, 8], Actives(view));
        view.Controller.Undo();
        ViewHost.Pump();
        Assert.Equal("aaa\nbbb\nccc", doc.GetText());
        Assert.Equal([3, 7, 11], Actives(view));
    }

    // ------------------------------------------------------------------ collapsing, edits from elsewhere

    [AvaloniaFact]
    public async Task A_plain_click_or_a_command_moving_the_caret_collapses_the_other_carets()
    {
        var (host, _, view, window) = await Open("one\ntwo\nthree\nfour");
        using var _h = host;
        view.Controller.SetSelections([TextRange.At(0, 0), TextRange.At(1, 0), TextRange.At(2, 0)]);
        Assert.True(view.MultiCursor.IsActive);
        var p = PointOf(window, view, view.Document.OffsetAt(new TextPosition(3, 2)));
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        ViewHost.Pump();
        Assert.False(view.MultiCursor.IsActive);

        view.Controller.SetSelections([TextRange.At(0, 0), TextRange.At(1, 0)]);
        Press(window, Avalonia.Input.Key.PageDown); // AvaloniaEdit moves the primary
        Assert.False(view.MultiCursor.IsActive);
    }

    [AvaloniaFact]
    public async Task Carets_follow_an_edit_made_elsewhere_such_as_a_language_servers()
    {
        var (host, doc, view, _) = await Open("a\nb\nc");
        using var _h = host;
        view.Controller.SetSelections([TextRange.At(1, 1), TextRange.At(2, 1)]);
        Assert.True(doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), "// header\n")]));
        ViewHost.Pump();
        Assert.True(view.MultiCursor.IsActive);
        Assert.Equal([new TextPosition(2, 1), new TextPosition(3, 1)], view.MultiCursor.Current.Carets.Select(c => doc.PositionAt(c.Active)));
    }

    // ------------------------------------------------------------------ Alt+Click, Cursor Undo

    [AvaloniaFact]
    public async Task Alt_click_adds_a_caret_alt_click_on_a_caret_removes_it_and_cursor_undo_goes_back()
    {
        var (host, _, view, window) = await Open("first line\nsecond line\nthird line");
        using var _h = host;
        view.Controller.SetSelection(TextRange.At(0, 2));
        var second = view.Document.OffsetAt(new TextPosition(1, 3));
        var third = view.Document.OffsetAt(new TextPosition(2, 4));
        AltClick(window, view, second);
        AltClick(window, view, third);
        Assert.Equal([2, second, third], Actives(view));
        Assert.Equal(third, view.MultiCursor.Current.Primary.Active);
        AltClick(window, view, second); // toggles it off
        Assert.Equal([2, third], Actives(view));

        await Run(host, MultiCursorCommands.CursorUndo);
        Assert.Equal([2, second, third], Actives(view));
        await Run(host, MultiCursorCommands.CursorUndo);
        Assert.Equal([2, second], Actives(view));
    }

    [AvaloniaFact]
    public async Task An_alt_drag_adds_no_caret_and_keeps_AvaloniaEdits_selection()
    {
        var (host, _, view, window) = await Open("abcdef\nabcdef\nabcdef");
        using var _h = host;
        var from = PointOf(window, view, 1);
        var to = PointOf(window, view, view.Document.OffsetAt(new TextPosition(2, 4)));
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.Alt);
        window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), RawInputModifiers.Alt | RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.Alt | RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left, RawInputModifiers.Alt);
        ViewHost.Pump();
        Assert.False(view.MultiCursor.IsActive);
        Assert.False(view.TextArea.Selection.IsEmpty);
        // AvaloniaEdit's box selection is Alt+drag on Linux and Windows; on macOS it wants Ctrl+Option, and Option+drag selects a stream
        if (!OperatingSystem.IsMacOS()) Assert.IsType<RectangleSelection>(view.TextArea.Selection);
    }

    private static void AltClick(Avalonia.Controls.Window window, CodeEditorControl view, int offset)
    {
        var p = PointOf(window, view, offset);
        p = new Point(p.X - 2 - view.TextView.WideSpaceWidth / 2 + 1, p.Y); // the caret position's left edge (PointOf aims a bit into the character)
        window.MouseDown(p, MouseButton.Left, RawInputModifiers.Alt);
        window.MouseUp(p, MouseButton.Left, RawInputModifiers.Alt);
        ViewHost.Pump();
    }

    // ------------------------------------------------------------------ clipboard

    [AvaloniaFact]
    public async Task Copy_at_several_carets_joins_their_selections_and_paste_spreads_one_line_per_caret()
    {
        var clipboard = new InMemoryClipboardService();
        var (host, doc, view, _) = await Open("k1 = v1\nk2 = v2\nk3 = v3\n", s => s.AddSingleton<IClipboardService>(clipboard));
        using var _h = host;
        view.Controller.SetSelections([TextRange.FromLines(0, 0, 0, 2), TextRange.FromLines(1, 0, 1, 2), TextRange.FromLines(2, 0, 2, 2)]);
        await view.Clipboard.CopyAsync();
        Assert.Equal("k1\nk2\nk3", clipboard.Content?.Text);
        Assert.Equal(ClipboardTextShape.Normal, clipboard.Content?.Shape); // not a column block

        view.Controller.SetSelections([TextRange.At(0, 7), TextRange.At(1, 7), TextRange.At(2, 7)]);
        await view.Clipboard.PasteAsync();
        Assert.Equal("k1 = v1k1\nk2 = v2k2\nk3 = v3k3\n", doc.GetText());
        Assert.Equal(3, view.MultiCursor.Current.Count); // the carets are kept, after each piece
        view.Controller.Undo();
        Assert.Equal("k1 = v1\nk2 = v2\nk3 = v3\n", doc.GetText());

        // at a single caret the copied pieces go in as plain lines
        view.Controller.SetSelection(TextRange.At(3, 0));
        await view.Clipboard.PasteAsync();
        Assert.Equal("k1 = v1\nk2 = v2\nk3 = v3\nk1\nk2\nk3", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Paste_with_a_line_count_unlike_the_caret_count_or_the_full_setting_puts_everything_at_each_caret()
    {
        var clipboard = new InMemoryClipboardService();
        var (host, doc, view, _) = await Open("a\nb\n", s => s.AddSingleton<IClipboardService>(clipboard));
        using var _h = host;
        clipboard.SetExternal("x\ny\nz");
        view.Controller.SetSelections([TextRange.At(0, 1), TextRange.At(1, 1)]);
        await view.Clipboard.PasteAsync();
        Assert.Equal("ax\ny\nz\nbx\ny\nz\n", doc.GetText());

        view.Controller.Undo();
        host.Config.Set(MultiCursorController.PasteKey, "full");
        ViewHost.Pump();
        clipboard.SetExternal("1\n2");
        view.Controller.SetSelections([TextRange.At(0, 1), TextRange.At(1, 1)]);
        await view.Clipboard.PasteAsync();
        Assert.Equal("a1\n2\nb1\n2\n", doc.GetText());
    }

    [AvaloniaFact]
    public async Task Cut_at_several_carets_removes_every_selection_and_keeps_a_caret_at_each()
    {
        var clipboard = new InMemoryClipboardService();
        var (host, doc, view, _) = await Open("one two\nthree four\n", s => s.AddSingleton<IClipboardService>(clipboard));
        using var _h = host;
        view.Controller.SetSelections([TextRange.FromLines(0, 4, 0, 7), TextRange.FromLines(1, 6, 1, 10)]);
        await view.Clipboard.CutAsync();
        Assert.Equal("one \nthree \n", doc.GetText());
        Assert.Equal("two\nfour", clipboard.Content?.Text);
        Assert.Equal([4, 11], Actives(view));
    }

    // ------------------------------------------------------------------ completion, Vim, read-only

    [AvaloniaFact]
    public async Task Accepting_a_completion_with_several_carets_completes_the_word_at_each()
    {
        var (host, doc, view, window) = await Open("\n\n");
        using var _h = host;
        host.Factory.Services.Features.Register<ICompletionProvider>(DocumentSelector.ForLanguage("plaintext"), FakeCompletionProvider.Items("Console"));
        view.Controller.SetSelections([TextRange.At(1, 0), TextRange.At(0, 0)]);
        TypeKeys(window, "Con");
        Assert.Equal("Con\nCon\n", doc.GetText());
        await ViewHost.WaitFor(() => view.Feature<CsCode.Editor.Features.CompletionFeature>()!.IsOpen, "the completion list at the primary");
        Press(window, Avalonia.Input.Key.Enter); // the list takes Enter, not the carets
        Assert.Equal("Console\nConsole\n", doc.GetText());
        Assert.Equal(2, view.MultiCursor.Current.Count);
    }

    [AvaloniaFact]
    public async Task With_Vim_attached_the_commands_add_no_carets_and_attaching_collapses_them()
    {
        var (host, _, view, _) = await Open("foo foo foo");
        using var _h = host;
        view.Controller.SetSelections([TextRange.At(0, 0), TextRange.At(0, 4)]);
        view.Modal.Attach(null);
        Assert.False(view.MultiCursor.IsActive);
        view.Controller.SetSelection(TextRange.FromLines(0, 0, 0, 3));
        await Run(host, MultiCursorCommands.SelectHighlights);
        await Run(host, MultiCursorCommands.AddSelectionToNextFindMatch);
        await Run(host, MultiCursorCommands.InsertCursorBelow);
        Assert.False(view.MultiCursor.IsActive);
        Assert.Contains(host.Window.StatusMessages, m => m.Contains("Vim", StringComparison.Ordinal));
        view.Modal.Detach();
        await Run(host, MultiCursorCommands.SelectHighlights);
        Assert.Equal(3, view.MultiCursor.Current.Count);
    }

    [AvaloniaFact]
    public async Task A_read_only_document_takes_carets_but_no_edits()
    {
        var (host, doc, view, window) = await Open("a\nb");
        using var _h = host;
        doc.IsReadOnly = true;
        view.Controller.SetSelections([TextRange.At(0, 1), TextRange.At(1, 1)]);
        TypeKeys(window, "x");
        Press(window, Avalonia.Input.Key.Back);
        Press(window, Avalonia.Input.Key.Enter);
        Assert.Equal("a\nb", doc.GetText());
        Assert.True(view.MultiCursor.IsActive);
        Press(window, Avalonia.Input.Key.Left);
        Assert.Equal([0, 2], Actives(view));
    }

    [AvaloniaFact]
    public async Task A_macro_replaying_text_and_keys_edits_at_every_caret()
    {
        var (host, doc, view, _) = await Open("ab\ncd\n");
        using var _h = host;
        view.Controller.SetSelections([TextRange.At(0, 2), TextRange.At(1, 2)]);
        // the macro player's hooks (docs/MACROS.md): typed text through the typing path, keys raised on the text area
        Assert.True(view.Controller.ReplayText("!"));
        Assert.True(view.Controller.ReplayKey(Avalonia.Input.Key.Left, KeyModifiers.None));
        Assert.True(view.Controller.ReplayText("?"));
        Assert.Equal("ab?!\ncd?!\n", doc.GetText());
        Assert.True(view.Controller.ReplayKey(Avalonia.Input.Key.Back, KeyModifiers.None));
        Assert.Equal("ab!\ncd!\n", doc.GetText());
    }

    // ------------------------------------------------------------------ scale, drawing, lifetime

    [AvaloniaFact]
    public async Task Ten_thousand_carets_type_in_one_change_event_and_one_undo_step()
    {
        var text = string.Concat(Enumerable.Repeat("item\n", 10_000));
        var (host, doc, view, window) = await Open(text);
        using var _h = host;
        view.Controller.SetSelection(TextRange.At(0, 0));
        await Run(host, MultiCursorCommands.SelectHighlights);
        Assert.Equal(10_000, view.MultiCursor.Current.Count);
        var events = 0;
        using var sub = doc.Changed.Subscribe(_ => events++);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        TypeKeys(window, "s");
        sw.Stop();
        Assert.Equal(1, events);
        Assert.Equal(string.Concat(Enumerable.Repeat("s\n", 10_000)), doc.GetText());
        Assert.True(sw.ElapsedMilliseconds < 5_000, $"typing at 10,000 carets took {sw.ElapsedMilliseconds} ms");
        view.Controller.Undo();
        Assert.Equal(text, doc.GetText());
    }

    [AvaloniaTheory]
    [InlineData(2_000)]  // above the undo snapshot limit: the carets follow the undo's changes
    [InlineData(10_000)]
    public async Task Undo_and_redo_of_a_step_typed_at_thousands_of_carets_move_the_carets_along_all_changes_at_once(int lines)
    {
        var text = string.Concat(Enumerable.Repeat("item\n", lines));
        var (host, doc, view, window) = await Open(text);
        using var _h = host;
        view.Controller.SetSelection(TextRange.At(0, 0));
        await Run(host, MultiCursorCommands.SelectHighlights);
        Assert.Equal(lines, view.MultiCursor.Current.Count);
        TypeKeys(window, "s");
        var follows = view.MultiCursor.FollowCount;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // AvaloniaEdit replays the step as one change per caret; the carets follow them once at the end (and once more when the undo's selection
        // restore asks for the selections in the middle), not once per change
        view.Controller.Undo();
        ViewHost.Pump();
        var undo = sw.ElapsedMilliseconds;
        Assert.Equal(text, doc.GetText());
        Assert.True(view.MultiCursor.FollowCount - follows <= 2, $"the carets followed {view.MultiCursor.FollowCount - follows} times");
        Assert.True(view.MultiCursor.IsActive);
        Assert.All(view.MultiCursor.Current.Carets, c => Assert.Equal(4, doc.PositionAt(c.Active).Character)); // after each restored "item"
        follows = view.MultiCursor.FollowCount;
        sw.Restart();
        view.Controller.Redo();
        ViewHost.Pump();
        var redo = sw.ElapsedMilliseconds;
        Assert.Equal(string.Concat(Enumerable.Repeat("s\n", lines)), doc.GetText());
        Assert.True(view.MultiCursor.FollowCount - follows <= 2, $"the carets followed {view.MultiCursor.FollowCount - follows} times");
        Assert.All(view.MultiCursor.Current.Carets, c => Assert.Equal(1, doc.PositionAt(c.Active).Character));
        Assert.True(undo < 3_000 && redo < 3_000, $"undo took {undo} ms, redo {redo} ms at {lines} carets");
    }

    [AvaloniaFact]
    public async Task Carets_follow_a_batch_of_edits_made_elsewhere_in_one_update()
    {
        var (host, doc, view, _) = await Open("a b c d\ne f g h");
        using var _h = host;
        view.Controller.SetSelections([TextRange.At(0, 3), TextRange.At(0, 7), TextRange.At(1, 1), TextRange.FromLines(1, 4, 1, 7)]);
        var follows = view.MultiCursor.FollowCount;
        // a formatter's edits: applied back to front in one update
        Assert.True(doc.ApplyEdits([TextEdit.Insert(new TextPosition(0, 0), ">> "), new TextEdit(TextRange.FromLines(0, 4, 0, 5), "CCC"), TextEdit.Delete(TextRange.FromLines(1, 2, 1, 3))]));
        ViewHost.Pump();
        Assert.Equal(1, view.MultiCursor.FollowCount - follows);
        Assert.Equal(">> a b CCC d\ne  g h", doc.GetText());
        Assert.Equal(
            [new TextPosition(0, 6), new TextPosition(0, 12), new TextPosition(1, 1), new TextPosition(1, 6)],
            view.MultiCursor.Current.Carets.Select(c => doc.PositionAt(c.Active)));
        Assert.Equal(new TextPosition(1, 3), doc.PositionAt(view.MultiCursor.Current.Carets[^1].Anchor)); // a selection keeps its anchor
    }

    [AvaloniaFact]
    public async Task Secondary_carets_are_drawn_blink_while_focused_and_stop_with_the_view()
    {
        var (host, _, view, window) = await Open("one\ntwo\nthree");
        using var _h = host;
        view.Controller.SetSelections([TextRange.At(0, 0), TextRange.At(1, 0), TextRange.At(2, 0)]);
        ViewHost.Pump();
        Assert.Equal(2, view.MultiCursor.VisibleSecondaryCount);
        Assert.True(view.MultiCursor.BlinkRunning);
        // the secondary caret on line 3 is on screen in the caret colour
        var frame = window.CaptureRenderedFrame()!;
        var caret = PointOf(window, view, view.Document.OffsetAt(new TextPosition(2, 0)));
        var region = new PixelRect((int)caret.X - 6, (int)caret.Y - 6, 10, 12);
        var color = ((Avalonia.Media.ISolidColorBrush)view.Palette.Caret).Color;
        Assert.True(CountPixels(frame, color, 40, region) > 3, "a secondary caret drawn at line 3");

        view.MultiCursor.Collapse();
        Assert.False(view.MultiCursor.BlinkRunning);
        view.Controller.SetSelections([TextRange.At(0, 0), TextRange.At(1, 0)]);
        Assert.True(view.MultiCursor.BlinkRunning);
        host.Factory.DisposeView(view);
        Assert.False(view.MultiCursor.BlinkRunning);
    }
}
