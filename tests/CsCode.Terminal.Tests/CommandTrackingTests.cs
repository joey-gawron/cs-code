using CsCode.Terminal.Emulator;
using Xunit;

namespace CsCode.Terminal.Tests;

/// <summary>
/// The emulator's side of shell integration: commands recorded from the OSC 633 A/B/E/C/D marks (the reported line only with the
/// terminal's nonce, else the line read from the screen), their output across soft-wrapped rows, OSC 633 value escaping, and bounds.
/// </summary>
public sealed class CommandTrackingTests
{
    private static VtEmulator Emulator(int columns = 40, int rows = 10) => new(columns, rows) { ShellIntegrationNonce = "n0nce" };

    private static void Command(VtEmulator e, string line, string output, int exitCode, string? reported = null, string? nonce = "n0nce")
    {
        e.Feed("\e]633;A\a$ \e]633;B\a" + line + "\r\n");
        if (reported is not null) e.Feed($"\e]633;E;{reported};{nonce}\a");
        e.Feed("\e]633;C\a" + output + $"\e]633;D;{exitCode}\a");
    }

    [Fact]
    public void A_finished_command_has_its_reported_line_output_and_exit_code()
    {
        var e = Emulator();
        TerminalCommand? recorded = null;
        e.CommandRecorded = c => recorded = c;
        Command(e, "ls -1", "a.txt\r\nb.txt\r\n", 0, reported: "ls -1");
        Assert.NotNull(recorded);
        Assert.Same(recorded, e.LastCommand);
        Assert.Equal("ls -1", recorded.CommandLine);
        Assert.Equal(0, recorded.ExitCode);
        Assert.Equal("a.txt\nb.txt", e.CommandOutput());
        Assert.True(e.HasShellIntegration);
        Assert.Equal(0, recorded.PromptLine);
        Assert.Equal(1, recorded.OutputStartLine);
    }

    [Fact]
    public void Without_a_trusted_report_the_line_is_read_from_the_screen()
    {
        var e = Emulator();
        Command(e, "echo hi", "hi\r\n", 0);
        Assert.Equal("echo hi", e.LastCommand!.CommandLine);
        // a program printing its own E (wrong or no nonce) cannot claim the line
        Command(e, "printf x", "x\r\n", 1, reported: "rm -rf /", nonce: "forged");
        Assert.Equal("printf x", e.LastCommand!.CommandLine);
        Assert.Equal(1, e.LastCommand.ExitCode);
        Command(e, "true", "", 0, reported: "sudo reboot", nonce: null);
        Assert.Equal("true", e.LastCommand!.CommandLine);
        // no nonce configured at all (a shell CsCode did not inject): nothing reported is trusted
        var plain = new VtEmulator(40, 10);
        Command(plain, "date", "today\r\n", 0, reported: "date", nonce: "");
        Assert.Equal("date", plain.LastCommand!.CommandLine);
    }

    [Fact]
    public void Output_of_soft_wrapped_rows_is_one_line_and_long_command_lines_from_the_screen_too()
    {
        var e = Emulator(columns: 10);
        Command(e, "echo 0123456789abcdefghij", "0123456789abcdefghij  end\r\nnext\r\n", 0);
        Assert.Equal("0123456789abcdefghij  end\nnext", e.CommandOutput());
        Assert.Equal("echo 0123456789abcdefghij", e.LastCommand!.CommandLine);
    }

    [Fact]
    public void Only_the_tail_is_read_when_asked_for_a_few_rows()
    {
        var e = Emulator(rows: 5);
        Command(e, "seq 1 50", string.Concat(Enumerable.Range(1, 50).Select(i => $"{i}\r\n")), 0);
        Assert.Equal(string.Join('\n', Enumerable.Range(1, 50)), e.CommandOutput());
        Assert.Equal("48\n49\n50", e.CommandOutput(maxRows: 4));
        Assert.Equal("49\n50", e.CommandOutput(maxChars: 5));
    }

    [Theory]
    [InlineData(@"plain", "plain")]
    [InlineData(@"a\x3bb", "a;b")]
    [InlineData(@"back\\slash", @"back\slash")]
    [InlineData(@"line\x0anext", "line\nnext")]
    [InlineData(@"\xc3\xa9t\xc3\xa9", "été")]
    [InlineData(@"ünïcode", "ünïcode")]
    [InlineData(@"trailing\", @"trailing\")]
    [InlineData(@"bad\xzz", @"bad\xzz")]
    public void Osc_633_values_are_unescaped(string escaped, string expected) =>
        Assert.Equal(expected, VtEmulator.UnescapeShellIntegrationValue(escaped));

    [Fact]
    public void The_reported_working_directory_is_unescaped()
    {
        var e = Emulator();
        string? reported = null;
        e.CurrentDirectoryChanged = d => reported = d;
        e.Feed(@"e]633;P;Cwd=/tmp/a\x3bb\\c d".Replace("e]", "\e]", StringComparison.Ordinal) + "\a");
        Assert.Equal(@"/tmp/a;b\c d", reported);
        Assert.Equal(@"/tmp/a;b\c d", e.CurrentDirectory);
    }

    [Fact]
    public void A_huge_reported_command_line_is_capped()
    {
        var e = Emulator();
        Command(e, "x", "", 0, reported: new string('y', 100_000));
        Assert.Equal(VtEmulator.MaxCommandLineLength, e.LastCommand!.CommandLine!.Length);
    }

    [Fact]
    public void Marks_on_the_alternate_screen_record_nothing()
    {
        var e = Emulator();
        e.Feed("\e[?1049h");
        Command(e, "vim", "", 0, reported: "vim");
        Assert.Null(e.LastCommand);
        e.Feed("\e[?1049l");
        Command(e, "ls", "f\r\n", 0, reported: "ls");
        Assert.Equal("ls", e.LastCommand!.CommandLine);
    }
}
