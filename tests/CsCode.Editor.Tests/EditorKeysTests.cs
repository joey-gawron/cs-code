using Avalonia.Input;
using CsCode.Editor.View;
using Xunit;

namespace CsCode.Editor.Tests;

/// <summary>The view's own command modifier (<see cref="EditorKeys"/>): Ctrl, ⌘ on macOS, for both platforms on any OS.</summary>
public class EditorKeysTests
{
    [Fact]
    public void Ctrl_click_and_Ctrl_wheel_are_Cmd_click_and_Cmd_wheel_on_macOS()
    {
        Assert.Equal(KeyModifiers.Control, EditorKeys.CommandFor(mac: false));
        Assert.Equal(KeyModifiers.Meta, EditorKeys.CommandFor(mac: true));
        Assert.True(EditorKeys.HasCommand(KeyModifiers.Control | KeyModifiers.Shift, mac: false));
        Assert.False(EditorKeys.HasCommand(KeyModifiers.Meta, mac: false));
        Assert.True(EditorKeys.HasCommand(KeyModifiers.Meta | KeyModifiers.Shift, mac: true));
        Assert.False(EditorKeys.HasCommand(KeyModifiers.Control, mac: true)); // Ctrl+click is a right-click on a Mac
    }

    [Fact]
    public void Releasing_the_command_key_is_Ctrl_or_Cmd_which_Avalonia_reports_as_the_Win_keys()
    {
        Assert.True(EditorKeys.IsCommandKey(Key.LeftCtrl, mac: false));
        Assert.True(EditorKeys.IsCommandKey(Key.RightCtrl, mac: false));
        Assert.False(EditorKeys.IsCommandKey(Key.LWin, mac: false));
        Assert.True(EditorKeys.IsCommandKey(Key.LWin, mac: true));
        Assert.True(EditorKeys.IsCommandKey(Key.RWin, mac: true));
        Assert.False(EditorKeys.IsCommandKey(Key.LeftCtrl, mac: true));
    }
}
