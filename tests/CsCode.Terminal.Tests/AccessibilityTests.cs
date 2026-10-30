using CsCode.Terminal.Emulator;
using Xunit;

namespace CsCode.Terminal.Tests;

/// <summary>
/// The screen-reader text model: the visible rows as text (soft wraps joined, blanks trimmed, the cursor's offset) and the accessible buffer
/// (scrollback and screen, prompts marked by shell integration, the newest text kept when it is cut, a full-screen program's screen alone).
/// </summary>
public sealed class AccessibilityTests
{
    [Fact]
    public void The_visible_text_joins_wrapped_rows_trims_blanks_and_places_the_cursor()
    {
        var e = new VtEmulator(10, 4);
        e.Feed("$ echo 0123456789xyz\r\n0123456789xyz\r\n$ ");
        var (text, cursor) = TerminalAccessibility.VisibleText(e.CreateSnapshot(1));
        // 4 rows: "$ echo 012" "3456789xyz"(wrapped) ... the first line scrolled away partly; the screen's own lines are joined
        Assert.DoesNotContain("  \n", text);
        Assert.EndsWith("0123456789xyz\n$ ", text);
        Assert.Equal(text.Length, cursor); // the cursor after the prompt's blank
        Assert.Equal("$ ", text[text.LastIndexOf('\n')..].TrimStart('\n'));
    }

    [Fact]
    public void A_cursor_past_the_written_cells_and_wide_characters_get_the_right_offset()
    {
        var e = new VtEmulator(20, 3);
        e.Feed("漢字 ok\e[1;12H");
        var (text, cursor) = TerminalAccessibility.VisibleText(e.CreateSnapshot(1));
        Assert.StartsWith("漢字 ok", text);
        // columns 0-1 and 2-3 are the two wide characters (one UTF-16 unit each), column 11 is offset 9
        Assert.Equal(9, cursor);
        Assert.Equal(new string(' ', 9 - "漢字 ok".Length), text.Substring("漢字 ok".Length, 9 - "漢字 ok".Length));
    }

    [Fact]
    public void The_accessible_buffer_holds_the_scrollback_marks_prompts_and_keeps_the_newest_text()
    {
        var e = new VtEmulator(20, 4);
        for (var i = 0; i < 6; i++) e.Feed($"\e]633;A\a$ \e]633;B\acmd{i}\r\n\e]633;C\aout{i}\r\n\e]633;D;0\a");
        e.Feed("\e]633;A\a$ ");
        var buffer = e.AccessibleBuffer();
        Assert.StartsWith("$ cmd0\nout0\n$ cmd1", buffer.Text);
        Assert.EndsWith("$ cmd5\nout5\n$ ", buffer.Text);
        Assert.Equal(buffer.Text.Length, buffer.CursorOffset);
        Assert.Equal(7, buffer.PromptOffsets.Count);
        Assert.All(buffer.PromptOffsets, p => Assert.Equal('$', buffer.Text[p]));
        Assert.False(buffer.Truncated);

        var cut = e.AccessibleBuffer(maxChars: 30);
        Assert.True(cut.Truncated);
        Assert.True(cut.Text.Length <= 30);
        Assert.EndsWith("$ cmd5\nout5\n$ ", cut.Text);
        Assert.Equal(cut.Text.Length, cut.CursorOffset);
        Assert.All(cut.PromptOffsets, p => Assert.Equal('$', cut.Text[p]));
    }

    [Fact]
    public void A_full_screen_program_shows_only_its_screen()
    {
        var e = new VtEmulator(20, 3);
        e.Feed("shell history\r\n\e[?1049h\e[Hvim screen\e[2;1Hline 2");
        var buffer = e.AccessibleBuffer();
        Assert.Equal("vim screen\nline 2", buffer.Text);
        Assert.Empty(buffer.PromptOffsets);
    }

    [Fact]
    public void Reading_the_buffer_does_not_keep_row_copies_of_the_scrollback()
    {
        var e = new VtEmulator(20, 3, new EmulatorOptions { ScrollbackLines = 500 });
        e.Feed(string.Concat(Enumerable.Range(0, 400).Select(i => $"line {i}\r\n")));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var first = e.AccessibleBuffer();
        Assert.Contains("line 0\nline 1\n", first.Text);
        // the scrollback lines' cached immutable copies stay as they were (none made)
        Assert.All(Enumerable.Range(0, e.ScrollbackCount), i => Assert.False(e.Scrollback.LineAt(i).HasSnapshot));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000);
    }
}
