using Avalonia.Input;
using CsCode.Ui.Menus;
using Xunit;

namespace CsCode.Panels.Search.Tests.Ui;

/// <summary>The platform keys views handle themselves (<see cref="CommandKeys"/>), for both platforms on any OS.</summary>
public class CommandKeysTests
{
    [Theory]
    [InlineData(false, KeyModifiers.Control, KeyModifiers.Alt)]
    [InlineData(true, KeyModifiers.Meta, KeyModifiers.Meta | KeyModifiers.Alt)]
    public void The_command_modifier_is_Cmd_and_the_panel_toggles_Option_Cmd_on_macOS(bool mac, KeyModifiers command, KeyModifiers toggle)
    {
        Assert.Equal(command, CommandKeys.CommandFor(mac));
        Assert.Equal(toggle, CommandKeys.AltToggleFor(mac));
        Assert.Equal(mac ? "Meta+C" : "Ctrl+C", CommandKeys.Gesture("C", mac));
        Assert.Equal(new KeyGesture(Key.C, command), KeyGesture.Parse(CommandKeys.Gesture("C", mac)));
    }

    [Fact]
    public void Delete_removes_a_row_and_on_macOS_Cmd_Backspace_does_too()
    {
        Assert.True(CommandKeys.IsDelete(Key.Delete, KeyModifiers.None, mac: false));
        Assert.True(CommandKeys.IsDelete(Key.Delete, KeyModifiers.None, mac: true));
        Assert.False(CommandKeys.IsDelete(Key.Delete, KeyModifiers.Shift, mac: false)); // Shift+Delete is Cut
        Assert.True(CommandKeys.IsDelete(Key.Back, KeyModifiers.Meta, mac: true));
        Assert.False(CommandKeys.IsDelete(Key.Back, KeyModifiers.Meta, mac: false));
        Assert.False(CommandKeys.IsDelete(Key.Back, KeyModifiers.None, mac: true)); // plain ⌫ is left to the view
        Assert.False(CommandKeys.IsMacDelete(Key.Delete, KeyModifiers.None, mac: true));
        Assert.Equal(new KeyGesture(Key.Back, KeyModifiers.Meta), KeyGesture.Parse("Meta+Back"));
    }

    [Fact]
    public void The_shared_clipboard_keys_follow_the_command_modifier()
    {
        Assert.Equal(ClipboardAction.Copy, ClipboardKeys.Classify(Key.C, CommandKeys.Command));
        Assert.Equal(ClipboardAction.None, ClipboardKeys.Classify(Key.C, CommandKeys.CommandFor(!CommandKeys.IsMac)));
    }
}
