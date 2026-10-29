using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(CsCode.App.Tests.TestAppBuilder))]

namespace CsCode.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<CsCode.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class HeadlessSmokeTests
{
    [AvaloniaFact]
    public void MainWindow_shows_and_renders_a_frame()
    {
        var window = new CsCode.App.MainWindow();
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 0 && frame.PixelSize.Height > 0);
    }
}
