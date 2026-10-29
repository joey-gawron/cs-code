using System.Text;
using CsCode.Core.Platform;

namespace CsCode.Platform.Keybindings;

/// <summary>Human-readable key labels per OS: <c>Ctrl+K, Ctrl+C</c> on Windows/Linux, <c>⌘K ⌘C</c> on macOS.</summary>
public static class KeyBindingFormatter
{
    public static string Format(IReadOnlyList<KeyStroke> chord, HostOs os)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < chord.Count; i++)
        {
            if (i > 0) sb.Append(os == HostOs.MacOs ? " " : ", ");
            AppendStroke(sb, chord[i], os);
        }
        return sb.ToString();
    }

    public static string Format(KeyStroke stroke, HostOs os)
    {
        var sb = new StringBuilder();
        AppendStroke(sb, stroke, os);
        return sb.ToString();
    }

    private static void AppendStroke(StringBuilder sb, KeyStroke s, HostOs os)
    {
        if (os == HostOs.MacOs)
        {
            // macOS order and glyphs: ⌃ Control, ⌥ Option, ⇧ Shift, ⌘ Command (no separators)
            if (s.Ctrl) sb.Append('⌃');
            if (s.Alt) sb.Append('⌥');
            if (s.Shift) sb.Append('⇧');
            if (s.Meta) sb.Append('⌘');
            sb.Append(MacKey(s.Key));
            return;
        }
        if (s.Ctrl) sb.Append("Ctrl+");
        if (s.Shift) sb.Append("Shift+");
        if (s.Alt) sb.Append("Alt+");
        if (s.Meta) sb.Append(os == HostOs.Windows ? "Win+" : "Super+");
        sb.Append(KeyName(s.Key));
    }

    private static string KeyName(string key) => key switch
    {
        "escape" => "Esc",
        "pageup" => "PageUp",
        "pagedown" => "PageDown",
        "backspace" => "Backspace",
        "enter" => "Enter",
        "space" => "Space",
        "tab" => "Tab",
        "delete" => "Delete",
        "insert" => "Insert",
        "home" => "Home",
        "end" => "End",
        "up" => "Up",
        "down" => "Down",
        "left" => "Left",
        "right" => "Right",
        { Length: 1 } k => k.ToUpperInvariant(),
        { Length: > 1 } k when k[0] == 'f' && int.TryParse(k.AsSpan(1), out _) => k.ToUpperInvariant(),
        { Length: > 1 } k => char.ToUpperInvariant(k[0]) + k[1..],
        _ => key,
    };

    private static string MacKey(string key) => key switch
    {
        "up" => "↑",
        "down" => "↓",
        "left" => "←",
        "right" => "→",
        "enter" => "↩",
        "escape" => "⎋",
        "backspace" => "⌫",
        "delete" => "⌦",
        "tab" => "⇥",
        "home" => "↖",
        "end" => "↘",
        "pageup" => "⇞",
        "pagedown" => "⇟",
        _ => KeyName(key),
    };
}
