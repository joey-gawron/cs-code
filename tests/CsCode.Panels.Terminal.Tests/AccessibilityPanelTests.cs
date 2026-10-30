using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.Panels.Terminal.Tests.Infrastructure;
using CsCode.Panels.Terminal.Views;
using Xunit;

namespace CsCode.Panels.Terminal.Tests;

/// <summary>
/// The terminal for screen readers over fake PTYs: the automation peer (a read-only document named after the terminal, its value the visible
/// text, its help the last command), the live region announcing finished commands, the accessible buffer (Alt+F2: the whole scrollback as
/// text, Ctrl+Up/Down between prompts, Escape back), and Copy Last Command (Output).
/// </summary>
public sealed class AccessibilityPanelTests
{
    private static string Command(string line, string output, int exitCode) =>
        $"\e]633;A\a$ \e]633;B\a{line}\r\n\e]633;C\a{output}\e]633;D;{exitCode}\a";

    [AvaloniaFact]
    public async Task The_peer_is_a_read_only_document_of_the_visible_text_named_after_the_terminal()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        await rig.OutputAsync("first line\r\nsecond line\r\n$ ", "second line");
        var peer = ControlAutomationPeer.CreatePeerForElement(rig.Control);
        var terminalPeer = Assert.IsType<TerminalAutomationPeer>(peer);
        Assert.Equal(AutomationControlType.Document, peer.GetAutomationControlType());
        Assert.StartsWith($"Terminal {rig.Session.Id}:", peer.GetName());
        var value = peer.GetProvider<IValueProvider>()!;
        Assert.True(value.IsReadOnly);
        Assert.Equal("first line\nsecond line\n$ ", value.Value);
        Assert.Throws<InvalidOperationException>(() => value.SetValue("x"));

        // the help text says what the last command was (shell integration)
        await rig.OutputAsync(Command("make", "built\r\n", 2) + "$ ", "built");
        Assert.True(await PanelRig.Pump(() => peer.GetHelpText() == "Last command: make, exit code 2."), peer.GetHelpText());
        Assert.Contains("built", terminalPeer.Value);
    }

    [AvaloniaFact]
    public async Task A_finished_command_is_announced_in_the_live_region()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        var announcer = rig.View.GetVisualDescendantsOfType<Avalonia.Controls.TextBlock>().Single(t => AutomationProperties.GetName(t) == "Terminal announcements");
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(announcer));
        await rig.OutputAsync(Command("ls", "a.txt\r\nb.txt\r\n", 0), "b.txt");
        Assert.True(await PanelRig.Pump(() => rig.View.Announcement == "Command finished: ls\na.txt\nb.txt"), rig.View.Announcement);
        await rig.OutputAsync(Command("cat nope", "cat: nope: No such file\r\n", 1), "No such");
        Assert.True(await PanelRig.Pump(() => rig.View.Announcement.StartsWith("Command failed with exit code 1: cat nope", StringComparison.Ordinal)), rig.View.Announcement);
    }

    [AvaloniaFact]
    public async Task The_accessible_buffer_shows_the_whole_scrollback_jumps_between_prompts_and_escape_returns()
    {
        await using var rig = await new PanelRig(800, 200).ReadyAsync();
        var output = string.Concat(Enumerable.Range(0, 8).Select(i => Command($"step{i}", string.Concat(Enumerable.Range(0, 5).Select(k => $"out{i}.{k}\r\n")), 0)));
        await rig.OutputAsync(output + "\e]633;A\a$ ", "out7.4");
        rig.Control.Focus();
        await rig.Commands.ExecuteAsync(TerminalCommandSet.FocusAccessibleBuffer);
        var box = rig.View.AccessibleBuffer;
        Assert.True(await PanelRig.Pump(() => rig.View.IsAccessibleBufferVisible && box.IsKeyboardFocusWithin), "the accessible buffer did not take the keyboard");
        Assert.StartsWith("$ step0\nout0.0\n", box.Text);  // the scrollback too, not just the screen
        Assert.EndsWith("out7.4\n$ ", box.Text);
        Assert.Equal(box.Text!.Length, box.CaretIndex); // at the terminal's cursor

        // Ctrl+Up: the prompt the cursor is on, then the commands before it
        rig.Window.KeyPress(Key.Up, RawInputModifiers.Control, PhysicalKey.ArrowUp, null);
        Assert.Equal(box.Text.Length - 2, box.CaretIndex);
        rig.Window.KeyPress(Key.Up, RawInputModifiers.Control, PhysicalKey.ArrowUp, null);
        Assert.Equal(box.Text.IndexOf("$ step7", StringComparison.Ordinal), box.CaretIndex);
        rig.Window.KeyPress(Key.Up, RawInputModifiers.Control, PhysicalKey.ArrowUp, null);
        Assert.Equal(box.Text.IndexOf("$ step6", StringComparison.Ordinal), box.CaretIndex);
        rig.Window.KeyPress(Key.Down, RawInputModifiers.Control, PhysicalKey.ArrowDown, null);
        Assert.Equal(box.Text.IndexOf("$ step7", StringComparison.Ordinal), box.CaretIndex);

        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.True(await PanelRig.Pump(() => !rig.View.IsAccessibleBufferVisible && rig.Control.IsKeyboardFocusWithin), "Escape did not return to the terminal");
    }

    [AvaloniaFact]
    public async Task Copy_last_command_and_its_output()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        await rig.Commands.ExecuteAsync(TerminalCommandSet.CopyLastCommandOutput);
        Assert.Contains(rig.WindowService.StatusMessages, m => m.Contains("No command", StringComparison.Ordinal));
        await rig.OutputAsync(Command("git status", "On branch main\r\nnothing to commit\r\n", 0) + "$ ", "nothing to commit");
        Assert.True(await PanelRig.Pump(() => rig.Session.LastCommand is not null));
        await rig.Commands.ExecuteAsync(TerminalCommandSet.CopyLastCommandOutput);
        Assert.Equal("On branch main\nnothing to commit", (await rig.Clipboard.ReadAsync())?.Text);
        await rig.Commands.ExecuteAsync(TerminalCommandSet.CopyLastCommand);
        Assert.Equal("git status", (await rig.Clipboard.ReadAsync())?.Text);
    }
}
