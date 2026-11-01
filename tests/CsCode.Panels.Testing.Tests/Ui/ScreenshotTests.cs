using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using CsCode.Core.Lifecycle;
using CsCode.Core.Theming;
using CsCode.Ui.Theming;
using CsCode.Ui.Threading;
using Xunit;

namespace CsCode.Panels.Testing.Tests.Ui;

/// <summary>Colour themes over the built-in VS2022 ones (no Editor module).</summary>
internal sealed class StaticThemeService : IColorThemeService
{
    private readonly Emitter<ColorTheme> _changed = new();
    public ColorTheme Current { get; private set; } = VsThemes.Dark;
    public IReadOnlyList<ColorTheme> Available => VsThemes.All;
    public IEventSource<ColorTheme> Changed => _changed;
    public IDisposable Register(ColorTheme theme) => Disposable.Empty;
    public ValueTask SetCurrentAsync(string themeId, CancellationToken ct = default)
    {
        Current = VsThemes.All.First(t => t.Id == themeId);
        _changed.Fire(Current);
        return ValueTask.CompletedTask;
    }
    public Rgba GetColor(string key) => Current.TryGetColor(key, out var c) ? c : new Rgba(128, 128, 128);
}

public sealed class ScreenshotTests
{
    /// <summary>
    /// Renders the explorer after a run in Visual Studio Dark, with a failed test selected, and saves it to .scratch/screenshots/test-explorer.png
    /// (for a person to look at; .scratch is not committed). Checks the failed row's red state icon and the passed row's green one are drawn.
    /// </summary>
    [AvaloniaFact]
    public async Task Explorer_after_a_run_renders_states_and_details()
    {
        using var applier = new ThemeApplier(Application.Current!, new StaticThemeService(), AvaloniaUiDispatcher.Instance);
        applier.ApplyNow(VsThemes.Dark);
        using var rig = new HostRig(AvaloniaUiDispatcher.Instance);
        var view = (TestExplorerView)new TestExplorerToolWindow(rig.Host).CreateView(rig.Services);
        var window = new Window { Width = 460, Height = 560, Content = view };
        window.Show();
        for (var i = 0; i < 200 && rig.Host.Controller.Tree.TestCount == 0; i++) { TestExplorerViewTests.Pump(); await Task.Delay(5, TestContext.Current.CancellationToken); }
        await rig.Host.Controller.RunAllAsync();
        TestExplorerViewTests.Pump();
        view.Model.ExpandAll();
        view.Model.Selected = view.Model.Rows.Single(r => r.Label == "Subtracts");
        TestExplorerViewTests.Pump();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CsCode.slnx"))) dir = Path.GetDirectoryName(dir);
        var target = Path.Combine(dir ?? Path.GetTempPath(), ".scratch", "screenshots");
        Directory.CreateDirectory(target);
#pragma warning disable CS0618 // the PNG default is all a screenshot needs
        frame!.Save(Path.Combine(target, "test-explorer.png"));
#pragma warning restore CS0618
        Assert.True(HasColor(frame, 0xF1, 0x4C, 0x4C), "the failed test's red icon");
        Assert.True(HasColor(frame, 0x73, 0xC9, 0x91), "the passed test's green icon");
        window.Close();
    }

    private static bool HasColor(Bitmap bitmap, byte r, byte g, byte b)
    {
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var bytes = new byte[stride * size.Height];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), bytes.Length, stride); }
        finally { handle.Free(); }
        static bool Near(int x, int y) => Math.Abs(x - y) < 14;
        for (var i = 0; i + 3 < bytes.Length; i += 4)
            if ((Near(bytes[i + 2], r) && Near(bytes[i + 1], g) && Near(bytes[i], b)) || (Near(bytes[i], r) && Near(bytes[i + 1], g) && Near(bytes[i + 2], b))) return true;
        return false;
    }

}
