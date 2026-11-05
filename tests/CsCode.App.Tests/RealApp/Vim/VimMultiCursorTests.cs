using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Text;
using Xunit;

namespace CsCode.App.Tests.RealApp.Vim;

/// <summary>
/// Multi-cursor with Vim on, in the REAL window (real key events, the real keymap and adapter): carets made by Insert Cursor Below, by gb and
/// by code; <c>i a A I o O</c> entering Insert mode at every caret; typing, Backspace and Enter at every caret; Esc back to Normal mode with the
/// carets kept, a second Esc leaving one; Visual mode over every selection with <c>c</c> and <c>d</c>; one <c>u</c> for a change made at every
/// caret; and every other key collapsing to the primary first.
/// </summary>
public class VimMultiCursorTests
{
    private const string Quiet = "\"editor.quickSuggestions\": false";

    /// <summary>Ctrl+Alt+Down, ⌘⌥↓ on macOS (Insert Cursor Below, both presets).</summary>
    private static string InsertCursorBelow => OperatingSystem.IsMacOS() ? "<D-A-Down>" : "<C-A-Down>";

    private static int Carets(VimApp app) => app.View.Modal.Controller.Selections.Count;

    private static TextPosition[] CaretPositions(VimApp app) => app.View.Modal.Carets.Select(c => c.Active).ToArray();

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Insert_cursor_below_then_i_types_at_every_caret_Esc_keeps_them_and_one_u_undoes_it()
    {
        await using var app = await VimApp.StartAsync("alpha\nbeta\ngamma\n", settings: Quiet);
        await app.KeysAsync(InsertCursorBelow);
        await app.KeysAsync(InsertCursorBelow);
        Assert.True(await RealAppHost.WaitFor(() => Carets(app) == 3), $"carets: {Carets(app)}");
        Assert.Equal("-- NORMAL --", app.ModeText);

        await app.KeysAsync("i>> ");
        Assert.Equal(">> alpha\n>> beta\n>> gamma\n", app.Text);
        Assert.Equal("-- INSERT --", app.ModeText);
        await app.KeysAsync("<Esc>");
        Assert.Equal("-- NORMAL --", app.ModeText);
        Assert.Equal([new TextPosition(0, 2), new TextPosition(1, 2), new TextPosition(2, 2)], CaretPositions(app)); // one left, as Vim's cursor
        await app.KeysAsync("<Esc>");
        Assert.Equal(1, Carets(app)); // the second Esc leaves one caret (VSCodeVim)

        await app.KeysAsync("u");
        Assert.Equal("alpha\nbeta\ngamma\n", app.Text); // the insert at three carets was one change
        await app.KeysAsync("<C-r>");
        Assert.Equal(">> alpha\n>> beta\n>> gamma\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task A_and_o_enter_insert_mode_at_every_caret_where_vim_would()
    {
        await using var app = await VimApp.StartAsync("one\n  two\n", settings: Quiet);
        await app.KeysAsync(InsertCursorBelow);
        Assert.True(await RealAppHost.WaitFor(() => Carets(app) == 2));
        await app.KeysAsync("A;<Esc>");
        Assert.Equal("one;\n  two;\n", app.Text);
        Assert.Equal(2, Carets(app));
        await app.KeysAsync("ox<Esc>");
        Assert.Equal("one;\nx\n  two;\n  x\n", app.Text); // each new line with its own line's indentation
        await app.KeysAsync("u");
        Assert.Equal("one;\n  two;\n", app.Text);
        await app.KeysAsync("u");
        Assert.Equal("one\n  two\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task I_a_and_O_place_every_caret_as_vim_places_its_cursor()
    {
        await using var app = await VimApp.StartAsync("  ab\n  cd\n", settings: Quiet);
        app.View.Modal.Controller.SetSelections([TextRange.At(0, 3), TextRange.At(1, 3)]);
        await app.KeysAsync("I#<Esc>");
        Assert.Equal("  #ab\n  #cd\n", app.Text);
        Assert.Equal(2, Carets(app));
        await app.KeysAsync("a!<Esc>"); // after the character under each caret ("#")
        Assert.Equal("  #!ab\n  #!cd\n", app.Text);
        await app.KeysAsync("O-<Esc>");
        Assert.Equal("  -\n  #!ab\n  -\n  #!cd\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Backspace_and_Enter_in_insert_mode_act_at_every_caret_in_the_same_change()
    {
        await using var app = await VimApp.StartAsync("ab\ncd\n", settings: Quiet);
        app.View.Modal.Controller.SetSelections([TextRange.At(0, 0), TextRange.At(1, 0)]);
        await app.KeysAsync("A<BS>X<CR>y<Esc>");
        Assert.Equal("aX\ny\ncX\ny\n", app.Text);
        await app.KeysAsync("u");
        Assert.Equal("ab\ncd\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Gb_selects_the_word_then_every_next_occurrence_in_visual_mode_and_c_changes_them_all()
    {
        await using var app = await VimApp.StartAsync("foo bar foo baz foo\n", settings: Quiet);
        await app.KeysAsync("gb");
        Assert.True(await RealAppHost.WaitFor(() => app.ModeText == "-- VISUAL --"), $"mode: {app.ModeText}");
        await app.KeysAsync("gb");
        await app.KeysAsync("gb");
        Assert.True(await RealAppHost.WaitFor(() => Carets(app) == 3), $"carets: {Carets(app)}");
        Assert.Equal("-- VISUAL --", app.ModeText);
        Assert.All(app.View.Modal.Controller.Selections, s => Assert.Equal("foo", app.View.Document.GetText(s)));

        await app.KeysAsync("cqux<Esc>");
        Assert.Equal("qux bar qux baz qux\n", app.Text);
        Assert.Equal("-- NORMAL --", app.ModeText);
        Assert.Equal(3, Carets(app));
        await app.KeysAsync("<Esc>u");
        Assert.Equal("foo bar foo baz foo\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task D_in_visual_mode_deletes_every_selection_and_Esc_keeps_a_caret_in_each()
    {
        await using var app = await VimApp.StartAsync("xx a xx b xx\n", settings: Quiet);
        await app.KeysAsync("gbgbgb");
        Assert.True(await RealAppHost.WaitFor(() => Carets(app) == 3), $"carets: {Carets(app)}");
        await app.KeysAsync("<Esc>");
        Assert.Equal("-- NORMAL --", app.ModeText);
        Assert.Equal([new TextPosition(0, 1), new TextPosition(0, 6), new TextPosition(0, 11)], CaretPositions(app)); // on each last character
        await app.KeysAsync("<Esc>gbgbgb");
        Assert.True(await RealAppHost.WaitFor(() => Carets(app) == 3), $"carets: {Carets(app)}");
        await app.KeysAsync("d");
        Assert.Equal(" a  b \n", app.Text);
        Assert.Equal("-- NORMAL --", app.ModeText);
        await app.KeysAsync("<Esc>u");
        Assert.Equal("xx a xx b xx\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Motions_with_counts_move_every_caret_by_vims_own_rules()
    {
        await using var app = await VimApp.StartAsync("foo.bar baz\nab cd, ef\n", settings: Quiet);
        app.View.Modal.Controller.SetSelections([TextRange.At(0, 0), TextRange.At(1, 0)]);
        await app.KeysAsync("w");
        Assert.Equal([new TextPosition(0, 3), new TextPosition(1, 3)], CaretPositions(app)); // "." is a word of its own; "cd"
        await app.KeysAsync("2w");
        Assert.Equal([new TextPosition(0, 8), new TextPosition(1, 7)], CaretPositions(app));
        await app.KeysAsync("$");
        Assert.Equal([new TextPosition(0, 10), new TextPosition(1, 8)], CaretPositions(app)); // on each last character
        await app.KeysAsync("0l");
        Assert.Equal([new TextPosition(0, 1), new TextPosition(1, 1)], CaretPositions(app));
        await app.KeysAsync("<Right>^");
        Assert.Equal([new TextPosition(0, 0), new TextPosition(1, 0)], CaretPositions(app));
        await app.KeysAsync("<End>");
        Assert.Equal([new TextPosition(0, 10), new TextPosition(1, 8)], CaretPositions(app));
        Assert.Equal(2, Carets(app));
        Assert.Equal("foo.bar baz\nab cd, ef\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task X_and_dd_delete_at_every_caret_in_one_change()
    {
        await using var app = await VimApp.StartAsync("abc\nabc\nabc\nkeep\n", settings: Quiet);
        app.View.Modal.Controller.SetSelections([TextRange.At(0, 1), TextRange.At(1, 1), TextRange.At(2, 2)]);
        await app.KeysAsync("x");
        Assert.Equal("ac\nac\nab\nkeep\n", app.Text);
        Assert.Equal([new TextPosition(0, 1), new TextPosition(1, 1), new TextPosition(2, 1)], CaretPositions(app)); // the last was at the line's end
        await app.KeysAsync("2x");
        Assert.Equal("a\na\na\nkeep\n", app.Text);
        await app.KeysAsync("u");
        Assert.Equal("ac\nac\nab\nkeep\n", app.Text); // one u for every caret's x
        app.View.Modal.Controller.SetSelections([TextRange.At(0, 0), TextRange.At(2, 0)]);
        await app.KeysAsync("dd");
        Assert.Equal("ac\nkeep\n", app.Text);
        Assert.Equal(2, Carets(app));
        await app.KeysAsync("u");
        Assert.Equal("ac\nac\nab\nkeep\n", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Dd_deletes_a_line_shared_by_two_carets_once_and_the_last_line_without_leaving_an_empty_one()
    {
        await using var app = await VimApp.StartAsync("one\ntwo\nthree\nfour", settings: Quiet);
        app.View.Modal.Controller.SetSelections([TextRange.At(0, 0), TextRange.At(0, 2), TextRange.At(3, 1)]);
        await app.KeysAsync("dd");
        Assert.Equal("two\nthree", app.Text);
        Assert.Equal([new TextPosition(0, 0), new TextPosition(1, 0)], CaretPositions(app));
        await app.KeysAsync("u");
        Assert.Equal("one\ntwo\nthree\nfour", app.Text);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Any_other_normal_mode_key_collapses_to_the_primary_and_runs_there()
    {
        await using var app = await VimApp.StartAsync("abc\nabc\nabc\n", settings: Quiet);
        app.View.Modal.Controller.SetSelections([TextRange.At(0, 1), TextRange.At(1, 1), TextRange.At(2, 1)]);
        await app.KeysAsync("~");
        Assert.Equal(1, Carets(app));
        Assert.Equal("aBc\nabc\nabc\n", app.Text);
    }
}
