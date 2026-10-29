using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using CsCode.App.Docking;
using CsCode.App.Input;
using CsCode.App.Menus;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Panels.Search;
using CsCode.Panels.Search.Views;
using CsCode.Ui;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// Which views the REAL composition shows, where and when (<see cref="RealAppHost"/>): the panels' commands through <see cref="IViewActivator"/>,
/// document-location windows as document tabs, the default layout, and the debug windows' auto-show.
/// </summary>
public class RealAppViewTests
{
    private static readonly string[] DefaultLayout = ["errorlist", "explorer", "output"];

    private static readonly string[] DebugModeWindows =
        ["debug.callstack", "debug.locals", "debug.autos", "debug.watch", "debug.breakpoints", "debug.threads", "debug.console"];

    private static string[] VisibleTools(ToolWindowManager tools) =>
        tools.Tools.Select(t => t.Spec.Id).Where(tools.IsVisible).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Find_in_Files_shows_the_search_window_and_shows_it_again_after_it_was_hidden()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var tools = host.Shell.Tools;
        Assert.False(tools.IsVisible(SearchPanelsHost.FindViewId)); // not part of the default layout

        await host.RunAsync(SearchPanelsHost.FindInFiles, "needle");
        Assert.True(tools.IsVisible(SearchPanelsHost.FindViewId));
        Assert.IsType<FindInFilesView>(tools.Get(SearchPanelsHost.FindViewId)!.LiveView);
        Assert.Equal("needle", host.Get<SearchPanelsHost>().LiveFind?.Query);

        // Hiding the window drops its view; the command must bring it back (it used to only park the request). Its X: the next test.
        tools.Hide(SearchPanelsHost.FindViewId);
        Assert.Null(tools.Get(SearchPanelsHost.FindViewId)!.LiveView);
        await host.RunAsync(SearchPanelsHost.ReplaceInFiles, "again");
        Assert.True(tools.IsVisible(SearchPanelsHost.FindViewId));
        Assert.Equal("again", host.Get<SearchPanelsHost>().LiveFind?.Query);

        // The other routes: Ctrl+Shift+F through the real keymap, the View > Tool Windows entry, Find in Folder.
        tools.Hide(SearchPanelsHost.FindViewId);
        Assert.Equal(KeyDispatch.Handled, host.Shell.Keyboard.Handle(KeyStrokeParser.ParseStroke("ctrl+shift+f")));
        Assert.True(await RealAppHost.WaitFor(() => tools.IsVisible(SearchPanelsHost.FindViewId)));
        tools.Hide(SearchPanelsHost.FindViewId);
        host.Shell.Menus.Refresh();
        var toolWindows = host.Shell.Menus.Roots.Single(r => r.Header == "_View").Children.Single(c => c.Header == "_Tool Windows");
        var entry = toolWindows.Children.Single(c => c.Header == "Find in Files");
        Assert.True(entry.IsEnabled);
        Assert.DoesNotContain(toolWindows.Children, c => c.Header is "Settings" or "Keyboard Shortcuts"); // documents, opened from Tools
        await ((IAsyncRelayCommand)entry.Command!).ExecuteAsync(null);
        Assert.True(await RealAppHost.WaitFor(() => tools.IsVisible(SearchPanelsHost.FindViewId)));
        tools.Hide(SearchPanelsHost.FindViewId);
        await host.RunAsync(SearchPanelsHost.FindInFolder);
        Assert.True(tools.IsVisible(SearchPanelsHost.FindViewId));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task A_tool_window_closed_with_its_X_is_hidden_and_its_command_key_menu_entry_and_Reset_show_it_again()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var tools = host.Shell.Tools;
        var find = tools.Get(SearchPanelsHost.FindViewId)!;

        // The X in the window's title bar, clicked. It used to remove the window from the layout with its view still alive, and nothing
        // (command, key, menu entry) could show it again for the rest of the session.
        await host.RunAsync(SearchPanelsHost.FindInFiles, "needle");
        await AssertOnScreenAsync(host, find);
        await ClickCloseAsync(host, find);
        Assert.False(tools.IsVisible(SearchPanelsHost.FindViewId));
        Assert.Null(find.LiveView); // a closed tool window keeps no view
        await host.RunAsync(SearchPanelsHost.FindInFiles, "again");
        await AssertOnScreenAsync(host, find);
        Assert.Equal("again", host.Get<SearchPanelsHost>().LiveFind?.Query);

        await ClickCloseAsync(host, find);
        Assert.Equal(KeyDispatch.Handled, host.Shell.Keyboard.Handle(KeyStrokeParser.ParseStroke("ctrl+shift+f")));
        await AssertOnScreenAsync(host, find);

        await ClickCloseAsync(host, find);
        host.Shell.Menus.Refresh();
        var entry = host.Shell.Menus.Roots.Single(r => r.Header == "_View").Children.Single(c => c.Header == "_Tool Windows").Children.Single(c => c.Header == "Find in Files");
        Assert.False(entry.IsChecked);
        await ((IAsyncRelayCommand)entry.Command!).ExecuteAsync(null);
        await AssertOnScreenAsync(host, find);

        await ClickCloseAsync(host, find);
        await host.RunAsync(SearchPanelsHost.FindInFolder);
        await AssertOnScreenAsync(host, find);

        // A default-layout window: the saved layout records it as closed, its toggle shows it again, and so does Reset Window Layout.
        var explorer = tools.Get("explorer")!;
        await ClickCloseAsync(host, explorer);
        Assert.Null(explorer.LiveView);
        Assert.False(host.Shell.Layout.Capture().Tools.Single(t => t.Id == "explorer").Visible);
        await host.RunAsync("workbench.view.explorer");
        await AssertOnScreenAsync(host, explorer);
        await ClickCloseAsync(host, explorer);
        await host.RunAsync(ShellMenus.ResetLayout);
        Assert.Equal(DefaultLayout, VisibleTools(tools));
        await AssertOnScreenAsync(host, explorer);
    }

    /// <summary>Clicks the close button of <paramref name="tool"/>'s window chrome (Dock binds it to <c>Factory.CloseDockable</c>).</summary>
    private static async Task ClickCloseAsync(RealAppHost host, ToolTab tool)
    {
        Button? x = null;
        Assert.True(await RealAppHost.WaitFor(() =>
            (x = host.Window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "PART_CloseButton" && ReferenceEquals(b.CommandParameter, tool)))
                is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Bounds.Width: > 0 }), $"no close button for {tool.Spec.Id}");
        var button = x!;
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), host.Window)!.Value;
        host.Window.MouseMove(center);
        host.Window.MouseDown(center, MouseButton.Left);
        host.Window.MouseUp(center, MouseButton.Left);
        await DevScene.SettleAsync(10);
    }

    /// <summary>Visible in the model and presented: the tool's current view is in the window's visual tree.</summary>
    private static async Task AssertOnScreenAsync(RealAppHost host, ToolTab tool)
    {
        Assert.True(host.Shell.Tools.IsVisible(tool.Spec.Id));
        Assert.True(await RealAppHost.WaitFor(() => tool.LiveView is { IsEffectivelyVisible: true } v && ReferenceEquals(TopLevel.GetTopLevel(v), host.Window)),
            $"{tool.Spec.Id} is not on screen");
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Keyboard_Shortcuts_opens_as_a_document_tab_not_as_a_tool_window()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var factory = host.Shell.Factory;
        var docked = factory.LeftTools.VisibleDockables!.Concat(factory.RightTools.VisibleDockables!).Concat(factory.BottomTools.VisibleDockables!).OfType<ToolTab>();
        Assert.DoesNotContain(docked, t => t.Spec.DefaultLocation == ToolWindowLocation.Document); // Settings and Keyboard Shortcuts used to sit next to Explorer
        Assert.Empty(host.Shell.Well.Tabs); // opened on demand, not at startup

        await host.RunAsync(ShellMenus.OpenKeyboardShortcuts);
        var tab = Assert.IsAssignableFrom<ShellDocument>(host.Shell.Well.Active);
        Assert.Equal("view:keybindings", tab.Id);
        Assert.IsType<KeyboardShortcutsView>(tab.LiveView);

        // Closing the tab disposes the view; the activator opens a fresh one, from any thread.
        await host.Shell.Well.CloseAsync(tab);
        var activator = host.Get<IViewActivator>();
        Assert.True(await Task.Run(() => activator.ShowViewAsync(SearchPanelsHost.KeybindingsViewId).AsTask()));
        Assert.Equal("view:keybindings", host.Shell.Well.Active?.Id);
        Assert.False(await activator.ShowViewAsync("no.such.view"));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_default_layout_shows_only_Explorer_Output_and_Error_List_and_Reset_Window_Layout_restores_it()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var tools = host.Shell.Tools;
        Assert.Equal(DefaultLayout, VisibleTools(tools)); // no Modules, Find in Files, Find Results or debug windows

        tools.Show("debug.modules");
        tools.Show(SearchPanelsHost.FindViewId);
        await host.RunAsync(ShellMenus.ResetLayout);
        Assert.Equal(DefaultLayout, VisibleTools(tools));
    }

    [AvaloniaFact(Timeout = 60_000)]
    public async Task Debug_windows_auto_show_in_debug_mode_and_hide_afterwards_and_Add_Watch_shows_the_Watch_window()
    {
        await using var host = await RealAppHost.Create().StartAsync();
        var tools = host.Shell.Tools;
        var keys = host.Get<IContextKeyService>();

        keys.Set("inDebugMode", true); // what DebugService does when a session starts
        Assert.True(await RealAppHost.WaitFor(() => DebugModeWindows.All(tools.IsVisible)));
        Assert.False(tools.IsVisible("debug.modules"));
        Assert.False(tools.IsVisible("debug.exception"));
        keys.Set("cscode.debug.exceptionStopped", true);
        Assert.True(await RealAppHost.WaitFor(() => tools.IsVisible("debug.exception")));

        keys.Set("cscode.debug.exceptionStopped", false);
        keys.Set("inDebugMode", false);
        Assert.True(await RealAppHost.WaitFor(() => !DebugModeWindows.Any(tools.IsVisible) && !tools.IsVisible("debug.exception")));
        Assert.Equal(DefaultLayout, VisibleTools(tools));

        await host.RunAsync("debug.addToWatch", "answer * 2");
        Assert.True(await RealAppHost.WaitFor(() => tools.IsVisible("debug.watch")));
    }
}
