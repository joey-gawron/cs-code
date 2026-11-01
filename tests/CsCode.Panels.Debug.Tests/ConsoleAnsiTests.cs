using CsCode.Core.Debugging;
using CsCode.Panels.Debug.Console;
using CsCode.Panels.Debug.Tests.Fakes;
using Xunit;

namespace CsCode.Panels.Debug.Tests;

/// <summary>ANSI escapes in console output: parsed into plain text + styled spans on the pump thread.</summary>
public class ConsoleAnsiTests
{
    private const string E = "\u001b";

    private static (string Text, ConsoleSpan[]? Spans) Parse(string line, AnsiParser? parser = null) => (parser ?? new AnsiParser()).ParseLine(line, ConsoleOutputPump.MaxLineChars);

    [Fact]
    public void Sgr_colours_become_spans_and_the_escapes_leave_the_text()
    {
        var (text, spans) = Parse($"{E}[31mred{E}[0m plain {E}[1;92mbold green{E}[39;22m tail");
        Assert.Equal("red plain bold green tail", text);
        Assert.NotNull(spans);
        Assert.Equal(2, spans.Length);
        Assert.Equal(new ConsoleSpan(0, 3, new ConsoleTextStyle(1, ConsoleTextStyle.Default, ConsoleTextFlags.None)), spans[0]);
        Assert.Equal(new ConsoleSpan(10, 10, new ConsoleTextStyle(10, ConsoleTextStyle.Default, ConsoleTextFlags.Bold)), spans[1]);
    }

    [Fact]
    public void Palette_true_colour_background_and_attributes_are_read()
    {
        var (text, spans) = Parse($"{E}[38;5;208;48;2;10;20;30;3;4;9mx{E}[7;2my{E}[27;22;23;24;29;49mz");
        Assert.Equal("xyz", text);
        Assert.NotNull(spans);
        var all = ConsoleTextFlags.Italic | ConsoleTextFlags.Underline | ConsoleTextFlags.Strikethrough;
        Assert.Equal(new ConsoleTextStyle(208, ConsoleTextStyle.Rgb(10, 20, 30), all), spans[0].Style);
        Assert.Equal(all | ConsoleTextFlags.Inverse | ConsoleTextFlags.Dim, spans[1].Style.Flags);
        Assert.Equal(new ConsoleTextStyle(208, ConsoleTextStyle.Default, ConsoleTextFlags.None), spans[2].Style);
        Assert.Equal(0xFF8700u, ConsoleTextStyle.ToRgb(208));
        Assert.Equal(0x0A141Eu, ConsoleTextStyle.ToRgb(ConsoleTextStyle.Rgb(10, 20, 30)));
        Assert.Equal(0x808080u, ConsoleTextStyle.ToRgb(244));
    }

    [Fact]
    public void Other_control_sequences_are_removed_and_a_cut_off_escape_is_dropped()
    {
        Assert.Equal(("ab", null), Parse($"{E}[2K{E}[1Ga{E}]0;title{E}\\b{E}(B"));
        Assert.Equal(("link", null), Parse($"{E}]8;;https://example.com{E}\\link{E}]8;;{E}\\"));
        Assert.Equal(("bel", null), Parse($"{E}]2;t\u0007bel"));
        Assert.Equal(("cut", null), Parse($"cut{E}[3"));
        Assert.Equal(("q", null), Parse($"{E}[?25lq{E}[?25h")); // private modes: removed, not colours
    }

    [Fact]
    public void A_style_carries_over_to_the_next_line_and_reset_ends_it()
    {
        var parser = new AnsiParser();
        Assert.False(parser.Needed("plain"));
        _ = Parse($"{E}[33mstart", parser);
        Assert.True(parser.Needed("no escape here"));
        var (text, spans) = Parse("continues", parser);
        Assert.Equal("continues", text);
        Assert.Equal(3, Assert.Single(spans!).Style.Foreground);
        _ = Parse($"end{E}[0m", parser);
        Assert.False(parser.Needed("plain again"));
    }

    [Fact]
    public void Long_styled_lines_are_cut_like_plain_ones()
    {
        var (text, spans) = new AnsiParser().ParseLine($"{E}[31m" + new string('x', 50) + $"{E}[0m", 10);
        Assert.Equal(new string('x', 10) + "…", text);
        Assert.Equal(new ConsoleSpan(0, 10, new ConsoleTextStyle(1, ConsoleTextStyle.Default, ConsoleTextFlags.None)), Assert.Single(spans!));
    }

    [Fact]
    public void The_pump_parses_escapes_per_category_and_plain_output_gets_no_spans()
    {
        using var pump = new ConsoleOutputPump(new ConsoleBuffer(), _ => true, _ => { });
        var s = new FakeDebugSession();
        var lines = pump.ToLines(
        [
            new DebugOutputEventArgs(s, "stdout", $"{E}[32m✓{E}[0m passed\n{E}[31mstill red\n"),
            new DebugOutputEventArgs(s, "stderr", "stderr is not red-by-stdout\n"),
            new DebugOutputEventArgs(s, "stdout", $"red continues{E}[0m\nplain\n"),
        ], 100);
        Assert.Equal(["✓ passed", "still red", "stderr is not red-by-stdout", "red continues", "plain"], lines.Select(l => l.Text));
        Assert.Equal(2, lines[0].Spans![0].Style.Foreground);
        Assert.Equal(1, Assert.Single(lines[1].Spans!).Style.Foreground);
        Assert.Null(lines[2].Spans);
        Assert.Equal(1, Assert.Single(lines[3].Spans!).Style.Foreground);
        Assert.Null(lines[4].Spans);
    }
}
