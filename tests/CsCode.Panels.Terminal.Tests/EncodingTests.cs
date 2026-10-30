using System.Text;
using Avalonia.Input;
using CsCode.Panels.Terminal.Input;
using CsCode.Terminal.Emulator;
using Xunit;

namespace CsCode.Panels.Terminal.Tests;

/// <summary>Keys, text, pastes and mouse events to the bytes xterm (and xterm.js) sends.</summary>
public sealed class EncodingTests
{
    private static string Key(Key key, KeyModifiers mods = KeyModifiers.None, string? symbol = null, TerminalModes? modes = null) =>
        KeyEncoder.Encode(key, mods, symbol, modes ?? TerminalModes.Initial) is { } b ? Encoding.UTF8.GetString(b) : "<text>";

    [Theory]
    [InlineData(Avalonia.Input.Key.Up, "\e[A")]
    [InlineData(Avalonia.Input.Key.Down, "\e[B")]
    [InlineData(Avalonia.Input.Key.Right, "\e[C")]
    [InlineData(Avalonia.Input.Key.Left, "\e[D")]
    [InlineData(Avalonia.Input.Key.Home, "\e[H")]
    [InlineData(Avalonia.Input.Key.End, "\e[F")]
    [InlineData(Avalonia.Input.Key.Insert, "\e[2~")]
    [InlineData(Avalonia.Input.Key.Delete, "\e[3~")]
    [InlineData(Avalonia.Input.Key.PageUp, "\e[5~")]
    [InlineData(Avalonia.Input.Key.PageDown, "\e[6~")]
    [InlineData(Avalonia.Input.Key.F1, "\eOP")]
    [InlineData(Avalonia.Input.Key.F4, "\eOS")]
    [InlineData(Avalonia.Input.Key.F5, "\e[15~")]
    [InlineData(Avalonia.Input.Key.F12, "\e[24~")]
    [InlineData(Avalonia.Input.Key.Enter, "\r")]
    [InlineData(Avalonia.Input.Key.Back, "\x7f")]
    [InlineData(Avalonia.Input.Key.Tab, "\t")]
    [InlineData(Avalonia.Input.Key.Escape, "\e")]
    public void Special_keys(Key key, string expected) => Assert.Equal(expected, Key(key));

    [Fact]
    public void Cursor_keys_in_application_mode_use_ss3()
    {
        var app = TerminalModes.Initial with { ApplicationCursorKeys = true };
        Assert.Equal("\eOA", Key(Avalonia.Input.Key.Up, modes: app));
        Assert.Equal("\eOH", Key(Avalonia.Input.Key.Home, modes: app));
        Assert.Equal("\e[1;5A", Key(Avalonia.Input.Key.Up, KeyModifiers.Control, modes: app)); // modifiers always use CSI
    }

    [Theory]
    [InlineData(KeyModifiers.Shift, "\e[1;2C")]
    [InlineData(KeyModifiers.Alt, "\e[1;3C")]
    [InlineData(KeyModifiers.Control, "\e[1;5C")]
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift, "\e[1;6C")]
    public void Modifiers_are_encoded_xterm_style(KeyModifiers mods, string expected) => Assert.Equal(expected, Key(Avalonia.Input.Key.Right, mods));

    [Fact]
    public void Modified_function_and_editing_keys()
    {
        Assert.Equal("\e[3;5~", Key(Avalonia.Input.Key.Delete, KeyModifiers.Control));
        Assert.Equal("\e[1;2P", Key(Avalonia.Input.Key.F1, KeyModifiers.Shift));
        Assert.Equal("\e[15;3~", Key(Avalonia.Input.Key.F5, KeyModifiers.Alt));
        Assert.Equal("\e[Z", Key(Avalonia.Input.Key.Tab, KeyModifiers.Shift));
        Assert.Equal("\e\x7f", Key(Avalonia.Input.Key.Back, KeyModifiers.Alt));
        Assert.Equal("\x08", Key(Avalonia.Input.Key.Back, KeyModifiers.Control));
        Assert.Equal("\e\r", Key(Avalonia.Input.Key.Enter, KeyModifiers.Alt));
    }

    [Fact]
    public void Ctrl_letters_and_punctuation_are_c0_controls()
    {
        Assert.Equal("\x03", Key(Avalonia.Input.Key.C, KeyModifiers.Control, "c"));
        Assert.Equal("\x01", Key(Avalonia.Input.Key.A, KeyModifiers.Control, "a"));
        Assert.Equal("\x1a", Key(Avalonia.Input.Key.Z, KeyModifiers.Control, "z"));
        Assert.Equal("\0", Key(Avalonia.Input.Key.Space, KeyModifiers.Control, " "));
        Assert.Equal("\e", Key(Avalonia.Input.Key.OemOpenBrackets, KeyModifiers.Control, "["));
        Assert.Equal("\x1c", Key(Avalonia.Input.Key.OemPipe, KeyModifiers.Control, "\\"));
        Assert.Equal("\x1d", Key(Avalonia.Input.Key.OemCloseBrackets, KeyModifiers.Control, "]"));
        Assert.Equal("\x1f", Key(Avalonia.Input.Key.OemMinus, KeyModifiers.Control, "-"));
        Assert.Equal("\e\x03", Key(Avalonia.Input.Key.C, KeyModifiers.Control | KeyModifiers.Alt, "c"));
    }

    [Fact]
    public void Alt_is_meta_and_plain_text_is_left_to_text_input()
    {
        Assert.Equal("\eb", Key(Avalonia.Input.Key.B, KeyModifiers.Alt, "b"));
        Assert.Equal("\eé", Key(Avalonia.Input.Key.E, KeyModifiers.Alt, "é"));
        Assert.Equal("<text>", Key(Avalonia.Input.Key.A, KeyModifiers.None, "a"));
        Assert.Equal("<text>", Key(Avalonia.Input.Key.A, KeyModifiers.Shift, "A"));
    }

    [Fact]
    public void Application_keypad()
    {
        var kp = TerminalModes.Initial with { ApplicationKeypad = true };
        Assert.Equal("\eOq", Key(Avalonia.Input.Key.NumPad1, modes: kp));
        Assert.Equal("\eOk", Key(Avalonia.Input.Key.Add, modes: kp));
    }

    [Fact]
    public void Paste_is_bracketed_when_asked_and_cannot_close_the_bracket_early()
    {
        Assert.Equal("a\rb", Encoding.UTF8.GetString(KeyEncoder.Paste("a\nb", bracketed: false)));
        Assert.Equal("\e[200~a\rb\r\e[201~", Encoding.UTF8.GetString(KeyEncoder.Paste("a\r\nb\n", bracketed: true)));
        Assert.Equal("\e[200~xrm -rf ~\r\e[201~", Encoding.UTF8.GetString(KeyEncoder.Paste("x\e[201~rm -rf ~\n", bracketed: true)));
    }

    [Fact]
    public void Mouse_reports_in_each_encoding()
    {
        var normal = TerminalModes.Initial with { Mouse = MouseTracking.Normal };
        string? M(TerminalModes m, MouseAction a, int b, int x, int y, bool shift = false, bool alt = false, bool ctrl = false, bool held = false) =>
            MouseEncoder.Encode(m, a, b, x, y, shift, alt, ctrl, held) is { } bytes ? Encoding.Latin1.GetString(bytes) : null;

        Assert.Null(M(TerminalModes.Initial, MouseAction.Press, 0, 0, 0));
        Assert.Equal("\e[M !!", M(normal, MouseAction.Press, 0, 0, 0));
        Assert.Equal("\e[M#+5", M(normal, MouseAction.Release, 0, 10, 20));
        Assert.Equal("\e[M`!!", M(normal, MouseAction.WheelUp, 0, 0, 0));
        Assert.Null(M(normal, MouseAction.Move, 0, 1, 1, held: true)); // 1000 reports no motion
        var button = normal with { Mouse = MouseTracking.ButtonEvent };
        Assert.Equal("\e[M@\"\"", M(button, MouseAction.Move, 0, 1, 1, held: true));
        Assert.Null(M(button, MouseAction.Move, 0, 1, 1, held: false));
        var sgr = normal with { MouseEncoding = MouseEncoding.Sgr };
        Assert.Equal("\e[<0;301;41M", M(sgr, MouseAction.Press, 0, 300, 40));
        Assert.Equal("\e[<0;301;41m", M(sgr, MouseAction.Release, 0, 300, 40));
        Assert.Equal("\e[<18;1;1M", M(sgr, MouseAction.Press, 2, 0, 0, ctrl: true));
        Assert.Null(M(normal, MouseAction.Press, 0, 300, 1)); // the classic encoding cannot say column 301
        var x10 = TerminalModes.Initial with { Mouse = MouseTracking.X10 };
        Assert.Equal("\e[M !!", M(x10, MouseAction.Press, 0, 0, 0, ctrl: true)); // no modifiers in X10
        Assert.Null(M(x10, MouseAction.Release, 0, 0, 0));
    }

    [Fact]
    public void Focus_reports() => Assert.Equal("\e[I\e[O", Encoding.ASCII.GetString([.. KeyEncoder.Focus(true), .. KeyEncoder.Focus(false)]));
}
