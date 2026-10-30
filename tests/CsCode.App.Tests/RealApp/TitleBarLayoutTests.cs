using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.Views;
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

    private static Rect BoundsIn(Visual root, Control control) =>
        new(control.TranslatePoint(default, root) ?? throw new InvalidOperationException($"{control} is not in the title bar"), control.Bounds.Size);
}
