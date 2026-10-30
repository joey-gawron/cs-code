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

        // Ctrl+H is bound (the editor's Replace) but not in commandsToSkipShell: the shell gets ^H (readline: backward-delete-char)
        var ctrlH = app.Get<IKeybindingService>().All.Where(b => b.Chord.Count == 1 && b.Chord[0] == KeyStrokeParser.ParseStroke("ctrl+h"))
            .Select(b => b.Command).Where(app.Get<ICommandService>().Has).ToArray();
        Assert.True(ctrlH.Length > 0, "precondition: Ctrl+H runs a registered command outside the terminal");
        app.Window.KeyPress(Key.H, RawInputModifiers.Control, PhysicalKey.H, "h");
        // Ctrl+C: ^C to the shell (the editor's copy binding is for the editor)
        app.Window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        await TerminalAppHost.SettleAsync(5);
        Assert.Equal("\x08\x03", host.Written);

        // Ctrl+F is the terminal's own Find (VS Code): the find bar opens and the shell gets nothing
        host.ClearWritten();
        app.Window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        Assert.True(await TerminalAppHost.WaitFor(() => app.View!.IsFindVisible && app.View.FindBox.IsKeyboardFocusWithin), "Ctrl+F did not open Find");
        Assert.Equal("", host.Written);
        app.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.True(await TerminalAppHost.WaitFor(() => !app.View!.IsFindVisible && app.View.Control.IsKeyboardFocusWithin), "Escape did not close Find");
        Assert.Equal("", host.Written);

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
    public async Task Injected_shell_integration_decorates_commands_by_exit_code_without_any_setup_in_the_shell()
    {
        await using var app = await TerminalAppHost.StartAsync();
        await app.RunAsync(TerminalCommandSet.New);
        Assert.True(await app.WaitForScreenAsync("$"), "no prompt: " + app.ScreenText());
        var session = app.Terminals.ActiveSession!;
        Assert.True(session.ShellIntegrationInjected); // the sandboxed profile is bash --noprofile --norc: injected through the first prompt
        await session.SendTextAsync("clear");
        foreach (var line in new[] { "true", "false" })
        {
            // one at a time: typed-ahead lines would be echoed out of place
            await session.SendTextAsync(line);
            Assert.True(await TerminalAppHost.WaitFor(() => session.LastCommand?.Command.CommandLine == line), $"'{line}' was not reported");
        }
        var control = app.View!.Control;
        int RowOf(string text) => Array.FindIndex(control.Snapshot!.Rows, r => r.GetText() == text);
        int? ExitOf(string text) => RowOf(text) is >= 0 and var row ? control.Snapshot!.Rows[row].MarkExitCode : null;
        Assert.True(await TerminalAppHost.WaitFor(() => ExitOf("$ false") == 1 && ExitOf("$ true") == 0),
            "the prompts carry no exit codes: " + app.ScreenText() + " / " + string.Join(",", control.Snapshot!.Rows.Select(r => $"{r.Mark}:{r.MarkExitCode}")));

        // the marks are drawn left of the prompts: success and error colours differ
        Avalonia.Media.Color MarkPixel(string text)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = app.Window.CaptureRenderedFrame()!;
            var cellHeight = control.Font.CellHeight;
            var p = control.TranslatePoint(new Avalonia.Point(Views.TerminalControl.PaddingLeft - 2, Views.TerminalControl.PaddingTop + (RowOf(text) + 0.5) * cellHeight), app.Window)!.Value;
            return Pixel(frame, (int)p.X, (int)p.Y);
        }
        var success = MarkPixel("$ true");
        var error = MarkPixel("$ false");
        Assert.NotEqual(success, error);
        app.Capture("terminal-shell-integration-injected");
        await app.Get<IConfigurationService>().UpdateAsync("terminal.integrated.shellIntegration.decorationsEnabled", "never");
        Assert.True(await TerminalAppHost.WaitFor(() => MarkPixel("$ false") != error, 5000), "decorationsEnabled=never still draws the marks");
    }

    [AvaloniaFact]
    public async Task Ctrl_f_in_a_real_shell_highlights_every_match_and_counts_them()
    {
        await using var app = await TerminalAppHost.StartAsync();
        await app.RunAsync(TerminalCommandSet.Focus);
        Assert.True(await app.WaitForScreenAsync("$"), "no prompt: " + app.ScreenText());
        var session = app.Terminals.ActiveSession!;
        await session.SendTextAsync("for i in 1 2 3 4 5; do echo \"step $i: needle, haystack, needles\"; done");
        Assert.True(await app.WaitForScreenAsync("step 5"), app.ScreenText());
        var view = app.View!;
        view.Control.Focus();
        Assert.True(await TerminalAppHost.WaitFor(() => view.Control.IsKeyboardFocusWithin));
        app.Window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        Assert.True(await TerminalAppHost.WaitFor(() => view.IsFindVisible && view.FindBox.IsKeyboardFocusWithin), "Ctrl+F did not open the find bar");
        app.Window.KeyTextInput("needle");
        // 5 lines x ("needle" + "needles"), plus the command line itself (once each); the rows in view have theirs highlighted
        Assert.True(await TerminalAppHost.WaitFor(() => view.FindCountText == "12 of 12" && view.Control.FindMatches is { Count: > 2 } shown
            && shown.Count == view.Control.Snapshot!.Rows.Sum(r => System.Text.RegularExpressions.Regex.Matches(r.GetText(), "needle").Count)),
            $"{view.FindCountText} / {view.Control.FindMatches?.Count}");
        app.Capture("terminal-find");
        app.Window.KeyPress(Key.W, RawInputModifiers.Alt, PhysicalKey.W, "w");
        Assert.True(await TerminalAppHost.WaitFor(() => view.FindCountText == "6 of 6"), view.FindCountText);
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
    public async Task With_a_vim_editor_open_keys_typed_into_the_terminal_reach_the_shell_and_leave_vim_alone()
    {
        var (app, hosts) = await StartWithFakePtys();
        await using var _ = app;
        var file = app.Sandbox.WriteFile("vim.txt", "first\nsecond\n");
        await app.Get<INavigationService>().OpenAsync(new Uri(file));
        var vim = app.Shell.Vim ?? throw new InvalidOperationException("the composition has no Vim adapter");
        CsCode.Editor.View.CodeEditorControl? view = null;
        Assert.True(await TerminalAppHost.WaitFor(() => (view = app.Shell.Well.ActiveEditor?.LiveView as CsCode.Editor.View.CodeEditorControl) is { } v
            && vim.EngineOf(v) is not null), "the editor (with its Vim engine) did not appear");
        var engine = vim.EngineOf(view!)!;
        Assert.Equal("n", engine.ModeCode);
        var caret = view!.Modal.Controller.Caret;

        await app.RunAsync(TerminalCommandSet.Focus);
        Assert.True(await TerminalAppHost.WaitFor(() => app.View is { } t && t.Control.IsKeyboardFocusWithin && hosts.Count == 1));
        var host = hosts[0];
        host.ClearWritten();

        // "jj" (an Insert-mode escape in many vimrcs, a motion in Normal mode) and Ctrl+V (Vim's block Visual; the editor's paste)
        for (var i = 0; i < 2; i++)
        {
            app.Window.KeyPress(Key.J, RawInputModifiers.None, PhysicalKey.J, "j");
            app.Window.KeyTextInput("j");
            app.Window.KeyRelease(Key.J, RawInputModifiers.None, PhysicalKey.J, "j");
        }
        app.Window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
        app.Window.KeyRelease(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
        await TerminalAppHost.SettleAsync(5);

        Assert.Equal("jj\x16", host.Written);
        Assert.Equal("n", engine.ModeCode);
        Assert.Equal(caret, view.Modal.Controller.Caret);
        Assert.Equal("first\nsecond\n", view.Document.GetText().Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.True(app.View!.Control.IsKeyboardFocusWithin, "the terminal lost the keyboard");
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
    public async Task Ctrl_shift_5_splits_the_terminal_and_alt_arrows_move_between_real_shells()
    {
        await using var app = await TerminalAppHost.StartAsync();
        app.Shell.Menus.Refresh();
        var terminalMenu = app.Shell.Menus.Roots.Single(r => r.Header.Replace("_", "", StringComparison.Ordinal) == "Terminal");
        var splitItem = terminalMenu.Children.Single(c => c.CommandId == TerminalCommandSet.Split);
        Assert.True(splitItem.IsEnabled);
        Assert.Equal("Ctrl+Shift+5", splitItem.Gesture);

        await app.RunAsync(TerminalCommandSet.Focus);
        Assert.True(await app.WaitForScreenAsync("$"), "no prompt: " + app.ScreenText());
        var first = app.Terminals.ActiveSession!;
        var work = app.Sandbox.Combine("left");
        Directory.CreateDirectory(work);
        await first.SendTextAsync($"cd '{work}'");
        // the shell has really changed directory (shell integration reports it) before the split looks
        Assert.True(await TerminalAppHost.WaitFor(() => first.CurrentDirectory == work), $"cwd {first.CurrentDirectory}");
        Assert.True(await TerminalAppHost.WaitFor(() => app.View!.Control.IsKeyboardFocusWithin));

        app.Window.KeyPress(Key.D5, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Digit5, "%");
        Assert.True(await TerminalAppHost.WaitFor(() => app.View!.Panes.Count == 2 && app.View.Panes[1].Control.IsKeyboardFocusWithin),
            "Ctrl+Shift+5 did not split the terminal and focus the new pane");
        var second = app.Terminals.ActiveSession!;
        Assert.NotSame(first, second);
        Assert.Equal(true, app.Get<IContextKeyService>().Get("terminalSplitPaneActive"));
        // the split starts where the first shell is (splitCwd "inherited")
        app.Window.KeyTextInput("pwd");
        app.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.True(await TerminalAppHost.WaitFor(() => app.View!.Panes[1].Control.Snapshot is { } s
            && s.Rows.Any(r => r.GetText() == work), 10_000), "the split did not start in the first shell's directory");

        // Alt+Left: the left pane; typing goes to its shell
        app.Window.KeyPress(Key.Left, RawInputModifiers.Alt, PhysicalKey.ArrowLeft, null);
        Assert.True(await TerminalAppHost.WaitFor(() => app.View!.Panes[0].Control.IsKeyboardFocusWithin && ReferenceEquals(app.Terminals.ActiveSession, first)),
            "Alt+Left did not move to the left pane");
        app.Window.KeyTextInput("echo left-$((40+2))");
        app.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.True(await TerminalAppHost.WaitFor(() => app.View!.Panes[0].Control.Snapshot is { } s && s.Rows.Any(r => r.GetText() == "left-42")),
            "the left shell did not run the command");
        Assert.DoesNotContain(app.View!.Panes[1].Control.Snapshot!.Rows, r => r.GetText().Contains("left-42", StringComparison.Ordinal));

        // Ctrl+Shift+Right: the left pane grows
        var before = app.View.Panes[0].Bounds.Width;
        app.Window.KeyPress(Key.Right, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.ArrowRight, null);
        Assert.True(await TerminalAppHost.WaitFor(() => app.View!.Panes[0].Bounds.Width > before + 10), "Ctrl+Shift+Right did not resize the pane");
        app.Capture("terminal-split");

        // Alt+Right back; kill the right pane: the left one is all that is left and has the keyboard
        app.Window.KeyPress(Key.Right, RawInputModifiers.Alt, PhysicalKey.ArrowRight, null);
        Assert.True(await TerminalAppHost.WaitFor(() => ReferenceEquals(app.Terminals.ActiveSession, second)));
        await app.RunAsync(TerminalCommandSet.Kill);
        Assert.True(await TerminalAppHost.WaitFor(() => app.View!.Panes.Count == 1 && app.View.Control.IsKeyboardFocusWithin
            && ReferenceEquals(app.View.Control.Session, first)), "killing the right pane did not leave the left one focused");
        Assert.Equal(false, app.Get<IContextKeyService>().Get("terminalSplitPaneActive"));
        // without a split Alt+Left belongs to the shell again (readline gets ESC [1;3D)
        Assert.False(app.Runtime.GoesToWorkbench(new CsCode.Core.Platform.KeyStroke("left", false, false, true, false)));
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
