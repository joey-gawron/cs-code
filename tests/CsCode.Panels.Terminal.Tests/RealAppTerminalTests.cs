using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using CsCode.App.QuickInput;
using CsCode.Core.Platform;
using CsCode.Core.Theming;
using CsCode.Panels.Terminal.Tests.Infrastructure;
using CsCode.Terminal;
using CsCode.Terminal.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.Panels.Terminal.Tests;

/// <summary>The terminal in the real composition: menus, keys, the tool window, a real (sandboxed) shell; screenshots for the user.</summary>
public sealed class RealAppTerminalTests
{
    private static async Task<(TerminalAppHost App, List<FakePtyHost> Hosts)> StartWithFakePtys()
    {
        var hosts = new List<FakePtyHost>();
        var app = await TerminalAppHost.StartAsync(configure: s => s.AddSingleton(sp => CsCode.Terminal.ServiceCollectionExtensions.CreateService(sp, () =>
        {
            var h = new FakePtyHost();
            lock (hosts) hosts.Add(h);
            return h;
        })));
        return (app, hosts);
    }

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
        Assert.True(await TerminalAppHost.WaitFor(() => app.View!.Control.IsKeyboardFocusWithin), "the new terminal did not get the keyboard");
        Assert.Equal(true, app.Get<IContextKeyService>().Get("terminalFocus"));
        app.Capture("terminal-ls-color");
    }

    [AvaloniaFact]
    public async Task The_terminal_menu_items_are_enabled_and_create_a_terminal()
    {
        await using var app = await TerminalAppHost.StartAsync();
        app.Shell.Menus.Refresh();
        var terminalMenu = app.Shell.Menus.Roots.Single(r => r.Header.Replace("_", "", StringComparison.Ordinal) == "Terminal");
        var newItem = terminalMenu.Children.Single(c => c.CommandId == TerminalCommandSet.New);
        var toggle = terminalMenu.Children.Single(c => c.CommandId == TerminalCommandSet.Toggle);
        Assert.True(newItem.IsEnabled && toggle.IsEnabled);
        Assert.Equal("Ctrl+Shift+`", newItem.Gesture);
        newItem.Command!.Execute(null);
        Assert.True(await TerminalAppHost.WaitFor(() => app.Terminals.Terminals.Count == 1 && app.View is { IsShown: true }));
        Assert.True(await app.WaitForScreenAsync("$"));
        Assert.True(app.Get<ICommandService>().Has(TerminalCommandSet.NewHere), "Explorer's Open in Terminal needs newHere");
    }

    [AvaloniaFact]
    public async Task Ctrl_backtick_toggles_the_terminal_and_focus()
    {
        await using var app = await TerminalAppHost.StartAsync();
        Assert.Null(app.View);
        app.Window.KeyPress(Key.OemTilde, RawInputModifiers.Control, PhysicalKey.Backquote, "`");
        Assert.True(await TerminalAppHost.WaitFor(() => app.View is { IsShown: true } v && v.Control.IsKeyboardFocusWithin), "Ctrl+` did not show and focus the terminal");
        Assert.Single(app.Terminals.Terminals); // created because there was none
        Assert.True(await app.WaitForScreenAsync("$"));
        app.Window.KeyPress(Key.OemTilde, RawInputModifiers.Control, PhysicalKey.Backquote, "`");
        Assert.True(await TerminalAppHost.WaitFor(() => app.View is null), "Ctrl+` did not hide the terminal");
        Assert.Single(app.Terminals.Terminals); // hiding keeps the terminal
        Assert.False(app.Terminals.ActiveSession!.HasExited);
    }

    [AvaloniaFact]
    public async Task While_the_terminal_has_focus_keys_go_to_the_shell_except_commands_to_skip_shell()
    {
        var (app, hosts) = await StartWithFakePtys();
        await using var _ = app;
        await app.RunAsync(TerminalCommandSet.Focus);
        Assert.True(await TerminalAppHost.WaitFor(() => app.View is { } v && v.Control.IsKeyboardFocusWithin && hosts.Count == 1));
        var host = hosts[0];
        host.ClearWritten();

        // Ctrl+F is bound (Find) but not in commandsToSkipShell: the shell gets ^F (readline: forward-char)
        app.Window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        // Ctrl+C: ^C to the shell (the editor's copy binding is for the editor)
        app.Window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        await TerminalAppHost.SettleAsync(5);
        Assert.Equal("\x06\x03", host.Written);

        // Ctrl+Shift+P (showCommands) is in commandsToSkipShell: the palette opens and the shell gets nothing
        host.ClearWritten();
        app.Window.KeyPress(Key.P, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.P, "P");
        Assert.True(await TerminalAppHost.WaitFor(() => app.Get<QuickInputViewModel>().IsVisible), "the palette did not open");
        Assert.Equal("", host.Written);
        app.Get<QuickInputViewModel>().Cancel();
        await TerminalAppHost.SettleAsync(5);

        // the user can send it to the shell after all
        app.View!.Control.Focus();
        await TerminalAppHost.WaitFor(() => app.View.Control.IsKeyboardFocusWithin);
        await app.Get<IConfigurationService>().UpdateAsync("terminal.integrated.commandsToSkipShell", new[] { "-workbench.action.showCommands" });
        host.ClearWritten();
        app.Window.KeyPress(Key.P, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.P, "P");
        await TerminalAppHost.SettleAsync(5);
        Assert.Equal("\x10", host.Written);
        Assert.False(app.Get<QuickInputViewModel>().IsVisible);
    }

    [AvaloniaFact]
    public async Task Terminal_keybindings_copy_paste_and_scroll()
    {
        var (app, hosts) = await StartWithFakePtys();
        await using var _ = app;
        await app.RunAsync(TerminalCommandSet.Focus);
        Assert.True(await TerminalAppHost.WaitFor(() => app.View is { } v && v.Control.IsKeyboardFocusWithin && hosts.Count == 1));
        var host = hosts[0];
        host.Output(string.Concat(Enumerable.Range(0, 200).Select(i => $"line {i}\r\n")));
        await TerminalAppHost.WaitFor(() => app.View!.Control.Snapshot is { ScreenTop: > 100 });
        app.Window.KeyPress(Key.PageUp, RawInputModifiers.Shift, PhysicalKey.PageUp, null);
        Assert.True(await TerminalAppHost.WaitFor(() => app.View!.Control.Snapshot is { IsAtBottom: false }), "Shift+PgUp did not scroll");
        app.Window.KeyPress(Key.End, RawInputModifiers.Control, PhysicalKey.End, null);
        Assert.True(await TerminalAppHost.WaitFor(() => app.View!.Control.Snapshot is { IsAtBottom: true }), "Ctrl+End did not scroll to the bottom");
        app.Window.KeyPress(Key.A, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.A, "A");
        app.Window.KeyPress(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.C, "C");
        var clipboard = app.Get<IClipboardService>();
        Assert.True(await TerminalAppHost.WaitFor(() => app.View!.Control.Selection is not null));
        string? copied = null;
        Assert.True(await TerminalAppHost.WaitFor(() =>
        {
            var read = clipboard.ReadAsync().AsTask();
            if (read.IsCompletedSuccessfully) copied = read.Result?.Text;
            return copied?.Contains("line 199", StringComparison.Ordinal) == true;
        }), "Ctrl+Shift+C did not copy the selection");
        Assert.StartsWith("line ", copied);
        host.ClearWritten();
        await clipboard.WriteAsync(ClipboardText.Plain("pasted"));
        app.Window.KeyPress(Key.V, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.V, "V");
        Assert.True(await TerminalAppHost.WaitFor(() => host.Written.Contains("pasted", StringComparison.Ordinal)), "Ctrl+Shift+V did not paste: " + host.Written);
    }

    [AvaloniaFact]
    public async Task Shell_integration_marks_prompts_with_exit_codes_and_ctrl_up_down_move_between_commands()
    {
        await using var app = await TerminalAppHost.StartAsync();
        await app.RunAsync(TerminalCommandSet.New);
        Assert.True(await app.WaitForScreenAsync("$"), "no prompt: " + app.ScreenText());
        var session = app.Terminals.ActiveSession!;
        // a bash configured as VS Code's integration script configures it: A before the prompt, B after it, D;$? before the next one
        await session.SendTextAsync("""PROMPT_COMMAND='printf "\033]633;D;%s\007" $?'; PS1='\[\033]633;A\007\]$ \[\033]633;B\007\]'""");
        await session.SendTextAsync("seq 1 40; true");
        await session.SendTextAsync("seq 41 80; false");
        await session.SendTextAsync("echo end-$((1+1))");
        Assert.True(await app.WaitForScreenAsync("end-2"), app.ScreenText());
        var control = app.View!.Control;
        Assert.True(await TerminalAppHost.WaitFor(() => control.Snapshot is { } s && s.Rows.Count(r => r.Mark == CsCode.Terminal.Emulator.RowMark.PromptStart) >= 2));
        control.Focus();
        Assert.True(await TerminalAppHost.WaitFor(() => control.IsKeyboardFocusWithin));

        CsCode.Terminal.Emulator.TerminalRow Top() => control.Snapshot!.Rows[0];
        bool TopIs(string text) => Top().GetText().StartsWith(text, StringComparison.Ordinal);
        async Task Press(Key key)
        {
            var before = control.Snapshot!.ViewportTop;
            app.Window.KeyPress(key, RawInputModifiers.Control, key == Key.Up ? PhysicalKey.ArrowUp : PhysicalKey.ArrowDown, null);
            await TerminalAppHost.WaitFor(() => control.Snapshot!.ViewportTop != before, 1000);
        }
        // the echo command's prompt is on the last screen (it cannot reach the top): Ctrl+Up goes past it to the "false" command's prompt
        for (var i = 0; i < 2 && !TopIs("$ seq 41 80"); i++) await Press(Key.Up);
        Assert.True(TopIs("$ seq 41 80"), "Ctrl+Up did not bring the previous command to the top: " + app.ScreenText());
        Assert.Equal(CsCode.Terminal.Emulator.RowMark.PromptStart, Top().Mark);
        Assert.Equal(1, Top().MarkExitCode);
        await Press(Key.Up);
        Assert.True(TopIs("$ seq 1 40"), "Ctrl+Up did not go to the first command: " + app.ScreenText());
        Assert.Equal(0, Top().MarkExitCode);
        app.Capture("terminal-shell-integration");
        await Press(Key.Down);
        Assert.True(TopIs("$ seq 41 80"), "Ctrl+Down did not go to the next command: " + app.ScreenText());
        for (var i = 0; i < 3 && !control.Snapshot!.IsAtBottom; i++) await Press(Key.Down);
        Assert.True(control.Snapshot!.IsAtBottom, "Ctrl+Down past the last prompt did not return to the bottom");
        Assert.Equal(true, app.Get<IContextKeyService>().Get("terminalFocus")); // the keys ran terminal commands, the shell got none of them
    }

    [AvaloniaFact]
    public async Task Confirm_on_exit_asks_after_the_main_windows_own_close_handling_and_closes_on_terminate()
    {
        await using var app = await TerminalAppHost.StartAsync(settings: new() { ["terminal.integrated.confirmOnExit"] = "always" });
        await app.RunAsync(TerminalCommandSet.New);
        Assert.True(await app.WaitForScreenAsync("$"), "no prompt: " + app.ScreenText());
        var closed = false;
        app.Window.Closed += (_, _) => closed = true;
        var notifications = app.Get<CsCode.App.Notifications.NotificationCenter>();
        app.Window.Close(); // the main window first runs its own close (save prompts), then the terminal asks
        Assert.True(await TerminalAppHost.WaitFor(() => notifications.Toasts.Any(t => t.Message.Contains("terminate the active terminal session", StringComparison.Ordinal))),
            "no question about the running terminal");
        Assert.False(closed);
        var toast = notifications.Toasts.First(t => t.Message.Contains("terminate", StringComparison.Ordinal));
        toast.InvokeCommand.Execute(toast.Actions.First(a => a.Title == "Terminate"));
        Assert.True(await TerminalAppHost.WaitFor(() => closed), "Terminate did not close the window");
    }

    [AvaloniaFact]
    public async Task Open_in_terminal_starts_the_shell_in_that_folder()
    {
        await using var app = await TerminalAppHost.StartAsync();
        var folder = app.Sandbox.Combine("project");
        Directory.CreateDirectory(folder);
        await app.RunAsync(TerminalCommandSet.NewHere, new Uri(folder + "/"));
        Assert.True(await app.WaitForScreenAsync("$"));
        await app.Terminals.ActiveSession!.SendTextAsync("pwd");
        Assert.True(await app.WaitForScreenAsync(folder), app.ScreenText());
    }

    [AvaloniaFact]
    public async Task A_theme_switch_repaints_the_terminal_in_the_new_colours()
    {
        await using var app = await TerminalAppHost.StartAsync();
        await app.RunAsync(TerminalCommandSet.New);
        Assert.True(await app.WaitForScreenAsync("$"));
        var before = BackgroundPixel(app);
        var themes = app.Get<IColorThemeService>();
        var light = themes.Available.First(t => t.Kind == ThemeKind.Light);
        await themes.SetCurrentAsync(light.Id);
        Assert.True(await TerminalAppHost.WaitFor(() => BackgroundPixel(app) != before, 10_000), "the terminal kept its colours after the theme switch");
        app.Capture("terminal-light-theme");
    }

    private static Avalonia.Media.Color BackgroundPixel(TerminalAppHost app)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = app.Window.CaptureRenderedFrame()!;
        var control = app.View!.Control;
        var p = control.TranslatePoint(new Avalonia.Point(control.Bounds.Width - 10, control.Bounds.Height - 6), app.Window)!.Value;
        return Pixel(frame, (int)p.X, (int)p.Y);
    }

    internal static unsafe Avalonia.Media.Color Pixel(WriteableBitmap bitmap, int x, int y)
    {
        using var fb = bitmap.Lock();
        var ptr = (byte*)fb.Address + y * fb.RowBytes + x * 4;
        return fb.Format == Avalonia.Platform.PixelFormat.Rgba8888
            ? Avalonia.Media.Color.FromArgb(ptr[3], ptr[0], ptr[1], ptr[2])
            : Avalonia.Media.Color.FromArgb(ptr[3], ptr[2], ptr[1], ptr[0]);
    }
}
