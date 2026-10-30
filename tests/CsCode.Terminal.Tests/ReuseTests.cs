using CsCode.Terminal.Emulator;
using Xunit;

namespace CsCode.Terminal.Tests;

/// <summary>A reused terminal (the next debug run, the next task) continues below the previous program's output, never over it.</summary>
public sealed class ReuseTests
{
    [Fact]
    public void The_reset_before_a_reused_terminals_next_program_keeps_the_cursor_below_the_output()
    {
        var e = new VtEmulator(60, 10);
        e.Feed("first\r\nsecond\r\nthird\r\n[The terminal process exited with code 0.]");
        e.PrepareForNextProgram();
        e.Feed("next program");
        var screen = e.ScreenText().Split('\n');
        Assert.Equal(["first", "second", "third", "[The terminal process exited with code 0.]", "next program"], screen.Take(5));
        // already on a fresh line: no blank line in between
        var fresh = new VtEmulator(60, 10);
        fresh.Feed("done\r\n");
        fresh.PrepareForNextProgram();
        fresh.Feed("next");
        Assert.Equal("done\nnext", fresh.ScreenText().TrimEnd('\n'));
    }

    [Fact]
    public void A_program_that_left_the_alternate_screen_on_is_taken_back_to_the_primary_one()
    {
        var e = new VtEmulator(40, 10);
        e.Feed("output\r\n\e[?1049h\e[2J\e[Hfull screen app");
        e.PrepareForNextProgram();
        e.Feed("next");
        Assert.False(e.IsAlternateScreen);
        Assert.Contains("output", e.ScreenText());
        Assert.Contains("next", e.ScreenText());
        Assert.DoesNotContain("full screen app", e.ScreenText());
    }
}
