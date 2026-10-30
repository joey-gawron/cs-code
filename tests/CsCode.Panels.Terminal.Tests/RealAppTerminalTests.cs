using Avalonia.Headless.XUnit;
using CsCode.Core.Platform;
using CsCode.Panels.Terminal.Tests.Infrastructure;
using Xunit;

namespace CsCode.Panels.Terminal.Tests;

/// <summary>The terminal in the real composition: menus, keys, the tool window, a real (sandboxed) shell; screenshots for the user.</summary>
public sealed class RealAppTerminalTests
{
    [AvaloniaFact]
    public async Task New_terminal_from_the_command_shows_the_tool_window_with_a_running_shell()
    {
        await using var app = await TerminalAppHost.StartAsync();
        await app.RunAsync(TerminalCommandSet.New);
        Assert.True(await TerminalAppHost.WaitFor(() => app.View is { IsShown: true }), "the Terminal window did not show");
        Assert.True(await app.WaitForScreenAsync("$"), "no prompt: " + app.ScreenText());
        var session = app.Terminals.ActiveSession!;
        await session.SendTextAsync("ls --color=always -F /usr; echo \"done-$((6*7))\"");
        Assert.True(await app.WaitForScreenAsync("done-42"), app.ScreenText());
        app.Capture("terminal-ls-color");
    }
}
