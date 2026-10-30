using Avalonia.Input;

namespace CsCode.Panels.Terminal.Input;

/// <summary>Avalonia keys to VS Code key names (the shell's mapping, which this panel cannot reference), for keybinding lookups.</summary>
internal static class KeyNames
{
    public static string? NameOf(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)('a' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.F1 and <= Key.F24) return "f" + (1 + (key - Key.F1));
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return "numpad" + (key - Key.NumPad0);
        return key switch
        {
            Key.Up => "up", Key.Down => "down", Key.Left => "left", Key.Right => "right",
            Key.PageUp => "pageup", Key.PageDown => "pagedown", Key.Home => "home", Key.End => "end",
            Key.Insert => "insert", Key.Delete => "delete", Key.Back => "backspace", Key.Tab => "tab",
            Key.Enter => "enter", Key.Escape => "escape", Key.Space => "space",
            Key.OemComma => ",", Key.OemPeriod => ".", Key.OemMinus => "-", Key.OemPlus => "=",
            Key.OemSemicolon => ";", Key.OemQuestion => "/", Key.OemTilde => "`", Key.OemOpenBrackets => "[",
            Key.OemPipe => "\\", Key.OemCloseBrackets => "]", Key.OemQuotes => "'",
            Key.Add => "numpad_add", Key.Subtract => "numpad_subtract", Key.Multiply => "numpad_multiply", Key.Divide => "numpad_divide", Key.Decimal => "numpad_decimal",
            _ => null,
        };
    }
}
