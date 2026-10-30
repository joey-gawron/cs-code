using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using CsCode.App.Menus;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Views;
using CsCode.Core.Platform;
using CsCode.TestDoubles;
using CsCode.Ui.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CsCode.App.Tests;

/// <summary>
/// The macOS menu bar exported from the shell's menu model (<see cref="NativeMenuExport"/>). Built and driven here on any OS: the headless
/// platform has no native menu bar, so the exported menus are read as Avalonia builds them, and the native side's open and click are raised as
/// macOS raises them.
/// </summary>
public class NativeMenuTests
{
    [AvaloniaFact]
    public void The_menu_bar_mirrors_the_model_without_access_keys_or_the_items_that_belong_to_the_application_menu()
    {
        using var rig = new Rig();
        var window = new Window();
        using var export = NativeMenuExport.BuildWindowMenu(rig.Menu, window, rig.Bindings, rig.Ui);

        Assert.Equal(["File", "Edit", "View", "Go", "Debug", "Terminal", "Tools", "Window", "Help"], export.Menu.Items.OfType<NativeMenuItem>().Select(i => i.Header));
        var file = Items(export, "File");
        Assert.Contains("Open File…", file.Select(i => i.Header)); // a Mac ellipsis
        Assert.Contains("Save All", file.Select(i => i.Header));
        Assert.DoesNotContain("Exit", file.Select(i => i.Header)); // Quit CsCode is in the application menu
        Assert.DoesNotContain("About CsCode", Items(export, "Help").Select(i => i.Header));
        foreach (var menu in export.Menu.Items.OfType<NativeMenuItem>().Select(i => i.Menu!))
            AssertSeparatorsOnlyBetweenItems(menu);

        var window_ = Items(export, "Window");
        Assert.Equal(["Minimize", "Zoom"], window_.Take(2).Select(i => i.Header));
        window_[0].RaiseClick();
        Assert.Equal(WindowState.Minimized, window.WindowState);
    }

    [AvaloniaFact]
    public void Exported_items_carry_no_key_equivalent_so_every_bound_shortcut_stays_the_keyboard_pipeline_s()
    {
        using var rig = new Rig();
        rig.Bindings.Add(KnownCommands.Save, "cmd+s");
        rig.Bindings.Add(KnownCommands.CommandPalette, "cmd+shift+p");
        rig.Menu.Refresh();
        using var export = NativeMenuExport.BuildWindowMenu(rig.Menu, new Window(), rig.Bindings, rig.Ui);

        var all = export.Menu.Items.OfType<NativeMenuItem>().SelectMany(i => Flatten(i.Menu!)).ToArray();
        // macOS would fire a key equivalent before the window sees the key: a bound shortcut would run twice, or bypass chords and when-clauses.
        Assert.All(all.Where(i => i.Header != "Minimize"), i => Assert.Null(i.Gesture));
        var save = all.Single(i => i.Header == "Save");
        Assert.Equal(rig.Bindings.GetLabel(KnownCommands.Save), save.ToolTip); // the shortcut is still named
    }

    [AvaloniaFact]
    public void An_item_runs_its_command_and_a_menu_opening_brings_enablement_and_dynamic_items_up_to_date()
    {
        using var rig = new Rig();
        using var export = NativeMenuExport.BuildWindowMenu(rig.Menu, new Window(), rig.Bindings, rig.Ui);
        var ran = new List<string>();
        rig.Commands.Register(KnownCommands.Save, _ => { ran.Add("save"); return ValueTask.FromResult<object?>(null); });

        var save = Items(export, "File").Single(i => i.Header == "Save");
        Assert.False(save.IsEnabled); // when built: no Save command, no editor open (hasOpenEditors)
        rig.Keys.Set("hasOpenEditors", true);
        var file = export.Menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "File");
        file.Menu!.RaiseNeedsUpdate(); // macOS asks as the menu opens
        save = Items(export, "File").Single(i => i.Header == "Save");
        Assert.True(save.IsEnabled);
        save.RaiseClick();
        Assert.Equal(["save"], ran);

        rig.Workspace.Recents.Add(new Uri("file:///work/recent/"));
        file.Menu!.RaiseNeedsUpdate();
        var recent = Items(export, "File").Single(i => i.Header == "Open Recent");
        Assert.Contains(recent.Menu!.Items.OfType<NativeMenuItem>(), i => i.Header?.Contains("/work/recent") == true);

        rig.Workspace.Recents.Add(new Uri("file:///work/my_project/"));
        rig.Workspace.Recents.Add(new Uri("file:///work/a__b/"));
        file.Menu!.RaiseNeedsUpdate();
        recent = Items(export, "File").Single(i => i.Header == "Open Recent");
        var headers = recent.Menu!.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToArray();
        Assert.Contains(headers, h => h?.EndsWith("/work/my_project/") == true); // a path's underscores are not access keys
        Assert.Contains(headers, h => h?.EndsWith("/work/a__b/") == true);
    }

    [AvaloniaFact]
    public void The_application_menu_offers_About_and_Settings_whose_command_key_is_the_menu_s_only_while_no_keybinding_takes_it()
    {
        using var rig = new Rig();
        using var app = NativeMenuExport.BuildApplicationMenu();
        var items = app.Menu.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToArray();
        Assert.Equal(["About CsCode", "Settings…"], items.Select(i => i.Header));
        var settings = items[1];
        Assert.Equal(new KeyGesture(Key.OemComma, KeyModifiers.Meta), settings.Gesture);

        var ran = new List<string>();
        rig.Commands.Register(KnownCommands.OpenSettings, _ => { ran.Add("settings"); return ValueTask.FromResult<object?>(null); });
        settings.RaiseClick();
        Assert.Empty(ran); // no run connected yet: the item does nothing

        rig.Bindings.Add(KnownCommands.OpenSettings, "cmd+,"); // the VS Code keymap's
        using (app.Connect(rig.Services))
        {
            Assert.Null(settings.Gesture); // the keyboard pipeline handles ⌘,: never both
            settings.RaiseClick();
            Assert.Equal(["settings"], ran);
        }
        Assert.Equal(new KeyGesture(Key.OemComma, KeyModifiers.Meta), settings.Gesture);
    }

    [Theory]
    [InlineData("_File", "File")]
    [InlineData("Save A_ll", "Save All")]
    [InlineData("Open _Folder...", "Open Folder…")]
    [InlineData("Snake__case", "Snake_case")]
    public void Headers_lose_their_access_key_marks_and_take_a_Mac_ellipsis(string header, string expected) =>
        Assert.Equal(expected, NativeMenuExport.Header(header));

    /// <summary>
    /// macOS (<c>inWindowMenu = false</c>): the menus are in the system menu bar, so the title bar has no in-window menu (not even a hidden one, which
    /// would still take F10 and Alt) and no logo, and the Quick Launch box stays clear of the traffic lights.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public void Without_the_in_window_menu_the_title_bar_has_no_menu_bar_or_logo_and_keeps_clear_of_the_traffic_lights()
    {
        using var rig = new Rig();
        var window = new Window { Width = 1000, Height = 200 };
        var bar = new TitleBarView(window, rig.Menu, rig.Commands, showCaptionButtons: false, inWindowMenu: false) { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        window.Content = bar;
        window.Show();
        try
        {
            for (var i = 0; i < 3; i++) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Assert.Empty(bar.GetVisualDescendants().OfType<ShellMenuBar>());
            Assert.Empty(bar.GetVisualDescendants().OfType<Menu>());
            Assert.DoesNotContain(bar.GetVisualDescendants().OfType<AppLogo>(), l => l.IsEffectivelyVisible);
            var quick = bar.QuickLaunchBox.TranslatePoint(default, bar)!.Value;
            Assert.True(quick.X >= TitleBarView.NativeCaptionButtonsInset - 0.5, $"the Quick Launch box ({quick}) is under the traffic lights");
        }
        finally { window.Close(); }
    }

    private static NativeMenuItem[] Items(NativeMenuExport export, string root) =>
        export.Menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == root).Menu!.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToArray();

    private static IEnumerable<NativeMenuItem> Flatten(NativeMenu menu) =>
        menu.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).SelectMany(i => i.Menu is { } sub ? Flatten(sub).Prepend(i) : [i]);

    private static void AssertSeparatorsOnlyBetweenItems(NativeMenu menu)
    {
        var items = menu.Items.ToArray();
        if (items.Length == 0) return;
        Assert.IsNotType<NativeMenuItemSeparator>(items[0]);
        Assert.IsNotType<NativeMenuItemSeparator>(items[^1]);
        for (var i = 1; i < items.Length; i++)
            Assert.False(items[i] is NativeMenuItemSeparator && items[i - 1] is NativeMenuItemSeparator, $"two separators in a row in {menu.Parent?.Header}");
    }

    private sealed class Rig : IDisposable
    {
        public ManualUiDispatcher Ui { get; } = new();
        public SimpleContextKeys Keys { get; } = new();
        public TestCommandService Commands { get; } = new();
        public FakeKeybindingService Bindings { get; }
        public FakeWorkspaceService Workspace { get; } = new();
        public MenuModel Menu { get; }
        public ServiceProvider Services { get; }

        public Rig()
        {
            Bindings = new FakeKeybindingService(Keys);
            foreach (var id in new[] { KnownCommands.NewUntitledFile, KnownCommands.OpenFile, KnownCommands.Quit, ShellMenus.About, ShellMenus.OpenRecent })
                Commands.Register(id, _ => ValueTask.FromResult<object?>(null), new CommandMetadata(id));
            Menu = new MenuModel(ShellMenus.Build(), Commands, Keys, Ui, Bindings, Workspace);
            Services = new ServiceCollection()
                .AddSingleton<ICommandService>(Commands).AddSingleton<IKeybindingService>(Bindings).AddSingleton<IUiDispatcher>(Ui)
                .BuildServiceProvider();
        }

        public void Dispose()
        {
            Menu.Dispose();
            Services.Dispose();
        }
    }
}

/// <summary>Raises what the native menu exporter raises (its bridge is internal to Avalonia).</summary>
internal static class NativeMenuBridge
{
    public static void RaiseClick(this NativeMenuItem item) => Invoke(item, "RaiseClicked");
    public static void RaiseNeedsUpdate(this NativeMenu menu) => Invoke(menu, "RaiseNeedsUpdate");

    private static void Invoke(object target, string method) =>
        target.GetType().GetInterfaces().SelectMany(i => i.GetMethods()).Single(m => m.Name == method).Invoke(target, null);
}
