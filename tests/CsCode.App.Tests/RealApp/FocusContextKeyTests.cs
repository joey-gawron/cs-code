using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using CsCode.App.Docking;
using CsCode.App.Input;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Panels.Terminal.Views;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// T7/T22 on the REAL composition: the focus context keys (<c>editorTextFocus</c>, <c>panelFocus</c>, <c>terminalFocus</c>...) and the shell's focus
/// area follow keyboard focus in every direction. Before the fix the main window never saw the editor take focus (AvaloniaEdit marks its text area's
/// GotFocus handled), so after a panel had focus the area stayed "panel" while the editor typed; and the window's handler, which runs after a
/// control's own, reset the <c>terminalFocus</c> the terminal had just set (the terminal re-posted it a dispatcher turn later). Checked synchronously
/// right after each focus change: no dispatcher turn may be needed for the keys to be right.
/// </summary>
public class FocusContextKeyTests
{
    private static RealAppHost Create(TempDirectory sandbox) => RealAppHost.Create(h =>
    {
        h.Files.WriteFile("a.txt", "alpha\nbeta\n");
        foreach (var d in new[] { "home", "config", "data" }) Directory.CreateDirectory(sandbox.Combine(d));
        // The terminal runs a sandboxed bash (never the user's rc files or config): docs/TERMINAL.md, tests/CsCode.Panels.Terminal.Tests.
        var settings = new Dictionary<string, object?>
        {
            ["cscode.languageServers"] = new[] { "typescript", "csharp", "python", "clangd", "rust-analyzer", "gopls", "json", "html", "css", "bash", "yaml", "lua" }
                .ToDictionary(s => s, _ => (object)new { enabled = false }),
            ["workbench.startupEditor"] = "none",
            ["terminal.integrated.profiles.linux"] = new Dictionary<string, object>
            {
                ["sandboxed"] = new
                {
                    path = File.Exists("/bin/bash") ? "/bin/bash" : "bash",
                    args = new[] { "--noprofile", "--norc" },
                    env = new Dictionary<string, string?>
                    {
                        ["HOME"] = sandbox.Combine("home"), ["XDG_CONFIG_HOME"] = sandbox.Combine("config"), ["XDG_DATA_HOME"] = sandbox.Combine("data"),
                        ["ZDOTDIR"] = sandbox.Combine("home"), ["PS1"] = "$ ", ["BASH_ENV"] = null, ["ENV"] = null, ["PROMPT_COMMAND"] = null,
                        ["HISTFILE"] = sandbox.Combine("home", ".bash_history"),
                    },
                },
            },
            ["terminal.integrated.defaultProfile.linux"] = "sandboxed",
            ["terminal.integrated.cwd"] = sandbox.Combine("home"),
            ["terminal.integrated.shellIntegration.enabled"] = false,
        };
        h.Home.WriteFile("config/settings.json", JsonSerializer.Serialize(settings));
    });

    private static (bool Editor, bool Panel, bool Terminal, bool List, bool Input) Keys(RealAppHost host)
    {
        var keys = host.Get<IContextKeyService>();
        return (keys.Get("editorTextFocus") is true, keys.Get("panelFocus") is true, keys.Get("terminalFocus") is true, keys.Get("listFocus") is true, keys.Get("inputFocus") is true);
    }

    private static Control FirstFocusable(Control root) =>
        root.GetVisualDescendants().OfType<Control>().First(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Focus_keys_follow_focus_between_the_editor_a_panel_the_explorer_and_the_terminal()
    {
        using var sandbox = new TempDirectory("cscode-focus-sandbox-");
        await using var host = await Create(sandbox).StartAsync();
        var shell = host.Shell;
        var tab = await shell.Well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => tab.Controller is not null && tab.LiveView!.IsAttachedToVisualTree()));
        await DevScene.SettleAsync(10);

        // Editor (AvaloniaEdit marks GotFocus handled).
        tab.Controller!.Focus();
        Assert.True(tab.LiveView!.IsKeyboardFocusWithin);
        Assert.Equal(FocusArea.Editor, shell.ContextKeys.Focus);
        Assert.Equal((true, false, false, false, false), Keys(host));

        // A panel (Output).
        shell.Tools.Show("output");
        await DevScene.SettleAsync(10);
        var output = shell.Tools.Get("output")!.LiveView!;
        FirstFocusable(output).Focus();
        Assert.Equal(FocusArea.Panel, shell.ContextKeys.Focus);
        Assert.Equal((false, true, false, false, false), Keys(host));

        // Back to the editor: the stale-area bug (the area stayed Panel, panelFocus stayed true).
        tab.Controller.Focus();
        Assert.Equal(FocusArea.Editor, shell.ContextKeys.Focus);
        Assert.Equal((true, false, false, false, false), Keys(host));

        // The Explorer.
        shell.Tools.Show("explorer");
        await DevScene.SettleAsync(10);
        FirstFocusable(shell.Tools.Get("explorer")!.LiveView!).Focus();
        Assert.Equal(FocusArea.Explorer, shell.ContextKeys.Focus);
        Assert.Equal((false, false, false, true, false), Keys(host));

        // The terminal: the control sets terminalFocus in its own GotFocus, and the window's handler (which runs after it) agrees.
        await host.RunAsync("workbench.action.terminal.new");
        Assert.True(await RealAppHost.WaitFor(() => host.Window.GetVisualDescendants().OfType<TerminalControl>().Any(c => c.IsEffectivelyVisible), 20_000), "no terminal");
        var terminal = host.Window.GetVisualDescendants().OfType<TerminalControl>().First(c => c.IsEffectivelyVisible);
        tab.Controller.Focus();
        Assert.Equal((true, false, false, false, false), Keys(host));
        terminal.Focus();
        Assert.True(terminal.IsKeyboardFocusWithin);
        Assert.Equal(FocusArea.Terminal, shell.ContextKeys.Focus);
        Assert.Equal((false, true, true, false, false), Keys(host));
        await DevScene.SettleAsync(5);
        Assert.Equal((false, true, true, false, false), Keys(host)); // and it stays so

        // Leaving the terminal for the editor clears it again.
        tab.Controller.Focus();
        Assert.Equal(FocusArea.Editor, shell.ContextKeys.Focus);
        Assert.Equal((true, false, false, false, false), Keys(host));
        await host.RunAsync("workbench.action.terminal.kill");
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_quick_input_box_is_input_focus_and_the_editor_comes_back_after_it()
    {
        using var sandbox = new TempDirectory("cscode-focus-sandbox-");
        await using var host = await Create(sandbox).StartAsync();
        var tab = await host.Shell.Well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => tab.Controller is not null && tab.LiveView!.IsAttachedToVisualTree()));
        tab.Controller!.Focus();
        Assert.Equal((true, false, false, false, false), Keys(host));

        var running = await host.OpenPaletteAsync(KnownCommands.QuickOpen);
        Assert.True(await RealAppHost.WaitFor(() => Keys(host).Input), "the palette's text box has focus");
        Assert.False(Keys(host).Editor);
        await host.ClosePaletteAsync(running);
        tab.Controller.Focus();
        Assert.Equal((true, false, false, false, false), Keys(host));
    }
}
