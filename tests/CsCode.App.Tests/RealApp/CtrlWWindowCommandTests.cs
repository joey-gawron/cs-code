using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CsCode.App.Commands;
using CsCode.App.Docking;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Tests.RealApp.Vim;
using CsCode.App.Vim;
using CsCode.Core.Platform;
using CsCode.Editor.View;
using CsCode.Vim;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Vim's window commands with Vim off, on the REAL composition (keymap presets, keyboard pipeline, document well): Ctrl+W then v / s / h / j / k / l /
/// w / W / p / o / c / q / n, the second key with or without Ctrl, while an editor has the keyboard. Before, Ctrl+W closed the tab off the Mac
/// and none of these existed without Vim.
/// </summary>
public class CtrlWWindowCommandTests
{
    private static async Task<RealAppHost> StartAsync(string settings)
    {
        var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("a.txt", "alpha\n");
            h.Files.WriteFile("b.txt", "beta\n");
            var path = h.Home.Combine("config/settings.json");
            var current = File.ReadAllText(path).TrimEnd();
            File.WriteAllText(path, current[..^1].TrimEnd() + ",\n" + settings + "\n}\n");
        });
        await host.StartAsync([host.Files.Combine("a.txt"), host.Files.Combine("b.txt")]);
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Count == 2));
        await DevScene.SettleAsync(10);
        return host;
    }

    private static CodeEditorControl ViewOf(EditorTab tab) => (CodeEditorControl)tab.LiveView!;

    private static async Task FocusAsync(RealAppHost host, EditorTab tab)
    {
        host.Shell.Well.Activate(tab);
        Assert.True(await RealAppHost.WaitFor(() => tab.LiveView is CodeEditorControl { IsEffectivelyVisible: true }), tab.Title + " is not shown");
        ViewOf(tab).Modal.Controller.Focus();
        Assert.True(await RealAppHost.WaitFor(() => ViewOf(tab).Modal.Controller.IsFocused), tab.Title + " did not take the keyboard");
        await DevScene.SettleAsync(3);
        Assert.True(host.Get<IContextKeyService>().Get("editorTextFocus") is true);
    }

    private static void Press(RealAppHost host, Key key, RawInputModifiers modifiers = RawInputModifiers.None, string? text = null)
    {
        host.Window.KeyPress(key, modifiers, PhysicalKey.None, text);
        // a platform may send the character of a key the shell took as text input too: it must not be typed
        if (text is not null) host.Window.KeyTextInput(text);
        host.Window.KeyRelease(key, modifiers, PhysicalKey.None, text);
        Dispatcher.UIThread.RunJobs();
    }

    private static void CtrlW(RealAppHost host) => Press(host, Key.W, RawInputModifiers.Control);

    private static string Text(EditorTab tab) => ViewOf(tab).Document.GetText().Replace("\r\n", "\n");

    private static Point Centre(EditorTab tab)
    {
        var v = ViewOf(tab);
        return v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), Avalonia.Controls.TopLevel.GetTopLevel(v)!)!.Value;
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Ctrl_W_Ctrl_V_splits_the_editor_to_the_right_and_Ctrl_W_h_l_w_move_the_keyboard_between_the_groups()
    {
        await using var host = await StartAsync("\"vim.enable\": false");
        var well = host.Shell.Well;
        var factory = host.Get<ShellDockFactory>();
        var a = well.EditorTabs.Single(t => t.Title == "a.txt");
        var b = well.EditorTabs.Single(t => t.Title == "b.txt");
        await FocusAsync(host, b);
        Assert.Single(factory.DocumentGroups());

        CtrlW(host);
        Assert.True(host.Shell.Keyboard.IsChordPending);             // Ctrl+W starts the chord: it closes nothing
        Assert.Equal(2, well.EditorTabs.Count);
        Press(host, Key.V, RawInputModifiers.Control);
        Assert.True(await RealAppHost.WaitFor(() => factory.DocumentGroups().Count == 2), "Ctrl+W Ctrl+V made no second group");
        Assert.False(host.Shell.Keyboard.IsChordPending);
        Assert.True(await RealAppHost.WaitFor(() => a.LiveView is { IsEffectivelyVisible: true } && b.LiveView is { IsEffectivelyVisible: true }));
        await DevScene.SettleAsync(10);
        // side by side: b moved into a new group to the right of a's
        Assert.NotSame(factory.GroupOf(a), factory.GroupOf(b));
        Assert.True(Centre(b).X > Centre(a).X + 10, $"b at {Centre(b)} is not right of a at {Centre(a)}");
        Assert.True(Math.Abs(Centre(b).Y - Centre(a).Y) < 10, $"b at {Centre(b)} is not beside a at {Centre(a)}");
        await FocusAsync(host, b);

        CtrlW(host);
        Press(host, Key.H, text: "h");                                // Ctrl+W h: the group to the left
        Assert.True(await RealAppHost.WaitFor(() => well.ActiveEditor == a && ViewOf(a).Modal.Controller.IsFocused), well.ActiveEditor?.Title);
        await DevScene.SettleAsync(3);
        CtrlW(host);
        Press(host, Key.L, RawInputModifiers.Control);                // Ctrl+W Ctrl+L: the group to the right
        Assert.True(await RealAppHost.WaitFor(() => well.ActiveEditor == b && ViewOf(b).Modal.Controller.IsFocused), well.ActiveEditor?.Title);
        await DevScene.SettleAsync(3);
        CtrlW(host);
        Press(host, Key.L, text: "l");                                // nothing further right: the keyboard stays
        await DevScene.SettleAsync(3);
        Assert.Same(b, well.ActiveEditor);
        CtrlW(host);
        Press(host, Key.W, text: "w");                                // Ctrl+W w: the next group, wrapping
        Assert.True(await RealAppHost.WaitFor(() => well.ActiveEditor == a && ViewOf(a).Modal.Controller.IsFocused), well.ActiveEditor?.Title);

        // none of the second keys was typed into an editor
        Assert.Equal("alpha\n", Text(a));
        Assert.Equal("beta\n", Text(b));

        await DevScene.SettleAsync(3);
        CtrlW(host);
        Press(host, Key.O, text: "o");                                // Ctrl+W o: only this group is left (Vim's :only)
        Assert.True(await RealAppHost.WaitFor(() => well.EditorTabs.Count == 1 && factory.DocumentGroups().Count == 1), $"{well.EditorTabs.Count} tabs, {factory.DocumentGroups().Count} groups");
        Assert.Same(a, well.ActiveEditor);
        Assert.Equal("alpha\n", Text(a));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task An_unbound_key_after_Ctrl_W_ends_the_chord_says_so_and_is_not_typed()
    {
        await using var host = await StartAsync("\"vim.enable\": false");
        var well = host.Shell.Well;
        var b = well.EditorTabs.Single(t => t.Title == "b.txt");
        await FocusAsync(host, b);

        CtrlW(host);
        Assert.True(host.Shell.Keyboard.IsChordPending);
        Assert.Contains(host.Get<IStatusBarService>().Items, i => i.Text.Contains("Waiting for second key of chord", StringComparison.Ordinal));
        Press(host, Key.X, text: "x");
        Assert.False(host.Shell.Keyboard.IsChordPending);
        Assert.Contains(host.Get<IStatusBarService>().Items, i => i.Text.Contains("is not a command", StringComparison.Ordinal));
        await DevScene.SettleAsync(3);
        Assert.Equal("beta\n", Text(b));
        Assert.Equal(2, well.EditorTabs.Count);

        // the next key is typing again
        Press(host, Key.X, text: "x");
        Assert.True(await RealAppHost.WaitFor(() => Text(b).Contains('x')), Text(b));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Ctrl_W_c_closes_the_editor_and_Ctrl_W_n_opens_an_untitled_one_below()
    {
        await using var host = await StartAsync("\"vim.enable\": false");
        var well = host.Shell.Well;
        var factory = host.Get<ShellDockFactory>();
        var b = well.EditorTabs.Single(t => t.Title == "b.txt");
        await FocusAsync(host, b);

        CtrlW(host);
        Press(host, Key.N, RawInputModifiers.Control);
        Assert.True(await RealAppHost.WaitFor(() => well.EditorTabs.Count == 3 && factory.DocumentGroups().Count == 2), $"{well.EditorTabs.Count} tabs, {factory.DocumentGroups().Count} groups");
        var untitled = Assert.IsType<EditorTab>(well.ActiveEditor);
        Assert.False(untitled.Uri.IsFile);
        Assert.True(await RealAppHost.WaitFor(() => untitled.LiveView is { IsEffectivelyVisible: true } && b.LiveView is { IsEffectivelyVisible: true }));
        await DevScene.SettleAsync(10);
        Assert.True(Centre(untitled).Y > Centre(b).Y + 10, $"the untitled editor at {Centre(untitled)} is not below b at {Centre(b)}");

        await FocusAsync(host, untitled);
        CtrlW(host);
        Press(host, Key.C, text: "c");                                // Ctrl+W c: closes the editor (clean: no prompt)
        Assert.True(await RealAppHost.WaitFor(() => well.EditorTabs.Count == 2 && !well.EditorTabs.Contains(untitled)), $"{well.EditorTabs.Count} tabs");
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task With_Vim_on_Ctrl_W_is_Vims_and_never_starts_the_shells_chord()
    {
        // Vim gives Ctrl-W to the application here (vim.handleKeys): the shell's chord must still not start, the key is Vim's to give away
        await using var app = await VimApp.StartAsync("text\n", settings: "\"editor.quickSuggestions\": false, \"vim.handleKeys\": { \"<C-w>\": false }");
        var keys = app.Host.Get<IContextKeyService>();
        Assert.True(keys.Get(VimIntegration.ActiveContextKey) is true);
        Assert.True(keys.Get("editorTextFocus") is true);
        app.Press(Key.W, RawInputModifiers.Control);
        Assert.False(app.Host.Shell.Keyboard.IsChordPending);
        app.Press(Key.V, RawInputModifiers.Control);
        await DevScene.SettleAsync(5);
        Assert.Single(app.Host.Get<ShellDockFactory>().DocumentGroups());

        // Vim off: the key is the shell's chord
        await app.Host.Get<ICommandService>().ExecuteAsync(VimService.DisableCommand);
        Assert.True(await RealAppHost.WaitFor(() => keys.Get(VimIntegration.ActiveContextKey) is false));
        app.View.Modal.Controller.Focus();
        await DevScene.SettleAsync(3);
        app.Press(Key.W, RawInputModifiers.Control);
        Assert.True(app.Host.Shell.Keyboard.IsChordPending);
        app.Host.Shell.Keyboard.Reset();
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task Text_input_with_no_key_press_after_a_chord_is_typed()
    {
        // A platform that sends no text input for the chord's second key: dictation or the Character Viewer later must still type. Before, the
        // second key left a flag that dropped the next text input whenever it came.
        await using var host = await StartAsync("\"vim.enable\": false");
        var well = host.Shell.Well;
        var factory = host.Get<ShellDockFactory>();
        var a = well.EditorTabs.Single(t => t.Title == "a.txt");
        var b = well.EditorTabs.Single(t => t.Title == "b.txt");
        await FocusAsync(host, b);

        CtrlW(host);
        Press(host, Key.L);                                           // Ctrl+W l, no text input follows (one group: the keyboard stays)
        host.Window.KeyTextInput("dictated");                         // straight after: several characters are not the key's own text
        Dispatcher.UIThread.RunJobs();
        Assert.True(await RealAppHost.WaitFor(() => Text(b).Contains("dictated", StringComparison.Ordinal)), Text(b));

        CtrlW(host);
        Press(host, Key.V);
        Assert.True(await RealAppHost.WaitFor(() => factory.DocumentGroups().Count == 2));
        Assert.True(await RealAppHost.WaitFor(() => a.LiveView is { IsEffectivelyVisible: true } && b.LiveView is { IsEffectivelyVisible: true }));
        await DevScene.SettleAsync(10);
        await FocusAsync(host, b);
        CtrlW(host);
        Press(host, Key.H);                                           // Ctrl+W h, no text input: a takes the keyboard
        Assert.True(await RealAppHost.WaitFor(() => well.ActiveEditor == a && ViewOf(a).Modal.Controller.IsFocused), well.ActiveEditor?.Title);
        await Task.Delay(400);                                        // a character picked later
        host.Window.KeyTextInput("é");
        Dispatcher.UIThread.RunJobs();
        Assert.True(await RealAppHost.WaitFor(() => Text(a).Contains('é')), Text(a));
    }

    /// <summary>a | b | the given tab in a third group to the right, each shown.</summary>
    private static async Task<(EditorTab A, EditorTab B, EditorTab C)> ThreeGroupsAsync(RealAppHost host)
    {
        var well = host.Shell.Well;
        var factory = host.Get<ShellDockFactory>();
        var a = well.EditorTabs.Single(t => t.Title == "a.txt");
        var b = well.EditorTabs.Single(t => t.Title == "b.txt");
        well.SplitRight(b);
        well.Activate(b);
        var c = well.OpenUntitledSplit(right: true);
        Assert.True(await RealAppHost.WaitFor(() => factory.DocumentGroups().Count == 3));
        Assert.True(await RealAppHost.WaitFor(() => new[] { a, b, c }.All(t => t.LiveView is { IsEffectivelyVisible: true })));
        await DevScene.SettleAsync(10);
        Assert.True(Centre(a).X < Centre(b).X && Centre(b).X < Centre(c).X, $"{Centre(a)} {Centre(b)} {Centre(c)}");
        return (a, b, c);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task From_a_tab_that_is_not_an_editor_the_group_commands_step_from_its_group()
    {
        // Welcome / Settings in front of the last group: Previous is the group before it, Next wraps to the first, Left is its neighbour. Before,
        // Previous from such a tab picked the middle group of three and the direction commands did nothing.
        await using var host = await StartAsync("\"vim.enable\": false");
        var well = host.Shell.Well;
        var commands = host.Get<ICommandService>();
        var (a, b, c) = await ThreeGroupsAsync(host);
        well.Activate(c);
        var page = well.OpenView("ctrl-w-test", "Page", () => new Avalonia.Controls.Border());
        Assert.True(await RealAppHost.WaitFor(() => page.LiveView is { IsEffectivelyVisible: true } && well.Active == page));
        await DevScene.SettleAsync(5);

        async Task FromPage(string command, EditorTab expected)
        {
            well.Activate(page);
            Assert.True(await RealAppHost.WaitFor(() => well.Active == page && page.LiveView is { IsEffectivelyVisible: true }));
            await DevScene.SettleAsync(3);
            await commands.ExecuteAsync(command);
            Assert.True(await RealAppHost.WaitFor(() => well.Active == expected), $"{command}: {well.Active?.Title}");
        }

        await FromPage(ShellCommands.FocusPreviousGroup, b);
        await FromPage(ShellCommands.FocusNextGroup, a);
        await FromPage(ShellCommands.FocusLeftGroup, b);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task From_a_floating_editor_the_next_and_previous_group_lead_back_to_the_main_window()
    {
        // Before, the cycle only held the floating window's own groups: from a floating window of one editor, w W t b (Vim's too) did nothing.
        await using var host = await StartAsync("\"vim.enable\": false");
        var well = host.Shell.Well;
        var factory = host.Get<ShellDockFactory>();
        var a = well.EditorTabs.Single(t => t.Title == "a.txt");
        var b = well.EditorTabs.Single(t => t.Title == "b.txt");
        well.Float(b);
        Assert.True(await RealAppHost.WaitFor(() => factory.IsFloating(b) && b.LiveView is { IsEffectivelyVisible: true } && a.LiveView is { IsEffectivelyVisible: true }));
        await DevScene.SettleAsync(10);
        Assert.NotSame(Avalonia.Controls.TopLevel.GetTopLevel(a.LiveView!), Avalonia.Controls.TopLevel.GetTopLevel(b.LiveView!));

        foreach (var step in new[] { GroupCycle.Next, GroupCycle.Previous, GroupCycle.First, GroupCycle.Last })
        {
            well.Activate(b);
            Assert.True(await RealAppHost.WaitFor(() => well.Active == b));
            Assert.True(well.FocusGroup(b, step));
            Assert.True(await RealAppHost.WaitFor(() => well.Active == a), $"{step}: {well.Active?.Title}");
        }
        // and from the main window, Next reaches the floating editor
        Assert.True(well.FocusGroup(a, GroupCycle.Next));
        Assert.True(await RealAppHost.WaitFor(() => well.Active == b), well.Active?.Title);
    }
}
