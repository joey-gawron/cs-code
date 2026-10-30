using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Menus;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Views;
using CsCode.TestDoubles;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// The title bar in windows of every width the window allows (<see cref="MainWindow"/>'s MinWidth is 640): seen on the desktop, a half-screen
/// window at 170 % (about 850 logical pixels) drew the fixed-width Quick Launch box under minimise and maximise and the workspace name past close.
/// </summary>
public class TitleBarLayoutTests
{
    [AvaloniaFact(Timeout = 60_000)]
    public async Task Nothing_in_the_title_bar_draws_under_the_window_controls_at_any_width()
    {
        await using var host = RealAppHost.Create(h => h.Files.WriteFile("src/Program.cs", "class P {}\n"));
        await host.StartAsync([host.Files.Path]);
        var bar = host.Window.GetVisualDescendants().OfType<TitleBarView>().Single();
        Assert.True(await RealAppHost.WaitFor(() => bar.WorkspaceName is not null));
        var buttons = bar.CaptionButtons ?? throw new InvalidOperationException("custom chrome expected off macOS");

        foreach (var width in new[] { 1600, 1280, 1000, 850, 760, 700, 640 })
        {
            host.Window.Width = width;
            for (var i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); host.Window.UpdateLayout(); }

            var controls = BoundsIn(bar, buttons);
            Assert.True(Math.Abs(controls.Right - bar.Bounds.Width) < 0.5, $"{width}: the window controls end at {controls.Right}, not at the edge {bar.Bounds.Width}");
            Assert.Equal(3 * 46, controls.Width, 0.5);
            foreach (var (name, control) in new[] { ("Quick Launch box", bar.QuickLaunchBox), ("workspace name", bar.WorkspaceBadge) })
            {
                if (control.Bounds.Width == 0) continue; // hidden for lack of room
                var r = BoundsIn(bar, control);
                Assert.True(r.Right <= controls.Left + 0.5, $"{width}: the {name} ({r}) runs under the window controls ({controls})");
                Assert.True(r.Left >= 0, $"{width}: the {name} starts left of the title bar ({r})");
            }
            var quick = bar.QuickLaunchBox.Bounds.Width;
            Assert.True(quick == 0 || quick >= TitleCenterPanel.MinQuickLaunchWidth - 0.5, $"{width}: a {quick} px Quick Launch box is too narrow to read");
            if (width >= 1280)
            {
                Assert.Equal(TitleCenterPanel.QuickLaunchWidth, quick, 0.5);
                Assert.True(bar.WorkspaceBadge.Bounds.Width > 0, $"{width}: the workspace name is hidden in a wide window");
            }
        }
    }

    /// <summary>
    /// Left to right, as children and for screen readers alike: logo, menu, the middle, then minimise, maximise and close. Laying the window
    /// controls out first (so they always get their width) once also made them the title bar's first children, so Narrator and AT-SPI read them
    /// before the File menu (and forward Tab landed on Minimise: see the next test).
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_title_bar_runs_logo_menu_middle_then_window_controls_in_layout_children_and_automation_order()
    {
        await using var host = RealAppHost.Create(h => h.Files.WriteFile("src/Program.cs", "class P {}\n"));
        await host.StartAsync([host.Files.Path]);
        var bar = host.Window.GetVisualDescendants().OfType<TitleBarView>().Single();
        Assert.True(await RealAppHost.WaitFor(() => bar.WorkspaceName is not null));
        var buttons = bar.CaptionButtons ?? throw new InvalidOperationException("custom chrome expected off macOS");
        var logo = bar.GetVisualDescendants().OfType<AppLogo>().Single();
        var menu = bar.GetVisualDescendants().OfType<ShellMenuBar>().Single();
        var file = menu.GetVisualDescendants().OfType<MenuItem>().First();
        var minimise = buttons.GetVisualDescendants().OfType<Button>().First();
        Control[] expected = [logo, menu, bar.QuickLaunchBox, buttons];

        var left = expected.Select(c => BoundsIn(bar, c).Left).ToArray();
        Assert.True(left.SequenceEqual(left.Order()), "left edges, logo to window controls: " + string.Join(", ", left));
        Assert.Equal(expected, Ordered(bar.GetVisualDescendants(), expected));
        Assert.Equal(expected, Ordered(bar.GetLogicalDescendants(), expected));

        var read = Read(ControlAutomationPeer.CreatePeerForElement(bar)).ToList();
        Assert.True(read.IndexOf(file) >= 0 && read.IndexOf(minimise) >= 0, "File or Minimise has no automation peer");
        Assert.True(read.IndexOf(file) < read.IndexOf(minimise), "a screen reader reaches Minimise before the File menu");
        Assert.True(read.IndexOf(bar.QuickLaunchBox) < read.IndexOf(minimise), "a screen reader reaches Minimise before the Quick Launch box");

        static Control[] Ordered(IEnumerable<object> tree, Control[] wanted) => tree.OfType<Control>().Where(wanted.Contains).ToArray();
        static IEnumerable<Control> Read(AutomationPeer peer)
        {
            if (peer is ControlAutomationPeer control) yield return control.Owner;
            foreach (var child in peer.GetChildren()) foreach (var owner in Read(child)) yield return owner;
        }
    }

    /// <summary>
    /// Minimise, maximise and close never take the keyboard, as native caption buttons do not: forward Tab into the title bar used to land on
    /// Minimise, Tab or Shift+Tab around the whole window must never stop on any of them, and a click on one used to leave the keyboard on it
    /// instead of in the editor.
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_window_controls_are_not_Tab_stops_and_a_click_leaves_the_keyboard_in_the_editor()
    {
        await using var host = RealAppHost.Create(h => h.Files.WriteFile("src/Program.cs", "class P {}\n"));
        await host.StartAsync();
        var doc = await host.Shell.Well.OpenAsync(new Uri(host.Files.Combine("src/Program.cs")));
        var bar = host.Window.GetVisualDescendants().OfType<TitleBarView>().Single();
        var buttons = (bar.CaptionButtons ?? throw new InvalidOperationException("custom chrome expected off macOS")).GetVisualDescendants().OfType<Button>().ToArray();
        Assert.Equal(3, buttons.Length);
        Assert.All(buttons, b => Assert.False(b.Focusable && b.IsTabStop, "a window control is a Tab stop"));

        var focus = host.Window.FocusManager ?? throw new InvalidOperationException("no focus manager");
        foreach (var direction in new[] { NavigationDirection.Next, NavigationDirection.Previous })
        {
            // The window's whole Tab cycle, from the window itself (forward: into the title bar first) until it comes round again.
            var visited = new List<IInputElement>();
            for (var at = focus.FindNextElement(direction, new FindNextElementOptions { FocusedElement = host.Window }); at is not null && !visited.Contains(at);
                 at = focus.FindNextElement(direction, new FindNextElementOptions { FocusedElement = at }))
                visited.Add(at);
            Assert.True(visited.Count > 3, $"{direction}: only {visited.Count} Tab stops in the window");
            var caption = visited.OfType<Button>().FirstOrDefault(buttons.Contains);
            Assert.True(caption is null, $"{direction}: Tab stop {visited.IndexOf(caption!)} of {visited.Count} is a window control");
        }

        Assert.True(await RealAppHost.WaitFor(() => doc.Controller is not null && doc.LiveView!.IsAttachedToVisualTree()));
        doc.Controller!.Focus();
        Assert.True(await RealAppHost.WaitFor(() => doc.Controller.IsFocused));
        var maximise = buttons[1].TranslatePoint(new Point(buttons[1].Bounds.Width / 2, buttons[1].Bounds.Height / 2), host.Window)!.Value;
        host.Window.MouseDown(maximise, MouseButton.Left);
        host.Window.MouseUp(maximise, MouseButton.Left);
        Assert.True(await RealAppHost.WaitFor(() => host.Window.WindowState == WindowState.Maximized), $"the click did not maximise: {host.Window.WindowState}");
        Assert.True(doc.Controller.IsFocused, $"the click moved the keyboard to {host.Window.FocusManager?.GetFocusedElement()}");
    }

    /// <summary>The window controls are icons only: a screen reader reads, and the tooltip shows, the names Windows gives its own.</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task The_window_controls_are_named_for_screen_readers_and_maximise_follows_the_window_state()
    {
        await using var host = RealAppHost.Create(h => h.Files.WriteFile("a.txt", "x\n"));
        await host.StartAsync();
        var bar = host.Window.GetVisualDescendants().OfType<TitleBarView>().Single();
        var buttons = (bar.CaptionButtons ?? throw new InvalidOperationException("custom chrome expected off macOS")).GetVisualDescendants().OfType<Button>().ToArray();
        string[] Names() => buttons.Select(b => ControlAutomationPeer.CreatePeerForElement(b).GetName() ?? "").ToArray();

        Assert.Equal(new[] { "Minimize", "Maximize", "Close" }, Names());
        Assert.Equal("Maximize", ToolTip.GetTip(buttons[1]));
        host.Window.WindowState = WindowState.Maximized;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "Minimize", "Restore Down", "Close" }, Names());
        Assert.Equal("Restore Down", ToolTip.GetTip(buttons[1]));
        host.Window.WindowState = WindowState.Normal;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Maximize", Names()[1]);
    }

    /// <summary>
    /// macOS keeps its own window controls (<c>showCaptionButtons = false</c>, the client area extended under the native title bar): the traffic
    /// lights end 70 to 78 px from the left and used to cover the logo and the File menu. Full screen hides them, and the title bar starts at the
    /// edge again. (Built directly: off macOS the real app always draws its own controls.)
    /// </summary>
    [AvaloniaFact(Timeout = 60_000)]
    public void With_native_window_controls_the_logo_and_menu_stay_clear_of_the_traffic_lights_except_in_full_screen()
    {
        const double trafficLights = 78; // their right end under the taller unified title bar
        var ui = new ManualUiDispatcher();
        var keys = new SimpleContextKeys();
        var commands = new TestCommandService();
        using var menus = new MenuModel(ShellMenus.Build(), commands, keys, ui);
        var window = new Window { Width = 1000, Height = 200 };
        var bar = new TitleBarView(window, menus, commands, showCaptionButtons: false) { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        window.Content = bar;
        window.Show();
        try
        {
            Assert.Null(bar.CaptionButtons);
            var logo = bar.GetVisualDescendants().OfType<AppLogo>().Single().GetVisualParent<Control>()!; // the 30 px cell around the artwork
            var menu = bar.GetVisualDescendants().OfType<ShellMenuBar>().Single();
            var file = menu.GetVisualDescendants().OfType<MenuItem>().First();

            Settle();
            Assert.True(BoundsIn(bar, logo).Left >= trafficLights - 0.5, $"the logo ({BoundsIn(bar, logo)}) is under the traffic lights");
            Assert.True(BoundsIn(bar, file).Left >= BoundsIn(bar, logo).Right - 0.5, $"the File menu ({BoundsIn(bar, file)}) is not right of the logo");
            Assert.True(BoundsIn(bar, bar.QuickLaunchBox).Left >= BoundsIn(bar, menu).Right - 0.5, "the Quick Launch box overlaps the menu");

            window.WindowState = WindowState.FullScreen;
            Settle();
            Assert.Equal(0, BoundsIn(bar, logo).Left, 0.5);

            window.WindowState = WindowState.Normal;
            Settle();
            Assert.True(BoundsIn(bar, logo).Left >= trafficLights - 0.5, $"out of full screen, the logo ({BoundsIn(bar, logo)}) is under the traffic lights");
        }
        finally { window.Close(); }

        void Settle()
        {
            for (var i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        }
    }

    private static Rect BoundsIn(Visual root, Control control) =>
        new(control.TranslatePoint(default, root) ?? throw new InvalidOperationException($"{control} is not in the title bar"), control.Bounds.Size);
}
