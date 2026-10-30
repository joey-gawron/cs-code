using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
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
        // Esc: the keyboard goes back to the editor by itself (VS Code, Visual Studio), and typing reaches it.
        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.True(await RealAppHost.WaitFor(() => running.IsCompleted));
        await running;
        Assert.True(await RealAppHost.WaitFor(() => Keys(host) == (true, false, false, false, false)), Keys(host).ToString());
        Assert.True(tab.Controller.IsFocused);
        host.Window.KeyTextInput("x"); // Vim is on (the default): x deletes the character under the caret
        Assert.Equal("lpha\nbeta\n", tab.Doc.GetText());
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_file_opened_from_the_palette_takes_the_keyboard()
    {
        using var sandbox = new TempDirectory("cscode-focus-sandbox-");
        await using var host = await Create(sandbox).StartAsync();
        host.Files.WriteFile("b.txt", "beta\n");
        host.Files.WriteFile("c.txt", "gamma\n");
        await host.Get<IWorkspaceService>().OpenFolderAsync(new Uri(host.Files.Path + "/"));
        var a = await host.Shell.Well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => a.Controller is not null && a.LiveView!.IsAttachedToVisualTree()));
        a.Controller!.Focus();

        var quick = host.Get<QuickInput.QuickInputViewModel>();
        var running = await host.OpenPaletteAsync(KnownCommands.QuickOpen);
        quick.Text = "b.txt";
        Assert.True(await RealAppHost.WaitFor(() => quick.Items.Any(i => i.Label == "b.txt")), string.Join(", ", quick.Items.Select(i => i.Label)));
        host.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.True(await RealAppHost.WaitFor(() => running.IsCompleted));
        await running;
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.ActiveEditor?.Title == "b.txt" && host.Shell.Well.ActiveEditor.Controller?.IsFocused == true),
            $"active {host.Shell.Well.ActiveEditor?.Title}, focused {host.Window.FocusManager?.GetFocusedElement()}");
        Assert.Equal((true, false, false, false, false), Keys(host));

        // Also from a tool window: the palette gives the keyboard back to the Explorer when it closes, then the file it opened takes it (VS Code).
        host.Shell.Tools.Show("explorer");
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Tools.Get("explorer")!.LiveView?.GetVisualDescendants().OfType<ListBox>().Any(l => l.IsEffectivelyVisible) == true));
        var list = host.Shell.Tools.Get("explorer")!.LiveView!.GetVisualDescendants().OfType<ListBox>().First(l => l.IsEffectivelyVisible);
        Assert.True(await RealAppHost.WaitFor(() => list.GetVisualDescendants().OfType<ListBoxItem>().Any()));
        list.GetVisualDescendants().OfType<ListBoxItem>().First().Focus(); // a row, as a click does
        Assert.True(Keys(host).List, $"{Keys(host)} focused={host.Window.FocusManager?.GetFocusedElement()}");
        running = await host.OpenPaletteAsync(KnownCommands.QuickOpen);
        quick.Text = "c.txt"; // not open yet: it opens after the palette has given the keyboard back
        Assert.True(await RealAppHost.WaitFor(() => quick.Items.FirstOrDefault()?.Label == "c.txt"), string.Join(", ", quick.Items.Select(i => i.Label)));
        host.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.True(await RealAppHost.WaitFor(() => running.IsCompleted));
        await running;
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.ActiveEditor?.Title == "c.txt" && host.Shell.Well.ActiveEditor.Controller?.IsFocused == true),
            $"active {host.Shell.Well.ActiveEditor?.Title}, focused {host.Window.FocusManager?.GetFocusedElement()}");
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_palette_command_that_ends_after_the_user_moved_on_leaves_the_keyboard_where_they_put_it()
    {
        using var sandbox = new TempDirectory("cscode-focus-sandbox-");
        await using var host = await Create(sandbox).StartAsync();
        host.Files.WriteFile("b.txt", "beta\n");
        var well = host.Shell.Well;
        var b = await well.OpenAsync(new Uri(host.Files.Combine("b.txt")));
        var a = await well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => a.Controller is not null && a.LiveView!.IsAttachedToVisualTree()));
        host.Shell.Tools.Show("debug.console");
        await DevScene.SettleAsync(10);
        var console = host.Shell.Tools.Get("debug.console")!.LiveView!;
        Assert.True(await RealAppHost.WaitFor(() => console.GetVisualDescendants().OfType<TextBox>().Any(t => t.IsEffectivelyVisible && t.Bounds.Width > 0)));
        var input = console.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Bounds.Width > 0);
        a.Controller!.Focus();

        // A palette command that runs for a while, as Start Debugging waits for its build and the adapter.
        var release = new TaskCompletionSource();
        using var slow = host.Get<ICommandService>().Register("test.slowCommand", async _ => { await release.Task; return null; },
            new CommandMetadata("test.slowCommand", "Slow Test Command", "Test"));
        var quick = host.Get<QuickInput.QuickInputViewModel>();
        var running = await host.OpenPaletteAsync(KnownCommands.CommandPalette);
        quick.Text = ">Slow Test Command";
        Assert.True(await RealAppHost.WaitFor(() => quick.Items.FirstOrDefault()?.Label == "Test: Slow Test Command"), string.Join(", ", quick.Items.Take(5).Select(i => i.Label)));
        host.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.True(await RealAppHost.WaitFor(() => !quick.IsVisible && a.Controller.IsFocused), "the palette did not give the keyboard back");

        // Meanwhile the user goes to the other tab (Ctrl+Tab), then clicks into the Debug Console's box.
        host.Window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
        host.Window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
        Assert.True(await RealAppHost.WaitFor(() => ReferenceEquals(well.ActiveEditor, b) && b.Controller?.IsFocused == true), "Ctrl+Tab did not switch tabs");
        var at = Avalonia.VisualExtensions.TranslatePoint(input, new Avalonia.Point(input.Bounds.Width / 2, input.Bounds.Height / 2), host.Window)!.Value;
        host.Window.MouseDown(at, MouseButton.Left);
        host.Window.MouseUp(at, MouseButton.Left);
        Assert.True(await RealAppHost.WaitFor(() => input.IsFocused), $"the click did not focus the box: {host.Window.FocusManager?.GetFocusedElement()}");

        // The command ends: the keyboard stays in the box (it used to be pulled into the editor that had become active).
        release.SetResult();
        Assert.True(await RealAppHost.WaitFor(() => running.IsCompleted));
        await running;
        await Task.Delay(600); // longer than the editor focus waits for its view
        await DevScene.SettleAsync(5);
        Assert.True(input.IsFocused, $"the keyboard moved to {host.Window.FocusManager?.GetFocusedElement()}");
        Assert.False(b.Controller!.IsFocused);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_file_clicked_in_the_Explorer_opens_as_a_preview_and_the_keyboard_stays_in_the_Explorer()
    {
        // The Explorer opens with preserveFocus off; the editor that takes the active tab's place gets the keyboard only from that tab's editor or
        // from nowhere, so a single click (a preview) leaves it in the Explorer for the next arrow key, as in VS Code and Visual Studio.
        using var sandbox = new TempDirectory("cscode-focus-sandbox-");
        await using var host = await Create(sandbox).StartAsync();
        host.Files.WriteFile("b.txt", "beta\n");
        await host.Get<IWorkspaceService>().OpenFolderAsync(new Uri(host.Files.Path + "/"));
        var well = host.Shell.Well;
        var a = await well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        Assert.True(await RealAppHost.WaitFor(() => a.Controller is not null && a.LiveView!.IsAttachedToVisualTree()));
        a.Controller!.Focus();
        host.Shell.Tools.Show("explorer");
        var explorer = host.Shell.Tools.Get("explorer")!;
        ListBoxItem? Row(string name) => explorer.LiveView?.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(r => r.IsEffectivelyVisible && r.Bounds.Height > 0 && r.DataContext is ToolWindows.Explorer.ExplorerNode { Name: var n } && n == name);
        Assert.True(await RealAppHost.WaitFor(() => Row("b.txt") is not null), "no b.txt row in the Explorer");
        await DevScene.SettleAsync(5);
        var row = Row("b.txt")!;
        var at = Avalonia.VisualExtensions.TranslatePoint(row, new Avalonia.Point(row.Bounds.Width / 2, row.Bounds.Height / 2), host.Window)!.Value;
        host.Window.MouseDown(at, MouseButton.Left);
        host.Window.MouseUp(at, MouseButton.Left);

        Assert.True(await RealAppHost.WaitFor(() => well.ActiveEditor is { Title: "b.txt", IsPreview: true } b && b.Controller is not null), $"no preview of b.txt: {well.ActiveEditor?.Title}");
        var preview = well.ActiveEditor!;
        await Task.Delay(600); // longer than the editor focus waits for its view
        await DevScene.SettleAsync(5);
        Assert.True(explorer.LiveView!.IsKeyboardFocusWithin, $"the keyboard moved to {host.Window.FocusManager?.GetFocusedElement()}");
        Assert.False(preview.Controller!.IsFocused);
        Assert.Equal((false, false, false, true, false), Keys(host)); // listFocus: the Explorer's keys (Enter, F2, Delete) apply
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task After_a_close_prompt_the_editor_that_takes_the_closed_ones_place_has_the_keyboard()
    {
        using var sandbox = new TempDirectory("cscode-focus-sandbox-");
        await using var host = await Create(sandbox).StartAsync();
        host.Files.WriteFile("b.txt", "beta\n");
        var well = host.Shell.Well;
        var a = await well.OpenAsync(new Uri(host.Files.Combine("a.txt")));
        var b = await well.OpenAsync(new Uri(host.Files.Combine("b.txt")));
        Assert.True(await RealAppHost.WaitFor(() => b.Controller is not null && b.LiveView!.IsAttachedToVisualTree()));
        b.Controller!.Focus();
        b.Doc.ApplyEdits([new Core.Text.TextEdit(Core.Text.TextRange.At(0, 0), "dirty ")]); // closing asks
        Assert.True(b.IsDirty);

        var dialogs = host.Get<Notifications.DialogHostViewModel>();
        host.Window.KeyPress(Key.F4, RawInputModifiers.Control, PhysicalKey.F4, null);
        Assert.True(await RealAppHost.WaitFor(() => dialogs.IsVisible), "no Save prompt");
        dialogs.Choose("Don't Save");
        Assert.True(await RealAppHost.WaitFor(() => !well.Tabs.Contains(b)));
        Assert.True(await RealAppHost.WaitFor(() => a.Controller?.IsFocused == true), $"focused {host.Window.FocusManager?.GetFocusedElement()}");
        Assert.Equal((true, false, false, false, false), Keys(host));

        // A cancelled prompt gives the keyboard back to the editor that asked.
        a.Doc.ApplyEdits([new Core.Text.TextEdit(Core.Text.TextRange.At(0, 0), "dirty ")]);
        host.Window.KeyPress(Key.F4, RawInputModifiers.Control, PhysicalKey.F4, null);
        Assert.True(await RealAppHost.WaitFor(() => dialogs.IsVisible), "no Save prompt");
        Assert.False(a.Controller!.IsFocused);
        dialogs.Choose("Cancel");
        Assert.True(await RealAppHost.WaitFor(() => a.Controller.IsFocused), $"focused {host.Window.FocusManager?.GetFocusedElement()}");
    }
}
