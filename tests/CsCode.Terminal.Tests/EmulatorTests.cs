using System.Text;
using CsCode.Terminal.Emulator;
using Xunit;

namespace CsCode.Terminal.Tests;

/// <summary>The emulator's sequences one by one (xterm semantics, as xterm.js implements them), on small screens.</summary>
public sealed class EmulatorTests
{
    private static VtEmulator Term(int cols = 10, int rows = 5, EmulatorOptions? options = null) => new(cols, rows, options);

    private static string Row(VtEmulator t, int r) => t.GetScreenRow(r).GetText();

    private static CellAttributes Attr(VtEmulator t, int r, int c) => t.GetScreenRow(r)[c].Attributes;

    // ------------------------------------------------------------------ printing and wrapping

    [Fact]
    public void Text_prints_and_wraps_with_a_pending_wrap_at_the_margin()
    {
        var t = Term(5, 3);
        t.Feed("abcde");
        Assert.Equal((4, 0), (t.CursorX, t.CursorY));
        Assert.True(t.PendingWrap);
        t.Feed("f");
        Assert.Equal("abcde\nf\n", t.ScreenText());
        Assert.True(t.GetScreenRow(1).IsWrapped);
        Assert.Equal((1, 1), (t.CursorX, t.CursorY));
    }

    [Fact]
    public void Carriage_return_at_the_margin_clears_the_pending_wrap()
    {
        var t = Term(5, 3);
        t.Feed("abcde\r\nx");
        Assert.Equal("abcde\nx\n", t.ScreenText());
        Assert.False(t.GetScreenRow(1).IsWrapped);
    }

    [Fact]
    public void Without_autowrap_the_last_column_is_overwritten()
    {
        var t = Term(5, 2);
        t.Feed("\e[?7labcdefg");
        Assert.Equal("abcdg\n", t.ScreenText());
        Assert.Equal(4, t.CursorX);
    }

    [Fact]
    public void Wide_characters_take_two_cells_and_wrap_when_they_do_not_fit()
    {
        var t = Term(5, 3);
        t.Feed("ab中");
        Assert.True(t.GetScreenRow(0)[2].IsWide);
        Assert.True(t.GetScreenRow(0)[3].IsSpacer);
        Assert.Equal(4, t.CursorX);
        t.Feed("c文");
        Assert.Equal("ab中c\n文\n", t.ScreenText());
        Assert.Equal(2, t.CursorX);
    }

    [Fact]
    public void Overwriting_half_of_a_wide_character_erases_the_other_half()
    {
        var t = Term(6, 2);
        t.Feed("中文\e[1;2Hx");
        Assert.Equal(" x文", Row(t, 0));
        t.Feed("\e[1;3Hy");
        Assert.Equal(" xy", Row(t, 0));
    }

    [Fact]
    public void Combining_marks_join_the_previous_cell()
    {
        var t = Term(10, 2);
        t.Feed("éx");
        Assert.Equal("éx", Row(t, 0));
        Assert.Equal(2, t.CursorX);
        t.Feed("\r\n\U0001F468‍\U0001F469");
        Assert.Equal("\U0001F468‍\U0001F469", t.GetScreenRow(1).CellText(0));
    }

    [Fact]
    public void A_hostile_stream_of_combining_marks_cannot_grow_a_cell()
    {
        var t = Term(10, 2);
        t.Feed("a" + string.Concat(Enumerable.Repeat("́", 10_000)));
        Assert.True(t.GetScreenRow(0).CellText(0).Length <= 32);
    }

    [Fact]
    public void Dec_special_graphics_draw_lines_and_si_so_switch_sets()
    {
        var t = Term(10, 2);
        t.Feed("\e(0lqqk\e(B x");
        Assert.Equal("┌──┐ x", Row(t, 0));
        t.Feed("\r\n\e)0\x0eq\x0fq");
        Assert.Equal("─q", Row(t, 1));
    }

    [Fact]
    public void Rep_repeats_the_last_character()
    {
        var t = Term(10, 2);
        t.Feed("a\e[3b");
        Assert.Equal("aaaa", Row(t, 0));
    }

    // ------------------------------------------------------------------ cursor

    [Fact]
    public void Cursor_movement_is_clamped_to_the_screen()
    {
        var t = Term(10, 5);
        t.Feed("\e[3;4H");
        Assert.Equal((3, 2), (t.CursorX, t.CursorY));
        t.Feed("\e[10A");
        Assert.Equal(0, t.CursorY);
        t.Feed("\e[20C");
        Assert.Equal(9, t.CursorX);
        t.Feed("\e[2D\e[2B");
        Assert.Equal((7, 2), (t.CursorX, t.CursorY));
        t.Feed("\e[5G\e[4d");
        Assert.Equal((4, 3), (t.CursorX, t.CursorY));
        t.Feed("\e[E");
        Assert.Equal((0, 4), (t.CursorX, t.CursorY));
        t.Feed("\e[2F");
        Assert.Equal((0, 2), (t.CursorX, t.CursorY));
        t.Feed("\e[0;0H");
        Assert.Equal((0, 0), (t.CursorX, t.CursorY));
    }

    [Fact]
    public void Origin_mode_positions_relative_to_the_scroll_region()
    {
        var t = Term(10, 6);
        t.Feed("\e[2;4r\e[?6h\e[1;1Hx\e[9;1Hy");
        Assert.Equal("x", Row(t, 1));
        Assert.Equal("y", Row(t, 3));
        t.Feed("\e[6n");
        // reported relative to the region in origin mode
    }

    [Fact]
    public void Save_and_restore_cursor_keep_position_and_rendition()
    {
        var t = Term(10, 5);
        t.Feed("\e[2;3H\e[1;31m\e7\e[0m\e[5;5H\e8x");
        Assert.Equal((3, 1), (t.CursorX, t.CursorY));
        Assert.True(Attr(t, 1, 2).Has(CellFlags.Bold));
        Assert.Equal(TermColor.Indexed(1), Attr(t, 1, 2).Foreground);
    }

    [Fact]
    public void Backspace_from_the_pending_wrap_moves_left_of_the_margin()
    {
        var t = Term(5, 2);
        t.Feed("abcde\bX");
        Assert.Equal("abcXe", Row(t, 0));
    }

    [Fact]
    public void Tabs_stop_every_eight_columns_and_can_be_set_and_cleared()
    {
        var t = Term(30, 2);
        t.Feed("\tx");
        Assert.Equal(9, t.CursorX);
        t.Feed("\r\e[3g\e[4G\eH\r\tY");
        Assert.Equal("   Y    x", Row(t, 0));
        t.Feed("\r\e[3g\t");
        Assert.Equal(29, t.CursorX);
        t.Feed("\e[4G\eH\e[20G\e[Z");
        Assert.Equal(3, t.CursorX);
    }

    // ------------------------------------------------------------------ erasing and editing

    [Fact]
    public void Erase_in_display_and_line()
    {
        var t = Term(5, 3);
        t.Feed("aaaaabbbbbccccc\e[2;3H\e[K");
        Assert.Equal("aaaaa\nbb\nccccc", t.ScreenText());
        t.Feed("\e[1K");
        Assert.Equal("aaaaa\n\nccccc", t.ScreenText());
        t.Feed("\e[1;1H\e[2;2H\e[J");
        Assert.Equal("aaaaa\n\n", t.ScreenText());
        t.Feed("\e[2J");
        Assert.Equal("\n\n", t.ScreenText());
    }

    [Fact]
    public void Erase_uses_the_current_background()
    {
        var t = Term(5, 2);
        t.Feed("\e[44m\e[2J\e[0m");
        Assert.Equal(TermColor.Indexed(4), Attr(t, 1, 4).Background);
    }

    [Fact]
    public void Insert_delete_and_erase_characters()
    {
        var t = Term(8, 2);
        t.Feed("abcdef\e[1;3H\e[2@");
        Assert.Equal("ab  cdef", Row(t, 0));
        t.Feed("\e[3P");
        Assert.Equal("abdef", Row(t, 0));
        t.Feed("\e[2X");
        Assert.Equal("ab  f", Row(t, 0));
    }

    [Fact]
    public void Insert_mode_shifts_text_right()
    {
        var t = Term(8, 2);
        t.Feed("abcd\e[1;2H\e[4hXY\e[4lZ");
        Assert.Equal("aXYZcd", Row(t, 0));
    }

    [Fact]
    public void Insert_and_delete_lines_inside_the_scroll_region()
    {
        var t = Term(3, 5);
        t.Feed("1\r\n2\r\n3\r\n4\r\n5\e[2;4r\e[3;1H\e[L");
        Assert.Equal("1\n2\n\n3\n5", t.ScreenText());
        t.Feed("\e[2M");
        Assert.Equal("1\n2\n\n\n5", t.ScreenText());
        t.Feed("\e[5;1H\e[L"); // outside the region: ignored
        Assert.Equal("1\n2\n\n\n5", t.ScreenText());
    }

    [Fact]
    public void Scroll_up_and_down_and_reverse_index()
    {
        var t = Term(3, 4);
        t.Feed("1\r\n2\r\n3\r\n4\e[S");
        Assert.Equal("2\n3\n4\n", t.ScreenText());
        t.Feed("\e[2T");
        Assert.Equal("\n\n2\n3", t.ScreenText());
        t.Feed("\e[1;1H\eM");
        Assert.Equal("\n\n\n2", t.ScreenText());
    }

    [Fact]
    public void Line_feed_at_the_bottom_of_a_region_scrolls_only_the_region()
    {
        var t = Term(3, 5);
        t.Feed("a\r\nb\r\nc\r\nd\r\ne\e[2;4r\e[4;1H\nX");
        Assert.Equal("a\nc\nd\nX\ne", t.ScreenText());
        Assert.Equal(0, t.ScrollbackCount); // the region did not start at the top: nothing enters the scrollback
    }

    // ------------------------------------------------------------------ scrollback

    [Fact]
    public void Lines_scrolled_off_the_top_enter_the_scrollback_with_absolute_numbers()
    {
        var t = Term(5, 3, new EmulatorOptions { ScrollbackLines = 4 });
        for (var i = 0; i < 10; i++) t.Feed($"L{i}\r\n");
        Assert.Equal(8, t.ScreenTop);
        Assert.Equal(4, t.ScrollbackCount);
        Assert.Equal(4, t.ScrollbackTop);
        Assert.Equal("L4", t.GetRow(4)!.GetText());
        Assert.Equal("L7", t.GetRow(7)!.GetText());
        Assert.Equal("L8", t.GetRow(8)!.GetText());
        Assert.Null(t.GetRow(3));
    }

    [Fact]
    public void The_scrollback_respects_its_memory_cap()
    {
        var t = Term(200, 3, new EmulatorOptions { ScrollbackLines = 100_000, ScrollbackMaxBytes = 1024 * 1024 });
        var line = new string('x', 200);
        for (var i = 0; i < 5000; i++) t.Feed(line + "\r\n");
        Assert.True(t.Scrollback.Bytes <= 1024 * 1024, $"{t.Scrollback.Bytes} bytes");
        Assert.True(t.ScrollbackCount < 5000);
    }

    [Fact]
    public void Erase_scrollback_clears_it()
    {
        var t = Term(5, 2);
        t.Feed("a\r\nb\r\nc\r\n");
        Assert.True(t.ScrollbackCount > 0);
        t.Feed("\e[3J");
        Assert.Equal(0, t.ScrollbackCount);
    }

    [Fact]
    public void The_viewport_follows_output_unless_the_user_scrolled_back()
    {
        var t = Term(5, 2);
        for (var i = 0; i < 6; i++) t.Feed($"{i}\r\n");
        Assert.Equal(t.ScreenTop, t.CreateSnapshot(1).ViewportTop);
        t.ScrollViewportBy(-3);
        var top = t.ViewportTop;
        t.Feed("more\r\nlines\r\n");
        var snap = t.CreateSnapshot(2);
        Assert.Equal(top, snap.ViewportTop); // the user keeps reading
        Assert.False(snap.IsAtBottom);
        t.ScrollViewportToBottom();
        Assert.True(t.CreateSnapshot(3).IsAtBottom);
    }

    // ------------------------------------------------------------------ SGR

    [Fact]
    public void Sgr_basic_256_and_truecolor_with_both_separators()
    {
        var t = Term(20, 2);
        t.Feed("\e[1;3;4;9mA\e[0;38;5;200mB\e[48;2;1;2;3mC\e[0;38:2::10:20:30mD\e[38:5:21mE\e[0;91;102mF\e[0m");
        var a = Attr(t, 0, 0);
        Assert.True(a.Has(CellFlags.Bold) && a.Has(CellFlags.Italic) && a.Has(CellFlags.Strikethrough));
        Assert.Equal(UnderlineStyle.Single, a.Underline);
        Assert.Equal(TermColor.Indexed(200), Attr(t, 0, 1).Foreground);
        Assert.Equal(TermColor.Rgb(1, 2, 3), Attr(t, 0, 2).Background);
        Assert.Equal(TermColor.Rgb(10, 20, 30), Attr(t, 0, 3).Foreground);
        Assert.Equal(TermColor.Indexed(21), Attr(t, 0, 4).Foreground);
        Assert.Equal(TermColor.Indexed(9), Attr(t, 0, 5).Foreground);
        Assert.Equal(TermColor.Indexed(10), Attr(t, 0, 5).Background);
        Assert.Equal(CellAttributes.Default, t.CurrentAttributes);
    }

    [Fact]
    public void Sgr_underline_styles_and_colour_and_resets()
    {
        var t = Term(20, 2);
        t.Feed("\e[4:3;58:2::255:0:0mA\e[21mB\e[24mC\e[2;5;7;8;53mD\e[22;25;27;28;55mE");
        Assert.Equal(UnderlineStyle.Curly, Attr(t, 0, 0).Underline);
        Assert.Equal(TermColor.Rgb(255, 0, 0), Attr(t, 0, 0).UnderlineColor);
        Assert.Equal(UnderlineStyle.Double, Attr(t, 0, 1).Underline);
        Assert.Equal(UnderlineStyle.None, Attr(t, 0, 2).Underline);
        var d = Attr(t, 0, 3);
        Assert.True(d.Has(CellFlags.Dim) && d.Has(CellFlags.Blink) && d.Has(CellFlags.Inverse) && d.Has(CellFlags.Invisible) && d.Has(CellFlags.Overline));
        var e = Attr(t, 0, 4);
        Assert.False(e.Has(CellFlags.Dim) || e.Has(CellFlags.Blink) || e.Has(CellFlags.Inverse) || e.Has(CellFlags.Invisible) || e.Has(CellFlags.Overline));
    }

    [Fact]
    public void Truncated_extended_colours_do_not_throw_or_misread_the_rest()
    {
        var t = Term(10, 2);
        t.Feed("\e[38;5mA\e[38;2;1;2mB\e[38mC\e[48:2mD");
        Assert.Equal("ABCD", Row(t, 0));
    }

    // ------------------------------------------------------------------ modes, screens, reports

    [Fact]
    public void Alternate_screen_1049_saves_the_cursor_starts_blank_and_restores_the_primary()
    {
        var t = Term(10, 3);
        t.Feed("primary\e[2;3H\e[?1049h");
        Assert.True(t.IsAlternateScreen);
        Assert.Equal("\n\n", t.ScreenText());
        t.Feed("\e[1;1Halt");
        t.Feed("\e[?1049l");
        Assert.False(t.IsAlternateScreen);
        Assert.Equal("primary\n\n", t.ScreenText());
        Assert.Equal((2, 1), (t.CursorX, t.CursorY));
    }

    [Fact]
    public void The_alternate_screen_has_no_scrollback()
    {
        var t = Term(5, 2);
        t.Feed("\e[?1049h");
        for (var i = 0; i < 10; i++) t.Feed("x\r\n");
        t.Feed("\e[?1049l");
        Assert.Equal(0, t.ScrollbackCount);
    }

    [Fact]
    public void Modes_are_published_for_input_encoding()
    {
        var t = Term();
        var v = t.ModesVersion;
        t.Feed("\e[?1h\e=\e[?2004h\e[?1004h\e[?1002h\e[?1006h");
        Assert.True(t.ModesVersion > v);
        var m = t.Modes;
        Assert.True(m.ApplicationCursorKeys && m.ApplicationKeypad && m.BracketedPaste && m.FocusEvents);
        Assert.Equal(MouseTracking.ButtonEvent, m.Mouse);
        Assert.Equal(MouseEncoding.Sgr, m.MouseEncoding);
        t.Feed("\e[?1l\e>\e[?2004l\e[?1002l");
        Assert.False(t.Modes.ApplicationCursorKeys || t.Modes.ApplicationKeypad || t.Modes.BracketedPaste);
        Assert.Equal(MouseTracking.None, t.Modes.Mouse);
    }

    [Fact]
    public void Cursor_visibility_and_shape()
    {
        var t = Term();
        t.Feed("\e[?25l\e[5 q");
        Assert.False(t.CursorVisible);
        Assert.Equal(CursorShape.Bar, t.CursorShape);
        Assert.True(t.CursorBlink);
        t.Feed("\e[?25h\e[2 q");
        Assert.True(t.CursorVisible);
        Assert.Equal(CursorShape.Block, t.CursorShape);
        Assert.False(t.CursorBlink);
    }

    [Fact]
    public void Reports_answer_status_position_device_attributes_and_modes()
    {
        var t = Term(10, 5);
        var answers = new List<string>();
        t.Respond = b => answers.Add(Encoding.UTF8.GetString(b));
        t.Feed("\e[5n\e[3;4H\e[6n\e[c\e[>c\e[?2004h\e[?2004$p\e[18t");
        Assert.Equal(["\e[0n", "\e[3;4R", "\e[?1;2c", "\e[>0;276;0c", "\e[?2004;1$y", "\e[8;5;10t"], answers);
    }

    [Fact]
    public void Osc_colour_queries_answer_from_the_theme_palette()
    {
        var t = Term(10, 2, new EmulatorOptions { Palette = new FixedPalette() });
        var answers = new List<string>();
        t.Respond = b => answers.Add(Encoding.UTF8.GetString(b));
        t.Feed("\e]10;?\a\e]11;?\e\\\e]4;1;?\a");
        Assert.Equal(["\e]10;rgb:1111/2222/3333\e\\", "\e]11;rgb:4444/5555/6666\e\\", "\e]4;1;rgb:abab/cdcd/efef\e\\"], answers);
    }

    private sealed class FixedPalette : ITerminalPalette
    {
        public int Foreground => 0x112233;
        public int Background => 0x445566;
        public int Cursor => 0x778899;
        public int Ansi(int index) => index == 1 ? 0xABCDEF : 0;
    }

    [Fact]
    public void Title_cwd_and_hyperlinks()
    {
        var t = Term(20, 2);
        string? title = null, cwd = null;
        t.TitleChanged = s => title = s;
        t.CurrentDirectoryChanged = s => cwd = s;
        t.Feed("\e]0;my\x01 title\a\e]7;file://host/tmp/a%20b\e\\");
        Assert.Equal("my title", title);
        Assert.Equal("/tmp/a b", cwd);
        t.Feed("x\e]8;;https://example.com\e\\link\e]8;;\e\\y");
        var row = t.GetScreenRow(0);
        Assert.Equal(0, row[0].Attributes.LinkId);
        Assert.Equal("https://example.com", row.Link(row[1].Attributes.LinkId));
        Assert.Equal("https://example.com", row.Link(row[4].Attributes.LinkId));
        Assert.Equal(0, row[5].Attributes.LinkId);
    }

    [Fact]
    public void Osc_52_writes_the_clipboard_only_when_allowed()
    {
        string? written = null;
        var off = Term();
        off.ClipboardWrite = s => written = s;
        off.Feed("\e]52;c;aGVsbG8=\a");
        Assert.Null(written);
        var on = Term(options: new EmulatorOptions { AllowOsc52 = true });
        on.ClipboardWrite = s => written = s;
        on.Feed("\e]52;c;aGVsbG8=\a\e]52;c;?\a");
        Assert.Equal("hello", written);
    }

    [Fact]
    public void Shell_integration_marks_prompts_with_their_commands_exit_codes_and_navigates_between_them()
    {
        var t = Term(20, 4, new EmulatorOptions { ScrollbackLines = 100 });
        int? finished = -99;
        t.CommandFinished = c => finished = c;
        // three commands as VS Code's bash integration reports them: A prompt, B command line, C executed, D;code finished
        for (var i = 0; i < 3; i++)
            t.Feed($"\e]633;A\a$ \e]633;B\acmd{i}\r\n\e]633;C\aoutput {i}\r\nmore {i}\r\n\e]633;D;{i}\a");
        t.Feed("\e]633;A\a$ \e]633;B\a");
        Assert.Equal(2, finished);
        var prompts = Enumerable.Range(0, (int)(t.ScreenTop + t.Rows)).Select(a => (a, Row: t.GetRow(a)))
            .Where(x => x.Row is { Mark: RowMark.PromptStart }).ToList();
        Assert.Equal(4, prompts.Count);
        Assert.Equal([0, 1, 2], prompts.Take(3).Select(p => p.Row!.MarkExitCode!.Value));
        Assert.Null(prompts[3].Row!.MarkExitCode); // the current prompt has not run anything yet
        Assert.Equal("$ cmd1", prompts[1].Row!.GetText());

        t.ScrollToCommand(-1);
        Assert.Equal(prompts[2].a, t.ViewportTop);
        t.ScrollToCommand(-1);
        Assert.Equal(prompts[1].a, t.ViewportTop);
        t.ScrollToCommand(+1);
        Assert.Equal(prompts[2].a, t.ViewportTop);
        t.ScrollToCommand(+1);
        t.ScrollToCommand(+1);
        Assert.True(t.FollowOutput);
    }

    [Fact]
    public void Find_searches_scrollback_and_screen_both_ways_wrapping_around_with_case_and_wide_characters()
    {
        var t = Term(20, 3, new EmulatorOptions { ScrollbackLines = 100 });
        t.Feed("alpha one\r\nbeta ONE\r\n漢字 one\r\ngamma\r\ndelta");
        Assert.Equal(2, t.ScreenTop); // rows 0-1 are in the scrollback
        // backward from the bottom (VS Code's first Enter), then on from each match
        var m = t.Find("one", long.MaxValue, 0, backward: true, matchCase: false);
        Assert.Equal(new TerminalMatch(2, 5, 7), m);
        m = t.Find("one", m!.Value.Line, m.Value.StartColumn, backward: true, matchCase: false);
        Assert.Equal(new TerminalMatch(1, 5, 7), m);
        Assert.Equal(new TerminalMatch(0, 6, 8), t.Find("one", 1, 5, backward: true, matchCase: true)); // "ONE" does not match
        Assert.Equal(new TerminalMatch(2, 5, 7), t.Find("one", 0, 6, backward: true, matchCase: true)); // wraps to the bottom
        // forward
        Assert.Equal(new TerminalMatch(0, 6, 8), t.Find("one", long.MinValue, 0, backward: false, matchCase: false));
        Assert.Equal(new TerminalMatch(1, 5, 7), t.Find("ONE", 0, 6, backward: false, matchCase: false));
        Assert.Equal(new TerminalMatch(0, 6, 8), t.Find("one", 2, 5, backward: false, matchCase: false)); // wraps to the top
        // a wide character's match ends on its second cell; a lone match is found again from itself
        Assert.Equal(new TerminalMatch(2, 0, 3), t.Find("漢字", long.MinValue, 0, backward: false, matchCase: true));
        Assert.Equal(new TerminalMatch(3, 0, 4), t.Find("gamma", 3, 0, backward: false, matchCase: true));
        Assert.Null(t.Find("zeta", long.MaxValue, 0, backward: true, matchCase: false));
        Assert.Null(t.Find("", long.MaxValue, 0, backward: true, matchCase: false));
    }

    [Fact]
    public void Bell_is_reported()
    {
        var t = Term();
        var bells = 0;
        t.Bell = () => bells++;
        t.Feed("a\ab\a");
        Assert.Equal(2, bells);
    }

    [Fact]
    public void Soft_and_full_reset()
    {
        var t = Term(10, 3);
        t.Feed("text\e[?25l\e[2;3r\e[4h\e[1m\e[!p");
        Assert.True(t.CursorVisible);
        Assert.False(t.InsertMode);
        Assert.Equal(CellAttributes.Default, t.CurrentAttributes);
        Assert.Equal("text\n\n", t.ScreenText());
        t.Feed("\e[?1049h\ec");
        Assert.False(t.IsAlternateScreen);
        Assert.Equal("\n\n", t.ScreenText());
        Assert.Equal((0, 0), (t.CursorX, t.CursorY));
    }

    [Fact]
    public void Decaln_fills_the_screen()
    {
        var t = Term(3, 2);
        t.Feed("\e#8");
        Assert.Equal("EEE\nEEE", t.ScreenText());
    }

    // ------------------------------------------------------------------ resize

    [Fact]
    public void Shrinking_rows_drops_blank_rows_below_the_cursor_then_pushes_rows_to_the_scrollback()
    {
        var t = Term(5, 5);
        t.Feed("a\r\nb");
        t.Resize(5, 3);
        Assert.Equal("a\nb\n", t.ScreenText());
        Assert.Equal(0, t.ScrollbackCount);
        t.Feed("\r\nc\r\nd");
        t.Resize(5, 2);
        Assert.Equal("c\nd", t.ScreenText());
        Assert.Equal(2, t.ScrollbackCount);
        t.Resize(5, 4); // growing pulls them back
        Assert.Equal("a\nb\nc\nd", t.ScreenText());
        Assert.Equal((1, 3), (t.CursorX, t.CursorY));
    }

    [Fact]
    public void Without_reflow_columns_truncate_and_pad()
    {
        var t = Term(6, 2, new EmulatorOptions { Reflow = false });
        t.Feed("abcdef");
        t.Resize(3, 2);
        Assert.Equal("abc\n", t.ScreenText());
        t.Resize(6, 2);
        Assert.Equal("abc\n", t.ScreenText());
    }

    [Fact]
    public void Reflow_joins_and_splits_soft_wrapped_lines_and_the_cursor_follows()
    {
        var t = Term(6, 4);
        t.Feed("abcdefghij\r\nxy");
        Assert.Equal("abcdef\nghij\nxy\n", t.ScreenText());
        t.Resize(12, 4);
        Assert.Equal("abcdefghij\nxy\n\n", t.ScreenText());
        Assert.Equal((2, 1), (t.CursorX, t.CursorY));
        t.Resize(4, 4);
        Assert.Equal("abcd\nefgh\nij\nxy", t.ScreenText());
        Assert.Equal((2, 3), (t.CursorX, t.CursorY));
        t.Resize(4, 2); // the rows above go to the scrollback
        Assert.Equal("ij\nxy", t.ScreenText());
        Assert.Equal("abcd", t.GetRow(t.ScrollbackTop)!.GetText());
        Assert.Equal((2, 1), (t.CursorX, t.CursorY));
    }

    [Fact]
    public void Reflow_keeps_wide_characters_whole()
    {
        var t = Term(5, 3);
        t.Feed("a中文b");
        t.Resize(4, 3);
        Assert.Equal("a中\n文b\n", t.ScreenText());
        Assert.True(t.GetScreenRow(1).IsWrapped);
    }

    [Fact]
    public void Alternate_screen_content_is_truncated_not_reflowed()
    {
        var t = Term(6, 2);
        t.Feed("\e[?1049habcdef");
        t.Resize(3, 2);
        Assert.Equal("abc\n", t.ScreenText());
    }

    // ------------------------------------------------------------------ snapshots

    [Fact]
    public void Unchanged_rows_are_the_same_instances_in_the_next_snapshot()
    {
        var t = Term(10, 3);
        t.Feed("one\r\ntwo\r\nthree");
        var a = t.CreateSnapshot(1);
        t.Feed("\e[2;1Hxx");
        var b = t.CreateSnapshot(2);
        Assert.Same(a.Rows[0], b.Rows[0]);
        Assert.NotSame(a.Rows[1], b.Rows[1]);
        Assert.Same(a.Rows[2], b.Rows[2]);
        Assert.Equal("xxo", b.Rows[1].GetText());
    }

    [Fact]
    public void Snapshot_reports_the_cursor_only_when_its_row_is_in_view()
    {
        var t = Term(5, 2);
        for (var i = 0; i < 5; i++) t.Feed($"{i}\r\n");
        Assert.Equal(1, t.CreateSnapshot(1).CursorY);
        t.ScrollViewportBy(-3);
        Assert.Equal(-1, t.CreateSnapshot(2).CursorY);
    }
}
