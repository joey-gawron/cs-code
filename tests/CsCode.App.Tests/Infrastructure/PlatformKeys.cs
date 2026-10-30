using Avalonia.Input;

namespace CsCode.App.Tests.Infrastructure;

/// <summary>
/// The current platform's application keys as the keymaps bind them: Cmd (Meta) on macOS where Linux has Ctrl (Ctrl+S is Cmd+S), and the labels
/// the menus show for them (Ctrl+C on Linux, ⌘C on macOS). Keys that stay Ctrl on macOS (Ctrl+Tab, Vim's Ctrl keys) keep
/// <see cref="RawInputModifiers.Control"/>.
/// </summary>
internal static class PlatformKeys
{
    public static RawInputModifiers Command => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    /// <summary><paramref name="linux"/> elsewhere, <paramref name="mac"/> on macOS: what a menu shows for a key.</summary>
    public static string Label(string linux, string mac) => OperatingSystem.IsMacOS() ? mac : linux;

    /// <summary><paramref name="linux"/> elsewhere, <paramref name="mac"/> on macOS: a key in keybinding syntax (<c>ctrl+shift+f</c>, <c>cmd+shift+f</c>).</summary>
    public static string Stroke(string linux, string mac) => OperatingSystem.IsMacOS() ? mac : linux;
}
