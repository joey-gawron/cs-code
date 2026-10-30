using Avalonia.Input;

namespace CsCode.Editor.View;

/// <summary>
/// The command modifier of the keys and clicks the view handles itself (Ctrl+Click go to definition, Ctrl+wheel zoom, the clipboard keys): Ctrl,
/// or ⌘ on macOS, where Ctrl+Click is a right-click (as CsCode.Ui's CommandKeys, which this assembly does not reference). The overloads that
/// take <c>mac</c> let tests check either platform's keys on any OS.
/// </summary>
internal static class EditorKeys
{
    internal static KeyModifiers Command => CommandFor(OperatingSystem.IsMacOS());

    internal static KeyModifiers CommandFor(bool mac) => mac ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>Whether <paramref name="modifiers"/> hold the command modifier (with or without others).</summary>
    internal static bool HasCommand(KeyModifiers modifiers) => HasCommand(modifiers, OperatingSystem.IsMacOS());

    internal static bool HasCommand(KeyModifiers modifiers, bool mac) => (modifiers & CommandFor(mac)) != 0;

    /// <summary>Whether <paramref name="key"/> is the command modifier's own key (Left/Right Ctrl, or ⌘, which Avalonia reports as LWin/RWin).</summary>
    internal static bool IsCommandKey(Key key) => IsCommandKey(key, OperatingSystem.IsMacOS());

    internal static bool IsCommandKey(Key key, bool mac) => mac ? key is Key.LWin or Key.RWin : key is Key.LeftCtrl or Key.RightCtrl;
}
