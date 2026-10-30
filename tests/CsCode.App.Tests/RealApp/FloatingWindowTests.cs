using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Docking;
using CsCode.App.Input;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Ui.Menus;
using Dock.Avalonia.Controls;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Floating tool windows and documents over the REAL composition (headless: a floating window is a headless Avalonia window owned by the main
/// one, never a window on a display): Float / Dock in the tab and tool-window menus, the shell's keys, focus keys, menus and clipboard in a
/// floating window, its own close button, the main window hiding (background mode), and layout persistence.
/// </summary>
public class FloatingWindowTests
{
    private static async Task<RealAppHost> StartAsync()
    {
        var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("a.txt", "alpha\n");
            h.Files.WriteFile("b.txt", "beta\n");
            // Plain typing (Vim, on by default, is not what these tests are about).
            var settings = h.Home.Combine("config", "settings.json");
            File.WriteAllText(settings, File.ReadAllText(settings).Replace("\"workbench.startupEditor\": \"none\"", "\"workbench.startupEditor\": \"none\",\n  \"vim.enable\": false", StringComparison.Ordinal));
        });
        await host.StartAsync([host.Files.Combine("a.txt"), host.Files.Combine("b.txt")]);
        await RealAppHost.WaitFor(() => host.Shell.Well.EditorTabs.Count == 2);
        await DevScene.SettleAsync(20);
        return host;
    }

    private static TopLevel? WindowOf(Control? view) => view is null ? null : TopLevel.GetTopLevel(view);

    private static async Task RunAsync(MenuEntry entry)
    {
        Assert.True(entry.IsEnabled, entry.AccessibleName + " is disabled");
        await entry.Run!();
        await DevScene.SettleAsync(10);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_tool_window_floats_into_a_window_of_its_own_and_docks_back()
    {
        await using var host = await StartAsync();
        var shell = host.Shell;
        var factory = shell.Factory;
        var output = shell.Tools.Get("output")!;
        shell.Tools.Show("output");
        await DevScene.SettleAsync(10);
        Assert.Same(host.Window, WindowOf(output.LiveView));

        var menu = shell.ContextMenus.ToolMenu(output).ToDictionary(e => e.AccessibleName);
        await RunAsync(menu["Float"]);
        Assert.True(shell.Tools.IsFloating("output"));
        Assert.True(shell.Tools.IsVisible("output"));
        Assert.True(await RealAppHost.WaitFor(() => WindowOf(output.LiveView) is HostWindow { IsVisible: true }), "the Output view is not in a floating window");
        var floating = (HostWindow)WindowOf(output.LiveView)!;
        Assert.Same(host.Window, floating.Owner); // owned by the main window, as Visual Studio's floating windows are
        Assert.Single(factory.FloatingWindows);
        Assert.False(factory.BottomTools.VisibleDockables!.Contains(output));

        menu = shell.ContextMenus.ToolMenu(output).ToDictionary(e => e.AccessibleName);
        Assert.False(menu["Float"].IsEnabled);
        Assert.False(menu["Auto Hide"].IsEnabled);
        await RunAsync(menu["Dock"]);
        Assert.False(shell.Tools.IsFloating("output"));
        Assert.Contains(output, factory.BottomTools.VisibleDockables!); // back in the well it came from
        Assert.True(await RealAppHost.WaitFor(() => !floating.IsVisible && factory.FloatingWindows.Count == 0), "the empty floating window stayed");
        Assert.True(await RealAppHost.WaitFor(() => WindowOf(output.LiveView) == host.Window));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_floating_document_keeps_the_shell_keys_focus_keys_and_clipboard_and_docks_back_as_a_tabbed_document()
    {
        await using var host = await StartAsync();
        var shell = host.Shell;
        var well = shell.Well;
        var b = well.EditorTabs.Single(t => t.Title == "b.txt");
        var tabMenu = shell.ContextMenus.TabMenu(b).Where(e => !e.IsSeparator).ToDictionary(e => e.AccessibleName);
        await RunAsync(tabMenu["Float"]);
        Assert.True(well.IsFloating(b));
        Assert.Contains(b, well.Tabs); // still one of the well's tabs
        Assert.Same(b, well.ActiveEditor);
        Assert.True(await RealAppHost.WaitFor(() => WindowOf(b.LiveView) is HostWindow { IsVisible: true } && b.Controller is not null), "b.txt is not in a floating window");
        var floating = (HostWindow)WindowOf(b.LiveView)!;

        // Focus keys: the floating window has the shell's focus handler.
        b.Controller!.Focus();
        Assert.Equal(FocusArea.Editor, shell.ContextKeys.Focus);
        Assert.Equal(true, host.Get<IContextKeyService>().Get("editorTextFocus"));

        // Typing reaches the editor, and Ctrl+S (the keyboard pipeline) saves it.
        floating.KeyTextInput("X");
        Assert.True(await RealAppHost.WaitFor(() => b.Doc.GetText().Contains('X')), b.Doc.GetText());
        Assert.True(b.IsDirty);
        floating.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
        floating.KeyRelease(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
        Assert.True(await RealAppHost.WaitFor(() => !b.IsDirty), "Ctrl+S in the floating window did not save");
        Assert.Contains("X", File.ReadAllText(host.Files.Combine("b.txt")));

        // Edit > Copy acts on the floating editor's selection (the router follows the window that has focus).
        b.Controller.SetSelection(new Core.Text.TextRange(Core.Text.TextPosition.Zero, b.Doc.PositionAt(b.Doc.Length)), reveal: false);
        await host.RunAsync("editor.action.clipboardCopyAction");
        Assert.Equal(b.Doc.GetText(), (await host.Get<IClipboardService>().ReadAsync())?.Text);

        // Its tab menu now offers Dock as Tabbed Document.
        tabMenu = shell.ContextMenus.TabMenu(b).Where(e => !e.IsSeparator).ToDictionary(e => e.AccessibleName);
        Assert.False(tabMenu.ContainsKey("Float"));
        await RunAsync(tabMenu["Dock as Tabbed Document"]);
        Assert.False(well.IsFloating(b));
        Assert.Same(shell.Factory.PrimaryDocuments, shell.Factory.GroupOf(b));
        Assert.True(await RealAppHost.WaitFor(() => !floating.IsVisible && shell.Factory.FloatingWindows.Count == 0));
        Assert.True(await RealAppHost.WaitFor(() => WindowOf(b.LiveView) == host.Window));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Closing_a_floating_tab_or_the_floating_windows_close_button_empties_and_closes_the_window()
    {
        await using var host = await StartAsync();
        var shell = host.Shell;
        var well = shell.Well;
        var factory = shell.Factory;
        var b = well.EditorTabs.Single(t => t.Title == "b.txt");

        // The palette's Move Editor into New Window, then closing the tab: the window goes with it.
        well.Activate(b);
        await host.RunAsync(Commands.ShellCommands.FloatEditor);
        Assert.True(well.IsFloating(b));
        await well.CloseAsync(b);
        await DevScene.SettleAsync(10);
        Assert.DoesNotContain(b, well.Tabs);
        Assert.Empty(factory.FloatingWindows);

        // A floating tool window's own close button (Alt+F4): the tool is hidden, not lost, and floats again where it was when shown.
        shell.Tools.Float("output");
        await DevScene.SettleAsync(10);
        var window = Assert.Single(factory.FloatingWindows);
        ShellDockFactory.SetBounds(window, new FloatingBounds(140, 120, 520, 260));
        var host1 = (Window)window.Host!;
        host1.Close();
        Assert.True(await RealAppHost.WaitFor(() => !shell.Tools.IsVisible("output") && factory.FloatingWindows.Count == 0), "the tool window was not hidden");
        Assert.Null(shell.Tools.Get("output")!.LiveView); // hidden tools keep no view
        shell.Tools.Show("output");
        await DevScene.SettleAsync(10);
        Assert.True(shell.Tools.IsFloating("output"));
        var again = ShellDockFactory.BoundsOf(Assert.Single(factory.FloatingWindows));
        Assert.Equal((140, 120, 520, 260), (again.X, again.Y, again.Width, again.Height));

        // Dirty documents in a floating window: its close button asks, and Cancel keeps the window.
        var a = well.EditorTabs.Single(t => t.Title == "a.txt");
        well.Float(a);
        a.Doc.ApplyEdits([new Core.Text.TextEdit(Core.Text.TextRange.At(0, 0), "dirty ")]);
        var dialogs = host.Get<Notifications.DialogHostViewModel>();
        var docWindow = (Window)factory.WindowOf(a)!.Host!;
        docWindow.Close();
        Assert.True(await RealAppHost.WaitFor(() => dialogs.IsVisible), "no Save prompt");
        dialogs.Choose("Cancel");
        await DevScene.SettleAsync(10);
        Assert.True(well.IsFloating(a) && docWindow.IsVisible);
        docWindow.Close();
        Assert.True(await RealAppHost.WaitFor(() => dialogs.IsVisible));
        dialogs.Choose("Don't Save");
        Assert.True(await RealAppHost.WaitFor(() => !well.Tabs.Contains(a) && !docWindow.IsVisible));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Floating_windows_hide_and_show_with_the_main_window()
    {
        await using var host = await StartAsync();
        var shell = host.Shell;
        shell.Tools.Float("output");
        await DevScene.SettleAsync(10);
        var floating = (Window)Assert.Single(shell.Factory.FloatingWindows).Host!;
        Assert.True(floating.IsVisible);
        host.Window.Hide(); // background mode: closing the window hides it
        await DevScene.SettleAsync(5);
        Assert.False(floating.IsVisible);
        host.Window.Show();
        await DevScene.SettleAsync(5);
        Assert.True(floating.IsVisible);
        Assert.True(shell.Tools.IsFloating("output"));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Floating_windows_are_part_of_the_saved_layout()
    {
        await using var host = await StartAsync();
        var shell = host.Shell;
        var factory = shell.Factory;
        var b = shell.Well.EditorTabs.Single(t => t.Title == "b.txt");
        shell.Tools.Float("output");
        shell.Well.Float(b);
        await DevScene.SettleAsync(10);
        ShellDockFactory.SetBounds(factory.WindowOf(shell.Tools.Get("output")!)!, new FloatingBounds(30, 40, 500, 220));
        ShellDockFactory.SetBounds(factory.WindowOf(b)!, new FloatingBounds(300, 200, 640, 480));
        // A tool hidden while floating keeps its place too.
        shell.Tools.Float("errorlist");
        ShellDockFactory.SetBounds(factory.WindowOf(shell.Tools.Get("errorlist")!)!, new FloatingBounds(60, 70, 300, 200));
        shell.Tools.Hide("errorlist");
        await DevScene.SettleAsync(10);

        var json = LayoutSerializer.Serialize(shell.Layout.Capture());
        var snapshot = LayoutSerializer.Deserialize(json)!;
        Assert.Equal(2, snapshot.Floating!.Count);
        Assert.Contains(snapshot.Floating, f => f.Tools.SequenceEqual(["output"]) && f.X == 30 && f.Width == 500);
        Assert.Contains(snapshot.Floating, f => f.Documents.SequenceEqual([b.Uri.ToString()]) && f.Y == 200 && f.Height == 480);
        Assert.Equal("errorlist", Assert.Single(snapshot.FloatingHidden!).Id);
        Assert.Contains(snapshot.Tools, t => t.Id == "output" && t.Visible && t.Dock == "bottom"); // its well, for when it docks again
        // An older layout (no floating part) still reads.
        Assert.NotNull(LayoutSerializer.Deserialize("""{"version":1,"tools":[],"leftProportion":0.2,"rightProportion":0.2,"bottomProportion":0.2,"documents":[],"activeDocument":null}"""));

        // Reset docks everything; applying the snapshot floats it all again, at the same places.
        await shell.ResetLayoutAsync();
        await DevScene.SettleAsync(10);
        Assert.Empty(factory.FloatingWindows);
        Assert.False(shell.Tools.IsFloating("output"));
        await shell.Layout.ApplyAsync(snapshot);
        await DevScene.SettleAsync(10);
        Assert.True(shell.Tools.IsFloating("output"));
        Assert.True(shell.Well.IsFloating(b));
        var o = ShellDockFactory.BoundsOf(factory.WindowOf(shell.Tools.Get("output")!)!);
        Assert.Equal((30, 40, 500, 220), (o.X, o.Y, o.Width, o.Height));
        var d = ShellDockFactory.BoundsOf(factory.WindowOf(b)!);
        Assert.Equal((300, 200, 640, 480), (d.X, d.Y, d.Width, d.Height));
        shell.Tools.Show("errorlist");
        await DevScene.SettleAsync(10);
        Assert.True(shell.Tools.IsFloating("errorlist"));

        // Applied while the main window cannot show a window (a background session restoring hidden): nothing floats until it can, and a save
        // meanwhile keeps the floating part.
        await shell.ResetLayoutAsync();
        shell.Layout.CanPresentWindows = () => false;
        await shell.Layout.ApplyAsync(snapshot);
        await DevScene.SettleAsync(10);
        Assert.Empty(factory.FloatingWindows);
        Assert.Equal(2, shell.Layout.Capture().Floating!.Count);
        shell.Layout.CanPresentWindows = () => true;
        shell.Layout.ApplyPendingFloating();
        await DevScene.SettleAsync(10);
        Assert.Equal(2, factory.FloatingWindows.Count);
    }
}
