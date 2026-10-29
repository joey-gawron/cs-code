using Avalonia.Headless.XUnit;
using CsCode.App.Input;
using CsCode.App.QuickInput;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>The keyboard pipeline over the REAL keymap presets, keybinding service and command table (<see cref="RealAppHost"/>).</summary>
public class RealAppKeyTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Keys_whose_preset_command_no_module_registers_reach_the_focused_control()
    {
        await using var host = RealAppHost.Create(h => h.Home.WriteFile("config/keybindings.json", """
            [
              { "key": "ctrl+alt+k", "command": "workbench.action.showCommands" },
              { "key": "ctrl+alt+k ctrl+alt+x", "command": "does.not.exist" }
            ]
            """));
        await host.StartAsync();
        var keyboard = host.Shell.Keyboard;
        var commands = host.Get<ICommandService>();
        host.Get<IContextKeyService>().Set("editorFocus", true);
        host.Get<IContextKeyService>().Set("editorTextFocus", true);

        // The Visual Studio preset binds these to commands nothing registers (find next, jump to bracket, peek, terminal...): they used to be
        // swallowed with a "command not available" toast, so F3 never reached the editor's own find.
        foreach (var key in new[] { "f3", "shift+f3", "ctrl+]", "alt+f12", "ctrl+`" })
        {
            var stroke = KeyStrokeParser.ParseStroke(key);
            var bound = host.Get<IKeybindingService>().All.Where(b => b.Chord.Count == 1 && b.Chord[0] == stroke).Select(b => b.Command).ToArray();
            Assert.True(bound.Length > 0 && !bound.Any(commands.Has), $"precondition: {key} is bound only to unregistered commands ({string.Join(", ", bound)})");
            Assert.Equal(KeyDispatch.PassThrough, keyboard.Handle(stroke));
            Assert.False(keyboard.IsChordPending);
        }
        Assert.Equal(0, keyboard.ExecutedCount);

        // A registered binding still runs when a dead chord (user keybindings.json, higher precedence) starts with the same key.
        Assert.Equal(KeyDispatch.Handled, keyboard.Handle(KeyStrokeParser.ParseStroke("ctrl+alt+k")));
        Assert.True(await RealAppHost.WaitFor(() => host.Get<QuickInputViewModel>().IsVisible));
        host.Get<QuickInputViewModel>().Cancel();
    }
}
