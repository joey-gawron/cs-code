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
using CsCode.TestDoubles;
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
    public async Task Closing_the_main_window_with_floating_windows_saves_them_in_the_layout_and_closes_without_emptying_them()
    {
        // The main window's own X (title bar) and Alt+F4 are Window.Close(). Avalonia asked the owned floating windows first by default: one emptied
        // itself (its documents closed, its tools hidden) and cancelled the main window's close, which then neither closed nor saved the layout.
        await using var host = await StartAsync();
        var shell = host.Shell;
        var factory = shell.Factory;
        var b = shell.Well.EditorTabs.Single(t => t.Title == "b.txt");
        shell.Tools.Float("output");
        shell.Well.Float(b);
        await DevScene.SettleAsync(10);
        Assert.Equal(2, factory.FloatingWindows.Count);
        var closed = false;
        host.Window.Closed += (_, _) => closed = true;

        host.Window.Close();
        Assert.True(await RealAppHost.WaitFor(() => closed), "the main window did not close");
        Assert.Empty(factory.FloatingWindows);
        // saved before anything closed: the next start floats them again
        var saved = LayoutSerializer.Deserialize(host.Get<IStateStorage>().Get<string>(LayoutPersistence.StateKey, StorageScope.Application))!;
        Assert.NotNull(saved.Floating);
        Assert.Contains(saved.Floating, f => f.Tools.SequenceEqual(["output"]));
        Assert.Contains(saved.Floating, f => f.Documents.SequenceEqual([b.Uri.ToString()]));
        Assert.Contains(saved.Documents, d => d.Uri == b.Uri.ToString());
        Assert.Contains(saved.Tools, t => t.Id == "output" && t.Visible);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task In_background_mode_closing_the_main_window_hides_it_with_its_floating_windows_as_they_are()
    {
        await using var host = await StartAsync();
        var shell = host.Shell;
        var factory = shell.Factory;
        var b = shell.Well.EditorTabs.Single(t => t.Title == "b.txt");
        b.Doc.ApplyEdits([new Core.Text.TextEdit(Core.Text.TextRange.At(0, 0), "dirty ")]); // hot exit keeps it: no Save prompt
        shell.Tools.Float("output");
        shell.Well.Float(b);
        await DevScene.SettleAsync(10);
        var windows = factory.FloatingWindows.Select(w => (Window)w.Host!).ToArray();
        Assert.Equal(2, windows.Length);
        // What the session controller does: the close hides the window (docs/SESSIONS.md).
        host.Window.CloseInterceptor = _ => { host.Window.Hide(); return true; };

        host.Window.Close();
        await DevScene.SettleAsync(10);
        Assert.False(host.Window.IsVisible);
        Assert.All(windows, w => Assert.False(w.IsVisible));
        Assert.False(host.Get<Notifications.DialogHostViewModel>().IsVisible); // no Save prompt
        Assert.True(shell.Tools.IsFloating("output") && shell.Tools.IsVisible("output"));
        Assert.True(shell.Well.IsFloating(b) && b.IsDirty);

        host.Window.Show();
        await DevScene.SettleAsync(10);
        Assert.All(windows, w => Assert.True(w.IsVisible));
        host.Window.CloseInterceptor = null;
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Typing_in_a_floating_window_is_user_activity_for_the_idle_trim()
    {
        await using var host = await StartAsync();
        var shell = host.Shell;
        var b = shell.Well.EditorTabs.Single(t => t.Title == "b.txt");
        shell.Well.Float(b);
        Assert.True(await RealAppHost.WaitFor(() => WindowOf(b.LiveView) is HostWindow { IsVisible: true } && b.Controller is not null));
        var floating = (HostWindow)WindowOf(b.LiveView)!;
        b.Controller!.Focus();
        var activity = host.Get<CsCode.Profiling.IUserActivityMonitor>();
        activity.NotifyActivity();
        await Task.Delay(400);
        Assert.True(activity.IdleTime >= TimeSpan.FromMilliseconds(350), activity.IdleTime.ToString());

        floating.KeyTextInput("q");
        Assert.Contains('q', b.Doc.GetText());
        Assert.True(activity.IdleTime < TimeSpan.FromMilliseconds(300), $"idle {activity.IdleTime.TotalMilliseconds} ms after typing in the floating window");
        await Task.Delay(400);
        floating.MouseMove(new Avalonia.Point(20, 20));
        Assert.True(activity.IdleTime < TimeSpan.FromMilliseconds(300), $"idle {activity.IdleTime.TotalMilliseconds} ms after moving the mouse in it");
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_Ctrl_Tab_switcher_closes_without_switching_when_its_window_loses_activation()
    {
        await using var host = await StartAsync();
        var well = host.Shell.Well;
        var a = well.EditorTabs.Single(t => t.Title == "a.txt");
        var b = well.EditorTabs.Single(t => t.Title == "b.txt");
        well.Activate(a);
        well.Activate(b);
        await DevScene.SettleAsync(5);
        var switcher = host.Window.GetVisualDescendants().OfType<Border>().Single(x => x.MinWidth == 300 && x.Child is StackPanel);

        host.Window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null); // Ctrl held: the switcher stays open
        await DevScene.SettleAsync(2);
        Assert.True(switcher.IsVisible);
        Deactivate(host.Window); // Alt+Tab to another application with Ctrl still down: the Ctrl release goes there
        await DevScene.SettleAsync(2);
        Assert.False(switcher.IsVisible);
        Assert.Same(b, well.ActiveEditor);
        // Back in CsCode, a plain Ctrl press and release (Ctrl+S...) does not switch to the tab the stale switcher had selected.
        host.Window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
        host.Window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
        await DevScene.SettleAsync(2);
        Assert.Same(b, well.ActiveEditor);

        // The same in a floating window.
        well.Float(b);
        Assert.True(await RealAppHost.WaitFor(() => WindowOf(b.LiveView) is HostWindow { IsVisible: true } && b.Controller is not null));
        var floating = (HostWindow)WindowOf(b.LiveView)!;
        b.Controller!.Focus();
        floating.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
        await DevScene.SettleAsync(2);
        Assert.True(switcher.IsVisible);
        Deactivate(floating);
        await DevScene.SettleAsync(2);
        Assert.False(switcher.IsVisible);
        Assert.Same(b, well.ActiveEditor);
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_palette_prompts_and_switcher_show_in_the_floating_window_that_has_the_keyboard()
    {
        // An owned floating window stays above the main one: overlays drawn in the main window could be hidden under it and answered blind.
        await using var host = await StartAsync();
        var shell = host.Shell;
        var well = shell.Well;
        var b = well.EditorTabs.Single(t => t.Title == "b.txt");
        well.Float(b);
        Assert.True(await RealAppHost.WaitFor(() => WindowOf(b.LiveView) is HostWindow { IsVisible: true } && b.Controller is not null));
        var floating = (HostWindow)WindowOf(b.LiveView)!;
        b.Controller!.Focus();
        var quickView = host.Window.GetVisualDescendants().OfType<Views.QuickInputView>().Single();
        var dialogView = host.Window.GetVisualDescendants().OfType<Views.DialogHostView>().Single();
        var quick = host.Get<QuickInput.QuickInputViewModel>();

        // Ctrl+Shift+P in the floating editor: the palette is in the floating window, takes the keyboard there, and Esc gives it back.
        floating.KeyPress(Key.P, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.P, null);
        Assert.True(await RealAppHost.WaitFor(() => quick.IsVisible), "no palette");
        Assert.Same(floating, WindowOf(quickView));
        Assert.True(await RealAppHost.WaitFor(() => quickView.GetVisualDescendants().OfType<TextBox>().Any(t => t.IsFocused)), "the palette's box has no focus");
        floating.KeyTextInput("zzz");
        Assert.Equal(">zzz", quick.Text);
        floating.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.True(await RealAppHost.WaitFor(() => !quick.IsVisible && b.Controller.IsFocused), "Esc did not close the palette and give the editor the keyboard");

        // Ctrl+Tab there: the switcher too.
        floating.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
        await DevScene.SettleAsync(2);
        var switcher = floating.GetVisualDescendants().OfType<Border>().SingleOrDefault(x => x.MinWidth == 300 && x.Child is StackPanel);
        Assert.NotNull(switcher);
        Assert.True(switcher.IsVisible);
        floating.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
        await DevScene.SettleAsync(2);
        Assert.False(switcher.IsVisible);
        well.Activate(b);
        await DevScene.SettleAsync(5);
        b.Controller!.Focus();

        // The floating window's close button with a dirty document: the Save prompt is in that window, Enter answers it (Save).
        b.Doc.ApplyEdits([new Core.Text.TextEdit(Core.Text.TextRange.At(0, 0), "Z")]);
        floating.Close();
        var dialogs = host.Get<Notifications.DialogHostViewModel>();
        Assert.True(await RealAppHost.WaitFor(() => dialogs.IsVisible), "no Save prompt");
        Assert.Same(floating, WindowOf(dialogView));
        Assert.True(await RealAppHost.WaitFor(() => dialogView.GetVisualDescendants().OfType<Button>().Any(x => x.IsFocused)), "the prompt's button has no focus");
        floating.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.True(await RealAppHost.WaitFor(() => !well.Tabs.Contains(b) && shell.Factory.FloatingWindows.Count == 0), "the floating window did not close");
        Assert.Equal("Zbeta\n", File.ReadAllText(host.Files.Combine("b.txt")));

        // Its window is gone: the overlays are back in the main window, and the palette opens there.
        Assert.Same(host.Window, WindowOf(quickView));
        Assert.Same(host.Window, WindowOf(dialogView));
        var a = well.EditorTabs.Single(t => t.Title == "a.txt");
        Assert.True(await RealAppHost.WaitFor(() => a.Controller is not null));
        a.Controller!.Focus();
        var running = await host.OpenPaletteAsync(KnownCommands.CommandPalette);
        Assert.Same(host.Window, WindowOf(quickView));
        await host.ClosePaletteAsync(running);
    }

    /// <summary>What the platform reports when another application takes the keyboard (the headless platform has no other application).</summary>
    private static void Deactivate(Window window)
    {
        var impl = (object)window.PlatformImpl!;
        var deactivated = impl.GetType().GetProperty("Deactivated", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("the headless window has no Deactivated callback");
        ((Action)deactivated.GetValue(impl)!).Invoke();
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

    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_closed_floating_window_and_the_tab_it_held_are_released()
    {
        await using var host = await StartAsync();
        var shell = host.Shell;
        await LeakAssert.NotRetainedAsync(async () =>
        {
            var b = shell.Well.EditorTabs.Single(t => t.Title == "b.txt");
            shell.Well.Float(b);
            await DevScene.SettleAsync(10);
            var window = (Window)shell.Factory.WindowOf(b)!.Host!;
            var view = b.LiveView!;
            var doc = b.Doc;
            b.Controller!.Focus(); // the keyboard in the floating editor when its tab closes (it moves to the window, which then closes)
            await shell.Well.CloseAsync(b);
            await DevScene.SettleAsync(10);
            Assert.Empty(shell.Factory.FloatingWindows);
            Assert.False(window.IsVisible);
            Assert.Same(host.Window, WindowOf(host.Window.FocusManager?.GetFocusedElement() as Control)); // back in the main window
            return [b, window, view, doc];
        });
        // A tool window floated and docked back leaves no window behind either.
        await LeakAssert.NotRetainedAsync(async () =>
        {
            shell.Tools.Float("output");
            await DevScene.SettleAsync(10);
            var window = (Window)shell.Factory.WindowOf(shell.Tools.Get("output")!)!.Host!;
            shell.Tools.Dock("output");
            await DevScene.SettleAsync(10);
            return [window];
        });
    }
}
