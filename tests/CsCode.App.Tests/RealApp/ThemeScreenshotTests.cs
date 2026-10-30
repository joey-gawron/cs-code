using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.Core.Platform;
using CsCode.Core.Theming;
using CsCode.Panels.Search;
using CsCode.Panels.Search.Views;
using CsCode.Ui.Theming;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// A3 evidence for the integrator: the REAL main window (every module, a folder and a C# file open, TextMate highlighting) photographed headless in
/// every installed theme (<c>.scratch/screenshots/theme-*.png</c>), plus the open dropdown and the Settings page. The picture is checked for content
/// and for the theme's colours where the chrome should show them.
/// </summary>
public class ThemeScreenshotTests
{
    private const string Sample = """
        using System;
        using System.Collections.Generic;

        namespace Demo;

        /// <summary>Greets people.</summary>
        public sealed class Greeter(string name)
        {
            private readonly List<string> _seen = new();

            public string Greet(int times)
            {
                // Say hello a few times.
                for (var i = 0; i < times; i++) _seen.Add($"Hello, {name} #{i}");
                return _seen.Count > 0 ? _seen[^1] : "nobody";
            }
        }
        """;

    private static async Task<RealAppHost> StartAsync()
    {
        var host = RealAppHost.Create(h =>
        {
            h.Files.WriteFile("src/Greeter.cs", Sample);
            h.Files.WriteFile("src/Program.cs", "Console.WriteLine(new Demo.Greeter(\"CsCode\").Greet(2));\n");
            h.Files.WriteFile("README.md", "# Demo\n");
        });
        await host.StartAsync([host.Files.Path, host.Files.Combine("src", "Greeter.cs")]);
        await RealAppHost.WaitFor(() => host.Shell.Well.Tabs.Count > 0);
        await DevScene.SettleAsync(30);
        return host;
    }

    private static string Capture(RealAppHost host, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var frame = host.Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered");
        var path = Path.Combine(DevHost.ScreenshotDirectory(), name + ".png");
        frame.Save(path, new PngBitmapEncoderOptions());
        return path;
    }

    private static async Task SwitchAsync(RealAppHost host, string themeId)
    {
        await ThemeSelection.CommitAsync(host.Get<IColorThemeService>(), host.Get<IConfigurationService>(), themeId);
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Theme!.AppliedThemeId == themeId), themeId + " was not applied");
        await DevScene.SettleAsync(15);
    }

    [AvaloniaFact(Timeout = 300_000)]
    public async Task The_main_window_in_every_installed_theme()
    {
        await using var host = await StartAsync();
        var themes = host.Get<IColorThemeService>();
        var written = new List<string>();
        foreach (var theme in themes.Available.OrderBy(t => t.Kind).ThenBy(t => t.Label, StringComparer.Ordinal).ToArray())
        {
            await SwitchAsync(host, theme.Id);
            var path = Capture(host, "theme-" + theme.Id.Replace('.', '-'));
            Assert.True(new FileInfo(path).Length > 20_000, theme.Label + ": the rendered frame looks empty");
            // The chrome shows the completed theme: the live toolbar brush is the theme's toolbar colour.
            var toolbar = (Avalonia.Media.ISolidColorBrush)host.Shell.Theme!.GetBrush("toolbar.background")!;
            var expected = theme.Colors["toolbar.background"];
            Assert.Equal(Avalonia.Media.Color.FromArgb(expected.A, expected.R, expected.G, expected.B), toolbar.Color);
            written.Add(path);
        }
        Assert.Equal(themes.Available.Count, written.Count);
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task The_open_dropdown_and_the_settings_page()
    {
        await using var host = await StartAsync();
        await host.RunAsync(SearchPanelsHost.OpenSettings);
        await DevScene.SettleAsync(10);
        var picker = host.Window.GetVisualDescendants().OfType<SettingsView>().Single().ThemePicker!;
        foreach (var id in new[] { VsThemes.DarkId, "vscode.solarized-light" })
        {
            await SwitchAsync(host, id);
            picker.IsDropDownOpen = true;
            await DevScene.SettleAsync(15);
            Capture(host, "theme-dropdown-" + id.Replace('.', '-'));
            picker.IsDropDownOpen = false;
            await DevScene.SettleAsync(5);
        }
        foreach (var id in new[] { VsThemes.DarkId, "vscode.monokai", VsThemes.LightId })
        {
            await SwitchAsync(host, id);
            Capture(host, "theme-settings-" + id.Replace('.', '-'));
        }
    }
}
