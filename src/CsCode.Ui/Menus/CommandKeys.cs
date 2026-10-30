using Avalonia.Input;

namespace CsCode.Ui.Menus;

/// <summary>
/// The platform's keys for the shortcuts views handle themselves: the command modifier (Ctrl, ⌘ on macOS, as <see cref="ClipboardKeys"/>),
/// Delete (also ⌘⌫ on macOS, whose 'delete' key sends Backspace and has no forward Delete without Fn) and the Alt+letter panel toggles
/// (⌥⌘ on macOS, where Option+letter types a character: ç, ∑, ®, π). The overloads that take <c>mac</c> let tests check either platform's
/// keys on any OS.
/// </summary>
public static class CommandKeys
{
    public static bool IsMac => OperatingSystem.IsMacOS();

    /// <summary>Ctrl, or Meta (⌘) on macOS.</summary>
    public static KeyModifiers Command => CommandFor(IsMac);

    public static KeyModifiers CommandFor(bool mac) => mac ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>Alt, or Meta+Alt (⌥⌘) on macOS: the modifiers of a panel's Alt+letter toggle.</summary>
    public static KeyModifiers AltToggle => AltToggleFor(IsMac);

    public static KeyModifiers AltToggleFor(bool mac) => mac ? KeyModifiers.Meta | KeyModifiers.Alt : KeyModifiers.Alt;

    /// <summary>Delete without modifiers, or on macOS also ⌘⌫ (Finder's Move to Trash): what removes the selected row of a list.</summary>
    public static bool IsDelete(KeyEventArgs e) => IsDelete(e.Key, e.KeyModifiers, IsMac);

    public static bool IsDelete(Key key, KeyModifiers modifiers, bool mac) =>
        (key == Key.Delete && modifiers == KeyModifiers.None) || IsMacDelete(key, modifiers, mac);

    /// <summary>⌘⌫ on macOS (never elsewhere): for a view that already takes Delete with any modifiers.</summary>
    public static bool IsMacDelete(KeyEventArgs e) => IsMacDelete(e.Key, e.KeyModifiers, IsMac);

    public static bool IsMacDelete(Key key, KeyModifiers modifiers, bool mac) => mac && key == Key.Back && modifiers == KeyModifiers.Meta;

    /// <summary>"Ctrl+C" (or "Meta+C", shown as ⌘C, on macOS): a <see cref="KeyGesture.Parse"/> string for a menu item's InputGesture.</summary>
    public static string Gesture(string key) => Gesture(key, IsMac);

    public static string Gesture(string key, bool mac) => (mac ? "Meta+" : "Ctrl+") + key;

    /// <summary>"Delete" (or "Meta+Back", shown as ⌘⌫, on macOS): the <see cref="KeyGesture.Parse"/> string of <see cref="IsDelete(KeyEventArgs)"/>.</summary>
    public static string DeleteGesture => IsMac ? "Meta+Back" : "Delete";

    /// <summary>"Del" (or "⌘⌫" on macOS): the text a menu entry or tooltip shows for <see cref="IsDelete(KeyEventArgs)"/>.</summary>
    public static string DeleteText => IsMac ? "⌘⌫" : "Del";

    /// <summary>"Alt+C" (or "⌥⌘C" on macOS): the text a tooltip shows for an <see cref="AltToggle"/> key.</summary>
    public static string AltToggleText(string key) => IsMac ? "⌥⌘" + key : "Alt+" + key;
}
