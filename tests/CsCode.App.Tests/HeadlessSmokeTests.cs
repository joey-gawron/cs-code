using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using CsCode.App.Tests.Hosting;
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
    public async Task MainWindow_shows_and_renders_a_frame_on_the_dev_host()
    {
        using var host = new DevHost().Build();
        await host.ShowAsync();
        var path = host.Capture("smoke");
        Assert.True(File.Exists(path));
    }
}
