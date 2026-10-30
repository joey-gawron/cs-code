using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Macros;
using CsCode.Panels.Terminal;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.RealApp.Macros;

/// <summary>
/// Macro recording and the integrated terminal in the REAL composition (docs/MACROS.md): nothing is recorded while the terminal has the keyboard
/// (context key <c>terminalFocus</c>), neither what is typed into the shell nor the terminal's commands run by key or from code. The terminal runs
/// a real, sandboxed bash (<c>--noprofile --norc</c>, HOME and the XDG dirs in a temp directory: no rc file of the user's is read).
/// </summary>
public class MacroTerminalRealAppTests
{
    private static string? Which(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Append("/usr/bin").Append("/bin")
            .Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);

    /// <summary>The settings (inside settings.json's object) of a sandboxed bash as the default terminal profile.</summary>
    private static string SandboxedShell(string bash, TempDirectory sandbox)
    {
        var settings = new Dictionary<string, object?>
        {
            [CsCode.Terminal.Sessions.TerminalSettingKeys.ProfilesKey] = new Dictionary<string, object>
            {
                ["sandboxed"] = new
                {
                    path = bash,
                    args = new[] { "--noprofile", "--norc" },
                    env = new Dictionary<string, string?>
                    {
                        ["HOME"] = sandbox.Combine("home"),
                        ["XDG_CONFIG_HOME"] = sandbox.Combine("config"),
                        ["XDG_DATA_HOME"] = sandbox.Combine("data"),
                        ["ZDOTDIR"] = sandbox.Combine("home"),
                        ["PS1"] = "$ ",
                        ["BASH_ENV"] = null,
                        ["ENV"] = null,
                        ["PROMPT_COMMAND"] = null,
                        ["HISTFILE"] = sandbox.Combine("home", ".bash_history"),
                        ["LANG"] = "C.UTF-8",
                    },
                },
            },
            [CsCode.Terminal.Sessions.TerminalSettingKeys.DefaultProfileKey] = "sandboxed",
            ["terminal.integrated.cwd"] = sandbox.Combine("home"),
            ["terminal.integrated.shellIntegration.enabled"] = false,
        };
        var json = JsonSerializer.Serialize(settings).Trim();
        return json[1..^1]; // the members only: MacroApp adds them to the settings file's object
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task Nothing_is_recorded_while_the_terminal_has_the_keyboard_typing_terminal_keys_or_terminal_commands()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the sandboxed shell is bash (Linux/macOS)");
        var bash = Which("bash");
        Assert.SkipWhen(bash is null, "bash is not installed");
        using var sandbox = new TempDirectory("cscode-macro-terminal-");
        foreach (var dir in new[] { "home", "config", "data" }) Directory.CreateDirectory(sandbox.Combine(dir));
        await using var app = await MacroApp.StartAsync("abc", settings: SandboxedShell(bash!, sandbox));
        var keys = app.Host.Get<IContextKeyService>();
        var runtime = app.Host.Get<TerminalPanelRuntime>();
        bool TerminalFocused() => runtime.View?.Control is { IsKeyboardFocusWithin: true } && keys.Get(MacroCommands.RecordingPausedWhen) is true;
        string Screen() => runtime.View?.Control.Snapshot is { } s ? string.Join('\n', s.Rows.Select(r => r.GetText())) : "";

        // a terminal with a running shell, then back to the editor
        await app.Host.RunAsync(TerminalCommandSet.Toggle);
        Assert.True(await RealAppHost.WaitFor(() => runtime.View is { IsVisible: true } && Screen().Contains('$'), 15_000), "no shell prompt: " + Screen());
        await app.FocusAsync();
        Assert.True(await RealAppHost.WaitFor(() => keys.Get(MacroCommands.RecordingPausedWhen) is not true));

        app.RecordKey();
        Assert.True(app.Service.IsRecording);
        app.Type("1");

        // the keyboard goes to the terminal (as a click does): recording pauses
        runtime.View!.Control.Focus();
        Assert.True(await RealAppHost.WaitFor(TerminalFocused), "the terminal did not take the keyboard");
        await app.SettleAsync();
        Assert.True(app.Service.IsRecordingPaused);
        Assert.Equal("$(record) Recording macro… (paused in the terminal)", app.StatusText(MacroCommands.RecordingStatusId));
        app.Type("echo mac");
        foreach (var c in "$((6*7))") app.Host.Window.KeyTextInput(c.ToString());
        app.Press(Key.Enter);
        Assert.True(await RealAppHost.WaitFor(() => Screen().Contains("mac42", StringComparison.Ordinal), 15_000), "the shell did not run the command: " + Screen());
        app.Press(Key.A, MacroApp.Ctrl | RawInputModifiers.Shift);   // the terminal's Select All (a command: commandsToSkipShell)
        app.Press(Key.PageUp, RawInputModifiers.Shift);              // its scroll (a command)
        await app.SettleAsync();
        await app.Host.RunAsync(TerminalCommandSet.Clear);           // and one run from the menu or the palette while it has the keyboard
        Assert.True(TerminalFocused());

        // back to the editor: recording goes on
        await app.FocusAsync();
        Assert.True(await RealAppHost.WaitFor(() => !app.Service.IsRecordingPaused));
        Assert.Equal("$(record) Recording macro…", app.StatusText(MacroCommands.RecordingStatusId));
        app.Type("2");
        app.RecordKey();
        await app.SettleAsync();

        Assert.False(app.Service.IsRecording);
        Assert.Equal([new TextStep("12")], app.Service.Last!.Steps);
        Assert.Equal("12abc", app.Text);
    }
}
