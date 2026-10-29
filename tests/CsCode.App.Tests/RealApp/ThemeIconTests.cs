using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CsCode.App.Commands;
using CsCode.App.Tests.Hosting;
using CsCode.App.Tests.Infrastructure;
using CsCode.App.ToolWindows.ErrorList;
using CsCode.App.Views;
using CsCode.Core.Languages;
using CsCode.Core.Text;
using CsCode.Core.Theming;
using CsCode.Editor.Theming;
using CsCode.Ui.Icons;
using CsCode.Ui.Theming;
using Xunit;

namespace CsCode.App.Tests.RealApp;

/// <summary>
/// A3, the integrator's Dark+ report: VS Code themes define no colours for the shell's coloured icons (the green Start triangle, the Error List's
/// severity icons, the debug step icons...), so before the completion those brushes were transparent and the icons vanished while monochrome ones
/// (drawn in <c>foreground</c>) stayed. The completion fills every published id from the nearest VS 2022 base; these tests prove the icons are
/// drawn, on the REAL composition, in every installed theme.
/// </summary>
public class ThemeIconTests
{
    /// <summary>The colour ids of the shell's coloured icons (toolbar, Error List, notifications).</summary>
    private static readonly string[] IconKeys =
    [
        "debugIcon.startForeground", "debugIcon.continueForeground", "debugIcon.pauseForeground", "debugIcon.stopForeground", "debugIcon.restartForeground",
        "debugIcon.stepOverForeground", "debugIcon.stepIntoForeground", "debugIcon.stepOutForeground", "debugIcon.breakpointForeground",
        "problemsErrorIcon.foreground", "problemsWarningIcon.foreground", "problemsInfoIcon.foreground",
        "notificationsErrorIcon.foreground", "notificationsWarningIcon.foreground", "notificationsInfoIcon.foreground", "icon.foreground",
    ];

    [Fact]
    public void Dark_plus_as_shipped_has_no_shell_icon_colours_and_the_completion_fills_them_opaque_in_every_theme()
    {
        var raw = BuiltinColorThemes.Load(BuiltinColorThemes.All.Single(d => d.Id == "vscode.dark-plus"));
        // The gap the integrator saw: none of these is in Dark+ (nor in Dark (Visual Studio), which it includes).
        Assert.All(new[] { "debugIcon.startForeground", "problemsErrorIcon.foreground", "problemsWarningIcon.foreground", "problemsInfoIcon.foreground" },
            k => Assert.False(raw.Colors.ContainsKey(k), k));
        foreach (var theme in BuiltinColorThemes.LoadAll().Concat(VsThemes.All))
        {
            var full = ThemeCompletion.Complete(theme);
            foreach (var key in IconKeys)
            {
                Assert.True(full.Colors.TryGetValue(key, out var c), $"{theme.Label} lacks {key}");
                Assert.True(c.A == 255, $"{theme.Label}: {key} is {c.ToHex()}, not opaque");
            }
        }
    }

    [Fact]
    public void Every_colour_id_the_sources_read_as_a_published_brush_is_published()
    {
        // ThemeBrushes.Get(id) and {DynamicResource vsc:id} of an id the applier does not publish are transparent: an icon or text drawn with them
        // vanishes in every theme. Every such id written literally in the sources must be in WorkbenchColors (the completion fills all of those).
        var root = Directory.GetParent(DevHost.ScreenshotDirectory())!.Parent!.FullName;
        var published = WorkbenchColors.All.ToHashSet(StringComparer.Ordinal);
        var pattern = new Regex(@"vsc:(?<id>[A-Za-z][A-Za-z0-9.]*[A-Za-z0-9])|ThemeBrushes\.Get\(""(?<id>[^""]+)""\)", RegexOptions.CultureInvariant);
        var missing = new List<string>();
        var seen = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal))
                                 && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            foreach (Match m in pattern.Matches(File.ReadAllText(file)))
            {
                seen++;
                var id = m.Groups["id"].Value;
                if (!published.Contains(id)) missing.Add($"{Path.GetRelativePath(root, file)}: {id}");
            }
        }
        Assert.True(seen > 100, $"only {seen} references found: the scan is broken");
        Assert.True(missing.Count == 0, "read as brushes but not published: " + string.Join(", ", missing));
    }

    private static async Task<RealAppHost> StartAsync()
    {
        var host = RealAppHost.Create(h => h.Files.WriteFile("src/Program.cs", "using System;\n\nclass Program\n{\n    static void Main() => Console.WriteLine(\"hi\");\n}\n"));
        await host.StartAsync([host.Files.Path, host.Files.Combine("src", "Program.cs")]);
        await RealAppHost.WaitFor(() => host.Shell.Well.Tabs.Count > 0);
        // One diagnostic of each severity, so the Error List's rows show their icons too.
        var program = new Uri(host.Files.Combine("src", "Program.cs"));
        host.Get<IDiagnosticService>().Set(program, "test", [
            new Diagnostic(TextRange.FromLines(4, 4, 4, 10), "an error", DiagnosticSeverity.Error, "E1", "test"),
            new Diagnostic(TextRange.FromLines(0, 0, 0, 5), "a warning", DiagnosticSeverity.Warning, "W1", "test"),
            new Diagnostic(TextRange.FromLines(2, 0, 2, 5), "a message", DiagnosticSeverity.Information, "I1", "test")]);
        await host.RunAsync(ShellCommands.ShowErrorList);
        await RealAppHost.WaitFor(() => ErrorList(host) is { IsEffectivelyVisible: true });
        await DevScene.SettleAsync(20);
        return host;
    }

    private static ErrorListView? ErrorList(RealAppHost host) => host.Window.GetVisualDescendants().OfType<ErrorListView>().FirstOrDefault();

    private static async Task SwitchAsync(RealAppHost host, string themeId)
    {
        await host.Get<IColorThemeService>().SetCurrentAsync(themeId);
        Assert.True(await RealAppHost.WaitFor(() => host.Shell.Theme!.AppliedThemeId == themeId), themeId + " was not applied");
        await DevScene.SettleAsync(10);
    }

    [AvaloniaFact(Timeout = 300_000)]
    public async Task Every_icon_in_the_window_has_a_visible_colour_in_every_installed_theme()
    {
        await using var host = await StartAsync();
        var themes = host.Get<IColorThemeService>();
        var failures = new List<string>();
        var checkedIcons = 0;
        foreach (var theme in themes.Available.ToArray())
        {
            await SwitchAsync(host, theme.Id);
            foreach (var icon in host.Window.GetVisualDescendants().OfType<Icon>().Where(i => IconCatalog.Contains(i.Kind)))
            {
                checkedIcons++;
                if (icon.Foreground is not ISolidColorBrush { Color.A: > 0 })
                    failures.Add($"{theme.Label}: '{icon.Kind}' in {icon.GetVisualParent()?.GetType().Name} has {(icon.Foreground as ISolidColorBrush)?.Color.ToString() ?? "no brush"}");
            }
        }
        Assert.True(checkedIcons > themes.Available.Count * 20, $"only {checkedIcons} icons checked");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Distinct().Take(40)));
    }

    [AvaloniaFact(Timeout = 120_000)]
    public async Task With_dark_plus_the_green_start_triangle_and_the_error_list_severity_icons_are_drawn()
    {
        await using var host = await StartAsync();
        await SwitchAsync(host, "vscode.dark-plus");
        var frame = Capture(host, "theme-icons-dark-plus");
        var scale = frame.PixelSize.Width / host.Window.ClientSize.Width;

        var toolbar = host.Window.GetVisualDescendants().OfType<ToolbarView>().Single();
        var start = toolbar.GetVisualDescendants().OfType<Icon>().Single(i => i.Kind == IconKey.Play);
        var list = ErrorList(host)!;
        var chips = list.GetVisualDescendants().OfType<Icon>().Where(i => i.Kind is "error" or "warning" or "info").ToList();
        var targets = new List<(Icon Icon, string ColorId)> { (start, "debugIcon.startForeground") };
        foreach (var (kind, id) in new[] { ("error", "problemsErrorIcon.foreground"), ("warning", "problemsWarningIcon.foreground"), ("info", "problemsInfoIcon.foreground") })
            targets.Add((chips.First(i => i.Kind == kind), id)); // the first is the filter chip (above the rows)

        using var pixels = Pixels.From(frame);
        foreach (var (icon, colorId) in targets)
        {
            var expected = ((ISolidColorBrush)host.Shell.Theme!.GetBrush(colorId)!).Color;
            Assert.True(expected.A == 255, $"{colorId} is {expected} in Dark+");
            var topLeft = icon.TranslatePoint(new Point(0, 0), host.Window)!.Value;
            var area = new PixelRect((int)(topLeft.X * scale), (int)(topLeft.Y * scale), (int)Math.Ceiling(icon.Size * scale), (int)Math.Ceiling(icon.Size * scale));
            // The icon's colour as drawn: its opacity (a disabled Start button is dimmed) composited over the surface beside it.
            var opacity = icon.GetSelfAndVisualAncestors().Aggregate(1.0, (o, v) => o * v.Opacity);
            var surface = pixels.At(area.X - 2, area.Y + area.Height / 2);
            var drawn = Blend(expected, surface, opacity);
            // Codicons are thin outlines at 13-14 px, so most of their pixels are anti-aliased: count the pixels at least half covered by the
            // icon's colour (on the line from the surface to that colour). A transparent brush, the bug, leaves every pixel at the surface colour.
            var hits = pixels.CountCovered(area, surface, drawn, minCoverage: 0.5);
            Assert.True(hits >= 8, $"Dark+: the '{icon.Kind}' icon ({colorId} {expected}, drawn {drawn} over {surface}) covers {hits} pixels of {area}");
        }
    }

    private static Bitmap Capture(RealAppHost host, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var frame = host.Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered");
        frame.Save(Path.Combine(DevHost.ScreenshotDirectory(), name + ".png"), new PngBitmapEncoderOptions());
        return frame;
    }

    private static Color Blend(Color top, Color under, double opacity)
    {
        static byte M(byte t, byte u, double a) => (byte)Math.Round(t * a + u * (1 - a));
        return Color.FromRgb(M(top.R, under.R, opacity), M(top.G, under.G, opacity), M(top.B, under.B, opacity));
    }

    /// <summary>A decoded frame's pixels (BGRA or RGBA, 4 bytes each).</summary>
    private sealed class Pixels : IDisposable
    {
        private readonly WriteableBitmap _bitmap;
        private readonly ILockedFramebuffer _fb;

        private Pixels(WriteableBitmap bitmap)
        {
            _bitmap = bitmap;
            _fb = bitmap.Lock();
        }

        public static Pixels From(Bitmap frame)
        {
            using var ms = new MemoryStream();
            frame.Save(ms, new PngBitmapEncoderOptions());
            ms.Position = 0;
            return new Pixels(WriteableBitmap.Decode(ms));
        }

        public Color At(int x, int y)
        {
            x = Math.Clamp(x, 0, _fb.Size.Width - 1);
            y = Math.Clamp(y, 0, _fb.Size.Height - 1);
            var px = new byte[4];
            Marshal.Copy(_fb.Address + y * _fb.RowBytes + x * 4, px, 0, 4);
            return _fb.Format == PixelFormat.Bgra8888 ? Color.FromRgb(px[2], px[1], px[0]) : Color.FromRgb(px[0], px[1], px[2]);
        }

        /// <summary>
        /// Pixels of <paramref name="area"/> that are <paramref name="ink"/> drawn over <paramref name="surface"/> with at least
        /// <paramref name="minCoverage"/> (anti-aliased edges included): the colour lies on the segment surface to ink, that far along it.
        /// </summary>
        public int CountCovered(PixelRect area, Color surface, Color ink, double minCoverage)
        {
            double vr = ink.R - surface.R, vg = ink.G - surface.G, vb = ink.B - surface.B;
            var vv = vr * vr + vg * vg + vb * vb;
            if (vv < 1) return 0; // ink and surface are the same colour: nothing can be told apart
            var n = 0;
            for (var y = area.Y; y < area.Bottom; y++)
                for (var x = area.X; x < area.Right; x++)
                {
                    var c = At(x, y);
                    double dr = c.R - surface.R, dg = c.G - surface.G, db = c.B - surface.B;
                    var a = (dr * vr + dg * vg + db * vb) / vv;
                    var off = Math.Sqrt(Math.Pow(dr - a * vr, 2) + Math.Pow(dg - a * vg, 2) + Math.Pow(db - a * vb, 2));
                    if (a >= minCoverage && a <= 1.1 && off <= 24) n++;
                }
            return n;
        }

        public void Dispose()
        {
            _fb.Dispose();
            _bitmap.Dispose();
        }
    }
}
