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

    // ---- editor.multiCursorModifier: which modifier adds a caret with the mouse, and so which one opens links (Go to Definition, URLs) ----

    /// <summary>The modifier a click adds a caret with: Alt (Option on macOS) by default, Ctrl (⌘ on macOS) with <c>"ctrlCmd"</c>.</summary>
    internal static KeyModifiers AddCaretModifier(string multiCursorModifier) => AddCaretModifier(multiCursorModifier, OperatingSystem.IsMacOS());

    internal static KeyModifiers AddCaretModifier(string multiCursorModifier, bool mac) => multiCursorModifier == "ctrlCmd" ? CommandFor(mac) : KeyModifiers.Alt;

    /// <summary>The modifier Go to Definition / Open Link take on a click and a hover: the one that does not add carets (VS Code's rule).</summary>
    internal static KeyModifiers LinkModifier(string multiCursorModifier) => LinkModifier(multiCursorModifier, OperatingSystem.IsMacOS());

    internal static KeyModifiers LinkModifier(string multiCursorModifier, bool mac) => multiCursorModifier == "ctrlCmd" ? KeyModifiers.Alt : CommandFor(mac);

    /// <summary>Whether <paramref name="modifiers"/> hold the link modifier (with or without others).</summary>
    internal static bool HasLinkModifier(KeyModifiers modifiers, string multiCursorModifier) => (modifiers & LinkModifier(multiCursorModifier)) != 0;

    /// <summary>Whether <paramref name="key"/> is the link modifier's own key (releasing it ends the link underline).</summary>
    internal static bool IsLinkKey(Key key, string multiCursorModifier) =>
        multiCursorModifier == "ctrlCmd" ? key is Key.LeftAlt or Key.RightAlt : IsCommandKey(key);

    /// <summary>How the link modifier is written in a tooltip ("Ctrl", "⌘", "Alt", "⌥").</summary>
    internal static string LinkModifierLabel(string multiCursorModifier) =>
        multiCursorModifier == "ctrlCmd" ? (OperatingSystem.IsMacOS() ? "⌥" : "Alt") : (OperatingSystem.IsMacOS() ? "⌘" : "Ctrl");
}
