using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CsCode.Core.Platform;
using CsCode.Panels.Terminal.Input;
using CsCode.Panels.Terminal.Tests.Infrastructure;
using CsCode.Panels.Terminal.Views;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.Panels.Terminal.Tests;

/// <summary>The Terminal view headless over fake PTYs: rendering, keys and text to bytes, paste, selection and copy, mouse reports, links.</summary>
public sealed class PanelTests
{
    [AvaloniaFact]
    public async Task Showing_the_view_creates_a_terminal_sized_to_the_control()
    {
        await using var rig = await new PanelRig(900, 400).ReadyAsync();
        var (cols, rows) = rig.Control.GridSize;
        Assert.True(cols > 40 && rows > 5, $"{cols}x{rows}");
        Assert.True(await PanelRig.Pump(() => rig.Host.Sizes.Contains((cols, rows))), string.Join(",", rig.Host.Sizes));
    }

    [AvaloniaFact]
    public async Task Output_is_drawn_and_unchanged_rows_are_reused()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        await rig.OutputAsync("\e[31mred\e[0m plain\r\nsecond\r\nthird", "third");
        Assert.Equal("red plain", rig.Control.Snapshot!.Rows[0].GetText());
        Assert.Equal(TermColorIndexed(1), rig.Control.Snapshot.Rows[0][0].Attributes.Foreground);
        await rig.OutputAsync("\e[2;1Hchanged", "changed");
        Assert.True(rig.Control.Painter.RowsBuiltLastFrame <= 2, $"rebuilt {rig.Control.Painter.RowsBuiltLastFrame} rows for a one-row change");
    }

    private static uint TermColorIndexed(int i) => CsCode.Terminal.Emulator.TermColor.Indexed(i);

    [AvaloniaFact]
    public async Task Keys_and_text_reach_the_program_as_xterm_bytes()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        rig.Control.Focus();
        await PanelRig.Pump(() => rig.Control.IsKeyboardFocusWithin);
        rig.Host.ClearWritten();
        rig.Window.KeyTextInput("ls -la");
        rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        rig.Window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        rig.Window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        rig.Window.KeyTextInput("c"); // some platforms also send the character: it must not be typed
        rig.Window.KeyPress(Key.B, RawInputModifiers.Alt, PhysicalKey.B, "b");
        await PanelRig.Pump(() => false, 30);
        Assert.Equal("ls -la\r\e[A\x03\eb", rig.Host.Written);
        Assert.Contains(true, rig.Host.UrgentFlags); // Ctrl+C overtakes queued input
        Assert.True(rig.Keys.Get(TerminalPanelRuntime.FocusContextKey) is true);
    }

    [AvaloniaFact]
    public async Task Confirm_on_exit_holds_the_window_close_until_the_user_agrees()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        var closed = false;
        rig.Window.Closed += (_, _) => closed = true;
        rig.Config.Set("terminal.integrated.confirmOnExit", "always");
        rig.WindowService.MessageActionResponses.Enqueue("Cancel");
        rig.Window.Close();
        Assert.True(await PanelRig.Pump(() => rig.WindowService.Messages.Count > 0));
        Assert.Contains(rig.WindowService.Messages, m => m.Message == "Do you want to terminate the active terminal session?");
        await PanelRig.Pump(() => closed, 300);
        Assert.False(closed, "the window closed although the user cancelled");

        rig.WindowService.MessageActionResponses.Enqueue("Terminate");
        rig.Window.Close();
        Assert.True(await PanelRig.Pump(() => closed), "Terminate did not close the window");
        Assert.Equal(2, rig.WindowService.Messages.Count);
    }

    [AvaloniaFact]
    public async Task Confirm_on_exit_with_child_processes_asks_only_when_a_program_runs_in_a_terminal()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        var closed = false;
        rig.Window.Closed += (_, _) => closed = true;
        rig.Config.Set("terminal.integrated.confirmOnExit", "hasChildProcesses");
        rig.Window.Close(); // only the shell runs (the fake PTY's foreground process is the one it started with)
        Assert.True(await PanelRig.Pump(() => closed), "the window did not close");
        Assert.Empty(rig.WindowService.Messages);
    }

    [AvaloniaFact]
    public async Task Application_cursor_mode_changes_the_arrow_keys()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        await rig.OutputAsync("\e[?1h", null);
        await PanelRig.Pump(() => rig.Session.Modes.ApplicationCursorKeys);
        rig.Control.Focus();
        rig.Host.ClearWritten();
        rig.Window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Equal("\eOA", rig.Host.Written);
    }

    [AvaloniaFact]
    public async Task A_keystroke_costs_the_ui_thread_well_under_a_millisecond()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        rig.Control.Focus();
        for (var i = 0; i < 50; i++) rig.Window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null); // warm up
        var times = new List<double>();
        for (var i = 0; i < 500; i++)
        {
            var sw = Stopwatch.StartNew();
            rig.Window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        // includes Avalonia's own routing of the event; the terminal's part is encoding + an enqueue
        Assert.True(times[times.Count / 2] < 1.0, $"median {times[times.Count / 2]:F3} ms per keystroke");
    }

    [AvaloniaFact]
    public async Task Paste_is_bracketed_when_the_program_asked_and_warns_otherwise_for_several_lines()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        rig.Clipboard.SetExternal("echo one\necho two\n");
        await rig.OutputAsync("\e[?2004h", null);
        await PanelRig.Pump(() => rig.Session.Modes.BracketedPaste);
        rig.Host.ClearWritten();
        await rig.Control.PasteAsync();
        Assert.Equal("\e[200~echo one\recho two\r\e[201~", rig.Host.Written);
        Assert.Empty(rig.WindowService.Messages);

        await rig.OutputAsync("\e[?2004l", null);
        await PanelRig.Pump(() => !rig.Session.Modes.BracketedPaste);
        rig.Host.ClearWritten();
        rig.WindowService.MessageActionResponses.Enqueue("Cancel");
        await rig.Control.PasteAsync();
        Assert.Contains(rig.WindowService.Messages, m => m.Message.Contains("paste 2 lines", StringComparison.Ordinal));
        Assert.Equal("", rig.Host.Written);
        rig.WindowService.MessageActionResponses.Enqueue("Paste");
        await rig.Control.PasteAsync();
        Assert.Equal("echo one\recho two\r", rig.Host.Written);
    }

    [AvaloniaFact]
    public async Task Dragging_selects_and_copy_puts_the_text_on_the_clipboard_and_primary()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        await rig.OutputAsync("first line here\r\nsecond line", "second line");
        rig.Window.MouseDown(rig.Cell(6, 0), MouseButton.Left, RawInputModifiers.None);
        rig.Window.MouseMove(rig.Cell(5, 1), RawInputModifiers.LeftMouseButton);
        rig.Window.MouseUp(rig.Cell(5, 1), MouseButton.Left, RawInputModifiers.None);
        await PanelRig.Pump(() => rig.Clipboard.OfferedPrimary is not null);
        Assert.Equal("line here\nsecond", await rig.Control.GetSelectionTextAsync());
        Assert.Equal("line here\nsecond", rig.Clipboard.OfferedPrimary); // PRIMARY: select to copy
        Assert.Null(rig.Clipboard.Content); // copyOnSelection is off by default
        await rig.Commands.ExecuteAsync(TerminalCommandSet.CopySelection);
        Assert.Equal("line here\nsecond", rig.Clipboard.Content?.Text);
    }

    [AvaloniaFact]
    public async Task Right_click_behavior_copy_paste_copies_a_selection_else_pastes()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        rig.Config.Set("terminal.integrated.rightClickBehavior", "copyPaste");
        await rig.OutputAsync("copy me please", "please");
        rig.Window.MouseDown(rig.Cell(5, 0), MouseButton.Left, RawInputModifiers.None);
        rig.Window.MouseMove(rig.Cell(6, 0), RawInputModifiers.LeftMouseButton);
        rig.Window.MouseUp(rig.Cell(6, 0), MouseButton.Left, RawInputModifiers.None);
        Assert.NotNull(rig.Control.Selection);
        rig.Window.MouseDown(rig.Cell(1, 1), MouseButton.Right, RawInputModifiers.None);
        rig.Window.MouseUp(rig.Cell(1, 1), MouseButton.Right, RawInputModifiers.None);
        Assert.True(await PanelRig.Pump(() => rig.Clipboard.Content?.Text == "me"), "right-click did not copy the selection");
        Assert.True(await PanelRig.Pump(() => rig.Control.Selection is null), "the copied selection stayed");
        rig.Host.ClearWritten();
        rig.Window.MouseDown(rig.Cell(1, 1), MouseButton.Right, RawInputModifiers.None);
        rig.Window.MouseUp(rig.Cell(1, 1), MouseButton.Right, RawInputModifiers.None);
        Assert.True(await PanelRig.Pump(() => rig.Host.Written == "me"), "right-click without a selection did not paste: " + rig.Host.Written);
    }

    [AvaloniaFact]
    public async Task Find_selects_matches_up_and_down_the_output_and_scrolls_them_into_view()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        var lines = string.Concat(Enumerable.Range(0, 120).Select(i => i % 40 == 5 ? $"line {i} has a Needle\r\n" : $"line {i}\r\n"));
        await rig.OutputAsync(lines + "end", "end");
        await rig.Commands.ExecuteAsync(TerminalCommandSet.FocusFind);
        Assert.True(await PanelRig.Pump(() => rig.View.IsFindVisible && rig.View.FindBox.IsKeyboardFocusWithin), "Ctrl+F did not open and focus the find bar");

        // typing searches up from the bottom: the last match first; each match is selected and on screen
        rig.View.FindBox.Text = "needle";
        Assert.Equal(("Needle", 85), await Selected(rig));
        // Enter: further up; Shift+Enter: back down; past the top it wraps to the bottom
        rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(("Needle", 45), await Selected(rig, after: 85));
        rig.Window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
        Assert.Equal(("Needle", 85), await Selected(rig, after: 45));
        rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(("Needle", 45), await Selected(rig, after: 85));
        rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(("Needle", 5), await Selected(rig, after: 45));
        rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(("Needle", 85), await Selected(rig, after: 5));

        // Escape goes back to the terminal; a missing text says so
        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.True(await PanelRig.Pump(() => !rig.View.IsFindVisible && rig.Control.IsKeyboardFocusWithin), "Escape did not close the find bar");
        await rig.Commands.ExecuteAsync(TerminalCommandSet.FocusFind);
        Assert.True(await PanelRig.Pump(() => rig.View.FindBox.IsKeyboardFocusWithin));
        rig.View.FindBox.Text = "haystack";
        Assert.True(await PanelRig.Pump(() => rig.View.FindStatus == "No results"), "no 'No results' for a missing text");
    }

    /// <summary>Waits until the selection is on screen on a "line N" row other than <paramref name="after"/>: the selected text and N.</summary>
    private static async Task<(string Text, int SourceLine)> Selected(PanelRig rig, int after = -1)
    {
        (string, int) result = ("", -1);
        await PanelRig.Pump(() =>
        {
            if (rig.Control.Selection is not { } sel || rig.Control.Snapshot is not { } snap) return false;
            var index = sel.Start.Line - snap.ViewportTop;
            if (index < 0 || index >= snap.RowCount) return false; // not scrolled into view (yet)
            var row = snap.Rows[index];
            var n = int.TryParse(row.GetText().Split(' ').ElementAtOrDefault(1), out var v) ? v : -1;
            result = (row.GetText(sel.Start.Column, sel.End.Column + 1), n);
            return n >= 0 && n != after;
        });
        return result;
    }

    [AvaloniaFact]
    public async Task Double_click_selects_a_word_and_triple_click_the_line()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        rig.Config.Set("terminal.integrated.copyOnSelection", true);
        await rig.OutputAsync("cat /etc/hosts now", "now");
        rig.Window.MouseDown(rig.Cell(7, 0), MouseButton.Left, RawInputModifiers.None);
        rig.Window.MouseUp(rig.Cell(7, 0), MouseButton.Left, RawInputModifiers.None);
        rig.Window.MouseDown(rig.Cell(7, 0), MouseButton.Left, RawInputModifiers.None);
        rig.Window.MouseUp(rig.Cell(7, 0), MouseButton.Left, RawInputModifiers.None);
        await PanelRig.Pump(() => rig.Clipboard.Content is not null);
        Assert.Equal("/etc/hosts", rig.Clipboard.Content?.Text);
        rig.Window.MouseDown(rig.Cell(7, 0), MouseButton.Left, RawInputModifiers.None);
        rig.Window.MouseUp(rig.Cell(7, 0), MouseButton.Left, RawInputModifiers.None);
        await PanelRig.Pump(() => rig.Clipboard.Content?.Text == "cat /etc/hosts now");
        Assert.Equal("cat /etc/hosts now", rig.Clipboard.Content?.Text);
    }

    [AvaloniaFact]
    public async Task Mouse_reports_go_to_a_program_that_asked_and_shift_bypasses_them()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        await rig.OutputAsync("\e[?1000h\e[?1006h", null);
        await PanelRig.Pump(() => rig.Session.Modes.Mouse == CsCode.Terminal.Emulator.MouseTracking.Normal);
        rig.Host.ClearWritten();
        rig.Window.MouseDown(rig.Cell(3, 2), MouseButton.Left, RawInputModifiers.None);
        rig.Window.MouseUp(rig.Cell(3, 2), MouseButton.Left, RawInputModifiers.None);
        Assert.Equal("\e[<0;4;3M\e[<0;4;3m", rig.Host.Written);
        rig.Host.ClearWritten();
        rig.Window.MouseDown(rig.Cell(3, 2), MouseButton.Left, RawInputModifiers.Shift);
        rig.Window.MouseUp(rig.Cell(3, 2), MouseButton.Left, RawInputModifiers.Shift);
        Assert.Equal("", rig.Host.Written);
    }

    [AvaloniaFact]
    public async Task Ctrl_click_on_a_path_opens_it_in_the_editor_at_the_position()
    {
        using var dir = new TempDirectory("cscode-term-links-");
        var file = dir.WriteFile("src/app.cs", "line1\nline2\nline3\n");
        await using var rig = await new PanelRig().ReadyAsync();
        await rig.OutputAsync($"\e]7;file://host{dir.Path}\e\\error at src/app.cs:3:2 here", "here");
        await PanelRig.Pump(() => rig.Session.CurrentDirectory == dir.Path);
        rig.Window.MouseDown(rig.Cell(12, 0), MouseButton.Left, RawInputModifiers.Control);
        rig.Window.MouseUp(rig.Cell(12, 0), MouseButton.Left, RawInputModifiers.Control);
        Assert.True(await PanelRig.Pump(() => rig.Navigation.Opened.Count > 0), "nothing was opened");
        var (uri, selection) = rig.Navigation.Opened[0];
        Assert.Equal(file, uri.LocalPath);
        Assert.Equal(2, selection!.Value.Start.Line);
        Assert.Equal(1, selection.Value.Start.Character);
    }

    [AvaloniaFact]
    public async Task Ctrl_click_on_a_web_link_opens_the_browser_and_other_schemes_ask_first()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        var opened = new List<Uri>();
        await rig.OutputAsync("go https://example.com/x and ssh://host/y", "ssh://");
        await rig.Runtime.OpenLinkAsync(LinkDetector.At(rig.Control.Snapshot!.Rows[0], 5, 80)!, rig.Session);
        Assert.Empty(rig.WindowService.Messages); // http(s): no confirmation
        rig.WindowService.MessageActionResponses.Enqueue("Cancel");
        await rig.Runtime.OpenLinkAsync(LinkDetector.At(rig.Control.Snapshot.Rows[0], 30, 80)!, rig.Session);
        Assert.Contains(rig.WindowService.Messages, m => m.Message.Contains("ssh://host/y", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Wheel_scrolls_the_scrollback_and_typing_returns_to_the_bottom()
    {
        await using var rig = await new PanelRig(900, 200).ReadyAsync();
        await rig.OutputAsync(string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i}\r\n")) + "end", "end");
        rig.Window.MouseWheel(rig.Cell(5, 2), new Avalonia.Vector(0, 2), RawInputModifiers.None);
        Assert.True(await PanelRig.Pump(() => rig.Control.Snapshot is { IsAtBottom: false }), "the wheel did not scroll back");
        var top = rig.Control.Snapshot!.ViewportTop;
        await rig.OutputAsync("more output\r\n", null);
        Assert.Equal(top, rig.Control.Snapshot!.ViewportTop); // reading position kept while output arrives
        rig.Control.Focus();
        rig.Window.KeyTextInput("x");
        Assert.True(await PanelRig.Pump(() => rig.Control.Snapshot is { IsAtBottom: true }), "typing did not return to the bottom");
    }

    [AvaloniaFact]
    public async Task On_the_alternate_screen_the_wheel_sends_cursor_keys()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        await rig.OutputAsync("\e[?1049h", null);
        await PanelRig.Pump(() => rig.Session.Modes.AlternateScreen);
        rig.Host.ClearWritten();
        rig.Window.MouseWheel(rig.Cell(5, 2), new Avalonia.Vector(0, -1), RawInputModifiers.None);
        Assert.Equal("\e[B\e[B\e[B", rig.Host.Written);
    }

    [AvaloniaFact]
    public async Task Exit_shows_the_code_and_the_terminal_keeps_its_buffer()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        await rig.OutputAsync("last words", "last words");
        rig.Host.Exit(3);
        Assert.True(await PanelRig.Pump(() => rig.ScreenText().Contains("[The terminal process exited with code 3.]", StringComparison.Ordinal)), rig.ScreenText());
        Assert.Contains("last words", rig.ScreenText());
    }

    [AvaloniaFact]
    public async Task Context_menu_has_copy_paste_select_all_clear_split_rename_kill()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        var entries = rig.Runtime.ContextMenuEntries(rig.Control).Where(e => !e.IsSeparator).Select(e => e.AccessibleName).ToList();
        Assert.Equal(["Copy", "Paste", "Select All", "Clear Terminal", "Split Terminal", "Rename...", "Kill Terminal"], entries);
        // a split pane offers Unsplit too
        await rig.Commands.ExecuteAsync(TerminalCommandSet.Split);
        Assert.True(await PanelRig.Pump(() => rig.View.Panes.Count == 2));
        entries = rig.Runtime.ContextMenuEntries(rig.Control).Where(e => !e.IsSeparator).Select(e => e.AccessibleName).ToList();
        Assert.Equal(["Copy", "Paste", "Select All", "Clear Terminal", "Split Terminal", "Unsplit Terminal", "Rename...", "Kill Terminal"], entries);
    }

    [AvaloniaFact]
    public async Task Commands_kill_clear_rename_and_cycle()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        var first = rig.Session;
        await rig.Commands.ExecuteAsync(TerminalCommandSet.New);
        await PanelRig.Pump(() => rig.Service.Terminals.Count == 2 && !ReferenceEquals(rig.Service.ActiveSession, first));
        var second = rig.Session;
        await PanelRig.Pump(() => ReferenceEquals(rig.Control.Session, second));
        await rig.Commands.ExecuteAsync(TerminalCommandSet.FocusPrevious);
        Assert.Same(first, rig.Service.ActiveSession);
        await rig.Commands.ExecuteAsync(TerminalCommandSet.Rename, "my shell");
        Assert.Equal("my shell", first.Title);
        await rig.OutputAsync("garbage\r\nmore\r\n$ ", "more");
        await rig.Commands.ExecuteAsync(TerminalCommandSet.Clear);
        Assert.True(await PanelRig.Pump(() => rig.ScreenText().Trim() == "$"), rig.ScreenText());
        await rig.Commands.ExecuteAsync(TerminalCommandSet.Kill);
        Assert.Same(second, Assert.Single(rig.Service.Terminals));
    }

    [AvaloniaFact]
    public async Task Send_sequence_writes_raw_text()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        rig.Host.ClearWritten();
        using var doc = System.Text.Json.JsonDocument.Parse("{\"text\":\"\\u001b[Aabc\"}");
        await rig.Commands.ExecuteAsync(TerminalCommandSet.SendSequence, doc.RootElement.Clone());
        Assert.Equal("\e[Aabc", rig.Host.Written);
    }

    [AvaloniaFact]
    public async Task Hidden_views_ask_for_no_frames()
    {
        await using var rig = await new PanelRig().ReadyAsync();
        var frames = 0;
        using var sub = rig.Session.FrameReady.Subscribe(_ => Interlocked.Increment(ref frames));
        rig.Window.Hide();
        for (var i = 0; i < 200; i++) rig.Host.Output($"line {i}\r\n");
        await PanelRig.Pump(() => false, 300);
        Assert.True(frames <= 1, $"{frames} frames built for a hidden view");
    }
}
