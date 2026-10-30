using System.Text;
using Avalonia.Input;
using CsCode.Terminal.Emulator;

namespace CsCode.Panels.Terminal.Input;

/// <summary>
/// Keys to the bytes xterm sends (as xterm.js encodes them): cursor keys in normal (<c>CSI A</c>) and application (<c>SS3 A</c>) mode,
/// modifiers as <c>CSI 1;m X</c> / <c>CSI n;m ~</c> (m = 1 + Shift + 2·Alt + 4·Ctrl), function keys, Ctrl+letter as C0 controls, Alt as Meta
/// (ESC prefix), Backspace as DEL, Shift+Tab as <c>CSI Z</c>. Printable text without Ctrl/Alt is not encoded here: it arrives as text input
/// (keyboard layouts, dead keys and IME compose it). Pure and allocation-light: called on the UI thread for every keystroke.
/// </summary>
public static class KeyEncoder
{
    /// <summary>The bytes for a key, or null when the key produces ordinary text (left to text input) or nothing.</summary>
    public static byte[]? Encode(Key key, KeyModifiers modifiers, string? keySymbol, TerminalModes modes)
    {
        var shift = (modifiers & KeyModifiers.Shift) != 0;
        var alt = (modifiers & KeyModifiers.Alt) != 0;
        var ctrl = (modifiers & KeyModifiers.Control) != 0;
        var mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        var app = modes.ApplicationCursorKeys;

        switch (key)
        {
            case Key.Up: return Cursor('A', mod, app);
            case Key.Down: return Cursor('B', mod, app);
            case Key.Right: return Cursor('C', mod, app);
            case Key.Left: return Cursor('D', mod, app);
            case Key.Home: return Cursor('H', mod, app);
            case Key.End: return Cursor('F', mod, app);
            case Key.Insert: return Tilde(2, mod);
            case Key.Delete: return Tilde(3, mod);
            case Key.PageUp: return Tilde(5, mod);
            case Key.PageDown: return Tilde(6, mod);
            case >= Key.F1 and <= Key.F4: return Ss3OrCsi((char)('P' + (key - Key.F1)), mod);
            case Key.F5: return Tilde(15, mod);
            case Key.F6: return Tilde(17, mod);
            case Key.F7: return Tilde(18, mod);
            case Key.F8: return Tilde(19, mod);
            case Key.F9: return Tilde(20, mod);
            case Key.F10: return Tilde(21, mod);
            case Key.F11: return Tilde(23, mod);
            case Key.F12: return Tilde(24, mod);
            case Key.Enter: return alt ? "\e\r"u8.ToArray() : modes.LineFeedNewLine ? "\r\n"u8.ToArray() : "\r"u8.ToArray();
            case Key.Back: return ctrl ? [0x08] : alt ? "\e\x7f"u8.ToArray() : [0x7f];
            case Key.Tab: return shift ? "\e[Z"u8.ToArray() : alt ? "\e\t"u8.ToArray() : "\t"u8.ToArray();
            case Key.Escape: return alt ? "\e\e"u8.ToArray() : "\e"u8.ToArray();
            case Key.Space when ctrl: return alt ? [0x1b, 0x00] : [0x00];
        }

        if (modes.ApplicationKeypad && Keypad(key) is { } kp) return Encoding.ASCII.GetBytes("\eO" + kp);

        if (ctrl && ControlCharacter(key, keySymbol) is { } c)
            return alt ? [0x1b, c] : [c];

        if (alt && !ctrl && keySymbol is { Length: > 0 } sym && !char.IsControl(sym[0]))
            return [0x1b, .. Encoding.UTF8.GetBytes(sym)]; // Alt as Meta: ESC + the character the key produces

        return null;
    }

    private static byte[] Cursor(char final, int mod, bool application) =>
        mod > 1 ? Encoding.ASCII.GetBytes($"\e[1;{mod}{final}") : Encoding.ASCII.GetBytes(application ? $"\eO{final}" : $"\e[{final}");

    private static byte[] Tilde(int code, int mod) =>
        Encoding.ASCII.GetBytes(mod > 1 ? $"\e[{code};{mod}~" : $"\e[{code}~");

    private static byte[] Ss3OrCsi(char final, int mod) =>
        Encoding.ASCII.GetBytes(mod > 1 ? $"\e[1;{mod}{final}" : $"\eO{final}");

    /// <summary>Ctrl+key as xterm maps it: letters to 0x01-0x1A, @ 2 space to NUL, [ 3 to ESC, \ 4 to FS, ] 5 to GS, ^ 6 to RS, _ 7 - / to US, 8 ? to DEL.</summary>
    internal static byte? ControlCharacter(Key key, string? symbol)
    {
        if (key is >= Key.A and <= Key.Z) return (byte)(key - Key.A + 1);
        var ch = symbol is { Length: 1 } ? symbol[0] : '\0';
        if (ch is >= 'a' and <= 'z') return (byte)(ch - 'a' + 1);
        if (ch is >= 'A' and <= 'Z') return (byte)(ch - 'A' + 1);
        return key switch
        {
            Key.D2 or Key.Space => 0x00,
            Key.D3 or Key.OemOpenBrackets => 0x1b,
            Key.D4 or Key.OemPipe or Key.OemBackslash => 0x1c,
            Key.D5 or Key.OemCloseBrackets => 0x1d,
            Key.D6 => 0x1e,
            Key.D7 or Key.OemMinus or Key.OemQuestion => 0x1f,
            Key.D8 => 0x7f,
            _ => ch switch { '@' => 0x00, '[' => 0x1b, '\\' => 0x1c, ']' => 0x1d, '^' => 0x1e, '_' or '/' => 0x1f, '?' => 0x7f, _ => null },
        };
    }

    private static string? Keypad(Key key) => key switch
    {
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('p' + (key - Key.NumPad0))).ToString(),
        Key.Multiply => "j",
        Key.Add => "k",
        Key.Separator => "l",
        Key.Subtract => "m",
        Key.Decimal => "n",
        Key.Divide => "o",
        _ => null,
    };

    /// <summary>Bytes of typed text (UTF-8), CR for a newline.</summary>
    public static byte[] Text(string text) => Encoding.UTF8.GetBytes(text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r'));

    /// <summary>
    /// A paste: line breaks become CR (as xterm.js and VS Code send them); with bracketed paste the text is wrapped in <c>CSI 200~</c> …
    /// <c>CSI 201~</c> and any end marker inside it removed, so pasted text cannot end the bracket early and run as typed commands.
    /// </summary>
    public static byte[] Paste(string text, bool bracketed)
    {
        var normalized = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        if (!bracketed) return Encoding.UTF8.GetBytes(normalized);
        normalized = normalized.Replace("\e[201~", "", StringComparison.Ordinal).Replace("\e[200~", "", StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes("\e[200~" + normalized + "\e[201~");
    }

    /// <summary>Focus in/out reports (DECSET 1004).</summary>
    public static byte[] Focus(bool focused) => focused ? "\e[I"u8.ToArray() : "\e[O"u8.ToArray();
}
