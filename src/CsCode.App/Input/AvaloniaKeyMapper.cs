using Avalonia.Input;
using CsCode.Core.Platform;

namespace CsCode.App.Input;

/// <summary>Maps Avalonia key events onto the toolkit-independent <see cref="KeyStroke"/> (VS Code key names, lower-case).</summary>
public static class AvaloniaKeyMapper
{
    /// <summary>False for modifier-only presses and keys with no VS Code name (those are never bindings).</summary>
    public static bool TryMap(Key key, KeyModifiers modifiers, out KeyStroke stroke)
    {
        stroke = default;
        var name = NameOf(key);
        if (name is null) return false;
        stroke = new KeyStroke(name,
            Ctrl: (modifiers & KeyModifiers.Control) != 0,
            Shift: (modifiers & KeyModifiers.Shift) != 0,
            Alt: (modifiers & KeyModifiers.Alt) != 0,
            Meta: (modifiers & KeyModifiers.Meta) != 0);
        return true;
    }

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
